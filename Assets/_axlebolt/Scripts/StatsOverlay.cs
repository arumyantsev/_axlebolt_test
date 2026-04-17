using System.Text;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// Runtime HUD со статами: FPS, CPU/GPU time, Tris, Verts, Draw Calls, Batches, Saved by batching, SetPass.
///
/// ВАЖНО: счётчики из ProfilerRecorder доступны только в Development Build
/// (Build Settings → Development Build ✅). В Release они всегда будут 0.
///
/// Повесить на любой GameObject в сцене. Не дублировать — один на сцену.
/// </summary>
public class StatsOverlay : MonoBehaviour
{
    [Header("Display")]
    public bool showOverlay = true;
    public KeyCode toggleKey = KeyCode.F1;
    public int fontSize = 14;
    public Color textColor = new Color(1f, 1f, 0.4f, 1f);
    public Color backgroundColor = new Color(0f, 0f, 0f, 0.55f);
    [Tooltip("Частота обновления, раз в секунду")]
    public float updateHz = 4f;

    [Header("Sizing")]
    [Tooltip("Ручной множитель размера всего UI. 1 = автоматика по DPI с мягкой кривой.")]
    [Range(0.3f, 3f)] public float uiScale = 1f;
    [Tooltip("Панель не шире этой доли ширины экрана (чтобы не ела пол-экрана на планшете).")]
    [Range(0.15f, 0.6f)] public float maxWidthRatio = 0.28f;

    [Header("Toggle Button (mobile)")]
    [Tooltip("Базовый размер кнопки; итог умножается на uiScale × dpiScale.")]
    public int buttonSize = 56;
    public int buttonMargin = 10;

    // Render stats
    private ProfilerRecorder drawCalls;
    private ProfilerRecorder batches;
    private ProfilerRecorder triangles;
    private ProfilerRecorder vertices;
    private ProfilerRecorder setPassCalls;

    // Timing (nanoseconds)
    private ProfilerRecorder mainThreadTime;  // CPU main thread
    private ProfilerRecorder renderThreadTime;
    private ProfilerRecorder gpuTime;

    // FPS accumulator
    private float fpsAccum;
    private int fpsFrames;
    private float refreshTimer;
    private float displayedFps;

    // Сглаженные значения (обновляются по updateHz)
    private double displayedCpuMs;
    private double displayedRenderMs;
    private double displayedGpuMs;
    private long displayedDraws, displayedBatches, displayedTris, displayedVerts, displayedSetPass;

    private GUIStyle textStyle;
    private GUIStyle buttonStyle;
    private Texture2D bgTexture;
    private readonly StringBuilder sb = new StringBuilder(512);

