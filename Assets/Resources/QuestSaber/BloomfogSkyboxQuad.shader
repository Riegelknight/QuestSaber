// ==============================================================================
//  BloomfogSkyboxQuad - glowing background from the Bloomfog texture.
// ==============================================================================
//  1:1 port from ArcViewer (Assets/Bloomfog/Shaders/BloomfogSkyboxQuad.shader):
//  A quad whose vertices are written directly in NDC (-1..1, z = 0) -
//  fills exactly the screen (per eye, multipass-stereo-stable, PICO rendering path
//  = MultiPass) and shows the soft-blurred lightshow (`_BloomfogTex`) as
//  "sky". Sample UV via `_FogTextureToScreenRatio` (the Bloomfog
//  feature sets it per frame from the capture geometry).
//  The quad is attached to the room anchor (inactive in passthrough) and draws with
//  queue 990 BEFORE all opaque geometry (ZWrite off) - it overpaints nothing.
// ==============================================================================
Shader "QuestSaber/BloomfogSkyboxQuad"
{
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            sampler2D _BloomfogTex;
            float2 _FogTextureToScreenRatio;

            v2f vert(appdata v)
            {
                v2f o;
                // NDC fullscreen: object/camera transforms are deliberately ignored.
                o.vertex = float4(v.vertex.xy, 0, 1);
                o.screenPos = ComputeNonStereoScreenPos(o.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 screenSpaceUV = i.screenPos.xy / i.screenPos.w;
                float2 scaledUV = float2(
                    (screenSpaceUV.x + -0.5) * _FogTextureToScreenRatio.x + 0.5,
                    (screenSpaceUV.y + -0.5) * _FogTextureToScreenRatio.y + 0.5
                );
                return tex2D(_BloomfogTex, scaledUV);
            }
            ENDCG
        }
    }
    FallBack Off
}