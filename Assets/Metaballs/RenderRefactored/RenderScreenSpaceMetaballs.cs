using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

// Unity 6 / URP 17+ (RenderGraph) port of bzgeb's UnityScreenSpaceMetaballs feature.
// Reference: https://github.com/bzgeb/UnityScreenSpaceMetaballs
// The old Configure/Execute/OnCameraCleanup (Compatibility Mode) API is gone -
// everything now goes through RecordRenderGraph. See:
// https://docs.unity3d.com/6000.0/Documentation/Manual/urp/upgrade-guide-unity-6.html
public class RenderScreenSpaceMetaballs : ScriptableRendererFeature
{
    #region Render Objects

    class RenderObjectsPass : ScriptableRenderPass
    {
        readonly string _profilerTag;
        readonly ProfilingSampler _profilingSampler;
        readonly List<ShaderTagId> _shaderTagIds = new List<ShaderTagId>();
        readonly FilteringSettings _filteringSettings;

        // Result of this pass, consumed by KawaseBlurRenderPass in the same frame.
        public TextureHandle MetaballTexture { get; private set; }

        class PassData
        {
            public RendererListHandle RendererListHandle;
        }

        public RenderObjectsPass(string profilerTag, LayerMask layerMask)
        {
            _profilerTag = profilerTag;
            _profilingSampler = new ProfilingSampler(profilerTag);

            _filteringSettings = new FilteringSettings(RenderQueueRange.all, layerMask);

            _shaderTagIds.Add(new ShaderTagId("SRPDefaultUnlit"));
            _shaderTagIds.Add(new ShaderTagId("UniversalForward"));
            _shaderTagIds.Add(new ShaderTagId("UniversalForwardOnly"));
            _shaderTagIds.Add(new ShaderTagId("LightweightForward"));
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            // Own render target, same size as the camera target, ARGB32, cleared to transparent -
            // equivalent to the old GetTemporaryRT(..., ARGB32) + ConfigureClear(Color.clear).
            TextureDesc desc = resourceData.activeColorTexture.GetDescriptor(renderGraph);
            desc.name = "_RenderMetaballsRT";
            desc.colorFormat = GraphicsFormat.R8G8B8A8_UNorm;
            desc.depthBufferBits = 0;
            desc.clearBuffer = true;
            desc.clearColor = Color.clear;
            desc.msaaSamples = MSAASamples.None;

            MetaballTexture = renderGraph.CreateTexture(desc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(_profilerTag, out var passData, _profilingSampler))
            {
                // Color target is our own RT, depth target is the camera's depth -
                // matches ConfigureTarget(_renderTargetIdentifier, cameraDepthTarget) in the old pass.
                builder.SetRenderAttachment(MetaballTexture, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);

                DrawingSettings drawingSettings = RenderingUtils.CreateDrawingSettings(
                    _shaderTagIds, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);

                var rendererListParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.RendererListHandle = renderGraph.CreateRendererList(rendererListParams);
                builder.UseRendererList(passData.RendererListHandle);

                // Keep this pass alive even though, on its own, nothing appears to "consume" the
                // attachment until the blur pass runs later in the frame.
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.DrawRendererList(data.RendererListHandle);
                });
            }
        }
    }

    #endregion

    #region Kawase Blur

    class KawaseBlurRenderPass : ScriptableRenderPass
    {
        public Material BlurMaterial;
        public Material BlitMaterial;
        public int Passes;
        public int Downsample;

        static readonly int OffsetId = Shader.PropertyToID("_offset");

        readonly ProfilingSampler _profilingSampler;
        readonly RenderObjectsPass _metaballsPass;

        public KawaseBlurRenderPass(string profilerTag, RenderObjectsPass metaballsPass)
        {
            _profilingSampler = new ProfilingSampler(profilerTag);
            _metaballsPass = metaballsPass;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            // Compositing onto the back buffer directly isn't supported as a blit source/destination.
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = _metaballsPass.MetaballTexture;
            if (!source.IsValid() || BlurMaterial == null || BlitMaterial == null)
                return;

            int downsample = Mathf.Max(1, Downsample);
            int passes = Mathf.Max(1, Passes);

            TextureDesc blurDesc = source.GetDescriptor(renderGraph);
            blurDesc.width = Mathf.Max(1, blurDesc.width / downsample);
            blurDesc.height = Mathf.Max(1, blurDesc.height / downsample);
            blurDesc.filterMode = FilterMode.Bilinear;
            blurDesc.depthBufferBits = 0;
            blurDesc.clearBuffer = false;
            blurDesc.msaaSamples = MSAASamples.None;

            blurDesc.name = "_KawaseBlurTmp1";
            TextureHandle rt1 = renderGraph.CreateTexture(blurDesc);
            blurDesc.name = "_KawaseBlurTmp2";
            TextureHandle rt2 = renderGraph.CreateTexture(blurDesc);

            using (null)
            {
                // First pass: source -> rt1, offset 1.5.
                var firstMpb = new MaterialPropertyBlock();
                firstMpb.SetFloat(OffsetId, 1.5f);
                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(source, rt1, BlurMaterial, 0, firstMpb),
                    "KawaseBlur_Pass0");

                TextureHandle current = rt1;
                TextureHandle other = rt2;

                // Ping-pong intermediate passes, offset growing by 1 each time.
                for (var i = 1; i < passes - 1; i++)
                {
                    var stepMpb = new MaterialPropertyBlock();
                    stepMpb.SetFloat(OffsetId, 0.5f + i);
                    renderGraph.AddBlitPass(
                        new RenderGraphUtils.BlitMaterialParameters(current, other, BlurMaterial, 0, stepMpb),
                        $"KawaseBlur_Pass{i}");

                    (current, other) = (other, current);
                }

                // Final pass: composite onto the camera color target using BlitMaterial.
                var finalMpb = new MaterialPropertyBlock();
                finalMpb.SetFloat(OffsetId, 0.5f + passes - 1f);
                renderGraph.AddBlitPass(
                    new RenderGraphUtils.BlitMaterialParameters(current, resourceData.cameraColor, BlitMaterial, 0, finalMpb),
                    "KawaseBlur_Composite");
            }
        }
    }

    #endregion

    #region Renderer Feature

    RenderObjectsPass _renderObjectsPass;
    KawaseBlurRenderPass _blurPass;

    const string PassTag = "RenderScreenSpaceMetaballs";
    [SerializeField] LayerMask _layerMask;
    [SerializeField] Material _blurMaterial;
    [SerializeField] Material _blitMaterial;
    [SerializeField, Range(1, 16)] int _blurPasses = 1;
    [SerializeField, Range(1, 8)] int _downsample = 1;

    public override void Create()
    {
        _renderObjectsPass = new RenderObjectsPass(PassTag, _layerMask)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques
        };

        _blurPass = new KawaseBlurRenderPass("KawaseBlur", _renderObjectsPass)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
            Downsample = _downsample,
            Passes = _blurPasses,
            BlitMaterial = _blitMaterial,
            BlurMaterial = _blurMaterial
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_blurMaterial == null || _blitMaterial == null)
            return;

        renderer.EnqueuePass(_renderObjectsPass);
        renderer.EnqueuePass(_blurPass);
    }

    #endregion
}
