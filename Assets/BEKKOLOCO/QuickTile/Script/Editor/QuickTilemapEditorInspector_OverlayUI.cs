using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        private const string OverlayDrawIconAsset = "brush_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24 (1).png";
        private const string OverlaySelectIconAsset = "lasso_select_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png";
        private const string OverlayEraseIconAsset = "eraser_size_3_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png";
        private const string OverlayEraseAllIconAsset = "eraser_size_5_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png";
        private const string OverlayMoveIconAsset = "drag_pan_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24 (1).png";
        private const string OverlayPickerIconAsset = "colorize_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png";
        private const string OverlayClearIconAsset = "delete_forever_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24 (1).png";

        private void DrawGridOverlayControls(Rect actionRect, Rect brushRect)
        {
            if (Event.current == null)
                return;

            if (actionRect.width > 0f && actionRect.height > 0f)
            {
                DrawActionStatusOverlay(actionRect);
                DrawActionOverlay(actionRect);
            }

            if (brushRect.width > 0f && brushRect.height > 0f)
            {
                DrawBrushOverlay(brushRect);
            }
        }

        private void DrawActionOverlay(Rect actionRect)
        {
            GUI.BeginGroup(actionRect);
            Rect innerRect = new Rect(8f, 6f, Mathf.Max(0f, actionRect.width - 16f), Mathf.Max(0f, actionRect.height - 12f));
            GUILayout.BeginArea(innerRect);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EnsureOverlayIcons();

            DrawOverlayIconButton(OverlayDrawIconAsset, drawToolContent, drawMode && !isSelectionMode && !panToolActive && !pickerToolActive, ActivateDrawMode);
            DrawOverlayIconButton(OverlayEraseIconAsset, eraseToolContent, !drawMode && eraseMode == EraseMode.Select && !panToolActive && !pickerToolActive, ActivateEraseSelectMode);
            DrawOverlayIconButton(OverlayEraseAllIconAsset, eraseAllToolContent, !drawMode && eraseMode == EraseMode.All && !panToolActive && !pickerToolActive, ActivateEraseAllMode);
            DrawOverlayIconButton(OverlayMoveIconAsset, panToolContent, panToolActive, TogglePanToolMode);
            DrawOverlayIconButton(OverlaySelectIconAsset, selectionToolContent, isSelectionMode, ToggleSelectionMode);
            DrawOverlayIconButton(OverlayPickerIconAsset, pickerToolContent, pickerToolActive, ActivatePickerMode);
            GUILayout.Space(6f);
            DrawOverlayIconButton(OverlayClearIconAsset, clearToolContent, false, ClearAllTilemapContent);
            GUILayout.FlexibleSpace();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.EndGroup();
        }

        private void DrawActionStatusOverlay(Rect actionRect)
        {
            string statusText = GetGridOverlayStatusText();
            if (string.IsNullOrWhiteSpace(statusText))
                return;

            Rect badgeRect = CalculateActionStatusRect(actionRect, statusText);
            if (badgeRect.width <= 0f || badgeRect.height <= 0f)
                return;

            DrawOverlayPanelBackground(badgeRect);

            GUIStyle labelStyle = GetOverlayStatusLabelStyle();
            Rect labelRect = new Rect(badgeRect.x + 10f, badgeRect.y + 4f, badgeRect.width - 20f, badgeRect.height - 8f);
            GUI.Label(labelRect, statusText, labelStyle);
        }

        private string GetGridOverlayStatusText()
        {
            if (tilemapEditor == null)
                return string.Empty;

            string toolStatus = GetActiveToolStatusText_V2();
            string targetStatus = GetActiveSelectionStatusText_V2();
            string selectionSummary = selectedCells.Count > 0 && (isSelectionMode || panToolActive)
                ? $" | 🔲 {selectedCells.Count}"
                : string.Empty;

            return $"{toolStatus} | {targetStatus}{selectionSummary}";
        }

        private Rect CalculateActionStatusRect(Rect actionRect, string statusText)
        {
            if (actionRect.width <= 0f || actionRect.height <= 0f)
                return Rect.zero;

            GUIStyle labelStyle = GetOverlayStatusLabelStyle();
            float width = Mathf.Clamp(labelStyle.CalcSize(new GUIContent(statusText)).x + 28f, 180f, Mathf.Max(180f, actionRect.width + 40f));
            float height = 32f;
            float x = actionRect.center.x - width * 0.5f;
            float y = actionRect.y - height - 8f;
            return new Rect(x, y, width, height);
        }

        private void DrawBrushOverlay(Rect backgroundRect)
        {
            if (backgroundRect.width <= 0f || backgroundRect.height <= 0f)
                return;

            Color background = new Color(0.02f, 0.02f, 0.02f, 0.92f);
            EditorGUI.DrawRect(backgroundRect, background);
            EditorGUI.DrawRect(new Rect(backgroundRect.x, backgroundRect.y, backgroundRect.width, 1f), new Color(1f, 1f, 1f, 0.08f));
            EditorGUI.DrawRect(new Rect(backgroundRect.x, backgroundRect.yMax - 1f, backgroundRect.width, 1f), new Color(0f, 0f, 0f, 0.4f));

            GUI.BeginGroup(backgroundRect);
            GUIStyle labelStyle = GetOverlayLabelStyle();
            GUI.Label(new Rect(0f, 4f, backgroundRect.width, 18f), "BRUSH", labelStyle);

            int maxBrush = 10;
            if (tilemapEditor.selectedGameObjectRuleIndex >= 0 && drawMode)
            {
                maxBrush = 1;
            }

            bool disableBrush = isSelectionMode || maxBrush == 1;
            float sliderHeight = Mathf.Max(12f, backgroundRect.height - 52f);
            Rect sliderRect = new Rect((backgroundRect.width - 20f) * 0.5f, 24f, 20f, sliderHeight);

            EditorGUI.BeginDisabledGroup(disableBrush);
            float sliderValue = GUI.VerticalSlider(sliderRect, tilemapEditor.brushSize, maxBrush, 1f);
            EditorGUI.EndDisabledGroup();

            int newBrushSize = Mathf.Clamp(Mathf.RoundToInt(sliderValue), 1, maxBrush);
            if (disableBrush)
            {
                newBrushSize = 1;
            }

            GUI.Label(new Rect(0f, backgroundRect.height - 24f, backgroundRect.width, 20f), newBrushSize.ToString(), labelStyle);
            GUI.EndGroup();

            if (newBrushSize != tilemapEditor.brushSize)
            {
                Undo.RecordObject(tilemapEditor, "Change Brush Size");
                brushSizeProperty.intValue = newBrushSize;
                tilemapEditor.brushSize = newBrushSize;
                serializedObject.ApplyModifiedProperties();
                Repaint();
            }
        }

        private GUIContent drawToolContent;
        private GUIContent selectionToolContent;
        private GUIContent eraseToolContent;
        private GUIContent eraseAllToolContent;
        private GUIContent clearToolContent;
        private GUIContent panToolContent;
        private GUIContent pickerToolContent;

        private void EnsureOverlayIcons()
        {
            if (drawToolContent == null)
            {
                drawToolContent = CreateOverlayToolContent(OverlayDrawIconAsset, "Grid.PaintTool", "Draw", "Draw");
            }

            if (selectionToolContent == null)
            {
                selectionToolContent = CreateOverlayToolContent(OverlaySelectIconAsset, "Grid.SelectTool", "Select", "Edit selection");
            }

            if (eraseToolContent == null)
            {
                eraseToolContent = CreateOverlayToolContent(OverlayEraseIconAsset, "Grid.EraserTool", "Erase", "Erase");
            }

            if (eraseAllToolContent == null)
            {
                eraseAllToolContent = CreateOverlayToolContent(OverlayEraseAllIconAsset, "TreeEditor.Trash", "All", "Erase all tiles");
            }

            if (clearToolContent == null)
            {
                clearToolContent = CreateOverlayToolContent(OverlayClearIconAsset, "TreeEditor.Trash", "Clear", "Clear everything");
            }

            if (panToolContent == null)
            {
                panToolContent = CreateOverlayToolContent(OverlayMoveIconAsset, "ViewToolMove", "Move", "Pan view");
            }

            if (pickerToolContent == null)
            {
                pickerToolContent = CreateOverlayToolContent(OverlayPickerIconAsset, null, "Pick", "Picker");
            }
        }

        private GUIContent CreateOverlayToolContent(string assetFileName, string fallbackIconName, string fallbackText, string tooltip)
        {
            Texture2D assetIcon = LoadOverlayIconTexture(assetFileName);
            if (assetIcon != null)
                return new GUIContent(assetIcon, tooltip);

            return CreateIconContent(fallbackIconName, fallbackText, tooltip);
        }

        private GUIContent CreateIconContent(string iconName, string fallbackText, string tooltip)
        {
            if (!string.IsNullOrEmpty(iconName))
            {
                Texture iconTexture = null;
                try
                {
                    // FindTexture returns null quietly when an icon name is unavailable,
                    // unlike IconContent which can spam the console on newer Unity versions.
                    iconTexture = EditorGUIUtility.FindTexture(iconName);
                }
                catch { /* icon not available in this Unity version — use fallback */ }

                if (iconTexture != null)
                    return new GUIContent(iconTexture, tooltip);
            }

            return new GUIContent(fallbackText, tooltip);
        }

        private Texture2D LoadOverlayIconTexture(string assetFileName)
        {
            if (string.IsNullOrWhiteSpace(assetFileName))
                return null;

            if (assetFileName.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return AssetDatabase.LoadAssetAtPath<Texture2D>(assetFileName);
            }

            bool hasSubfolder = assetFileName.Contains("/");
            string[] candidatePaths = hasSubfolder
                ? new[] { $"Assets/BEKKOLOCO/QuickTile/Script/Editor/{assetFileName}" }
                : new[]
                {
                    $"Assets/BEKKOLOCO/QuickTile/Script/Editor/icon/{assetFileName}",
                    $"Assets/BEKKOLOCO/QuickTile/Script/Editor/{assetFileName}"
                };

            for (int i = 0; i < candidatePaths.Length; i++)
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(candidatePaths[i]);
                if (texture != null)
                    return texture;
            }

            return null;
        }

        private void DrawOverlayIconButton(string assetFileName, GUIContent fallbackContent, bool isActive, Action onClick, float width = 40f)
        {
            Texture2D iconTexture = LoadOverlayIconTexture(assetFileName);
            if (iconTexture == null)
            {
                DrawOverlayButton(fallbackContent, isActive, onClick, width);
                return;
            }

            var style = GetOverlayButtonStyle(false, width);
            Color previous = GUI.color;
            GUI.color = isActive ? new Color(0.4f, 0.8f, 1f, 1f) : Color.white;

            Rect rect = GUILayoutUtility.GetRect(width, 40f, style, GUILayout.Width(width), GUILayout.Height(40f));
            if (GUI.Button(rect, GUIContent.none, style))
            {
                onClick?.Invoke();
                GUI.FocusControl(null);
            }

            GUI.color = Color.white;
            float padding = Mathf.Clamp(rect.width * 0.22f, 6f, 10f);
            Rect iconRect = new Rect(
                rect.x + padding,
                rect.y + padding,
                rect.width - padding * 2f,
                rect.height - padding * 2f);
            GUI.DrawTexture(iconRect, iconTexture, ScaleMode.ScaleToFit, true);
            GUI.color = previous;
        }

        private void DrawOverlaySelectButton(GUIContent fallbackContent, bool isActive, Action onClick, float width = 40f)
        {
            var style = GetOverlayButtonStyle(false, width);
            Color previous = GUI.color;
            GUI.color = isActive ? new Color(0.4f, 0.8f, 1f, 1f) : Color.white;

            Rect rect = GUILayoutUtility.GetRect(width, 40f, style, GUILayout.Width(width), GUILayout.Height(40f));
            if (GUI.Button(rect, GUIContent.none, style))
            {
                onClick?.Invoke();
                GUI.FocusControl(null);
            }

            DrawSelectGlyph(rect);
            GUI.color = previous;
        }

        private void DrawOverlayPickerButton(GUIContent fallbackContent, bool isActive, Action onClick, float width = 40f)
        {
            var style = GetOverlayButtonStyle(false, width);
            Color previous = GUI.color;
            GUI.color = isActive ? new Color(0.4f, 0.8f, 1f, 1f) : Color.white;

            Rect rect = GUILayoutUtility.GetRect(width, 40f, style, GUILayout.Width(width), GUILayout.Height(40f));
            if (GUI.Button(rect, GUIContent.none, style))
            {
                onClick?.Invoke();
                GUI.FocusControl(null);
            }

            DrawPickerGlyph(rect);
            GUI.color = previous;
        }

        private void DrawSelectGlyph(Rect rect)
        {
            Color iconColor = new Color(0.94f, 0.97f, 1f, 0.98f);
            float dashThickness = Mathf.Max(1f, Mathf.Round(rect.width * 0.06f));
            float dashLength = Mathf.Max(3f, rect.width * 0.12f);

            Rect boxRect = new Rect(
                rect.x + rect.width * 0.18f,
                rect.y + rect.height * 0.16f,
                rect.width * 0.46f,
                rect.height * 0.46f);

            DrawDashedHorizontalLine(new Vector2(boxRect.xMin, boxRect.yMin), boxRect.width, dashLength, dashThickness, iconColor);
            DrawDashedHorizontalLine(new Vector2(boxRect.xMin, boxRect.yMax - dashThickness), boxRect.width, dashLength, dashThickness, iconColor);
            DrawDashedVerticalLine(new Vector2(boxRect.xMin, boxRect.yMin), boxRect.height, dashLength, dashThickness, iconColor);
            DrawDashedVerticalLine(new Vector2(boxRect.xMax - dashThickness, boxRect.yMin), boxRect.height, dashLength, dashThickness, iconColor);

            Vector2 tip = new Vector2(rect.x + rect.width * 0.74f, rect.y + rect.height * 0.70f);
            Vector2 baseTop = new Vector2(rect.x + rect.width * 0.46f, rect.y + rect.height * 0.48f);
            Vector2 baseBottom = new Vector2(rect.x + rect.width * 0.58f, rect.y + rect.height * 0.84f);
            Vector2 notch = new Vector2(rect.x + rect.width * 0.62f, rect.y + rect.height * 0.69f);
            Vector2 tail = new Vector2(rect.x + rect.width * 0.74f, rect.y + rect.height * 0.88f);

            DrawLineSegment(baseTop, tip, dashThickness, iconColor);
            DrawLineSegment(tip, notch, dashThickness, iconColor);
            DrawLineSegment(notch, tail, dashThickness, iconColor);
            DrawLineSegment(tail, baseBottom, dashThickness, iconColor);
            DrawLineSegment(baseBottom, baseTop, dashThickness, iconColor);
        }

        private void DrawPickerGlyph(Rect rect)
        {
            Color iconColor = new Color(0.94f, 0.97f, 1f, 0.98f);
            float thickness = Mathf.Max(2f, rect.width * 0.10f);

            Vector2 tip = new Vector2(rect.x + rect.width * 0.28f, rect.y + rect.height * 0.76f);
            Vector2 shaftStart = new Vector2(rect.x + rect.width * 0.37f, rect.y + rect.height * 0.66f);
            Vector2 shaftEnd = new Vector2(rect.x + rect.width * 0.68f, rect.y + rect.height * 0.35f);

            DrawLineSegment(shaftStart, shaftEnd, thickness, iconColor);
            DrawLineSegment(shaftEnd, new Vector2(rect.x + rect.width * 0.78f, rect.y + rect.height * 0.25f), thickness, iconColor);
            DrawLineSegment(new Vector2(rect.x + rect.width * 0.68f, rect.y + rect.height * 0.25f), new Vector2(rect.x + rect.width * 0.84f, rect.y + rect.height * 0.41f), thickness, iconColor);

            DrawLineSegment(new Vector2(rect.x + rect.width * 0.61f, rect.y + rect.height * 0.18f), new Vector2(rect.x + rect.width * 0.88f, rect.y + rect.height * 0.45f), thickness * 0.9f, iconColor);
            DrawLineSegment(new Vector2(rect.x + rect.width * 0.88f, rect.y + rect.height * 0.28f), new Vector2(rect.x + rect.width * 0.71f, rect.y + rect.height * 0.45f), thickness * 0.9f, iconColor);

            DrawLineSegment(tip, shaftStart, thickness * 0.85f, iconColor);
            DrawLineSegment(tip, new Vector2(rect.x + rect.width * 0.20f, rect.y + rect.height * 0.68f), thickness * 0.85f, iconColor);
            DrawLineSegment(tip, new Vector2(rect.x + rect.width * 0.30f, rect.y + rect.height * 0.86f), thickness * 0.85f, iconColor);
        }

        private void DrawDashedHorizontalLine(Vector2 start, float width, float dashLength, float thickness, Color color)
        {
            for (float offset = 0f; offset < width; offset += dashLength * 1.7f)
            {
                float currentWidth = Mathf.Min(dashLength, width - offset);
                EditorGUI.DrawRect(new Rect(start.x + offset, start.y, currentWidth, thickness), color);
            }
        }

        private void DrawDashedVerticalLine(Vector2 start, float height, float dashLength, float thickness, Color color)
        {
            for (float offset = 0f; offset < height; offset += dashLength * 1.7f)
            {
                float currentHeight = Mathf.Min(dashLength, height - offset);
                EditorGUI.DrawRect(new Rect(start.x, start.y + offset, thickness, currentHeight), color);
            }
        }

        private void DrawLineSegment(Vector2 start, Vector2 end, float thickness, Color color)
        {
            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color;

            Vector2 delta = end - start;
            float length = delta.magnitude;
            if (length <= 0.01f)
                return;

            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, start);
            GUI.DrawTexture(new Rect(start.x, start.y - thickness * 0.5f, length, thickness), EditorGUIUtility.whiteTexture);

            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }

        private void DrawOverlayButton(GUIContent content, bool isActive, Action onClick, float width = 40f)
        {
            var style = GetOverlayButtonStyle(content != null && content.image != null, width);
            Color previous = GUI.color;
            GUI.color = isActive ? new Color(0.4f, 0.8f, 1f, 1f) : Color.white;
            if (GUILayout.Button(content, style))
            {
                onClick?.Invoke();
                GUI.FocusControl(null);
            }
            GUI.color = previous;
        }

        private GUIStyle GetOverlayButtonStyle(bool hasImage, float width)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = hasImage ? 10 : 14,
                alignment = TextAnchor.MiddleCenter,
                fixedHeight = 40f,
                fixedWidth = width,
                margin = new RectOffset(4, 4, 2, 2),
                padding = new RectOffset(4, 4, 4, 4),
                imagePosition = hasImage ? ImagePosition.ImageOnly : ImagePosition.TextOnly,
                wordWrap = false
            };

            return style;
        }

        private GUIStyle GetOverlayLabelStyle()
        {
            if (overlayLabelStyle == null)
            {
                overlayLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 11,
                    normal = { textColor = Color.white }
                };
            }

            return overlayLabelStyle;
        }

        private GUIStyle GetOverlayStatusLabelStyle()
        {
            if (overlayStatusLabelStyle == null)
            {
                overlayStatusLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                    clipping = TextClipping.Clip,
                    normal = { textColor = Color.white }
                };
            }

            return overlayStatusLabelStyle;
        }

        private void DrawOverlayPanelBackground(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.02f, 0.02f, 0.02f, 0.96f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), new Color(1f, 1f, 1f, 0.08f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), new Color(0f, 0f, 0f, 0.35f));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), new Color(1f, 1f, 1f, 0.06f));
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), new Color(0f, 0f, 0f, 0.25f));
        }

        private Rect CalculateActionBarRect(Rect paddedGridRect)
        {
            const float buttonWidth = 40f;
            const float buttonMargin = 8f;
            const float buttonGapBeforeClear = 6f;
            const int buttonCount = 7;
            const float sidePadding = 16f;

            float contentWidth = buttonCount * (buttonWidth + buttonMargin) + buttonGapBeforeClear;
            float width = Mathf.Min(contentWidth + sidePadding, paddedGridRect.width - 20f);
            float height = 56f;
            if (width <= 0f || height <= 0f)
                return Rect.zero;

            float x = paddedGridRect.x + (paddedGridRect.width - width) * 0.5f;
            float y = paddedGridRect.yMax - height - 12f;
            return new Rect(x, y, width, height);
        }

        private Rect CalculateBrushSliderRect(Rect paddedGridRect)
        {
            float width = Mathf.Clamp(paddedGridRect.width * 0.12f, 52f, 78f);
            width = Mathf.Min(width, paddedGridRect.width / 3f);
            float height = Mathf.Clamp(paddedGridRect.height - 20f, 80f, 240f);
            height = Mathf.Min(height, paddedGridRect.height - 8f);
            if (width <= 0f || height <= 0f)
                return Rect.zero;

            float x = paddedGridRect.xMax - width - 12f;
            float y = paddedGridRect.y + (paddedGridRect.height - height) * 0.5f;
            return new Rect(x, y, width, height);
        }

        private Rect ExpandRect(Rect rect, float horizontalPadding, float verticalPadding)
        {
            if (rect.width <= 0f || rect.height <= 0f)
                return Rect.zero;

            return new Rect(
                rect.x - horizontalPadding,
                rect.y - verticalPadding,
                rect.width + horizontalPadding * 2f,
                rect.height + verticalPadding * 2f);
        }

        private void ActivateDrawMode()
        {
            bool wasPickerActive = pickerToolActive;
            bool stateChanged = isSelectionMode || !drawMode || panToolActive || pickerToolActive;
            drawMode = true;
            isSelectionMode = false;
            eraseMode = EraseMode.Select;
            panToolActive = false;
            pickerToolActive = false;
            ResetSelectionVisuals();

            if (wasPickerActive)
            {
                // When leaving the picker, keep the tab/selection that was just picked
                // so Brush paints exactly the picked asset instead of reviving stale
                // selection state from another tab.
                SyncSelectionToActiveTab();
            }
            else
            {
                AlignTabWithCurrentSelection();
            }

            if (stateChanged)
            {
                Repaint();
            }
        }

        private void ActivateEraseSelectMode()
        {
            bool stateChanged = drawMode || isSelectionMode || eraseMode != EraseMode.Select || panToolActive || pickerToolActive;
            drawMode = false;
            isSelectionMode = false;
            eraseMode = EraseMode.Select;
            panToolActive = false;
            pickerToolActive = false;
            ResetSelectionVisuals();
            if (stateChanged)
            {
                Repaint();
            }
        }

        private void ActivateEraseAllMode()
        {
            bool stateChanged = drawMode || isSelectionMode || eraseMode != EraseMode.All || panToolActive || pickerToolActive;
            drawMode = false;
            isSelectionMode = false;
            eraseMode = EraseMode.All;
            panToolActive = false;
            pickerToolActive = false;
            ResetSelectionVisuals();
            if (stateChanged)
            {
                Repaint();
            }
        }

        private void ActivatePickerMode()
        {
            bool stateChanged = !pickerToolActive || drawMode || isSelectionMode || panToolActive;
            pickerToolActive = true;
            drawMode = false;
            isSelectionMode = false;
            eraseMode = EraseMode.Select;
            panToolActive = false;
            ResetSelectionVisuals();
            if (stateChanged)
            {
                Repaint();
            }
        }

        private void ToggleSelectionMode()
        {
            isSelectionMode = !isSelectionMode;
            pickerToolActive = false;
            if (isSelectionMode)
            {
                drawMode = true;
                eraseMode = EraseMode.Select;
                panToolActive = false;
                tilemapEditor.selectedTileRuleIndex = -1;
                tilemapEditor.selectedGameObjectRuleIndex = -1;
                tilemapEditor.selectedTextureRule = null;
                tilemapEditor.selectedPathIndex = -1;
                brushSizeProperty.intValue = 1;
                tilemapEditor.brushSize = 1;
            }
            else
            {
                ResetSelectionVisuals();
            }

            selectedCells.Clear();
            Repaint();
        }

        private void TogglePanToolMode()
        {
            panToolActive = !panToolActive;
            if (panToolActive)
                pickerToolActive = false;
            Repaint();
        }

        private void ResetSelectionVisuals()
        {
            if (selectedCells.Count > 0)
            {
                selectedCells.Clear();
            }

            selectionOffset = Vector3Int.zero;
            isDraggingSelection = false;
        }

        private void AlignTabWithCurrentSelection()
        {
            if (tilemapEditor.selectedPathIndex >= 0)
            {
                SetInspectorTab(3, true);
            }
            else if (tilemapEditor.selectedTextureRule != null)
            {
                SetInspectorTab(1, true);
            }
            else if (tilemapEditor.selectedGameObjectRuleIndex >= 0)
            {
                SetInspectorTab(2, true);
            }
            else if (tilemapEditor.selectedTileRuleIndex >= 0)
            {
                SetInspectorTab(0, true);
            }
        }

        private void ClearAllTilemapContent()
        {
            if (!EditorUtility.DisplayDialog(
                "Clear All",
                "Are you sure you want to clear all tiles, gameobjects, and painted textures? This cannot be undone.",
                "Yes, Clear All", "Cancel"))
            {
                return;
            }

            if (tilemapEditor.targetTilemap != null)
            {
                Undo.RegisterCompleteObjectUndo(tilemapEditor.targetTilemap, "Clear All Tiles");
                tilemapEditor.targetTilemap.ClearAllTiles();
            }

            if (tilemapEditor.heightTilemaps != null)
            {
                foreach (var map in tilemapEditor.heightTilemaps.Values)
                {
                    if (map == null)
                        continue;

                    Undo.RegisterCompleteObjectUndo(map, "Clear All Tiles");
                    map.ClearAllTiles();
                }
            }

            if (tilemapEditor.tileRules != null)
            {
                foreach (var rule in tilemapEditor.tileRules)
                {
                    if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                    {
                        Undo.RegisterCompleteObjectUndo(rule.customTargetTilemap, "Clear All Tiles");
                        rule.customTargetTilemap.ClearAllTiles();
                    }
                }
            }

            tilemapEditor.EraseAllGameObjects();
            tilemapEditor.texturePaintMask?.Clear();

            SyncVegetationAfterTexturePaintStroke();

            if (tilemapEditor.paintMaskTexture != null)
            {
                var old = RenderTexture.active;
                RenderTexture.active = tilemapEditor.paintMaskTexture;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = old;
            }

            tilemapEditor.UpdateBlendPreviewMaterial();
            tilemapEditor.SyncAllProceduralRenderers();
            tilemapEditor.RefreshAllSkirts();
            tilemapEditor.RebuildPaintMaskAndMaterials();
            tilemapEditor.RefreshAllPathFollowers();
            tilemapEditor.RebuildAllTrackMeshes();
            tilemapEditor.needsRefreshPreview = true;
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
            Repaint();
        }
    }
}
