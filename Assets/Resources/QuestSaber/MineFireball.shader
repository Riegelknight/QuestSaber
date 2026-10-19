// ==============================================================================
//  MineFireball - additive, unlit VOLUME shader of the mine explosion (Round 10.11h).
// ==============================================================================
//  Renders the procedural sphere (Fx.MineSphereMesh) as real 3D gas fire -
//  NO billboard. Procedural 3D value noise (fbm, 2 octaves, cheap) in
//  OBJECT space, which "flows" over time; hot white center -> yellow -> orange -> red
//  (warm), soft falloff at the edge (no hard circle). _Fade globally fades out
//  (and cools the color slightly toward red), _Seed shifts the noise per explosion.
//  Blend SrcAlpha One, ZWrite Off, Cull Off (front AND back faces add ->
//  volumetric). No lighting, no shadow. Fits the Bloom/Bloomfog look.
// ==============================================================================
Shader "QuestSaber/MineFireball"
{
    Properties
    {
        [HDR]_Color ("Color", Color) = (1,1,1,1)
        _Fade ("Fade", Range(0,1)) = 1
        _Seed ("Seed", Float) = 0
        _Flow ("Flow Speed", Float) = 0.8
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 obj : TEXCOORD0; };

            fixed4 _Color;
            float _Fade;
            float _Seed;
            float _Flow;

            float Hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float Noise13(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = Hash13(i + float3(0.0, 0.0, 0.0));
                float n100 = Hash13(i + float3(1.0, 0.0, 0.0));
                float n010 = Hash13(i + float3(0.0, 1.0, 0.0));
                float n110 = Hash13(i + float3(1.0, 1.0, 0.0));
                float n001 = Hash13(i + float3(0.0, 0.0, 1.0));
                float n101 = Hash13(i + float3(1.0, 0.0, 1.0));
                float n011 = Hash13(i + float3(0.0, 1.0, 1.0));
                float n111 = Hash13(i + float3(1.0, 1.0, 1.0));
                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);
                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);
                return lerp(nxy0, nxy1, f.z);
            }

            float Fbm13(float3 p)
            {
                return Noise13(p) * 0.65 + Noise13(p * 2.07 + 13.1) * 0.35;
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.obj = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // We render ONLY the sphere SHELL (radius 0.5). The density comes from the
                // view direction: in the middle of the silhouette you look through the thick
                // core (ball->1), at the edge the view grazes the shell (ball->0) -> round
                // volume. NO solid ball: the noise tears the surface open with gaps
                // (high contrast, dense spots glow, it shows through in between).
                float3 op = i.obj;                            // point on the shell
                float3 nrm = normalize(op);                   // sphere normal (object space)
                float3 camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1.0)).xyz;
                float3 vd = normalize(camObj - op);           // view direction (object space)
                float facing = saturate(dot(nrm, vd));
                float ball = pow(facing, 1.35);               // volume silhouette
                float core = pow(facing, 3.2);                // small, very hot core

                float3 p = op * 2.0;                          // sphere radius 0.5 -> p in [-1,1]
                float t = _Time.y * _Flow;

                // Two noise scales: large flames billow, small ones tear the edge sharply open.
                float nBig = Fbm13(p * 2.0 + float3(_Seed, -t, _Seed * 0.7));
                float nSm = Fbm13(p * 6.5 - float3(t * 0.9, _Seed * 0.3, t * 0.5));
                float n = nBig * 0.68 + nSm * 0.32;

                // High contrast + with gaps -> clear flame tongues instead of soft mush.
                float flame = saturate((ball * (0.55 + 1.25 * n) - 0.30) * 2.6);

                // Brightness: hot core + flames.
                float heat = saturate(core * 1.1 + flame * 0.75);

                // Alpha DELIBERATELY < 1 (partially transparent); core contributes the most.
                float a = flame * (0.30 + 0.8 * core) * _Fade;

                // Cools slightly toward red when fading out.
                float h = saturate(heat) * lerp(0.6, 1.0, _Fade);

                float3 cRed = float3(0.80, 0.13, 0.02);
                float3 cOrange = float3(1.0, 0.45, 0.10);
                float3 cYellow = float3(1.0, 0.84, 0.38);
                float3 cWhite = float3(1.0, 0.98, 0.90);
                float3 warm;
                if (h < 0.35) warm = lerp(cRed, cOrange, h / 0.35);
                else if (h < 0.70) warm = lerp(cOrange, cYellow, (h - 0.35) / 0.35);
                else warm = lerp(cYellow, cWhite, (h - 0.70) / 0.30);

                fixed4 o;
                o.rgb = _Color.rgb * warm;
                o.a = _Color.a * a;
                return o;
            }
            ENDCG
        }
    }
    FallBack Off
}
