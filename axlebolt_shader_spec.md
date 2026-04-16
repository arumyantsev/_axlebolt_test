# Axlebolt Test — Technical Specification for Claude Code

> **Проект:** Тестовое задание на позицию Senior 3D Environment Artist в Axlebolt (Standoff 2).
> **Сцена:** Ретранслятор / точка связи на холме. Near-future mobile FPS, stylized realism (ref: Apex Legends).
> **Движок:** Unity 2022+, URP, Android target, OpenGLES3, Linear color space.
> **Правила:** Каждый шейдер — самостоятельный .shader файл. Только стандартные URP includes. Никаких кастомных инклюдов между шейдерами.

---

## 1. Структура проекта Unity

```
Assets/
  _Project/
    Shaders/
      MainLit.shader              // Texture2DArray, vertex color, opaque + alpha clip variant
      Terrain.shader              // SplatMap + height blend
      Foliage.shader              // Alpha cutout, wind, двусторонний
      SimpleLit.shader            // Простой Lit: ствол дерева, пульт, мелкие пропсы
      Decal.shader                // Unlit, alpha blend / multiply
      EmissivePulse.shader        // Пульсация, Meta pass
    Materials/
      Environment/                // Материалы Main Lit (один основной + alpha clip)
      Terrain/                    // Материал террейна
      Foliage/                    // Материалы растительности
      Decals/                     // Материал декалей
      Emissive/                   // Материалы свечения
      SimpleLit/                  // Ствол, пульт, мелкие пропсы
    Models/
      Environment/
        Building/                 // Здание, лестница, балкон
        Tower/                    // Вышка-ретранслятор
        Fence/                    // Забор, ворота
        Props/                    // Генератор, контейнеры
      Terrain/                    // Меш террейна из Houdini
      Vegetation/                 // Деревья, кусты, трава
      Mannequin/                  // Манекен 1.80м
    Textures/
      Arrays/
        Buildings/                // Текстуры для Building Texture2DArray (слоты 0-7+)
          0_metal_panels_albedo.png
          0_metal_panels_normal.png
          1_concrete_albedo.png
          1_concrete_normal.png
          ...
        Terrain/                  // Текстуры для Terrain Texture2DArray (слоты 0-3)
          0_dirt_albedo.png
          0_dirt_normal.png
          1_grass_albedo.png
          1_grass_normal.png
          2_rock_albedo.png
          2_rock_normal.png
          3_reserve_albedo.png
          3_reserve_normal.png
      SplatMap/                   // SplatMap террейна из Houdini
      Decals/                     // Атлас декалей 512×512
      Unique/                     // Пульт управления 1024×1024
      PropsAtlas/                 // Атлас мелких пропсов 1024×1024
    Scenes/
      MainScene.unity
    Prefabs/
      Environment/
      Props/
      Vegetation/
    Lighting/                     // LightingData, reflection probes, light probes
  _ThirdParty/
    FPSController/                // Готовый ассет
    SpeedTree/                    // Растительность из маркетплейса
    MarketplaceAssets/            // Камни, манекен и прочее
```

---

## 2. Шейдер 1: MainLit.shader (ПРИОРИТЕТ 1)

### 2.1. Назначение

Универсальный шейдер для всего opaque хард-серфейса сцены: здание, контейнеры, генератор, вышка, забор стойки, лестница, перила, тримы, серверная стойка. Один шейдер → один материал → один draw call (static batch).

### 2.2. Входные данные

**Texture2DArray — 2 массива (512×512, 8-10 слайсов):**

| Массив | R | G | B | A |
|--------|---|---|---|---|
| `_AlbedoArray` | Albedo R | Albedo G | Albedo B | Ambient Occlusion |
| `_NormalArray` | Normal X | Normal Y | Roughness | Metallic |

**Vertex Color:**

