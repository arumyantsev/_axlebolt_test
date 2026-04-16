using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// SplatMap Painter — рисование по текстуре SplatMap прямо в Scene View.
/// Каналы RGBA = слои террейна (R=Layer1, G=Layer2, B=Layer3, A=Layer4).
/// Base layer (0) = implicit (1 - sum(RGBA)).
///
/// Menu: Tools/cgrum/Splat Map Painter
/// </summary>
public class SplatMapPainter : EditorWindow
{
    private const string PREFS_PREFIX = "SplatMapPainter_";
    private const string ALPHAS_FOLDER = "Assets/Editor/SplatMapAlphas";

    private enum PaintChannel { R = 0, G = 1, B = 2, A = 3 }

    // Состояние
    private bool enablePaint = false;
    private bool eraseMode = false;
    private bool normalize = true;
    private PaintChannel channel = PaintChannel.R;
    private float brushRadius = 5f;
    private float brushStrength = 0.25f;
    private float brushFalloff = 0.5f;

    // Текстура — работаем НАПРЯМУЮ с оригиналом (Read/Write ON)
    private Texture2D splatMap;
    private Color[] splatPixels;
    private Color[] sessionSnapshot; // снимок при загрузке (для Revert)
    private Color[] strokeSnapshot;  // снимок перед stroke (для Undo)
    private int splatWidth, splatHeight;
    private bool isDirty = false;

    // Brush alphas
    private Texture2D[] brushAlphas;
    private string[] brushAlphaNames;
    private int selectedBrushAlpha = 0;

    private float worldBrushRadius = 1f;
    private Vector2 scrollPos;

    [MenuItem("Tools/cgrum/Splat Map Painter")]
    public static void ShowWindow()
    {
        GetWindow<SplatMapPainter>("Splat Map Painter");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        LoadPrefs();
        RefreshBrushAlphas();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SavePrefs();
        if (isDirty && splatMap != null)
            SaveSplatMap();
    }

    // ==================== GUI ====================

    private void OnGUI()
    {
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        GUILayout.Label("Splat Map Painter", EditorStyles.boldLabel);
        enablePaint = EditorGUILayout.Toggle("Enable Paint", enablePaint);

        EditorGUILayout.Space(5);

        EditorGUI.BeginChangeCheck();
        splatMap = (Texture2D)EditorGUILayout.ObjectField("Splat Map", splatMap, typeof(Texture2D), false);
        if (EditorGUI.EndChangeCheck() && splatMap != null)
            LoadSplatMap();

        if (splatMap != null)
            EditorGUILayout.LabelField($"Size: {splatWidth}x{splatHeight}  Format: {splatMap.format}", EditorStyles.miniLabel);

        EditorGUILayout.Space(5);

        // Channel buttons
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Channel:", GUILayout.Width(60));
        DrawChannelButton(PaintChannel.R, "R (1)", Color.red);
        DrawChannelButton(PaintChannel.G, "G (2)", Color.green);
        DrawChannelButton(PaintChannel.B, "B (3)", new Color(0.3f, 0.5f, 1f));
        DrawChannelButton(PaintChannel.A, "A (4)", Color.yellow);
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(3);
        eraseMode = EditorGUILayout.Toggle("Erase Mode (Tab)", eraseMode);
        normalize = EditorGUILayout.Toggle("Normalize Channels", normalize);

        EditorGUILayout.Space(5);
        GUILayout.Label("Brush", EditorStyles.boldLabel);
        brushRadius = EditorGUILayout.Slider("Radius (Scroll)", brushRadius, 1f, 100f);
        brushStrength = EditorGUILayout.Slider("Strength (Ctrl+Scroll)", brushStrength, 0.01f, 1f);
        brushFalloff = EditorGUILayout.Slider("Falloff (Alt+Scroll)", brushFalloff, 0f, 1f);

        EditorGUILayout.Space(5);
        GUILayout.Label("Brush Alpha", EditorStyles.boldLabel);
        if (brushAlphaNames != null && brushAlphaNames.Length > 0)
            selectedBrushAlpha = EditorGUILayout.Popup("Shape", selectedBrushAlpha, brushAlphaNames);
        if (GUILayout.Button("Refresh Brush Alphas"))
            RefreshBrushAlphas();

        if (selectedBrushAlpha > 0 && brushAlphas != null && selectedBrushAlpha - 1 < brushAlphas.Length)
        {
            Texture2D alphaTex = brushAlphas[selectedBrushAlpha - 1];
            if (alphaTex != null)
            {
                Rect r = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64));
                EditorGUI.DrawPreviewTexture(r, alphaTex);
            }
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Save", GUILayout.Height(30)))
            SaveSplatMap();
        if (GUILayout.Button("Revert to Session Start", GUILayout.Height(30)))
            RevertToSessionStart();
        EditorGUILayout.EndHorizontal();

