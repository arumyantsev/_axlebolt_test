using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class VertexPainterWindow : EditorWindow
{
    public enum PaintMode { Paint, Erase }
    public enum ChannelMode { RGB, Alpha }

    private bool enablePaint = false;

    private PaintMode mode = PaintMode.Paint;
    private ChannelMode channelMode = ChannelMode.RGB;

    private Color paintColor = Color.red;
    private float brushRadius = 0.5f;
    private float brushStrength = 0.25f;

    private MeshFilter meshFilter;
    private Mesh mesh;
    private Vector3[] vertices;
    private Color[] colors;

    // cahs default colors for original mesh
    private static Dictionary<Mesh, Color[]> originalColorsCache = new Dictionary<Mesh, Color[]>();

    [MenuItem("Tools/Editor Tools/Scene Setup//Vertex Painter", false, 300)]
    public static void ShowWindow()
    {
        GetWindow<VertexPainterWindow>("Vertex Painter");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    private void OnGUI()
    {
        GUILayout.Label("🎨 Vertex Painter Settings", EditorStyles.boldLabel);

        enablePaint = EditorGUILayout.Toggle("Enable Paint", enablePaint);

        mode = (PaintMode)EditorGUILayout.EnumPopup("Mode", mode);
        channelMode = (ChannelMode)EditorGUILayout.EnumPopup("Channel", channelMode);

        paintColor = EditorGUILayout.ColorField("Paint Color", paintColor);
        brushRadius = EditorGUILayout.Slider("Radius", brushRadius, 0.01f, 5f);
        brushStrength = EditorGUILayout.Slider("Strength", brushStrength, 0f, 1f);

        EditorGUILayout.HelpBox(
            "ЛКМ: рисовать\nКолесо: радиус\nShift+Колесо: сила\nTab: Paint/Erase\nC: RGB/Alpha",
            MessageType.Info
        );
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!enablePaint) return;

        Event e = Event.current;
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        meshFilter = Selection.activeGameObject?.GetComponent<MeshFilter>();
        if (!meshFilter) return;

        EnsureMeshCopy(meshFilter);
        mesh = meshFilter.sharedMesh;
        if (!mesh) return;

        if (colors == null || colors.Length != mesh.vertexCount)
        {
            colors = mesh.colors.Length == mesh.vertexCount
                ? mesh.colors
                : new Color[mesh.vertexCount];
        }

        vertices = mesh.vertices;

        HandleBrushControls(e);
        HandleHotkeys(e);

        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        if (RaycastMesh(ray, mesh, meshFilter.transform, out Vector3 hitPoint, out Vector3 hitNormal))
        {
            // radius circle
            Handles.color = channelMode == ChannelMode.RGB
                ? new Color(paintColor.r, paintColor.g, paintColor.b, 0.6f)
                : new Color(1f, 1f, 1f, 0.6f);
            Handles.DrawWireDisc(hitPoint, hitNormal, brushRadius);

            DrawOverlayUI();

            if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && !e.alt)
            {
                e.Use();
                Paint(hitPoint, meshFilter.transform);
                mesh.colors = colors;

                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssets();
            }
        }

        sceneView.Repaint();
    }

    private void DrawOverlayUI()
    {
        Handles.BeginGUI();
        Vector2 guiPos = Event.current.mousePosition + new Vector2(20, 20);
        Rect rect = new Rect(guiPos.x, guiPos.y, 220, 80);

        GUIStyle boxStyle = new GUIStyle("box")
        {
            alignment = TextAnchor.UpperLeft,
            fontSize = 12,
            normal = { textColor = Color.white }
        };

        GUI.BeginGroup(rect, GUIContent.none, boxStyle);
        GUI.Label(new Rect(5, 5, 190, 20), $"Mode: {mode} (Tab)");
        GUI.Label(new Rect(5, 25, 190, 20), $"Channel: {channelMode} (C)");
        GUI.Label(new Rect(5, 45, 190, 20), $"Radius: {brushRadius:F2}");
        GUI.Label(new Rect(5, 65, 190, 20), $"Strength: {brushStrength:F2}");
        GUI.EndGroup();

        Handles.EndGUI();
    }

    private void HandleBrushControls(Event e)
    {
        if (e.type == EventType.ScrollWheel)
        {
            if (e.shift)
            {
                brushStrength -= Mathf.Sign(e.delta.y) * 0.05f;
                brushStrength = Mathf.Clamp01(brushStrength);
            }
            else
            {
                brushRadius -= Mathf.Sign(e.delta.y) * 0.1f;
                brushRadius = Mathf.Max(0.01f, brushRadius);
            }

            e.Use();
            SceneView.RepaintAll();
        }
    }

    private void HandleHotkeys(Event e)
    {
        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Tab)
            {
                mode = mode == PaintMode.Paint ? PaintMode.Erase : PaintMode.Paint;
                e.Use();
                SceneView.RepaintAll();
            }
            else if (e.keyCode == KeyCode.C)
            {
                channelMode = channelMode == ChannelMode.RGB ? ChannelMode.Alpha : ChannelMode.RGB;
                e.Use();
                SceneView.RepaintAll();
            }
        }
    }

    // get default colors
    private void EnsureMeshCopy(MeshFilter mf)
    {
        if (mf.sharedMesh == null) return;

        string assetPath = AssetDatabase.GetAssetPath(mf.sharedMesh);

        // is mesh exist do nothing
        if (!string.IsNullOrEmpty(assetPath) && assetPath.StartsWith("Assets/PaintedMeshes"))
            return;

        // folder
        string folderPath = "Assets/PaintedMeshes";
        if (!AssetDatabase.IsValidFolder(folderPath))
            AssetDatabase.CreateFolder("Assets", "PaintedMeshes");

        string cleanName = mf.gameObject.name;
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            cleanName = cleanName.Replace(c.ToString(), "_");
        cleanName = cleanName.Replace("(", "_").Replace(")", "_").Replace(" ", "_");

        Mesh sourceMesh = mf.sharedMesh;

        // get default colors from original
        if (!originalColorsCache.TryGetValue(sourceMesh, out Color[] baseColors))
        {
            if (sourceMesh.colors != null && sourceMesh.colors.Length == sourceMesh.vertexCount)
            {
                baseColors = (Color[])sourceMesh.colors.Clone();
            }
            else
            {
                baseColors = new Color[sourceMesh.vertexCount];
                for (int i = 0; i < baseColors.Length; i++)
                    baseColors[i] = Color.white;
            }
            originalColorsCache[sourceMesh] = baseColors;
        }

        // mesh copy
        Mesh newMesh = Object.Instantiate(sourceMesh);
        newMesh.name = sourceMesh.name + "_Painted_" + cleanName;

        // assign defauld colors
        newMesh.colors = (Color[])originalColorsCache[sourceMesh].Clone();

        string uniquePath = AssetDatabase.GenerateUniqueAssetPath(
            $"{folderPath}/{newMesh.name}.asset"
        );

        AssetDatabase.CreateAsset(newMesh, uniquePath);
        AssetDatabase.SaveAssets();

        mf.sharedMesh = newMesh;
    }

    private void Paint(Vector3 hitPoint, Transform meshTransform)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 worldPos = meshTransform.TransformPoint(vertices[i]);
            float dist = Vector3.Distance(hitPoint, worldPos);

            if (dist < brushRadius)
            {
                float strength = 1f - (dist / brushRadius);
                strength *= brushStrength;

                Color c = colors[i];

                if (channelMode == ChannelMode.RGB)
                {
                    if (mode == PaintMode.Paint)
                        c = Color.Lerp(c, new Color(paintColor.r, paintColor.g, paintColor.b, c.a), strength);
                    else
                        c = Color.Lerp(c, new Color(0, 0, 0, c.a), strength);
                }
                else if (channelMode == ChannelMode.Alpha)
                {
                    float targetAlpha = mode == PaintMode.Paint ? Mathf.Clamp01(paintColor.a) : 0f;
                    c.a = Mathf.Lerp(c.a, targetAlpha, strength);
                }

                colors[i] = c;
            }
        }
    }

    // raycast by mesh filter
    private bool RaycastMesh(Ray ray, Mesh mesh, Transform transform, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        hitPoint = Vector3.zero;
        hitNormal = Vector3.up;
        bool hasHit = false;
        float closestDist = float.MaxValue;

        var verts = mesh.vertices;
        var tris = mesh.triangles;

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 v0 = transform.TransformPoint(verts[tris[i]]);
            Vector3 v1 = transform.TransformPoint(verts[tris[i + 1]]);
            Vector3 v2 = transform.TransformPoint(verts[tris[i + 2]]);

            if (RayIntersectsTriangle(ray, v0, v1, v2, out Vector3 p))
            {
                float dist = Vector3.Distance(ray.origin, p);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    hitPoint = p;
                    hitNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                    hasHit = true;
                }
            }
        }

        return hasHit;
    }

    private bool RayIntersectsTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2, out Vector3 hitPoint)
    {
        hitPoint = Vector3.zero;

        Vector3 e1 = v1 - v0;
        Vector3 e2 = v2 - v0;
        Vector3 pvec = Vector3.Cross(ray.direction, e2);
        float det = Vector3.Dot(e1, pvec);

        if (Mathf.Abs(det) < Mathf.Epsilon) return false;

        float invDet = 1f / det;
        Vector3 tvec = ray.origin - v0;
        float u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0 || u > 1) return false;

        Vector3 qvec = Vector3.Cross(tvec, e1);
        float v = Vector3.Dot(ray.direction, qvec) * invDet;
        if (v < 0 || u + v > 1) return false;

        float t = Vector3.Dot(e2, qvec) * invDet;
        if (t > Mathf.Epsilon)
        {
            hitPoint = ray.origin + ray.direction * t;
            return true;
        }

        return false;
    }
}
