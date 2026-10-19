// ==============================================================================
//  Shockwave - expanding glow ring on hits (bomb/bad cut/note).
// ==============================================================================
//  Lightweight replacement for the original `ShockwaveEffect`: a billboarded
//  quad with a ring that runs outward via `_Progress` and fades out.
//  Additive, ZWrite off. Values per instance via MaterialPropertyBlock (pooled, no GC).
// ==============================================================================
Shader "QuestSaber/Shockwave"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Progress ("Progress", Range(0,1)) = 0
        _Thickness ("Thickness", Range(0.01,0.5)) = 0.12
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; };

            fixed4 _Color;
            float _Progress;
            float _Thickness;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv * 2.0 - 1.0;
                float r = length(p);
                float ring = smoothstep(_Progress - _Thickness, _Progress, r)
                           * (1.0 - smoothstep(_Progress, _Progress + _Thickness, r));
                float a = ring * (1.0 - _Progress) * _Color.a;
                return fixed4(_Color.rgb * a, a);
            }
            ENDCG
        }
    }
    FallBack Off
}
