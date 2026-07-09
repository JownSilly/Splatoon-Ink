using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule; // Essencial no Unity 6

public class RenderMetaballs : ScriptableRendererFeature
{
    class RenderMetaballsPass : ScriptableRenderPass
    {
        int _downsamplingAmount = 4;

        public Material BlitMaterial;
        public Material BlurMaterial;
        public Material BlitCopyDepthMaterial;

        RenderQueueType renderQueueType;
        FilteringSettings m_FilteringSettings;
        RenderObjects.CustomCameraSettings m_CameraSettings;

        public Material overrideMaterial { get; set; }
        public int overrideMaterialPassIndex { get; set; }

        List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();

        public RenderMetaballsPass(string profilerTag, RenderPassEvent renderPassEvent, string[] shaderTags,
            RenderQueueType renderQueueType, int layerMask, RenderObjects.CustomCameraSettings cameraSettings, int downsamplingAmount)
        {
            this.renderPassEvent = renderPassEvent;
            this.renderQueueType = renderQueueType;
            this.overrideMaterial = null;
            this.overrideMaterialPassIndex = 0;

            RenderQueueRange renderQueueRange = (renderQueueType == RenderQueueType.Transparent)
                ? RenderQueueRange.transparent
                : RenderQueueRange.opaque;
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);

            if (shaderTags != null && shaderTags.Length > 0)
            {
                foreach (var passName in shaderTags)
                    m_ShaderTagIdList.Add(new ShaderTagId(passName));
            }
            else
            {
                m_ShaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
                m_ShaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            }

            m_CameraSettings = cameraSettings;

            BlitCopyDepthMaterial = new Material(Shader.Find("Hidden/BlitToDepth"));
            BlurMaterial = new Material(Shader.Find("Hidden/KawaseBlur"));
            _downsamplingAmount = downsamplingAmount;
        }

        // Estrutura de dados que o Render Graph usará para passar variáveis para o CommandBuffer
        private class PassData
        {
            public TextureHandle smallRT;
            public TextureHandle largeRT;
            public TextureHandle large2RT;
            public TextureHandle cameraColorTarget;
            public TextureHandle cameraDepthTarget;
            public RendererListHandle rendererListHandle;

            public Material blitMat;
            public Material blurMat;
            public Material copyDepthMat;

            public bool overrideCamera;
            public bool restoreCamera;
            public Matrix4x4 viewMatrix;
            public Matrix4x4 projMatrix;
        }

        // O novo coração da renderização no Unity 6
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var lightData = frameData.Get<UniversalLightData>();

            if (BlitMaterial == null || BlurMaterial == null || BlitCopyDepthMaterial == null) return;

            // 1. Configurar Descritores (Tamanho e Formato das texturas)
            RenderTextureDescriptor smallDesc = cameraData.cameraTargetDescriptor;
            smallDesc.width /= _downsamplingAmount;
            smallDesc.height /= _downsamplingAmount;
            smallDesc.colorFormat = RenderTextureFormat.ARGB32;
            smallDesc.depthBufferBits = 0;

            RenderTextureDescriptor largeDesc = cameraData.cameraTargetDescriptor;
            largeDesc.colorFormat = RenderTextureFormat.ARGB32;
            largeDesc.depthBufferBits = 0;

