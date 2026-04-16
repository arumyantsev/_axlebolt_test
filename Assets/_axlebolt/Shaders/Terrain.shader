Shader "Axlebolt/Terrain"
{
    Properties
    {
        [NoScaleOffset] _TerrainAlbedoArray ("Terrain Albedo Array", 2DArray) = "" {}
        [NoScaleOffset] _TerrainNormalArray ("Terrain Normal Array", 2DArray) = "" {}
        [NoScaleOffset] _SplatMap ("Splat Map", 2D) = "black" {}
        _Tiling0 ("Layer 0 Tiling (Base)", Float) = 0.2
        _Tiling1 ("Layer 1 Tiling (Splat R)", Float) = 0.2
        _Tiling2 ("Layer 2 Tiling (Splat G)", Float) = 0.2
        _Tiling3 ("Layer 3 Tiling (Splat B)", Float) = 0.2
        _Tiling4 ("Layer 4 Tiling (Splat A)", Float) = 0.2
        _BlendSharpness ("Height Blend Sharpness", Range(0.01, 0.5)) = 0.15
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1.0
        _TintStrength ("Vertex Color Tint Strength", Range(0, 1)) = 0.5
        _NoiseStrength ("Vertex Alpha Noise Strength", Range(0, 1)) = 0.3
        _SmoothnessScale ("Smoothness Scale (Terrain)", Range(0, 1)) = 0.5
    }

    CustomEditor "TerrainShaderGUI"

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-1"
        }

        // ============================================================
        // Pass 1: ForwardLit
        // ============================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _FORWARD_PLUS
            #pragma multi_compile _ _CLUSTERED_RENDERING
            #pragma multi_compile _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "AXLEBOLT_Lighting.hlsl"

            TEXTURE2D_ARRAY(_TerrainAlbedoArray);
            SAMPLER(sampler_TerrainAlbedoArray);
            TEXTURE2D_ARRAY(_TerrainNormalArray);
            SAMPLER(sampler_TerrainNormalArray);
            TEXTURE2D(_SplatMap);
            SAMPLER(sampler_SplatMap);

            CBUFFER_START(UnityPerMaterial)
                float _Tiling0;
                float _Tiling1;
                float _Tiling2;
                float _Tiling3;
                float _Tiling4;
                half  _BlendSharpness;
                half  _NormalStrength;
                half  _TintStrength;
                half  _NoiseStrength;
                half  _SmoothnessScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float3 positionWS   : TEXCOORD1;
                half3  normalWS     : TEXCOORD2;
                half3  tangentWS    : TEXCOORD3;
                half3  bitangentWS  : TEXCOORD4;
                half4  color        : TEXCOORD5;
                float  fogFactor    : TEXCOORD6;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 7);
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS  = posInputs.positionCS;
                OUT.positionWS  = posInputs.positionWS;
                OUT.normalWS    = normInputs.normalWS;
                OUT.tangentWS   = normInputs.tangentWS;
                OUT.bitangentWS = normInputs.bitangentWS;
                OUT.uv          = IN.uv;
                OUT.color       = IN.color;
                OUT.fogFactor   = ComputeFogFactor(posInputs.positionCS.z);

                OUTPUT_LIGHTMAP_UV(IN.uv1, unity_LightmapST, OUT.lightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // 1. SplatMap по UV0 меша (всегда тайлинг 1:1)
                half4 splat = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, IN.uv);

                // 2. Per-layer world-space tiling UV
                float2 worldUV = IN.positionWS.xz;
                float2 tUV0 = worldUV * _Tiling0;
                float2 tUV1 = worldUV * _Tiling1;
                float2 tUV2 = worldUV * _Tiling2;
                float2 tUV3 = worldUV * _Tiling3;
                float2 tUV4 = worldUV * _Tiling4;

                // 3. Вес базового слоя (0) = всё что не покрыто сплатмапом
                half baseWeight = saturate(1.0 - (splat.r + splat.g + splat.b + splat.a));

                // 4. Sample 5 слоёв albedo (0=base, 1-4=splatmap RGBA)
                half4 a0 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV0, 0);
                half4 a1 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV1, 1);
                half4 a2 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV2, 2);
                half4 a3 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV3, 3);
                half4 a4 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV4, 4);

                // 5. Height-based blend (height из A канала альбедо + splat вес)
                half h0 = a0.a + baseWeight;
                half h1 = a1.a + splat.r;
                half h2 = a2.a + splat.g;
                half h3 = a3.a + splat.b;
                half h4 = a4.a + splat.a;

                half maxH = max(max(max(h0, h1), max(h2, h3)), h4) - _BlendSharpness;
                half w0 = max(h0 - maxH, 0);
                half w1 = max(h1 - maxH, 0);
                half w2 = max(h2 - maxH, 0);
                half w3 = max(h3 - maxH, 0);
                half w4 = max(h4 - maxH, 0);
                half wSum = w0 + w1 + w2 + w3 + w4 + 0.001;

                // 6. Blend albedo
                half3 albedo = (a0.rgb * w0 + a1.rgb * w1 + a2.rgb * w2 + a3.rgb * w3 + a4.rgb * w4) / wSum;

                // 7. Sample и blend нормали (тот же тайлинг)
                half4 n0 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV0, 0);
                half4 n1 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV1, 1);
                half4 n2 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV2, 2);
                half4 n3 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV3, 3);
                half4 n4 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV4, 4);
                half4 nBlend = (n0 * w0 + n1 * w1 + n2 * w2 + n3 * w3 + n4 * w4) / wSum;

                // 7. Unpack: скейл XY → реконструкция Z
                half2 normalXY = (nBlend.rg * 2.0 - 1.0) * _NormalStrength;
                half3 normalTS = half3(normalXY, sqrt(saturate(1.0 - dot(normalXY, normalXY))));
                half roughness = nBlend.b;
                half ao = nBlend.a;
                half metallic = 0;
                // motherland style: smoothness = (1 - roughness) * _SmoothnessScale
                half smoothness = (1.0 - roughness) * _SmoothnessScale;

                // 8. Арт-тинт из RGB vertex color (нейтраль 0.5)
                albedo *= lerp(half3(1,1,1), IN.color.rgb * 2.0, _TintStrength);

                // 9. Noise из vertex alpha
                albedo *= lerp(1.0 - _NoiseStrength, 1.0 + _NoiseStrength, IN.color.a);

                // 9. Normal в world space
                half3 normalWS = TransformTangentToWorld(normalTS,
                    half3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS));
                normalWS = normalize(normalWS);

                // 10. MERCS_SurfaceData + весь лайтинг
                half3 viewDirWS = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));

                MERCS_SurfaceData surfaceData = (MERCS_SurfaceData)0;
                surfaceData.albedo     = albedo;
                surfaceData.metallic   = metallic;
                surfaceData.specular   = half3(0, 0, 0);
                surfaceData.smoothness = smoothness;
                surfaceData.occlusion  = ao;
                surfaceData.normalWS   = normalWS;
                surfaceData.viewDirWS  = viewDirWS;
                surfaceData.positionWS = IN.positionWS;
                surfaceData.alpha      = 1.0;

                half3 color = AxleboltLighting(surfaceData, AXLEBOLT_GI_ARGS(IN));

                // 14. Fog
                color = MixFog(color, IN.fogFactor);

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ============================================================
        // Pass 2: ShadowCaster
        // ============================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vertShadow(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normalOS);
                posWS = ApplyShadowBias(posWS, normWS, _LightDirection);
                OUT.positionCS = TransformWorldToHClip(posWS);
                return OUT;
            }

            half4 fragShadow(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // ============================================================
        // Pass 3: Meta
        // ============================================================
        Pass
        {
            Name "Meta"
            Tags { "LightMode" = "Meta" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertMeta
            #pragma fragment fragMeta

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

            TEXTURE2D_ARRAY(_TerrainAlbedoArray);
            SAMPLER(sampler_TerrainAlbedoArray);
            TEXTURE2D(_SplatMap);
            SAMPLER(sampler_SplatMap);

            CBUFFER_START(UnityPerMaterial)
                float _Tiling0;
                float _Tiling1;
                float _Tiling2;
                float _Tiling3;
                float _Tiling4;
                half  _BlendSharpness;
                half  _NormalStrength;
                half  _TintStrength;
                half  _NoiseStrength;
                half  _SmoothnessScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half4  color      : TEXCOORD2;
            };

            Varyings vertMeta(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = MetaVertexPosition(IN.positionOS, IN.uv1, IN.uv2,
                    unity_LightmapST, unity_DynamicLightmapST);
                OUT.uv = IN.uv;
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.color = IN.color;
                return OUT;
            }

            half4 fragMeta(Varyings IN) : SV_Target
            {
                half4 splat = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, IN.uv);
                float2 worldUV = IN.positionWS.xz;

                half baseWeight = saturate(1.0 - (splat.r + splat.g + splat.b + splat.a));

                half4 a0 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, worldUV * _Tiling0, 0);
                half4 a1 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, worldUV * _Tiling1, 1);
                half4 a2 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, worldUV * _Tiling2, 2);
                half4 a3 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, worldUV * _Tiling3, 3);
                half4 a4 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, worldUV * _Tiling4, 4);

                half h0 = a0.a + baseWeight;
                half h1 = a1.a + splat.r;
                half h2 = a2.a + splat.g;
                half h3 = a3.a + splat.b;
                half h4 = a4.a + splat.a;
                half maxH = max(max(max(h0, h1), max(h2, h3)), h4) - _BlendSharpness;
                half w0 = max(h0 - maxH, 0);
                half w1 = max(h1 - maxH, 0);
                half w2 = max(h2 - maxH, 0);
                half w3 = max(h3 - maxH, 0);
                half w4 = max(h4 - maxH, 0);
                half wSum = w0 + w1 + w2 + w3 + w4 + 0.001;

                half3 albedo = (a0.rgb * w0 + a1.rgb * w1 + a2.rgb * w2 + a3.rgb * w3 + a4.rgb * w4) / wSum;
                albedo *= lerp(half3(1,1,1), IN.color.rgb * 2.0, _TintStrength);
                albedo *= lerp(1.0 - _NoiseStrength, 1.0 + _NoiseStrength, IN.color.a);

                MetaInput metaInput;
                metaInput.Albedo = albedo;
                metaInput.Emission = half3(0,0,0);
                return MetaFragment(metaInput);
            }
            ENDHLSL
        }
    }
}
