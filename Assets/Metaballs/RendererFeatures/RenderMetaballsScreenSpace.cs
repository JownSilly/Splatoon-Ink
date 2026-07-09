using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class RenderMetaballsScreenSpace : ScriptableRendererFeature
{
    class RenderMetaballsCombinedPass : ScriptableRenderPass
    {
        public Material WriteDepthMaterial;
        public Material BlitMaterial;
        private Material _blurMaterial;
        private Material _blitCopyDepthMaterial;

        public int BlurPasses;
        public float BlurDistance;

        RenderQueueType _renderQueueType;
        FilteringSettings _filteringSettings;
        List<ShaderTagId> ShaderTagIdList = new List<ShaderTagId>();

        public RenderMetaballsCombinedPass(string profilerTag, RenderPassEvent renderPassEvent,
            string[] shaderTags, RenderQueueType renderQueueType, int layerMask)
        {
            this.renderPassEvent = renderPassEvent;
            this._renderQueueType = renderQueueType;
            
            RenderQueueRange renderQueueRange = (renderQueueType == RenderQueueType.Transparent)
                ? RenderQueueRange.transparent : RenderQueueRange.opaque;
            _filteringSettings = new FilteringSettings(renderQueueRange, layerMask);

            if (shaderTags != null && shaderTags.Length > 0)
            {
                foreach (var passName in shaderTags)
                    ShaderTagIdList.Add(new ShaderTagId(passName));
            }
            else
            {
                ShaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                ShaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                ShaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
                ShaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            }

            _blitCopyDepthMaterial = new Material(Shader.Find("Hidden/BlitToDepth"));
            _blurMaterial = new Material(Shader.Find("Hidden/KawaseBlur"));
        }

        private class PassData 
        {
            public TextureHandle depthRT;
            public TextureHandle metaballRT;
            public TextureHandle metaballRT2;
            public TextureHandle cameraColorTarget;
            public TextureHandle cameraDepthTarget;
            
            public RendererListHandle depthRendererList;
            public RendererListHandle colorRendererList;

            public Material writeDepthMat;
            public Material blitMat;
            public Material blurMat;
            public Material copyDepthMat;

            public int blurPasses;
            public float blurDistance;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var lightData = frameData.Get<UniversalLightData>();

            if (BlitMaterial == null || WriteDepthMaterial == null) return;

            RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
            desc.colorFormat = RenderTextureFormat.ARGB32;
            desc.depthBufferBits = 0;

            // Criar as 3 Texturas
            TextureHandle depthRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MetaballDepthRT", false);
            TextureHandle metaballRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MetaballRT", false);
            TextureHandle metaballRT2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "_MetaballRT2", false);

            SortingCriteria sortingCriteria = (_renderQueueType == RenderQueueType.Transparent) ? SortingCriteria.CommonTransparent : cameraData.defaultOpaqueSortFlags;

            // Lista 1: Objetos desenhados com o Material de Profundidade
            DrawingSettings depthSettings = CreateDrawingSettings(ShaderTagIdList,renderingData, cameraData, lightData, sortingCriteria);
            depthSettings.overrideMaterial = WriteDepthMaterial;
            var depthParam = new RendererListParams(renderingData.cullResults, depthSettings, _filteringSettings);
            RendererListHandle depthList = renderGraph.CreateRendererList(depthParam);

            // Lista 2: Objetos desenhados normalmente
            DrawingSettings colorSettings = CreateDrawingSettings(ShaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);
            var colorParam = new RendererListParams(renderingData.cullResults, colorSettings, _filteringSettings);
            RendererListHandle colorList = renderGraph.CreateRendererList(colorParam);

            using (var builder = renderGraph.AddUnsafePass<PassData>("Metaballs Screen Space Pass", out var passData))
            {
                passData.depthRT = depthRT;
                passData.metaballRT = metaballRT;
                passData.metaballRT2 = metaballRT2;
                passData.cameraColorTarget = resourceData.activeColorTexture;
                passData.cameraDepthTarget = resourceData.activeDepthTexture;
                passData.depthRendererList = depthList;
                passData.colorRendererList = colorList;
                passData.writeDepthMat = WriteDepthMaterial;
                passData.blitMat = BlitMaterial;
                passData.blurMat = _blurMaterial;
                passData.copyDepthMat = _blitCopyDepthMaterial;
                passData.blurPasses = BlurPasses;
                passData.blurDistance = BlurDistance;

                builder.UseTexture(passData.depthRT, AccessFlags.ReadWrite);
                builder.UseTexture(passData.metaballRT, AccessFlags.ReadWrite);
                builder.UseTexture(passData.metaballRT2, AccessFlags.ReadWrite);
                builder.UseTexture(passData.cameraColorTarget, AccessFlags.ReadWrite);
                if (passData.cameraDepthTarget.IsValid()) builder.UseTexture(passData.cameraDepthTarget, AccessFlags.Read);
                
                builder.UseRendererList(passData.depthRendererList);
                builder.UseRendererList(passData.colorRendererList);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                    // 1. Renderizar Textura de Profundidade Própria
                    CoreUtils.SetRenderTarget(cmd, data.depthRT, ClearFlag.Color, Color.clear);
                    cmd.DrawRendererList(data.depthRendererList);

                    // 2. Limpar a Textura Principal
                    CoreUtils.SetRenderTarget(cmd, data.metaballRT, ClearFlag.Color, Color.clear);

                    // 3. Copiar Profundidade da Câmera
                    if (data.cameraDepthTarget.IsValid())
                        Blitter.BlitCameraTexture(cmd, data.cameraDepthTarget, data.metaballRT, data.copyDepthMat, 0);

                    // 4. Renderizar a Tinta
                    CoreUtils.SetRenderTarget(cmd, data.metaballRT);
                    cmd.DrawRendererList(data.colorRendererList);

                    // 5. Preparar variáveis de Blur
                    cmd.SetGlobalTexture("_BlurDepthTex", data.depthRT);
                    cmd.SetGlobalFloat("_BlurDistance", data.blurDistance);
                    
                    float offset = 1.5f;
                    cmd.SetGlobalFloat("_Offset", offset);
                    
                    TextureHandle currentSource = data.metaballRT;
                    TextureHandle currentDest = data.metaballRT2;

                    // Primeiro passe de Blur
                    Blitter.BlitCameraTexture(cmd, currentSource, currentDest, data.blurMat, 0);

                    // Loop de passes adicionais
                    for (int i = 1; i < data.blurPasses; ++i)
                    {
                        offset += 1.0f;
                        cmd.SetGlobalFloat("_Offset", offset);
                        
                        // Inverter as texturas
                        TextureHandle tmp = currentSource;
                        currentSource = currentDest;
                        currentDest = tmp;

                        Blitter.BlitCameraTexture(cmd, currentSource, currentDest, data.blurMat, 0);
                    }

                    // 6. Devolver para a tela
                    Blitter.BlitCameraTexture(cmd, currentDest, data.cameraColorTarget, data.blitMat, 0);
                });
            }
        }
    }

    public string PassTag = "RenderMetaballsScreenSpace";
    public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;
    public RenderObjects.FilterSettings FilterSettings = new RenderObjects.FilterSettings();
    public Material BlitMaterial;
    public Material WriteDepthMaterial;

    RenderMetaballsCombinedPass _scriptableMetaballsPass;

    [Range(1, 15)] public int BlurPasses = 1;
    [Range(0f, 1f)] public float BlurDistance = 0.5f;

    public override void Create()
    {
        _scriptableMetaballsPass = new RenderMetaballsCombinedPass(PassTag, Event,
            FilterSettings.PassNames, FilterSettings.RenderQueueType, FilterSettings.LayerMask)
        {
            BlitMaterial = BlitMaterial,
            WriteDepthMaterial = WriteDepthMaterial,
            BlurPasses = BlurPasses,
            BlurDistance = BlurDistance
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_scriptableMetaballsPass);
    }
}