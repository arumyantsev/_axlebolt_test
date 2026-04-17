Shader "Axlebolt/Foliage"
{
    Properties
    {
        [MainColor] _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Albedo ("Albedo (A=Alpha cutout)", 2D) = "white" {}
        _Normal ("Normal (RG), Roughness (B), Metallic (A)", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 2)) = 1.0

        _Cutoff ("Alpha Clip Threshold", Range(0, 1)) = 0.5

        [Header(HSV Color Variation)]
        _Hue ("Hue Shift", Range(0, 1)) = 0
        _Saturation ("Saturation", Range(-1, 1)) = 0
        _Brightness ("Brightness", Range(-1, 1)) = 0

        [Header(Translucency)]
        _TranslucentPower ("Translucent Power (leaves backlight)", Range(0, 1)) = 0.5

        [Header(Wind)]
        _SwaySpeed ("Sway Speed", Range(0, 5)) = 1.5
        _SwayStrength ("Sway Strength", Range(0, 0.5)) = 0.08
        _FlutterSpeed ("Flutter Speed", Range(0, 20)) = 8.0
        _FlutterStrength ("Flutter Strength", Range(0, 0.1)) = 0.02
        _SmoothnessScale ("Smoothness Scale", Range(0, 1)) = 0.3
        _AOStrength ("AO Strength (from vertex A)", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
        }

        // ============================================================
        // Pass 1: ForwardLit
        // ============================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _FORWARD_PLUS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "AXLEBOLT_Lighting.hlsl"

            TEXTURE2D(_Albedo);     SAMPLER(sampler_Albedo);
            TEXTURE2D(_Normal);     SAMPLER(sampler_Normal);

            // Per-instance Light Probe data
            UNITY_INSTANCING_BUFFER_START(PerInstance)
                UNITY_DEFINE_INSTANCED_PROP(half4, _ProbeColor)
                UNITY_DEFINE_INSTANCED_PROP(half4, _ProbeOcclusion)
            UNITY_INSTANCING_BUFFER_END(PerInstance)

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half  _NormalStrength;
                half  _Cutoff;
                half  _Hue;
                half  _Saturation;
                half  _Brightness;
                half  _TranslucentPower;
                float _SwaySpeed;
                float _SwayStrength;
                float _FlutterSpeed;
                float _FlutterStrength;
                half  _SmoothnessScale;
                half  _AOStrength;
            CBUFFER_END

            // Мини RGB2HSV/HSV2RGB (оптимизированные)
            half3 RGB2HSV(half3 c)
            {
                half4 K = half4(0.0, -1.0/3.0, 2.0/3.0, -1.0);
                half4 p = lerp(half4(c.bg, K.wz), half4(c.gb, K.xy), step(c.b, c.g));
                half4 q = lerp(half4(p.xyw, c.r), half4(c.r, p.yzx), step(p.x, c.r));
                half d = q.x - min(q.w, q.y);
                return half3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
            }
            half3 HSV2RGB(half3 c)
            {
                half3 rgb = clamp(abs(fmod(c.x * 6.0 + half3(0,4,2), 6) - 3.0) - 1.0, 0, 1);
                rgb = rgb * rgb * (3.0 - 2.0 * rgb);
                return saturate(c.z * lerp(half3(1,1,1), rgb, c.y));
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
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
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                // World pos для ветра
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);

                // Ветер: sway (всё растение) + flutter (листья)
                float swayGradient = IN.color.g;
                float phase = IN.color.b * 6.28;

                float sway = sin(_Time.y * _SwaySpeed + posWS.x * 0.5) * _SwayStrength * swayGradient;
                float flutter = sin(_Time.y * _FlutterSpeed + phase) * _FlutterStrength * swayGradient;
                posWS.xz += sway + flutter;

                OUT.positionCS  = TransformWorldToHClip(posWS);
                OUT.positionWS  = posWS;

                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                OUT.normalWS    = normInputs.normalWS;
                OUT.tangentWS   = normInputs.tangentWS;
                OUT.bitangentWS = normInputs.bitangentWS;
                OUT.uv          = IN.uv;
                OUT.color       = IN.color;
                OUT.fogFactor   = ComputeFogFactor(OUT.positionCS.z);

                OUTPUT_LIGHTMAP_UV(IN.uv1, unity_LightmapST, OUT.lightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);

                return OUT;
            }

            half4 frag(Varyings IN, bool facing : SV_IsFrontFace) : SV_Target
            {
                // 1. Sample textures
                half4 albedoAlpha = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, IN.uv) * _BaseColor;
                half4 normalRMA = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, IN.uv);

                // 2. Alpha clip ДО лайтинга
                clip(albedoAlpha.a - _Cutoff);

                // 3. HSV color variation per-leaf (vertex color R как маска)
                half3 albedo = albedoAlpha.rgb;
                half3 hsv = RGB2HSV(albedo);
                hsv.x += _Hue * IN.color.r;
                hsv.y += _Saturation * IN.color.r;
                hsv.z += _Brightness * IN.color.r;
                albedo = HSV2RGB(hsv);

                // 4. Unpack normal + flip для backface (2-sided rendering)
                half facingSign = facing ? 1.0 : -1.0;
                half2 normalXY = (normalRMA.rg * 2.0 - 1.0) * _NormalStrength;
                half3 normalTS = half3(normalXY * facingSign,
                    sqrt(saturate(1.0 - dot(normalXY, normalXY))));

                half3 normalWS = TransformTangentToWorld(normalTS,
                    half3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS));
                normalWS = normalize(normalWS) * facingSign;

                // 5. Roughness + metallic + AO (listva обычно non-metallic)
                half roughness = normalRMA.b;
                half metallic  = normalRMA.a;
                half smoothness = (1.0 - roughness) * _SmoothnessScale;

                // AO из vertex color A (как и было)
                half ao = lerp(1.0, IN.color.a, _AOStrength);

                // 6. MERCS_SurfaceData с translucency в masks.r и facing в masks.y
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
                surfaceData.masks      = half4(_TranslucentPower, facingSign, 0, 0);
                surfaceData.alpha      = albedoAlpha.a;

                // 7. Vegetation lighting
                half3 color = AxleboltLighting_Vegetation(surfaceData, AXLEBOLT_GI_ARGS(IN));

                // 8. Per-instance Light Probe tint (от InstancedGrassRenderer)
                half4 probeCol = UNITY_ACCESS_INSTANCED_PROP(PerInstance, _ProbeColor);
                if (probeCol.w > 0.5h)
                {
                    // probeColor задан — тинтим ambient
                    color *= probeCol.rgb * 2.0h; // *2 потому что значения ~0.5 = нейтральные
                }

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
            Cull Off

            HLSLPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half  _NormalStrength;
                half  _Cutoff;
                half  _Hue;
                half  _Saturation;
                half  _Brightness;
                half  _TranslucentPower;
                float _SwaySpeed;
                float _SwayStrength;
                float _FlutterSpeed;
                float _FlutterStrength;
                half  _SmoothnessScale;
                half  _AOStrength;
            CBUFFER_END

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vertShadow(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normalOS);

                // Ветер и для теней (иначе тени не совпадают с геометрией)
                float swayGradient = IN.color.g;
                float phase = IN.color.b * 6.28;
                float sway = sin(_Time.y * _SwaySpeed + posWS.x * 0.5) * _SwayStrength * swayGradient;
                float flutter = sin(_Time.y * _FlutterSpeed + phase) * _FlutterStrength * swayGradient;
                posWS.xz += sway + flutter;

                posWS = ApplyShadowBias(posWS, normWS, _LightDirection);
                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 fragShadow(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, IN.uv);
                clip(tex.a - _Cutoff);
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

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half  _NormalStrength;
                half  _Cutoff;
                half  _Hue;
                half  _Saturation;
                half  _Brightness;
                half  _TranslucentPower;
                float _SwaySpeed;
                float _SwayStrength;
                float _FlutterSpeed;
                float _FlutterStrength;
                half  _SmoothnessScale;
                half  _AOStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vertMeta(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                OUT.positionCS = MetaVertexPosition(IN.positionOS, IN.uv1, IN.uv2,
                    unity_LightmapST, unity_DynamicLightmapST);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 fragMeta(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, IN.uv) * _BaseColor;
                clip(tex.a - _Cutoff);

                MetaInput metaInput;
                metaInput.Albedo = tex.rgb;
                metaInput.Emission = half3(0,0,0);
                return MetaFragment(metaInput);
            }
            ENDHLSL
        }
    }
}
