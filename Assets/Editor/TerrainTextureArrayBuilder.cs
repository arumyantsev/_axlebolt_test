using UnityEditor;
using UnityEngine;
using System.IO;

/// <summary>
/// EditorWindow для создания Texture2DArray из набора текстур.
/// Поддерживает Color Array (sRGB) и Mask Array (Linear).
/// Режим "Build Both" — автоматически находит Mask текстуры по суффиксу.
/// Окно: Window > Terrain V2 > Texture Array Builder
/// </summary>
public class TerrainTextureArrayBuilder : EditorWindow
{
    private const int MAX_LAYERS = 16;

    private static readonly string[] layerNames = {
        "0", "1", "2",  "3",  "4",  "5",  "6",  "7",
        "8", "9", "10", "11", "12", "13", "14", "15"
    };

    private enum CompressionMode
    {
        Auto,
        PC_BC7,
        Android_ASTC6x6,
        Android_ASTC4x4,
        None_RGBA32
    }

    // Префикс для EditorPrefs ключей
    private const string PREFS_PREFIX = "TerrainV2Builder_";

    private Texture2D[] sourceTextures = new Texture2D[MAX_LAYERS];
    private Vector2 scrollPosition;
    private string colorSavePath = "Assets/_Shaders/ML/TERRAIN_V2/TerrainColorArray.asset";
    private string maskSavePath  = "Assets/_Shaders/ML/TERRAIN_V2/TerrainMaskArray.asset";
    private CompressionMode compressionMode = CompressionMode.Auto;

    // Суффиксы для авто-поиска пар
    private string colorSuffix = "_TerrainColor";
    private string maskSuffix  = "_TerrainMask";

    [MenuItem("Tools/cgrum/Texture Array Builder")]
    public static void ShowWindow()
    {
        var window = GetWindow<TerrainTextureArrayBuilder>("Terrain Array Builder");
        window.minSize = new Vector2(450, 750);
    }

    private void OnEnable()
    {
        LoadState();
    }

    private void OnDisable()
    {
        SaveState();
    }

    /// <summary>
    /// Сохраняет все настройки и назначенные текстуры в EditorPrefs.
    /// </summary>
    private void SaveState()
    {
        EditorPrefs.SetString(PREFS_PREFIX + "colorSavePath", colorSavePath);
        EditorPrefs.SetString(PREFS_PREFIX + "maskSavePath", maskSavePath);
        EditorPrefs.SetInt(PREFS_PREFIX + "compressionMode", (int)compressionMode);
        EditorPrefs.SetString(PREFS_PREFIX + "colorSuffix", colorSuffix);
        EditorPrefs.SetString(PREFS_PREFIX + "maskSuffix", maskSuffix);

        // Сохраняем GUID каждой текстуры (переживает реимпорт и перемещение файлов)
        for (int i = 0; i < MAX_LAYERS; i++)
        {
            string guid = "";
            if (sourceTextures[i] != null)
            {
                string path = AssetDatabase.GetAssetPath(sourceTextures[i]);
                guid = AssetDatabase.AssetPathToGUID(path);
            }
            EditorPrefs.SetString(PREFS_PREFIX + "layer_" + i, guid);
        }
    }