            // 2. Alocar as texturas temporárias via Render Graph
            TextureHandle smallRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, smallDesc, "_MetaballRTSmall", false);
            TextureHandle largeRT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, largeDesc, "_MetaballRTLarge", false);
            TextureHandle large2RT = UniversalRenderer.CreateRenderGraphTexture(renderGraph, largeDesc, "_MetaballRTLarge2", false);

            // 3. Preparar a lista de objetos que serão desenhados (Renderers)
            SortingCriteria sortingCriteria = (renderQueueType == RenderQueueType.Transparent) ? SortingCriteria.CommonTransparent : cameraData.defaultOpaqueSortFlags;
            // Passamos o primeiro item da lista na criação
    DrawingSettings drawingSettings = CreateDrawingSettings(m_ShaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);


            drawingSettings.overrideMaterial = overrideMaterial;
            drawingSettings.overrideMaterialPassIndex = overrideMaterialPassIndex;

            var param = new RendererListParams(renderingData.cullResults, drawingSettings, m_FilteringSettings);
            RendererListHandle rendererList = renderGraph.CreateRendererList(param);

            // 4. Criar o UnsafePass (Ponte entre o Unity antigo e o novo)
            using (var builder = renderGraph.AddUnsafePass<PassData>("Render Metaballs Pass", out var passData))
            {
                // 1. Pegue a matriz de projeção padrão
                Matrix4x4 projMatrix = cameraData.GetProjectionMatrix();

                // 2. Verifique se a API gráfica atual inverte a textura no eixo Y
                bool isFlipped = SystemInfo.graphicsUVStartsAtTop;
                // Preencher os dados
                passData.smallRT = smallRT;
                passData.largeRT = largeRT;
                passData.large2RT = large2RT;
                passData.cameraColorTarget = resourceData.activeColorTexture;
                passData.cameraDepthTarget = resourceData.activeDepthTexture;
                passData.rendererListHandle = rendererList;
                passData.blitMat = BlitMaterial;
                passData.blurMat = BlurMaterial;
                passData.copyDepthMat = BlitCopyDepthMaterial;
                passData.overrideCamera = m_CameraSettings.overrideCamera;
                passData.restoreCamera = m_CameraSettings.restoreCamera;
                passData.viewMatrix = cameraData.GetViewMatrix();
                passData.projMatrix = GL.GetGPUProjectionMatrix(projMatrix, isFlipped);

                // Avisar ao Render Graph o que será lido e escrito nesta passagem
                builder.UseTexture(passData.smallRT, AccessFlags.ReadWrite);
                builder.UseTexture(passData.largeRT, AccessFlags.ReadWrite);
                builder.UseTexture(passData.large2RT, AccessFlags.ReadWrite);
                builder.UseTexture(passData.cameraColorTarget, AccessFlags.ReadWrite);
                if (passData.cameraDepthTarget.IsValid()) builder.UseTexture(passData.cameraDepthTarget, AccessFlags.Read);
                builder.UseRendererList(passData.rendererListHandle);

                builder.AllowPassCulling(false); // Impede o Unity de ignorar esse efeito

                // Executar a lógica visual
                builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
                {
                    CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                    // Limpar RT Pequeno
                    CoreUtils.SetRenderTarget(cmd, data.smallRT, ClearFlag.Color, new Color(0, 0, 0, 0));

                    // Copiar Profundidade da Câmera
                    if (data.cameraDepthTarget.IsValid())
                        Blitter.BlitCameraTexture(cmd, data.cameraDepthTarget, data.smallRT, data.copyDepthMat, 0);

                    // Desenhar objetos da Layer (Tintas/Metaballs)
                    CoreUtils.SetRenderTarget(cmd, data.smallRT);
                    if (data.overrideCamera && data.restoreCamera)
                        RenderingUtils.SetViewAndProjectionMatrices(cmd, data.viewMatrix, data.projMatrix, false);
                    cmd.DrawRendererList(data.rendererListHandle);

                    // Blit para RT Grande (limpando primeiro)
                    CoreUtils.SetRenderTarget(cmd, data.largeRT, ClearFlag.Color, new Color(0, 0, 0, 0));
                    Blitter.BlitCameraTexture(cmd, data.smallRT, data.largeRT, data.blitMat, 0);

                    // Blur
                    cmd.SetGlobalVector("_Offsets", new Vector4(1.5f, 2.0f, 2.5f, 3.0f));
                    Blitter.BlitCameraTexture(cmd, data.largeRT, data.large2RT, data.blurMat, 0);

                    // Devolver para a Câmera
                    Blitter.BlitCameraTexture(cmd, data.large2RT, data.cameraColorTarget, data.blitMat, 0);
                });
            }
        }
    }

    public Material blitMaterial;
    RenderMetaballsPass _scriptableMetaballsPass;
    public RenderObjects.RenderObjectsSettings renderObjectsSettings = new RenderObjects.RenderObjectsSettings();
    [Range(1, 16)] public int downsamplingAmount;

    public override void Create()
    {
        RenderObjects.FilterSettings filter = renderObjectsSettings.filterSettings;
        _scriptableMetaballsPass = new RenderMetaballsPass("MetaballsPass", renderObjectsSettings.Event,
            filter.PassNames, filter.RenderQueueType, filter.LayerMask, renderObjectsSettings.cameraSettings, downsamplingAmount)
        {
            BlitMaterial = blitMaterial,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_scriptableMetaballsPass);
    }
}