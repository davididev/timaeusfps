// QuickTileRuntimePainter.cs
// Runtime façade over the existing QuickTilemapEditor paint methods.
// Delegates to PaintTile / PaintTextureCell / PlaceGameObjectDot so no
// painting logic is duplicated. Wraps each stroke in the procedural sync
// batch already exposed by the core (BeginProceduralSyncBatch /
// EndProceduralSyncBatch) so drags don't rebuild procedural meshes per cell.

using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    [DisallowMultipleComponent]
    public class QuickTileRuntimePainter : MonoBehaviour
    {
        public enum PaintMode
        {
            Tile,
            EraseTile,
            Texture,
            EraseTexture,
            Object,
            EraseObject
        }

        public enum BrushShape { Square, Circle }

        [Header("Target")]
        public QuickTilemapEditor editor;

        [Header("Active Rule Indices")]
        public int activeTileRuleIndex;
        public int activeTextureRuleIndex;
        public int activeGameObjectRuleIndex;
        public Color objectDotColor = Color.white;

        [Header("Brush")]
        public PaintMode mode = PaintMode.Tile;
        public BrushShape shape = BrushShape.Square;
        [Min(1)] public int brushSize = 1;
        public bool eraseAllHeights;

        bool strokeActive;
        bool objectsDirtyThisStroke;
        readonly HashSet<Vector3Int> strokeCells = new HashSet<Vector3Int>();
        Vector3Int lastStrokeCell;
        bool hasLastCell;

        public bool IsReady => editor != null;
        public bool StrokeActive => strokeActive;

        void Reset()
        {
            if (editor == null) editor = GetComponent<QuickTilemapEditor>();
        }

        void OnDisable()
        {
            if (strokeActive) EndStroke();
        }

        public void BeginStroke()
        {
            if (!IsReady || strokeActive) return;
            editor.BeginProceduralSyncBatch();
            strokeActive = true;
            objectsDirtyThisStroke = false;
            strokeCells.Clear();
            hasLastCell = false;
        }

        public void EndStroke()
        {
            if (!IsReady || !strokeActive) return;
            editor.EndProceduralSyncBatch();
            if (objectsDirtyThisStroke)
                editor.ResyncInstancesSafe();
            strokeActive = false;
            objectsDirtyThisStroke = false;
            strokeCells.Clear();
            hasLastCell = false;
        }

        public void PaintAt(Vector3Int cell)
        {
            if (!IsReady) return;
            bool openedImplicitly = false;
            if (!strokeActive) { BeginStroke(); openedImplicitly = true; }

            if (hasLastCell && lastStrokeCell != cell)
            {
                foreach (var c in BresenhamLine(lastStrokeCell, cell))
                    ApplyBrush(c);
            }
            else
            {
                ApplyBrush(cell);
            }
            lastStrokeCell = cell;
            hasLastCell = true;

            if (openedImplicitly) EndStroke();
        }

        public void ApplyBrush(Vector3Int centerCell)
        {
            if (!IsReady) return;
            int r = Mathf.Max(1, brushSize) - 1;
            int rr = r * r;
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (shape == BrushShape.Circle && dx * dx + dy * dy > rr) continue;
                    var c = new Vector3Int(centerCell.x + dx, centerCell.y + dy, centerCell.z);
                    if (strokeActive && !strokeCells.Add(c)) continue;
                    ApplyModeAt(c);
                }
            }
        }

        void ApplyModeAt(Vector3Int cell)
        {
            switch (mode)
            {
                case PaintMode.Tile:
                {
                    var rule = GetTileRule(activeTileRuleIndex);
                    if (rule != null) editor.PaintTile(cell, rule);
                    break;
                }
                case PaintMode.EraseTile:
                    if (eraseAllHeights)
                    {
                        editor.EraseTileAtAllHeights(cell);
                    }
                    else
                    {
                        editor.selectedTileRuleIndex = activeTileRuleIndex;
                        editor.EraseTileAtSelectedLayer(cell);
                    }
                    break;
                case PaintMode.Texture:
                    if (HasTextureRule(activeTextureRuleIndex))
                        editor.PaintTextureCell(cell, activeTextureRuleIndex);
                    break;
                case PaintMode.EraseTexture:
                    editor.EraseTextureCell(cell);
                    break;
                case PaintMode.Object:
                    if (HasGameObjectRule(activeGameObjectRuleIndex))
                    {
                        editor.PlaceGameObjectDot(cell, activeGameObjectRuleIndex, objectDotColor);
                        objectsDirtyThisStroke = true;
                    }
                    break;
                case PaintMode.EraseObject:
                    editor.EraseGameObjectDot(cell);
                    break;
            }
        }

        QuickTilemapEditor.TileRule GetTileRule(int index)
        {
            if (editor?.tileRules == null) return null;
            if (index < 0 || index >= editor.tileRules.Count) return null;
            return editor.tileRules[index];
        }

        bool HasTextureRule(int index)
        {
            return editor?.texturePaintRules != null
                && index >= 0
                && index < editor.texturePaintRules.Count;
        }

        bool HasGameObjectRule(int index)
        {
            return editor?.gameObjectRules != null
                && index >= 0
                && index < editor.gameObjectRules.Count;
        }

        static IEnumerable<Vector3Int> BresenhamLine(Vector3Int a, Vector3Int b)
        {
            int x0 = a.x, y0 = a.y, x1 = b.x, y1 = b.y;
            int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            while (true)
            {
                yield return new Vector3Int(x0, y0, a.z);
                if (x0 == x1 && y0 == y1) yield break;
                int e2 = err << 1;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 <  dx) { err += dx; y0 += sy; }
            }
        }
    }
}
