#ifndef AXLEBOLT_LIGHTING_INCLUDED
#define AXLEBOLT_LIGHTING_INCLUDED

// =============================================================
// AXLEBOLT Lighting — точный порт MERCS base shaders
// (CGRUM MERX / Axlebolt Standoff tech art).
// Pipeline:
//   1. InitializeSurfaceData (albedo, metallic, smoothness, normal)
//   2. InitializeBRDFdata
//   3. MetallicToSpecularConvert (ВАЖНО: albedo *= (1-metallic))
//   4. AmbientLight (lightmap + SH dominant specular)
//   5. MainLight (albedo + specBRDF) * Lambert
//   6. AdditionalLights Blinn-Phong
//   7. Final combine + fog
// =============================================================

// ------------- Utils -------------

half Fast_Pow4_half(half x)
{
    half p = x * x;
    return p * p;
}

half3 SafeNormalizeHalf(half3 v)
{
    half dp3 = max(HALF_MIN, dot(v, v));
    return v * rsqrt(dp3);
}

// ------------- Structs -------------

struct MERCS_SurfaceData
{
    half3 albedo;
    half  metallic;
    half3 specular;
    half  smoothness;
    half  occlusion;
    half4 masks;
    half3 emission;
    float3 positionWS;
    half3 viewDirWS;
    half3 normalWS;
    half  alpha;
};

struct MERCS_BRDFdata
{
    half perceptualRoughness;
    half roughness;
    half roughness2;
    half roughness2minusOne;
    half normalizationTerm;
    half3 reflectVector;
    half NoV;
};

// ------------- Init -------------

inline void InitializeBRDFdata(in MERCS_SurfaceData s, out MERCS_BRDFdata b)
{
    b = (MERCS_BRDFdata)0;
    half perceptualRoughness = PerceptualSmoothnessToPerceptualRoughness(s.smoothness);
    half roughness = max(perceptualRoughness * perceptualRoughness, HALF_MIN_SQRT);
    half roughness2 = roughness * roughness;

    b.perceptualRoughness = perceptualRoughness;
    b.roughness = roughness;
    b.roughness2 = roughness2;
    b.roughness2minusOne = roughness2 - 1.0h;
    b.normalizationTerm = roughness * 4.0h + 2.0h;
    b.reflectVector = reflect(-s.viewDirWS, s.normalWS);
    b.NoV = saturate(dot(s.normalWS, s.viewDirWS)) + 1e-5h;
}

// КРИТИЧНО: albedo уменьшается у metals, specular берёт цвет от albedo
inline void MERCS_MetallicToSpecularConvert(inout MERCS_SurfaceData s)
{
    s.specular = lerp(half3(0.04, 0.04, 0.04), s.albedo, s.metallic);
    s.albedo   = s.albedo * (1.0h - s.metallic);
}

// ------------- BRDF specular -------------

half3 MERCS_BRDF(MERCS_SurfaceData s, MERCS_BRDFdata b, half3 lightDir)
{
    half3 halfDir = SafeNormalizeHalf(s.viewDirWS + lightDir);
    half LoH = saturate(dot(lightDir, halfDir));
    half3 NxH = cross(s.normalWS, halfDir);
    float d = b.roughness2 - dot(NxH, NxH) * b.roughness2minusOne + 0.00001f;
    half specularTerm = b.roughness2 / (half(d * d) * max(0.1h, LoH * LoH) * b.normalizationTerm);
    return specularTerm * s.specular;
}

// ------------- Blinn-Phong (additional lights) -------------

half3 MERCS_CalculateBlinnPhong(Light light, MERCS_SurfaceData s)
{
    half3 H = SafeNormalizeHalf(s.viewDirWS + light.direction);
    half HdN = saturate(dot(H, s.normalWS));

    half3 diffuseColor = s.albedo * saturate(dot(s.normalWS, light.direction));
    half3 specular = s.specular * pow(HdN, exp2(s.smoothness * 10.0h + 1.0h)) * FOUR_PI;

    return (diffuseColor + specular) * light.distanceAttenuation * light.shadowAttenuation * light.color * s.occlusion;
}

// ------------- Fresnel для indirect -------------

half3 MERCS_Fresnel(MERCS_SurfaceData s, MERCS_BRDFdata b)
{
    half reflectivity = ReflectivitySpecular(s.specular);
    half grazingTerm = saturate(s.smoothness + reflectivity);
    half surfaceReduction = 1.0h / (b.roughness2 + 1.0h);
    return surfaceReduction * lerp(s.specular, grazingTerm, Fast_Pow4_half(1.0h - b.NoV));
}

// ------------- Main Light (stylized: albedo+spec под Lambert) -------------

half3 MERCS_MainLight(MERCS_SurfaceData s, MERCS_BRDFdata b, half shadowMaskW)
{
    float4 shadowCoord = TransformWorldToShadowCoord(s.positionWS);
    Light mainLight = GetMainLight(shadowCoord);

    half shadow = shadowMaskW * mainLight.shadowAttenuation;

    half3 specularMainLight = MERCS_BRDF(s, b, mainLight.direction);
    // LightingLambert: saturate(NdotL) * color
    half NdotL = saturate(dot(s.normalWS, mainLight.direction));
    return (s.albedo + specularMainLight) * shadow * mainLight.color * NdotL;
}

