using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Scatter Tool — расстановка префабов по террейну.
/// Tab 1: Scatter by SplatMap — batch генерация по каналу текстуры.
/// Tab 2: Brush Scatter — интерактивное рисование/удаление кистью.
///
/// Menu: Tools/cgrum/Scatter Tool
/// </summary>
public class ScatterTool : EditorWindow
{
    private const string PREFS_PREFIX = "ScatterTool_";

    // ==================== Tabs ====================
    private int currentTab = 0;
    private readonly string[] tabNames = { "Scatter by Map", "Brush Scatter" };

    // ==================== Shared ====================
    [System.Serializable]
    private class PrefabEntry
    {
        public GameObject prefab;
        public float weight = 1f;
    }

    private List<PrefabEntry> prefabs = new List<PrefabEntry>();
    private Vector2 scrollPos;

    // Placement settings
    private Vector2 rotationYRange = new Vector2(0f, 360f);
    private Vector2 scaleRange = new Vector2(0.8f, 1.2f);
    private bool alignToNormal = false;
    private float normalBlend = 0.5f; // 0=vertical, 1=full surface normal
    private float minDistance = 0.3f;
    private Transform parentObject;
    private LayerMask raycastLayer = ~0;

    // ==================== Tab 1: Scatter by Map ====================
    private Texture2D scatterSplatMap;
    private int scatterChannel = 0; // 0=R, 1=G, 2=B, 3=A
    private readonly string[] channelNames = { "R", "G", "B", "A" };
    private float scatterThreshold = 0.1f;
    private float scatterDensity = 2f; // шт/м²
    private int scatterSeed = 42;

    // ==================== Tab 2: Brush Scatter ====================
    private bool enableBrush = false;
    private bool brushEraseMode = false;
    private float brushRadius = 3f;
    private int brushDensity = 5; // штук за клик/drag
    // Placed objects tracking
    private List<GameObject> placedObjects = new List<GameObject>();

    [MenuItem("Tools/cgrum/Scatter Tool")]
    public static void ShowWindow()
    {
        GetWindow<ScatterTool>("Scatter Tool");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        LoadPrefs();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SavePrefs();
    }

    // ==================== GUI ====================

    private void OnGUI()
    {
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        GUILayout.Label("Scatter Tool", EditorStyles.boldLabel);

        currentTab = GUILayout.Toolbar(currentTab, tabNames, GUILayout.Height(28));

        EditorGUILayout.Space(5);

        // Shared: prefabs list
        DrawPrefabList();

        EditorGUILayout.Space(5);

        // Shared: placement settings
        DrawPlacementSettings();

        EditorGUILayout.Space(10);

        if (currentTab == 0)
            DrawScatterByMapTab();
        else
            DrawBrushScatterTab();

        EditorGUILayout.Space(10);

        // Cleanup
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Select All Scattered", GUILayout.Height(25)))
            SelectAllScattered();
        if (GUILayout.Button("Delete All Scattered", GUILayout.Height(25)))
            DeleteAllScattered();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndScrollView();
    }

    // ==================== Prefab List ====================

    private void DrawPrefabList()
    {
        GUILayout.Label("Prefabs", EditorStyles.boldLabel);

        for (int i = 0; i < prefabs.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            prefabs[i].prefab = (GameObject)EditorGUILayout.ObjectField(
                prefabs[i].prefab, typeof(GameObject), false);
            prefabs[i].weight = EditorGUILayout.FloatField(prefabs[i].weight, GUILayout.Width(50));
            if (GUILayout.Button("X", GUILayout.Width(22)))
            {
                prefabs.RemoveAt(i);
                i--;
            }
            EditorGUILayout.EndHorizontal();
        }

        if (GUILayout.Button("+ Add Prefab"))
            prefabs.Add(new PrefabEntry());
    }

    // ==================== Placement Settings ====================

