using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(InstancedGrassRenderer))]
public class InstancedGrassRendererEditor : Editor
{
    // Скрываем Source поля — они заполняются автоматически при Bake
    private static readonly string[] hiddenFields = { "m_Script", "grassMesh", "grassMaterial", "instanceMatrices" };

    // Кеш суб-рендереров — пересобирается только когда childCount изменился.
    // Для парента с 24К скатерных GameObject'ов это спасает от 4 проходов GetComponent за каждый OnInspectorGUI.
    private List<InstancedGrassRenderer> cachedSubs = new List<InstancedGrassRenderer>();
    private bool cachedHasScattered;
    private int cachedChildCount = -1;

    private void RefreshChildCacheIfNeeded(InstancedGrassRenderer renderer)
    {
        int count = renderer.transform.childCount;
        if (count == cachedChildCount) return;
        cachedChildCount = count;
        cachedSubs.Clear();
        cachedHasScattered = false;
        for (int i = 0; i < count; i++)
        {
            var sub = renderer.transform.GetChild(i).GetComponent<InstancedGrassRenderer>();
            if (sub != null) cachedSubs.Add(sub);
            else cachedHasScattered = true;
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        // Рисуем только нужные поля (без Source mesh/material и без raw matrices)
        SerializedProperty prop = serializedObject.GetIterator();
        prop.NextVisible(true); // skip m_Script
        while (prop.NextVisible(false))
        {
            bool skip = false;
            foreach (var h in hiddenFields)
                if (prop.name == h) { skip = true; break; }
            if (!skip)
                EditorGUILayout.PropertyField(prop, true);
        }

        serializedObject.ApplyModifiedProperties();

        InstancedGrassRenderer renderer = (InstancedGrassRenderer)target;

        RefreshChildCacheIfNeeded(renderer);

        bool hasBaked = renderer.InstanceCount > 0 || cachedSubs.Count > 0;
        bool hasChildren = cachedHasScattered;

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Stats", EditorStyles.boldLabel);

        // Статистика — суммируем только по кешированным суб-рендерерам (без прохода по всем детям)
        int totalInstances = renderer.InstanceCount;
        int totalVisible = renderer.visibleInstances;
        int totalVisibleChunks = renderer.visibleChunks;
        int totalAllChunks = renderer.totalChunks;

        for (int i = 0; i < cachedSubs.Count; i++)
        {
            var sub = cachedSubs[i];
            totalInstances += sub.InstanceCount;
            totalVisible += sub.visibleInstances;
            totalVisibleChunks += sub.visibleChunks;
            totalAllChunks += sub.totalChunks;
        }

        EditorGUILayout.LabelField($"Instances: {totalInstances}");
        EditorGUILayout.LabelField($"Chunks: {totalVisibleChunks} / {totalAllChunks} visible");
        EditorGUILayout.LabelField($"Visible Instances: {totalVisible}");
        EditorGUILayout.LabelField($"Draw Calls: {Mathf.CeilToInt(totalVisible / 1023f)}");

        // Детализация по суб-рендерерам — только из кеша
        if (cachedSubs.Count > 0)
        {
            EditorGUILayout.Space(3);
            for (int i = 0; i < cachedSubs.Count; i++)
            {
                var sub = cachedSubs[i];
                EditorGUILayout.LabelField($"  {sub.grassMesh?.name}: {sub.InstanceCount} inst, {sub.visibleChunks}/{sub.totalChunks} chunks");
            }
        }

        if (totalInstances == 0)
            EditorGUILayout.LabelField("No baked data. Scatter then Bake.");

        // Предупреждение — много скатерных GameObject'ов дают огромный CPU cost в редакторе
        // (selection outline + hierarchy draw для каждого ребёнка).
        if (hasChildren && !hasBaked && cachedChildCount > 500)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                $"{cachedChildCount} scattered children — editor will lag until you Bake.\n" +
                "Editor selection outline рисуется для каждого ребёнка — это Unity overhead, не скрипт.",
                MessageType.Warning);
        }

        EditorGUILayout.Space(5);

        // Bake
        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        GUI.enabled = hasChildren;
        if (GUILayout.Button("Bake from Children", GUILayout.Height(35)))
        {
            Undo.RegisterCompleteObjectUndo(renderer, "Bake Grass");
            renderer.BakeFromChildren();
            EditorUtility.SetDirty(renderer);
        }
        GUI.enabled = true;
        GUI.backgroundColor = Color.white;

        // Rebake probes
        EditorGUILayout.Space(3);
        GUI.enabled = hasBaked;
        if (GUILayout.Button("Rebake Light Probes", GUILayout.Height(25)))
        {
            renderer.ForceRebakeProbes();
            for (int i = 0; i < cachedSubs.Count; i++)
                cachedSubs[i].ForceRebakeProbes();
            EditorUtility.SetDirty(renderer);
        }
        GUI.enabled = true;

        // Unbake
        EditorGUILayout.Space(3);
        GUI.backgroundColor = new Color(0.8f, 0.8f, 0.4f);
        GUI.enabled = hasBaked || hasChildren;
        if (GUILayout.Button("Unbake (edit mode)", GUILayout.Height(28)))
        {
            Undo.RegisterCompleteObjectUndo(renderer, "Unbake Grass");
            renderer.Unbake();
            EditorUtility.SetDirty(renderer);
        }
        GUI.enabled = true;
        GUI.backgroundColor = Color.white;

        // Delete children
        if (hasChildren && hasBaked)
        {
            EditorGUILayout.Space(3);
            GUI.backgroundColor = new Color(1f, 0.5f, 0.3f);
            if (GUILayout.Button("Delete Hidden Children", GUILayout.Height(22)))
            {
                if (EditorUtility.DisplayDialog("Delete", "Delete hidden children permanently?", "Delete", "Cancel"))
                {
                    renderer.DeleteChildren();
                    EditorUtility.SetDirty(renderer);
                }
            }
            GUI.backgroundColor = Color.white;
        }

        // Clear
        EditorGUILayout.Space(3);
        GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
        GUI.enabled = hasBaked;
        if (GUILayout.Button("Clear All", GUILayout.Height(22)))
        {
            if (EditorUtility.DisplayDialog("Clear", "Remove all instance data?", "Clear", "Cancel"))
            {
                renderer.ClearAll();
                EditorUtility.SetDirty(renderer);
            }
        }
        GUI.enabled = true;
        GUI.backgroundColor = Color.white;

        // Repaint только когда есть что показывать в stats — pre-bake без суб-рендереров
        // никаких динамических данных не меняется, Repaint спамил бы иерархию зря.
        if (hasBaked) Repaint();
    }
}