// ------------- Additional lights (Blinn-Phong) -------------

half3 MERCS_AdditionalLights_half(MERCS_SurfaceData s)
{
    half3 combine = half3(0, 0, 0);
    #ifdef _ADDITIONAL_LIGHTS
        uint pixelLightCount = GetAdditionalLightsCount();
        for (uint i = 0u; i < pixelLightCount; ++i)
        {
            Light light = GetAdditionalLight(i, s.positionWS);
            combine += MERCS_CalculateBlinnPhong(light, s);
        }
    #endif
    return combine;
}

// ------------- Ambient Lighting (lightmap / SH) -------------

half4 MERCS_AmbientLight(MERCS_SurfaceData s, MERCS_BRDFdata b, float2 lightmapUV, half3 vertexSH)
{
    half3 fresnel = MERCS_Fresnel(s, b);
    half4 shadowMask = 1;
    half3 ambientLight = 0;
    half3 reflection = 0;

    #if defined(LIGHTMAP_ON)
        // Directional lightmap: получаем направление L и illuminance
        shadowMask = SAMPLE_SHADOWMASK(lightmapUV);

        half4 L = SAMPLE_TEXTURE2D(unity_LightmapInd, samplerunity_Lightmap, lightmapUV);
        L.xyz = L.xyz - 0.5h;

        half4 encodedIlluminance = SAMPLE_TEXTURE2D(unity_Lightmap, samplerunity_Lightmap, lightmapUV);
        half3 illuminance;
        #ifdef UNITY_LIGHTMAP_FULL_HDR
            illuminance = encodedIlluminance.rgb;
        #else
            illuminance = DecodeLightmap(encodedIlluminance, half4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0.0h, 0.0h));
        #endif

        // Diffuse ambient (направленный)
        half3 diffuseAmbient = (dot(s.normalWS, L.xyz) + 0.5h) / max(1e-4, L.w) * s.albedo;

        // Specular ambient (BRDF по направлению из лайтмапы)
        half3 specularAmbient = MERCS_BRDF(s, b, SafeNormalizeHalf(L.xyz));
        half specularAmbientMask = saturate(1.0h - (s.smoothness - 0.65h) / 0.25h);
        specularAmbient *= specularAmbientMask;

        ambientLight = (specularAmbient + diffuseAmbient) * illuminance;

        // Reflection cubemap
        reflection = GlossyEnvironmentReflection(b.reflectVector, b.perceptualRoughness, 1.0h) * fresnel;
        half reflectionMask = saturate(dot(b.reflectVector, L.xyz) + encodedIlluminance.w);
        reflection *= illuminance * (1.0h - reflectionMask) + reflectionMask;
    #else
        // SH probes + dominant direction specular
        shadowMask = unity_ProbesOcclusion;

        half3 illuminance = SampleSH(s.normalWS);
        half3 diffuseAmbient = s.albedo * illuminance;

        // Extract dominant direction from L1
        half3 L0 = half3(unity_SHAr.w, unity_SHAg.w, unity_SHAb.w);
        half3 L0_safe = max(L0, half3(1e-4h, 1e-4h, 1e-4h));

        half3x3 L1 = half3x3(
            unity_SHAr.x, unity_SHAg.x, unity_SHAb.x,
            unity_SHAr.y, unity_SHAg.y, unity_SHAb.y,
            unity_SHAr.z, unity_SHAg.z, unity_SHAb.z) * 2.0h;

        half3x3 nL1 = half3x3(L1[0] / L0_safe, L1[1] / L0_safe, L1[2] / L0_safe);
        half3 dominantDir = mul(nL1, half3(0.333h, 0.333h, 0.333h));

        half3 sh = L0 + mul(dominantDir, L1);
        sh *= saturate((dot(dominantDir, s.normalWS) + 0.1h) / 1.1h);

        half3 specularAmbient = MERCS_BRDF(s, b, dominantDir) * sh;

        ambientLight = specularAmbient + diffuseAmbient;

        reflection = GlossyEnvironmentReflection(b.reflectVector, b.perceptualRoughness, 1.0h) * fresnel;
    #endif

    return half4((ambientLight + reflection) * s.occlusion, shadowMask.r);
}

// =============================================================
// VEGETATION-специфичные функции (backlight translucency)
// =============================================================

// Main light для листвы: добавляет backlight через masks.r (translucent power)
half3 MERCS_MainLight_Vegetation(MERCS_SurfaceData s, MERCS_BRDFdata b, half shadowMaskW)
{
    float4 shadowCoord = TransformWorldToShadowCoord(s.positionWS);
    Light mainLight = GetMainLight(shadowCoord);

    half shadow = shadowMaskW * mainLight.shadowAttenuation;

    // Backlight: свет просвечивающий сквозь лист сзади
    half3 backLight = saturate(dot(-s.normalWS, mainLight.direction)) * s.masks.r * mainLight.color * s.albedo;

    half3 specularMainLight = MERCS_BRDF(s, b, mainLight.direction);
    half NdotL = saturate(dot(s.normalWS, mainLight.direction));

    return ((s.albedo + specularMainLight) * mainLight.color * NdotL + backLight) * shadow;
}

