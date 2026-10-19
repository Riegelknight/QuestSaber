Shader "Custom/Bloomfog/PrepassShader"
{
    // URP-17/RenderGraph variant: The source is NO longer processed via `Graphics.Blit`/`tex2D(_MainTex)`,
    // but bound via `Blitter.BlitCameraTexture`. The source texture is then called
    // `_BlitTexture` (2D, mono capture camera) and its texel size is in `_BlitTexture_TexelSize`.
    // The fullscreen triangle vertex comes from core `Common.hlsl` (SV_VertexID).
    Properties
    {
        _FadeStart ("Fade Start", float) = 0.2
        _FadeEnd ("Fade End", float) = 0.1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Bloomfog Prepass"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Source bound by `Blitter` (not `_MainTex`!).
            TEXTURE2D(_BlitTexture);
            float4 _BlitTexture_TexelSize;

            float _FadeStart, _FadeEnd;
            float _Threshold;
            float _BrightnessMult;

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

            float remap(float2 input, float2 minMax, float2 outMinMax)
            {
                float value = max(input.x, input.y);
                return outMinMax.x + (value - minMax.x) * (outMinMax.y - outMinMax.x) / (minMax.y - minMax.x);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float t = _Threshold;
                float m = _BrightnessMult;

                // The Bloomfog fog texture is a blit target - alpha is not evaluated,
                // but is fully initialized (otherwise shader warning).
                half4 col = half4(0, 0, 0, 1);
                col.rgb = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.uv).rgb;

                half maximum = max(col.r, col.g);
                maximum = max(maximum, col.b);

                col.rgb *= ((maximum > t) ? 1.0 : 0.0) * m;

                float2 distFromCenter = abs((input.uv - float2(0.5, 0.5)) * 2);
                float fadeoutAmount = remap(distFromCenter, float2(1.0 - _FadeStart, 1.0 - _FadeEnd), float2(0.0, 1.0));
                col.rgb = lerp(col.rgb, float3(0, 0, 0), saturate(fadeoutAmount));

                return col;
            }
            ENDHLSL
        }
    }
    FallBack "Diffuse"
}
