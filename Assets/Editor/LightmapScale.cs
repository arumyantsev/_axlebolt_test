using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class LightmapScale
{
    public static float multiplier;
    static List<Renderer> childRenderers;
    static SerializedObject serializedRenderer;
    static SerializedProperty lightmapScaleProperty;

    static void EditObjectsLightmapScale(GameObject currentPrefab)
    {
        childRenderers = new List<Renderer>();
        currentPrefab.transform.GetComponentsInChildren(true, childRenderers);

        foreach (var child in childRenderers)
        {
            if (child.name.ToLower().Contains("clip")) // skip current object if it's a clip (by default all clips have proper naming)
                continue;

            serializedRenderer = new SerializedObject(child);

            if (serializedRenderer.FindProperty("m_ReceiveGI").intValue == 2) // skip object if ReceiveGI is set to LightProbes and not Lightmaps
                continue;

            lightmapScaleProperty = serializedRenderer.FindProperty("m_ScaleInLightmap");
            lightmapScaleProperty.floatValue *= multiplier;

            if (serializedRenderer.ApplyModifiedProperties())
                Debug.Log($"Lightmap scale was multiplied by {multiplier} for {child.name} object");
        }
    }
    public static void MultiplyLightmapScale()
    {
        if (Selection.gameObjects.Length == 1)
            EditObjectsLightmapScale(Selection.activeGameObject);
        else
            foreach (var child in Selection.gameObjects)
                EditObjectsLightmapScale(child);
    }
}