// Ambient для листвы: добавляет ambient translucent через SampleSH(-normal)
half4 MERCS_AmbientLight_Vegetation(MERCS_SurfaceData s, MERCS_BRDFdata b, float2 lightmapUV, half3 vertexSH)
{
    half3 fresnel = MERCS_Fresnel(s, b);
    half4 shadowMask = 1;
    half3 ambientLight = 0;
    half3 reflection = 0;

    #if defined(LIGHTMAP_ON)
        shadowMask = SAMPLE_SHADOWMASK(lightmapUV);

        half4 L = SAMPLE_TEXTURE2D(unity_LightmapInd, samplerunity_Lightmap, lightmapUV);
        L.xyz = L.xyz - 0.5h;

        half4 encodedIlluminance = SAMPLE_TEXTURE2D(unity_Lightmap, samplerunity_Lightmap, lightmapUV);
        half3 illuminance;
        #ifdef UNITY_LIGHTMAP_FULL_HDR
            illuminance = encodedIlluminance.rgb;
        #else
            illuminance = DecodeLightmap(encodedIlluminance, half4(LIGHTMAP_HDR_MULTIPLIER, LIGHTMAP_HDR_EXPONENT, 0.0h, 0.0h));
        #endif

        half3 diffuseAmbient = (dot(s.normalWS, L.xyz) + 0.5h) / max(1e-4, L.w) * s.albedo;
        diffuseAmbient += s.masks.r * s.albedo;  // translucent ambient

        // Specular ambient (с учётом facing через masks.y: +1 front, -1 back)
        half3 specularAmbient = MERCS_BRDF(s, b, SafeNormalizeHalf(L.xyz) * s.masks.y);
        half specularAmbientMask = saturate(1.0h - (s.smoothness - 0.65h) / 0.25h);
        specularAmbient *= specularAmbientMask;

        ambientLight = (specularAmbient + diffuseAmbient) * illuminance;

        reflection = GlossyEnvironmentReflection(b.reflectVector, b.perceptualRoughness, 1.0h) * fresnel;
        half reflectionMask = saturate(dot(b.reflectVector, L.xyz) + encodedIlluminance.w);
        reflection *= illuminance * (1.0h - reflectionMask) + reflectionMask;
    #else
        shadowMask = unity_ProbesOcclusion;

        // Двухсторонний SH: прямой + обратный с masks.r (translucent)
        half3 illuminance = SampleSH(s.normalWS) * s.occlusion;
        illuminance += SampleSH(-s.normalWS) * s.masks.r * s.albedo;

        half3 diffuseAmbient = s.albedo;
        ambientLight = diffuseAmbient * illuminance;

        reflection = GlossyEnvironmentReflection(b.reflectVector, b.perceptualRoughness, 1.0h) * fresnel;
    #endif

    return half4((ambientLight + reflection) * s.occlusion, shadowMask.r);
}

// Полный цикл для vegetation (Foliage)
half3 AxleboltLighting_Vegetation(inout MERCS_SurfaceData surfaceData, float2 lightmapUV, half3 vertexSH)
{
    MERCS_BRDFdata brdfData;
    InitializeBRDFdata(surfaceData, brdfData);

    MERCS_MetallicToSpecularConvert(surfaceData);

    half4 ambient = MERCS_AmbientLight_Vegetation(surfaceData, brdfData, lightmapUV, vertexSH);
    half3 mainLight = MERCS_MainLight_Vegetation(surfaceData, brdfData, ambient.a);
    half3 addLights = MERCS_AdditionalLights_half(surfaceData);

    return ambient.rgb + mainLight + addLights;
}

// ------------- Главный вход: полный цикл лайтинга -------------
// Вызываем после заполнения всех полей ML_SurfaceData (кроме specular).
// Возвращает финальный цвет без emission и fog.

half3 AxleboltLighting(inout MERCS_SurfaceData surfaceData, float2 lightmapUV, half3 vertexSH)
{
    MERCS_BRDFdata brdfData;
    InitializeBRDFdata(surfaceData, brdfData);

    // КРИТИЧНО: конвертация metallic→specular модифицирует albedo (diffuse)
    MERCS_MetallicToSpecularConvert(surfaceData);

    half4 ambient = MERCS_AmbientLight(surfaceData, brdfData, lightmapUV, vertexSH);
    half3 mainLight = MERCS_MainLight(surfaceData, brdfData, ambient.a);
    half3 addLights = MERCS_AdditionalLights_half(surfaceData);

    return ambient.rgb + mainLight + addLights;
}

// Макрос для передачи lightmap/SH — подбирает правильные аргументы
#if defined(LIGHTMAP_ON)
    #define AXLEBOLT_GI_ARGS(IN) IN.lightmapUV, half3(0, 0, 0)
#else
    #define AXLEBOLT_GI_ARGS(IN) float2(0, 0), IN.vertexSH
#endif

#endif // AXLEBOLT_LIGHTING_INCLUDED
