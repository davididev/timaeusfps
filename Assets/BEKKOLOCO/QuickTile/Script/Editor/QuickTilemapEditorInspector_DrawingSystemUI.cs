using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        // V2 Grid state
        private VisualElement gridCanvasV2;
        private VisualElement gridBackgroundsContainerV2;
        private VisualElement gridCellsContainer;
        private VisualElement gridTileVisualsContainerV2;
        private VisualElement gridHiddenTileOutlinesContainerV2;
        private VisualElement gridPathLinesContainerV2;
        private VisualElement selectionStatusBadgeV2;
        private Label selectionStatusLabelV2;
        private Toggle drawModeToggleV2;
        private Vector2 gridViewOffsetV2 = Vector2.zero;
        private bool isDrawingV2 = false;
        private Dictionary<Vector2Int, VisualElement> cellElementsV2 = new Dictionary<Vector2Int, VisualElement>();
        
        // V2 Scene View Dragging State
        private bool isDraggingSelectionV2 = false;
        private Vector3Int dragStartMouseCellV2;
        private Vector3Int selectionOffsetV2;
        private float brushRotation = 0f; // V2 Rotation
        private bool pickerToolActive = false; // V2 Picker
        private bool hasActiveBrushStrokeV2 = false;
        private bool hasLastInteractedCellV2 = false;
        private Vector3Int lastInteractedCellV2;
        private bool isBoxSelectingV2 = false;
        private Vector3Int selectionStartCellV2;
        private bool hasHoveredCellV2 = false;
        private Vector3Int hoveredCellV2;
        private bool hasLastPickerCell = false;
        private Vector3Int lastPickerCell;
        private int pickerCycleIndex = -1;
        private string lastPickerSignature = string.Empty;

        private enum PickerCandidateKind
        {
            Texture,
            Tile,
            GameObject,
            Path
        }

        private sealed class PickerCandidate
        {
            public PickerCandidateKind kind;
            public int ruleIndex = -1;
            public int pathIndex = -1;
            public TileBase tile;
            public QuickTilemapEditor.TileRule tileRule;
            public QuickTilemapEditor.TexturePaintRule textureRule;
            public QuickTilemapEditor.GameObjectRule gameObjectRule;
            public QuickTilemapEditor.Path path;
            public Color feedbackColor = Color.white;
            public string signature = string.Empty;
        }

        private void ResetPickerCycleState()
        {
            hasLastPickerCell = false;
            lastPickerCell = default;
            pickerCycleIndex = -1;
            lastPickerSignature = string.Empty;
        }

        /// <summary>
        /// Creates the drawing system section using the stable V1 grid only.
        /// </summary>
        private VisualElement CreateDrawingSystemSection_UIToolkit()
        {
            var container = new VisualElement();
            container.name = "drawing-system";
            container.style.marginTop = 8;
            container.style.marginBottom = 8;

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.marginBottom = 8;
            headerRow.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.1f, 1f));
            headerRow.style.paddingLeft = 8;
            headerRow.style.paddingRight = 8;
            headerRow.style.paddingTop = 6;
            headerRow.style.paddingBottom = 6;
            headerRow.style.borderLeftWidth = 3;
            headerRow.style.borderLeftColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));

            var titleLabel = new Label("✏️ Drawing System");
            titleLabel.style.fontSize = 16;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new StyleColor(new Color(0.5f, 0.8f, 1f));
            titleLabel.style.flexGrow = 1;
            headerRow.Add(titleLabel);

            var headerControls = new IMGUIContainer(() => {
                DrawGridHeaderControls();
            });
            headerControls.name = "drawing-header-controls";
            headerControls.style.width = 340;
            headerControls.style.flexShrink = 0;
            headerControls.style.marginLeft = 8;
            headerRow.Add(headerControls);

            container.Add(headerRow);

            var drawingSystemV1Container = new VisualElement();
            drawingSystemV1Container.name = "drawing-v1";
            
            var imguiGrid = new IMGUIContainer(() => {
                DrawTilemapGrid();
                DrawGridControls();
            });
            imguiGrid.name = "imgui-grid";
            imguiGrid.style.minHeight = 300;
            drawingSystemV1Container.Add(imguiGrid);
            container.Add(drawingSystemV1Container);
            return container;
        }

        /// <summary>
        /// Draws the compact grid view controls in the drawing header
        /// </summary>
        private void DrawGridHeaderControls()
        {
            if (tilemapEditor == null)
                return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            DrawGridPresetAndScaleControls(compact: true);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Draws the controls below the grid (for IMGUI V1)
        /// </summary>
        private void DrawGridControls()
        {
            if (tilemapEditor == null)
                return;

            DrawBrushFooterControls();

            if (tilemapEditor.useCustomSize)
                DrawCustomGridSizeControls();
        }

        private void DrawGridPresetAndScaleControls(bool compact)
        {
            string[] presetOptions = compact
                ? new[] { "⊞ 7x7", "⊞ 16x16", "⊞ 32x32", "⊞ 64x64", "⊞ Custom" }
                : new[] { "⊞ Grid Size 7x7", "⊞ Grid Size 16x16", "⊞ Grid Size 32x32", "⊞ Grid Size 64x64", "⊞ Custom Grid View" };

            int selectedPreset = EditorGUILayout.Popup(
                GetCurrentPresetIndex(tilemapEditor.gridSize),
                presetOptions,
                GUILayout.Width(compact ? 130f : 170f)
            );

            bool useCustomSize = tilemapEditor.useCustomSize;
            Vector3Int newGridSize = tilemapEditor.gridSize;
            switch (selectedPreset)
            {
                case 0: newGridSize = new Vector3Int(7, 7, 1); useCustomSize = false; break;
                case 1: newGridSize = new Vector3Int(16, 16, 1); useCustomSize = false; break;
                case 2: newGridSize = new Vector3Int(32, 32, 1); useCustomSize = false; break;
                case 3: newGridSize = new Vector3Int(64, 64, 1); useCustomSize = false; break;
                case 4: useCustomSize = true; break;
            }

            if (useCustomSize != tilemapEditor.useCustomSize)
            {
                Undo.RecordObject(tilemapEditor, "Toggle Custom Grid Size");
                tilemapEditor.useCustomSize = useCustomSize;
                EditorUtility.SetDirty(tilemapEditor);
                Repaint();
            }

            if (!useCustomSize && newGridSize != tilemapEditor.gridSize)
            {
                Undo.RecordObject(tilemapEditor, "Change Grid Size Preset");
                gridSizeProperty.vector3IntValue = newGridSize;
                serializedObject.ApplyModifiedProperties();
                if (newGridSize.x < 64 || newGridSize.y < 64)
                    tilemapEditor.ForceUpdateSmallGrid();
                EditorUtility.SetDirty(tilemapEditor);
                Repaint();
            }

            EditorGUILayout.LabelField("Scale", GUILayout.Width(compact ? 34f : 40f));
            int newScale = EditorGUILayout.IntPopup(
                tilemapEditor.gridScale,
                Enumerable.Range(1, 10).Select(i => $"x{i}").ToArray(),
                Enumerable.Range(1, 10).ToArray(),
                GUILayout.Width(60)
            );

            if (newScale != tilemapEditor.gridScale)
            {
                Undo.RecordObject(tilemapEditor, "Change Grid Scale");
                tilemapEditor.gridScale = newScale;
                tilemapEditor.ApplyGridScale();
                EditorUtility.SetDirty(tilemapEditor);
                Repaint();
            }

            // Grid style (3D / 2.5D)
            string[] styleLabels = { "3D", "2.5D (beta)" };
            int[] styleValues = { (int)QuickTilemapEditor.GridStyle.Mode3D, (int)QuickTilemapEditor.GridStyle.Mode2_5D };
            int newStyle = EditorGUILayout.IntPopup(
                (int)tilemapEditor.gridStyle,
                styleLabels,
                styleValues,
                GUILayout.Width(100)
            );
            if (newStyle != (int)tilemapEditor.gridStyle)
            {
                var grid = tilemapEditor.FindGrid();
                Undo.RecordObject(tilemapEditor, "Change Grid Style");
                if (grid != null) Undo.RecordObject(grid, "Change Grid Style");
                tilemapEditor.gridStyle = (QuickTilemapEditor.GridStyle)newStyle;
                tilemapEditor.ApplyGridStyleToGrid();

                // Force all procedural renderers to rebuild — the 3D and 2.5D pipelines
                // produce completely different geometry, so we can't leave stale meshes.
                try { tilemapEditor.RebuildAllProceduralMeshes(); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[QuickTile] RebuildAllProceduralMeshes failed after GridStyle change: {ex.Message}");
                }

                EditorUtility.SetDirty(tilemapEditor);
                if (grid != null) EditorUtility.SetDirty(grid);
                SceneView.RepaintAll();
                Repaint();
            }
        }

        private void DrawBrushFooterControls()
        {
            int maxBrush = 10;
            if (tilemapEditor.selectedGameObjectRuleIndex >= 0 && drawMode)
                maxBrush = 1;

            bool disableBrush = isSelectionMode || maxBrush == 1;
            int displayedBrushSize = disableBrush ? 1 : Mathf.Clamp(tilemapEditor.brushSize, 1, maxBrush);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Brush", GUILayout.Width(40));

            EditorGUI.BeginDisabledGroup(disableBrush);
            float sliderValue = GUILayout.HorizontalSlider(displayedBrushSize, 1f, maxBrush, GUILayout.MinWidth(140f));
            EditorGUI.EndDisabledGroup();

            int newBrushSize = disableBrush ? 1 : Mathf.Clamp(Mathf.RoundToInt(sliderValue), 1, maxBrush);
            GUILayout.Label(newBrushSize.ToString(), GUILayout.Width(24));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("✴︎ Center View", GUILayout.Width(100), GUILayout.Height(18)))
            {
                gridViewOffset = Vector2.zero;
                Repaint();
            }

            EditorGUILayout.EndHorizontal();

            if (newBrushSize != tilemapEditor.brushSize)
            {
                Undo.RecordObject(tilemapEditor, "Change Brush Size");
                brushSizeProperty.intValue = newBrushSize;
                tilemapEditor.brushSize = newBrushSize;
                serializedObject.ApplyModifiedProperties();
                Repaint();
            }
        }

        private void DrawCustomGridSizeControls()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Custom Width:", GUILayout.Width(100));
            customWidth = EditorGUILayout.IntField(customWidth, GUILayout.Width(60));
            EditorGUILayout.LabelField("Height:", GUILayout.Width(50));
            customHeight = EditorGUILayout.IntField(customHeight, GUILayout.Width(60));
            customWidth = Mathf.Max(1, customWidth);
            customHeight = Mathf.Max(1, customHeight);
            if (GUILayout.Button("Apply Custom Size", GUILayout.Width(120)))
            {
                Undo.RecordObject(tilemapEditor, "Apply Custom Grid Size");
                Vector3Int newGridSize = new Vector3Int(customWidth, customHeight, 1);
                gridSizeProperty.vector3IntValue = newGridSize;
                serializedObject.ApplyModifiedProperties();

                if (newGridSize.x < 64 || newGridSize.y < 64)
                    tilemapEditor.ForceUpdateSmallGrid();

                EditorUtility.SetDirty(tilemapEditor);
                Repaint();
            }
            EditorGUILayout.EndHorizontal();
        }

        #region V2 Native UI Toolkit Grid

        /// <summary>
        /// Creates the native UI Toolkit grid canvas (V2)
        /// </summary>
        private VisualElement CreateGridCanvas_V2()
        {
            var canvas = new VisualElement();
            canvas.name = "grid-canvas-v2";
            canvas.style.backgroundColor = new StyleColor(new Color(0.05f, 0.05f, 0.05f, 1f));
            // Always square - width = 100%, height matches width
            canvas.style.width = new Length(100, LengthUnit.Percent);
            canvas.style.minHeight = 400; // Fallback minimum
            // Use GeometryChangedEvent to maintain square aspect ratio
            canvas.RegisterCallback<GeometryChangedEvent>(evt => {
                if (evt.newRect.width > 0)
                    canvas.style.height = evt.newRect.width; // Keep square
            });
            canvas.style.overflow = Overflow.Hidden;
            canvas.style.borderTopWidth = 1;
            canvas.style.borderBottomWidth = 1;
            canvas.style.borderLeftWidth = 1;
            canvas.style.borderRightWidth = 1;
            canvas.style.borderTopColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f, 1f));
            canvas.style.borderBottomColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f, 1f));
            canvas.style.borderLeftColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f, 1f));
            canvas.style.borderRightColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f, 1f));

            gridBackgroundsContainerV2 = new VisualElement();
            gridBackgroundsContainerV2.name = "grid-backgrounds-v2";
            gridBackgroundsContainerV2.style.position = Position.Absolute;
            gridBackgroundsContainerV2.style.left = 0;
            gridBackgroundsContainerV2.style.top = 0;
            gridBackgroundsContainerV2.style.right = 0;
            gridBackgroundsContainerV2.style.bottom = 0;
            gridBackgroundsContainerV2.pickingMode = PickingMode.Ignore;
            canvas.Add(gridBackgroundsContainerV2);

            gridTileVisualsContainerV2 = new VisualElement();
            gridTileVisualsContainerV2.name = "grid-tile-visuals-v2";
            gridTileVisualsContainerV2.style.position = Position.Absolute;
            gridTileVisualsContainerV2.style.left = 0;
            gridTileVisualsContainerV2.style.top = 0;
            gridTileVisualsContainerV2.style.right = 0;
            gridTileVisualsContainerV2.style.bottom = 0;
            gridTileVisualsContainerV2.pickingMode = PickingMode.Ignore;
            canvas.Add(gridTileVisualsContainerV2);

            gridHiddenTileOutlinesContainerV2 = new VisualElement();
            gridHiddenTileOutlinesContainerV2.name = "grid-hidden-tile-outlines-v2";
            gridHiddenTileOutlinesContainerV2.style.position = Position.Absolute;
            gridHiddenTileOutlinesContainerV2.style.left = 0;
            gridHiddenTileOutlinesContainerV2.style.top = 0;
            gridHiddenTileOutlinesContainerV2.style.right = 0;
            gridHiddenTileOutlinesContainerV2.style.bottom = 0;
            gridHiddenTileOutlinesContainerV2.pickingMode = PickingMode.Ignore;
            canvas.Add(gridHiddenTileOutlinesContainerV2);

            // Grid cells container (will be populated with overlay cell elements)
            gridCellsContainer = new VisualElement();
            gridCellsContainer.name = "grid-cells";
            gridCellsContainer.style.position = Position.Absolute;
            gridCellsContainer.style.left = 0;
            gridCellsContainer.style.top = 0;
            gridCellsContainer.style.right = 0;
            gridCellsContainer.style.bottom = 0;
            canvas.Add(gridCellsContainer);

            gridPathLinesContainerV2 = new VisualElement();
            gridPathLinesContainerV2.name = "grid-path-lines-v2";
            gridPathLinesContainerV2.style.position = Position.Absolute;
            gridPathLinesContainerV2.style.left = 0;
            gridPathLinesContainerV2.style.top = 0;
            gridPathLinesContainerV2.style.right = 0;
            gridPathLinesContainerV2.style.bottom = 0;
            gridPathLinesContainerV2.pickingMode = PickingMode.Ignore;
            canvas.Add(gridPathLinesContainerV2);

            var selectionStatus = CreateSelectionStatusOverlay_V2();
            canvas.Add(selectionStatus);

            // === ACTION BAR OVERLAY (bottom center) ===
            var actionBar = CreateActionBarOverlay_V2();
            canvas.Add(actionBar);

            // === BRUSH SLIDER OVERLAY (right side) ===
            var brushSlider = CreateBrushSliderOverlay_V2();
            canvas.Add(brushSlider);

            // Register events
            canvas.RegisterCallback<GeometryChangedEvent>(evt => RefreshGridCellsV2());
            canvas.RegisterCallback<PointerDownEvent>(OnGridPointerDown_V2);
            canvas.RegisterCallback<PointerMoveEvent>(OnGridPointerMove_V2);
            canvas.RegisterCallback<PointerUpEvent>(OnGridPointerUp_V2);
            canvas.RegisterCallback<PointerLeaveEvent>(OnGridPointerLeave_V2);

            return canvas;
        }

        /// <summary>
        /// Refreshes all grid cells for V2
        /// </summary>
        private void RefreshGridCellsV2()
        {
            if (gridBackgroundsContainerV2 == null || gridCellsContainer == null || gridTileVisualsContainerV2 == null ||
                gridHiddenTileOutlinesContainerV2 == null ||
                gridPathLinesContainerV2 == null || tilemapEditor == null || tilemapEditor.targetTilemap == null)
                return;

            UpdateBrushSliderState_V2();
            UpdateSelectionStatusLabel_V2();
            gridBackgroundsContainerV2.Clear();
            gridCellsContainer.Clear();
            gridTileVisualsContainerV2.Clear();
            gridHiddenTileOutlinesContainerV2.Clear();
            gridPathLinesContainerV2.Clear();
            cellElementsV2.Clear();

            var rect = gridCanvasV2.contentRect;
            if (rect.width <= 0 || rect.height <= 0) return;

            int gridWidth = tilemapEditor.gridSize.x;
            int gridHeight = tilemapEditor.gridSize.y;
            
            // Keep cells square by using the smaller dimension
            float cellSize = Mathf.Min(rect.width / gridWidth, rect.height / gridHeight);
            
            // Calculate offset to center the grid
            float totalGridWidth = cellSize * gridWidth;
            float totalGridHeight = cellSize * gridHeight;
            float offsetX = (rect.width - totalGridWidth) / 2f;
            float offsetY = (rect.height - totalGridHeight) / 2f;

            var tileVisuals = new List<(float yOffset, int layerOrder, float top, VisualElement visual)>();

            for (int x = 0; x < gridWidth; x++)
            {
                for (int y = 0; y < gridHeight; y++)
                {
                    Vector3Int cellPos = new Vector3Int(
                        x - gridWidth / 2 + (int)gridViewOffsetV2.x,
                        gridHeight - 1 - y - gridHeight / 2 + (int)gridViewOffsetV2.y,
                        0
                    );

                    var backgroundCell = CreateGridBackgroundCell_V2(x, y, cellSize, cellSize, offsetX, offsetY);
                    gridBackgroundsContainerV2.Add(backgroundCell);

                    var cell = CreateGridCell_V2(x, y, cellSize, cellSize, cellPos, offsetX, offsetY);
                    gridCellsContainer.Add(cell);
                    cellElementsV2[new Vector2Int(x, y)] = cell;

                    float cellLeft = offsetX + x * cellSize;
                    float cellTop = offsetY + y * cellSize;
                    CollectTileVisualsForCell_V2(tileVisuals, null, cellPos, cellLeft, cellTop, cellSize, cellSize);
                }
            }

            foreach (var tileVisual in tileVisuals
                         .OrderBy(visual => visual.yOffset)
                         .ThenBy(visual => visual.layerOrder)
                         .ThenBy(visual => visual.top))
                gridTileVisualsContainerV2.Add(tileVisual.visual);

            RefreshPathLinesV2(cellSize, offsetX, offsetY, gridWidth, gridHeight);
        }

        private VisualElement CreateGridBackgroundCell_V2(int x, int y, float cellWidth, float cellHeight, float offsetX = 0f, float offsetY = 0f)
        {
            var cell = new VisualElement();
            cell.name = $"cell-bg-{x}-{y}";
            cell.style.position = Position.Absolute;
            cell.style.left = offsetX + x * cellWidth;
            cell.style.top = offsetY + y * cellHeight;
            cell.style.width = cellWidth;
            cell.style.height = cellHeight;
            cell.style.backgroundColor = new StyleColor(new Color(0.08f, 0.08f, 0.08f, 1f));
            cell.style.borderTopWidth = 0.5f;
            cell.style.borderLeftWidth = 0.5f;
            cell.style.borderTopColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f, 0.5f));
            cell.style.borderLeftColor = new StyleColor(new Color(0.3f, 0.3f, 0.3f, 0.5f));
            cell.pickingMode = PickingMode.Ignore;
            return cell;
        }

        /// <summary>
        /// Creates a single grid cell element
        /// </summary>
        private VisualElement CreateGridCell_V2(int x, int y, float cellWidth, float cellHeight, Vector3Int cellPos, float offsetX = 0, float offsetY = 0)
        {
            var cell = new VisualElement();
            cell.name = $"cell-{x}-{y}";
            cell.userData = cellPos;
            cell.style.position = Position.Absolute;
            cell.style.left = offsetX + x * cellWidth;
            cell.style.top = offsetY + y * cellHeight;
            cell.style.width = cellWidth;
            cell.style.height = cellHeight;
            cell.style.overflow = Overflow.Visible;
            cell.style.backgroundColor = new StyleColor(Color.clear);

            if (selectedCells.Contains(cellPos))
            {
                cell.style.borderTopWidth = 2f;
                cell.style.borderBottomWidth = 2f;
                cell.style.borderLeftWidth = 2f;
                cell.style.borderRightWidth = 2f;
                cell.style.borderTopColor = new StyleColor(new Color(1f, 0.84f, 0.2f, 1f));
                cell.style.borderBottomColor = new StyleColor(new Color(1f, 0.84f, 0.2f, 1f));
                cell.style.borderLeftColor = new StyleColor(new Color(1f, 0.84f, 0.2f, 1f));
                cell.style.borderRightColor = new StyleColor(new Color(1f, 0.84f, 0.2f, 1f));
            }

            // Check for texture paint
            if (tilemapEditor.texturePaintMask != null && 
                tilemapEditor.texturePaintMask.TryGetValue(cellPos, out int texIdx) &&
                texIdx >= 0 && texIdx < tilemapEditor.texturePaintRules.Count)
            {
                var tex = tilemapEditor.texturePaintRules[texIdx].albedo;
                if (tex != null)
                {
                    var texOverlay = new Image();
                    texOverlay.image = tex;
                    texOverlay.style.position = Position.Absolute;
                    texOverlay.style.right = 0;
                    texOverlay.style.bottom = 0;
                    texOverlay.style.width = new Length(50, LengthUnit.Percent);
                    texOverlay.style.height = new Length(50, LengthUnit.Percent);
                    cell.Add(texOverlay);
                }
            }

            AddPathMarkersToCell_V2(cell, cellPos, cellWidth, cellHeight);
            AddGameObjectDotToCell_V2(cell, cellPos, cellWidth, cellHeight);
            AddMovePreviewToCell_V2(cell, cellPos);
            AddBrushPreviewToCell_V2(cell, cellPos, cellWidth, cellHeight);
            AddTemporarySelectionFeedbackToCell_V2(cell, cellPos);

            return cell;
        }

        /// <summary>
        /// Creates the controls for V2 grid
        /// </summary>
        private VisualElement CreateGridControls_V2()
        {
            var controls = new VisualElement();
            controls.name = "grid-controls-v2";
            controls.style.flexDirection = FlexDirection.Column;
            controls.style.marginTop = 8;
            controls.style.paddingLeft = 4;
            controls.style.paddingRight = 4;

            var primaryRow = new VisualElement();
            primaryRow.style.flexDirection = FlexDirection.Row;
            primaryRow.style.alignItems = Align.Center;

            // Draw/Erase toggle
            drawModeToggleV2 = new Toggle("✏️ Draw");
            drawModeToggleV2.value = drawMode;
            drawModeToggleV2.RegisterValueChangedCallback(evt => {
                drawMode = evt.newValue;
                if (drawMode)
                    eraseMode = EraseMode.Select;
                UpdateToolButtonStates_V2();
            });
            primaryRow.Add(drawModeToggleV2);

            // Grid size dropdown
            var sizeChoices = new List<string> { "⊞ 7x7", "⊞ 16x16", "⊞ 32x32", "⊞ 64x64", "⊞ Custom" };
            var sizeDropdown = new DropdownField("Size", sizeChoices, Mathf.Clamp(GetCurrentPresetIndex(tilemapEditor.gridSize), 0, sizeChoices.Count - 1));
            sizeDropdown.style.flexGrow = 1;
            sizeDropdown.RegisterValueChangedCallback(evt => {
                int idx = sizeChoices.IndexOf(evt.newValue);
                Vector3Int newSize = tilemapEditor.gridSize;
                switch (idx)
                {
                    case 0:
                        newSize = new Vector3Int(7, 7, 1);
                        tilemapEditor.useCustomSize = false;
                        break;
                    case 1:
                        newSize = new Vector3Int(16, 16, 1);
                        tilemapEditor.useCustomSize = false;
                        break;
                    case 2:
                        newSize = new Vector3Int(32, 32, 1);
                        tilemapEditor.useCustomSize = false;
                        break;
                    case 3:
                        newSize = new Vector3Int(64, 64, 1);
                        tilemapEditor.useCustomSize = false;
                        break;
                    default:
                        tilemapEditor.useCustomSize = true;
                        break;
                }

                if (newSize != tilemapEditor.gridSize && !tilemapEditor.useCustomSize)
                {
                    Undo.RecordObject(tilemapEditor, "Change Grid Size");
                    tilemapEditor.gridSize = newSize;
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshGridCellsV2();
                }
            });
            primaryRow.Add(sizeDropdown);

            var scaleChoices = Enumerable.Range(1, 10).Select(i => $"x{i}").ToList();
            var scaleDropdown = new DropdownField("Scale", scaleChoices, Mathf.Clamp(tilemapEditor.gridScale - 1, 0, scaleChoices.Count - 1));
            scaleDropdown.style.width = 110;
            scaleDropdown.RegisterValueChangedCallback(evt => {
                int idx = scaleChoices.IndexOf(evt.newValue);
                int newScale = Mathf.Clamp(idx + 1, 1, 10);
                if (newScale != tilemapEditor.gridScale)
                {
                    Undo.RecordObject(tilemapEditor, "Change Grid Scale");
                    tilemapEditor.gridScale = newScale;
                    tilemapEditor.ApplyGridScale();
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshGridCellsV2();
                }
            });
            primaryRow.Add(scaleDropdown);

            // Refresh button
            var refreshBtn = new Button(() => RefreshGridCellsV2());
            refreshBtn.text = "🔄 Refresh";
            refreshBtn.style.width = 80;
            primaryRow.Add(refreshBtn);

            // Center button
            var centerBtn = new Button(() => {
                gridViewOffsetV2 = Vector2.zero;
                RefreshGridCellsV2();
            });
            centerBtn.text = "✴︎ Center";
            centerBtn.style.width = 80;
            primaryRow.Add(centerBtn);

            controls.Add(primaryRow);

            var customRow = new VisualElement();
            customRow.style.flexDirection = FlexDirection.Row;
            customRow.style.alignItems = Align.Center;
            customRow.style.marginTop = 4;
            customRow.style.display = tilemapEditor.useCustomSize ? DisplayStyle.Flex : DisplayStyle.None;

            var widthField = new IntegerField("Width");
            widthField.value = tilemapEditor.gridSize.x;
            widthField.style.width = 140;
            widthField.RegisterValueChangedCallback(evt => {
                if (!tilemapEditor.useCustomSize)
                    return;

                int newWidth = Mathf.Max(1, evt.newValue);
                if (newWidth == tilemapEditor.gridSize.x)
                    return;

                Undo.RecordObject(tilemapEditor, "Change Grid Width");
                tilemapEditor.gridSize = new Vector3Int(newWidth, tilemapEditor.gridSize.y, 1);
                EditorUtility.SetDirty(tilemapEditor);
                RefreshGridCellsV2();
            });
            customRow.Add(widthField);

            var heightField = new IntegerField("Height");
            heightField.value = tilemapEditor.gridSize.y;
            heightField.style.width = 140;
            heightField.style.marginLeft = 8;
            heightField.RegisterValueChangedCallback(evt => {
                if (!tilemapEditor.useCustomSize)
                    return;

                int newHeight = Mathf.Max(1, evt.newValue);
                if (newHeight == tilemapEditor.gridSize.y)
                    return;

                Undo.RecordObject(tilemapEditor, "Change Grid Height");
                tilemapEditor.gridSize = new Vector3Int(tilemapEditor.gridSize.x, newHeight, 1);
                EditorUtility.SetDirty(tilemapEditor);
                RefreshGridCellsV2();
            });
            customRow.Add(heightField);

            sizeDropdown.RegisterValueChangedCallback(_ => {
                customRow.style.display = tilemapEditor.useCustomSize ? DisplayStyle.Flex : DisplayStyle.None;
                widthField.SetValueWithoutNotify(tilemapEditor.gridSize.x);
                heightField.SetValueWithoutNotify(tilemapEditor.gridSize.y);
            });

            controls.Add(customRow);

            return controls;
        }

        // V2 Event handlers
        private bool isPanningV2 = false;
        private Vector3 lastPanPositionV2;

        private void OnGridPointerDown_V2(PointerDownEvent evt)
        {
            if (evt.button == 1 || evt.altKey)
            {
                ClearHoveredCellPreview_V2();
                isPanningV2 = true;
                lastPanPositionV2 = evt.localPosition;
                evt.StopPropagation();
                return;
            }

            if (evt.button != 0)
                return;

            if (!TryGetCellFromLocalPos_V2(evt.localPosition, out Vector3Int cellPos))
                return;

            SetHoveredCellPreview_V2(cellPos);

            if (IsPathTabActive() && (evt.ctrlKey || evt.commandKey) && tilemapEditor.selectedPathIndex >= 0)
            {
                HandlePathAssignment(cellPos);
                RefreshGridCellsV2();
                evt.StopPropagation();
                return;
            }

            if (panToolActive)
            {
                if (selectedCells.Count > 0)
                {
                    isDraggingSelectionV2 = true;
                    dragStartMouseCellV2 = cellPos;
                    selectionOffsetV2 = Vector3Int.zero;
                    RefreshGridCellsV2();
                }
                evt.StopPropagation();
                return;
            }

            if (evt.shiftKey && IsTilesTabActive() && !pickerToolActive && !isSelectionMode)
            {
                PickTileAt(cellPos, false);
                RefreshGridCellsV2();
                evt.StopPropagation();
                return;
            }

            isDrawingV2 = true;
            hasLastInteractedCellV2 = false;

            if (isSelectionMode)
            {
                if (evt.shiftKey)
                {
                    if (!selectedCells.Contains(cellPos))
                        selectedCells.Add(cellPos);
                    isBoxSelectingV2 = false;
                }
                else if (evt.ctrlKey || evt.commandKey)
                {
                    selectedCells.Remove(cellPos);
                    isBoxSelectingV2 = false;
                }
                else
                {
                    selectedCells.Clear();
                    selectedCells.Add(cellPos);
                    selectionStartCellV2 = cellPos;
                    isBoxSelectingV2 = true;
                }

                SceneView.RepaintAll();
                RefreshGridCellsV2();
                evt.StopPropagation();
                return;
            }

            if (!pickerToolActive && !isSelectionMode)
                BeginBrushStroke_V2();

            HandleCellInteraction_V2(cellPos, true);
            evt.StopPropagation();
        }

        private void OnGridPointerMove_V2(PointerMoveEvent evt)
        {
            if (isPanningV2)
            {
                var rect = gridCanvasV2.contentRect;
                int gridWidth = tilemapEditor?.gridSize.x ?? 7;
                int gridHeight = tilemapEditor?.gridSize.y ?? 7;
                float cellSize = Mathf.Min(rect.width / gridWidth, rect.height / gridHeight);
                
                Vector3 delta = evt.localPosition - lastPanPositionV2;
                gridViewOffsetV2.x -= delta.x / cellSize;
                gridViewOffsetV2.y += delta.y / cellSize;
                lastPanPositionV2 = evt.localPosition;
                
                RefreshGridCellsV2();
                return;
            }

            if (!TryGetCellFromLocalPos_V2(evt.localPosition, out Vector3Int hoverCellPos))
            {
                ClearHoveredCellPreview_V2();
                return;
            }

            SetHoveredCellPreview_V2(hoverCellPos);

            if (isDraggingSelectionV2)
            {
                selectionOffsetV2 = hoverCellPos - dragStartMouseCellV2;
                RefreshGridCellsV2();
                return;
            }
            
            if (!isDrawingV2) return;
            Vector3Int cellPos = hoverCellPos;

            if (isSelectionMode && isBoxSelectingV2)
            {
                selectedCells.Clear();

                int minX = Mathf.Min(selectionStartCellV2.x, cellPos.x);
                int maxX = Mathf.Max(selectionStartCellV2.x, cellPos.x);
                int minY = Mathf.Min(selectionStartCellV2.y, cellPos.y);
                int maxY = Mathf.Max(selectionStartCellV2.y, cellPos.y);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        selectedCells.Add(new Vector3Int(x, y, 0));
                    }
                }

                RefreshGridCellsV2();
                return;
            }

            HandleCellInteraction_V2(cellPos, false);
        }

        private void OnGridPointerUp_V2(PointerUpEvent evt)
        {
            if (isDraggingSelectionV2)
            {
                isDraggingSelectionV2 = false;
                if (selectionOffsetV2 != Vector3Int.zero && selectedCells.Count > 0)
                {
                    ApplyMoveSelectionV2(selectionOffsetV2);
                }
                selectionOffsetV2 = Vector3Int.zero;
            }

            isDrawingV2 = false;
            isPanningV2 = false;
            isBoxSelectingV2 = false;
            hasLastInteractedCellV2 = false;
            EndBrushStroke_V2();
            RefreshGridCellsV2();
        }

        private void OnGridPointerLeave_V2(PointerLeaveEvent evt)
        {
            if (isDrawingV2 || isDraggingSelectionV2 || isPanningV2)
                return;

            ClearHoveredCellPreview_V2();
        }

        /// <summary>
        /// Converts a local pointer position into a grid cell.
        /// </summary>
        private bool TryGetCellFromLocalPos_V2(Vector3 localPos, out Vector3Int cellPos)
        {
            cellPos = Vector3Int.zero;
            if (tilemapEditor == null || tilemapEditor.targetTilemap == null || gridCanvasV2 == null)
                return false;

            var rect = gridCanvasV2.contentRect;
            int gridWidth = tilemapEditor.gridSize.x;
            int gridHeight = tilemapEditor.gridSize.y;
            if (gridWidth <= 0 || gridHeight <= 0 || rect.width <= 0f || rect.height <= 0f)
                return false;
            
            // Keep cells square (same calculation as RefreshGridCellsV2)
            float cellSize = Mathf.Min(rect.width / gridWidth, rect.height / gridHeight);
            float totalGridWidth = cellSize * gridWidth;
            float totalGridHeight = cellSize * gridHeight;
            float offsetX = (rect.width - totalGridWidth) / 2f;
            float offsetY = (rect.height - totalGridHeight) / 2f;

            int x = Mathf.FloorToInt((localPos.x - offsetX) / cellSize);
            int y = Mathf.FloorToInt((localPos.y - offsetY) / cellSize);

            if (x < 0 || x >= gridWidth || y < 0 || y >= gridHeight)
                return false;

            cellPos = new Vector3Int(
                x - gridWidth / 2 + (int)gridViewOffsetV2.x,
                gridHeight - 1 - y - gridHeight / 2 + (int)gridViewOffsetV2.y,
                0
            );
            return true;
        }

        /// <summary>
        /// Handles drawing/erasing/selection at a specific grid cell.
        /// </summary>
        private void HandleCellInteraction_V2(Vector3Int cellPos, bool isPointerDown)
        {
            if (tilemapEditor == null || tilemapEditor.targetTilemap == null)
                return;

            if (hasLastInteractedCellV2 && lastInteractedCellV2 == cellPos && !pickerToolActive)
                return;

            lastInteractedCellV2 = cellPos;
            hasLastInteractedCellV2 = true;

            if (pickerToolActive)
            {
                PickTileAt(cellPos);
                UpdateToolButtonStates_V2();
                RefreshGridCellsV2();
                return;
            }

            if (isSelectionMode)
                return;

            List<Vector3Int> brushCells = GetBrushCells(cellPos, tilemapEditor.brushSize, tilemapEditor.brushShape);
            for (int i = 0; i < brushCells.Count; i++)
            {
                if (drawMode)
                    PaintAt(brushCells[i]);
                else
                    EraseAt(brushCells[i]);
            }

            if (IsTextureTabActive())
                SyncVegetationAfterTexturePaintStroke();

            EditorUtility.SetDirty(tilemapEditor);
            RefreshGridCellsV2();
        }

        private void BeginBrushStroke_V2()
        {
            if (tilemapEditor == null || hasActiveBrushStrokeV2)
                return;

            string undoLabel = drawMode
                ? (IsPathTabActive() ? "Add Path Points"
                    : IsGameObjectsTabActive() ? "Place GameObjects"
                    : IsTextureTabActive() ? "Paint Textures"
                    : "Paint Tiles")
                : (IsPathTabActive() ? "Erase Path Points"
                    : IsGameObjectsTabActive() ? "Erase GameObjects"
                    : IsTextureTabActive() ? "Erase Textures"
                    : (eraseMode == EraseMode.All ? "Erase All Tiles" : "Erase Tiles"));
            Undo.RegisterCompleteObjectUndo(tilemapEditor, undoLabel);
            if (tilemapEditor.targetTilemap != null)
                Undo.RegisterCompleteObjectUndo(tilemapEditor.targetTilemap, undoLabel);

            foreach (var rule in tilemapEditor.tileRules)
            {
                if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                    Undo.RegisterCompleteObjectUndo(rule.customTargetTilemap, undoLabel);
                else if (Mathf.Abs(rule.yOffset) > 0.001f &&
                         tilemapEditor.heightTilemaps.TryGetValue(rule.yOffset, out Tilemap heightTilemap) &&
                         heightTilemap != null)
                    Undo.RegisterCompleteObjectUndo(heightTilemap, undoLabel);
            }

            tilemapEditor.BeginProceduralSyncBatch();
            hasActiveBrushStrokeV2 = true;
        }

        private void EndBrushStroke_V2()
        {
            if (tilemapEditor == null || !hasActiveBrushStrokeV2)
                return;

            tilemapEditor.EndProceduralSyncBatch();
            hasActiveBrushStrokeV2 = false;
        }

        private Vector2? GetInspectorPathOverlayPoint_V2(QuickTilemapEditor.Path path, Vector3Int point, float offsetX,
                                                         float offsetY, int gridWidth, int gridHeight, float cellWidth, float cellHeight)
        {
            float localX = point.x - (int)gridViewOffsetV2.x + gridWidth / 2f;
            float localY = gridHeight - 1 - (point.y - (int)gridViewOffsetV2.y + gridHeight / 2f);

            if (ShouldDrawPathAtDualGridIntersection(path))
            {
                float pixelX = offsetX + localX * cellWidth;
                float pixelY = offsetY + (localY + 1f) * cellHeight;

                if (pixelX < offsetX || pixelX > offsetX + gridWidth * cellWidth ||
                    pixelY < offsetY || pixelY > offsetY + gridHeight * cellHeight)
                    return null;

                return new Vector2(pixelX, pixelY);
            }

            if (localX < 0f || localX >= gridWidth || localY < 0f || localY >= gridHeight)
                return null;

            return new Vector2(
                offsetX + (localX + 0.5f) * cellWidth,
                offsetY + (localY + 0.5f) * cellHeight);
        }

        private void RefreshPathLinesV2(float cellSize, float offsetX, float offsetY, int gridWidth, int gridHeight)
        {
            if (gridPathLinesContainerV2 == null || tilemapEditor?.paths == null)
                return;

            const float lineThickness = 3f;

            foreach (var path in tilemapEditor.paths)
            {
                if (path == null || path.points == null || path.points.Count < 2)
                    continue;

                Vector2? prevCenter = null;
                foreach (Vector3Int point in path.points)
                {
                    Vector2? overlayPoint = GetInspectorPathOverlayPoint_V2(path, point, offsetX, offsetY, gridWidth, gridHeight, cellSize, cellSize);
                    if (!overlayPoint.HasValue)
                        continue;

                    Vector2 currentCenter = overlayPoint.Value;
                    if (prevCenter.HasValue)
                    {
                        Vector2 delta = currentCenter - prevCenter.Value;
                        float length = delta.magnitude;
                        if (length > 0.01f)
                        {
                            float angleDeg = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
                            Vector2 mid = (prevCenter.Value + currentCenter) * 0.5f;

                            var line = new VisualElement();
                            line.style.position = Position.Absolute;
                            line.style.left = mid.x - length * 0.5f;
                            line.style.top = mid.y - lineThickness * 0.5f;
                            line.style.width = length;
                            line.style.height = lineThickness;
                            line.style.backgroundColor = new StyleColor(path.color);
                            line.style.rotate = new Rotate(new Angle(angleDeg, AngleUnit.Degree));
                            line.style.opacity = 0.95f;
                            line.pickingMode = PickingMode.Ignore;
                            gridPathLinesContainerV2.Add(line);
                        }
                    }

                    prevCenter = currentCenter;
                }
            }
        }

        private float GetTileDisplayYOffset_V2(Tilemap targetMap, float fallbackYOffset = 0f)
        {
            if (targetMap == null)
                return fallbackYOffset;

            return targetMap.transform.localPosition.y;
        }

        private List<(TileBase tile, Color color, float yOffset, int sortOrder, int layerKey)> GetVisibleTileLayersForCell_V2(Vector3Int cellPos)
        {
            var layers = new List<(TileBase tile, Color color, float yOffset, int sortOrder, int layerKey)>();

            if (tilemapEditor == null || tilemapEditor.targetTilemap == null)
                return layers;

            TileBase baseTile = tilemapEditor.targetTilemap.GetTile(cellPos);
            if (baseTile != null)
            {
                layers.Add((baseTile,
                    tilemapEditor.targetTilemap.GetColor(cellPos),
                    GetTileDisplayYOffset_V2(tilemapEditor.targetTilemap, 0f),
                    int.MinValue,
                    tilemapEditor.targetTilemap.GetInstanceID()));
            }

            for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
            {
                var rule = tilemapEditor.tileRules[i];
                if (rule == null || !rule.isVisible)
                    continue;

                Tilemap targetMap = ResolveRuleTilemapForDisplay_V2(rule);
                if (targetMap == null)
                    continue;

                TileBase ruleTile = targetMap.GetTile(cellPos);
                if (ruleTile == null)
                    continue;

                layers.Add((ruleTile, rule.color, GetTileDisplayYOffset_V2(targetMap, rule.yOffset), i, targetMap.GetInstanceID()));
            }

            layers.Sort((a, b) =>
            {
                int yCompare = a.yOffset.CompareTo(b.yOffset);
                if (yCompare != 0)
                    return yCompare;

                return a.sortOrder.CompareTo(b.sortOrder);
            });

            return layers;
        }

        private VisualElement CreateTileLayerVisual_V2(TileBase tile, Color color, float left, float top, float width, float height)
        {
            if (tile == null)
                return null;

            VisualElement visual;
            if (tile is UnityEngine.Tilemaps.Tile spriteTile && spriteTile.sprite != null)
            {
                var image = new Image();
                image.sprite = spriteTile.sprite;
                image.tintColor = color;
                visual = image;
            }
            else
            {
                visual = new VisualElement();
                visual.style.backgroundColor = new StyleColor(color);
            }

            visual.style.position = Position.Absolute;
            visual.style.left = left;
            visual.style.top = top;
            visual.style.width = width;
            visual.style.height = height;
            visual.pickingMode = PickingMode.Ignore;
            return visual;
        }

        private void CollectTileVisualsForCell_V2(List<(float yOffset, int layerOrder, float top, VisualElement visual)> tileVisuals,
                                                  List<(Vector3Int cellPos, Rect baseRect, Rect visualRect, float yOffset, int layerOrder, int layerKey)> tileDrawEntries,
                                                  Vector3Int cellPos, float cellLeft, float cellTop, float cellWidth, float cellHeight)
        {
            if (tileVisuals == null)
                return;

            var tileLayers = GetVisibleTileLayersForCell_V2(cellPos);
            Rect baseRect = new Rect(cellLeft, cellTop, cellWidth, cellHeight);
            for (int i = 0; i < tileLayers.Count; i++)
            {
                var tileLayer = tileLayers[i];
                float visualTop = cellTop - tileLayer.yOffset * cellHeight;
                VisualElement visual = CreateTileLayerVisual_V2(tileLayer.tile, tileLayer.color, cellLeft, visualTop, cellWidth, cellHeight);
                if (visual != null)
                {
                    tileVisuals.Add((tileLayer.yOffset, i, visualTop, visual));
                    tileDrawEntries?.Add((cellPos, baseRect, new Rect(cellLeft, visualTop, cellWidth, cellHeight), tileLayer.yOffset, i, tileLayer.layerKey));
                }
            }
        }

        private void CollectHiddenMaskOutlineSegments_V2(
            List<(Vector3Int cellPos, Rect baseRect, Rect visualRect, float yOffset, int layerOrder, int layerKey)> tileDrawEntries,
            out List<(float y, float start, float end)> horizontalSegments,
            out List<(float x, float start, float end)> verticalSegments)
        {
            horizontalSegments = new List<(float y, float start, float end)>();
            verticalSegments = new List<(float x, float start, float end)>();
            if (tileDrawEntries == null || tileDrawEntries.Count < 2)
                return;

            var orderedEntries = tileDrawEntries
                .OrderBy(entry => entry.visualRect.yMin)
                .ThenBy(entry => entry.layerOrder)
                .ToList();

            for (int i = 0; i < orderedEntries.Count; i++)
            {
                var hiddenCandidate = orderedEntries[i];
                if (hiddenCandidate.yOffset <= 0.01f)
                    continue;

                Rect? extensionRect = GetOffsetExtensionRect_V2(hiddenCandidate.baseRect, hiddenCandidate.visualRect);
                if (!extensionRect.HasValue)
                    continue;

                List<Rect> hiddenRects = new List<Rect>();
                for (int j = i + 1; j < orderedEntries.Count; j++)
                {
                    var occluder = orderedEntries[j];
                    if (occluder.layerKey == hiddenCandidate.layerKey)
                        continue;

                    Rect? hiddenIntersection = IntersectRects_V2(extensionRect.Value, occluder.visualRect);
                    if (!hiddenIntersection.HasValue)
                        continue;

                    hiddenRects.Add(QuantizeRectForOutline_V2(hiddenIntersection.Value));
                }

                if (hiddenRects.Count == 0)
                    continue;

                CollectHiddenContourEdgesForExtension_V2(extensionRect.Value, hiddenRects, horizontalSegments, verticalSegments);
            }
        }

        private Rect? GetOffsetExtensionRect_V2(Rect baseRect, Rect visualRect)
        {
            const float epsilon = 0.01f;

            if (visualRect.yMin < baseRect.yMin - epsilon)
                return new Rect(visualRect.xMin, visualRect.yMin, visualRect.width, baseRect.yMin - visualRect.yMin);

            return null;
        }

        private Rect? IntersectRects_V2(Rect a, Rect b)
        {
            float xMin = Mathf.Max(a.xMin, b.xMin);
            float xMax = Mathf.Min(a.xMax, b.xMax);
            float yMin = Mathf.Max(a.yMin, b.yMin);
            float yMax = Mathf.Min(a.yMax, b.yMax);

            if (xMax - xMin <= 0.01f || yMax - yMin <= 0.01f)
                return null;

            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private float QuantizeOutlineCoord_V2(float value)
        {
            return Mathf.Round(value * 100f) / 100f;
        }

        private Rect QuantizeRectForOutline_V2(Rect rect)
        {
            float xMin = QuantizeOutlineCoord_V2(rect.xMin);
            float xMax = QuantizeOutlineCoord_V2(rect.xMax);
            float yMin = QuantizeOutlineCoord_V2(rect.yMin);
            float yMax = QuantizeOutlineCoord_V2(rect.yMax);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        private void CollectHiddenContourEdgesForExtension_V2(
            Rect extensionRect,
            List<Rect> hiddenRects,
            List<(float y, float start, float end)> horizontalSegments,
            List<(float x, float start, float end)> verticalSegments)
        {
            if (hiddenRects == null || hiddenRects.Count == 0)
                return;

            List<Rect> validRegions = hiddenRects
                .Where(rect => rect.width > 0.01f && rect.height > 0.01f)
                .Select(QuantizeRectForOutline_V2)
                .ToList();

            if (validRegions.Count == 0)
                return;

            Rect quantizedExtension = QuantizeRectForOutline_V2(extensionRect);

            List<float> xCoords = validRegions
                .SelectMany(rect => new[] { rect.xMin, rect.xMax })
                .Concat(new[] { quantizedExtension.xMin, quantizedExtension.xMax })
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            List<float> yCoords = validRegions
                .SelectMany(rect => new[] { rect.yMin, rect.yMax })
                .Concat(new[] { quantizedExtension.yMin, quantizedExtension.yMax })
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            if (xCoords.Count < 2 || yCoords.Count < 2)
                return;

            var xIndexMap = new Dictionary<float, int>();
            var yIndexMap = new Dictionary<float, int>();
            for (int i = 0; i < xCoords.Count; i++)
                xIndexMap[xCoords[i]] = i;
            for (int i = 0; i < yCoords.Count; i++)
                yIndexMap[yCoords[i]] = i;

            bool[,] hiddenGrid = new bool[xCoords.Count - 1, yCoords.Count - 1];
            foreach (Rect rect in validRegions)
            {
                if (!xIndexMap.TryGetValue(rect.xMin, out int xStart) ||
                    !xIndexMap.TryGetValue(rect.xMax, out int xEnd) ||
                    !yIndexMap.TryGetValue(rect.yMin, out int yStart) ||
                    !yIndexMap.TryGetValue(rect.yMax, out int yEnd))
                    continue;

                for (int x = xStart; x < xEnd; x++)
                {
                    for (int y = yStart; y < yEnd; y++)
                        hiddenGrid[x, y] = true;
                }
            }

            bool[,] insideExtension = new bool[xCoords.Count - 1, yCoords.Count - 1];
            for (int x = 0; x < xCoords.Count - 1; x++)
            {
                for (int y = 0; y < yCoords.Count - 1; y++)
                {
                    float cellCenterX = (xCoords[x] + xCoords[x + 1]) * 0.5f;
                    float cellCenterY = (yCoords[y] + yCoords[y + 1]) * 0.5f;
                    insideExtension[x, y] =
                        cellCenterX > quantizedExtension.xMin + 0.001f &&
                        cellCenterX < quantizedExtension.xMax - 0.001f &&
                        cellCenterY > quantizedExtension.yMin + 0.001f &&
                        cellCenterY < quantizedExtension.yMax - 0.001f;
                }
            }

            for (int x = 0; x < hiddenGrid.GetLength(0); x++)
            {
                for (int y = 0; y < hiddenGrid.GetLength(1); y++)
                {
                    if (!hiddenGrid[x, y] || !insideExtension[x, y])
                        continue;

                    float xMin = xCoords[x];
                    float xMax = xCoords[x + 1];
                    float yMin = yCoords[y];
                    float yMax = yCoords[y + 1];

                    if (y == 0 || !insideExtension[x, y - 1])
                        horizontalSegments.Add((yMin, xMin, xMax));

                    if (y == hiddenGrid.GetLength(1) - 1 || !insideExtension[x, y + 1])
                        horizontalSegments.Add((yMax, xMin, xMax));

                    if (x == 0 || !insideExtension[x - 1, y])
                        verticalSegments.Add((xMin, yMin, yMax));

                    if (x == hiddenGrid.GetLength(0) - 1 || !insideExtension[x + 1, y])
                        verticalSegments.Add((xMax, yMin, yMax));
                }
            }
        }

        private void RenderHiddenTileOutlines_V2(
            List<(float y, float start, float end)> horizontalSegments,
            List<(float x, float start, float end)> verticalSegments,
            float cellSize)
        {
            if (gridHiddenTileOutlinesContainerV2 == null)
                return;

            float dashSize = Mathf.Clamp(cellSize * 0.22f, 4f, 12f);
            const float borderWidth = 3f;
            Color outlineColor = new Color(1f, 1f, 1f, 0.92f);

            foreach (var segment in MergeHorizontalOutlineSegments_V2(horizontalSegments))
                AddDashedEdge_V2(segment.start, segment.y, segment.end, segment.y, borderWidth, dashSize, outlineColor);

            foreach (var segment in MergeVerticalOutlineSegments_V2(verticalSegments))
                AddDashedEdge_V2(segment.x, segment.start, segment.x, segment.end, borderWidth, dashSize, outlineColor);
        }

        private List<(float y, float start, float end)> MergeHorizontalOutlineSegments_V2(List<(float y, float start, float end)> segments)
        {
            var merged = new List<(float y, float start, float end)>();
            if (segments == null || segments.Count == 0)
                return merged;

            const float epsilon = 0.05f;
            foreach (var group in segments
                         .GroupBy(segment => QuantizeOutlineCoord_V2(segment.y))
                         .OrderBy(group => group.Key))
            {
                float currentStart = float.NaN;
                float currentEnd = float.NaN;

                foreach (var segment in group.OrderBy(segment => segment.start))
                {
                    if (float.IsNaN(currentStart))
                    {
                        currentStart = segment.start;
                        currentEnd = segment.end;
                        continue;
                    }

                    if (segment.start <= currentEnd + epsilon)
                    {
                        currentEnd = Mathf.Max(currentEnd, segment.end);
                        continue;
                    }

                    merged.Add((group.Key, currentStart, currentEnd));
                    currentStart = segment.start;
                    currentEnd = segment.end;
                }

                if (!float.IsNaN(currentStart))
                    merged.Add((group.Key, currentStart, currentEnd));
            }

            return merged;
        }

        private List<(float x, float start, float end)> MergeVerticalOutlineSegments_V2(List<(float x, float start, float end)> segments)
        {
            var merged = new List<(float x, float start, float end)>();
            if (segments == null || segments.Count == 0)
                return merged;

            const float epsilon = 0.05f;
            foreach (var group in segments
                         .GroupBy(segment => QuantizeOutlineCoord_V2(segment.x))
                         .OrderBy(group => group.Key))
            {
                float currentStart = float.NaN;
                float currentEnd = float.NaN;

                foreach (var segment in group.OrderBy(segment => segment.start))
                {
                    if (float.IsNaN(currentStart))
                    {
                        currentStart = segment.start;
                        currentEnd = segment.end;
                        continue;
                    }

                    if (segment.start <= currentEnd + epsilon)
                    {
                        currentEnd = Mathf.Max(currentEnd, segment.end);
                        continue;
                    }

                    merged.Add((group.Key, currentStart, currentEnd));
                    currentStart = segment.start;
                    currentEnd = segment.end;
                }

                if (!float.IsNaN(currentStart))
                    merged.Add((group.Key, currentStart, currentEnd));
            }

            return merged;
        }

        private void AddDashedEdge_V2(float startX, float startY, float endX, float endY, float thickness, float dashSize, Color color)
        {
            if (gridHiddenTileOutlinesContainerV2 == null)
                return;

            bool horizontal = Mathf.Abs(startY - endY) < 0.01f;
            bool vertical = Mathf.Abs(startX - endX) < 0.01f;
            if (!horizontal && !vertical)
                return;

            float length = horizontal ? Mathf.Abs(endX - startX) : Mathf.Abs(endY - startY);
            float origin = horizontal ? Mathf.Min(startX, endX) : Mathf.Min(startY, endY);

            for (float offset = 0f; offset < length; offset += dashSize * 2f)
            {
                float dashLength = Mathf.Min(dashSize, length - offset);
                if (dashLength <= 0f)
                    continue;

                var dash = new VisualElement();
                dash.style.position = Position.Absolute;
                dash.style.backgroundColor = new StyleColor(color);
                dash.style.borderTopLeftRadius = thickness * 0.5f;
                dash.style.borderTopRightRadius = thickness * 0.5f;
                dash.style.borderBottomLeftRadius = thickness * 0.5f;
                dash.style.borderBottomRightRadius = thickness * 0.5f;
                dash.pickingMode = PickingMode.Ignore;

                if (horizontal)
                {
                    dash.style.left = origin + offset;
                    dash.style.top = startY - thickness * 0.5f;
                    dash.style.width = dashLength;
                    dash.style.height = thickness;
                }
                else
                {
                    dash.style.left = startX - thickness * 0.5f;
                    dash.style.top = origin + offset;
                    dash.style.width = thickness;
                    dash.style.height = dashLength;
                }

                gridHiddenTileOutlinesContainerV2.Add(dash);
            }
        }

        private Tilemap ResolveRuleTilemapForDisplay_V2(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null)
                return null;

            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                return rule.customTargetTilemap;

            if (Mathf.Abs(rule.yOffset) > 0.001f &&
                tilemapEditor.heightTilemaps.TryGetValue(rule.yOffset, out Tilemap heightTilemap))
                return heightTilemap;

            return tilemapEditor.targetTilemap;
        }

        private bool TryGetTopVisibleTileForCell_V2(Vector3Int cellPos, out TileBase tile, out Color color)
        {
            tile = null;
            color = Color.white;

            var candidates = GetVisibleTileLayersForCell_V2(cellPos);
            if (candidates.Count == 0)
                return false;

            var topCandidate = candidates.Last();

            tile = topCandidate.tile;
            color = topCandidate.color;
            return true;
        }

        private void AddPathMarkersToCell_V2(VisualElement cell, Vector3Int cellPos, float cellWidth, float cellHeight)
        {
            if (tilemapEditor?.paths == null || tilemapEditor.paths.Count == 0)
                return;

            int dualMarkerIndex = 0;
            for (int i = 0; i < tilemapEditor.paths.Count; i++)
            {
                var path = tilemapEditor.paths[i];
                if (path == null || path.points == null || !path.points.Contains(cellPos))
                    continue;

                if (ShouldDrawPathAtDualGridIntersection(path))
                {
                    float markerSize = Mathf.Clamp(Mathf.Min(cellWidth, cellHeight) * 0.28f, 5f, 12f);
                    float markerOffset = dualMarkerIndex * (markerSize + 2f);

                    var marker = new VisualElement();
                    marker.style.position = Position.Absolute;
                    marker.style.left = -markerSize * 0.5f + markerOffset;
                    marker.style.top = cellHeight - markerSize * 0.5f;
                    marker.style.width = markerSize;
                    marker.style.height = markerSize;
                    marker.style.backgroundColor = new StyleColor(path.color);
                    marker.style.borderTopWidth = 1f;
                    marker.style.borderBottomWidth = 1f;
                    marker.style.borderLeftWidth = 1f;
                    marker.style.borderRightWidth = 1f;
                    marker.style.borderTopColor = new StyleColor(Color.black * 0.65f);
                    marker.style.borderBottomColor = new StyleColor(Color.black * 0.65f);
                    marker.style.borderLeftColor = new StyleColor(Color.black * 0.65f);
                    marker.style.borderRightColor = new StyleColor(Color.black * 0.65f);
                    cell.Add(marker);
                    dualMarkerIndex++;
                    continue;
                }

                var border = new VisualElement();
                border.style.position = Position.Absolute;
                border.style.left = 3f;
                border.style.top = 3f;
                border.style.right = 3f;
                border.style.bottom = 3f;
                border.style.borderTopWidth = 2f;
                border.style.borderBottomWidth = 2f;
                border.style.borderLeftWidth = 2f;
                border.style.borderRightWidth = 2f;
                border.style.borderTopColor = new StyleColor(path.color);
                border.style.borderBottomColor = new StyleColor(path.color);
                border.style.borderLeftColor = new StyleColor(path.color);
                border.style.borderRightColor = new StyleColor(path.color);
                cell.Add(border);
                break;
            }
        }

        private void AddGameObjectDotToCell_V2(VisualElement cell, Vector3Int cellPos, float cellWidth, float cellHeight)
        {
            if (tilemapEditor?.placedObjects == null)
                return;

            var placedObject = tilemapEditor.placedObjects.FirstOrDefault(obj => obj != null && obj.position == cellPos);
            if (placedObject == null)
                return;

            Color dotColor = Color.magenta;
            if (placedObject.ruleIndex >= 0 && placedObject.ruleIndex < tilemapEditor.gameObjectRules.Count)
                dotColor = tilemapEditor.gameObjectRules[placedObject.ruleIndex].color;
            else if (placedObject.color != default(Color))
                dotColor = placedObject.color;

            dotColor.a = 0.8f;

            var dot = new VisualElement();
            dot.style.position = Position.Absolute;
            dot.style.left = cellWidth * 0.25f;
            dot.style.top = cellHeight * 0.25f;
            dot.style.width = cellWidth * 0.5f;
            dot.style.height = cellHeight * 0.5f;
            dot.style.backgroundColor = new StyleColor(dotColor);
            dot.style.borderTopWidth = 1f;
            dot.style.borderBottomWidth = 1f;
            dot.style.borderLeftWidth = 1f;
            dot.style.borderRightWidth = 1f;
            dot.style.borderTopColor = new StyleColor(Color.black * 0.5f);
            dot.style.borderBottomColor = new StyleColor(Color.black * 0.5f);
            dot.style.borderLeftColor = new StyleColor(Color.black * 0.5f);
            dot.style.borderRightColor = new StyleColor(Color.black * 0.5f);
            cell.Add(dot);
        }

        private void AddMovePreviewToCell_V2(VisualElement cell, Vector3Int cellPos)
        {
            if (!isDraggingSelectionV2 || selectionOffsetV2 == Vector3Int.zero || selectedCells.Count == 0)
                return;

            if (!selectedCells.Contains(cellPos - selectionOffsetV2))
                return;

            var preview = new VisualElement();
            preview.style.position = Position.Absolute;
            preview.style.left = 2f;
            preview.style.top = 2f;
            preview.style.right = 2f;
            preview.style.bottom = 2f;
            preview.style.backgroundColor = new StyleColor(new Color(1f, 0.95f, 0.35f, 0.18f));
            preview.style.borderTopWidth = 2f;
            preview.style.borderBottomWidth = 2f;
            preview.style.borderLeftWidth = 2f;
            preview.style.borderRightWidth = 2f;
            preview.style.borderTopColor = new StyleColor(new Color(1f, 0.9f, 0.2f, 0.95f));
            preview.style.borderBottomColor = new StyleColor(new Color(1f, 0.9f, 0.2f, 0.95f));
            preview.style.borderLeftColor = new StyleColor(new Color(1f, 0.9f, 0.2f, 0.95f));
            preview.style.borderRightColor = new StyleColor(new Color(1f, 0.9f, 0.2f, 0.95f));
            cell.Add(preview);
        }

        private void AddBrushPreviewToCell_V2(VisualElement cell, Vector3Int cellPos, float cellWidth, float cellHeight)
        {
            if (!hasHoveredCellV2 || isPanningV2 || isDraggingSelectionV2)
                return;

            int previewBrushSize = isSelectionMode ? 1 : tilemapEditor.brushSize;
            var previewCells = GetBrushCells(hoveredCellV2, previewBrushSize, tilemapEditor.brushShape);
            if (!previewCells.Contains(cellPos))
                return;

            if (pickerToolActive)
            {
                var pickerPreview = new VisualElement();
                pickerPreview.style.position = Position.Absolute;
                pickerPreview.style.left = 2f;
                pickerPreview.style.top = 2f;
                pickerPreview.style.right = 2f;
                pickerPreview.style.bottom = 2f;
                pickerPreview.style.backgroundColor = new StyleColor(new Color(1f, 0.88f, 0.2f, 0.2f));
                pickerPreview.style.borderTopWidth = 2f;
                pickerPreview.style.borderBottomWidth = 2f;
                pickerPreview.style.borderLeftWidth = 2f;
                pickerPreview.style.borderRightWidth = 2f;
                pickerPreview.style.borderTopColor = new StyleColor(new Color(1f, 0.88f, 0.2f, 0.95f));
                pickerPreview.style.borderBottomColor = new StyleColor(new Color(1f, 0.88f, 0.2f, 0.95f));
                pickerPreview.style.borderLeftColor = new StyleColor(new Color(1f, 0.88f, 0.2f, 0.95f));
                pickerPreview.style.borderRightColor = new StyleColor(new Color(1f, 0.88f, 0.2f, 0.95f));
                cell.Add(pickerPreview);
                return;
            }

            var hoveredPath = IsPathTabActive() ? GetSelectedInspectorPath() : null;
            bool previewPathAtDualGridIntersection = hoveredPath != null &&
                                                     ShouldDrawPathAtDualGridIntersection(hoveredPath) &&
                                                     !isSelectionMode;

            if (previewPathAtDualGridIntersection)
            {
                float markerSize = Mathf.Clamp(Mathf.Min(cellWidth, cellHeight) * 0.32f, 6f, 14f);
                var marker = new VisualElement();
                marker.style.position = Position.Absolute;
                marker.style.left = -markerSize * 0.5f;
                marker.style.top = cellHeight - markerSize * 0.5f;
                marker.style.width = markerSize;
                marker.style.height = markerSize;
                marker.style.backgroundColor = new StyleColor(drawMode ? new Color(0.2f, 0.8f, 0.2f, 0.7f) : new Color(0.8f, 0.2f, 0.2f, 0.7f));
                marker.style.borderTopWidth = 2f;
                marker.style.borderBottomWidth = 2f;
                marker.style.borderLeftWidth = 2f;
                marker.style.borderRightWidth = 2f;
                marker.style.borderTopColor = new StyleColor(drawMode ? new Color(0f, 0.6f, 0f, 0.9f) : new Color(0.6f, 0f, 0f, 0.9f));
                marker.style.borderBottomColor = new StyleColor(drawMode ? new Color(0f, 0.6f, 0f, 0.9f) : new Color(0.6f, 0f, 0f, 0.9f));
                marker.style.borderLeftColor = new StyleColor(drawMode ? new Color(0f, 0.6f, 0f, 0.9f) : new Color(0.6f, 0f, 0f, 0.9f));
                marker.style.borderRightColor = new StyleColor(drawMode ? new Color(0f, 0.6f, 0f, 0.9f) : new Color(0.6f, 0f, 0f, 0.9f));
                cell.Add(marker);
                return;
            }

            Color fillColor;
            Color borderColor;

            if (isSelectionMode)
            {
                fillColor = new Color(0.2f, 0.8f, 0.9f, 0.35f);
                borderColor = new Color(0f, 0.6f, 0.8f, 0.85f);
            }
            else
            {
                fillColor = drawMode ? new Color(0.2f, 0.8f, 0.2f, 0.28f) : new Color(0.8f, 0.2f, 0.2f, 0.28f);
                borderColor = drawMode ? new Color(0f, 0.6f, 0f, 0.85f) : new Color(0.6f, 0f, 0f, 0.85f);
            }

            var brushPreview = new VisualElement();
            brushPreview.style.position = Position.Absolute;
            brushPreview.style.left = 1f;
            brushPreview.style.top = 1f;
            brushPreview.style.right = 1f;
            brushPreview.style.bottom = 1f;
            brushPreview.style.backgroundColor = new StyleColor(fillColor);
            brushPreview.style.borderTopWidth = previewBrushSize > 5 ? 1f : 2f;
            brushPreview.style.borderBottomWidth = previewBrushSize > 5 ? 1f : 2f;
            brushPreview.style.borderLeftWidth = previewBrushSize > 5 ? 1f : 2f;
            brushPreview.style.borderRightWidth = previewBrushSize > 5 ? 1f : 2f;
            brushPreview.style.borderTopColor = new StyleColor(borderColor);
            brushPreview.style.borderBottomColor = new StyleColor(borderColor);
            brushPreview.style.borderLeftColor = new StyleColor(borderColor);
            brushPreview.style.borderRightColor = new StyleColor(borderColor);
            cell.Add(brushPreview);
        }

        private void AddTemporarySelectionFeedbackToCell_V2(VisualElement cell, Vector3Int cellPos)
        {
            if (cellPos != tempSelectionPos || EditorApplication.timeSinceStartup >= tempSelectionTime)
                return;

            var feedback = new VisualElement();
            feedback.style.position = Position.Absolute;
            feedback.style.left = 0f;
            feedback.style.top = 0f;
            feedback.style.right = 0f;
            feedback.style.bottom = 0f;
            feedback.style.backgroundColor = new StyleColor(new Color(tempSelectionColor.r, tempSelectionColor.g, tempSelectionColor.b, 0.45f));
            feedback.style.borderTopWidth = 2f;
            feedback.style.borderBottomWidth = 2f;
            feedback.style.borderLeftWidth = 2f;
            feedback.style.borderRightWidth = 2f;
            feedback.style.borderTopColor = new StyleColor(Color.yellow);
            feedback.style.borderBottomColor = new StyleColor(Color.yellow);
            feedback.style.borderLeftColor = new StyleColor(Color.yellow);
            feedback.style.borderRightColor = new StyleColor(Color.yellow);
            cell.Add(feedback);

            int remainingMs = Mathf.Max(1, Mathf.CeilToInt((tempSelectionTime - (float)EditorApplication.timeSinceStartup) * 1000f));
            feedback.schedule.Execute(() => feedback.RemoveFromHierarchy()).StartingIn(remainingMs);
        }

        private void SetHoveredCellPreview_V2(Vector3Int cellPos)
        {
            bool changed = !hasHoveredCellV2 || hoveredCellV2 != cellPos;
            hasHoveredCellV2 = true;
            hoveredCellV2 = cellPos;

            UpdateGameObjectPreview_V2(cellPos, true);

            if (changed && !isDrawingV2)
                RefreshGridCellsV2();
        }

        private void ClearHoveredCellPreview_V2()
        {
            bool hadHover = hasHoveredCellV2;
            hasHoveredCellV2 = false;
            UpdateGameObjectPreview_V2(Vector3Int.zero, false);

            if (hadHover)
                RefreshGridCellsV2();
        }

        private void UpdateGameObjectPreview_V2(Vector3Int cellPos, bool hasCell)
        {
            if (tilemapEditor == null)
                return;

            if (!hasCell ||
                !drawMode ||
                !IsGameObjectsTabActive() ||
                tilemapEditor.selectedGameObjectRuleIndex < 0 ||
                tilemapEditor.selectedGameObjectRuleIndex >= tilemapEditor.gameObjectRules.Count)
            {
                tilemapEditor.ClearPreviewObject();
                return;
            }

            var rule = tilemapEditor.gameObjectRules[tilemapEditor.selectedGameObjectRuleIndex];
            if (rule == null || rule.prefab == null)
            {
                tilemapEditor.ClearPreviewObject();
                return;
            }

            Vector3 worldPos = tilemapEditor.GetPlacementWorldPos(cellPos);
            float highestY = float.MinValue;

            if (tilemapEditor.targetTilemap != null && tilemapEditor.targetTilemap.GetTile(cellPos) != null)
                highestY = Mathf.Max(highestY, tilemapEditor.SafeCellToWorld(cellPos).y);

            foreach (Tilemap tilemap in tilemapEditor.GetAllCustomTilemaps())
            {
                if (tilemap != null && tilemap.GetTile(cellPos) != null)
                    highestY = Mathf.Max(highestY, tilemap.transform.position.y);
            }

            if (highestY == float.MinValue)
                highestY = 0f;

            worldPos.y = highestY + rule.yOffset;

            QuickTilemapEditor.PlacedObject existingPlaced = tilemapEditor.placedObjects != null
                ? tilemapEditor.placedObjects.Find(placed => placed != null && placed.position == cellPos)
                : null;
            float previewRotation = existingPlaced != null ? existingPlaced.rotation : 0f;
            Quaternion previewQuat = Quaternion.Euler(0f, previewRotation, 0f);
            tilemapEditor.ShowPreviewObject(rule.prefab, worldPos, previewQuat, rule.color);
        }

        #region V2 Overlays

        private VisualElement actionBarV2;
        private VisualElement brushSliderV2;
        private Slider brushSliderControlV2;

        private VisualElement CreateSelectionStatusOverlay_V2()
        {
            selectionStatusBadgeV2 = new VisualElement();
            selectionStatusBadgeV2.name = "selection-status-v2";
            selectionStatusBadgeV2.style.position = Position.Absolute;
            selectionStatusBadgeV2.style.left = new Length(50, LengthUnit.Percent);
            selectionStatusBadgeV2.style.bottom = 58;
            selectionStatusBadgeV2.style.translate = new StyleTranslate(new Translate(new Length(-50, LengthUnit.Percent), 0));
            selectionStatusBadgeV2.style.maxWidth = new Length(68, LengthUnit.Percent);
            selectionStatusBadgeV2.style.paddingLeft = 10;
            selectionStatusBadgeV2.style.paddingRight = 10;
            selectionStatusBadgeV2.style.paddingTop = 6;
            selectionStatusBadgeV2.style.paddingBottom = 6;
            selectionStatusBadgeV2.style.backgroundColor = new StyleColor(new Color(0.05f, 0.08f, 0.13f, 0.92f));
            selectionStatusBadgeV2.style.borderTopLeftRadius = 7;
            selectionStatusBadgeV2.style.borderTopRightRadius = 7;
            selectionStatusBadgeV2.style.borderBottomLeftRadius = 7;
            selectionStatusBadgeV2.style.borderBottomRightRadius = 7;
            selectionStatusBadgeV2.style.borderLeftWidth = 1f;
            selectionStatusBadgeV2.style.borderRightWidth = 1f;
            selectionStatusBadgeV2.style.borderTopWidth = 1f;
            selectionStatusBadgeV2.style.borderBottomWidth = 1f;
            selectionStatusBadgeV2.style.borderLeftColor = new StyleColor(new Color(1f, 1f, 1f, 0.08f));
            selectionStatusBadgeV2.style.borderRightColor = new StyleColor(new Color(1f, 1f, 1f, 0.08f));
            selectionStatusBadgeV2.style.borderTopColor = new StyleColor(new Color(1f, 1f, 1f, 0.08f));
            selectionStatusBadgeV2.style.borderBottomColor = new StyleColor(new Color(0f, 0f, 0f, 0.35f));
            selectionStatusBadgeV2.pickingMode = PickingMode.Ignore;

            selectionStatusLabelV2 = new Label();
            selectionStatusLabelV2.style.color = new StyleColor(Color.white);
            selectionStatusLabelV2.style.fontSize = 12;
            selectionStatusLabelV2.style.unityFontStyleAndWeight = FontStyle.Bold;
            selectionStatusLabelV2.style.whiteSpace = WhiteSpace.NoWrap;
            selectionStatusLabelV2.style.overflow = Overflow.Hidden;
            selectionStatusLabelV2.style.flexShrink = 1f;
            selectionStatusLabelV2.pickingMode = PickingMode.Ignore;
            selectionStatusBadgeV2.Add(selectionStatusLabelV2);

            UpdateSelectionStatusLabel_V2();
            return selectionStatusBadgeV2;
        }

        private void UpdateSelectionStatusLabel_V2()
        {
            if (selectionStatusLabelV2 == null || tilemapEditor == null)
                return;

            string toolStatus = GetActiveToolStatusText_V2();
            string targetStatus = GetActiveSelectionStatusText_V2();
            string selectionSummary = selectedCells.Count > 0 && (isSelectionMode || panToolActive)
                ? $"  |  🔲 {selectedCells.Count}"
                : string.Empty;

            selectionStatusLabelV2.text = $"{toolStatus}  |  {targetStatus}{selectionSummary}";
            selectionStatusLabelV2.tooltip = selectionStatusLabelV2.text;
        }

        private string GetActiveToolStatusText_V2()
        {
            if (pickerToolActive)
                return "🧪 Picker";

            if (panToolActive)
                return "↔️ Move";

            if (isSelectionMode)
                return "✂️ Select";

            if (drawMode)
                return "✏️ Draw";

            return eraseMode == EraseMode.All ? "💥 Erase All" : "🧽 Erase";
        }

        private string GetActiveSelectionStatusText_V2()
        {
            if (IsPathTabActive())
                return GetPathSelectionStatusText_V2();

            if (IsGameObjectsTabActive())
                return GetGameObjectSelectionStatusText_V2();

            if (IsTextureTabActive())
                return GetTextureSelectionStatusText_V2();

            return GetTileSelectionStatusText_V2();
        }

        private string GetTileSelectionStatusText_V2()
        {
            if (tilemapEditor.selectedTileRuleIndex >= 0 &&
                tilemapEditor.selectedTileRuleIndex < tilemapEditor.tileRules.Count)
            {
                var rule = tilemapEditor.tileRules[tilemapEditor.selectedTileRuleIndex];
                if (rule != null)
                {
                    string name = !string.IsNullOrWhiteSpace(rule.ruleName)
                        ? rule.ruleName
                        : rule.tile != null ? rule.tile.name : $"Tile {tilemapEditor.selectedTileRuleIndex + 1}";
                    return $"🧱 Tile: {name}";
                }
            }

            return "🧱 Tile: none";
        }

        private string GetTextureSelectionStatusText_V2()
        {
            var rule = tilemapEditor.selectedTextureRule;
            if (rule != null)
            {
                string name = !string.IsNullOrWhiteSpace(rule.ruleName)
                    ? rule.ruleName
                    : rule.albedo != null ? rule.albedo.name : $"Texture {rule.textureIndex + 1}";
                return $"🎨 Texture: {name}";
            }

            return "🎨 Texture: none";
        }

        private string GetGameObjectSelectionStatusText_V2()
        {
            if (tilemapEditor.selectedGameObjectRuleIndex >= 0 &&
                tilemapEditor.selectedGameObjectRuleIndex < tilemapEditor.gameObjectRules.Count)
            {
                var rule = tilemapEditor.gameObjectRules[tilemapEditor.selectedGameObjectRuleIndex];
                if (rule != null)
                {
                    string name = rule.prefab != null
                        ? rule.prefab.name
                        : !string.IsNullOrWhiteSpace(rule.prefabResourcePath)
                            ? System.IO.Path.GetFileNameWithoutExtension(rule.prefabResourcePath)
                            : $"Object {tilemapEditor.selectedGameObjectRuleIndex + 1}";
                    return $"📦 Object: {name}";
                }
            }

            return "📦 Object: none";
        }

        private string GetPathSelectionStatusText_V2()
        {
            if (tilemapEditor.selectedPathIndex >= 0 &&
                tilemapEditor.selectedPathIndex < tilemapEditor.paths.Count)
            {
                var path = tilemapEditor.paths[tilemapEditor.selectedPathIndex];
                if (path != null)
                {
                    string typeName = ObjectNames.NicifyVariableName(path.pathType.ToString());
                    return $"🛤️ Path #{tilemapEditor.selectedPathIndex + 1}: {typeName}";
                }
            }

            return "🛤️ Path: none";
        }

        /// <summary>
        /// Creates the action bar overlay with tool icons
        /// </summary>
        private VisualElement CreateActionBarOverlay_V2()
        {
            actionBarV2 = new VisualElement();
            actionBarV2.name = "action-bar-v2";
            actionBarV2.style.position = Position.Absolute;
            actionBarV2.style.bottom = 12;
            actionBarV2.style.left = new Length(50, LengthUnit.Percent);
            actionBarV2.style.translate = new StyleTranslate(new Translate(new Length(-50, LengthUnit.Percent), 0));
            actionBarV2.style.flexDirection = FlexDirection.Row;
            actionBarV2.style.backgroundColor = new StyleColor(new Color(0.05f, 0.08f, 0.13f, 0.95f));
            actionBarV2.style.paddingLeft = 8;
            actionBarV2.style.paddingRight = 8;
            actionBarV2.style.paddingTop = 6;
            actionBarV2.style.paddingBottom = 6;
            actionBarV2.style.borderTopLeftRadius = 6;
            actionBarV2.style.borderTopRightRadius = 6;
            actionBarV2.style.borderBottomLeftRadius = 6;
            actionBarV2.style.borderBottomRightRadius = 6;

            // Draw button
            var drawBtn = CreateToolButton_V2(OverlayDrawIconAsset, "Draw", drawMode && !isSelectionMode, () => {
                drawMode = true;
                eraseMode = EraseMode.Select;
                isSelectionMode = false;
                panToolActive = false;
                pickerToolActive = false;
                UpdateToolButtonStates_V2();
            });
            drawBtn.name = "tool-draw";
            actionBarV2.Add(drawBtn);

            var eraseBtn = CreateToolButton_V2(OverlayEraseIconAsset, "Erase", !drawMode && eraseMode == EraseMode.Select && !panToolActive, () => {
                drawMode = false;
                eraseMode = EraseMode.Select;
                isSelectionMode = false;
                panToolActive = false;
                pickerToolActive = false;
                UpdateToolButtonStates_V2();
            });
            eraseBtn.name = "tool-erase";
            actionBarV2.Add(eraseBtn);

            var eraseAllBtn = CreateToolButton_V2(OverlayEraseAllIconAsset, "Erase All", !drawMode && eraseMode == EraseMode.All && !panToolActive, () => {
                drawMode = false;
                eraseMode = EraseMode.All;
                isSelectionMode = false;
                panToolActive = false;
                pickerToolActive = false;
                UpdateToolButtonStates_V2();
            });
            eraseAllBtn.name = "tool-erase-all";
            actionBarV2.Add(eraseAllBtn);

            var moveBtn = CreateToolButton_V2(OverlayMoveIconAsset, "Move [M]", panToolActive, () => {
                panToolActive = true;
                drawMode = false;
                isSelectionMode = false;
                pickerToolActive = false;
                UpdateToolButtonStates_V2();
            });
            moveBtn.name = "tool-move";
            actionBarV2.Add(moveBtn);

            // Selection button
            var selectBtn = CreateToolButton_V2(OverlaySelectIconAsset, "Select", isSelectionMode, () => {
                isSelectionMode = !isSelectionMode;
                if (isSelectionMode)
                {
                    drawMode = true;
                    eraseMode = EraseMode.Select;
                    panToolActive = false;
                    pickerToolActive = false;
                    selectedCells.Clear();
                }
                UpdateToolButtonStates_V2();
            });
            selectBtn.name = "tool-select";
            actionBarV2.Add(selectBtn);
            
            var pickerBtn = CreateToolButton_V2(OverlayPickerIconAsset, "Picker [I]", pickerToolActive, () => {
                ResetPickerCycleState();
                pickerToolActive = true;
                panToolActive = false;
                drawMode = false;
                isSelectionMode = false;
                UpdateToolButtonStates_V2();
            });
            pickerBtn.name = "tool-picker";
            actionBarV2.Add(pickerBtn);

            // Clear button
            var clearBtn = CreateToolButton_V2(OverlayClearIconAsset, "Clear All", false, () => {
                ClearAllTilemapContent();
                RefreshGridCellsV2();
            });
            clearBtn.style.marginLeft = 8;
            actionBarV2.Add(clearBtn);

            return actionBarV2;
        }

        private bool awaitingTexturePicker = false;
        private bool applyTextureToAll = false;
        private int texturePickerRuleIndex = -1;

        private static readonly string[] PreferredMaterialTextureProperties =
        {
            "_BaseMap",
            "_MainTex",
            "_BaseColorMap",
            "_BaseTex",
            "_AlbedoMap",
            "_Tex",
            "_Texture",
        };

        private enum TileRuleTextureSurface
        {
            Top = 0,
            Wall = 1,
            Bottom = 2,
            All = 3,
        }

        private void ShowTextureMenu_V2()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Apply to Selected Only"), false, () => PickTexture(false));
            menu.AddItem(new GUIContent("Apply to All Using Same Material"), false, () => PickTexture(true));
            menu.ShowAsContext();
        }

        private void PickTexture(bool all)
        {
            awaitingTexturePicker = true;
            applyTextureToAll = all;
            texturePickerRuleIndex = tilemapEditor != null ? tilemapEditor.selectedTileRuleIndex : -1;
            
            // Open object picker for Texture2D
            int controlID = EditorGUIUtility.GetControlID(FocusType.Passive);
            EditorGUIUtility.ShowObjectPicker<Texture2D>(null, false, "", controlID);
        }

        private void BeginTileRuleTexturePicker(int ruleIndex, int surfaceType, bool applyToSharedMaterial)
        {
            if (tilemapEditor?.tileRules == null || ruleIndex < 0 || ruleIndex >= tilemapEditor.tileRules.Count)
                return;

            SelectTileRuleExclusive(ruleIndex);
            awaitingTexturePicker = true;
            applyTextureToAll = applyToSharedMaterial;
            texturePickerTargetType = surfaceType;
            texturePickerRuleIndex = ruleIndex;

            int controlID = EditorGUIUtility.GetControlID(FocusType.Passive);
            EditorGUIUtility.ShowObjectPicker<Texture2D>(null, false, "", controlID);
        }

        private void ShowTileRuleTextureScopeMenu(int ruleIndex, int surfaceType)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("This Rule Only"), false,
                () => BeginTileRuleTexturePicker(ruleIndex, surfaceType, false));
            menu.AddItem(new GUIContent("All Using Same Material"), false,
                () => BeginTileRuleTexturePicker(ruleIndex, surfaceType, true));
            menu.ShowAsContext();
        }

        private void HandleTexturePicker()
        {
            if (!awaitingTexturePicker) return;

            if (Event.current.commandName == "ObjectSelectorUpdated" && EditorGUIUtility.GetObjectPickerObject() is Texture2D)
            {
                // Live preview could go here if desired
            }
            else if (Event.current.commandName == "ObjectSelectorClosed")
            {
                var tex = EditorGUIUtility.GetObjectPickerObject() as Texture2D;
                if (tex != null)
                {
                    UpdateSkirtTexture(tex, applyTextureToAll);
                }
                awaitingTexturePicker = false;
                texturePickerRuleIndex = -1;
            }
        }

        // 0=Grass, 1=Wall, 2=Cliff, 3=All (legacy logic)
        private int texturePickerTargetType = 0;

        private void UpdateSkirtTexture(Texture2D tex, bool all)
        {
            if (tex == null) return;

            if (TryApplyTextureToSelectedTileRule(tex, all))
            {
                RefreshTileRuleTextureUI();
                return;
            }

            // Apply to all SkirtManagers in scene
            if (all)
            {
                var skirts = Object.FindObjectsByType<SkirtManager>(FindObjectsSortMode.None);
                foreach (var skirt in skirts)
                {
                    Undo.RecordObject(skirt.gameObject, "Change Texture");
                    if (texturePickerTargetType == 0) skirt.UpdateGrassTexture(tex);
                    else if (texturePickerTargetType == 1) skirt.UpdateWallTexture(tex);
                    else if (texturePickerTargetType == 2) skirt.UpdateCliffTexture(tex);
                    else skirt.UpdateMaterialTexture(tex);
                }
            }
            else
            {
                // Helper to apply to single skirt
                void ApplyToSkirt(SkirtManager s)
                {
                    if (s == null) return;
                    Undo.RecordObject(s.gameObject, "Change Texture");
                    if (texturePickerTargetType == 0) s.UpdateGrassTexture(tex);
                    else if (texturePickerTargetType == 1) s.UpdateWallTexture(tex);
                    else if (texturePickerTargetType == 2) s.UpdateCliffTexture(tex);
                    else s.UpdateMaterialTexture(tex);
                }

                // Apply to selected rule's skirt manager or selected object
                if (tilemapEditor.selectedTileRuleIndex >= 0 && 
                    tilemapEditor.selectedTileRuleIndex < tilemapEditor.tileRules.Count)
                {
                    var rule = tilemapEditor.tileRules[tilemapEditor.selectedTileRuleIndex];
                    var map = rule.useCustomTilemap && rule.customTargetTilemap != null
                        ? rule.customTargetTilemap
                        : (Mathf.Abs(rule.yOffset) > 0.001f && tilemapEditor.heightTilemaps.ContainsKey(rule.yOffset)
                            ? tilemapEditor.heightTilemaps[rule.yOffset]
                            : tilemapEditor.targetTilemap);
                    if (map != null)
                    {
                        var skirt = map.GetComponentInChildren<SkirtManager>(true);
                        ApplyToSkirt(skirt);
                    }
                }
                // Fallback: try active tilemap
                else if (tilemapEditor.targetTilemap != null)
                {
                    var skirt = tilemapEditor.targetTilemap.GetComponentInChildren<SkirtManager>(true);
                    ApplyToSkirt(skirt);
                }
            }
            
            SceneView.RepaintAll();
        }

        private bool TryApplyTextureToSelectedTileRule(Texture2D texture, bool applyToSharedMaterial)
        {
            if (tilemapEditor?.tileRules == null || tilemapEditor.tileRules.Count == 0)
                return false;

            int ruleIndex = texturePickerRuleIndex >= 0
                ? texturePickerRuleIndex
                : tilemapEditor.selectedTileRuleIndex;

            if (ruleIndex < 0 || ruleIndex >= tilemapEditor.tileRules.Count)
                return false;

            var rule = tilemapEditor.tileRules[ruleIndex];
            if (rule == null)
                return false;

            bool changed = texturePickerTargetType == (int)TileRuleTextureSurface.All
                ? ApplyTextureToAllTileRuleSurfaces(rule, texture, applyToSharedMaterial)
                : ApplyTextureToTileRuleSurface(rule, texturePickerTargetType, texture, applyToSharedMaterial);

            if (!changed)
                return false;

            EditorUtility.SetDirty(tilemapEditor);
            return true;
        }

        private bool ApplyTextureToAllTileRuleSurfaces(
            QuickTilemapEditor.TileRule rule,
            Texture2D texture,
            bool applyToSharedMaterial)
        {
            if (rule != null && rule.meshMode == QuickTilemapEditor.MeshMode.Procedural && applyToSharedMaterial)
            {
                bool proceduralChanged = false;
                var proceduralMaterials = new HashSet<Material>();

                void ApplyIfValid(Material material)
                {
                    if (material == null || !proceduralMaterials.Add(material))
                        return;

                    proceduralChanged |= ApplyTextureToMaterial(material, texture);
                }

                ApplyIfValid(rule.proceduralFloorMaterial);
                ApplyIfValid(rule.proceduralWallMaterial);
                ApplyIfValid(rule.proceduralCeilingMaterial);
                ApplyIfValid(rule.proceduralBottomMaterial);

                if (proceduralChanged)
                    RebuildProceduralMeshes(rule);

                return proceduralChanged;
            }

            bool changed = false;
            var processedSharedMaterials = new HashSet<Material>();

            for (int surfaceType = (int)TileRuleTextureSurface.Top; surfaceType <= (int)TileRuleTextureSurface.Bottom; surfaceType++)
            {
                if (applyToSharedMaterial)
                {
                    var material = GetTileRuleSurfacePreviewMaterial(rule, surfaceType);
                    if (material == null || !processedSharedMaterials.Add(material))
                        continue;

                    changed |= ApplyTextureToMaterial(material, texture);
                    continue;
                }

                changed |= ApplyTextureToTileRuleSurface(rule, surfaceType, texture, false);
            }

            return changed;
        }

        private bool ApplyTextureToTileRuleSurface(
            QuickTilemapEditor.TileRule rule,
            int surfaceType,
            Texture2D texture,
            bool applyToSharedMaterial)
        {
            if (rule == null || texture == null)
                return false;

            if (rule.meshMode == QuickTilemapEditor.MeshMode.Procedural)
                return ApplyTextureToProceduralSurface(rule, surfaceType, texture, applyToSharedMaterial);

            return ApplyTextureToLegacySurface(rule, surfaceType, texture, applyToSharedMaterial);
        }

        private bool ApplyTextureToProceduralSurface(
            QuickTilemapEditor.TileRule rule,
            int surfaceType,
            Texture2D texture,
            bool applyToSharedMaterial)
        {
            Material sourceMaterial = GetResolvedTileRuleSurfaceMaterial(rule, surfaceType);
            if (sourceMaterial == null)
                return false;

            Material targetMaterial = sourceMaterial;

            Undo.RecordObject(tilemapEditor, "Change Tile Rule Surface Texture");

            if (!applyToSharedMaterial)
            {
                PreserveProceduralSurfaceFallbacks(rule, surfaceType, sourceMaterial);
                targetMaterial = DuplicateMaterialAsset(
                    sourceMaterial,
                    BuildTileRuleSurfaceMaterialName(rule, surfaceType));

                if (targetMaterial == null)
                    return false;

                AssignProceduralSurfaceMaterial(rule, surfaceType, targetMaterial);
            }

            bool changed = ApplyTextureToMaterial(targetMaterial, texture);
            if (!changed)
                return false;

            RebuildProceduralMeshes(rule);
            return true;
        }

        private bool ApplyTextureToLegacySurface(
            QuickTilemapEditor.TileRule rule,
            int surfaceType,
            Texture2D texture,
            bool applyToSharedMaterial)
        {
            var renderers = GetLegacySurfaceRenderers(rule, surfaceType);
            if (renderers.Count == 0)
                return false;

            Material sourceMaterial = renderers
                .Select(renderer => renderer != null ? renderer.sharedMaterial : null)
                .FirstOrDefault(material => material != null);

            if (sourceMaterial == null)
                return false;

            Material targetMaterial = sourceMaterial;
            if (!applyToSharedMaterial)
            {
                targetMaterial = DuplicateMaterialAsset(
                    sourceMaterial,
                    BuildTileRuleSurfaceMaterialName(rule, surfaceType));

                if (targetMaterial == null)
                    return false;

                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                        continue;

                    Undo.RecordObject(renderer, "Assign Local Surface Material");
                    renderer.sharedMaterial = targetMaterial;
                    EditorUtility.SetDirty(renderer);
                }
            }

            bool changed = ApplyTextureToMaterial(targetMaterial, texture);
            if (!changed)
                return false;

            return true;
        }

        private void PreserveProceduralSurfaceFallbacks(
            QuickTilemapEditor.TileRule rule,
            int surfaceType,
            Material sourceMaterial)
        {
            if (rule == null || sourceMaterial == null)
                return;

            Material resolvedWall = GetResolvedTileRuleSurfaceMaterial(rule, (int)TileRuleTextureSurface.Wall);
            Material resolvedBottom = GetResolvedTileRuleSurfaceMaterial(rule, (int)TileRuleTextureSurface.Bottom);

            if (surfaceType == (int)TileRuleTextureSurface.Top)
            {
                if (rule.proceduralWallMaterial == null && resolvedWall == sourceMaterial)
                    rule.proceduralWallMaterial = sourceMaterial;

                if (rule.proceduralBottomMaterial == null && resolvedBottom == sourceMaterial)
                    rule.proceduralBottomMaterial = sourceMaterial;
            }
            else if (surfaceType == (int)TileRuleTextureSurface.Wall)
            {
                if (rule.proceduralBottomMaterial == null && resolvedBottom == sourceMaterial)
                    rule.proceduralBottomMaterial = sourceMaterial;
            }
        }

        private void AssignProceduralSurfaceMaterial(
            QuickTilemapEditor.TileRule rule,
            int surfaceType,
            Material material)
        {
            switch ((TileRuleTextureSurface)surfaceType)
            {
                case TileRuleTextureSurface.Top:
                    rule.proceduralFloorMaterial = material;
                    break;
                case TileRuleTextureSurface.Wall:
                    if (rule.proceduralSettings != null &&
                        rule.proceduralSettings.skirtMaterialMode == SkirtMaterialMode.UseFloorMaterialWithMask)
                    {
                        rule.proceduralFloorMaterial = material;
                    }
                    else
                    {
                        rule.proceduralCeilingMaterial = material;
                    }
                    break;
                case TileRuleTextureSurface.Bottom:
                    rule.proceduralBottomMaterial = material;
                    break;
            }
        }

        private Material GetResolvedTileRuleSurfaceMaterial(QuickTilemapEditor.TileRule rule, int surfaceType)
        {
            if (rule == null)
                return null;

            switch ((TileRuleTextureSurface)surfaceType)
            {
                case TileRuleTextureSurface.Top:
                    return rule.proceduralFloorMaterial;

                case TileRuleTextureSurface.Wall:
                    if (rule.proceduralSettings != null &&
                        rule.proceduralSettings.skirtMaterialMode == SkirtMaterialMode.UseFloorMaterialWithMask)
                    {
                        return rule.proceduralFloorMaterial
                            ?? rule.proceduralCeilingMaterial
                            ?? rule.proceduralWallMaterial;
                    }

                    if (rule.proceduralCeilingMaterial != null)
                        return rule.proceduralCeilingMaterial;

                    if (rule.proceduralWallMaterial != null)
                        return rule.proceduralWallMaterial;

                    return rule.proceduralFloorMaterial;

                case TileRuleTextureSurface.Bottom:
                    if (rule.proceduralBottomMaterial != null)
                        return rule.proceduralBottomMaterial;

                    if (rule.proceduralWallMaterial != null)
                        return rule.proceduralWallMaterial;

                    return rule.proceduralFloorMaterial;

                default:
                    return rule.proceduralFloorMaterial
                        ?? rule.proceduralWallMaterial
                        ?? rule.proceduralBottomMaterial;
            }
        }

        private List<Renderer> GetLegacySurfaceRenderers(QuickTilemapEditor.TileRule rule, int surfaceType)
        {
            var renderers = new List<Renderer>();
            if (rule == null)
                return renderers;

            Tilemap tilemap = GetTilemapForRule(rule);
            if (tilemap == null)
                return renderers;

            var skirt = tilemap.GetComponentInChildren<SkirtManager>(true);
            if (skirt == null)
                return renderers;

            void AddRendererFromTransform(Transform target)
            {
                if (target == null)
                    return;

                var renderer = target.GetComponent<Renderer>();
                if (renderer != null && !renderers.Contains(renderer))
                    renderers.Add(renderer);
            }

            switch ((TileRuleTextureSurface)surfaceType)
            {
                case TileRuleTextureSurface.Top:
                    AddRendererFromTransform(skirt.skirt);
                    break;

                case TileRuleTextureSurface.Wall:
                    AddRendererFromTransform(skirt.wall);
                    for (int i = 0; i < skirt.transform.childCount; i++)
                    {
                        Transform child = skirt.transform.GetChild(i);
                        if (child != null && child.name.StartsWith("Wall_"))
                            AddRendererFromTransform(child);
                    }
                    break;

                case TileRuleTextureSurface.Bottom:
                    AddRendererFromTransform(skirt.bottom);
                    break;
            }

            return renderers;
        }

        private Texture2D GetTileRuleSurfacePreviewTexture(QuickTilemapEditor.TileRule rule, int surfaceType)
        {
            return GetMaterialPreviewTexture(GetTileRuleSurfacePreviewMaterial(rule, surfaceType));
        }

        private Material GetTileRuleSurfacePreviewMaterial(QuickTilemapEditor.TileRule rule, int surfaceType)
        {
            if (rule == null)
                return null;

            if (rule.meshMode == QuickTilemapEditor.MeshMode.Procedural)
                return GetResolvedTileRuleSurfaceMaterial(rule, surfaceType);

            var renderer = GetLegacySurfaceRenderers(rule, surfaceType).FirstOrDefault();
            return renderer != null ? renderer.sharedMaterial : null;
        }

        private static Texture2D GetMaterialPreviewTexture(Material material)
        {
            if (material == null)
                return null;

            string propertyName = GetPrimaryTexturePropertyName(material);
            if (!string.IsNullOrEmpty(propertyName))
            {
                var directTexture = material.GetTexture(propertyName) as Texture2D;
                if (directTexture != null)
                    return directTexture;
            }

            return AssetPreview.GetAssetPreview(material) ?? AssetPreview.GetMiniThumbnail(material);
        }

        private static bool ApplyTextureToMaterial(Material material, Texture2D texture)
        {
            if (material == null || texture == null)
                return false;

            string propertyName = GetPrimaryTexturePropertyName(material);
            if (string.IsNullOrEmpty(propertyName))
            {
                Debug.LogWarning($"[QuickTile] No texture slot found on material '{material.name}'.");
                return false;
            }

            Undo.RecordObject(material, "Change Material Texture");
            material.SetTexture(propertyName, texture);
            EditorUtility.SetDirty(material);

            if (AssetDatabase.Contains(material))
                AssetDatabase.SaveAssets();

            return true;
        }

        private static string GetPrimaryTexturePropertyName(Material material)
        {
            if (material == null || material.shader == null)
                return null;

            string fallbackProperty = null;

            foreach (string propertyName in PreferredMaterialTextureProperties)
            {
                if (!material.HasProperty(propertyName))
                    continue;

                if (fallbackProperty == null)
                    fallbackProperty = propertyName;

                if (material.GetTexture(propertyName) != null)
                    return propertyName;
            }

            int propertyCount = material.shader.GetPropertyCount();
            for (int i = 0; i < propertyCount; i++)
            {
                if (material.shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                    continue;

                string propertyName = material.shader.GetPropertyName(i);
                if (material.HasProperty(propertyName) && material.GetTexture(propertyName) != null)
                    return propertyName;

                fallbackProperty ??= propertyName;
            }

            return fallbackProperty;
        }

        private Material DuplicateMaterialAsset(Material sourceMaterial, string desiredName)
        {
            if (sourceMaterial == null)
                return null;

            string sourcePath = AssetDatabase.GetAssetPath(sourceMaterial);
            string directory = !string.IsNullOrEmpty(sourcePath)
                ? Path.GetDirectoryName(sourcePath)?.Replace("\\", "/")
                : "Assets/BEKKOLOCO/QuickTile/Material";

            if (string.IsNullOrEmpty(directory))
                directory = "Assets/BEKKOLOCO/QuickTile/Material";

            string safeName = SanitizeMaterialAssetName(desiredName);
            string uniquePath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{safeName}.mat");

            Material duplicatedMaterial = null;
            if (!string.IsNullOrEmpty(sourcePath) && AssetDatabase.CopyAsset(sourcePath, uniquePath))
            {
                duplicatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(uniquePath);
            }
            else
            {
                duplicatedMaterial = new Material(sourceMaterial);
                AssetDatabase.CreateAsset(duplicatedMaterial, uniquePath);
            }

            AssetDatabase.SaveAssets();
            return duplicatedMaterial;
        }

        private static string BuildTileRuleSurfaceMaterialName(QuickTilemapEditor.TileRule rule, int surfaceType)
        {
            string baseName = rule != null && !string.IsNullOrWhiteSpace(rule.ruleName)
                ? rule.ruleName
                : rule?.tile != null
                    ? rule.tile.name
                    : "QuickTile";

            string suffix = ((TileRuleTextureSurface)surfaceType) switch
            {
                TileRuleTextureSurface.Top => "Top",
                TileRuleTextureSurface.Wall => "Wall",
                TileRuleTextureSurface.Bottom => "Bottom",
                _ => "Surface",
            };

            return $"{baseName}_{suffix}";
        }

        private static string SanitizeMaterialAssetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "QuickTile_Surface";

            string sanitized = name.Trim();
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                sanitized = sanitized.Replace(invalidChar, '_');

            sanitized = sanitized.Replace('/', '_').Replace('\\', '_');
            return string.IsNullOrWhiteSpace(sanitized) ? "QuickTile_Surface" : sanitized;
        }

        private void RefreshTileRuleTextureUI()
        {
            AssetDatabase.Refresh();

            if (tileRulesUIToolkitContainer != null)
            {
                var tileRulesList = tileRulesUIToolkitContainer.Q<VisualElement>("tile-rules-list");
                if (tileRulesList != null)
                    RefreshTileRulesList_UIToolkit(tileRulesList);
                else
                    RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
            }

            Repaint();
            SceneView.RepaintAll();
        }

        /// <summary>
        /// Creates a tool button for the action bar
        /// </summary>
        private Button CreateToolButton_V2(string icon, string tooltip, bool isActive, System.Action onClick)
        {
            var btn = new Button(onClick);
            
            // Check if icon is a file path (simple check for extension)
            if (icon.EndsWith(".png"))
            {
                var tex = LoadOverlayIconTexture(icon);
                if (tex != null)
                {
                    btn.style.backgroundImage = tex;
                    // Reset text just in case
                    btn.text = "";
                }
                else
                {
                    btn.text = icon; // Fallback
                }
            }
            else
            {
                btn.text = icon;
            }

            btn.tooltip = tooltip;
            btn.name = $"tool-{tooltip.ToLower().Replace(" ", "-")}";
            // Reduced to ~70% of 36px (36 * 0.7 = 25.2) -> 26px for cleaner look
            btn.style.width = 26;
            btn.style.height = 26;
            btn.style.marginLeft = 2;
            btn.style.marginRight = 2;
            btn.style.fontSize = 18; // font size might need reduction too if fallback text is used, but for icons w/h controls it.
            // Let's reduce font slightly too just in case
            btn.style.fontSize = 14; 
            btn.style.unityTextAlign = TextAnchor.MiddleCenter;
            btn.style.backgroundColor = isActive 
                ? new StyleColor(new Color(0.25f, 0.6f, 0.9f, 1f))
                : new StyleColor(new Color(0.15f, 0.15f, 0.15f, 0.8f));
            btn.style.color = new StyleColor(Color.white);
            btn.style.borderTopLeftRadius = 4;
            btn.style.borderTopRightRadius = 4;
            btn.style.borderBottomLeftRadius = 4;
            btn.style.borderBottomRightRadius = 4;
            return btn;
        }

        /// <summary>
        /// Updates all tool button states in V2 overlay
        /// </summary>
        private void UpdateToolButtonStates_V2()
        {
            if (actionBarV2 == null) return;

            var drawBtn = actionBarV2.Q<Button>("tool-draw");
            var selectBtn = actionBarV2.Q<Button>("tool-select");
            var eraseBtn = actionBarV2.Q<Button>("tool-erase");
            var eraseAllBtn = actionBarV2.Q<Button>("tool-erase-all");
            var moveBtn = actionBarV2.Q<Button>("tool-move");
            var pickerBtn = actionBarV2.Q<Button>("tool-picker");

            var activeColor = new StyleColor(new Color(0.25f, 0.6f, 0.9f, 1f));
            var inactiveColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f, 0.8f));

            if (drawBtn != null)
                drawBtn.style.backgroundColor = (drawMode && !isSelectionMode && !panToolActive && !pickerToolActive) ? activeColor : inactiveColor;
            if (selectBtn != null)
                selectBtn.style.backgroundColor = isSelectionMode ? activeColor : inactiveColor;
            if (eraseBtn != null)
                eraseBtn.style.backgroundColor = (!drawMode && eraseMode == EraseMode.Select && !panToolActive && !pickerToolActive) ? activeColor : inactiveColor;
            if (eraseAllBtn != null)
                eraseAllBtn.style.backgroundColor = (!drawMode && eraseMode == EraseMode.All && !panToolActive && !pickerToolActive) ? activeColor : inactiveColor;
            if (moveBtn != null)
                moveBtn.style.backgroundColor = panToolActive ? activeColor : inactiveColor;
            if (pickerBtn != null)
                pickerBtn.style.backgroundColor = pickerToolActive ? activeColor : inactiveColor;

            drawModeToggleV2?.SetValueWithoutNotify(drawMode);

            UpdateBrushSliderState_V2();
            UpdateSelectionStatusLabel_V2();
        }

        private void UpdateBrushSliderState_V2()
        {
            if (tilemapEditor == null || brushSliderControlV2 == null)
                return;

            int maxBrush = IsGameObjectsTabActive() ? 1 : 10;
            bool disableBrush = isSelectionMode || maxBrush == 1;
            int targetSize = Mathf.Clamp(tilemapEditor.brushSize, 1, maxBrush);
            if (disableBrush)
                targetSize = 1;

            brushSliderControlV2.highValue = maxBrush;
            brushSliderControlV2.SetEnabled(!disableBrush);

            if (tilemapEditor.brushSize != targetSize)
            {
                tilemapEditor.brushSize = targetSize;
                if (brushSizeProperty != null)
                {
                    brushSizeProperty.intValue = targetSize;
                    serializedObject.ApplyModifiedProperties();
                }
            }

            brushSliderControlV2.SetValueWithoutNotify(targetSize);
            UpdateBrushSizeLabel_V2();
        }

        /// <summary>
        /// Creates the brush size slider overlay
        /// </summary>
        private VisualElement CreateBrushSliderOverlay_V2()
        {
            brushSliderV2 = new VisualElement();
            brushSliderV2.name = "brush-slider-v2";
            brushSliderV2.style.position = Position.Absolute;
            brushSliderV2.style.top = 12;
            brushSliderV2.style.left = new Length(50, LengthUnit.Percent);
            brushSliderV2.style.translate = new StyleTranslate(new Translate(new Length(-50, LengthUnit.Percent), 0));
            brushSliderV2.style.flexDirection = FlexDirection.Row;
            brushSliderV2.style.alignItems = Align.Center;
            brushSliderV2.style.backgroundColor = new StyleColor(new Color(0.05f, 0.08f, 0.13f, 0.95f));
            brushSliderV2.style.paddingLeft = 12;
            brushSliderV2.style.paddingRight = 12;
            brushSliderV2.style.paddingTop = 6;
            brushSliderV2.style.paddingBottom = 6;
            brushSliderV2.style.borderTopLeftRadius = 6;
            brushSliderV2.style.borderTopRightRadius = 6;
            brushSliderV2.style.borderBottomLeftRadius = 6;
            brushSliderV2.style.borderBottomRightRadius = 6;

            // Title label
            var titleLabel = new Label("BRUSH");
            titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            titleLabel.style.fontSize = 10;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new StyleColor(Color.white);
            titleLabel.style.marginRight = 8;
            brushSliderV2.Add(titleLabel);

            // Create horizontal slider
            brushSliderControlV2 = new Slider(1, 10, SliderDirection.Horizontal);
            brushSliderControlV2.value = tilemapEditor?.brushSize ?? 1;
            brushSliderControlV2.style.width = 120;
            brushSliderControlV2.RegisterValueChangedCallback(evt => {
                int newSize = Mathf.RoundToInt(evt.newValue);
                if (tilemapEditor != null && newSize != tilemapEditor.brushSize)
                {
                    Undo.RecordObject(tilemapEditor, "Change Brush Size");
                    tilemapEditor.brushSize = newSize;
                    brushSizeProperty.intValue = newSize;
                    serializedObject.ApplyModifiedProperties();
                    UpdateBrushSizeLabel_V2();
                }
            });
            brushSliderV2.Add(brushSliderControlV2);

            // Size value label
            var sizeLabel = new Label(tilemapEditor?.brushSize.ToString() ?? "1");
            sizeLabel.name = "brush-size-label";
            sizeLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            sizeLabel.style.fontSize = 14;
            sizeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            sizeLabel.style.color = new StyleColor(new Color(0.4f, 0.8f, 1f, 1f));
            sizeLabel.style.marginLeft = 8;
            sizeLabel.style.minWidth = 20;
            brushSliderV2.Add(sizeLabel);

            // Brush Shape field
            var brushShapeField = new EnumField("Shape", tilemapEditor?.brushShape ?? QuickTilemapEditor.BrushShape.Square);
            brushShapeField.style.marginLeft = 12;
            brushShapeField.style.color = new StyleColor(Color.white);
            brushShapeField.style.fontSize = 11;
            brushShapeField.RegisterValueChangedCallback(evt => {
                if (tilemapEditor != null)
                {
                    Undo.RecordObject(tilemapEditor, "Change Brush Shape");
                    tilemapEditor.brushShape = (QuickTilemapEditor.BrushShape)evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    SceneView.RepaintAll();
                }
            });
            brushSliderV2.Add(brushShapeField);

            // Hex Grid toggle
            var hexToggle = new Toggle("Hex Grid");
            hexToggle.value = tilemapEditor?.useHexGrid ?? false;
            hexToggle.style.marginLeft = 12;
            hexToggle.style.color = new StyleColor(Color.white);
            // Make text smaller to fit
            hexToggle.style.fontSize = 11;
             
            hexToggle.RegisterValueChangedCallback(evt => {
                if (tilemapEditor != null)
                {
                    Undo.RecordObject(tilemapEditor, "Toggle Hex Grid");
                    tilemapEditor.useHexGrid = evt.newValue;
                    SceneView.RepaintAll();
                }
            });
            brushSliderV2.Add(hexToggle);

            UpdateBrushSliderState_V2();
            return brushSliderV2;
        }

        /// <summary>
        /// Updates the brush size label
        /// </summary>
        private void UpdateBrushSizeLabel_V2()
        {
            if (brushSliderV2 == null) return;
            var label = brushSliderV2.Q<Label>("brush-size-label");
            if (label != null && tilemapEditor != null)
            {
                label.text = tilemapEditor.brushSize.ToString();
            }
        }

        #endregion

        #endregion


        // ───────── SCENE GUI & BRUSH PREVIEW ─────────
        public void OnSceneGUI()
        {
            if (tilemapEditor == null || !tilemapEditor.editorEnabled) return;
            
            // Global Shortcuts
            HandleShortcuts();

            // Handle Move Tool
            if (panToolActive)
            {
                HandleMoveTool();
                return; 
            }
            
            // Handle Picker Tool
            if (pickerToolActive)
            {
                HandlePickerTool();
                return;
            }

            // Handle Draw Tool (Paint/Erase)
            if (drawMode || !isSelectionMode) // If not selecting, we are potentially drawing or erasing
            {
                 // Check if actually drawing (Mouse interactions)
                 HandleDrawTool();
            }

            // Draw Brush Preview on Repaint
            if (Event.current.type == EventType.Repaint)
            {
                // Verify we are in drawing mode and not panning/selecting/picking
                if ((drawMode || !isSelectionMode) && !panToolActive && !pickerToolActive)
                {
                    DrawBrushPreview();
                }
            }

            // ── Height handles: draggable Y-axis icons on each tilemap ──
            DrawHeightHandles();

        }
        
        private void HandleShortcuts()
        {
            Event evt = Event.current;
            if (evt.type == EventType.KeyDown && GUIUtility.hotControl == 0) // Only if not dragging
            {
                // Tools
                if (evt.keyCode == KeyCode.B) 
                {
                    drawMode = true;
                    eraseMode = EraseMode.Select;
                    isSelectionMode = false;
                    panToolActive = false;
                    pickerToolActive = false;
                    UpdateToolButtonStates_V2();
                    evt.Use();
                }
                else if (evt.keyCode == KeyCode.E) 
                {
                    drawMode = false; 
                    eraseMode = EraseMode.Select;
                    isSelectionMode = false;
                    panToolActive = false;
                    pickerToolActive = false;
                    UpdateToolButtonStates_V2();
                    evt.Use();
                }
                else if (evt.keyCode == KeyCode.M) 
                {
                    panToolActive = true;
                    drawMode = false; 
                    isSelectionMode = false;
                    pickerToolActive = false;
                    UpdateToolButtonStates_V2();
                    evt.Use();
                }
                else if (evt.keyCode == KeyCode.I) 
                {
                    pickerToolActive = true;
                    panToolActive = false;
                    drawMode = false;
                    isSelectionMode = false;
                    UpdateToolButtonStates_V2();
                    evt.Use();
                }
                else if (evt.keyCode == KeyCode.R) // Rotation
                {
                    brushRotation = (brushRotation + 90f) % 360f;
                    Debug.Log($"Rotation: {brushRotation}°");
                    evt.Use();
                }
            }
        }
        
        private void HandleDrawTool()
        {
             // Only handle mouse events for painting
             Event evt = Event.current;
             if (evt.type != EventType.MouseDown && evt.type != EventType.MouseDrag) return;
             if (evt.button != 0 || evt.alt) return; // Left click only

             // If pointer is over UI, ignore (DrawingSystemUI check?)
             // TODO: Check isPointerOverOverlay logic from GridUI if possible, or assume SceneView eats it?
             // Usually UIElements consume the event if over UI.
             
             if (tilemapEditor.targetTilemap == null) return;
             var grid = tilemapEditor.targetTilemap.layoutGrid;
             if (grid == null) return;

             Tilemap layoutMap = tilemapEditor.targetTilemap;
             Plane plane = new Plane(layoutMap.transform.up, layoutMap.transform.position);
             Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);

             if (plane.Raycast(ray, out float enter))
             {
                 Vector3 hitPoint = ray.GetPoint(enter);
                 Vector3Int centerCell = grid.WorldToCell(hitPoint);
                 
                 // Apply Paint or Erase
                 bool isErasing = !drawMode; // If not drawMode and not other tools -> Erase?
                 // Wait, eraser button sets drawMode=false. 
                 
                 int brushSize = tilemapEditor.brushSize;

                 // Register Undo once per drag?
                 // Unity's Undo groups actions by event.
                 Undo.RegisterCompleteObjectUndo(tilemapEditor, isErasing ? "Erase Tiles" : "Paint Tiles");
                 if (tilemapEditor.targetTilemap != null) Undo.RegisterCompleteObjectUndo(tilemapEditor.targetTilemap, isErasing ? "Erase Tiles" : "Paint Tiles");
                 foreach (var r in tilemapEditor.tileRules)
                     if (r.useCustomTilemap && r.customTargetTilemap) Undo.RegisterCompleteObjectUndo(r.customTargetTilemap, isErasing ? "Erase Tiles" : "Paint Tiles");

                 // Get brush cells based on shape
                 List<Vector3Int> brushCells = GetBrushCells(centerCell, brushSize, tilemapEditor.brushShape);

                 tilemapEditor.BeginProceduralSyncBatch();
                 try
                 {
	                     foreach (Vector3Int cell in brushCells)
	                     {
	                         if (isErasing)
	                         {
	                             EraseAt(cell);
                         }
                         else
                         {
                             PaintAt(cell);
	                         }
	                     }

                         if (IsTextureTabActive())
                             SyncVegetationAfterTexturePaintStroke();
	                 }
	                 finally
	                 {
	                     tilemapEditor.EndProceduralSyncBatch();
                 }
                 
                 Event.current.Use();
             }
        }

        /// <summary>
        /// Returns a list of brush cells based on the brush shape (square or circle).
        /// </summary>
        private List<Vector3Int> GetBrushCells(Vector3Int center, int brushSize, QuickTilemapEditor.BrushShape shape)
        {
            List<Vector3Int> cells = new List<Vector3Int>();

            if (IsGameObjectsTabActive())
            {
                cells.Add(center);
                return cells;
            }

            int offset = brushSize / 2;

            if (shape == QuickTilemapEditor.BrushShape.Circle)
            {
                float radius = brushSize / 2.0f;

                for (int x = 0; x < brushSize; x++)
                {
                    for (int y = 0; y < brushSize; y++)
                    {
                        int dx = x - offset;
                        int dy = y - offset;

                        // Check if cell is within circle radius
                        if (dx * dx + dy * dy <= radius * radius)
                        {
                            Vector3Int cell = new Vector3Int(center.x - offset + x, center.y - offset + y, center.z);
                            cells.Add(cell);
                        }
                    }
                }
            }
            else // Square shape (default)
            {
                for (int x = 0; x < brushSize; x++)
                {
                    for (int y = 0; y < brushSize; y++)
                    {
                        Vector3Int cell = new Vector3Int(center.x - offset + x, center.y - offset + y, center.z);
                        cells.Add(cell);
                    }
                }
            }

            return cells;
        }

        private bool HasSupportingTileAt_V2(Vector3Int pos)
        {
            if (tilemapEditor == null)
                return false;

            if (tilemapEditor.targetTilemap != null && tilemapEditor.targetTilemap.GetTile(pos) != null)
                return true;

            foreach (Tilemap tilemap in tilemapEditor.GetAllCustomTilemaps())
            {
                if (tilemap != null && tilemap.GetTile(pos) != null)
                    return true;
            }

            return false;
        }

        private void PaintPathPointAt_V2(Vector3Int cellPos)
        {
            var activePath = GetSelectedInspectorPath();
            if (activePath == null)
                return;

            if (activePath.points == null)
                activePath.points = new List<Vector3Int>();
            if (activePath.points.Contains(cellPos))
                return;

            activePath.points.Add(cellPos);
            SyncTrackPoints(activePath);

            if (tilemapEditor.placedObjects != null &&
                tilemapEditor.placedObjects.Any(placed => placed != null && placed.position == cellPos))
                tilemapEditor.AssignPathToObject(cellPos, tilemapEditor.selectedPathIndex);

            tilemapEditor.RebuildAllTrackMeshes();
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        private void ErasePathPointAt_V2(Vector3Int cellPos)
        {
            var activePath = GetSelectedInspectorPath();
            if (activePath == null || activePath.points == null)
                return;

            if (!activePath.points.Remove(cellPos))
                return;

            SyncTrackPoints(activePath);
            tilemapEditor.RefreshAllPathFollowers();
            tilemapEditor.RebuildAllTrackMeshes();
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        private bool TryFindPlacedGameObjectAtCell_V2(Vector3Int cellPos, out QuickTilemapEditor.PlacedObject placedObject, out GameObject instance, out Tilemap parentTilemap)
        {
            placedObject = tilemapEditor?.placedObjects?.FirstOrDefault(placed => placed != null && placed.position.x == cellPos.x && placed.position.y == cellPos.y);
            instance = null;
            parentTilemap = null;

            if (tilemapEditor?.instantiatedGameObjects == null)
                return placedObject != null;

            foreach (GameObject gameObject in tilemapEditor.instantiatedGameObjects)
            {
                if (gameObject == null)
                    continue;

                Tilemap parentMap = gameObject.transform.parent != null ? gameObject.transform.parent.GetComponent<Tilemap>() : null;
                if (parentMap == null)
                    continue;

                Vector3 adjustedPosition = gameObject.transform.position;
                adjustedPosition.y = parentMap.transform.position.y;
                Vector3Int gameObjectCell = tilemapEditor.SafeWorldToCell(adjustedPosition, parentMap);
                if (gameObjectCell.x == cellPos.x && gameObjectCell.y == cellPos.y)
                {
                    instance = gameObject;
                    parentTilemap = parentMap;
                    return true;
                }
            }

            return placedObject != null;
        }

        private void PaintGameObjectAt_V2(Vector3Int cellPos)
        {
            if (tilemapEditor == null ||
                tilemapEditor.selectedGameObjectRuleIndex < 0 ||
                tilemapEditor.selectedGameObjectRuleIndex >= tilemapEditor.gameObjectRules.Count)
                return;

            var goRule = tilemapEditor.gameObjectRules[tilemapEditor.selectedGameObjectRuleIndex];
            if (goRule == null || goRule.prefab == null)
                return;

            tilemapEditor.instantiatedGameObjects ??= new List<GameObject>();
            tilemapEditor.placedObjects ??= new List<QuickTilemapEditor.PlacedObject>();

            if (TryFindPlacedGameObjectAtCell_V2(cellPos, out QuickTilemapEditor.PlacedObject existingPlaced, out GameObject existingInstance, out Tilemap existingParent))
            {
                if (existingInstance != null)
                {
                    Undo.RecordObject(existingInstance.transform, "Rotate GameObject");
                    float newRotation = (existingInstance.transform.eulerAngles.y + 45f) % 360f;
                    existingInstance.transform.eulerAngles = new Vector3(existingInstance.transform.eulerAngles.x, newRotation, existingInstance.transform.eulerAngles.z);
                    if (existingPlaced != null)
                        existingPlaced.rotation = newRotation;

                    EditorUtility.SetDirty(existingInstance);
                    EditorUtility.SetDirty(tilemapEditor);
                    return;
                }

                if (existingPlaced != null)
                {
                    existingPlaced.rotation = (existingPlaced.rotation + 45f) % 360f;
                    tilemapEditor.ResynchronizeGameObjectsFromScene();
                    EditorUtility.SetDirty(tilemapEditor);
                    return;
                }
            }

            if (goRule.placeOnGround && !HasSupportingTileAt_V2(cellPos))
            {
                EditorUtility.DisplayDialog(
                    "Place on Ground Alert",
                    "No tile exists at this location. This object is set to 'Place on Ground' only.",
                    "OK");
                return;
            }

            Vector3 tileWorldPos = tilemapEditor.GetPlacementWorldPos(cellPos);
            Tilemap parentTilemap = tilemapEditor.targetTilemap;
            float highestY = float.MinValue;

            if (tilemapEditor.targetTilemap != null && tilemapEditor.targetTilemap.GetTile(cellPos) != null)
            {
                highestY = Mathf.Max(highestY, tilemapEditor.SafeCellToWorld(cellPos).y);
                parentTilemap = tilemapEditor.targetTilemap;
            }

            foreach (Tilemap tilemap in tilemapEditor.GetAllCustomTilemaps())
            {
                if (tilemap == null || tilemap.GetTile(cellPos) == null)
                    continue;

                if (tilemap.transform.position.y >= highestY)
                {
                    highestY = tilemap.transform.position.y;
                    parentTilemap = tilemap;
                }
            }

            if (highestY == float.MinValue)
                highestY = 0f;

            tileWorldPos.y = highestY + goRule.yOffset;

            GameObject placedGameObject = PrefabUtility.InstantiatePrefab(goRule.prefab) as GameObject;
            if (placedGameObject == null)
                return;

            placedGameObject.transform.position = tileWorldPos;
            placedGameObject.transform.SetParent(null, true);
            placedGameObject.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            Undo.RegisterCreatedObjectUndo(placedGameObject, "Place GameObject");

            if (parentTilemap != null)
                placedGameObject.transform.SetParent(parentTilemap.transform, true);

            tilemapEditor.instantiatedGameObjects.Add(placedGameObject);

            goRule.instanceOffsets ??= new List<QuickTilemapEditor.InstanceOffset>();
            goRule.instanceOffsets.Add(new QuickTilemapEditor.InstanceOffset
            {
                instanceObject = placedGameObject,
                yOffset = goRule.yOffset
            });

            if (string.IsNullOrEmpty(goRule.id))
                goRule.id = System.Guid.NewGuid().ToString();

            int newPathIndex = -1;
            if (tilemapEditor.selectedPathIndex >= 0 && tilemapEditor.selectedPathIndex < tilemapEditor.paths.Count)
            {
                var currentPath = tilemapEditor.paths[tilemapEditor.selectedPathIndex];
                if (currentPath != null && currentPath.points != null && currentPath.points.Contains(cellPos))
                    newPathIndex = tilemapEditor.selectedPathIndex + 1;
            }

            float instanceYOffset = tilemapEditor.ComputeInstanceYOffset(parentTilemap, cellPos, placedGameObject.transform.position);
            var placedData = new QuickTilemapEditor.PlacedObject
            {
                position = cellPos,
                ruleIndex = tilemapEditor.selectedGameObjectRuleIndex,
                ruleId = goRule.id,
                color = goRule.color,
                pathIndex = newPathIndex,
                rotation = placedGameObject.transform.eulerAngles.y,
                parentTilemapName = parentTilemap != null ? parentTilemap.name : string.Empty,
                prefabResourcePath = AssetDatabase.GetAssetPath(goRule.prefab)
            };
            placedData.instanceYOffset = instanceYOffset;
            placedData.MarkInstanceYOffsetUpgraded();
            tilemapEditor.placedObjects.Add(placedData);

            var marker = placedGameObject.GetComponent<QuickTileMarker>() ?? placedGameObject.AddComponent<QuickTileMarker>();
            marker.Initialize(
                placedData.UniqueId,
                tilemapEditor.GetInstanceID().ToString(),
                tilemapEditor.selectedGameObjectRuleIndex,
                cellPos);

            if (newPathIndex > 0)
                tilemapEditor.AssignPathToObject(cellPos, newPathIndex - 1);

            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        private void EraseAt(Vector3Int pos)
        {
            if (IsPathTabActive())
            {
                ErasePathPointAt_V2(pos);
                return;
            }

            if (IsGameObjectsTabActive())
            {
                tilemapEditor.EraseGameObjectDot(pos);
                EditorUtility.SetDirty(tilemapEditor);
                SceneView.RepaintAll();
                return;
            }

            if (IsTextureTabActive())
            {
                tilemapEditor.EraseTextureCell(pos);
                tilemapEditor.UpdateBlendPreviewMaterial();
                tilemapEditor.needsRefreshPreview = true;
                return;
            }

            if (eraseMode == EraseMode.All)
                tilemapEditor.EraseTileAtAllHeights(pos);
            else
                tilemapEditor.EraseTileAtSelectedLayer(pos);
        }
        
        private void PaintAt(Vector3Int pos)
        {
            if (IsPathTabActive())
            {
                PaintPathPointAt_V2(pos);
                return;
            }

            if (IsGameObjectsTabActive())
            {
                PaintGameObjectAt_V2(pos);
                return;
            }

            if (IsTextureTabActive())
            {
                if (tilemapEditor.selectedTextureRule != null &&
                    (!tilemapEditor.paintOnlyOnTiles || HasSupportingTileAt_V2(pos)))
                {
                    tilemapEditor.PaintTextureCell(pos, tilemapEditor.selectedTextureRule);
                    tilemapEditor.needsRefreshPreview = true;
                }
                return;
            }

            // Rotation Matrix
            Matrix4x4 matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0, 0, brushRotation), Vector3.one);
            
            var selectedRule = tilemapEditor.GetSelectedTileRule();
            if (selectedRule != null && selectedRule.tile != null)
            {
                tilemapEditor.PaintTile(pos, selectedRule);

                Tilemap targetMap = selectedRule.useCustomTilemap && selectedRule.customTargetTilemap != null
                    ? selectedRule.customTargetTilemap
                    : (Mathf.Abs(selectedRule.yOffset) > 0.001f
                        ? (tilemapEditor.heightTilemaps.ContainsKey(selectedRule.yOffset)
                            ? tilemapEditor.heightTilemaps[selectedRule.yOffset]
                            : (tilemapEditor.heightTilemaps[selectedRule.yOffset] = tilemapEditor.CreateTilemapForRule(selectedRule)))
                        : tilemapEditor.targetTilemap);

                if (targetMap != null)
                {
                    targetMap.SetTransformMatrix(pos, matrix);
                    targetMap.SetTileFlags(pos, TileFlags.None);
                }

                return;
            }

            if (tilemapEditor.activeTile != null && tilemapEditor.targetTilemap != null)
            {
                tilemapEditor.targetTilemap.SetTile(pos, tilemapEditor.activeTile);
                tilemapEditor.targetTilemap.SetTransformMatrix(pos, matrix);
                tilemapEditor.targetTilemap.SetTileFlags(pos, TileFlags.None);
            }
        }

        private void HandlePickerTool()
        {
            if (tilemapEditor.targetTilemap == null) return;
            var grid = tilemapEditor.targetTilemap.layoutGrid;
            if (grid == null) return;

            Event evt = Event.current;
            
            // Draw Picker Cursor/Preview?
             if (evt.type == EventType.Repaint)
             {
                 Ray previewRay = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                 Plane plane = new Plane(grid.transform.up, grid.transform.position);
                 if (plane.Raycast(previewRay, out float enter))
                 {
                     Vector3 center = grid.GetCellCenterWorld(grid.WorldToCell(previewRay.GetPoint(enter)));
                     Handles.color = Color.yellow;
                     Handles.DrawWireDisc(center, grid.transform.up, grid.cellSize.x * 0.4f);
                 }
             }

            if (evt.type == EventType.MouseDown && evt.button == 0 && !evt.alt)
            {
                Plane plane = new Plane(grid.transform.up, grid.transform.position);
                Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                if (plane.Raycast(ray, out float enter))
                {
                    Vector3Int cellPos = grid.WorldToCell(ray.GetPoint(enter));
                    PickTileAt(cellPos);
                    UpdateToolButtonStates_V2();
                    evt.Use();
                }
            }
        }
        
        private void PickTileAt(Vector3Int cellPos, bool allowTexture = true)
        {
            if (tilemapEditor == null || tilemapEditor.targetTilemap == null)
                return;

            var candidates = BuildPickerCandidates(cellPos, allowTexture);
            if (candidates.Count == 0)
            {
                Debug.Log($"[QuickTile] Aucun asset trouve a {cellPos}");
                return;
            }

            string pickerSignature = string.Join("|", candidates.Select(candidate => candidate.signature));
            if (hasLastPickerCell && lastPickerCell == cellPos && lastPickerSignature == pickerSignature)
                pickerCycleIndex = (pickerCycleIndex + 1) % candidates.Count;
            else
                pickerCycleIndex = 0;

            hasLastPickerCell = true;
            lastPickerCell = cellPos;
            lastPickerSignature = pickerSignature;

            ApplyPickerCandidate(candidates[pickerCycleIndex], cellPos);
        }

        private List<PickerCandidate> BuildPickerCandidates(Vector3Int cellPos, bool allowTexture)
        {
            var candidates = new List<PickerCandidate>();

            if (allowTexture &&
                tilemapEditor.texturePaintRules != null &&
                tilemapEditor.texturePaintMask != null &&
                tilemapEditor.texturePaintMask.TryGetValue(cellPos, out int texIndex))
            {
                var textureRule = tilemapEditor.texturePaintRules.FirstOrDefault(r => r.textureIndex == texIndex);
                if (textureRule != null)
                {
                    candidates.Add(new PickerCandidate
                    {
                        kind = PickerCandidateKind.Texture,
                        ruleIndex = tilemapEditor.texturePaintRules.IndexOf(textureRule),
                        textureRule = textureRule,
                        feedbackColor = Color.white,
                        signature = $"tex:{textureRule.textureIndex}"
                    });
                }
            }

            var tilesAtPosition = new List<(TileBase tile, QuickTilemapEditor.TileRule rule, int ruleIndex)>();

            TileBase baseTile = tilemapEditor.targetTilemap.GetTile(cellPos);
            if (baseTile != null)
            {
                var baseRule = new QuickTilemapEditor.TileRule
                {
                    tile = baseTile,
                    useCustomTilemap = false,
                    customTargetTilemap = tilemapEditor.targetTilemap,
                    yOffset = 0f,
                    color = tilemapEditor.targetTilemap.GetColor(cellPos)
                };
                tilesAtPosition.Add((baseTile, baseRule, -1));
            }

            for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
            {
                var rule = tilemapEditor.tileRules[i];
                if (rule == null || !rule.isVisible) continue;

                Tilemap targetMap = rule.useCustomTilemap && rule.customTargetTilemap != null
                    ? rule.customTargetTilemap
                    : (Mathf.Abs(rule.yOffset) > 0.001f && tilemapEditor.heightTilemaps.ContainsKey(rule.yOffset)
                        ? tilemapEditor.heightTilemaps[rule.yOffset]
                        : null);

                if (targetMap == null) continue;

                TileBase tile = targetMap.GetTile(cellPos);
                if (tile != null)
                    tilesAtPosition.Add((tile, rule, i));
            }

            tilesAtPosition.Sort((a, b) => b.rule.yOffset.CompareTo(a.rule.yOffset));
            foreach (var tileEntry in tilesAtPosition)
            {
                candidates.Add(new PickerCandidate
                {
                    kind = PickerCandidateKind.Tile,
                    tile = tileEntry.tile,
                    tileRule = tileEntry.rule,
                    ruleIndex = tileEntry.ruleIndex,
                    feedbackColor = tileEntry.rule.color,
                    signature = tileEntry.ruleIndex >= 0
                        ? $"tile-rule:{tileEntry.ruleIndex}"
                        : $"tile-base:{tileEntry.tile?.name}:{tileEntry.rule.yOffset:0.###}"
                });
            }

            var objectCandidates = tilemapEditor.placedObjects
                .Where(placedObject => placedObject != null && placedObject.position == cellPos)
                .OrderByDescending(placedObject => placedObject.instanceYOffset)
                .ToList();

            foreach (var placedObject in objectCandidates)
            {
                int ruleIndex = ResolveGameObjectRuleIndexForPicker(placedObject);
                if (ruleIndex < 0 || ruleIndex >= tilemapEditor.gameObjectRules.Count)
                    continue;

                var rule = tilemapEditor.gameObjectRules[ruleIndex];
                if (rule == null || !rule.isVisible)
                    continue;

                candidates.Add(new PickerCandidate
                {
                    kind = PickerCandidateKind.GameObject,
                    ruleIndex = ruleIndex,
                    gameObjectRule = rule,
                    feedbackColor = rule.color,
                    signature = $"go:{placedObject.UniqueId}:{ruleIndex}"
                });
            }

            if (tilemapEditor.paths != null)
            {
                for (int i = 0; i < tilemapEditor.paths.Count; i++)
                {
                    var path = tilemapEditor.paths[i];
                    if (path?.points == null || !path.isVisible)
                        continue;

                    if (!path.points.Contains(cellPos))
                        continue;

                    candidates.Add(new PickerCandidate
                    {
                        kind = PickerCandidateKind.Path,
                        pathIndex = i,
                        path = path,
                        feedbackColor = path.color,
                        signature = $"path:{i}"
                    });
                }
            }

            return candidates;
        }

        private int ResolveGameObjectRuleIndexForPicker(QuickTilemapEditor.PlacedObject placedObject)
        {
            if (placedObject == null || tilemapEditor?.gameObjectRules == null)
                return -1;

            if (!string.IsNullOrEmpty(placedObject.ruleId))
            {
                int foundIndex = tilemapEditor.gameObjectRules.FindIndex(rule => rule != null && rule.id == placedObject.ruleId);
                if (foundIndex >= 0)
                    return foundIndex;
            }

            if (placedObject.ruleIndex >= 0 && placedObject.ruleIndex < tilemapEditor.gameObjectRules.Count)
                return placedObject.ruleIndex;

            return -1;
        }

        private void ApplyPickerCandidate(PickerCandidate candidate, Vector3Int cellPos)
        {
            if (candidate == null)
                return;

            switch (candidate.kind)
            {
                case PickerCandidateKind.Texture:
                    tilemapEditor.selectedTextureRuleIndex = candidate.ruleIndex;
                    tilemapEditor.selectedTextureRule = candidate.textureRule;
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                    SetInspectorTab(1, true);
                    break;

                case PickerCandidateKind.GameObject:
                    tilemapEditor.selectedGameObjectRuleIndex = candidate.ruleIndex;
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                    tilemapEditor.selectedTextureRule = null;
                    tilemapEditor.selectedTextureRuleIndex = -1;
                    SetInspectorTab(2, true);
                    break;

                case PickerCandidateKind.Path:
                    tilemapEditor.selectedPathIndex = candidate.pathIndex;
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedTextureRule = null;
                    tilemapEditor.selectedTextureRuleIndex = -1;
                    SetInspectorTab(3, true);
                    break;

                default:
                    ApplyTilePickerCandidate(candidate);
                    break;
            }

            RefreshPickerSelectionUI();
            UpdateToolButtonStates_V2();
            ShowTemporarySelectionFeedback(cellPos, candidate.feedbackColor);
            serializedObject.Update();
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(tilemapEditor);
            Repaint();
        }

        private void ApplyTilePickerCandidate(PickerCandidate candidate)
        {
            if (candidate == null || candidate.tile == null)
                return;

            if (candidate.ruleIndex >= 0)
            {
                tilemapEditor.selectedTileRuleIndex = candidate.ruleIndex;
            }
            else
            {
                bool hasMatchingRule = false;
                for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
                {
                    var rule = tilemapEditor.tileRules[i];
                    if (rule.tile == candidate.tile && Mathf.Abs(rule.yOffset) < 0.001f)
                    {
                        tilemapEditor.selectedTileRuleIndex = i;
                        hasMatchingRule = true;
                        break;
                    }
                }

                if (!hasMatchingRule)
                {
                    tilemapEditor.tileRules.Add(new QuickTilemapEditor.TileRule
                    {
                        tile = candidate.tile,
                        useCustomTilemap = false,
                        yOffset = 0f,
                        color = candidate.tileRule != null ? candidate.tileRule.color : Color.white,
                        renderOrder = 0,
                        isVisible = true
                    });
                    tilemapEditor.selectedTileRuleIndex = tilemapEditor.tileRules.Count - 1;
                }
            }

            tilemapEditor.selectedGameObjectRuleIndex = -1;
            tilemapEditor.selectedPathIndex = -1;
            tilemapEditor.selectedTextureRule = null;
            tilemapEditor.selectedTextureRuleIndex = -1;
            tilemapEditor.activeTile = candidate.tile;
            SetInspectorTab(0, true);
        }

        private void RefreshPickerSelectionUI()
        {
            if (!useUIToolkit)
                return;

            if (tileRulesUIToolkitContainer != null)
            {
                var tileRulesList = tileRulesUIToolkitContainer.Q<VisualElement>("tile-rules-list");
                if (tileRulesList != null)
                    RefreshTileRulesList_UIToolkit(tileRulesList);
            }

            if (gameObjectRulesUIToolkitContainer != null)
            {
                var gameObjectRulesList = gameObjectRulesUIToolkitContainer.Q<VisualElement>("gameobject-rules-list");
                if (gameObjectRulesList != null)
                    RefreshGameObjectRulesList_UIToolkit(gameObjectRulesList);
            }

            if (pathUIToolkitContainer != null)
            {
                var pathsList = pathUIToolkitContainer.Q<VisualElement>("paths-list");
                if (pathsList != null)
                    RefreshPathList_UIToolkit(pathsList);
            }

            RefreshTexturePaintSectionIfNeeded_UIToolkit(true);
        }

        private void HandleMoveTool()
        {
             if (tilemapEditor.targetTilemap == null) return;
            var grid = tilemapEditor.targetTilemap.layoutGrid;
            if (grid == null) return;

            Event evt = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            switch (evt.GetTypeForControl(controlID))
            {
                case EventType.MouseDown:
                    if (evt.button == 0 && !evt.alt && selectedCells != null && selectedCells.Count > 0) // Left click to drag
                    {
                        // Raycast to find cell
                        Plane plane = new Plane(grid.transform.up, grid.transform.position);
                        Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                        if (plane.Raycast(ray, out float enter))
                        {
                            Vector3 hitPoint = ray.GetPoint(enter);
                            Vector3Int cellPos = grid.WorldToCell(hitPoint);
                            
                            // Start dragging
                            isDraggingSelectionV2 = true;
                            dragStartMouseCellV2 = cellPos;
                            selectionOffsetV2 = Vector3Int.zero;
                            
                            GUIUtility.hotControl = controlID;
                            evt.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == controlID && isDraggingSelectionV2)
                    {
                        Plane plane = new Plane(grid.transform.up, grid.transform.position);
                        Ray ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
                        if (plane.Raycast(ray, out float enter))
                        {
                            Vector3 hitPoint = ray.GetPoint(enter);
                            Vector3Int currentCell = grid.WorldToCell(hitPoint);
                            
                            selectionOffsetV2 = currentCell - dragStartMouseCellV2;
                            evt.Use();
                        }
                    }
                    break;

                case EventType.MouseUp:
                    if (GUIUtility.hotControl == controlID && isDraggingSelectionV2)
                    {
                        isDraggingSelectionV2 = false;
                        GUIUtility.hotControl = 0;
                        evt.Use();

                        if (selectionOffsetV2 != Vector3Int.zero && selectedCells != null && selectedCells.Count > 0)
                        {
                            ApplyMoveSelectionV2(selectionOffsetV2);
                             selectionOffsetV2 = Vector3Int.zero;
                        }
                    }
                    break;
                
                case EventType.Repaint:
                    if (isDraggingSelectionV2 && selectedCells != null && selectedCells.Count > 0)
                    {
                        // Draw preview of selected cells at offset
                        Handles.color = new Color(1f, 1f, 0f, 0.5f); // Yellow for move
                        foreach (var cell in selectedCells)
                        {
                            Vector3Int targetPos = cell + selectionOffsetV2;
                            Vector3 center = grid.GetCellCenterWorld(targetPos);
                             // Assumes unit size/default layout, approximate for preview
                            Vector3 size = grid.cellSize; 
                            Handles.DrawWireCube(center, size);
                        }
                    }
                    break;
            }
        }

        private void ApplyMoveSelectionV2(Vector3Int offset)
        {
            if (tilemapEditor == null || selectedCells.Count == 0)
                return;

            Dictionary<Vector3Int, int> texturesToMove = new Dictionary<Vector3Int, int>();
            Dictionary<Vector3Int, Dictionary<int, TileData>> tilesToMove = new Dictionary<Vector3Int, Dictionary<int, TileData>>();
            List<Vector3Int> cellsToErase = new List<Vector3Int>(selectedCells);
            HashSet<Vector3Int> selectedCellsSet = new HashSet<Vector3Int>(selectedCells);
            List<Vector3Int> newSelectedCells = new List<Vector3Int>();

            foreach (Vector3Int pos in selectedCells)
            {
                Vector3Int newPos = pos + offset;
                newSelectedCells.Add(newPos);

                if (tilemapEditor.texturePaintMask.TryGetValue(pos, out int textureIndex))
                    texturesToMove[newPos] = textureIndex;

                var tilesAtPosition = new Dictionary<int, TileData>();
                for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
                {
                    var rule = tilemapEditor.tileRules[i];
                    Tilemap targetMap = ResolveRuleTilemapForDisplay_V2(rule);
                    if (targetMap == null)
                        continue;

                    TileBase tile = targetMap.GetTile(pos);
                    if (tile != null)
                    {
                        tilesAtPosition[i] = new TileData
                        {
                            tile = tile,
                            color = targetMap.GetColor(pos),
                            targetMap = targetMap,
                            transform = targetMap.GetTransformMatrix(pos)
                        };
                    }
                }

                if (tilemapEditor.targetTilemap != null)
                {
                    TileBase tile = tilemapEditor.targetTilemap.GetTile(pos);
                    if (tile != null)
                    {
                        tilesAtPosition[-1] = new TileData
                        {
                            tile = tile,
                            color = tilemapEditor.targetTilemap.GetColor(pos),
                            targetMap = tilemapEditor.targetTilemap,
                            transform = tilemapEditor.targetTilemap.GetTransformMatrix(pos)
                        };
                    }
                }

                if (tilesAtPosition.Count > 0)
                    tilesToMove[newPos] = tilesAtPosition;
            }

            Undo.RegisterCompleteObjectUndo(tilemapEditor, "Move Selection");
            if (tilemapEditor.targetTilemap != null)
                Undo.RegisterCompleteObjectUndo(tilemapEditor.targetTilemap, "Move Selection");

            foreach (var rule in tilemapEditor.tileRules)
            {
                Tilemap targetMap = ResolveRuleTilemapForDisplay_V2(rule);
                if (targetMap != null)
                    Undo.RegisterCompleteObjectUndo(targetMap, "Move Selection");
            }

            tilemapEditor.BeginProceduralSyncBatch();
            try
            {
                foreach (Vector3Int originalPos in cellsToErase)
                {
                    tilemapEditor.texturePaintMask.Remove(originalPos);

                    if (tilemapEditor.targetTilemap != null)
                        tilemapEditor.targetTilemap.SetTile(originalPos, null);

                    foreach (var rule in tilemapEditor.tileRules)
                    {
                        Tilemap targetMap = ResolveRuleTilemapForDisplay_V2(rule);
                        if (targetMap != null)
                            targetMap.SetTile(originalPos, null);
                    }
                }

	                foreach (var textureEntry in texturesToMove)
	                {
	                    if (textureEntry.Value >= 0 && textureEntry.Value < tilemapEditor.texturePaintRules.Count)
	                        tilemapEditor.PaintTextureCell(textureEntry.Key, textureEntry.Value);
	                }

                    SyncVegetationAfterTexturePaintStroke();

	                foreach (var movedTile in tilesToMove)
	                {
                    foreach (var tileEntry in movedTile.Value)
                    {
                        TileData tileData = tileEntry.Value;
                        if (tileData.tile == null || tileData.targetMap == null)
                            continue;

                        tileData.targetMap.SetTile(movedTile.Key, tileData.tile);
                        tileData.targetMap.SetTileFlags(movedTile.Key, TileFlags.None);
                        tileData.targetMap.SetColor(movedTile.Key, tileData.color);
                        tileData.targetMap.SetTransformMatrix(movedTile.Key, tileData.transform);
                    }
                }
            }
            finally
            {
                tilemapEditor.EndProceduralSyncBatch();
            }

            tilemapEditor.UpdatePaintMaskTexture();
            tilemapEditor.UpdateBlendPreviewMaterial();

            List<QuickTilemapEditor.PlacedObject> updatedPlacedObjects = new List<QuickTilemapEditor.PlacedObject>();
            HashSet<string> movedPlacedObjectIds = new HashSet<string>();
            HashSet<GameObject> processedGameObjects = new HashSet<GameObject>();

            foreach (Vector3Int originalPos in cellsToErase)
            {
                Vector3Int newPos = originalPos + offset;

                for (int i = 0; i < tilemapEditor.placedObjects.Count; i++)
                {
                    var placedObject = tilemapEditor.placedObjects[i];
                    if (placedObject == null || placedObject.position != originalPos)
                        continue;

                    movedPlacedObjectIds.Add(placedObject.UniqueId);
                    var movedObject = placedObject;
                    movedObject.position = newPos;
                    updatedPlacedObjects.Add(movedObject);

                    foreach (GameObject gameObject in tilemapEditor.instantiatedGameObjects)
                    {
                        if (gameObject == null || processedGameObjects.Contains(gameObject))
                            continue;

                        Tilemap parentMap = gameObject.transform.parent?.GetComponent<Tilemap>();
                        if (parentMap == null)
                            continue;

                        Vector3Int goCell = tilemapEditor.SafeWorldToCell(gameObject.transform.position, parentMap);
                        if (goCell != originalPos)
                            continue;

                        Undo.RecordObject(gameObject.transform, "Move GameObject");
                        Vector3 worldPos = tilemapEditor.GetPlacementWorldPos(newPos);
                        float yOffset = gameObject.transform.position.y - parentMap.transform.position.y;
                        worldPos.y = parentMap.transform.position.y + yOffset;
                        gameObject.transform.position = worldPos;

                        QuickTileMarker marker = gameObject.GetComponent<QuickTileMarker>();
                        if (marker != null)
                            marker.Initialize(movedObject.UniqueId, tilemapEditor.GetInstanceID().ToString(), movedObject.ruleIndex, newPos);

                        processedGameObjects.Add(gameObject);
                        break;
                    }
                }
            }

            foreach (var placedObject in tilemapEditor.placedObjects)
            {
                if (placedObject != null && !movedPlacedObjectIds.Contains(placedObject.UniqueId))
                    updatedPlacedObjects.Add(placedObject);
            }

            tilemapEditor.placedObjects = updatedPlacedObjects;

            if (tilemapEditor.paths != null)
            {
                for (int pathIndex = 0; pathIndex < tilemapEditor.paths.Count; pathIndex++)
                {
                    var path = tilemapEditor.paths[pathIndex];
                    if (path == null || path.points == null)
                        continue;

                    List<Vector3Int> updatedPoints = new List<Vector3Int>();
                    bool pathModified = false;

                    foreach (Vector3Int point in path.points)
                    {
                        if (selectedCellsSet.Contains(point))
                        {
                            updatedPoints.Add(point + offset);
                            pathModified = true;
                        }
                        else
                        {
                            updatedPoints.Add(point);
                        }
                    }

                    if (!pathModified)
                        continue;

                    path.points = updatedPoints;
                    SyncTrackPoints(path);
                }
            }

            tilemapEditor.RefreshAllPathFollowers();
            tilemapEditor.RebuildAllTrackMeshes();

            selectedCells.Clear();
            selectedCells.AddRange(newSelectedCells);

            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
            RefreshGridCellsV2();
        }

        private void DrawBrushPreview()
        {
            if (tilemapEditor.targetTilemap == null) return;
            var grid = tilemapEditor.targetTilemap.layoutGrid;
            if (grid == null) return;

            // Simple plane raycast for grid
            // Grid uses XZ plane (up is Y). We use the grid's transform plane.
            // Ensure we use the grid's world position/rotation
            Plane plane = new Plane(grid.transform.up, grid.transform.position);
            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);

            if (plane.Raycast(ray, out float enter))
            {
                Vector3 hitPoint = ray.GetPoint(enter);
                Vector3Int cellPos = grid.WorldToCell(hitPoint);

                // Calculate brush bounds
                int brushSize = tilemapEditor.brushSize;
                int brushOffset = brushSize / 2;

                Handles.color = new Color(0f, 1f, 1f, 0.5f); // Cyan

                // Draw brush preview based on shape
                if (tilemapEditor.brushShape == QuickTilemapEditor.BrushShape.Circle)
                {
                    // Draw circle using brush cells
                    float radius = brushSize / 2.0f;
                    Vector3 brushCenterWorld = grid.CellToWorld(cellPos);

                    // Draw circle outline
                    int circleSegments = Mathf.Max(8, brushSize * 2);
                    float angleStep = 360f / circleSegments;
                    Vector3 lastPoint = brushCenterWorld;

                    for (int i = 0; i <= circleSegments; i++)
                    {
                        float angle = i * angleStep * Mathf.Deg2Rad;
                        float cellDist = radius * 0.5f; // Approximate cell size
                        Vector3 point = brushCenterWorld + new Vector3(Mathf.Cos(angle) * cellDist, Mathf.Sin(angle) * cellDist, 0);

                        if (i > 0)
                        {
                            Handles.DrawLine(lastPoint, point);
                        }
                        lastPoint = point;
                    }
                }
                else // Square shape
                {
                    // Brush logic: range [cell - offset, cell - offset + size]
                    Vector3Int minCell = new Vector3Int(cellPos.x - brushOffset, cellPos.y - brushOffset, cellPos.z);
                    Vector3Int maxCell = new Vector3Int(minCell.x + brushSize, minCell.y + brushSize, cellPos.z);

                    Vector3 minWorld = grid.CellToWorld(minCell);
                    Vector3 maxWorld = grid.CellToWorld(maxCell);

                    Vector3 center = (minWorld + maxWorld) * 0.5f;
                    Vector3 size = maxWorld - minWorld;

                    // Force small thickness for visibility if flat
                    if (Mathf.Abs(size.y) < 0.01f) size.y = 0.1f;

                    Handles.DrawWireCube(center, size);
                }
            }
        }

    }
}
