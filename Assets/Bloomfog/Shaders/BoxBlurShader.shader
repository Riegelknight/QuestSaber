Shader "Custom/Bloomfog/BoxBlurShader"
{
    // URP-17/RenderGraph variant: source bound via `Blitter.BlitCameraTexture`.
    // `_BlitTexture` (2D) + `_BlitTexture_TexelSize`, vertex as fullscreen triangle.
    // The alpha value is used as the blur/upsample blend (`Blend SrcAlpha OneMinusSrcAlpha`).
    Properties
    {
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        ZTest Always ZWrite Off Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "Bloomfog Box Blur"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BlitTexture);
            float4 _BlitTexture_TexelSize;

            float _BlurAlpha;
            float _Offset;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 res = _BlitTexture_TexelSize.xy;

                half4 col = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-2, -2) * res);
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-1, -2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(0, -2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(1, -2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(2, -2) * res).rgb;

                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-2, -1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-1, -1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(0, -1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(1, -1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(2, -1) * res).rgb;

                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-2, 0) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-1, 0) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(0, 0) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(1, 0) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(2, 0) * res).rgb;

                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-2, 1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-1, 1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(0, 1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(1, 1) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(2, 1) * res).rgb;

                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-2, 2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(-1, 2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(0, 2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(1, 2) * res).rgb;
                col.rgb += SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv + float2(2, 2) * res).rgb;

                col.rgb /= 25;
                col.a = _BlurAlpha;

                return col;
            }
            ENDHLSL
        }
    }
    FallBack "Diffuse"
}