| Канал | Содержимое | Описание |
|-------|-----------|----------|
| R | Арт-тинт | Свободная покраска кистью (тёплый сдвиг, грязь) |
| G | Арт-тинт | Свободная покраска (холодный сдвиг, мох) |
| B | Арт-тинт | Свободная покраска (градиенты, вариации) |
| A | Slice Index | Индекс слайса в Texture2DArray. Значения в 0-255: 0=слот 0, 17=слот 1, 34=слот 2, 51=слот 3, 68=слот 4, 85=слот 5, 102=слот 6, 119=слот 7, 136=слот 8, 153=слот 9, 170=слот 10, 187=слот 11, 204=слот 12, 221=слот 13, 238=слот 14, 255=слот 15 |

### 2.3. Properties

```hlsl
Properties
{
    _AlbedoArray ("Albedo Array", 2DArray) = "" {}
    _NormalArray ("Normal Array", 2DArray) = "" {}
    _NormalStrength ("Normal Strength", Range(0, 2)) = 1.0
    _TintStrength ("Vertex Color Tint Strength", Range(0, 1)) = 0.5
    _AlphaClip ("Alpha Clip Threshold", Range(0, 1)) = 0.5
    // Keyword: _ALPHATEST_ON для рабицы/решёток
}
```

### 2.4. Логика фрагмента

```hlsl
// 1. Slice index из vertex color alpha
int idx = (int)round(IN.color.a * 15.0);

// 2. Sample texture arrays
half4 albedoAO  = SAMPLE_TEXTURE2D_ARRAY(_AlbedoArray, sampler_AlbedoArray, IN.uv, idx);
half4 normalRMA = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, IN.uv, idx);

// 3. Unpack
half3 albedo    = albedoAO.rgb;
half  ao        = albedoAO.a;
half3 normalTS  = half3(normalRMA.rg * 2.0 - 1.0, 0);
normalTS.z      = sqrt(1.0 - saturate(dot(normalTS.xy, normalTS.xy)));
normalTS.xy    *= _NormalStrength;
half  roughness = normalRMA.b;
half  metallic  = normalRMA.a;
half  smoothness = 1.0 - roughness;

// 4. Alpha clip (keyword _ALPHATEST_ON)
#ifdef _ALPHATEST_ON
    clip(albedoAO.a - _AlphaClip);
    ao = 1.0; // В alpha clip режиме A = cutout маска, AO отключён
#endif

// 5. Арт-тинт из RGB vertex color
albedo *= lerp(half3(1,1,1), IN.color.rgb * 2.0, _TintStrength);
// *2.0 потому что vertex color 0.5 = нейтральный (без изменений), <0.5 затемняет, >0.5 осветляет

// 6. Трансформация нормали в world space
half3 normalWS = TransformTangentToWorld(normalTS, half3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS));
normalWS = normalize(normalWS);

// 7. Lighting (см. секцию 7 — общие фичи)
```

### 2.5. Passes

| Pass | Назначение | Особенности |
|------|-----------|-------------|
| ForwardLit | Main Light + Shadows + Lightmaps + SH + Reflection Probes + Fog | Tags: "LightMode" = "UniversalForward" |
| ShadowCaster | Отбрасывание теней | Для alpha clip: + clip() |
| Meta | Лайтмап бейк | Передаёт albedo и emission |
| DepthOnly | Depth prepass | Tags: "LightMode" = "DepthOnly" |

### 2.6. Слоты массива

| Слот | Alpha (0-255) | Alpha (0-1) | Текстура |
|------|--------------|-------------|----------|
| 0 | 0 | 0.000 | Металл панели (стены, генератор, мачта, стойки, перила) |
| 1 | 17 | 0.067 | Бетон (площадка, фундамент, ступени) |
| 2 | 34 | 0.133 | Гофрированный металл (контейнеры) |
| 3 | 51 | 0.200 | Крыша (кровля здания) |
| 4 | 68 | 0.267 | Резиновый пол (индор) |
| 5 | 85 | 0.333 | Трим-шит (рёбра, болты, решётки, уплотнители) |
| 6 | 102 | 0.400 | Металл тёмный (вариация) |
| 7 | 119 | 0.467 | Металл ржавый (вариация) |
| 8 | 136 | 0.533 | Резерв |
| 9 | 153 | 0.600 | Резерв |
| 10 | 170 | 0.667 | Резерв |
| 11 | 187 | 0.733 | Резерв |
| 12 | 204 | 0.800 | Резерв |
| 13 | 221 | 0.867 | Резерв |
| 14 | 238 | 0.933 | Резерв |
| 15 | 255 | 1.000 | Резерв |