    /// <summary>
    /// Восстанавливает настройки и текстуры из EditorPrefs.
    /// </summary>
    private void LoadState()
    {
        colorSavePath = EditorPrefs.GetString(PREFS_PREFIX + "colorSavePath", colorSavePath);
        maskSavePath = EditorPrefs.GetString(PREFS_PREFIX + "maskSavePath", maskSavePath);
        compressionMode = (CompressionMode)EditorPrefs.GetInt(PREFS_PREFIX + "compressionMode", (int)compressionMode);
        colorSuffix = EditorPrefs.GetString(PREFS_PREFIX + "colorSuffix", colorSuffix);
        maskSuffix = EditorPrefs.GetString(PREFS_PREFIX + "maskSuffix", maskSuffix);

        for (int i = 0; i < MAX_LAYERS; i++)
        {
            string guid = EditorPrefs.GetString(PREFS_PREFIX + "layer_" + i, "");
            if (!string.IsNullOrEmpty(guid))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    sourceTextures[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Terrain Texture2DArray Builder", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Перетащите Color текстуры (_TerrainColor) в слоты.\n" +
            "Mask текстуры (_TerrainMask) подхватятся автоматически по имени.\n\n" +
            "Color Array (sRGB): RGB=Albedo, A=Height\n" +
            "Mask Array (Linear): RG=Normal, B=Roughness, A=AO",
            MessageType.Info);

        // Суффиксы
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Naming Convention", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        colorSuffix = EditorGUILayout.TextField("Color Suffix", colorSuffix);
        maskSuffix  = EditorGUILayout.TextField("Mask Suffix", maskSuffix);
        EditorGUILayout.EndHorizontal();

        // Компрессия
        EditorGUILayout.Space(5);
        compressionMode = (CompressionMode)EditorGUILayout.EnumPopup("Compression", compressionMode);

        TextureFormat resolvedFormat = ResolveTextureFormat(compressionMode);
        string platformName = GetCurrentPlatformName();
        EditorGUILayout.HelpBox(
            $"Платформа: {platformName}  |  Формат: {resolvedFormat}",
            MessageType.None);

        // Пути сохранения
        EditorGUILayout.Space(5);
        DrawSavePathField("Color Array Path", ref colorSavePath);
        DrawSavePathField("Mask Array Path",  ref maskSavePath);

        // Слоты текстур
        EditorGUILayout.Space(5);
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        for (int i = 0; i < MAX_LAYERS; i++)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(layerNames[i], GUILayout.Width(150));
            sourceTextures[i] = (Texture2D)EditorGUILayout.ObjectField(
                sourceTextures[i], typeof(Texture2D), false);

            // Индикатор: найдена ли парная Mask текстура
            if (sourceTextures[i] != null)
            {
                Texture2D mask = FindPairedTexture(sourceTextures[i]);
                if (mask != null)
                    EditorGUILayout.LabelField("\u2714", GUILayout.Width(20)); // галочка
                else
                    EditorGUILayout.LabelField("\u2716", GUILayout.Width(20)); // крестик
            }
            else
            {
                EditorGUILayout.LabelField("", GUILayout.Width(20));
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();

        // Валидация
        EditorGUILayout.Space();
        int filledCount = 0;
        int pairedCount = 0;
        int firstWidth = 0, firstHeight = 0;

        for (int i = 0; i < MAX_LAYERS; i++)
        {
            if (sourceTextures[i] == null) continue;
            filledCount++;

            if (FindPairedTexture(sourceTextures[i]) != null)
                pairedCount++;

            if (firstWidth == 0)
            {
                firstWidth = sourceTextures[i].width;
                firstHeight = sourceTextures[i].height;
            }
            else if (sourceTextures[i].width != firstWidth || sourceTextures[i].height != firstHeight)
            {
                EditorGUILayout.HelpBox(
                    $"Layer {i}: {sourceTextures[i].width}x{sourceTextures[i].height} " +
                    $"!= {firstWidth}x{firstHeight}. Будет resize.",
                    MessageType.Warning);
            }
        }

        EditorGUILayout.LabelField($"Слоёв: {filledCount}/{MAX_LAYERS}  |  " +
                                   $"Mask найдены: {pairedCount}/{filledCount}  " +
                                   $"(\u2714 = найден, \u2716 = не найден)");

        if (firstWidth != 0 && firstWidth != firstHeight)
            EditorGUILayout.HelpBox("Текстуры должны быть квадратными!", MessageType.Warning);

        // ========== Кнопки ==========
        EditorGUILayout.Space(10);
        GUI.enabled = filledCount > 0;

        // Главная кнопка — собрать оба массива
        var prevColor = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button($"Build Both Arrays ({resolvedFormat})", GUILayout.Height(45)))
        {
            BuildBothArrays(firstWidth, firstHeight);
        }
        GUI.backgroundColor = prevColor;

        EditorGUILayout.Space(3);

        // Отдельные кнопки
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Build Color Only", GUILayout.Height(30)))
        {
            BuildSingleArray(false, sourceTextures, colorSavePath, firstWidth, firstHeight);
        }
        if (GUILayout.Button("Build Mask Only", GUILayout.Height(30)))
        {
            Texture2D[] maskTextures = CollectMaskTextures();
            BuildSingleArray(true, maskTextures, maskSavePath, firstWidth, firstHeight);
        }
        EditorGUILayout.EndHorizontal();

        // Быстрые кнопки платформ
        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Быстрая сборка под платформу:", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Both PC (BC7)", GUILayout.Height(28)))
        {
            compressionMode = CompressionMode.PC_BC7;
            BuildBothArrays(firstWidth, firstHeight);
        }
        if (GUILayout.Button("Both Android (ASTC)", GUILayout.Height(28)))
        {
            compressionMode = CompressionMode.Android_ASTC6x6;
            BuildBothArrays(firstWidth, firstHeight);
        }
        if (GUILayout.Button("Both RGBA32", GUILayout.Height(28)))
        {
            compressionMode = CompressionMode.None_RGBA32;
            BuildBothArrays(firstWidth, firstHeight);
        }
        EditorGUILayout.EndHorizontal();

        GUI.enabled = true;

