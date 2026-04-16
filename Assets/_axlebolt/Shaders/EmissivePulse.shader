Shader "Axlebolt/EmissivePulse"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0, 0, 0, 1)
        _EmissionColor ("Emission Color", Color) = (0.1, 0.8, 0.6, 1)
        _EmissionIntensity ("Intensity", Range(0, 10)) = 3.0
        _PulseSpeed ("Pulse Speed", Range(0, 5)) = 1.2
        _PulseMin ("Min Brightness", Range(0, 1)) = 0.3
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
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
                half  _EmissionIntensity;
                float _PulseSpeed;
                half  _PulseMin;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  fogFactor  : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = posInputs.positionCS;
                OUT.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half pulse = lerp(_PulseMin, 1.0, sin(_Time.y * _PulseSpeed) * 0.5 + 0.5);
                half3 emission = _EmissionColor.rgb * _EmissionIntensity * pulse;
                half3 color = _BaseColor.rgb + emission;
                color = MixFog(color, IN.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // ============================================================
        // Pass 2: Meta (лайтмап бейк — средняя яркость без пульсации)
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

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
                half  _EmissionIntensity;
                float _PulseSpeed;
                half  _PulseMin;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv1        : TEXCOORD1;
                float2 uv2        : TEXCOORD2;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vertMeta(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = MetaVertexPosition(IN.positionOS, IN.uv1, IN.uv2,
                    unity_LightmapST, unity_DynamicLightmapST);
                return OUT;
            }

            half4 fragMeta(Varyings IN) : SV_Target
            {
                MetaInput metaInput;
                metaInput.Albedo = _BaseColor.rgb;
                // Средняя яркость без пульсации для бейка
                metaInput.Emission = _EmissionColor.rgb * _EmissionIntensity * 0.7;
                return MetaFragment(metaInput);
            }
            ENDHLSL
        }
    }
}
