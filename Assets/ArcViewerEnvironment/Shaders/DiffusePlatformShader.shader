// ==============================================================================
//  DiffusePlatformShader - diffuse environment surfaces (LinkinPark floor/tunnel).
// ==============================================================================
//  Round 09.10: converted to ArcViewer's look like PlatformShader - fake
//  reflection of the Bloomfog texture + ambient + laser light + lightshow tint +
//  Bloomfog fog, additionally this shader's `_LightingStrength` multiplier.
//  Deliberately without a normal-map branch (no tangent guarantee) and without
//  the original's GPU laser buffer (`_LaserColors`/`_Occlusion*` - the
//  LaserDataHandler only fills the arrays in LinkinPark; without them everything
//  would be black). Property names stay identical.
// ==============================================================================
Shader "Custom/DiffusePlatformShader"
{
    Properties
    {
        [HDR]_LaserColor ("Laser Color", Color) = (0,0,0,0)
        _ColorMult ("Laser Color Multiplier", float) = 1
        _BaseColor ("Base Color", Color) = (0.055,0.065,0.09,1)
        _MainTex ("Main Texture", 2D) = "white" {}
        _TextureDistance ("Max Texture Distance", float) = 100.0
        _NormalMap ("Normal Map", 2D) = "bump" {}
        _NormalDistance ("Max Normal Map Distance", float) = 100.0
        _SpecularMap ("Specular Map", 2D) = "white" {}
        _FogStartOffset ("Fog Start Offset", float) = 0
        _FogScale ("Fog Scale", float) = 1
        _FogHeightOffset ("Fog Height Offset", float) = 0
        _FogHeightScale ("Fog Height Scale", float) = 1
        _AmbientStrength ("Ambient Light", float) = 1
        _ReflectionStrength ("Reflection Strength", float) = 1
        _LightingStrength ("Lighting Brightness", float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

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
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                BLOOM_FOG_COORDS(2, 3, 4);
                float4 vertex : SV_POSITION;
            };

            uniform float4 _BloomfogTex_TexelSize;

            sampler2D _MainTex;
            float4 _MainTex_ST;

            fixed4 _LaserColor;
            fixed4 _BaseColor;
            float _ColorMult;
            float _TextureDistance, _NormalDistance;
            float _FogStartOffset, _FogScale;
            float _FogHeightOffset, _FogHeightScale;
            float _AmbientStrength;
            float _ReflectionStrength;
            float _LightingStrength;

            // 0 = mode "Default", 1 = "Animated".
            // (`_EnvironmentTint` is deliberately NO longer consumed - see PlatformShader.)
            uniform float _ProbeReflStrength;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldNormal = mul((float3x3)unity_ObjectToWorld, v.normal);

                BLOOM_FOG_INITIALIZE_VERT(o, v.vertex);
                o.fogCoord = 0; // is determined in the fragment from screenPos (init. against shader warning)
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                BLOOM_FOG_INITIALIZE_FRAG(i);

                float3 cameraOffset = _WorldSpaceCameraPos - i.worldPos;
                float cameraDistance = length(cameraOffset);

                // ROOM-local distance (Round 10.11): The texture->reflection transition
                // grows/shrinks with the stage (same RELATIVE look as at scale 1).
                // Previously, when scaling, distant surfaces flipped earlier to pure reflection
                // and became discolored (device feedback). Like the fog in BloomFog.cginc.
                float roomScale = max(_RoomScale, 0.001);
                float cameraDistanceLocal = cameraDistance / roomScale;

                float2 bloomfogRes = _BloomfogTex_TexelSize.xy;
                float3 worldNormal = normalize(i.worldNormal);

                // Fake reflection (like PlatformShader)
                float3 viewNormal = mul((float3x3)UNITY_MATRIX_V, worldNormal);
                float3 cameraDir = cameraOffset / cameraDistance;

                float2 originalFogCoord = (i.fogCoord * 2) - 1;
                originalFogCoord.y *= bloomfogRes;

                float fresnel = dot(cameraDir, worldNormal);
                float reflectionDist = fresnel * bloomfogRes.x * 0.5;
                float2 screenReflectPos = originalFogCoord + (viewNormal.xy * reflectionDist);

                screenReflectPos.y /= bloomfogRes;
                float2 screenReflectUV = (screenReflectPos + 1) * 0.5;

                // Keep minimum share small (see PlatformShader): most of the reflection
                // via the Fresnel angle -> arena stays gray at a perpendicular view.
                float reflectionMult = _ReflectionStrength * (0.18f + 0.82f * (1.0f - fresnel));

                fixed4 col = BLOOM_FOG_SAMPLE(screenReflectUV) * reflectionMult;
                col.rgb += _BaseColor.rgb;

                fixed4 skyCol = unity_AmbientSky * clamp(worldNormal.y, 0, 1);
                fixed4 equatorCol = unity_AmbientEquator * (1 - abs(worldNormal.y));
                fixed4 groundCol = unity_AmbientGround * abs(clamp(worldNormal.y, -1, 0));
                col += (skyCol + equatorCol + groundCol) * _AmbientStrength;

                col = lerp(col * tex2D(_MainTex, i.uv), col, clamp(cameraDistanceLocal / _TextureDistance, 0.001, 1));

                col.rgb *= _LightingStrength;
                // Only laser light (LightHandler); the global `_EnvironmentTint` addition is
                // REMOVED (colored every surface the same -> arena "blinked" over the whole area).
                col.rgb += _LaserColor.rgb * _LaserColor.a * _ColorMult;

                // REAL environment reflection (reflection probe `unity_SpecCube0`, URP).
                // Sharper mip (1 instead of 2) -> visible reflections.
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float3 reflDir = reflect(-viewDir, worldNormal);
                half4 probeSample = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, reflDir, 1.0);
                half3 probeCol = DecodeHDR(probeSample, unity_SpecCube0_HDR);
                float probeGloss = _ProbeReflStrength * _ReflectionStrength * (0.35f + 0.65f * (1.0f - fresnel));
                col.rgb += saturate(probeCol) * probeGloss;

                BLOOM_HEIGHT_FOG_APPLY(i, col, _FogStartOffset, _FogScale, _FogHeightOffset, _FogHeightScale);

                col.a = 1;
                return col;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}