        if (isDirty)
            EditorGUILayout.HelpBox("Unsaved changes!", MessageType.Warning);

        EditorGUILayout.Space(5);
        EditorGUILayout.HelpBox(
            "LMB: paint\nScroll: radius\nCtrl+Scroll: strength\nAlt+Scroll: falloff\n" +
            "1/2/3/4: channel R/G/B/A\nTab: Paint/Erase\n\nErase stерёт ВСЕ каналы (чёрный = base layer)",
            MessageType.Info);

        EditorGUILayout.EndScrollView();
    }

    private void DrawChannelButton(PaintChannel ch, string label, Color col)
    {
        GUI.backgroundColor = channel == ch ? col : Color.white;
        if (GUILayout.Button(label, GUILayout.Height(25))) channel = ch;
    }

    // ==================== Scene View ====================

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!enablePaint || splatMap == null || splatPixels == null) return;

        Event e = Event.current;
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        HandleBrushControls(e);
        HandleHotkeys(e);

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            Vector2 uv = hit.textureCoord;
            float texelWorldSize = EstimateTexelWorldSize(hit);
            worldBrushRadius = brushRadius * texelWorldSize;

            // Brush preview
            Color brushColor = eraseMode ? Color.white : GetChannelColor();

            // Заливка круга — alpha = strength (визуализация силы)
            Color fillColor = new Color(brushColor.r, brushColor.g, brushColor.b, brushStrength * 0.3f);
            Handles.color = fillColor;
            Handles.DrawSolidDisc(hit.point, hit.normal, worldBrushRadius);

            // Внешний круг — radius
            brushColor.a = 0.8f;
            Handles.color = brushColor;
            Handles.DrawWireDisc(hit.point, hit.normal, worldBrushRadius);

            // Внутренний круг — falloff
            if (brushFalloff > 0.01f)
            {
                Handles.color = new Color(brushColor.r, brushColor.g, brushColor.b, 0.3f);
                Handles.DrawWireDisc(hit.point, hit.normal, worldBrushRadius * (1f - brushFalloff));
            }

            DrawOverlayUI();

            // Paint on LMB
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && !e.alt)
            {
                if (e.type == EventType.MouseDown)
                    strokeSnapshot = (Color[])splatPixels.Clone();

                PaintAtUV(uv);
                splatMap.SetPixels(splatPixels);
                splatMap.Apply();
                isDirty = true;
                e.Use();
            }
        }

        sceneView.Repaint();
    }

    // ==================== Paint ====================

    private void PaintAtUV(Vector2 uv)
    {
        int centerX = Mathf.RoundToInt(uv.x * (splatWidth - 1));
        int centerY = Mathf.RoundToInt(uv.y * (splatHeight - 1));
        int radiusPixels = Mathf.CeilToInt(brushRadius);

        // Brush alpha
        Color[] alphaPixels = null;
        int alphaW = 0, alphaH = 0;
        if (selectedBrushAlpha > 0 && brushAlphas != null && selectedBrushAlpha - 1 < brushAlphas.Length)
        {
            Texture2D alphaTex = brushAlphas[selectedBrushAlpha - 1];
            if (alphaTex != null)
            {
                alphaPixels = alphaTex.GetPixels();
                alphaW = alphaTex.width;
                alphaH = alphaTex.height;
            }
        }

        int channelIdx = (int)channel;

        for (int y = -radiusPixels; y <= radiusPixels; y++)
        {
            for (int x = -radiusPixels; x <= radiusPixels; x++)
            {
                int px = centerX + x;
                int py = centerY + y;
                if (px < 0 || px >= splatWidth || py < 0 || py >= splatHeight) continue;

                float dist = Mathf.Sqrt(x * x + y * y);
                if (dist > brushRadius) continue;

                // Falloff
                float falloffFactor = 1f;
                if (brushFalloff > 0.01f)
                {
                    float innerRadius = brushRadius * (1f - brushFalloff);
                    if (dist > innerRadius)
                    {
                        falloffFactor = 1f - ((dist - innerRadius) / (brushRadius - innerRadius + 0.001f));
                        falloffFactor = Mathf.Clamp01(falloffFactor);
                        // Smooth falloff (cubic)
                        falloffFactor = falloffFactor * falloffFactor * (3f - 2f * falloffFactor);
                    }
                }

                // Alpha mask
                float alphaMask = 1f;
                if (alphaPixels != null)
                {
                    float au = (float)(x + radiusPixels) / (radiusPixels * 2f);
                    float av = (float)(y + radiusPixels) / (radiusPixels * 2f);
                    int aX = Mathf.Clamp(Mathf.RoundToInt(au * (alphaW - 1)), 0, alphaW - 1);
                    int aY = Mathf.Clamp(Mathf.RoundToInt(av * (alphaH - 1)), 0, alphaH - 1);
                    alphaMask = alphaPixels[aY * alphaW + aX].r;
                }

                float strength = brushStrength * falloffFactor * alphaMask;

                int idx = py * splatWidth + px;
                Color c = splatPixels[idx];

                if (eraseMode)
                {
                    // Erase: уменьшаем ВСЕ каналы к 0 (чёрный = базовый слой)
                    c.r = Mathf.Lerp(c.r, 0f, strength);
                    c.g = Mathf.Lerp(c.g, 0f, strength);
                    c.b = Mathf.Lerp(c.b, 0f, strength);
                    c.a = Mathf.Lerp(c.a, 0f, strength);
                }
                else
                {
                    // Paint: увеличиваем выбранный канал к 1
                    c[channelIdx] = Mathf.Lerp(c[channelIdx], 1f, strength);

                    // Normalize: другие каналы пропорционально уменьшаем
                    if (normalize)
                    {
                        float sum = c.r + c.g + c.b + c.a;
                        if (sum > 1f)
                        {
                            float otherSum = sum - c[channelIdx];
                            if (otherSum > 0.0001f)
                            {
                                float scale = (1f - c[channelIdx]) / otherSum;
                                for (int ch = 0; ch < 4; ch++)
                                {
                                    if (ch != channelIdx)
                                        c[ch] *= scale;
                                }
                            }
                        }
                    }
                }

                splatPixels[idx] = c;
            }
        }
    }

    // ==================== Texture I/O ====================

    private void LoadSplatMap()
    {
        if (splatMap == null) return;

        string path = AssetDatabase.GetAssetPath(splatMap);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            bool needReimport = false;
            if (!importer.isReadable) { importer.isReadable = true; needReimport = true; }
            if (importer.sRGBTexture) { importer.sRGBTexture = false; needReimport = true; }

            // Default platform → Uncompressed
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                needReimport = true;
            }

            // Все platform overrides → RGBA32 Uncompressed
            foreach (string platform in new[] { "Android", "iPhone", "Standalone" })
            {
                var settings = importer.GetPlatformTextureSettings(platform);
                if (settings.overridden && settings.format != TextureImporterFormat.RGBA32)
                {
                    settings.format = TextureImporterFormat.RGBA32;
                    settings.textureCompression = TextureImporterCompression.Uncompressed;
                    importer.SetPlatformTextureSettings(settings);
                    needReimport = true;
                }
            }

            if (needReimport)
            {
                importer.SaveAndReimport();
                splatMap = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }

        splatWidth = splatMap.width;
        splatHeight = splatMap.height;
        splatPixels = splatMap.GetPixels();
        sessionSnapshot = (Color[])splatPixels.Clone();
        isDirty = false;

        Debug.Log($"[SplatMapPainter] Loaded: {path} ({splatWidth}x{splatHeight}, {splatMap.format})");
    }

    private void SaveSplatMap()
    {
        if (splatMap == null || splatPixels == null) return;

        splatMap.SetPixels(splatPixels);
        splatMap.Apply();

        string path = AssetDatabase.GetAssetPath(splatMap);
        if (!string.IsNullOrEmpty(path))
        {
            string ext = Path.GetExtension(path).ToLower();
            byte[] bytes;
            if (ext == ".tga") bytes = splatMap.EncodeToTGA();
            else if (ext == ".png") bytes = splatMap.EncodeToPNG();
            else { bytes = splatMap.EncodeToPNG(); path = Path.ChangeExtension(path, ".png"); }

            File.WriteAllBytes(path, bytes);
            AssetDatabase.Refresh();

            // Обновляем session snapshot после save
            sessionSnapshot = (Color[])splatPixels.Clone();
            isDirty = false;
            Debug.Log($"[SplatMapPainter] Saved: {path}");
        }
    }

    private void RevertToSessionStart()
    {
        if (sessionSnapshot == null || splatMap == null) return;

        splatPixels = (Color[])sessionSnapshot.Clone();
        splatMap.SetPixels(splatPixels);
        splatMap.Apply();
        isDirty = false;
        Debug.Log("[SplatMapPainter] Reverted to session start");
    }

    // ==================== Brush Alphas ====================

    private void RefreshBrushAlphas()
    {
        var alphas = new List<Texture2D>();
        var names = new List<string> { "Circle (default)" };

        if (AssetDatabase.IsValidFolder(ALPHAS_FOLDER))
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ALPHAS_FOLDER }))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (tex != null)
                {
                    var imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (imp != null && !imp.isReadable)
                    {
                        imp.isReadable = true;
                        imp.SaveAndReimport();
                        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    }
                    alphas.Add(tex);
                    names.Add(Path.GetFileNameWithoutExtension(assetPath));
                }
            }
        }

        brushAlphas = alphas.ToArray();
        brushAlphaNames = names.ToArray();
        if (selectedBrushAlpha >= brushAlphaNames.Length) selectedBrushAlpha = 0;
    }

    // ==================== Controls ====================

    private void HandleBrushControls(Event e)
    {
        if (e.type != EventType.ScrollWheel) return;

        float delta = -Mathf.Sign(e.delta.y);

        if (e.alt)
        {
            // Alt + Scroll = falloff
            brushFalloff += delta * 0.05f;
            brushFalloff = Mathf.Clamp01(brushFalloff);
        }
        else if (e.control)
        {
            // Ctrl + Scroll = strength
            brushStrength += delta * 0.05f;
            brushStrength = Mathf.Clamp(brushStrength, 0.01f, 1f);
        }
        else
        {
            // Scroll = radius
            brushRadius += delta * 2f;
            brushRadius = Mathf.Max(1f, brushRadius);
        }

        e.Use();
        Repaint();
        SceneView.RepaintAll();
    }

    private void HandleHotkeys(Event e)
    {
        if (e.type != EventType.KeyDown) return;

        switch (e.keyCode)
        {
            case KeyCode.Alpha1: channel = PaintChannel.R; e.Use(); Repaint(); break;
            case KeyCode.Alpha2: channel = PaintChannel.G; e.Use(); Repaint(); break;
            case KeyCode.Alpha3: channel = PaintChannel.B; e.Use(); Repaint(); break;
            case KeyCode.Alpha4: channel = PaintChannel.A; e.Use(); Repaint(); break;
            case KeyCode.Tab: eraseMode = !eraseMode; e.Use(); Repaint(); break;
        }
    }

    // ==================== Overlay ====================

    private void DrawOverlayUI()
    {
        Handles.BeginGUI();
        Vector2 guiPos = Event.current.mousePosition + new Vector2(20, 20);
        Rect rect = new Rect(guiPos.x, guiPos.y, 240, 100);

        var boxStyle = new GUIStyle("box") { alignment = TextAnchor.UpperLeft, fontSize = 12, normal = { textColor = Color.white } };
        GUI.BeginGroup(rect, GUIContent.none, boxStyle);

        string modeName = eraseMode ? "ERASE (all channels)" : $"PAINT [{channel}]";
        Color col = eraseMode ? Color.white : GetChannelColor();
        string colHex = ColorUtility.ToHtmlStringRGB(col);

        var richStyle = new GUIStyle(GUI.skin.label) { richText = true };
        GUI.Label(new Rect(5, 5, 230, 20), $"<color=#{colHex}>{modeName}</color>", richStyle);
        GUI.Label(new Rect(5, 25, 230, 20), $"Radius: {brushRadius:F1}");
        GUI.Label(new Rect(5, 45, 230, 20), $"Strength: {brushStrength:F2}");
        GUI.Label(new Rect(5, 65, 230, 20), $"Falloff: {brushFalloff:F2}");
        GUI.Label(new Rect(5, 85, 230, 20), $"Normalize: {(normalize ? "ON" : "OFF")}");

        GUI.EndGroup();
        Handles.EndGUI();
    }

    // ==================== Helpers ====================

    private Color GetChannelColor()
    {
        switch (channel)
        {
            case PaintChannel.R: return Color.red;
            case PaintChannel.G: return Color.green;
            case PaintChannel.B: return new Color(0.3f, 0.5f, 1f);
            case PaintChannel.A: return Color.yellow;
            default: return Color.white;
        }
    }

    private float EstimateTexelWorldSize(RaycastHit hit)
    {
        var mr = hit.collider.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            float meshSize = mr.bounds.size.magnitude;
            return meshSize / Mathf.Max(splatWidth, splatHeight);
        }
        return 0.1f;
    }

    // ==================== Persistence ====================

    private void SavePrefs()
    {
        EditorPrefs.SetBool(PREFS_PREFIX + "enablePaint", enablePaint);
        EditorPrefs.SetBool(PREFS_PREFIX + "eraseMode", eraseMode);
        EditorPrefs.SetBool(PREFS_PREFIX + "normalize", normalize);
        EditorPrefs.SetInt(PREFS_PREFIX + "channel", (int)channel);
        EditorPrefs.SetFloat(PREFS_PREFIX + "radius", brushRadius);
        EditorPrefs.SetFloat(PREFS_PREFIX + "strength", brushStrength);
        EditorPrefs.SetFloat(PREFS_PREFIX + "falloff", brushFalloff);
        EditorPrefs.SetInt(PREFS_PREFIX + "brushAlpha", selectedBrushAlpha);

        if (splatMap != null)
            EditorPrefs.SetString(PREFS_PREFIX + "splatMapGUID",
                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(splatMap)));
    }

    private void LoadPrefs()
    {
        enablePaint = EditorPrefs.GetBool(PREFS_PREFIX + "enablePaint", false);
        eraseMode = EditorPrefs.GetBool(PREFS_PREFIX + "eraseMode", false);
        normalize = EditorPrefs.GetBool(PREFS_PREFIX + "normalize", true);
        channel = (PaintChannel)EditorPrefs.GetInt(PREFS_PREFIX + "channel", 0);
        brushRadius = EditorPrefs.GetFloat(PREFS_PREFIX + "radius", 5f);
        brushStrength = EditorPrefs.GetFloat(PREFS_PREFIX + "strength", 0.25f);
        brushFalloff = EditorPrefs.GetFloat(PREFS_PREFIX + "falloff", 0.5f);
        selectedBrushAlpha = EditorPrefs.GetInt(PREFS_PREFIX + "brushAlpha", 0);

        string guid = EditorPrefs.GetString(PREFS_PREFIX + "splatMapGUID", "");
        if (!string.IsNullOrEmpty(guid))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
            {
                splatMap = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (splatMap != null) LoadSplatMap();
            }
        }
    }
}
