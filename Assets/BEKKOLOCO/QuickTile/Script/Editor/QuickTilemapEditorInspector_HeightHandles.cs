// QuickTilemapEditorInspector_HeightHandles.cs
// Draws draggable Y-axis handles on each TileRule's tilemap in the Scene View.
// The handle sits at the center of the tilemap's occupied bounds and allows
// the user to drag up/down to change yOffset in real time.

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System.Collections.Generic;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        // ─── Settings ───
        private const float kHandleDiscSize = 0.35f;
        private const float kHandleLabelOffsetY = 0.4f;
        private const float kHeightSnap = 0.1f;

        // ─── Icon (loaded once) ───
        private static Texture2D _heightIcon;
        private static Texture2D HeightIcon
        {
            get
            {
                if (_heightIcon == null)
                {
                    // Try loading a custom icon; fall back to built-in
                    _heightIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/QuickTile/Script/Editor/ico_drag.png");
                    if (_heightIcon == null)
                        _heightIcon = EditorGUIUtility.IconContent("d_MoveTool").image as Texture2D;
                }
                return _heightIcon;
            }
        }

        /// <summary>
        /// Call this from OnSceneGUI to draw height handles for all tile rules.
        /// </summary>
        public void DrawHeightHandles()
        {
            if (tilemapEditor == null) return;
            if (tilemapEditor.tileRules == null || tilemapEditor.tileRules.Count == 0) return;

            for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
            {
                var rule = tilemapEditor.tileRules[i];
                if (rule == null || !rule.isVisible) continue;

                DrawSingleHeightHandle(rule, i);
            }
        }

        private void DrawSingleHeightHandle(QuickTilemapEditor.TileRule rule, int ruleIndex)
        {
            // Find the tilemap for this rule
            Tilemap tm = GetTilemapForRule(rule);
            if (tm == null && tilemapEditor.targetTilemap == null) return;

            // Compute the center of the tilemap's occupied area in world space
            Vector3 handlePos = ComputeHandlePosition(rule, tm);

            // ── Draw the vertical line (ghost) ──
            float lineBottom = handlePos.y - 2f;
            float lineTop = handlePos.y + 2f;
            Handles.color = new Color(1f, 0.6f, 0f, 0.3f);
            Handles.DrawDottedLine(
                new Vector3(handlePos.x, lineBottom, handlePos.z),
                new Vector3(handlePos.x, lineTop, handlePos.z),
                4f
            );

            // ── Draw the handle icon/disc ──
            float handleSize = HandleUtility.GetHandleSize(handlePos) * kHandleDiscSize;

            // Color by rule index for visual distinction
            Color handleColor = GetRuleColor(rule, ruleIndex);
            Handles.color = handleColor;

            // Icon label above the disc
            GUIStyle labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = handleColor },
                fontSize = 11
            };

            string label = !string.IsNullOrEmpty(rule.ruleName)
                ? rule.ruleName
                : $"Rule {ruleIndex}";

            Vector3 labelPos = handlePos + Vector3.up * (handleSize + kHandleLabelOffsetY);
            Handles.Label(labelPos, $"↕ {label}\nY: {rule.yOffset:F2}", labelStyle);

            // ── Draggable Y-axis slider ──
            EditorGUI.BeginChangeCheck();

            // Use a vertical slider handle constrained to Y axis
            Vector3 newPos = Handles.Slider(
                handlePos,
                Vector3.up,
                handleSize * 1.5f,
                Handles.ArrowHandleCap,
                kHeightSnap
            );

            // Also draw a disc at the base for visual clarity + easy click target
            if (Handles.Button(handlePos, Quaternion.LookRotation(Vector3.up), handleSize, handleSize * 1.2f, Handles.CircleHandleCap))
            {
                // Select this rule in the inspector on click
                tilemapEditor.selectedTileRuleIndex = ruleIndex;
                EditorUtility.SetDirty(tilemapEditor);
                Repaint();
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(tilemapEditor, "Change Tile Rule Height");

                float newY = Mathf.Round(newPos.y / kHeightSnap) * kHeightSnap;
                rule.yOffset = newY;

                // Update the actual tilemap position if it exists
                if (tm != null)
                {
                    tm.transform.localPosition = new Vector3(
                        tm.transform.localPosition.x,
                        newY,
                        tm.transform.localPosition.z
                    );
                }

                EditorUtility.SetDirty(tilemapEditor);
                SceneView.RepaintAll();
                RefreshProceduralMeshesForLayerChange(rule, rule.isDigLayer);

                // Refresh skirts if needed
#if UNITY_EDITOR
                tilemapEditor.skirtsNeedRefresh = true;
#endif
            }
        }

        // ─── Helpers ───

        private Tilemap GetTilemapForRule(QuickTilemapEditor.TileRule rule)
        {
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                return rule.customTargetTilemap;

            if (Mathf.Abs(rule.yOffset) > 0.001f &&
                tilemapEditor.heightTilemaps.TryGetValue(rule.yOffset, out Tilemap heightTm))
                return heightTm;

            return tilemapEditor.targetTilemap;
        }

        private Vector3 ComputeHandlePosition(QuickTilemapEditor.TileRule rule, Tilemap tm)
        {
            Vector3 center = Vector3.zero;

            if (tm != null)
            {
                tm.CompressBounds();
                BoundsInt bounds = tm.cellBounds;

                if (bounds.size.x > 0 && bounds.size.y > 0)
                {
                    // Center of occupied cells in world space
                    Vector3 min = tm.CellToWorld(bounds.min);
                    Vector3 max = tm.CellToWorld(bounds.max);
                    center = (min + max) * 0.5f;
                }
                else
                {
                    center = tm.transform.position;
                }
            }
            else if (tilemapEditor.targetTilemap != null)
            {
                center = tilemapEditor.targetTilemap.transform.position;
            }

            // Force Y to the rule's yOffset
            center.y = rule.yOffset;

            return center;
        }

        private Color GetRuleColor(QuickTilemapEditor.TileRule rule, int index)
        {
            // Use the rule's color if it's not white/default, otherwise generate from index
            if (rule.color != Color.white)
                return rule.color;

            // Generate a distinct color based on index
            float hue = (index * 0.618034f) % 1f; // Golden ratio for good distribution
            return Color.HSVToRGB(hue, 0.8f, 0.9f);
        }
    }
}
#endif
