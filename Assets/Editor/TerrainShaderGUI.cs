using UnityEditor;
using UnityEngine;

public class TerrainShaderGUI : ShaderGUI
{
    private bool showTextures = true;
    private bool showTiling = true;
    private bool showBlend = true;

    public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
    {
        Material mat = materialEditor.target as Material;

        // === Текстуры ===
        showTextures = EditorGUILayout.Foldout(showTextures, "Textures", true, EditorStyles.foldoutHeader);
        if (showTextures)
        {
            EditorGUI.indentLevel++;
            MaterialProperty albedoArray = FindProperty("_TerrainAlbedoArray", properties);
            MaterialProperty normalArray = FindProperty("_TerrainNormalArray", properties);
            MaterialProperty splatMap = FindProperty("_SplatMap", properties);

            // Без тайлинга/оффсета — только слот текстуры
            materialEditor.TexturePropertySingleLine(
                new GUIContent("Albedo Array", "RGB=Albedo, A=Height"),
                albedoArray);
            materialEditor.TexturePropertySingleLine(
                new GUIContent("Normal Array", "RG=Normal, B=Roughness, A=AO"),
                normalArray);
            materialEditor.TexturePropertySingleLine(
                new GUIContent("Splat Map", "R=Layer1, G=Layer2, B=Layer3, A=Layer4"),
                splatMap);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);

        // === Per Layer Tiling ===
        showTiling = EditorGUILayout.Foldout(showTiling, "Per Layer Tiling", true, EditorStyles.foldoutHeader);
        if (showTiling)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_Tiling0", properties), "Layer 0 — Base");
            materialEditor.ShaderProperty(FindProperty("_Tiling1", properties), "Layer 1 — Splat R");
            materialEditor.ShaderProperty(FindProperty("_Tiling2", properties), "Layer 2 — Splat G");
            materialEditor.ShaderProperty(FindProperty("_Tiling3", properties), "Layer 3 — Splat B");
            materialEditor.ShaderProperty(FindProperty("_Tiling4", properties), "Layer 4 — Splat A");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);

        // === Blend Settings ===
        showBlend = EditorGUILayout.Foldout(showBlend, "Blend Settings", true, EditorStyles.foldoutHeader);
        if (showBlend)
        {
            EditorGUI.indentLevel++;
            materialEditor.ShaderProperty(FindProperty("_BlendSharpness", properties), "Height Blend Sharpness");
            materialEditor.ShaderProperty(FindProperty("_NormalStrength", properties), "Normal Strength");
            materialEditor.ShaderProperty(FindProperty("_TintStrength", properties), "Vertex RGB Tint");
            materialEditor.ShaderProperty(FindProperty("_NoiseStrength", properties), "Vertex Alpha Noise");
            materialEditor.ShaderProperty(FindProperty("_SmoothnessScale", properties), "Smoothness Scale");
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(10);
        materialEditor.RenderQueueField();
        materialEditor.DoubleSidedGIField();
    }
}