    private void DrawPlacementSettings()
    {
        GUILayout.Label("Placement", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Rotation Y", GUILayout.Width(80));
        rotationYRange.x = EditorGUILayout.FloatField(rotationYRange.x, GUILayout.Width(50));
        EditorGUILayout.MinMaxSlider(ref rotationYRange.x, ref rotationYRange.y, 0f, 360f);
        rotationYRange.y = EditorGUILayout.FloatField(rotationYRange.y, GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Scale", GUILayout.Width(80));
        scaleRange.x = EditorGUILayout.FloatField(scaleRange.x, GUILayout.Width(50));
        EditorGUILayout.MinMaxSlider(ref scaleRange.x, ref scaleRange.y, 0.1f, 3f);
        scaleRange.y = EditorGUILayout.FloatField(scaleRange.y, GUILayout.Width(50));
        EditorGUILayout.EndHorizontal();

        alignToNormal = EditorGUILayout.Toggle("Align to Surface", alignToNormal);
        if (alignToNormal)
            normalBlend = EditorGUILayout.Slider("Normal Blend (0=up, 1=surface)", normalBlend, 0f, 1f);

        minDistance = EditorGUILayout.Slider("Min Distance", minDistance, 0f, 3f);
        parentObject = (Transform)EditorGUILayout.ObjectField("Parent", parentObject, typeof(Transform), true);
        raycastLayer = EditorGUILayout.MaskField("Raycast Layers", raycastLayer, UnityEditorInternal.InternalEditorUtility.layers);
    }

    // ==================== Tab 1: Scatter by Map ====================

    private void DrawScatterByMapTab()
    {
        GUILayout.Label("Scatter by SplatMap", EditorStyles.boldLabel);

        scatterSplatMap = (Texture2D)EditorGUILayout.ObjectField("Splat Map", scatterSplatMap, typeof(Texture2D), false);
        scatterChannel = GUILayout.Toolbar(scatterChannel, channelNames, GUILayout.Height(22));
        scatterThreshold = EditorGUILayout.Slider("Threshold", scatterThreshold, 0f, 1f);
        scatterDensity = EditorGUILayout.Slider("Density (per m²)", scatterDensity, 0.1f, 20f);
        scatterSeed = EditorGUILayout.IntField("Seed", scatterSeed);

        EditorGUILayout.Space(5);

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Generate Scatter", GUILayout.Height(35)))
            GenerateScatterByMap();
        GUI.backgroundColor = Color.white;
    }

    // ==================== Tab 2: Brush Scatter ====================

    private void DrawBrushScatterTab()
    {
        GUILayout.Label("Brush Scatter", EditorStyles.boldLabel);

        enableBrush = EditorGUILayout.Toggle("Enable Brush", enableBrush);
        brushEraseMode = EditorGUILayout.Toggle("Erase Mode (Tab)", brushEraseMode);

        brushRadius = EditorGUILayout.Slider("Radius (Scroll)", brushRadius, 0.5f, 20f);
        brushDensity = EditorGUILayout.IntSlider("Density per stroke", brushDensity, 1, 30);

        EditorGUILayout.Space(3);
        EditorGUILayout.HelpBox(
            "LMB: place/erase\nScroll: radius\nTab: Place/Erase",
            MessageType.Info);
    }

    // ==================== Scene View (Brush) ====================

    private void OnSceneGUI(SceneView sceneView)
    {
        if (currentTab != 1 || !enableBrush) return;

        Event e = Event.current;
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        // Hotkeys
        if (e.type == EventType.ScrollWheel)
        {
            float delta = -Mathf.Sign(e.delta.y);
            brushRadius += delta * 0.5f;
            brushRadius = Mathf.Max(0.5f, brushRadius);
            e.Use();
            Repaint();
            SceneView.RepaintAll();
        }

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Tab)
        {
            brushEraseMode = !brushEraseMode;
            e.Use();
            Repaint();
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f, raycastLayer))
        {
            // Preview
            Color discColor = brushEraseMode ? new Color(1, 0.3f, 0.3f, 0.4f) : new Color(0.3f, 1, 0.3f, 0.4f);
            Handles.color = discColor;
            Handles.DrawSolidDisc(hit.point, hit.normal, brushRadius * 0.15f);
            discColor.a = 0.8f;
            Handles.color = discColor;
            Handles.DrawWireDisc(hit.point, hit.normal, brushRadius);

            // Overlay
            Handles.BeginGUI();
            Vector2 gp = e.mousePosition + new Vector2(20, 20);
            var bs = new GUIStyle("box") { alignment = TextAnchor.UpperLeft, fontSize = 12, normal = { textColor = Color.white } };
            GUI.BeginGroup(new Rect(gp.x, gp.y, 200, 45), GUIContent.none, bs);
            GUI.Label(new Rect(5, 5, 190, 20), brushEraseMode ? "ERASE (Tab)" : "PLACE (Tab)");
            GUI.Label(new Rect(5, 25, 190, 20), $"Radius: {brushRadius:F1}  Density: {brushDensity}");
            GUI.EndGroup();
            Handles.EndGUI();

            // Place/Erase
            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && !e.alt)
            {
                if (brushEraseMode)
                    BrushErase(hit.point);
                else
                    BrushPlace(hit.point, hit.normal);
                e.Use();
            }
        }

