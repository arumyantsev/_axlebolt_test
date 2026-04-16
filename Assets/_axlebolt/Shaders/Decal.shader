Shader "Axlebolt/Decal"
{
    Properties
    {
        _DecalAtlas ("Decal Atlas", 2D) = "white" {}
        _TintColor ("Tint", Color) = (1,1,1,1)
        _AlphaMultiplier ("Alpha Strength", Range(0, 1)) = 1.0
        [KeywordEnum(Alpha, Multiply)] _BlendMode ("Blend Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        // ============================================================
        // Pass: Alpha Blend
        // ============================================================
        Pass
        {
            Name "DecalAlpha"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Offset -1, -1
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local _BLENDMODE_ALPHA _BLENDMODE_MULTIPLY

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_DecalAtlas);
            SAMPLER(sampler_DecalAtlas);

            CBUFFER_START(UnityPerMaterial)
                half4 _TintColor;
                half  _AlphaMultiplier;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_DecalAtlas, sampler_DecalAtlas, IN.uv);
                half3 color = tex.rgb * _TintColor.rgb;
                half alpha = tex.a * _AlphaMultiplier;

                #ifdef _BLENDMODE_MULTIPLY
                    // Multiply: выходной цвет = текстура, альфа = маска
                    // Blend DstColor Zero → result = dst * src
                    // Нужно вернуть color как множитель, alpha не важен
                    return half4(lerp(half3(1,1,1), color, alpha), 1.0);
                #else
                    // Alpha blend
                    return half4(color, alpha);
                #endif
            }
            ENDHLSL
        }
    }
}