### 2.7. Важные нюансы

- Split vertices ОБЯЗАТЕЛЕН на границах полигонов с разными слайсами. Иначе GPU интерполирует alpha между соседними полигонами и round() даст неправильный индекс.
- Alpha clip вариант (рабица, решётки): отдельный меш, тот же шейдер + keyword `_ALPHATEST_ON`. В alpha clip режиме `_AlbedoArray.A` = cutout маска (не AO), AO = 1.0.
- Vertex color RGB нейтральное значение = (0.5, 0.5, 0.5). Умножение `color.rgb * 2.0` даёт 1.0 при нейтрале, <1.0 при затемнении, >1.0 при осветлении.

---

## 3. Шейдер 2: Terrain.shader (ПРИОРИТЕТ 1)

### 3.1. Назначение

Шейдер для ландшафта (меш из Houdini). SplatMap RGBA определяет маски 4 слоёв. Height-based blend из альфа-канала альбедо даёт реалистичные переходы (трава не лезет поверх камней, грунт заполняет впадины).

### 3.2. Входные данные

**Texture2DArray — 2 массива (512×512, 4 слоя), ОТДЕЛЬНЫЕ от зданий:**

| Массив | R | G | B | A |
|--------|---|---|---|---|
| `_TerrainAlbedoArray` | Albedo R | Albedo G | Albedo B | Height Map |
| `_TerrainNormalArray` | Normal X | Normal Y | Roughness | Ambient Occlusion |

**SplatMap (отдельная текстура 1024×1024):**

| Канал | Слой |
|-------|------|
| R | Грунт / дорога (слайс 0) |
| G | Трава (слайс 1) |
| B | Камень (слайс 2) |
| A | Резерв — песок/гравий (слайс 3) |

**Vertex Color меша террейна:**

| Канал | Содержимое |
|-------|-----------|
| R | Low-frequency noise для вариации тона. Запекается в Houdini (Perlin noise, большой масштаб). В шейдере: `albedo *= lerp(1.0 - _NoiseStrength, 1.0 + _NoiseStrength, IN.color.r)` |
| G | Резерв (второй noise или slope mask) |
| B | Резерв |
| A | Не используется |

**Metallic = 0 всегда (диэлектрик).**

### 3.3. Properties

```hlsl
Properties
{
    _TerrainAlbedoArray ("Terrain Albedo Array", 2DArray) = "" {}
    _TerrainNormalArray ("Terrain Normal Array", 2DArray) = "" {}
    _SplatMap ("Splat Map", 2D) = "white" {}
    _Tiling ("World UV Tiling", Float) = 0.2
    _BlendSharpness ("Height Blend Sharpness", Range(0.01, 0.5)) = 0.15
    _NormalStrength ("Normal Strength", Range(0, 2)) = 1.0
    _NoiseStrength ("Vertex Color Noise Strength", Range(0, 0.3)) = 0.1
}
```

### 3.4. Логика фрагмента — Height-Based Blend