        sceneView.Repaint();
    }

    // ==================== Scatter by Map Logic ====================

    private void GenerateScatterByMap()
    {
        if (scatterSplatMap == null || prefabs.Count == 0 || !prefabs.Any(p => p.prefab != null))
        {
            Debug.LogWarning("[ScatterTool] No splat map or prefabs assigned!");
            return;
        }

        // Ensure readable
        EnsureTextureReadable(scatterSplatMap);

        // Find terrain mesh bounds через scene colliders
        MeshCollider[] colliders = Object.FindObjectsOfType<MeshCollider>();
        if (colliders.Length == 0)
        {
            Debug.LogWarning("[ScatterTool] No MeshCollider found in scene for raycast!");
            return;
        }

        // Берём bounds первого коллайдера как area
        Bounds bounds = colliders[0].bounds;
        foreach (var col in colliders)
            bounds.Encapsulate(col.bounds);

        float areaSize = bounds.size.x * bounds.size.z;
        int totalPoints = Mathf.RoundToInt(areaSize * scatterDensity);

        Color[] pixels = scatterSplatMap.GetPixels();
        int texW = scatterSplatMap.width;
        int texH = scatterSplatMap.height;

        Random.InitState(scatterSeed);

        Transform parent = GetOrCreateParent();
        int placed = 0;
        List<Vector3> usedPositions = new List<Vector3>();

        Undo.RegisterCreatedObjectUndo(parent.gameObject, "Scatter Generate");

        EditorUtility.DisplayProgressBar("Scatter", "Generating...", 0f);

        try
        {
            for (int i = 0; i < totalPoints * 3; i++) // x3 attempts для density
            {
                if (placed >= totalPoints) break;

                float rx = Random.Range(bounds.min.x, bounds.max.x);
                float rz = Random.Range(bounds.min.z, bounds.max.z);

                // UV from world position (X→U, Z→V, как в шейдере террейна)
                float u = Mathf.InverseLerp(bounds.min.x, bounds.max.x, rx);
                float v = Mathf.InverseLerp(bounds.min.z, bounds.max.z, rz);

                // Текстура: px=col (U), py=row (V)
                int px = Mathf.Clamp(Mathf.RoundToInt(v * (texW - 1)), 0, texW - 1);
                int py = Mathf.Clamp(Mathf.RoundToInt((1f - u) * (texH - 1)), 0, texH - 1);

                float channelValue = pixels[py * texW + px][scatterChannel];
                if (channelValue < scatterThreshold) continue;

                // Probability по значению канала
                if (Random.value > channelValue) continue;

                // Raycast вниз
                Vector3 origin = new Vector3(rx, bounds.max.y + 10f, rz);
                if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 500f, raycastLayer))
                    continue;

                // Min distance check
                if (minDistance > 0 && usedPositions.Any(p => Vector3.Distance(p, hit.point) < minDistance))
                    continue;

                GameObject obj = PlaceObject(hit.point, hit.normal, parent);
                if (obj != null)
                {
                    usedPositions.Add(hit.point);
                    placedObjects.Add(obj);
                    placed++;
                }

                if (placed % 100 == 0)
                    EditorUtility.DisplayProgressBar("Scatter", $"Placed {placed}/{totalPoints}", (float)placed / totalPoints);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log($"[ScatterTool] Generated {placed} objects (seed={scatterSeed})");
    }

    // ==================== Brush Logic ====================

    private void BrushPlace(Vector3 center, Vector3 surfaceNormal)
    {
        if (prefabs.Count == 0 || !prefabs.Any(p => p.prefab != null)) return;

        Transform parent = GetOrCreateParent();

        for (int i = 0; i < brushDensity; i++)
        {
            Vector2 rnd = Random.insideUnitCircle * brushRadius;
            Vector3 origin = center + new Vector3(rnd.x, 50f, rnd.y);

            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 500f, raycastLayer))
                continue;

            // Min distance
            if (minDistance > 0)
            {
                bool tooClose = false;
                foreach (var obj in placedObjects)
                {
                    if (obj != null && Vector3.Distance(obj.transform.position, hit.point) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;
            }

            GameObject placed = PlaceObject(hit.point, hit.normal, parent);
            if (placed != null)
            {
                Undo.RegisterCreatedObjectUndo(placed, "Brush Scatter");
                placedObjects.Add(placed);
            }
        }
    }

    private void BrushErase(Vector3 center)
    {
        for (int i = placedObjects.Count - 1; i >= 0; i--)
        {
            if (placedObjects[i] == null)
            {
                placedObjects.RemoveAt(i);
                continue;
            }

            if (Vector3.Distance(placedObjects[i].transform.position, center) < brushRadius)
            {
                DestroyImmediate(placedObjects[i]);
                placedObjects.RemoveAt(i);
            }
        }
    }

    // ==================== Placement ====================

    private GameObject PlaceObject(Vector3 position, Vector3 surfaceNormal, Transform parent)
    {
        GameObject prefab = GetWeightedRandomPrefab();
        if (prefab == null) return null;

        GameObject obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        obj.transform.position = position;

        // Rotation
        float yRot = Random.Range(rotationYRange.x, rotationYRange.y);
        if (alignToNormal)
        {
            Vector3 up = Vector3.Lerp(Vector3.up, surfaceNormal, normalBlend);
            obj.transform.rotation = Quaternion.FromToRotation(Vector3.up, up) * Quaternion.Euler(0, yRot, 0);
        }
        else
        {
            obj.transform.rotation = Quaternion.Euler(0, yRot, 0);
        }

        // Scale
        float s = Random.Range(scaleRange.x, scaleRange.y);
        obj.transform.localScale = Vector3.one * s;

        if (parent != null)
            obj.transform.SetParent(parent, true);

        return obj;
    }

    private GameObject GetWeightedRandomPrefab()
    {
        var valid = prefabs.Where(p => p.prefab != null).ToList();
        if (valid.Count == 0) return null;

        float totalWeight = valid.Sum(p => p.weight);
        float rnd = Random.Range(0f, totalWeight);
        float cumulative = 0f;

        foreach (var entry in valid)
        {
            cumulative += entry.weight;
            if (rnd <= cumulative)
                return entry.prefab;
        }

        return valid.Last().prefab;
    }

    // ==================== Helpers ====================

    private Transform GetOrCreateParent()
    {
        if (parentObject != null) return parentObject;

        GameObject existing = GameObject.Find("_ScatteredObjects");
        if (existing != null) return existing.transform;

        GameObject go = new GameObject("_ScatteredObjects");
        parentObject = go.transform;
        return parentObject;
    }

    private void EnsureTextureReadable(Texture2D tex)
    {
        string path = AssetDatabase.GetAssetPath(tex);
        TextureImporter imp = AssetImporter.GetAtPath(path) as TextureImporter;
        if (imp != null && !imp.isReadable)
        {
            imp.isReadable = true;
            imp.SaveAndReimport();
        }
    }

    private void SelectAllScattered()
    {
        placedObjects.RemoveAll(o => o == null);
        Selection.objects = placedObjects.Select(o => (Object)o).ToArray();
    }

    private void DeleteAllScattered()
    {
        if (!EditorUtility.DisplayDialog("Delete All", $"Delete {placedObjects.Count} scattered objects?", "Delete", "Cancel"))
            return;

        foreach (var obj in placedObjects)
        {
            if (obj != null)
                DestroyImmediate(obj);
        }
        placedObjects.Clear();
    }

    // ==================== Persistence ====================

    private void SavePrefs()
    {
        EditorPrefs.SetInt(PREFS_PREFIX + "tab", currentTab);
        EditorPrefs.SetFloat(PREFS_PREFIX + "rotMinY", rotationYRange.x);
        EditorPrefs.SetFloat(PREFS_PREFIX + "rotMaxY", rotationYRange.y);
        EditorPrefs.SetFloat(PREFS_PREFIX + "scaleMin", scaleRange.x);
        EditorPrefs.SetFloat(PREFS_PREFIX + "scaleMax", scaleRange.y);
        EditorPrefs.SetBool(PREFS_PREFIX + "alignNormal", alignToNormal);
        EditorPrefs.SetFloat(PREFS_PREFIX + "normalBlend", normalBlend);
        EditorPrefs.SetFloat(PREFS_PREFIX + "minDist", minDistance);
        EditorPrefs.SetInt(PREFS_PREFIX + "scatterChannel", scatterChannel);
        EditorPrefs.SetFloat(PREFS_PREFIX + "scatterThreshold", scatterThreshold);
        EditorPrefs.SetFloat(PREFS_PREFIX + "scatterDensity", scatterDensity);
        EditorPrefs.SetInt(PREFS_PREFIX + "scatterSeed", scatterSeed);
        EditorPrefs.SetBool(PREFS_PREFIX + "enableBrush", enableBrush);
        EditorPrefs.SetFloat(PREFS_PREFIX + "brushRadius", brushRadius);
        EditorPrefs.SetInt(PREFS_PREFIX + "brushDensity", brushDensity);
        EditorPrefs.SetBool(PREFS_PREFIX + "alignNormal", alignToNormal);
        EditorPrefs.SetFloat(PREFS_PREFIX + "normalBlend", normalBlend);

        if (scatterSplatMap != null)
            EditorPrefs.SetString(PREFS_PREFIX + "splatMapGUID",
                AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(scatterSplatMap)));

        // Сохраняем префабы (до 10)
        int prefabCount = Mathf.Min(prefabs.Count, 10);
        EditorPrefs.SetInt(PREFS_PREFIX + "prefabCount", prefabCount);
        for (int i = 0; i < prefabCount; i++)
        {
            string guid = "";
            if (prefabs[i].prefab != null)
                guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefabs[i].prefab));
            EditorPrefs.SetString(PREFS_PREFIX + "prefab_" + i, guid);
            EditorPrefs.SetFloat(PREFS_PREFIX + "prefabW_" + i, prefabs[i].weight);
        }
    }

    private void LoadPrefs()
    {
        currentTab = EditorPrefs.GetInt(PREFS_PREFIX + "tab", 0);
        rotationYRange.x = EditorPrefs.GetFloat(PREFS_PREFIX + "rotMinY", 0f);
        rotationYRange.y = EditorPrefs.GetFloat(PREFS_PREFIX + "rotMaxY", 360f);
        scaleRange.x = EditorPrefs.GetFloat(PREFS_PREFIX + "scaleMin", 0.8f);
        scaleRange.y = EditorPrefs.GetFloat(PREFS_PREFIX + "scaleMax", 1.2f);
        alignToNormal = EditorPrefs.GetBool(PREFS_PREFIX + "alignNormal", false);
        normalBlend = EditorPrefs.GetFloat(PREFS_PREFIX + "normalBlend", 0.5f);
        minDistance = EditorPrefs.GetFloat(PREFS_PREFIX + "minDist", 0.3f);
        scatterChannel = EditorPrefs.GetInt(PREFS_PREFIX + "scatterChannel", 0);
        scatterThreshold = EditorPrefs.GetFloat(PREFS_PREFIX + "scatterThreshold", 0.1f);
        scatterDensity = EditorPrefs.GetFloat(PREFS_PREFIX + "scatterDensity", 2f);
        scatterSeed = EditorPrefs.GetInt(PREFS_PREFIX + "scatterSeed", 42);
        enableBrush = EditorPrefs.GetBool(PREFS_PREFIX + "enableBrush", false);
        brushRadius = EditorPrefs.GetFloat(PREFS_PREFIX + "brushRadius", 3f);
        brushDensity = EditorPrefs.GetInt(PREFS_PREFIX + "brushDensity", 5);
        alignToNormal = EditorPrefs.GetBool(PREFS_PREFIX + "alignNormal", false);
        normalBlend = EditorPrefs.GetFloat(PREFS_PREFIX + "normalBlend", 0.5f);

        string splatGuid = EditorPrefs.GetString(PREFS_PREFIX + "splatMapGUID", "");
        if (!string.IsNullOrEmpty(splatGuid))
        {
            string path = AssetDatabase.GUIDToAssetPath(splatGuid);
            if (!string.IsNullOrEmpty(path))
                scatterSplatMap = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Загружаем префабы
        int prefabCount = EditorPrefs.GetInt(PREFS_PREFIX + "prefabCount", 0);
        prefabs.Clear();
        for (int i = 0; i < prefabCount; i++)
        {
            string guid = EditorPrefs.GetString(PREFS_PREFIX + "prefab_" + i, "");
            float w = EditorPrefs.GetFloat(PREFS_PREFIX + "prefabW_" + i, 1f);
            var entry = new PrefabEntry { weight = w };
            if (!string.IsNullOrEmpty(guid))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(p))
                    entry.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(p);
            }
            prefabs.Add(entry);
        }
    }
}