        EditorGUILayout.Space(3);
        if (GUILayout.Button("Clear All Slots", GUILayout.Height(25)))
        {
            for (int i = 0; i < MAX_LAYERS; i++)
                sourceTextures[i] = null;
        }
    }

    // ==================== Save Path с кнопкой "..." ====================

    private void DrawSavePathField(string label, ref string path)
    {
        EditorGUILayout.BeginHorizontal();
        path = EditorGUILayout.TextField(label, path);
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) dir = "Assets";
            string defaultName = Path.GetFileName(path);
            string selected = EditorUtility.SaveFilePanelInProject(
                "Сохранить Texture Array", defaultName, "asset", "Выберите путь для сохранения", dir);
            if (!string.IsNullOrEmpty(selected))
                path = selected;
        }
        EditorGUILayout.EndHorizontal();
    }

    // ==================== Авто-поиск парных текстур ====================

    /// <summary>
    /// По Color текстуре находит Mask текстуру в той же папке, заменяя суффикс.
    /// </summary>
    private Texture2D FindPairedTexture(Texture2D colorTexture)
    {
        if (colorTexture == null) return null;

        string colorPath = AssetDatabase.GetAssetPath(colorTexture);
        if (string.IsNullOrEmpty(colorPath)) return null;

        string fileName = Path.GetFileNameWithoutExtension(colorPath);
        string ext = Path.GetExtension(colorPath);
        string dir = Path.GetDirectoryName(colorPath);

        // Заменяем суффикс Color → Mask
        if (!fileName.Contains(colorSuffix)) return null;
        string maskFileName = fileName.Replace(colorSuffix, maskSuffix);
        string maskPath = Path.Combine(dir, maskFileName + ext);

        // Пробуем с тем же расширением
        Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
        if (mask != null) return mask;

        // Пробуем другие расширения (.tga, .png, .jpg, .exr)
        string[] extensions = { ".tga", ".png", ".jpg", ".exr", ".tif" };
        foreach (string e in extensions)
        {
            if (e == ext) continue;
            maskPath = Path.Combine(dir, maskFileName + e);
            mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
            if (mask != null) return mask;
        }

        return null;
    }

    /// <summary>
    /// Собирает массив Mask текстур из Color слотов через авто-поиск.
    /// </summary>
    private Texture2D[] CollectMaskTextures()
    {
        Texture2D[] masks = new Texture2D[MAX_LAYERS];
        for (int i = 0; i < MAX_LAYERS; i++)
        {
            if (sourceTextures[i] != null)
            {
                masks[i] = FindPairedTexture(sourceTextures[i]);
                if (masks[i] == null)
                    Debug.LogWarning($"[TerrainV2] Layer {i} ({layerNames[i]}): " +
                                     $"Mask текстура не найдена для {sourceTextures[i].name}");
            }
        }
        return masks;
    }

    // ==================== Сборка массивов ====================

    private void BuildBothArrays(int width, int height)
    {
        if (width == 0 || height == 0)
        {
            Debug.LogError("[TerrainV2] Нет текстур для сборки!");
            return;
        }

        Texture2D[] maskTextures = CollectMaskTextures();

        // Собираем Color Array (sRGB)
        BuildSingleArray(false, sourceTextures, colorSavePath, width, height);

        // Собираем Mask Array (Linear)
        BuildSingleArray(true, maskTextures, maskSavePath, width, height);

        Debug.Log("[TerrainV2] Оба массива собраны!");
    }

    private void BuildSingleArray(bool isLinear, Texture2D[] textures, string path, int width, int height)
    {
        if (width == 0 || height == 0)
        {
            Debug.LogError("[TerrainV2] Нет текстур для сборки!");
            return;
        }

        TextureFormat targetFormat = ResolveTextureFormat(compressionMode);
        bool needsCompression = (targetFormat != TextureFormat.RGBA32);

        Texture2DArray array = new Texture2DArray(
            width, height, MAX_LAYERS,
            targetFormat,
            true,
            isLinear
        );

        array.filterMode = FilterMode.Bilinear;
        array.wrapMode = TextureWrapMode.Repeat;
        array.anisoLevel = 4;

        string arrayLabel = isLinear ? "Mask Array" : "Color Array";
        EditorUtility.DisplayProgressBar($"Building {arrayLabel}", "Подготовка...", 0f);

        try
        {
            for (int i = 0; i < MAX_LAYERS; i++)
            {
                EditorUtility.DisplayProgressBar($"Building {arrayLabel}",
                    $"Слой {i}: {layerNames[i]}", (float)i / MAX_LAYERS);

                Texture2D layerTex = new Texture2D(width, height, TextureFormat.RGBA32, true, isLinear);

                if (textures[i] != null)
                {
                    Texture2D src = PrepareSourceTexture(textures[i], width, height, isLinear);
                    Color[] pixels = src.GetPixels(0);
                    layerTex.SetPixels(pixels);
                    layerTex.Apply(true, false);

                    Debug.Log($"[TerrainV2] {arrayLabel} Layer {i} ({layerNames[i]}): {textures[i].name}");
                }
                else
                {
                    Color defaultColor = isLinear
                        ? new Color(0.5f, 0.5f, 0.5f, 1.0f)
                        : new Color(0.5f, 0.5f, 0.5f, 0.5f);

                    Color[] fillColors = new Color[width * height];
                    for (int p = 0; p < fillColors.Length; p++)
                        fillColors[p] = defaultColor;
                    layerTex.SetPixels(fillColors);
                    layerTex.Apply(true, false);
                }

                if (needsCompression)
                {
                    EditorUtility.CompressTexture(layerTex, targetFormat, TextureCompressionQuality.Best);
                }

                int mipCount = Mathf.Min(layerTex.mipmapCount, array.mipmapCount);
                for (int mip = 0; mip < mipCount; mip++)
                {
                    Graphics.CopyTexture(layerTex, 0, mip, array, i, mip);
                }

                DestroyImmediate(layerTex);
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existing = AssetDatabase.LoadAssetAtPath<Texture2DArray>(path);
            if (existing != null)
                AssetDatabase.DeleteAsset(path);

            AssetDatabase.CreateAsset(array, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            float sizeMB = EstimateArraySizeMB(width, height, targetFormat);
            Debug.Log($"[TerrainV2] {arrayLabel} ({(isLinear ? "Linear" : "sRGB")}) создан: {path} " +
                      $"({width}x{height}, {MAX_LAYERS} layers, {targetFormat}, ~{sizeMB:F1} MB)");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ==================== Утилиты ====================

    private TextureFormat ResolveTextureFormat(CompressionMode mode)
    {
        if (mode == CompressionMode.Auto)
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            switch (target)
            {
                case BuildTarget.Android:
                case BuildTarget.iOS:
                    return TextureFormat.ASTC_6x6;
                default:
                    return TextureFormat.BC7;
            }
        }

        switch (mode)
        {
            case CompressionMode.PC_BC7:            return TextureFormat.BC7;
            case CompressionMode.Android_ASTC6x6:   return TextureFormat.ASTC_6x6;
            case CompressionMode.Android_ASTC4x4:   return TextureFormat.ASTC_4x4;
            case CompressionMode.None_RGBA32:        return TextureFormat.RGBA32;
            default:                                 return TextureFormat.RGBA32;
        }
    }

    private string GetCurrentPlatformName()
    {
        BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
        switch (target)
        {
            case BuildTarget.Android:              return "Android";
            case BuildTarget.iOS:                  return "iOS";
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:   return "Windows";
            case BuildTarget.StandaloneOSX:        return "macOS";
            case BuildTarget.StandaloneLinux64:     return "Linux";
            default:                               return target.ToString();
        }
    }

    private float EstimateArraySizeMB(int width, int height, TextureFormat format)
    {
        float bpp;
        switch (format)
        {
            case TextureFormat.BC7:         bpp = 8f;    break;
            case TextureFormat.ASTC_4x4:    bpp = 8f;    break;
            case TextureFormat.ASTC_6x6:    bpp = 3.56f; break;
            case TextureFormat.RGBA32:      bpp = 32f;   break;
            default:                        bpp = 32f;   break;
        }

        float baseSize = (width * height * bpp / 8f) * MAX_LAYERS;
        float withMips = baseSize * 1.33f;
        return withMips / (1024f * 1024f);
    }

    private Texture2D PrepareSourceTexture(Texture2D texture, int targetWidth, int targetHeight, bool isLinear)
    {
        string texPath = AssetDatabase.GetAssetPath(texture);
        TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;

        if (importer != null)
        {
            bool needReimport = false;

            if (!importer.isReadable)
            {
                importer.isReadable = true;
                needReimport = true;
            }

            if (isLinear && importer.sRGBTexture)
            {
                importer.sRGBTexture = false;
                needReimport = true;
            }

            var platformSettings = importer.GetDefaultPlatformTextureSettings();
            if (platformSettings.format != TextureImporterFormat.RGBA32)
            {
                platformSettings.format = TextureImporterFormat.RGBA32;
                importer.SetPlatformTextureSettings(platformSettings);
                needReimport = true;
            }

            if (needReimport)
            {
                importer.SaveAndReimport();
            }
        }

        Texture2D src = texture;

        if (src.width != targetWidth || src.height != targetHeight)
        {
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32,
                isLinear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false, isLinear);
            resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            resized.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            src = resized;

            Debug.Log($"[TerrainV2] Resize {texture.name}: {texture.width}x{texture.height} -> {targetWidth}x{targetHeight}");
        }

        return src;
    }
}
