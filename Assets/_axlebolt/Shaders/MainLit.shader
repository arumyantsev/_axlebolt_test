Shader "Axlebolt/MainLit"
{
    Properties
    {
        _AlbedoArray ("Albedo Array", 2DArray) = "" {}
        _NormalArray ("Normal Array", 2DArray) = "" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1.0
        _TintStrength ("Vertex Color Tint Strength", Range(0, 1)) = 0.5
        _AlphaClip ("Alpha Clip Threshold", Range(0, 1)) = 0.5
        [Toggle(_ALPHATEST_ON)] _AlphaTestToggle ("Alpha Test", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
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
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "AXLEBOLT_Lighting.hlsl"

            TEXTURE2D_ARRAY(_AlbedoArray);
            SAMPLER(sampler_AlbedoArray);
            TEXTURE2D_ARRAY(_NormalArray);
            SAMPLER(sampler_NormalArray);

            CBUFFER_START(UnityPerMaterial)
                half _NormalStrength;
                half _TintStrength;
                half _AlphaClip;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1; // lightmap UV
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
                // 1. Slice index из vertex color alpha
                int idx = (int)round(IN.color.a * 15.0);

                // 2. Sample texture arrays
                half4 albedoAO  = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, IN.uv, idx);
                half4 normalRMA = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, IN.uv, idx);

                // 3. Unpack
                half3 albedo    = albedoAO.rgb;
                half  ao        = albedoAO.a;
                // Normal: скейл XY → реконструкция Z (в таком порядке!)
                half2 normalXY  = (normalRMA.rg * 2.0 - 1.0) * _NormalStrength;
                half3 normalTS  = half3(normalXY, sqrt(saturate(1.0 - dot(normalXY, normalXY))));
                half  roughness = normalRMA.b;
                half  metallic  = normalRMA.a;
                half  smoothness = 1.0 - roughness;

                // 4. Alpha clip
                #ifdef _ALPHATEST_ON
                    clip(albedoAO.a - _AlphaClip);
                    ao = 1.0;
                #endif

                // 5. Арт-тинт из RGB vertex color
                albedo *= lerp(half3(1,1,1), IN.color.rgb * 2.0, _TintStrength);

                // 6. Normal в world space
                half3 normalWS = TransformTangentToWorld(normalTS,
                    half3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS));
                normalWS = normalize(normalWS);

                // 7. Сборка MERCS_SurfaceData (specular=0, convert происходит внутри AxleboltLighting)
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

                // 13. Fog
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
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D_ARRAY(_AlbedoArray);
            SAMPLER(sampler_AlbedoArray);

            CBUFFER_START(UnityPerMaterial)
                half _NormalStrength;
                half _TintStrength;
                half _AlphaClip;
            CBUFFER_END

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half   sliceAlpha : TEXCOORD1;
            };

            Varyings vertShadow(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normalOS);
                posWS = ApplyShadowBias(posWS, normWS, _LightDirection);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.uv = IN.uv;
                OUT.sliceAlpha = IN.color.a;
                return OUT;
            }

            half4 fragShadow(Varyings IN) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    int idx = (int)round(IN.sliceAlpha * 15.0);
                    half4 albedoAO = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, IN.uv, idx);
                    clip(albedoAO.a - _AlphaClip);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ============================================================
        // Pass 3: DepthOnly
        // ============================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma shader_feature_local _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_AlbedoArray);
            SAMPLER(sampler_AlbedoArray);

            CBUFFER_START(UnityPerMaterial)
                half _NormalStrength;
                half _TintStrength;
                half _AlphaClip;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half   sliceAlpha : TEXCOORD1;
            };

            Varyings vertDepth(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                OUT.sliceAlpha = IN.color.a;
                return OUT;
            }

            half4 fragDepth(Varyings IN) : SV_Target
            {
                #ifdef _ALPHATEST_ON
                    int idx = (int)round(IN.sliceAlpha * 15.0);
                    half4 albedoAO = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, IN.uv, idx);
                    clip(albedoAO.a - _AlphaClip);
                #endif
                return 0;
            }
            ENDHLSL
        }

        // ============================================================
        // Pass 4: Meta (лайтмап бейк)
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

            TEXTURE2D_ARRAY(_AlbedoArray);
            SAMPLER(sampler_AlbedoArray);

            CBUFFER_START(UnityPerMaterial)
                half _NormalStrength;
                half _TintStrength;
                half _AlphaClip;
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
                half4  color      : TEXCOORD1;
            };

            Varyings vertMeta(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = MetaVertexPosition(IN.positionOS, IN.uv1, IN.uv2,
                    unity_LightmapST, unity_DynamicLightmapST);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                return OUT;
            }

            half4 fragMeta(Varyings IN) : SV_Target
            {
                int idx = (int)round(IN.color.a * 15.0);
                half4 albedoAO = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, IN.uv, idx);
                half3 albedo = albedoAO.rgb;
                albedo *= lerp(half3(1,1,1), IN.color.rgb * 2.0, _TintStrength);

                MetaInput metaInput;
                metaInput.Albedo = albedo;
                metaInput.Emission = half3(0,0,0);
                return MetaFragment(metaInput);
            }
            ENDHLSL
        }
    }
}
