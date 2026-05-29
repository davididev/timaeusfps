#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;


namespace Bekkoloco
{

    [InitializeOnLoad]
    public static class QuickTilemapEditorPlaymodeListener
    {
        static QuickTilemapEditorPlaymodeListener()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                var editor = Object.FindFirstObjectByType<QuickTilemapEditor>();
                if (editor != null)
                {
                    editor.UpdateBlendPreviewMaterial();
                }
            }
        }
    }
#endif

}