    private void OnEnable()
    {
        // Render counters — всегда доступны в dev build
        drawCalls    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        batches      = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        triangles    = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        vertices     = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        setPassCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");

        // Timing — храним последние 15 сэмплов для усреднения
        mainThreadTime   = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        renderThreadTime = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 15);
        gpuTime          = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GPU Frame Time", 15);
    }

    private void OnDisable()
    {
        drawCalls.Dispose();
        batches.Dispose();
        triangles.Dispose();
        vertices.Dispose();
        setPassCalls.Dispose();
        mainThreadTime.Dispose();
        renderThreadTime.Dispose();
        gpuTime.Dispose();

        if (bgTexture != null)
        {
            Destroy(bgTexture);
            bgTexture = null;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            showOverlay = !showOverlay;

        float dt = Time.unscaledDeltaTime;
        fpsAccum += dt;
        fpsFrames++;
        refreshTimer += dt;

        float refreshInterval = 1f / Mathf.Max(updateHz, 0.1f);
        if (refreshTimer >= refreshInterval)
        {
            displayedFps = fpsFrames / fpsAccum;
            fpsAccum = 0f;
            fpsFrames = 0;
            refreshTimer = 0f;

            displayedCpuMs    = GetAverageFrameTimeMs(mainThreadTime);
            displayedRenderMs = GetAverageFrameTimeMs(renderThreadTime);
            displayedGpuMs    = GetAverageFrameTimeMs(gpuTime);

            displayedDraws   = drawCalls.LastValue;
            displayedBatches = batches.LastValue;
            displayedTris    = triangles.LastValue;
            displayedVerts   = vertices.LastValue;
            displayedSetPass = setPassCalls.LastValue;
        }
    }

    private void OnGUI()
    {
        EnsureStyle();

        float dpiScale = CalcDpiScale();
        float scale = dpiScale * uiScale;
        float btn = buttonSize * scale;
        float margin = buttonMargin * scale;

        // Кнопка тоггла — всегда в правом верхнем углу, независимо от видимости панели
        Rect btnRect = new Rect(Screen.width - btn - margin, margin, btn, btn);
        string btnLabel = showOverlay ? "×" : "FPS";
        if (GUI.Button(btnRect, btnLabel, buttonStyle))
            showOverlay = !showOverlay;

        if (!showOverlay) return;

        // "Saved by batching" = сколько draw call-ов убил SRP Batcher / static batching.
        // Когда батчинг работает, draws ≥ batches, разница = экономия.
        long savedByBatching = Mathf.Max(0, (int)(displayedDraws - displayedBatches));

        sb.Clear();
        sb.Append("FPS: ").AppendLine(displayedFps.ToString("F1"));
        sb.Append("CPU:        ").Append(displayedCpuMs.ToString("F2")).AppendLine(" ms");
        sb.Append("Render thr: ").Append(displayedRenderMs.ToString("F2")).AppendLine(" ms");
        sb.Append("GPU:        ").Append(displayedGpuMs.ToString("F2")).AppendLine(" ms");
        sb.AppendLine();
        sb.Append("Tris:   ").AppendLine(FormatCount(displayedTris));
        sb.Append("Verts:  ").AppendLine(FormatCount(displayedVerts));
        sb.AppendLine();
        sb.Append("Draw Calls:     ").AppendLine(displayedDraws.ToString());
        sb.Append("Batches:        ").AppendLine(displayedBatches.ToString());
        sb.Append("Saved by batch: ").AppendLine(savedByBatching.ToString());
        sb.Append("SetPass:        ").AppendLine(displayedSetPass.ToString());

        // Grass debug — показывает что происходит с InstancedGrassRenderer в рантайме.
        // Убрать после того как трава поднимется на мобилке.
        var grass = FindObjectOfType<InstancedGrassRenderer>();
        if (grass != null)
        {
            sb.AppendLine();
            sb.Append("Grass inst: ").AppendLine(grass.InstanceCount.ToString());
            sb.Append("Grass chunks: ").Append(grass.visibleChunks.ToString()).Append("/").AppendLine(grass.totalChunks.ToString());
            sb.Append("Grass visible: ").AppendLine(grass.visibleInstances.ToString());
            sb.Append("Mesh: ").AppendLine(grass.grassMesh != null ? grass.grassMesh.name : "NULL");
            sb.Append("Mat: ").AppendLine(grass.grassMaterial != null ? grass.grassMaterial.name : "NULL");
            sb.Append("Shader: ").Append(grass.grassMaterial != null && grass.grassMaterial.shader != null
                ? grass.grassMaterial.shader.name : "NULL");
        }

        float width = Mathf.Min(280f * scale, Screen.width * maxWidthRatio);
        float lineH = (fontSize + 4) * scale;
        float height = 22 * lineH;
        // Смещаем панель под кнопкой тоггла
        Rect rect = new Rect(Screen.width - width - margin, btn + margin * 2f, width, height);

        GUI.DrawTexture(rect, bgTexture, ScaleMode.StretchToFill);
        GUI.Label(rect, sb.ToString(), textStyle);
    }

    // Мягкая DPI-кривая: 160 dpi (lowend) → 1.0×, 440 dpi (Pixel 5) → ~1.5×, 550+ dpi → ~1.7×.
    // Не даём UI разрастаться в 2-3 раза на high-DPI — вместо линейной зависимости sqrt,
    // и потом ещё cap сверху. Так же, пользователь может добивать вручную через uiScale.
    private static float CalcDpiScale()
    {
        float dpi = Screen.dpi > 0 ? Screen.dpi : 96f;
        float raw = dpi / 160f;
        float softened = Mathf.Sqrt(Mathf.Max(raw, 1f));
        return Mathf.Clamp(softened, 1f, 1.8f);
    }

    private void EnsureStyle()
    {
        float dpiScale = CalcDpiScale();
        float scale = dpiScale * uiScale;

        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label);
            textStyle.alignment = TextAnchor.UpperLeft;
            textStyle.padding = new RectOffset(10, 10, 6, 6);
            textStyle.richText = false;
        }
        textStyle.fontSize = Mathf.RoundToInt(fontSize * scale);
        textStyle.normal.textColor = textColor;

        if (buttonStyle == null)
        {
            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.alignment = TextAnchor.MiddleCenter;
            buttonStyle.fontStyle = FontStyle.Bold;
        }
        buttonStyle.fontSize = Mathf.RoundToInt(fontSize * 1.3f * scale);

        if (bgTexture == null)
        {
            bgTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            bgTexture.SetPixel(0, 0, backgroundColor);
            bgTexture.Apply();
            bgTexture.hideFlags = HideFlags.HideAndDontSave;
        }
    }

    // Усредняем последние сэмплы рекордера из наносекунд → миллисекунды
    private static double GetAverageFrameTimeMs(ProfilerRecorder recorder)
    {
        if (!recorder.Valid) return 0;
        int count = recorder.Count;
        if (count == 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += recorder.GetSample(i).Value;
        return (sum / count) * 1e-6; // ns → ms
    }

    private static string FormatCount(long v)
    {
        if (v >= 1_000_000) return (v / 1_000_000f).ToString("F2") + "M";
        if (v >= 1_000)     return (v / 1_000f).ToString("F1") + "K";
        return v.ToString();
    }
}