```hlsl
// 1. SplatMap по UV0 меша (0-1 по всему террейну)
half4 splat = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, IN.uv0);

// 2. World-space tiling UV (не нужна UV-развёртка террейна)
float2 tUV = IN.positionWS.xz * _Tiling;

// 3. Sample все 4 слоя albedo
half4 a0 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV, 0);
half4 a1 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV, 1);
half4 a2 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV, 2);
half4 a3 = SAMPLE_TEXTURE2D_ARRAY(_TerrainAlbedoArray, sampler_TerrainAlbedoArray, tUV, 3);

// 4. Height-based blend: height из A + splat вес
half h0 = a0.a + splat.r;
half h1 = a1.a + splat.g;
half h2 = a2.a + splat.b;
half h3 = a3.a + splat.a;

half maxH = max(max(h0, h1), max(h2, h3)) - _BlendSharpness;
half w0 = max(h0 - maxH, 0);
half w1 = max(h1 - maxH, 0);
half w2 = max(h2 - maxH, 0);
half w3 = max(h3 - maxH, 0);
half wSum = w0 + w1 + w2 + w3 + 0.001;

// 5. Blend albedo
half3 albedo = (a0.rgb * w0 + a1.rgb * w1 + a2.rgb * w2 + a3.rgb * w3) / wSum;

// 6. Sample и blend нормали тем же способом
half4 n0 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV, 0);
half4 n1 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV, 1);
half4 n2 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV, 2);
half4 n3 = SAMPLE_TEXTURE2D_ARRAY(_TerrainNormalArray, sampler_TerrainNormalArray, tUV, 3);
half4 nBlend = (n0 * w0 + n1 * w1 + n2 * w2 + n3 * w3) / wSum;

// 7. Unpack
half3 normalTS = half3(nBlend.rg * 2.0 - 1.0, 0);
normalTS.z = sqrt(1.0 - saturate(dot(normalTS.xy, normalTS.xy)));
normalTS.xy *= _NormalStrength;
half roughness = nBlend.b;
half ao = nBlend.a;
half metallic = 0; // ВСЕГДА 0 — земля диэлектрик

// 8. Vertex color noise для вариации тона
albedo *= lerp(1.0 - _NoiseStrength, 1.0 + _NoiseStrength, IN.color.r);
```

### 3.5. Texture Samples

| Ситуация | Samples |
|----------|---------|
| Всегда (наивный) | 1 splatmap + 4 albedo + 4 normal = 9 |
| С branch по splat weight | ~5 в большинстве пикселей |

### 3.6. Passes

| Pass | Назначение | Особенности |
|------|-----------|-------------|
| ForwardLit | Main Light + Shadows + Lightmaps + SH + Reflection Probes + Fog | |
| ShadowCaster | Тени от рельефа | |
| Meta | Для бейка | Blend albedo |

### 3.7. UV для SplatMap

```hlsl
// SplatMap сэмплится по UV0 меша террейна (0-1 по всему квадрату, уже есть из Houdini)
half4 splat = SAMPLE_TEXTURE2D(_SplatMap, sampler_SplatMap, IN.uv0);

// Тайловые текстуры слоёв — по world-space position для тайлинга
float2 tilingUV = IN.positionWS.xz * _Tiling;

// Lightmap — по UV1 (как и на всех остальных Lit шейдерах)
OUT.lightmapUV = IN.uv1.xy * unity_LightmapST.xy + unity_LightmapST.zw;
```

---

## 4. Шейдер 3: Foliage.shader (ПРИОРИТЕТ 2)

### 4.1. Назначение

Деревья (крона), кусты, трава. Alpha cutout, двусторонний рендеринг, двухуровневый ветровой эффект (sway + flutter).

**ВАЖНО:** Трава — НЕ СТАТИК. Трава на Light Probes + SH, без лайтмапа. Деревья и кусты — статик, бейкаются.
**ВАЖНО:** Ствол дерева — НЕ этот шейдер. Ствол на SimpleLit (opaque, простой Lit без массивов). Крона — Foliage (alpha clip). Два суб-меша, два материала.

### 4.2. Входные данные

**Текстуры (обычные, не массив):**

| Текстура | R | G | B | A |
|----------|---|---|---|---|
| `_Albedo` | Albedo R | Albedo G | Albedo B | Alpha (cutout маска) |

**Vertex Color:**

