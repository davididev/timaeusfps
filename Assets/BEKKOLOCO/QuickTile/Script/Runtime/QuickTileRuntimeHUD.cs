// QuickTileRuntimeHUD.cs
// Minimal IMGUI debug panel for the runtime painter. Shows mode, active rule
// indices, brush controls, and save/load. Intended as a starter UI — replace
// with uGUI or UI Toolkit once the feature is validated in Play Mode.

using UnityEngine;

namespace Bekkoloco
{
    [DisallowMultipleComponent]
    public class QuickTileRuntimeHUD : MonoBehaviour
    {
        [Header("Refs")]
        public QuickTileRuntimePainter painter;
        public QuickTileRuntimeSaveLoad saveLoad;
        public QuickTileRuntimeInput input;

        [Header("Panel")]
        public Rect panel = new Rect(12, 12, 300, 360);
        public bool visible = true;
        public KeyCode toggleKey = KeyCode.F1;

        string levelNameField = "Level01";
        Vector2 scroll;

        void Reset()
        {
            painter  = GetComponent<QuickTileRuntimePainter>();
            saveLoad = GetComponent<QuickTileRuntimeSaveLoad>();
            input    = GetComponent<QuickTileRuntimeInput>();
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey)) visible = !visible;
        }

        void OnGUI()
        {
            if (!visible || painter == null || !painter.IsReady) return;
            panel = GUILayout.Window(GetInstanceID(), panel, DrawWindow, "QuickTile Runtime");
        }

        void DrawWindow(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("Mode");
            painter.mode = (QuickTileRuntimePainter.PaintMode)GUILayout.SelectionGrid(
                (int)painter.mode,
                new[] { "Tile", "EraseTile", "Texture", "EraseTex", "Object", "EraseObj" },
                3);

            GUILayout.Space(6);

            int tileCount = painter.editor.tileRules?.Count ?? 0;
            int texCount  = painter.editor.texturePaintRules?.Count ?? 0;
            int goCount   = painter.editor.gameObjectRules?.Count ?? 0;

            painter.activeTileRuleIndex       = IntSlider("Tile rule",    painter.activeTileRuleIndex,       0, Mathf.Max(0, tileCount - 1));
            painter.activeTextureRuleIndex    = IntSlider("Texture rule", painter.activeTextureRuleIndex,    0, Mathf.Max(0, texCount  - 1));
            painter.activeGameObjectRuleIndex = IntSlider("Object rule",  painter.activeGameObjectRuleIndex, 0, Mathf.Max(0, goCount   - 1));

            GUILayout.Space(6);
            GUILayout.Label($"Brush size: {painter.brushSize}");
            painter.brushSize = Mathf.RoundToInt(GUILayout.HorizontalSlider(painter.brushSize, 1, 16));
            painter.shape = GUILayout.Toggle(painter.shape == QuickTileRuntimePainter.BrushShape.Circle, " Circle")
                ? QuickTileRuntimePainter.BrushShape.Circle
                : QuickTileRuntimePainter.BrushShape.Square;
            painter.eraseAllHeights = GUILayout.Toggle(painter.eraseAllHeights, " Erase all heights");

            if (input != null)
                input.showPreview = GUILayout.Toggle(input.showPreview, " Show preview");

            GUILayout.Space(8);
            GUILayout.Label("Level I/O");
            levelNameField = GUILayout.TextField(levelNameField ?? "");

            GUILayout.BeginHorizontal();
            GUI.enabled = saveLoad != null && !string.IsNullOrWhiteSpace(levelNameField);
            if (GUILayout.Button("Save")) saveLoad.Save(levelNameField);
            if (GUILayout.Button("Load")) saveLoad.Load(levelNameField);
            if (GUILayout.Button("Delete")) saveLoad.Delete(levelNameField);
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (saveLoad != null)
            {
                GUILayout.Label("Existing:");
                foreach (var name in saveLoad.ListLevels())
                {
                    if (GUILayout.Button(name, GUI.skin.label))
                        levelNameField = name;
                }
            }

            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        static int IntSlider(string label, int value, int min, int max)
        {
            GUILayout.Label($"{label}: {value} / {max}");
            return Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
        }
    }
}
