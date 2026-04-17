using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Per-instance Light Probes через StructuredBuffer + DrawMeshInstancedIndirect.
/// Chunk-based frustum + distance culling.
///
/// Использует FoliageInstanced.shader (не обычный Foliage).
/// Foliage.shader остаётся для деревьев (static, lightmap).
/// </summary>
[ExecuteInEditMode]
public class InstancedGrassRenderer : MonoBehaviour
{
    [Header("Rendering")]
    public ShadowCastingMode castShadows = ShadowCastingMode.Off;
    public bool receiveShadows = true;

    [Header("Culling")]
    public float maxDistance = 80f;
    public float chunkSize = 16f;

    [HideInInspector] public Mesh grassMesh;
    [HideInInspector] public Material grassMaterial; // должен быть FoliageInstanced

    // Per-instance data struct (must match shader)
    [StructLayout(LayoutKind.Sequential)]
    private struct GrassInstanceData
    {
        public Matrix4x4 objectToWorld;  // 64 bytes
        public Vector4 probeColor;       // 16 bytes
        public Vector4 occlusion;        // 16 bytes
    }
    private static readonly int INSTANCE_STRIDE = 64 + 16 + 16; // 96 bytes

    // Serialized raw data
    [SerializeField, HideInInspector] private List<Matrix4x4> instanceMatrices = new List<Matrix4x4>();
    [SerializeField, HideInInspector] private List<Vector4> instanceProbeColors = new List<Vector4>();
    [SerializeField, HideInInspector] private List<Vector4> instanceOcclusions = new List<Vector4>();

    // GPU resources per chunk
    private struct Chunk
    {
        public Bounds bounds;
        public ComputeBuffer instanceBuffer;
        public ComputeBuffer argsBuffer;
        public int count;
        public MaterialPropertyBlock mpb; // для SetBuffer
    }

    private List<Chunk> chunks;
    private bool isDirty = true;

    // Stats
    [System.NonSerialized] public int visibleChunks;
    [System.NonSerialized] public int totalChunks;
    [System.NonSerialized] public int visibleInstances;

    private void OnEnable() { isDirty = true; }
    // OnValidate НЕ ставит isDirty — culling settings не требуют rebuild GPU buffers!
    // Rebuild происходит только при Bake/Unbake/Clear или при первом OnEnable.
    private void OnDisable() { ReleaseBuffers(); }
    private void OnDestroy() { ReleaseBuffers(); }

    private void Update()
    {
        if (grassMesh == null || grassMaterial == null || instanceMatrices.Count == 0) return;

        // Rebuild только если реально нужно (isDirty + buffers не созданы)
        if (isDirty || chunks == null)
        {
            RebuildChunks();
            isDirty = false;
        }

        if (chunks == null || chunks.Count == 0) return;

        Camera cam = null;
        #if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            var sv = UnityEditor.SceneView.lastActiveSceneView;
            if (sv != null) cam = sv.camera;
        }
        else cam = Camera.main;
        #else
        cam = Camera.main;
        #endif
        if (cam == null) return;

        Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(cam);
        Vector3 camPos = cam.transform.position;
        float maxDist2 = maxDistance * maxDistance;

        visibleChunks = 0;
        visibleInstances = 0;
        totalChunks = chunks.Count;