| Канал | Содержимое | Описание |
|-------|-----------|----------|
| R | Color Variation | Сдвиг оттенка листвы. 0=базовый цвет, 1=максимальный сдвиг к `_VariationColor`. Рандом per-leaf |
| G | Sway Gradient | 0 у корня/основания, 1 на верхушке. Контролирует амплитуду основного качания всего растения |
| B | Phase | Рандом per-leaf/per-blade (0-1). Сдвиг фазы чтобы листья качались не синхронно |
| A | AO | Затемнение у основания куста / внутри кроны |

### 4.3. Properties

```hlsl
Properties
{
    _Albedo ("Albedo (A=Alpha)", 2D) = "white" {}
    _AlphaClip ("Alpha Clip", Range(0, 1)) = 0.5
    _VariationColor ("Variation Color", Color) = (0.8, 0.7, 0.3, 1) // осенний/сухой оттенок
    _VariationStrength ("Variation Strength", Range(0, 1)) = 0.3
    _SwaySpeed ("Sway Speed", Range(0, 5)) = 1.5
    _SwayStrength ("Sway Strength", Range(0, 0.5)) = 0.08
    _FlutterSpeed ("Flutter Speed", Range(0, 20)) = 8.0
    _FlutterStrength ("Flutter Strength", Range(0, 0.1)) = 0.02
    _AOStrength ("AO Strength", Range(0, 1)) = 0.5
}
```

### 4.4. Vertex Shader — Wind

```hlsl
// Два масштаба ветра
float swayGradient = IN.color.g;   // 0 корень → 1 верхушка
float phase = IN.color.b * 6.28;   // 0-1 → 0-2π

// Sway: медленное качание всего растения
float sway = sin(_Time.y * _SwaySpeed + positionWS.x * 0.5) * _SwayStrength * swayGradient;

// Flutter: быстрое трепетание отдельных листьев
float flutter = sin(_Time.y * _FlutterSpeed + phase) * _FlutterStrength * swayGradient;

positionWS.xz += sway + flutter;
```

### 4.5. Fragment Shader

```hlsl
half4 tex = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, IN.uv);

// Alpha clip
clip(tex.a - _AlphaClip);

// Color variation из vertex color R
half3 albedo = lerp(tex.rgb, tex.rgb * _VariationColor.rgb, IN.color.r * _VariationStrength);

// AO из vertex color A
half ao = lerp(1.0, IN.color.a, _AOStrength);

// Flip normal для backface
half3 normalWS = IN.normalWS;
normalWS *= IS_FRONT_VFACE(IN.facing, 1.0, -1.0);

// Lighting: Lambert only, без specular
// Трава: SampleSH(normalWS) ВСЕГДА, без lightmap
// Деревья/кусты: #ifdef LIGHTMAP_ON → SampleLightmap, else SampleSH
```

### 4.6. Passes

| Pass | Особенности |
|------|-------------|
| ForwardLit | Cull Off, alpha clip, simplified Lambert |
| ShadowCaster | Cull Off, alpha clip по форме листьев |
| Meta | Для бейка (только деревья/кусты) |

### 4.7. Ствол дерева

Ствол дерева НЕ на этом шейдере. Ствол — opaque геометрия на SimpleLit.shader. Причина: alpha clip шейдер ломает Early-Z на мобильном тайловом GPU, overdraw растёт. Opaque ствол обрабатывается корректно.

Дерево = 2 суб-меша: ствол (SimpleLit, opaque) + крона (Foliage, alpha clip).

---

## 5. Шейдер 4: SimpleLit.shader (ПРИОРИТЕТ 2)

### 5.1. Назначение

Простой Lit шейдер без Texture2DArray. Для объектов с уникальными/отдельными текстурами: ствол дерева, пульт управления, атлас мелких пропсов. Одна Albedo + одна Normal, та же раскладка каналов что и у MainLit.

### 5.2. Входные данные

**Текстуры (обычные, не массив):**

| Текстура | R | G | B | A |
|----------|---|---|---|---|
| `_Albedo` | Albedo R | Albedo G | Albedo B | Ambient Occlusion |
| `_Normal` | Normal X | Normal Y | Roughness | Metallic |

### 5.3. Properties

