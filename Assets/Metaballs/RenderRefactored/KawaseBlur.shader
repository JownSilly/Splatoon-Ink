Shader "Custom/RenderFeature/KawaseBlur"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "KawaseBlur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag

            // Core.hlsl pulls in the shader library, Blit.hlsl provides the
            // Attributes/Varyings structs and the full-screen-triangle Vert()
            // function that the RenderGraph Blitter / AddBlitPass API expects.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // AddBlitPass binds the source texture to _BlitTexture (not _MainTex).
            // _BlitTexture and _BlitTexture_TexelSize are already declared by Blit.hlsl.
            float _offset;

            half4 frag(Varyings input) : SV_Target
            {
                float2 res = _BlitTexture_TexelSize.xy;
                float i = _offset;

                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(i, i) * res);
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(i, -i) * res);
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(-i, i) * res);
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord + float2(-i, -i) * res);
                col /= 5.0h;

                return col;
            }
            ENDHLSL
        }
    }
}