        for (int c = 0; c < chunks.Count; c++)
        {
            var chunk = chunks[c];
            if ((chunk.bounds.center - camPos).sqrMagnitude > maxDist2) continue;
            if (!GeometryUtility.TestPlanesAABB(frustum, chunk.bounds)) continue;

            visibleChunks++;
            visibleInstances += chunk.count;

            Graphics.DrawMeshInstancedIndirect(
                grassMesh, 0, grassMaterial,
                chunk.bounds,
                chunk.argsBuffer,
                0,
                chunk.mpb,
                castShadows,
                receiveShadows,
                gameObject.layer
            );
        }
    }

    // ==================== Build ====================

    private void RebuildChunks()
    {
        ReleaseBuffers();
        chunks = new List<Chunk>();
        if (instanceMatrices.Count == 0) return;

        // Ensure probe data exists
        while (instanceProbeColors.Count < instanceMatrices.Count)
            instanceProbeColors.Add(new Vector4(0.5f, 0.5f, 0.5f, 1));
        while (instanceOcclusions.Count < instanceMatrices.Count)
            instanceOcclusions.Add(Vector4.one);

        float cs = Mathf.Max(chunkSize, 1f);

        Vector3 globalMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        foreach (var m in instanceMatrices)
        {
            Vector3 pos = new Vector3(m.m03, m.m13, m.m23);
            globalMin = Vector3.Min(globalMin, pos);
        }

        // Group by chunk
        var chunkMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < instanceMatrices.Count; i++)
        {
            var m = instanceMatrices[i];
            int cx = Mathf.FloorToInt((m.m03 - globalMin.x) / cs);
            int cz = Mathf.FloorToInt((m.m23 - globalMin.z) / cs);
            int key = cx * 10000 + cz;
            if (!chunkMap.ContainsKey(key))
                chunkMap[key] = new List<int>();
            chunkMap[key].Add(i);
        }

        float pad = grassMesh != null ? grassMesh.bounds.size.magnitude : 2f;

        foreach (var kvp in chunkMap)
        {
            var indices = kvp.Value;
            int count = indices.Count;

            // Bounds
            Vector3 cMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 cMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (int i in indices)
            {
                var m = instanceMatrices[i];
                Vector3 pos = new Vector3(m.m03, m.m13, m.m23);
                cMin = Vector3.Min(cMin, pos);
                cMax = Vector3.Max(cMax, pos);
            }
            cMin -= Vector3.one * pad;
            cMax += Vector3.one * pad;
            Bounds bounds = new Bounds();
            bounds.SetMinMax(cMin, cMax);

            // Build GPU data
            var gpuData = new GrassInstanceData[count];
            for (int j = 0; j < count; j++)
            {
                int idx = indices[j];
                gpuData[j].objectToWorld = instanceMatrices[idx];
                gpuData[j].probeColor = instanceProbeColors[idx];
                gpuData[j].occlusion = instanceOcclusions[idx];
            }

            // ComputeBuffer
            var instanceBuffer = new ComputeBuffer(count, INSTANCE_STRIDE);
            instanceBuffer.SetData(gpuData);

            // Args buffer: [indexCount, instanceCount, startIndex, baseVertex, startInstance]
            var argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            uint[] args = { grassMesh.GetIndexCount(0), (uint)count, 0, 0, 0 };
            argsBuffer.SetData(args);

            // MPB с buffer reference
            var mpb = new MaterialPropertyBlock();
            mpb.SetBuffer("_InstanceBuffer", instanceBuffer);

            chunks.Add(new Chunk
            {
                bounds = bounds,
                instanceBuffer = instanceBuffer,
                argsBuffer = argsBuffer,
                count = count,
                mpb = mpb
            });
        }
    }

    private void ReleaseBuffers()
    {
        if (chunks == null) return;
        foreach (var chunk in chunks)
        {
            chunk.instanceBuffer?.Release();
            chunk.argsBuffer?.Release();
        }
        chunks = null;
    }

    // ==================== Light Probes ====================

    public void ForceRebakeProbes()
    {
        if (instanceMatrices.Count == 0) return;
        BakePerInstanceProbes();
        isDirty = true; // trigger RebuildChunks to update GPU buffers
        Debug.Log($"[InstancedGrassRenderer] Rebaked probes for {instanceMatrices.Count} instances");
    }

    private void BakePerInstanceProbes()
    {
        instanceProbeColors.Clear();
        instanceOcclusions.Clear();

        int count = instanceMatrices.Count;
        Vector3[] positions = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            var m = instanceMatrices[i];
            positions[i] = new Vector3(m.m03, m.m13, m.m23);
        }

        SphericalHarmonicsL2[] shs = new SphericalHarmonicsL2[count];
        Vector4[] occlusions = new Vector4[count];
        LightProbes.CalculateInterpolatedLightAndOcclusionProbes(positions, shs, occlusions);

        Vector3[] upDir = { Vector3.up };
        Color[] result = new Color[1];

        for (int i = 0; i < count; i++)
        {
            shs[i].Evaluate(upDir, result);
            instanceProbeColors.Add(new Vector4(result[0].r, result[0].g, result[0].b, 1));
            instanceOcclusions.Add(occlusions[i]);
        }
    }

    // ==================== Bake / Unbake ====================

    public void BakeFromChildren()
    {
        instanceMatrices.Clear();
        instanceProbeColors.Clear();
        instanceOcclusions.Clear();

        MeshFilter[] filters = GetComponentsInChildren<MeshFilter>(true);

        var groups = new Dictionary<string, List<MeshFilter>>();
        foreach (var mf in filters)
        {
            if (mf.gameObject == gameObject) continue;
            if (mf.GetComponent<InstancedGrassRenderer>() != null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mf.sharedMesh == null || mr == null || mr.sharedMaterial == null) continue;

            string key = mf.sharedMesh.GetInstanceID() + "_" + mr.sharedMaterial.GetInstanceID();
            if (!groups.ContainsKey(key))
                groups[key] = new List<MeshFilter>();
            groups[key].Add(mf);
        }

        if (groups.Count == 0)
        {
            Debug.LogWarning("[InstancedGrassRenderer] No valid children found!");
            return;
        }

        // Удаляем старые суб-рендереры
        var oldSubs = new List<GameObject>();
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child.GetComponent<InstancedGrassRenderer>() != null)
                oldSubs.Add(child.gameObject);
        }
        foreach (var old in oldSubs)
        {
            #if UNITY_EDITOR
            DestroyImmediate(old);
            #else
            Destroy(old);
            #endif
        }

        if (groups.Count == 1)
        {
            var pair = new List<List<MeshFilter>>(groups.Values)[0];
            grassMesh = pair[0].sharedMesh;

            var origMat = pair[0].GetComponent<MeshRenderer>().sharedMaterial;
            grassMaterial = CreateInstancedMaterial(origMat);

            foreach (var mf in pair)
                instanceMatrices.Add(mf.transform.localToWorldMatrix);

            BakePerInstanceProbes();
        }
        else
        {
            int groupIdx = 0;
            foreach (var kvp in groups)
            {
                var meshFilters = kvp.Value;
                Mesh mesh = meshFilters[0].sharedMesh;

                GameObject subGo = new GameObject($"_instanced_{mesh.name}_{groupIdx}");
                subGo.transform.SetParent(transform, false);

                var sub = subGo.AddComponent<InstancedGrassRenderer>();
                sub.grassMesh = mesh;
                sub.castShadows = castShadows;
                sub.receiveShadows = receiveShadows;
                sub.maxDistance = maxDistance;
                sub.chunkSize = chunkSize;

                var origMat = meshFilters[0].GetComponent<MeshRenderer>().sharedMaterial;
                sub.grassMaterial = CreateInstancedMaterial(origMat);

                foreach (var mf in meshFilters)
                    sub.instanceMatrices.Add(mf.transform.localToWorldMatrix);

                sub.BakePerInstanceProbes();
                groupIdx++;
            }
            instanceMatrices.Clear();
            grassMesh = null;
            grassMaterial = null;
        }

        // Деактивируем скатеренные объекты
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child.GetComponent<InstancedGrassRenderer>() == null)
                child.gameObject.SetActive(false);
        }

        isDirty = true;
    }

    public void Unbake()
    {
        var subs = new List<GameObject>();
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child.GetComponent<InstancedGrassRenderer>() != null)
                subs.Add(child.gameObject);
        }
        foreach (var sub in subs)
        {
            #if UNITY_EDITOR
            DestroyImmediate(sub);
            #else
            Destroy(sub);
            #endif
        }

        if (transform.childCount > 0)
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                var child = transform.GetChild(i).gameObject;
                child.SetActive(true);
                var mr = child.GetComponent<MeshRenderer>();
                if (mr != null) mr.enabled = true;
            }
        }
        else if (instanceMatrices.Count > 0 && grassMesh != null)
        {
            for (int i = 0; i < instanceMatrices.Count; i++)
            {
                var m = instanceMatrices[i];
                GameObject go = new GameObject($"grass_{i}");
                go.transform.SetParent(transform, false);
                go.transform.position = new Vector3(m.m03, m.m13, m.m23);
                go.transform.rotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = grassMesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = grassMaterial;
                mr.shadowCastingMode = castShadows;
                mr.receiveShadows = receiveShadows;
            }
        }

        instanceMatrices.Clear();
        instanceProbeColors.Clear();
        instanceOcclusions.Clear();
        ReleaseBuffers();
        isDirty = true;
    }

    public void DeleteChildren()
    {
        // Удаляем только scatter children (деактивированные), но НЕ суб-рендереры
        var toDelete = new List<GameObject>();
        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i);
            if (child.GetComponent<InstancedGrassRenderer>() != null) continue; // skip sub-renderers
            toDelete.Add(child.gameObject);
        }

        #if UNITY_EDITOR
        foreach (var child in toDelete) DestroyImmediate(child);
        #else
        foreach (var child in toDelete) Destroy(child);
        #endif

        // Также в суб-рендерерах удаляем их children (scatter objects)
        for (int i = 0; i < transform.childCount; i++)
        {
            var sub = transform.GetChild(i).GetComponent<InstancedGrassRenderer>();
            if (sub != null) sub.DeleteChildren();
        }
    }

    public void ClearAll()
    {
        instanceMatrices.Clear();
        instanceProbeColors.Clear();
        instanceOcclusions.Clear();
        ReleaseBuffers();
        isDirty = true;
    }

    public int InstanceCount => instanceMatrices.Count;

    // ==================== Helpers ====================

    private Material CreateInstancedMaterial(Material original)
    {
        var shader = Shader.Find("Axlebolt/FoliageInstanced");
        if (shader == null)
        {
            Debug.LogWarning("[InstancedGrassRenderer] Shader 'Axlebolt/FoliageInstanced' not found!");
            return original;
        }

        var mat = new Material(shader);
        mat.name = original.name + "_Instanced";

        // Копируем ВСЕ shared properties
        string[] texProps = { "_Albedo", "_Normal" };
        foreach (var p in texProps)
            if (original.HasTexture(p)) mat.SetTexture(p, original.GetTexture(p));

        string[] colorProps = { "_BaseColor" };
        foreach (var p in colorProps)
            if (original.HasColor(p)) mat.SetColor(p, original.GetColor(p));

        string[] floatProps = {
            "_NormalStrength", "_Cutoff",
            "_Hue", "_Saturation", "_Brightness",
            "_TranslucentPower",
            "_SwaySpeed", "_SwayStrength", "_FlutterSpeed", "_FlutterStrength",
            "_SmoothnessScale", "_AOStrength"
        };
        foreach (var p in floatProps)
            if (original.HasFloat(p)) mat.SetFloat(p, original.GetFloat(p));

        return mat;
    }
}