```hlsl
Properties
{
    _Albedo ("Albedo (A=AO)", 2D) = "white" {}
    _Normal ("Normal (RG=Normal, B=Roughness, A=Metallic)", 2D) = "bump" {}
    _NormalStrength ("Normal Strength", Range(0, 2)) = 1.0
}
```

### 5.4. Логика фрагмента

```hlsl
half4 albedoAO  = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, IN.uv);
half4 normalRMA = SAMPLE_TEXTURE2D(_Normal, sampler_Normal, IN.uv);

half3 albedo    = albedoAO.rgb;
half  ao        = albedoAO.a;
half3 normalTS  = half3(normalRMA.rg * 2.0 - 1.0, 0);
normalTS.z      = sqrt(1.0 - saturate(dot(normalTS.xy, normalTS.xy)));
normalTS.xy    *= _NormalStrength;
half  roughness = normalRMA.b;
half  metallic  = normalRMA.a;
half  smoothness = 1.0 - roughness;

// Без vertex color, без массивов — просто чистый Lit
```

### 5.5. Passes

| Pass | Назначение |
|------|-----------|
| ForwardLit | Main Light + Shadows + Lightmaps + SH + Reflection Probes + Fog |
| ShadowCaster | Отбрасывание теней |
| Meta | Лайтмап бейк |
| DepthOnly | Depth prepass |

### 5.6. Объекты на этом шейдере

| Объект | Текстура | Размер |
|--------|---------|--------|
| Ствол дерева | Уникальная (тайл коры) | 512×512 |
| Пульт управления | Уникальная | 1024×1024 |
| Атлас мелких пропсов | Атлас (ящики, бочки, стул...) | 1024×1024 |

---

## 6. Шейдер 5: Decal.shader (ПРИОРИТЕТ 3)

### 6.1. Назначение

Квады поверх геометрии. Один атлас 512×512 с альфой. Hazard-полосы, надписи, teal-индикаторы, грязь/потёки/ржавчина, трафаретные номера.

### 6.2. Текстура

| R | G | B | A |
|---|---|---|---|
| Color R | Color G | Color B | Alpha маска |

### 6.3. Properties

```hlsl
Properties
{
    _DecalAtlas ("Decal Atlas", 2D) = "white" {}
    _TintColor ("Tint", Color) = (1,1,1,1)
    _AlphaMultiplier ("Alpha Strength", Range(0, 1)) = 1.0
    [KeywordEnum(Alpha, Multiply)] _BlendMode ("Blend Mode", Float) = 0
}
```

### 6.4. Два режима блендинга

**Alpha Blend** (надписи, hazard, teal):
```
Blend SrcAlpha OneMinusSrcAlpha
```

**Multiply** (грязь, потёки, ржавчина):
```
Blend DstColor Zero
```

### 6.5. Особенности

- **Unlit** — не считает собственное освещение, получает цвет визуально от поверхности под собой
- `ZWrite Off`
- `Offset -1, -1` — polygon offset против z-fighting
- `Queue = Transparent`
- Не отбрасывает тени, без ShadowCaster pass

---

## 7. Шейдер 6: EmissivePulse.shader (ПРИОРИТЕТ 5 — бонус)

### 7.1. Назначение

Кольцо вышки-ретранслятора, экраны пульта, LED-индикаторы серверной стойки.

### 7.2. Properties

```hlsl
Properties
{
    _BaseColor ("Base Color", Color) = (0, 0, 0, 1)
    _EmissionColor ("Emission Color", Color) = (0.1, 0.8, 0.6, 1) // teal
    _EmissionIntensity ("Intensity", Range(0, 10)) = 3.0
    _PulseSpeed ("Pulse Speed", Range(0, 5)) = 1.2
    _PulseMin ("Min Brightness", Range(0, 1)) = 0.3
}
```

### 7.3. Логика

```hlsl
half pulse = lerp(_PulseMin, 1.0, sin(_Time.y * _PulseSpeed) * 0.5 + 0.5);
half3 emission = _EmissionColor.rgb * _EmissionIntensity * pulse;
return half4(_BaseColor.rgb + emission, 1.0);
```

