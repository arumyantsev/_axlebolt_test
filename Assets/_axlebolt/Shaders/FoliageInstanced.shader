Shader "Axlebolt/FoliageInstanced"
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
        _TranslucentPower ("Translucent Power", Range(0, 1)) = 0.5

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
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "AXLEBOLT_Lighting.hlsl"

            TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
            TEXTURE2D(_Normal); SAMPLER(sampler_Normal);

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

            // Per-instance data через StructuredBuffer
            struct GrassInstanceData
            {
                float4x4 objectToWorld;
                float4 probeColor;   // xyz = ambient RGB from Light Probes, w = 1
                float4 occlusion;    // shadowmask occlusion
            };

            StructuredBuffer<GrassInstanceData> _InstanceBuffer;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
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
                half4  vertexColor  : TEXCOORD5;
                half3  probeColor   : TEXCOORD6;
                half   shadowOccl   : TEXCOORD7;
                float  fogFactor    : TEXCOORD8;
            };

            Varyings vert(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT = (Varyings)0;

                GrassInstanceData inst = _InstanceBuffer[instanceID];
                float4x4 otw = inst.objectToWorld;

                // Transform
                float3 posWS = mul(otw, float4(IN.positionOS.xyz, 1)).xyz;

                // Ветер
                float swayGradient = IN.color.g;
                float phase = IN.color.b * 6.28;
                float sway = sin(_Time.y * _SwaySpeed + posWS.x * 0.5) * _SwayStrength * swayGradient;
                float flutter = sin(_Time.y * _FlutterSpeed + phase) * _FlutterStrength * swayGradient;
                posWS.xz += sway + flutter;

                OUT.positionCS = TransformWorldToHClip(posWS);
                OUT.positionWS = posWS;
                OUT.uv = IN.uv;
                OUT.vertexColor = IN.color;

                // Normal/tangent/bitangent transform
                float3x3 nm = (float3x3)otw;
                OUT.normalWS = normalize(mul(nm, IN.normalOS));
                OUT.tangentWS = normalize(mul(nm, IN.tangentOS.xyz));
                OUT.bitangentWS = cross(OUT.normalWS, OUT.tangentWS) * IN.tangentOS.w;

                // Per-instance probe
                OUT.probeColor = inst.probeColor.rgb;
                OUT.shadowOccl = inst.occlusion.r;

                OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);

                return OUT;
            }

            half4 frag(Varyings IN, bool facing : SV_IsFrontFace) : SV_Target
            {
                // 1. Sample textures
                half4 albedoAlpha = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, IN.uv) * _BaseColor;
                half4 normalRMA = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, IN.uv);

                clip(albedoAlpha.a - _Cutoff);

                // 2. HSV variation
                half3 albedo = albedoAlpha.rgb;
                half3 hsv = RGB2HSV(albedo);
                hsv.x += _Hue * IN.vertexColor.r;
                hsv.y += _Saturation * IN.vertexColor.r;
                hsv.z += _Brightness * IN.vertexColor.r;
                albedo = HSV2RGB(hsv);

                // 3. Normal map + facing flip
                half facingSign = facing ? 1.0 : -1.0;
                half2 normalXY = (normalRMA.rg * 2.0 - 1.0) * _NormalStrength * facingSign;
                half3 normalTS = half3(normalXY, sqrt(saturate(1.0 - dot(normalXY, normalXY))));
                half3 normalWS = normalize(
                    normalTS.x * IN.tangentWS +
                    normalTS.y * IN.bitangentWS +
                    normalTS.z * IN.normalWS
                ) * facingSign;

                // 4. Roughness + AO
                half smoothness = (1.0 - normalRMA.b) * _SmoothnessScale;
                half metallic = normalRMA.a;
                half ao = lerp(1.0, IN.vertexColor.a, _AOStrength);

                // 5. MERCS SurfaceData (идентично Foliage.shader)
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

                // 6. MERCS BRDF + MetallicToSpecularConvert (как в Foliage)
                MERCS_BRDFdata brdfData;
                InitializeBRDFdata(surfaceData, brdfData);
                MERCS_MetallicToSpecularConvert(surfaceData);

                // 7. Main light (MERCS vegetation: albedo + spec + backlight)
                half4 shadowMask = half4(IN.shadowOccl, 1, 1, 1);
                half3 mainLight = MERCS_MainLight_Vegetation(surfaceData, brdfData, IN.shadowOccl);

                // 8. Additional lights (Blinn-Phong как в MERCS)
                half3 addLights = MERCS_AdditionalLights_half(surfaceData);

                // 9. Ambient: per-instance probe color (заменяет SampleSH)
                half3 ambient = surfaceData.albedo * IN.probeColor * ao;

                // 10. Fresnel reflection (как в Foliage)
                half3 fresnel = MERCS_Fresnel(surfaceData, brdfData);
                half3 reflection = GlossyEnvironmentReflection(brdfData.reflectVector, brdfData.perceptualRoughness, 1.0h) * fresnel;

                // 11. Combine
                half3 color = ambient + reflection * ao + mainLight + addLights;
                color = MixFog(color, IN.fogFactor);

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ShadowCaster
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

            struct GrassInstanceData
            {
                float4x4 objectToWorld;
                float4 probeColor;
                float4 occlusion;
            };

            StructuredBuffer<GrassInstanceData> _InstanceBuffer;
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
            };

            Varyings vertShadow(Attributes IN, uint instanceID : SV_InstanceID)
            {
                Varyings OUT;
                GrassInstanceData inst = _InstanceBuffer[instanceID];

                float3 posWS = mul(inst.objectToWorld, float4(IN.positionOS.xyz, 1)).xyz;
                float3 normWS = normalize(mul((float3x3)inst.objectToWorld, IN.normalOS));

                // Ветер в тенях тоже
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
    }
}
