using UnityEngine;
using UnityEditor;

    public class ReplaceSelection : EditorWindow
    {
        static GameObject replacement = null;
        static bool keep = false;

        [MenuItem("Tools/Editor Tools/Scene Setup/Replace Selection", false, 300)]
        public static void ShowWindow()
        {
            GetWindow<ReplaceSelection>(false, "Replace", true);
        }

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Replacement", GUILayout.MaxWidth(100));
            var temp = EditorGUILayout.ObjectField(replacement, typeof (GameObject), true, GUILayout.MinWidth(200));
            replacement = (GameObject) temp;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Keep", GUILayout.MaxWidth(100));
            var bl = EditorGUILayout.Toggle(keep);
            keep = bl;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Replace Selection", GUILayout.Height(40), GUILayout.Width(200)))
            {
                OnWizardCreate();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        void OnWizardCreate()
        {
            if (replacement == null)
                return;

            Transform[] transforms = Selection.GetTransforms(SelectionMode.TopLevel | SelectionMode.Editable);

            foreach (Transform t in transforms)
            {
                GameObject g;
                var pref = PrefabUtility.GetPrefabAssetType(replacement);

                g = (GameObject) PrefabUtility.InstantiatePrefab(replacement);
                g.transform.parent = t.parent;
                g.name = replacement.name;
                g.transform.localPosition = t.localPosition;
                g.transform.localScale = t.localScale;
                g.transform.localRotation = t.localRotation;

                Undo.RegisterCreatedObjectUndo(g, "Replace Selection");
            }

            if (!keep)
            {
                foreach (GameObject g in Selection.gameObjects)
                {
                    Undo.DestroyObjectImmediate(g);
                }
            }
            Undo.IncrementCurrentGroup(); 
        }
    }