### 7.4. Meta Pass

```hlsl
// Meta pass для лайтмап бейка
// Лайтмаппер подхватит emission → teal свет от кольца на бетон, свет экранов на стены
MetaInput metaInput;
metaInput.Albedo = _BaseColor.rgb;
metaInput.Emission = _EmissionColor.rgb * _EmissionIntensity * 0.7; // средняя яркость без пульсации
return MetaFragment(metaInput);
```

---

## 8. Общие фичи всех Lit шейдеров (1, 2, 3, 4, 5)

Каждый Lit шейдер (MainLit, Terrain, Foliage, SimpleLit) должен поддерживать следующее:

### 8.1. URP Includes

```hlsl
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"
```

### 8.2. Baked GI (Lightmaps)

```hlsl
// Vertex shader:
OUT.lightmapUV = IN.uv1.xy * unity_LightmapST.xy + unity_LightmapST.zw;

// Fragment shader:
#ifdef LIGHTMAP_ON
    half3 bakedGI = SampleLightmap(IN.lightmapUV, normalWS);
#else
    half3 bakedGI = SampleSH(normalWS);
#endif
```

**Исключение:** Трава всегда через SampleSH, никогда через lightmap.

### 8.3. Reflection Probes (все Lit шейдеры: MainLit, SimpleLit, Terrain, Foliage)

```hlsl
half3 reflectDir = reflect(-viewDirWS, normalWS);
half perceptualRoughness = 1.0 - smoothness;
half3 reflection = GlossyEnvironmentReflection(reflectDir, perceptualRoughness, 1.0);
finalColor += reflection * smoothness * ao;
```

Reflection Probes на всех Lit шейдерах. Видимость рефлексов контролируется через smoothness в материале — при roughness=1 (smoothness=0) рефлексы не видны, но пробы всё равно дают корректный ambient.

### 8.4. Fog

```hlsl
// Vertex:
OUT.fogFactor = ComputeFogFactor(OUT.positionCS.z);

// Fragment (последняя строка перед return):
color.rgb = MixFog(color.rgb, IN.fogFactor);
```

### 8.5. Main Light + Shadows

```hlsl
float4 shadowCoord = TransformWorldToShadowCoord(positionWS);
Light mainLight = GetMainLight(shadowCoord);
half NdotL = saturate(dot(normalWS, mainLight.direction));
half3 directLighting = mainLight.color * mainLight.distanceAttenuation
    * mainLight.shadowAttenuation * NdotL;
```

### 8.6. ShadowCaster Pass

```hlsl
// Минимальный pass: только трансформация позиции + shadow bias
// Для Alpha Clip шейдеров: + clip(alpha - _Cutoff)
// Tags { "LightMode" = "ShadowCaster" }
```

### 8.7. Meta Pass

```hlsl
// Tags { "LightMode" = "Meta" }
MetaInput metaInput;
metaInput.Albedo = albedo;
metaInput.Emission = emission; // float3(0,0,0) если нет emission
return MetaFragment(metaInput);
```

### 8.8. Normal Z реконструкция (все шейдеры)

```hlsl
half3 normalTS = half3(normalMap.rg * 2.0 - 1.0, 0);
normalTS.z = sqrt(1.0 - saturate(dot(normalTS.xy, normalTS.xy)));
```

---

## 9. Каналы текстур — полная сводная

### Buildings Array

| Массив | R | G | B | A |
|--------|---|---|---|---|
| _AlbedoArray | Albedo | Albedo | Albedo | AO |
| _NormalArray | Normal X | Normal Y | Roughness | Metallic |

### Terrain Array

| Массив | R | G | B | A |
|--------|---|---|---|---|
| _TerrainAlbedoArray | Albedo | Albedo | Albedo | Height (для blend) |
| _TerrainNormalArray | Normal X | Normal Y | Roughness | AO |

### SplatMap

