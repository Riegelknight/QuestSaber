// ==============================================================================
//  Spark - additive unlit shader for the spark/explosion particles (Fx).
// ==============================================================================
//  Procedurally built ParticleSystems (note-hit sparks, mine explosion)
//  need a thin, additively glowing material with HDR color (_Color is
//  set per instance via MaterialPropertyBlock - red/blue/white flash). The
//  particle vcolor (color-over-lifetime fade) drives the transparency.
//  No lighting, no ZWrite: Blend SrcAlpha One, queue Transparent (like the
//  ArcViewer lasers, so they fit the Bloom/Bloomfog look).
//  Model: Resources/BeatSaber/Shockwave.shader (same structure).
// ==============================================================================
Shader "QuestSaber/Spark"
{
    Properties
    {
        [HDR]_Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;   // particle fade (color-over-lifetime)
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
            };

            fixed4 _Color;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = _Color;
                c.a *= i.color.a;       // fade-out over the lifetime
                return c;
            }
            ENDCG
        }
    }
    FallBack Off
}