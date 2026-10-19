// ==============================================================================
//  LaserShader - emissive laser beam of the ArcViewer environment.
// ==============================================================================
//  Round 09.10: reset to the ArcViewer ORIGINAL state (1:1 port from
//  ArcViewer-main `Assets/Shaders/Environment/LaserShader.shader`): The beam
//  now reads the Bloomfog texture (`_BloomfogTex`, BloomFog.cginc) and blends
//  the near range into the soft glowing fog with it - exactly the ArcViewer look.
//  Previously there was an "adapted" variant here WITHOUT fog application (the texture
//  was never read -> everything looked dull and the capture effort was wasted).
//  Property names `_LaserColor`/`_ColorMult` identical (LightHandler sets them
//  via MaterialPropertyBlock); the fog properties carry ArcViewer's material
//  values as defaults (Laser.mat there: _FogScale 0.5 - MrSaber's material does not have
//  the property, the shader default takes it over).
// ==============================================================================
Shader "Custom/LaserShader"
{
    Properties
    {
        [HDR]_LaserColor ("Color", Color) = (1,1,1,1)
        _ColorMult ("Color Multiplier", float) = 1
        _FogStartOffset ("Fog Start Offset", float) = 0
        _FogScale ("Fog Scale", float) = 0.5
        _FogHeightOffset ("Fog Height Offset", float) = 0
        _FogHeightScale ("Fog Height Scale", float) = 1
    }
    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType"="Transparent"
        }

        LOD 100
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "BloomFog.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                BLOOM_FOG_COORDS(0, 1, 2);
                float4 vertex : SV_POSITION;
            };

            fixed4 _LaserColor;
            float _ColorMult;
            float _FogStartOffset, _FogScale;
            float _FogHeightOffset, _FogHeightScale;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                BLOOM_FOG_INITIALIZE_VERT(o, v.vertex);
                o.fogCoord = 0; // is determined in the fragment from screenPos (init. against shader warning)
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                BLOOM_FOG_INITIALIZE_FRAG(i);

                fixed4 col = _LaserColor * _ColorMult;
                BLOOM_HEIGHT_FOG_APPLY(i, col, _FogStartOffset, _FogScale, _FogHeightOffset, _FogHeightScale);

                return col;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}