| R | G | B | A |
|---|---|---|---|
| Грунт/дорога | Трава | Камень | Резерв |

### Decal Atlas

| R | G | B | A |
|---|---|---|---|
| Color | Color | Color | Alpha маска |

### Foliage

| Текстура | R | G | B | A |
|----------|---|---|---|---|
| _Albedo | Albedo | Albedo | Albedo | Alpha cutout |

### SimpleLit

| Текстура | R | G | B | A |
|----------|---|---|---|---|
| _Albedo | Albedo | Albedo | Albedo | AO |
| _Normal | Normal X | Normal Y | Roughness | Metallic |

---

## 10. Vertex Color — сводная по шейдерам

### MainLit

| R | G | B | A |
|---|---|---|---|
| Арт-тинт (свободно) | Арт-тинт (свободно) | Арт-тинт (свободно) | Slice Index (0, 17, 34, 51, 68, 85, 102, 119, 136, 153, 170, 187, 204, 221, 238, 255) |

Нейтральное значение RGB = (0.5, 0.5, 0.5) = без изменений.

### Terrain

| R | G | B | A |
|---|---|---|---|
| Noise вариация тона | Резерв | Резерв | Не используется |

### Foliage

| R | G | B | A |
|---|---|---|---|
| Color Variation (рандом per-leaf) | Sway Gradient (0 корень → 1 верх) | Phase (рандом per-leaf, 0-1) | AO (затемнение у основания) |

---

## 11. Draw Calls — итого

| # | Шейдер | Объекты |
|---|--------|---------|
| 1 | MainLit | Все opaque хард-серфейс (static batch) |
| 2 | MainLit + _ALPHATEST_ON | Рабица, решётки (отдельный меш) |
| 3 | Terrain | Ландшафт |
| 4 | SimpleLit | Ствол дерева |
| 5 | SimpleLit | Пульт управления (уникальный) |
| 6 | SimpleLit | Мелкие пропсы (ящики, бочки, стул) |
| 7 | Foliage | Кроны, кусты, трава |
| 8 | Decal | Все декали |
| 9 | EmissivePulse | Кольцо вышки, экраны |
| **9** | **Итого** | **(без теней и маркетплейса)** |

---

## 12. HDA тулза для Houdini — Slice Index Assigner

### 12.1. Назначение

Кастомная HDA нода для назначения slice index через vertex color Alpha на выбранные группы примитивов. С визуальным превью текстур.

### 12.2. Интерфейс (параметры HDA)

| Параметр | Тип | Описание |
|----------|-----|----------|
| Primitive Group | String (menu из входа) | Выпадающий список всех групп примитивов на входном меше. Автоматически подтягивается |
| Slice Index | Integer (0-15) | Номер слайса в массиве |
| Texture Folder | String (folder path) | Путь к папке с текстурами. Тулза сканирует, находит текстуры по индексу в имени файла, назначает материалы |

### 12.3. Внутренняя логика

1. **Получает** выбранную группу примитивов из входного меша
2. **Сплитит вершины** на границах выбранной группы (Facet SOP → Unique Points на группе, или эквивалент через Wrangle)
3. **Назначает** `f@Alpha` на vertex level для всех вершин выбранных примитивов: `@Alpha = sliceIndex / 15.0`
4. **Сканирует папку** текстур, ищет файлы с паттерном `{index}_*` или `*_slice_{index}.*` в имени
5. **Создаёт материал** в Houdini и назначает на выбранную группу примитивов для визуального превью

### 12.4. Формат имён текстур

```
0_metal_panels_albedo.png
0_metal_panels_normal.png
1_concrete_albedo.png
1_concrete_normal.png
2_corrugated_albedo.png
...
```

Паттерн: `{sliceIndex}_{name}_{type}.{ext}` — тулза парсит первое число до подчёркивания как slice index.

### 12.5. Выходной атрибут

- `f@Alpha` (vertex level) — значение `sliceIndex / 15.0` для слотов 0-15
- `s@shop_materialpath` (primitive level) — путь к превью-материалу для viewport
