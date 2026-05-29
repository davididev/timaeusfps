using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using System.Linq;
using Bekkoloco.DOTS;
using UnityEditorInternal;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        private void DrawTilemapGrid()
        {
            if (tilemapEditor == null || tilemapEditor.targetTilemap == null)
                return;

            // ─── 1) Remove any destroyed Tilemaps ─────
            tilemapEditor.heightTilemaps = tilemapEditor.heightTilemaps
                .Where(kvp => kvp.Value != null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            int gridWidth = tilemapEditor.gridSize.x;
            int gridHeight = tilemapEditor.gridSize.y;
            float availableWidth = EditorGUIUtility.currentViewWidth - 70f;
            float gridHeightPixels = availableWidth * gridHeight / gridWidth;

            Rect gridRect = GUILayoutUtility.GetRect(availableWidth, gridHeightPixels);
            float canvasBorderWidth = 1f;
            Color canvasBackgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);
            Color canvasBorderColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            EditorGUI.DrawRect(gridRect, canvasBackgroundColor);
            EditorGUI.DrawRect(new Rect(gridRect.x, gridRect.y, gridRect.width, canvasBorderWidth), canvasBorderColor);
            EditorGUI.DrawRect(new Rect(gridRect.x, gridRect.yMax - canvasBorderWidth, gridRect.width, canvasBorderWidth), canvasBorderColor);
            EditorGUI.DrawRect(new Rect(gridRect.x, gridRect.y, canvasBorderWidth, gridRect.height), canvasBorderColor);
            EditorGUI.DrawRect(new Rect(gridRect.xMax - canvasBorderWidth, gridRect.y, canvasBorderWidth, gridRect.height), canvasBorderColor);

            Rect paddedGridRect = new Rect(
                gridRect.x + canvasBorderWidth,
                gridRect.y + canvasBorderWidth,
                gridRect.width - 2 * canvasBorderWidth,
                gridRect.height - 2 * canvasBorderWidth
            );
            float cellWidth = paddedGridRect.width / gridWidth;
            float cellHeight = paddedGridRect.height / gridHeight;
            Event evt = Event.current;
            Vector2 mousePos = evt.mousePosition;

            Rect actionOverlayRect = CalculateActionBarRect(paddedGridRect);
            Rect actionStatusRect = CalculateActionStatusRect(actionOverlayRect, GetGridOverlayStatusText());
            bool isPointerOverOverlay =
                (actionStatusRect.width > 0f && actionStatusRect.height > 0f && actionStatusRect.Contains(mousePos)) ||
                (actionOverlayRect.width > 0f && actionOverlayRect.height > 0f && actionOverlayRect.Contains(mousePos));
            bool tilesTabActive = IsTilesTabActive();
            bool textureTabActive = IsTextureTabActive();
            bool gameObjectsTabActive = IsGameObjectsTabActive();
            bool pathTabActive = IsPathTabActive();

            // Vérifiez si l'utilisateur est en train de déplacer la grille
            // bool isPanning = evt.button == 1 || evt.alt || evt.command || evt.control;
            // Vérifiez si l'utilisateur veut panner la grille (seulement hors sélection)
            bool panHotkeyActive = (evt.button == 1 || evt.alt);
            bool panToolEngaged = panToolActive && evt.isMouse && evt.button == 0;
            bool isPanning = (panToolActive && panToolEngaged) || (!isSelectionMode && panHotkeyActive);

            // Ajouter le curseur approprié en fonction du mode
            if (panToolActive || isPanning)
            {
                EditorGUIUtility.AddCursorRect(paddedGridRect, MouseCursor.Pan);
            }
            else if (drawMode || pickerToolActive)
            {
                EditorGUIUtility.AddCursorRect(paddedGridRect, MouseCursor.Arrow);
            }
            else
            {
                EditorGUIUtility.AddCursorRect(paddedGridRect, MouseCursor.ArrowMinus);
            }



            if (pickerToolActive && paddedGridRect.Contains(mousePos) && !isPointerOverOverlay &&
                evt.type == EventType.MouseDown && evt.button == 0 && !evt.alt)
            {
                int x = Mathf.FloorToInt((mousePos.x - paddedGridRect.x) / cellWidth);
                int y = gridHeight - 1 - Mathf.FloorToInt((mousePos.y - paddedGridRect.y) / cellHeight);
                Vector3Int cellPos = new Vector3Int(
                    x - gridWidth / 2 + (int)gridViewOffset.x,
                    y - gridHeight / 2 + (int)gridViewOffset.y,
                    0
                );

                PickTileAt(cellPos);
                evt.Use();
            }

            if (tilesTabActive && paddedGridRect.Contains(mousePos) && !isPointerOverOverlay && evt.type == EventType.MouseDown && evt.button == 0 && evt.shift)
            {
                // Calculer la position de la cellule cliquée
                int x = Mathf.FloorToInt((mousePos.x - paddedGridRect.x) / cellWidth);
                int y = gridHeight - 1 - Mathf.FloorToInt((mousePos.y - paddedGridRect.y) / cellHeight);
                Vector3Int cellPos = new Vector3Int(
                    x - gridWidth / 2 + (int)gridViewOffset.x,
                    y - gridHeight / 2 + (int)gridViewOffset.y,
                    0
                );

                // Rechercher les tiles à cette position et leurs tilemaps correspondantes
                List<(TileBase tile, QuickTilemapEditor.TileRule rule, int ruleIndex)> tilesAtPosition = new List<(TileBase, QuickTilemapEditor.TileRule, int)>();

                // Vérifier le tile de base
                TileBase baseTile = tilemapEditor.targetTilemap.GetTile(cellPos);
                if (baseTile != null)
                {
                    // Créer une règle fictive pour la tilemap de base
                    var baseRule = new QuickTilemapEditor.TileRule
                    {
                        tile = baseTile,
                        useCustomTilemap = false,
                        customTargetTilemap = tilemapEditor.targetTilemap,
                        yOffset = 0f,
                        color = tilemapEditor.targetTilemap.GetColor(cellPos)
                    };
                    tilesAtPosition.Add((baseTile, baseRule, -1)); // -1 pour la tilemap de base
                }
                // Vérifier toutes les règles de tiles personnalisées
                for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
                {
                    var rule = tilemapEditor.tileRules[i];
                    if (!rule.isVisible) continue; // Ignorer les tilemaps non visibles

                    Tilemap targetMap = rule.useCustomTilemap && rule.customTargetTilemap != null
                        ? rule.customTargetTilemap
                        : (Mathf.Abs(rule.yOffset) > 0.001f && tilemapEditor.heightTilemaps.ContainsKey(rule.yOffset)
                           ? tilemapEditor.heightTilemaps[rule.yOffset] : null);

                    if (targetMap != null)
                    {
                        TileBase tile = targetMap.GetTile(cellPos);
                        if (tile != null)
                        {
                            tilesAtPosition.Add((tile, rule, i));
                        }
                    }
                }

                // Si on a trouvé des tiles à cette position
                if (tilesAtPosition.Count > 0)
                {
                    // Trier par Y offset (du plus haut au plus bas) pour sélectionner le tile le plus visible
                    tilesAtPosition.Sort((a, b) => b.rule.yOffset.CompareTo(a.rule.yOffset));

                    var selectedTile = tilesAtPosition[0];
                    int ruleIndex = selectedTile.ruleIndex;

                    if (ruleIndex >= 0) // Règle personnalisée
                    {
                        // Sélectionner cette règle
                        tilemapEditor.selectedTileRuleIndex = ruleIndex;
                        tilemapEditor.selectedGameObjectRuleIndex = -1;
                        tilemapEditor.selectedPathIndex = -1;
                        tilemapEditor.selectedTextureRule = null;

                        // Passer à l'onglet Tiles et mode dessin
                        SetInspectorTab(0, true);
                        drawMode = true;

                        // Mettre à jour le tile actif
                        tilemapEditor.activeTile = selectedTile.tile;

                        // Message de confirmation
                        string tileName = selectedTile.tile.name;
                        string tilemapName = selectedTile.rule.customTargetTilemap != null
                            ? selectedTile.rule.customTargetTilemap.name
                            : "Base Tilemap";

                        Debug.Log($"✅ Sélectionné: Tile '{tileName}' de la tilemap '{tilemapName}' (Y offset: {selectedTile.rule.yOffset})");

                        // Afficher un feedback visuel temporaire
                        ShowTemporarySelectionFeedback(cellPos, selectedTile.rule.color);
                    }
                    else // Tilemap de base
                    {
                        // Créer une nouvelle règle basée sur le tile de base si nécessaire
                        bool hasMatchingRule = false;
                        for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
                        {
                            var rule = tilemapEditor.tileRules[i];
                            if (rule.tile == selectedTile.tile && Mathf.Abs(rule.yOffset) < 0.001f)
                            {
                                tilemapEditor.selectedTileRuleIndex = i;
                                hasMatchingRule = true;
                                break;
                            }
                        }

                        if (!hasMatchingRule)
                        {
                            // Créer une nouvelle règle pour ce tile
                            var newRule = new QuickTilemapEditor.TileRule
                            {
                                tile = selectedTile.tile,
                                useCustomTilemap = false,
                                yOffset = 0f,
                                color = selectedTile.rule.color,
                                renderOrder = 0,
                                isVisible = true
                            };

                            tilemapEditor.tileRules.Add(newRule);
                            tilemapEditor.selectedTileRuleIndex = tilemapEditor.tileRules.Count - 1;
                        }

                        // Désélectionner les autres outils
                        tilemapEditor.selectedGameObjectRuleIndex = -1;
                        tilemapEditor.selectedPathIndex = -1;
                        tilemapEditor.selectedTextureRule = null;

                        // Passer à l'onglet Tiles et mode dessin
                        SetInspectorTab(0, true);
                        drawMode = true;
                        tilemapEditor.activeTile = selectedTile.tile;

                        Debug.Log($"✅ Sélectionné: Tile '{selectedTile.tile.name}' de la tilemap de base");
                    }

                    // Forcer la mise à jour de l'interface
                    serializedObject.Update();
                    serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(tilemapEditor);
                    Repaint();
                }
                else
                {
                    Debug.Log("❌ Aucun tile trouvé à cette position");
                }

                evt.Use(); // Consommer l'événement pour éviter d'autres traitements
                return; // Sortir tôt pour éviter les autres handlers d'événements
            }

            // Gestion du déplacement //  // Gestion du déplacement (panning) uniquement hors sélection
            //if (paddedGridRect.Contains(mousePos) && isPanning && evt.type == EventType.MouseDrag)
            if (paddedGridRect.Contains(mousePos) && !isPointerOverOverlay && isPanning && evt.type == EventType.MouseDrag)
            {
                gridViewOffset.x -= evt.delta.x / cellWidth;
                gridViewOffset.y += evt.delta.y / cellHeight;
                evt.Use();
                Repaint();
            }

            Dictionary<Vector3Int, Texture2D> paintedTextures = new Dictionary<Vector3Int, Texture2D>();
            if (tilemapEditor.texturePaintMask != null)
            {
                foreach (var kvp in tilemapEditor.texturePaintMask)
                {
                    int idx = kvp.Value;
                    if (idx >= 0 && idx < tilemapEditor.texturePaintRules.Count)
                    {
                        // ✅ albedo to show
                        paintedTextures[kvp.Key] = tilemapEditor.texturePaintRules[idx].albedo;
                    }
                }
            }



            Dictionary<Vector2Int, List<(TileBase tile, Color color, float yOffset, int sortOrder, int layerKey)>> tileLayersMap =
                new Dictionary<Vector2Int, List<(TileBase, Color, float, int, int)>>();


            for (int x = 0; x < gridWidth; x++)
            {
                for (int y = 0; y < gridHeight; y++)
                {
                    Vector3Int cellPos = new Vector3Int(
                        x - gridWidth / 2 + (int)gridViewOffset.x,
                        gridHeight - 1 - y - gridHeight / 2 + (int)gridViewOffset.y,
                        0
                    );
                    Vector2Int key = new Vector2Int(x, y);
                    tileLayersMap[key] = GetVisibleTileLayersForCell_V2(cellPos);
                }
            }

            // Second pass: Draw grid backgrounds and borders.
            for (int x = 0; x < gridWidth; x++)
            {
                for (int y = 0; y < gridHeight; y++)
                {
                    Rect cellRect = new Rect(
                        paddedGridRect.x + x * cellWidth,
                        paddedGridRect.y + y * cellHeight,
                        cellWidth,
                        cellHeight
                    );

                    EditorGUI.DrawRect(cellRect, new Color(0.08f, 0.08f, 0.08f, 1f));
                    // Draw horizontal and vertical lines for the grid.
                    float lineWidth = gridWidth > 32 ? 0.5f : 1f;
                    Color gridLineColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                    EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y, cellRect.width, lineWidth), gridLineColor);
                    EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y, lineWidth, cellRect.height), gridLineColor);
                }
            }


            // Third pass: draw tiles and collect every rendered surface group for outlines.
            Dictionary<string, List<Rect>> visibleSurfaceRects = new Dictionary<string, List<Rect>>();
            for (int x = 0; x < gridWidth; x++)
            {
                for (int y = 0; y < gridHeight; y++)
                {
                    Vector2Int key = new Vector2Int(x, y);
                    Rect cellRect = new Rect(
                        paddedGridRect.x + x * cellWidth,
                        paddedGridRect.y + y * cellHeight,
                        cellWidth,
                        cellHeight
                    );

                    // Calculate the cellPos here so we can use it for the skip check
                    Vector3Int cellPos = new Vector3Int(
                        x - gridWidth / 2 + (int)gridViewOffset.x,
                        gridHeight - 1 - y - gridHeight / 2 + (int)gridViewOffset.y,
                        0
                    );

                    // Skip rendering this cell if it's part of the selected cells being dragged
                    // AND there's an actual offset (so tiles don't disappear until you actually move them)
                    bool skipCell = isSelectionMode && isDraggingSelection &&
                                    selectedCells.Contains(cellPos) &&
                                    selectionOffset != Vector3Int.zero;

                    if (!skipCell && tileLayersMap.TryGetValue(key, out var tileList))
                    {
                        tileList.Sort((a, b) =>
                        {
                            int yCompare = a.yOffset.CompareTo(b.yOffset);
                            return yCompare != 0 ? yCompare : a.sortOrder.CompareTo(b.sortOrder);
                        });

                        foreach (var tileLayer in tileList)
                        {
                            TileBase tile = tileLayer.tile;
                            Color color = tileLayer.color;
                            if (tile != null)
                            {
                                Color32 layerColor = tileLayer.color;
                                string surfaceGroupKey = $"{tileLayer.layerKey}:{tileLayer.sortOrder}:{tileLayer.yOffset}:{layerColor.r}:{layerColor.g}:{layerColor.b}:{layerColor.a}";

                                if (!visibleSurfaceRects.TryGetValue(surfaceGroupKey, out var surfaceRects))
                                {
                                    surfaceRects = new List<Rect>();
                                    visibleSurfaceRects[surfaceGroupKey] = surfaceRects;
                                }

                                surfaceRects.Add(cellRect);

                                if (tile is UnityEngine.Tilemaps.Tile t && t.sprite != null)
                                {
                                    // Grab the sprite's texture and UVs
                                    var sprite = t.sprite;
                                    var texture = sprite.texture;
                                    var rect = sprite.textureRect;
                                    var uv = new Rect(
                                        rect.x / texture.width,
                                        rect.y / texture.height,
                                        rect.width / texture.width,
                                        rect.height / texture.height
                                    );

                                    // Draw the actual tile texture into the cell
                                    GUI.color = color;
                                    GUI.DrawTextureWithTexCoords(cellRect, texture, uv);
                                    GUI.color = Color.white;
                                }
                                else
                                {
                                    // fallback if it's not a Tile or has no sprite
                                    EditorGUI.DrawRect(cellRect, color);
                                }

                            }
                        }
                    }

                    // ✅ Draw painted texture on top, if any - also skip if cell is being dragged
                    if (!skipCell && paintedTextures.TryGetValue(cellPos, out Texture2D tex) && tex != null)
                    {
                        Vector2? textureOverlayCenter = GetInspectorTextureOverlayPoint(
                            cellPos,
                            paddedGridRect,
                            gridWidth,
                            gridHeight,
                            cellWidth,
                            cellHeight);

                        if (textureOverlayCenter.HasValue)
                            DrawInspectorTextureCellOverlay(tex, textureOverlayCenter.Value, cellWidth, cellHeight);
                    }
                }
            }

            foreach (var kvp in visibleSurfaceRects)
            {
                var surfaceRects = kvp.Value;
                if (surfaceRects == null || surfaceRects.Count == 0)
                    continue;

                CollectRectUnionContourSegments_V1(surfaceRects, out var horizontalSegments, out var verticalSegments);
                DrawSurfaceOutlines_V1(horizontalSegments, verticalSegments);
            }

            //  If a path is selected, intercept left-click events to add a point to the path
            if (paddedGridRect.Contains(mousePos) && !isPointerOverOverlay && (evt.type == EventType.MouseDown || evt.type == EventType.MouseDrag) && evt.button == 0)
            {
                // Check if we're working with paths
                if (pathTabActive && tilemapEditor.selectedPathIndex != -1)
                {
                    // Safety checks
                    if (tilemapEditor.selectedPathIndex >= tilemapEditor.paths.Count || tilemapEditor.paths[tilemapEditor.selectedPathIndex] == null)
                    {
                        //Debug.Log(Error("Invalid path index or path is null");
                        tilemapEditor.selectedPathIndex = -1;
                        return;
                    }

                    var activePath = tilemapEditor.paths[tilemapEditor.selectedPathIndex];
                    if (activePath.points == null)
                    {
                        activePath.points = new List<Vector3Int>();
                    }

                    // Calculate cell position
                    int x = Mathf.FloorToInt((mousePos.x - paddedGridRect.x) / cellWidth);
                    int y = gridHeight - 1 - Mathf.FloorToInt((mousePos.y - paddedGridRect.y) / cellHeight);
                    Vector3Int cellPos = new Vector3Int(
                        x - gridWidth / 2 + (int)gridViewOffset.x,
                        y - gridHeight / 2 + (int)gridViewOffset.y,
                        0);

                    if (drawMode)
                    {
                        // In draw mode, add a point if it doesn't exist
                        if (!activePath.points.Contains(cellPos))
                        {
                            activePath.points.Add(cellPos);

                            // Check all placed objects at this position
                            foreach (var placed in tilemapEditor.placedObjects)
                            {
                                if (placed.position == cellPos)
                                {
                                    // Update the pathIndex for this placed object
                                    placed.pathIndex = tilemapEditor.selectedPathIndex + 1;

                                    try
                                    {
                                        // Find and update corresponding GameObjects
                                        foreach (GameObject go in tilemapEditor.instantiatedGameObjects)
                                        {
                                            if (go == null) continue;

                                            Tilemap parentMap = go.transform.parent?.GetComponent<Tilemap>();
                                            if (parentMap == null) continue;

                                            Vector3Int goCell = tilemapEditor.SafeWorldToCell(go.transform.position, parentMap);
                                            if (goCell == cellPos)
                                            {
                                                // Get or add PathFollower component
                                                PathFollower pf = go.GetComponent<PathFollower>();
                                                if (pf == null)
                                                {
                                                    pf = go.AddComponent<PathFollower>();
                                                }

                                                pf.SetPathIndex(tilemapEditor.selectedPathIndex);

                                                // Create world path coordinates
                                                List<Vector2> worldPath = new List<Vector2>();
                                                foreach (var p in activePath.points)
                                                {
                                                    Vector3 worldPos = tilemapEditor.GetPathWorldPos(activePath, p);
                                                    worldPath.Add(new Vector2(worldPos.x, worldPos.z));
                                                }

                                                pf.SetPath(worldPath);
                                                //Debug.Log(($"Path updated on {go.name} at {cellPos} - Path has {worldPath.Count} points");

                                                if (Application.isPlaying)
                                                {
                                                    pf.StartMoving();
                                                }

                                                EditorUtility.SetDirty(go);
                                            }
                                        }
                                    }
                                    catch (System.Exception)
                                    {
                                        //Debug.Log(Error($"Error updating path follower: {ex.Message}");
                                    }
                                }
                            }

                            EditorUtility.SetDirty(tilemapEditor);
                        }
                    }
                    else
                    {
                        // In erase mode, remove points
                        int brushSize = tilemapEditor.brushSize;
                        int brushOffset = brushSize / 2;

                        // Calculate base grid cell under the mouse
                        int baseX = Mathf.FloorToInt((mousePos.x - paddedGridRect.x) / cellWidth) - (gridWidth / 2) + (int)gridViewOffset.x;
                        int baseY = (gridHeight - 1) - Mathf.FloorToInt((mousePos.y - paddedGridRect.y) / cellHeight) - (gridHeight / 2) + (int)gridViewOffset.y;

                        // Build a list of all grid positions covered by the brush
                        List<Vector3Int> brushCells = new List<Vector3Int>();
                        for (int bx = 0; bx < brushSize; bx++)
                        {
                            for (int by = 0; by < brushSize; by++)
                            {
                                brushCells.Add(new Vector3Int(baseX + bx, baseY + by, 0));
                            }
                        }

                        // Remove any point in the active path that is within the brush area
                        if (activePath.points.RemoveAll(point => brushCells.Contains(point)) > 0)
                        {
                            EditorUtility.SetDirty(tilemapEditor);

                            // Update any PathFollowers that were using this path
                            foreach (var go in tilemapEditor.instantiatedGameObjects)
                            {
                                try
                                {
                                    if (go == null) continue;

                                    PathFollower pf = go.GetComponent<PathFollower>();
                                    if (pf != null && pf.GetPathIndex() == tilemapEditor.selectedPathIndex)
                                    {
                                        // Create world path coordinates
                                        List<Vector2> worldPath = new List<Vector2>();
                                        foreach (var p in activePath.points)
                                        {
                                            Vector3 worldPos = tilemapEditor.GetPathWorldPos(activePath, p);
                                            worldPath.Add(new Vector2(worldPos.x, worldPos.z));
                                        }

                                        pf.SetPath(worldPath);
                                        //Debug.Log(($"Updated path on {go.name} after point removal - Path now has {worldPath.Count} points");

                                        EditorUtility.SetDirty(go);
                                    }
                                }
                                catch (System.Exception)
                                {
                                    //Debug.Log(Error($"Error updating path follower after removal: {ex.Message}");
                                }
                            }
                        }
                    }

                    evt.Use();
                    SceneView.RepaintAll();
                    Repaint();
                    return; // Prevent further processing for this event
                }


            }

            if (tilemapEditor.editorEnabled && paddedGridRect.Contains(mousePos) && !isPointerOverOverlay)
            {
                int x = Mathf.FloorToInt((mousePos.x - paddedGridRect.x) / cellWidth);
                int y = gridHeight - 1 - Mathf.FloorToInt((mousePos.y - paddedGridRect.y) / cellHeight);
                Vector3Int cellPos = new Vector3Int(
                    x - gridWidth / 2 + (int)gridViewOffset.x,
                    y - gridHeight / 2 + (int)gridViewOffset.y,
                    0
                );

                if (evt.type == EventType.MouseDown && evt.control)
                {
                    if (pathTabActive && tilemapEditor.selectedPathIndex >= 0)
                    {
                        HandlePathAssignment(cellPos);
                        evt.Use();
                        return;
                    }
                }

                ////////////////////////

                // Rendu des cellules sélectionnées
                if (isSelectionMode && selectedCells.Count > 0)
                {
                    // First collect what's actually in each selected cell
                    Dictionary<Vector3Int, List<(TileBase tile, Color color, float yOffset)>> selectedTileContent = new Dictionary<Vector3Int, List<(TileBase, Color, float)>>();
                    Dictionary<Vector3Int, Texture2D> selectedTextureContent = new Dictionary<Vector3Int, Texture2D>();

                    // Gather all tile and texture content for selected cells
                    foreach (var selectedPos in selectedCells)
                    {
                        // Collect tile content
                        List<(TileBase, Color, float)> tileList = new List<(TileBase, Color, float)>();

                        // Add the base tile
                        TileBase baseTile = tilemapEditor.targetTilemap.GetTile(selectedPos);
                        if (baseTile != null)
                            tileList.Add((baseTile, tilemapEditor.targetTilemap.GetColor(selectedPos), 0f));

                        // Add tiles from custom tilemaps
                        foreach (var rule in tilemapEditor.tileRules)
                        {
                            Tilemap t = rule.useCustomTilemap && rule.customTargetTilemap != null
                                        ? rule.customTargetTilemap
                                        : (Mathf.Abs(rule.yOffset) > 0.001f && tilemapEditor.heightTilemaps.ContainsKey(rule.yOffset)
                                           ? tilemapEditor.heightTilemaps[rule.yOffset] : null);
                            if (t != null)
                            {
                                TileBase customTile = t.GetTile(selectedPos);
                                if (customTile != null)
                                    tileList.Add((customTile, rule.color, rule.yOffset));
                            }
                        }

                        if (tileList.Count > 0)
                            selectedTileContent[selectedPos] = tileList;

                        // Collect texture content
                        if (tilemapEditor.texturePaintMask != null &&
                            tilemapEditor.texturePaintMask.TryGetValue(selectedPos, out int textureIndex) &&
                            textureIndex >= 0 && textureIndex < tilemapEditor.texturePaintRules.Count)
                        {
                            Texture2D tex = tilemapEditor.texturePaintRules[textureIndex].albedo;
                            if (tex != null)
                                selectedTextureContent[selectedPos] = tex;
                        }
                    }

                    // RENDERING LOGIC

                    // Only show the original selection if we're NOT dragging
                    if (!isDraggingSelection)
                    {
                        foreach (var selectedPos in selectedCells)
                        {
                            int selectedX = selectedPos.x + gridWidth / 2 - (int)gridViewOffset.x;
                            int selectedY = gridHeight - 1 - (selectedPos.y + gridHeight / 2 - (int)gridViewOffset.y);

                            if (selectedX >= 0 && selectedX < gridWidth && selectedY >= 0 && selectedY < gridHeight)
                            {
                                Rect selectedRect = new Rect(
                                    paddedGridRect.x + selectedX * cellWidth,
                                    paddedGridRect.y + selectedY * cellHeight,
                                    cellWidth,
                                    cellHeight
                                );

                                // Dessiner un cadre de sélection autour de la cellule
                                float borderWidth = 2f;
                                Color selectionColor = new Color(0.2f, 0.6f, 1f, 0.8f);
                                EditorGUI.DrawRect(new Rect(selectedRect.x, selectedRect.y, selectedRect.width, borderWidth), selectionColor);
                                EditorGUI.DrawRect(new Rect(selectedRect.x, selectedRect.y, borderWidth, selectedRect.height), selectionColor);
                                EditorGUI.DrawRect(new Rect(selectedRect.x + selectedRect.width - borderWidth, selectedRect.y, borderWidth, selectedRect.height), selectionColor);
                                EditorGUI.DrawRect(new Rect(selectedRect.x, selectedRect.y + selectedRect.height - borderWidth, selectedRect.width, borderWidth), selectionColor);

                                // Remplissage semi-transparent
                                EditorGUI.DrawRect(selectedRect, new Color(0.2f, 0.6f, 1f, 0.3f));
                            }
                        }
                    }

                    // If dragging, show ONLY the destination preview with content
                    if (isDraggingSelection && selectionOffset != Vector3Int.zero)
                    {
                        // Draw tiles at preview position first (so textures appear on top)
                        foreach (var kvp in selectedTileContent)
                        {
                            Vector3Int originalPos = kvp.Key;
                            Vector3Int previewPos = originalPos + selectionOffset;

                            // Convert to screen coordinates
                            int previewX = previewPos.x + gridWidth / 2 - (int)gridViewOffset.x;
                            int previewY = gridHeight - 1 - (previewPos.y + gridHeight / 2 - (int)gridViewOffset.y);

                            // Only render if it's within view
                            if (previewX >= 0 && previewX < gridWidth && previewY >= 0 && previewY < gridHeight)
                            {
                                Rect previewRect = new Rect(
                                    paddedGridRect.x + previewX * cellWidth,
                                    paddedGridRect.y + previewY * cellHeight,
                                    cellWidth,
                                    cellHeight
                                );

                                // Draw the actual tile content from the original position
                                List<(TileBase tile, Color color, float yOffset)> tileList = kvp.Value;
                                tileList.Sort((a, b) => a.yOffset.CompareTo(b.yOffset));

                                // Draw all the tiles with normal colors (not semitransparent)
                                foreach (var (tile, color, _) in tileList)
                                {
                                    if (tile != null)
                                    {
                                        if (tile is UnityEngine.Tilemaps.Tile t && t.sprite != null)
                                        {
                                            // Get the sprite texture and UVs
                                            var sprite = t.sprite;
                                            var texture = sprite.texture;
                                            var rect = sprite.textureRect;
                                            var uv = new Rect(
                                                rect.x / texture.width,
                                                rect.y / texture.height,
                                                rect.width / texture.width,
                                                rect.height / texture.height
                                            );

                                            // Draw with full color
                                            GUI.color = color;
                                            GUI.DrawTextureWithTexCoords(previewRect, texture, uv);
                                            GUI.color = Color.white;
                                        }
                                        else
                                        {
                                            // Fallback if it's not a Tile or has no sprite
                                            EditorGUI.DrawRect(previewRect, color);
                                        }
                                    }
                                }
                            }
                        }


                        //// Preview
                        ///


                        // Draw texture overlays at preview position
                        foreach (var kvp in selectedTextureContent)
                        {
                            Vector3Int originalPos = kvp.Key;
                            Vector3Int previewPos = originalPos + selectionOffset;

                            // Convert to screen coordinates
                            int previewX = previewPos.x + gridWidth / 2 - (int)gridViewOffset.x;
                            int previewY = gridHeight - 1 - (previewPos.y + gridHeight / 2 - (int)gridViewOffset.y);

                            // Only render if it's within view
                            if (previewX >= 0 && previewX < gridWidth && previewY >= 0 && previewY < gridHeight)
                            {
                                Rect previewRect = new Rect(
                                    paddedGridRect.x + previewX * cellWidth,
                                    paddedGridRect.y + previewY * cellHeight,
                                    cellWidth,
                                    cellHeight
                                );

                                // Get the texture from the original position
                                Texture2D tex = kvp.Value;

                                if (tex != null)
                                {
                                    Vector2? textureOverlayCenter = GetInspectorTextureOverlayPoint(
                                        previewPos,
                                        paddedGridRect,
                                        gridWidth,
                                        gridHeight,
                                        cellWidth,
                                        cellHeight);

                                    if (textureOverlayCenter.HasValue)
                                        DrawInspectorTextureCellOverlay(tex, textureOverlayCenter.Value, cellWidth, cellHeight);
                                }
                            }
                        }



                        // Draw preview border (orange dashed lines) on top of everything
                        foreach (var selectedPos in selectedCells)
                        {
                            Vector3Int previewPos = selectedPos + selectionOffset;

                            // Convert to screen coordinates
                            int previewX = previewPos.x + gridWidth / 2 - (int)gridViewOffset.x;
                            int previewY = gridHeight - 1 - (previewPos.y + gridHeight / 2 - (int)gridViewOffset.y);

                            // Only render if it's within view
                            if (previewX >= 0 && previewX < gridWidth && previewY >= 0 && previewY < gridHeight)
                            {
                                Rect previewRect = new Rect(
                                    paddedGridRect.x + previewX * cellWidth,
                                    paddedGridRect.y + previewY * cellHeight,
                                    cellWidth,
                                    cellHeight
                                );

                                // Draw a dashed border effect (alternating dashes)
                                float dashSize = 4f;
                                float borderWidth = 2f;
                                Color previewBorderColor = new Color(0.9f, 0.5f, 0.1f, 0.8f);  // Bright orange border

                                // Top border (dashed)
                                for (float dashX = 0; dashX < previewRect.width; dashX += dashSize * 2)
                                {
                                    float dashWidth = Mathf.Min(dashSize, previewRect.width - dashX);
                                    EditorGUI.DrawRect(
                                        new Rect(previewRect.x + dashX, previewRect.y, dashWidth, borderWidth),
                                        previewBorderColor
                                    );
                                }

                                // Bottom border (dashed)
                                for (float dashX = 0; dashX < previewRect.width; dashX += dashSize * 2)
                                {
                                    float dashWidth = Mathf.Min(dashSize, previewRect.width - dashX);
                                    EditorGUI.DrawRect(
                                        new Rect(previewRect.x + dashX, previewRect.y + previewRect.height - borderWidth, dashWidth, borderWidth),
                                        previewBorderColor
                                    );
                                }

                                // Left border (dashed)
                                for (float dashY = 0; dashY < previewRect.height; dashY += dashSize * 2)
                                {
                                    float dashHeight = Mathf.Min(dashSize, previewRect.height - dashY);
                                    EditorGUI.DrawRect(
                                        new Rect(previewRect.x, previewRect.y + dashY, borderWidth, dashHeight),
                                        previewBorderColor
                                    );
                                }

                                // Right border (dashed)
                                for (float dashY = 0; dashY < previewRect.height; dashY += dashSize * 2)
                                {
                                    float dashHeight = Mathf.Min(dashSize, previewRect.height - dashY);
                                    EditorGUI.DrawRect(
                                        new Rect(previewRect.x + previewRect.width - borderWidth, previewRect.y + dashY, borderWidth, dashHeight),
                                        previewBorderColor
                                    );
                                }
                            }
                        }
                    }
                }


                // Mode sélection
                if (isSelectionMode)
                {
                    // Existing hover highlight code...
                    Rect hoverCellRect = new Rect(
                        paddedGridRect.x + x * cellWidth,
                        paddedGridRect.y + y * cellHeight,
                        cellWidth, cellHeight
                    );
                    EditorGUI.DrawRect(hoverCellRect, new Color(1f, 1f, 1f, 0.2f));

                    // Mouse down - selection start logic (keep existing code)
                    if (evt.type == EventType.MouseDown && evt.button == 0)
                    {
                        bool clickedInsideExistingSelection = selectedCells.Count > 0 && selectedCells.Contains(cellPos);

                        if (evt.shift) // Add to selection
                        {
                            if (!selectedCells.Contains(cellPos))
                                selectedCells.Add(cellPos);
                        }
                        else if (evt.control) // Remove from selection
                        {
                            selectedCells.Remove(cellPos);
                        }
                        else if (clickedInsideExistingSelection) // Re-click inside selection => move whole block
                        {
                            isDraggingSelection = true;
                            dragStartMousePos = evt.mousePosition;
                            selectionOffset = Vector3Int.zero;
                        }
                        else // New selection
                        {
                            selectedCells.Clear();
                            selectedCells.Add(cellPos);
                            dragStartCell = cellPos;
                            dragStartMousePos = evt.mousePosition;
                        }
                        evt.Use();
                        Repaint();
                    }

                    // Box selection logic (keep existing code)
                    if (evt.type == EventType.MouseDrag && evt.button == 0 && !evt.shift && !evt.control && !isDraggingSelection)
                    {
                        // Calculate selection rectangle
                        Vector3Int currentCell = cellPos;
                        int minX = Mathf.Min(dragStartCell.x, currentCell.x);
                        int maxX = Mathf.Max(dragStartCell.x, currentCell.x);
                        int minY = Mathf.Min(dragStartCell.y, currentCell.y);
                        int maxY = Mathf.Max(dragStartCell.y, currentCell.y);

                        // Create selection based on rectangle
                        selectedCells.Clear();
                        for (int cx = minX; cx <= maxX; cx++)
                        {
                            for (int cy = minY; cy <= maxY; cy++)
                            {
                                selectedCells.Add(new Vector3Int(cx, cy, 0));
                            }
                        }

                        evt.Use();
                        Repaint();
                    }

                    // OPTIMIZED: Start movement with either right mouse button or alt+left
                    if ((evt.type == EventType.MouseDown && evt.button == 1) ||
                        (evt.type == EventType.MouseDown && evt.button == 0 && evt.alt))
                    {
                        bool clickedInsideExistingSelection = selectedCells.Count > 0 && selectedCells.Contains(cellPos);
                        if (clickedInsideExistingSelection)
                        {
                            isDraggingSelection = true;
                            dragStartMousePos = evt.mousePosition;
                            selectionOffset = Vector3Int.zero;
                            evt.Use();
                        }
                    }

                    if (evt.type == EventType.MouseDrag && isDraggingSelection &&
                        (evt.button == 0 || evt.button == 1))
                    {
                        // Calculate cell offset with better precision
                        Vector2 delta = evt.mousePosition - dragStartMousePos;
                        int offsetX = Mathf.RoundToInt(delta.x / cellWidth);
                        int offsetY = -Mathf.RoundToInt(delta.y / cellHeight); // Invert Y

                        Vector3Int newOffset = new Vector3Int(offsetX, offsetY, 0);

                        // Always update the offset and force a repaint to show the preview
                        // even if the offset hasn't changed (this ensures smooth movement)
                        selectionOffset = newOffset;
                        Repaint();

                        evt.Use();
                    }

                    // OPTIMIZED: Complete the movement when mouse is released
                    if (evt.type == EventType.MouseUp && isDraggingSelection &&
                        (evt.button == 0 || evt.button == 1))
                    {
                        isDraggingSelection = false;

                        if (selectionOffset != Vector3Int.zero)
                        {
                            // OPTIMIZATION 1: Batch operations - collect data first
                            Dictionary<Vector3Int, int> texturesToMove = new Dictionary<Vector3Int, int>();
                            List<Vector3Int> cellsToErase = new List<Vector3Int>(selectedCells);
                            allTilesToMove.Clear(); // Clear previous data

                            // OPTIMIZATION 2: Use HashSet for faster lookups
                            HashSet<Vector3Int> selectedCellsSet = new HashSet<Vector3Int>(selectedCells);
                            List<Vector3Int> newSelectedCells = new List<Vector3Int>();

                            // Batch collecting data in one pass - improved to collect ALL layers
                            foreach (var pos in selectedCells)
                            {
                                Vector3Int newPos = pos + selectionOffset;
                                newSelectedCells.Add(newPos);

                                // Check for textures first (single lookup)
                                if (tilemapEditor.texturePaintMask.TryGetValue(pos, out int textureIndex))
                                {
                                    texturesToMove[newPos] = textureIndex;
                                }

                                // Initialize the inner dictionary for this position
                                var tilesAtPosition = new Dictionary<int, TileData>();

                                // Check for tiles from EACH rule, not just the first one found
                                for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
                                {
                                    var rule = tilemapEditor.tileRules[i];
                                    Tilemap targetMap = rule.useCustomTilemap && rule.customTargetTilemap != null
                                        ? rule.customTargetTilemap
                                        : tilemapEditor.targetTilemap;

                                    TileBase tile = targetMap.GetTile(pos);
                                    if (tile != null)
                                    {
                                        // Store this tile with its associated rule index
                                        tilesAtPosition[i] = new TileData
                                        {
                                            tile = tile,
                                            color = targetMap.GetColor(pos),
                                            targetMap = targetMap
                                        };
                                    }
                                }

                                // Also check the base tilemap as a fallback (rule index = -1)
                                if (tilemapEditor.targetTilemap != null)
                                {
                                    TileBase tile = tilemapEditor.targetTilemap.GetTile(pos);
                                    if (tile != null)
                                    {
                                        tilesAtPosition[-1] = new TileData
                                        {
                                            tile = tile,
                                            color = tilemapEditor.targetTilemap.GetColor(pos),
                                            targetMap = tilemapEditor.targetTilemap
                                        };
                                    }
                                }

                                // Only add to the collection if we found any tiles at this position
                                if (tilesAtPosition.Count > 0)
                                {
                                    allTilesToMove[newPos] = tilesAtPosition;
                                }
                            }

                            // OPTIMIZATION 3: Register Undo for affected tilemaps
                            Undo.RegisterCompleteObjectUndo(tilemapEditor, "Move Selection");

                            // First register the base tilemap
                            if (tilemapEditor.targetTilemap != null)
                            {
                                Undo.RegisterCompleteObjectUndo(tilemapEditor.targetTilemap, "Move Selection");
                            }

                            // Register all rule tilemaps
                            foreach (var rule in tilemapEditor.tileRules)
                            {
                                if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                                {
                                    Undo.RegisterCompleteObjectUndo(rule.customTargetTilemap, "Move Selection");
                                }
                            }

                            // CRITICAL FIX: Completely erase all tiles at original positions first
                            // This ensures we don't get duplicated tiles
                            foreach (var originalPos in cellsToErase)
                            {
                                // 1. Remove textures
                                tilemapEditor.texturePaintMask.Remove(originalPos);

                                // 2. Remove tiles from base tilemap
                                if (tilemapEditor.targetTilemap != null)
                                {
                                    tilemapEditor.targetTilemap.SetTile(originalPos, null);
                                }

                                // 3. Remove tiles from each rule tilemap
                                foreach (var rule in tilemapEditor.tileRules)
                                {
                                    if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                                    {
                                        rule.customTargetTilemap.SetTile(originalPos, null);
                                    }
                                    else if (tilemapEditor.heightTilemaps.TryGetValue(rule.yOffset, out Tilemap heightTilemap))
                                    {
                                        heightTilemap.SetTile(originalPos, null);
                                    }
                                }
                            }

                            // Place textures at new positions
                            foreach (var kvp in texturesToMove)
                            {
                                if (kvp.Value >= 0 && kvp.Value < tilemapEditor.texturePaintRules.Count)
                                {
                                    tilemapEditor.PaintTextureCell(kvp.Key, kvp.Value);
                                }
                            }

                            SyncVegetationAfterTexturePaintStroke();

                            // Place tiles at new positions
                            foreach (var posKvp in allTilesToMove)
                            {
                                Vector3Int newPos = posKvp.Key;
                                var tilesAtPosition = posKvp.Value;

                                // Place each tile from each rule that was found
                                foreach (var tileKvp in tilesAtPosition)
                                {
                                    int ruleIndex = tileKvp.Key;
                                    TileData tileData = tileKvp.Value;

                                    if (tileData.tile != null)
                                    {
                                        if (ruleIndex >= 0 && ruleIndex < tilemapEditor.tileRules.Count)
                                        {
                                            // Use a specific tile rule
                                            var rule = tilemapEditor.tileRules[ruleIndex];

                                            // Get the correct tilemap from the rule
                                            Tilemap targetMap = rule.useCustomTilemap && rule.customTargetTilemap != null
                                                ? rule.customTargetTilemap
                                                : tilemapEditor.targetTilemap;

                                            // Place the tile
                                            targetMap.SetTile(newPos, tileData.tile);
                                            targetMap.SetColor(newPos, tileData.color);
                                        }
                                        else if (ruleIndex == -1 && tilemapEditor.targetTilemap != null)
                                        {
                                            // Use the base tilemap (for rule index -1)
                                            tilemapEditor.targetTilemap.SetTile(newPos, tileData.tile);
                                            tilemapEditor.targetTilemap.SetColor(newPos, tileData.color);
                                        }
                                    }
                                }
                            }

                            // Update the paint mask texture
                            tilemapEditor.UpdatePaintMaskTexture();
                            tilemapEditor.UpdateBlendPreviewMaterial();

                            // Update the selection
                            selectedCells = newSelectedCells;

                            // Create a mapping of original positions to new positions
                            Dictionary<Vector3Int, Vector3Int> positionMapping = new Dictionary<Vector3Int, Vector3Int>();
                            foreach (var originalPos in cellsToErase)
                            {
                                positionMapping[originalPos] = originalPos + selectionOffset;
                            }

                            // Move GameObjects
                            List<QuickTilemapEditor.PlacedObject> updatedPlacedObjects = new List<QuickTilemapEditor.PlacedObject>();
                            // Keep track of which GameObjects we've already processed to avoid duplicates
                            HashSet<GameObject> processedGameObjects = new HashSet<GameObject>();

                            // First find and update all GameObjects that need to be moved
                            foreach (var originalPos in cellsToErase)
                            {
                                Vector3Int newPos = originalPos + selectionOffset;

                                // Find objects at the original position to move them
                                for (int i = 0; i < tilemapEditor.placedObjects.Count; i++)
                                {
                                    var placedObj = tilemapEditor.placedObjects[i];

                                    // Check if this object is at the position we're moving from
                                    if (placedObj.position == originalPos)
                                    {
                                        // Create a copy with the updated position
                                        var movedObj = placedObj;
                                        movedObj.position = newPos;
                                        if (movedObj.placementSurface == QuickTilemapEditor.GameObjectPlacementSurface.Skirt)
                                            movedObj.skirtAnchorCell += selectionOffset;

                                        // Add to the updated list
                                        updatedPlacedObjects.Add(movedObj);

                                        QuickTilemapEditor.GameObjectRule movedRule =
                                            movedObj.ruleIndex >= 0 && movedObj.ruleIndex < tilemapEditor.gameObjectRules.Count
                                                ? tilemapEditor.gameObjectRules[movedObj.ruleIndex]
                                                : null;

                                        // Now find and move the actual GameObject instance
                                        foreach (var go in tilemapEditor.instantiatedGameObjects)
                                        {
                                            if (go == null || processedGameObjects.Contains(go)) continue;

                                            Tilemap parentMap = go.transform.parent?.GetComponent<Tilemap>();
                                            var marker = go.GetComponent<QuickTileMarker>();
                                            bool matchesPlacedObject = marker != null
                                                ? marker.PlacedObjectId == movedObj.UniqueId
                                                : (parentMap != null && tilemapEditor.SafeWorldToCell(go.transform.position, parentMap) == originalPos);

                                            // If this GameObject is at the original position
                                            if (matchesPlacedObject)
                                            {
                                                // Record for undo
                                                Undo.RecordObject(go.transform, "Move GameObject");

                                                if (!tilemapEditor.TryResolvePlacedObjectWorldPosition(movedObj, movedRule, out Tilemap destinationParentMap, out Vector3 worldPos))
                                                    continue;

                                                if (destinationParentMap != parentMap)
                                                {
                                                    Undo.SetTransformParent(go.transform, destinationParentMap.transform, "Move GameObject");
                                                }

                                                // Update position
                                                go.transform.position = worldPos;
                                                movedObj.parentTilemapName = destinationParentMap.name;

                                                if (marker != null)
                                                {
                                                    marker.Initialize(
                                                        movedObj.UniqueId,
                                                        tilemapEditor.GetInstanceID().ToString(),
                                                        movedObj.ruleIndex,
                                                        movedObj.position);
                                                }

                                                // Mark as processed
                                                processedGameObjects.Add(go);

                                                //Debug.Log(($"Moving GameObject from {originalPos} to {newPos}, worldPos: {worldPos}");
                                                break;
                                            }
                                        }
                                    }
                                }
                            }

                            // Now add all objects that were NOT in the selection
                            foreach (var placedObj in tilemapEditor.placedObjects)
                            {
                                // Only add objects not at any of the original positions
                                if (!cellsToErase.Contains(placedObj.position))
                                {
                                    updatedPlacedObjects.Add(placedObj);
                                }
                            }

                            // Replace the old placed objects list with the updated one
                            tilemapEditor.placedObjects = updatedPlacedObjects;

                            // Update paths
                            for (int pathIndex = 0; pathIndex < tilemapEditor.paths.Count; pathIndex++)
                            {
                                var path = tilemapEditor.paths[pathIndex];
                                List<Vector3Int> updatedPoints = new List<Vector3Int>();
                                bool pathModified = false;

                                // Update path points
                                foreach (var point in path.points)
                                {
                                    if (selectedCellsSet.Contains(point))
                                    {
                                        updatedPoints.Add(point + selectionOffset);
                                        pathModified = true;
                                    }
                                    else
                                    {
                                        updatedPoints.Add(point);
                                    }
                                }

                                // If path was modified, update it and connected PathFollowers
                                if (pathModified)
                                {
                                    path.points = updatedPoints;

                                    // Update PathFollower components
                                    foreach (var go in tilemapEditor.instantiatedGameObjects)
                                    {
                                        if (go == null) continue;

                                        PathFollower pf = go.GetComponent<PathFollower>();
                                        if (pf != null && pf.GetPathIndex() == pathIndex)
                                        {
                                            List<Vector2> worldPath = updatedPoints
                                                .Select(p => {
                                                    Vector3 worldPos = tilemapEditor.GetPathWorldPos(path, p);
                                                    return new Vector2(worldPos.x, worldPos.z);
                                                })
                                                .ToList();

                                            pf.SetPath(worldPath);
                                        }
                                    }
                                }
                            }

                            // Selection moves bypass PaintTile / EraseTile helpers, so we need to
                            // refresh generated visuals explicitly once the batch is committed.
                            tilemapEditor.SyncAllProceduralRenderers();
                            tilemapEditor.RefreshAllSkirts();
                            tilemapEditor.RebuildPaintMaskAndMaterials();
                            tilemapEditor.needsRefreshPreview = true;
                            tilemapEditor.RefreshAllPathFollowers();
                            tilemapEditor.RebuildAllTrackMeshes();

                            EditorUtility.SetDirty(tilemapEditor);
                            EditorSceneManager.MarkSceneDirty(tilemapEditor.gameObject.scene);
                            SceneView.RepaintAll();
                        }

                        evt.Use();
                        Repaint();
                    }

                }
                else if (drawMode)
                {
                    // Votre code existant pour le mode dessin...

                }
                /////////////////////////

                if (drawMode && gameObjectsTabActive && tilemapEditor.selectedGameObjectRuleIndex >= 0 &&
                tilemapEditor.selectedGameObjectRuleIndex < tilemapEditor.gameObjectRules.Count)
                {
                    var rule = tilemapEditor.gameObjectRules[tilemapEditor.selectedGameObjectRuleIndex];
                    if (rule.prefab != null &&
                        tilemapEditor.TryResolveNewGameObjectPlacement(rule, cellPos, out _, out Vector3 worldPos, out _, out _))
                    {
                        // Check if there's already a placed object at this cell.
                        QuickTilemapEditor.PlacedObject existingPlaced =
                            tilemapEditor.placedObjects.Find(p => p.position == cellPos);
                        // Use its rotation if it exists, otherwise default to 0°.
                        float previewRotation = existingPlaced != null ? existingPlaced.rotation : 0f;
                        Quaternion previewQuat = Quaternion.Euler(0, previewRotation, 0);
                        tilemapEditor.ShowPreviewObject(rule.prefab, worldPos, previewQuat, rule.color);
                    }
                    else
                    {
                        tilemapEditor.ClearPreviewObject();
                    }
                }
                else
                {
                    tilemapEditor.ClearPreviewObject();
                }



                int brushSize = tilemapEditor.brushSize;
                int brushOffset = brushSize / 2;
                var hoveredPath = pathTabActive ? GetSelectedInspectorPath() : null;
                bool previewPathAtDualGridIntersection = ShouldDrawPathAtDualGridIntersection(hoveredPath);

                int effectiveBrushSize = (isSelectionMode || pickerToolActive) ? 1 : brushSize;
                int effectiveBrushOffset = effectiveBrushSize / 2;

                for (int bx = 0; bx < effectiveBrushSize; bx++)
                {
                    for (int by = 0; by < effectiveBrushSize; by++)
                    {
                        int gridX = x - effectiveBrushOffset + bx;
                        int gridY = y - effectiveBrushOffset + by;
                        if (gridX < 0 || gridX >= gridWidth || gridY < 0 || gridY >= gridHeight)
                            continue;

                        Rect highlightCellRect = new Rect(
                            paddedGridRect.x + gridX * cellWidth,
                            paddedGridRect.y + (gridHeight - 1 - gridY) * cellHeight,
                            cellWidth, cellHeight
                        );

                        Texture2D preview = null;


                        if (drawMode && preview != null)
                        {
                            GUI.color = new Color(1f, 1f, 1f, 0.8f);
                            GUI.DrawTexture(highlightCellRect, preview, ScaleMode.ScaleToFit);
                            GUI.color = Color.white;
                        }
                        else
                        {
                            Color highlightColor;
                            Color borderColor;

                            if (isSelectionMode)
                            {
                                // Cyan for selection mode
                                highlightColor = new Color(0.2f, 0.8f, 0.9f, 0.5f);
                                borderColor = new Color(0f, 0.6f, 0.8f, 0.8f);
                            }
                            else
                            {
                                // Original colors for draw/erase
                                highlightColor = drawMode ? new Color(0.2f, 0.8f, 0.2f, 0.5f) : new Color(0.8f, 0.2f, 0.2f, 0.5f);
                                borderColor = drawMode ? new Color(0f, 0.6f, 0f, 0.8f) : new Color(0.6f, 0f, 0f, 0.8f);
                            }

                            if (pickerToolActive)
                            {
                                DrawPickerHoverPreview_V1(highlightCellRect);
                            }
                            else if (textureTabActive && !isSelectionMode)
                            {
                                Vector3Int previewPoint = new Vector3Int(
                                    gridX - gridWidth / 2 + (int)gridViewOffset.x,
                                    gridY - gridHeight / 2 + (int)gridViewOffset.y,
                                    0);

                                Vector2? overlayPoint = GetInspectorTextureOverlayPoint(
                                    previewPoint,
                                    paddedGridRect,
                                    gridWidth,
                                    gridHeight,
                                    cellWidth,
                                    cellHeight);

                                if (overlayPoint.HasValue)
                                {
                                    Texture2D texturePreview = tilemapEditor.selectedTextureRule != null
                                        ? tilemapEditor.selectedTextureRule.albedo
                                        : null;

                                    DrawInspectorTexturePreviewMarker(
                                        texturePreview,
                                        overlayPoint.Value,
                                        cellWidth,
                                        cellHeight,
                                        highlightColor,
                                        borderColor);
                                }
                            }
                            else if (previewPathAtDualGridIntersection && !isSelectionMode)
                            {
                                Vector3Int previewPoint = new Vector3Int(
                                    gridX - gridWidth / 2 + (int)gridViewOffset.x,
                                    gridY - gridHeight / 2 + (int)gridViewOffset.y,
                                    0);

                                Vector2? overlayPoint = GetInspectorPathOverlayPoint(
                                    hoveredPath,
                                    previewPoint,
                                    paddedGridRect,
                                    gridWidth,
                                    gridHeight,
                                    cellWidth,
                                    cellHeight);

                                if (overlayPoint.HasValue)
                                {
                                    DrawInspectorPathPreviewMarker(overlayPoint.Value, cellWidth, cellHeight, highlightColor, borderColor);
                                }
                            }
                            else
                            {
                                EditorGUI.DrawRect(highlightCellRect, highlightColor);
                                float borderWidth = brushSize > 5 ? 1f : 2f;
                                EditorGUI.DrawRect(new Rect(highlightCellRect.x, highlightCellRect.y, highlightCellRect.width, borderWidth), borderColor);
                                EditorGUI.DrawRect(new Rect(highlightCellRect.x, highlightCellRect.y, borderWidth, highlightCellRect.height), borderColor);
                                EditorGUI.DrawRect(new Rect(highlightCellRect.x + highlightCellRect.width - borderWidth, highlightCellRect.y, borderWidth, highlightCellRect.height), borderColor);
                                EditorGUI.DrawRect(new Rect(highlightCellRect.x, highlightCellRect.y + highlightCellRect.height - borderWidth, highlightCellRect.width, borderWidth), borderColor);
                            }
                        }
                    }

                }

                if (evt.type == EventType.MouseDown || evt.type == EventType.MouseDrag)
                {

                    if (evt.button == 0 && !isPanning && !evt.shift)
                    {
                        tilemapEditor.BeginProceduralSyncBatch();
                        try
                        {
                            bool canPaintTextures = textureTabActive && tilemapEditor.selectedTextureRule != null;
                            bool canEditGameObjects = gameObjectsTabActive &&
                                tilemapEditor.selectedGameObjectRuleIndex >= 0 &&
                                tilemapEditor.selectedGameObjectRuleIndex < tilemapEditor.gameObjectRules.Count;
                            bool canPaintTiles = tilesTabActive;

                            for (int bx = 0; bx < tilemapEditor.brushSize; bx++)
                            {
                                for (int by = 0; by < tilemapEditor.brushSize; by++)
                                {
                                    Vector3Int brushCellPos = new Vector3Int(
                                        cellPos.x - brushOffset + bx,
                                        cellPos.y - brushOffset + by,
                                        cellPos.z);

                                    if (drawMode && canPaintTextures)
                                    {
                                        bool tileExists = !tilemapEditor.paintOnlyOnTiles;

                                        if (tilemapEditor.paintOnlyOnTiles)
                                        {
                                            if (tilemapEditor.targetTilemap.GetTile(brushCellPos) != null)
                                                tileExists = true;
                                            else
                                            {
                                                foreach (var tm in tilemapEditor.GetAllCustomTilemaps())
                                                {
                                                    if (tm.GetTile(brushCellPos) != null)
                                                    {
                                                        tileExists = true;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        if (tileExists)
                                        {
                                            tilemapEditor.PaintTextureCell(brushCellPos, tilemapEditor.selectedTextureRule);
                                        }
                                    }



                                    if (drawMode)
                                    {
                                        if (canEditGameObjects)
                                        {
                                            var goRule = tilemapEditor.gameObjectRules[tilemapEditor.selectedGameObjectRuleIndex];
                                            if (goRule.prefab == null)
                                                return; // Safety check

                                            // Check if an object already exists at brushCellPos.
                                            QuickTilemapEditor.PlacedObject existingPlaced = tilemapEditor.placedObjects.Find(p =>
                                                p.position.x == brushCellPos.x &&
                                                p.position.y == brushCellPos.y
                                            );
                                            if (existingPlaced != null)
                                            {
                                                string placedObjectId = existingPlaced.UniqueId;

                                                // Iterate through instantiated objects
                                                foreach (GameObject go in tilemapEditor.instantiatedGameObjects)
                                                {
                                                    if (go == null)
                                                        continue;

                                                    var marker = go.GetComponent<QuickTileMarker>();
                                                    if (marker != null && marker.PlacedObjectId == placedObjectId)
                                                    {
                                                        float currentY = go.transform.eulerAngles.y;
                                                        float newY = (currentY + 45f) % 360f;
                                                        go.transform.eulerAngles = new Vector3(go.transform.eulerAngles.x, newY, go.transform.eulerAngles.z);
                                                        existingPlaced.rotation = newY;
                                                        EditorUtility.SetDirty(go);
                                                        EditorUtility.SetDirty(tilemapEditor);
                                                        return;
                                                    }

                                                    // Get the parent tilemap of this object.
                                                    Tilemap parentTile = go.transform.parent != null ? go.transform.parent.GetComponent<Tilemap>() : null;
                                                    {
                                                        // Create a temporary position that "removes" the offset by setting y to the parent’s base y.
                                                        Vector3 posNoOffset = go.transform.position;
                                                        if (parentTile != null)
                                                            posNoOffset.y = parentTile.transform.position.y;
                                                        else
                                                            posNoOffset.y = 0f;
                                                        Vector3Int cell = tilemapEditor.SafeWorldToCell(posNoOffset, parentTile);
                                                        // Compare only the X and Y (ignoring Y offset)
                                                        if (cell.x == brushCellPos.x && cell.y == brushCellPos.y)
                                                        {
                                                            // Rotate this GameObject by 45°.
                                                            float currentY = go.transform.eulerAngles.y;
                                                            float newY = (currentY + 45f) % 360f;
                                                            go.transform.eulerAngles = new Vector3(go.transform.eulerAngles.x, newY, go.transform.eulerAngles.z);
                                                            // Update the rotation stored in the placed object.
                                                            existingPlaced.rotation = newY;
                                                            EditorUtility.SetDirty(go);
                                                            EditorUtility.SetDirty(tilemapEditor);
                                                            return;
                                                        }
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                if (!tilemapEditor.TryResolveNewGameObjectPlacement(goRule, brushCellPos, out Tilemap parentTilemap, out Vector3 tileWorldPos, out Vector3Int skirtAnchorCell, out string failureMessage))
                                                {
                                                    if (!string.IsNullOrEmpty(failureMessage))
                                                    {
                                                        EditorUtility.DisplayDialog(
                                                            goRule.placementSurface == QuickTilemapEditor.GameObjectPlacementSurface.Skirt ? "Place On Skirt Alert" : "Place on Ground Alert",
                                                            failureMessage,
                                                            "OK");
                                                    }
                                                    return;
                                                }

                                                GameObject placedGO = (GameObject)PrefabUtility.InstantiatePrefab(goRule.prefab);
                                                placedGO.transform.position = tileWorldPos;

                                                // detach from any parent before setting rotation
                                                placedGO.transform.SetParent(null, true);

                                                // Apply fixed rotation (90° around Y)
                                                placedGO.transform.rotation = Quaternion.Euler(0, 90f, 0);

                                                // Register undo
                                                Undo.RegisterCreatedObjectUndo(placedGO, "Place GameObject");

                                                // Reattach while preserving world position
                                                placedGO.transform.SetParent(parentTilemap.transform, true);

                                                // Add to the instantiated objects list (if you already do this)
                                                tilemapEditor.instantiatedGameObjects.Add(placedGO);

                                                // Use QuickTilemapEditor.InstanceOffset to refer to the nested type.
                                                QuickTilemapEditor.InstanceOffset newOffset = new QuickTilemapEditor.InstanceOffset
                                                {
                                                    instanceObject = placedGO,
                                                    yOffset = goRule.placementSurface == QuickTilemapEditor.GameObjectPlacementSurface.Skirt
                                                        ? tilemapEditor.GetRuleInstanceYOffsetForPlacement(goRule, goRule.placementSurface, parentTilemap)
                                                        : goRule.yOffset
                                                };
                                                goRule.instanceOffsets.Add(newOffset);

                                                EditorUtility.SetDirty(tilemapEditor);

                                                int newPathIndex = -1; // Default = not on a path
                                                if (tilemapEditor.selectedPathIndex != -1)
                                                {
                                                    var currentPath = tilemapEditor.paths[tilemapEditor.selectedPathIndex];
                                                    if (currentPath.points.Contains(brushCellPos))
                                                        newPathIndex = tilemapEditor.selectedPathIndex + 1;
                                                }

                                                if (string.IsNullOrEmpty(goRule.id))
                                                    goRule.id = System.Guid.NewGuid().ToString();

                                                /*

                                                tilemapEditor.placedObjects.Add(new QuickTilemapEditor.PlacedObject
                                                {
                                                    position = brushCellPos,
                                                    ruleIndex = tilemapEditor.selectedGameObjectRuleIndex,
                                                    ruleId = goRule.id,
                                                    color = goRule.color,
                                                    pathIndex = newPathIndex,
                                                    rotation = 0f
                                                });
                                                */
                                                var placedData = new QuickTilemapEditor.PlacedObject
                                                {
                                                    position = brushCellPos,
                                                    ruleIndex = tilemapEditor.selectedGameObjectRuleIndex,
                                                    ruleId = goRule.id,
                                                    color = goRule.color,
                                                    pathIndex = newPathIndex,
                                                    rotation = placedGO.transform.eulerAngles.y,
                                                    parentTilemapName = parentTilemap.name,
                                                    placementSurface = goRule.placementSurface,
                                                    skirtAnchorCell = skirtAnchorCell
                                                };
                                                placedData.instanceYOffset = goRule.placementSurface == QuickTilemapEditor.GameObjectPlacementSurface.Skirt
                                                    ? tilemapEditor.GetRuleInstanceYOffsetForPlacement(goRule, placedData.placementSurface, parentTilemap)
                                                    : tilemapEditor.ComputeInstanceYOffset(parentTilemap, brushCellPos, placedGO.transform.position);
                                                placedData.MarkInstanceYOffsetUpgraded();
                                                tilemapEditor.placedObjects.Add(placedData);

                                                // Add QuickTileMarker component to track this GameObject
                                                var marker = placedGO.GetComponent<QuickTileMarker>() ?? placedGO.AddComponent<QuickTileMarker>();
                                                marker.Initialize(
                                                    placedData.UniqueId,
                                                    tilemapEditor.GetInstanceID().ToString(),
                                                    tilemapEditor.selectedGameObjectRuleIndex,
                                                    brushCellPos
                                                );



                                                EditorUtility.SetDirty(tilemapEditor);
                                            }
                                        }
                                        else if (canPaintTiles)
                                        {
                                            var selectedRule = tilemapEditor.GetSelectedTileRule();
                                            if (selectedRule != null && selectedRule.tile != null)
                                            {
                                                tilemapEditor.PaintTile(brushCellPos, selectedRule);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        EraseAt(brushCellPos);
                                    }
                                }
                            }

                            if (!drawMode)
                            {
                                tilemapEditor.needsRefreshPreview = true;
                                EditorUtility.SetDirty(tilemapEditor);
                            }

                            if (textureTabActive)
                                SyncVegetationAfterTexturePaintStroke();
                        }
                        finally
                        {
                            tilemapEditor.EndProceduralSyncBatch();
                        }
                        evt.Use();
                        SceneView.RepaintAll();
                        Repaint();
                    }
                }
                else if (evt.type == EventType.MouseUp)
                {
                    // End painting/erasing
                }
                Repaint();
            }
            else // ICI
            {
                tilemapEditor.ClearPreviewObject();
            }

            // Draw path points and connecting lines over the grid
            foreach (var path in tilemapEditor.paths)
            {
                Vector2? prevCenter = null;
                foreach (var point in path.points)
                {
                    Vector2? overlayPoint = GetInspectorPathOverlayPoint(path, point, paddedGridRect, gridWidth, gridHeight, cellWidth, cellHeight);
                    if (!overlayPoint.HasValue)
                        continue;
                    Vector2 currCenter = overlayPoint.Value;

                    if (ShouldDrawPathAtDualGridIntersection(path))
                    {
                        float markerSize = Mathf.Clamp(Mathf.Min(cellWidth, cellHeight) * 0.28f, 5f, 12f);
                        Rect markerRect = new Rect(
                            currCenter.x - markerSize * 0.5f,
                            currCenter.y - markerSize * 0.5f,
                            markerSize,
                            markerSize);

                        EditorGUI.DrawRect(markerRect, path.color);
                        EditorGUI.DrawRect(new Rect(markerRect.x - 1f, markerRect.y - 1f, markerRect.width + 2f, 1f), Color.black * 0.65f);
                        EditorGUI.DrawRect(new Rect(markerRect.x - 1f, markerRect.yMax, markerRect.width + 2f, 1f), Color.black * 0.65f);
                        EditorGUI.DrawRect(new Rect(markerRect.x - 1f, markerRect.y - 1f, 1f, markerRect.height + 2f), Color.black * 0.65f);
                        EditorGUI.DrawRect(new Rect(markerRect.xMax, markerRect.y - 1f, 1f, markerRect.height + 2f), Color.black * 0.65f);
                    }
                    else
                    {
                        int inspectorGridX = point.x - (int)gridViewOffset.x + gridWidth / 2;
                        int inspectorGridY = gridHeight - 1 - (point.y - (int)gridViewOffset.y + gridHeight / 2);
                        Rect cellRect = new Rect(
                            paddedGridRect.x + inspectorGridX * cellWidth,
                            paddedGridRect.y + inspectorGridY * cellHeight,
                            cellWidth,
                            cellHeight
                        );
                        float borderWidth = 3f;
                        Color borderColor = Color.yellow;

                        EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y, cellRect.width, borderWidth), borderColor);
                        EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y + cellRect.height - borderWidth, cellRect.width, borderWidth), borderColor);
                        EditorGUI.DrawRect(new Rect(cellRect.x, cellRect.y, borderWidth, cellRect.height), borderColor);
                        EditorGUI.DrawRect(new Rect(cellRect.x + cellRect.width - borderWidth, cellRect.y, borderWidth, cellRect.height), borderColor);
                    }

                    if (prevCenter.HasValue)
                    {
                        Handles.color = path.color;
                        Handles.DrawAAPolyLine(3f, new Vector3[] { prevCenter.Value, currCenter });

                    }
                    prevCenter = currCenter;
                }
            }

            foreach (var obj in tilemapEditor.placedObjects)
            {

                int inspectorGridX = obj.position.x - (int)gridViewOffset.x + gridWidth / 2;

                int inspectorGridY = gridHeight - 1 - (obj.position.y - (int)gridViewOffset.y + gridHeight / 2);

                if (inspectorGridX < 0 || inspectorGridX >= gridWidth || inspectorGridY < 0 || inspectorGridY >= gridHeight)
                {
                    continue;
                }

                Rect cellRect = new Rect(
                    paddedGridRect.x + inspectorGridX * cellWidth,
                    paddedGridRect.y + inspectorGridY * cellHeight,
                    cellWidth,
                    cellHeight);

                Rect dotRect = new Rect(cellRect.x + cellRect.width * 0.25f, cellRect.y + cellRect.height * 0.25f, cellRect.width * 0.5f, cellRect.height * 0.5f);
                Color dotColor = Color.magenta; // Couleur par défaut si la règle n'est pas trouvée
                if (obj.ruleIndex != -1 && obj.ruleIndex < tilemapEditor.gameObjectRules.Count)
                {

                    dotColor = tilemapEditor.gameObjectRules[obj.ruleIndex].color;
                }
                else if (obj.color != default(Color))
                {
                    dotColor = obj.color;
                }

                dotColor.a = 0.8f;
                EditorGUI.DrawRect(dotRect, dotColor);

                EditorGUI.DrawRect(new Rect(dotRect.x - 1, dotRect.y - 1, dotRect.width + 2, 1), Color.black * 0.5f); // Top
                EditorGUI.DrawRect(new Rect(dotRect.x - 1, dotRect.yMax, dotRect.width + 2, 1), Color.black * 0.5f);    // Bottom
                EditorGUI.DrawRect(new Rect(dotRect.x - 1, dotRect.y - 1, 1, dotRect.height + 2), Color.black * 0.5f); // Left
                EditorGUI.DrawRect(new Rect(dotRect.xMax, dotRect.y - 1, 1, dotRect.height + 2), Color.black * 0.5f);   // Right
            }

            // 🎨 FEEDBACK VISUEL pour Shift+Click selection
            if (EditorApplication.timeSinceStartup < tempSelectionTime)
            {
                // Calculer la position d'affichage de la sélection temporaire
                int tempX = tempSelectionPos.x - (int)gridViewOffset.x + gridWidth / 2;
                int tempY = gridHeight - 1 - (tempSelectionPos.y - (int)gridViewOffset.y + gridHeight / 2);

                if (tempX >= 0 && tempX < gridWidth && tempY >= 0 && tempY < gridHeight)
                {
                    Rect tempRect = new Rect(
                        paddedGridRect.x + tempX * cellWidth,
                        paddedGridRect.y + tempY * cellHeight,
                        cellWidth,
                        cellHeight
                    );

                    // Animation pulsante
                    float timeElapsed = (float)(EditorApplication.timeSinceStartup - (tempSelectionTime - TEMP_SELECTION_DURATION));
                    float normalizedTime = timeElapsed / TEMP_SELECTION_DURATION;
                    float pulse = Mathf.Sin((float)(EditorApplication.timeSinceStartup * 8f)) * 0.5f + 0.5f;

                    Color feedbackColor = Color.Lerp(tempSelectionColor, Color.white, pulse * 0.7f);
                    feedbackColor.a = 0.9f * (1f - normalizedTime);

                    // Dessiner le feedback avec un fond semi-transparent
                    EditorGUI.DrawRect(tempRect, feedbackColor);

                    // Bordure qui pulse
                    float borderWidth = (3f + pulse * 2f) * (1f - normalizedTime * 0.5f);
                    Color borderColor = Color.yellow;
                    borderColor.a = feedbackColor.a * 1.2f;

                    // Bordures avec effet pulse
                    EditorGUI.DrawRect(new Rect(tempRect.x, tempRect.y, tempRect.width, borderWidth), borderColor);
                    EditorGUI.DrawRect(new Rect(tempRect.x, tempRect.y, borderWidth, tempRect.height), borderColor);
                    EditorGUI.DrawRect(new Rect(tempRect.x + tempRect.width - borderWidth, tempRect.y, borderWidth, tempRect.height), borderColor);
                    EditorGUI.DrawRect(new Rect(tempRect.x, tempRect.y + tempRect.height - borderWidth, tempRect.width, borderWidth), borderColor);

                    // Texte "SELECTED" qui fade
                    GUI.color = new Color(1f, 1f, 1f, feedbackColor.a);
                    GUIStyle style = new GUIStyle(EditorStyles.boldLabel);
                    style.alignment = TextAnchor.MiddleCenter;
                    style.fontSize = (int)(8f * (1f + pulse * 0.3f));
                    GUI.Label(tempRect, "SELECTED", style);
                    GUI.color = Color.white;
                }
            }


            DrawGridOverlayControls(actionOverlayRect, Rect.zero);

            if (drawMode && tilemapEditor.activeTile == null)
                EditorGUILayout.HelpBox("No active tile selected! Please select a tile to paint with.", MessageType.Warning);
        }

        private void DrawSurfaceOutlines_V1(
            List<(float y, float start, float end)> horizontalSegments,
            List<(float x, float start, float end)> verticalSegments)
        {
            const float borderWidth = 1f;
            const float dashSize = 4f;
            Color outlineColor = new Color(0f, 0f, 0f, 0.8f);

            foreach (var segment in MergeHorizontalOutlineSegments_V2(horizontalSegments))
            {
                float width = segment.end - segment.start;
                if (width <= 0.01f)
                    continue;

                DrawDashedHorizontalLine(
                    new Vector2(segment.start, segment.y - borderWidth * 0.5f),
                    width,
                    dashSize,
                    borderWidth,
                    outlineColor);
            }

            foreach (var segment in MergeVerticalOutlineSegments_V2(verticalSegments))
            {
                float height = segment.end - segment.start;
                if (height <= 0.01f)
                    continue;

                DrawDashedVerticalLine(
                    new Vector2(segment.x - borderWidth * 0.5f, segment.start),
                    height,
                    dashSize,
                    borderWidth,
                    outlineColor);
            }
        }

        private void DrawPickerHoverPreview_V1(Rect cellRect)
        {
            float size = Mathf.Min(cellRect.width, cellRect.height) * 0.46f;
            Rect previewRect = new Rect(
                cellRect.center.x - size * 0.5f,
                cellRect.center.y - size * 0.5f,
                size,
                size);

            float dashThickness = Mathf.Max(1f, Mathf.Round(size * 0.08f));
            float dashLength = Mathf.Max(3f, size * 0.24f);
            Color outlineColor = new Color(1f, 1f, 1f, 0.95f);

            DrawDashedHorizontalLine(new Vector2(previewRect.xMin, previewRect.yMin), previewRect.width, dashLength, dashThickness, outlineColor);
            DrawDashedHorizontalLine(new Vector2(previewRect.xMin, previewRect.yMax - dashThickness), previewRect.width, dashLength, dashThickness, outlineColor);
            DrawDashedVerticalLine(new Vector2(previewRect.xMin, previewRect.yMin), previewRect.height, dashLength, dashThickness, outlineColor);
            DrawDashedVerticalLine(new Vector2(previewRect.xMax - dashThickness, previewRect.yMin), previewRect.height, dashLength, dashThickness, outlineColor);
        }

        private void CollectRectUnionContourSegments_V1(
            List<Rect> rects,
            out List<(float y, float start, float end)> horizontalSegments,
            out List<(float x, float start, float end)> verticalSegments)
        {
            horizontalSegments = new List<(float y, float start, float end)>();
            verticalSegments = new List<(float x, float start, float end)>();

            if (rects == null || rects.Count == 0)
                return;

            List<Rect> validRects = rects
                .Where(rect => rect.width > 0.01f && rect.height > 0.01f)
                .Select(QuantizeRectForOutline_V2)
                .Distinct()
                .ToList();

            if (validRects.Count == 0)
                return;

            List<float> xCoords = validRects
                .SelectMany(rect => new[] { rect.xMin, rect.xMax })
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            List<float> yCoords = validRects
                .SelectMany(rect => new[] { rect.yMin, rect.yMax })
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

            bool[,] filledGrid = new bool[xCoords.Count - 1, yCoords.Count - 1];
            foreach (Rect rect in validRects)
            {
                if (!xIndexMap.TryGetValue(rect.xMin, out int xStart) ||
                    !xIndexMap.TryGetValue(rect.xMax, out int xEnd) ||
                    !yIndexMap.TryGetValue(rect.yMin, out int yStart) ||
                    !yIndexMap.TryGetValue(rect.yMax, out int yEnd))
                    continue;

                for (int x = xStart; x < xEnd; x++)
                {
                    for (int y = yStart; y < yEnd; y++)
                        filledGrid[x, y] = true;
                }
            }

            for (int x = 0; x < filledGrid.GetLength(0); x++)
            {
                for (int y = 0; y < filledGrid.GetLength(1); y++)
                {
                    if (!filledGrid[x, y])
                        continue;

                    float xMin = xCoords[x];
                    float xMax = xCoords[x + 1];
                    float yMin = yCoords[y];
                    float yMax = yCoords[y + 1];

                    if (y == 0 || !filledGrid[x, y - 1])
                        horizontalSegments.Add((yMin, xMin, xMax));

                    if (y == filledGrid.GetLength(1) - 1 || !filledGrid[x, y + 1])
                        horizontalSegments.Add((yMax, xMin, xMax));

                    if (x == 0 || !filledGrid[x - 1, y])
                        verticalSegments.Add((xMin, yMin, yMax));

                    if (x == filledGrid.GetLength(0) - 1 || !filledGrid[x + 1, y])
                        verticalSegments.Add((xMax, yMin, yMax));
                }
            }
        }

        private QuickTilemapEditor.Path GetSelectedInspectorPath()
        {
            if (tilemapEditor == null || tilemapEditor.paths == null)
                return null;

            if (tilemapEditor.selectedPathIndex < 0 || tilemapEditor.selectedPathIndex >= tilemapEditor.paths.Count)
                return null;

            return tilemapEditor.paths[tilemapEditor.selectedPathIndex];
        }

        private void DrawInspectorPathPreviewMarker(Vector2 center, float cellWidth, float cellHeight, Color fillColor, Color outlineColor)
        {
            float markerSize = Mathf.Clamp(Mathf.Min(cellWidth, cellHeight) * 0.32f, 6f, 14f);
            Rect markerRect = new Rect(
                center.x - markerSize * 0.5f,
                center.y - markerSize * 0.5f,
                markerSize,
                markerSize);

            fillColor.a = Mathf.Max(fillColor.a, 0.7f);
            outlineColor.a = Mathf.Max(outlineColor.a, 0.9f);

            EditorGUI.DrawRect(markerRect, fillColor);

            float outlineWidth = 2f;
            EditorGUI.DrawRect(new Rect(markerRect.x - outlineWidth, markerRect.y - outlineWidth, markerRect.width + outlineWidth * 2f, outlineWidth), outlineColor);
            EditorGUI.DrawRect(new Rect(markerRect.x - outlineWidth, markerRect.yMax, markerRect.width + outlineWidth * 2f, outlineWidth), outlineColor);
            EditorGUI.DrawRect(new Rect(markerRect.x - outlineWidth, markerRect.y - outlineWidth, outlineWidth, markerRect.height + outlineWidth * 2f), outlineColor);
            EditorGUI.DrawRect(new Rect(markerRect.xMax, markerRect.y - outlineWidth, outlineWidth, markerRect.height + outlineWidth * 2f), outlineColor);
        }

        private bool ShouldDrawTextureAtDualGridIntersection()
        {
            return tilemapEditor != null &&
                   tilemapEditor.IsDualGrid();
        }

        private Vector2? GetInspectorTextureOverlayPoint(Vector3Int point, Rect paddedGridRect,
                                                         int gridWidth, int gridHeight, float cellWidth, float cellHeight)
        {
            float localX = point.x - (int)gridViewOffset.x + gridWidth / 2f;
            float localY = gridHeight - 1 - (point.y - (int)gridViewOffset.y + gridHeight / 2f);

            if (ShouldDrawTextureAtDualGridIntersection())
            {
                float pixelX = paddedGridRect.x + localX * cellWidth;
                float pixelY = paddedGridRect.y + (localY + 1f) * cellHeight;

                if (pixelX < paddedGridRect.xMin || pixelX > paddedGridRect.xMax ||
                    pixelY < paddedGridRect.yMin || pixelY > paddedGridRect.yMax)
                    return null;

                return new Vector2(pixelX, pixelY);
            }

            if (localX < 0f || localX >= gridWidth || localY < 0f || localY >= gridHeight)
                return null;

            return new Vector2(
                paddedGridRect.x + (localX + 0.5f) * cellWidth,
                paddedGridRect.y + (localY + 0.5f) * cellHeight);
        }

        private void DrawInspectorTextureCellOverlay(Texture2D texture, Vector2 center, float cellWidth, float cellHeight)
        {
            if (texture == null)
                return;

            float textureSize = Mathf.Clamp(Mathf.Min(cellWidth, cellHeight) * 0.58f, 10f, 26f);
            Rect textureRect = new Rect(
                center.x - textureSize * 0.5f,
                center.y - textureSize * 0.5f,
                textureSize,
                textureSize);

            GUI.color = Color.white;
            GUI.DrawTexture(textureRect, texture, ScaleMode.ScaleToFit);
            GUI.color = Color.white;
        }

        private void DrawInspectorTexturePreviewMarker(Texture2D texture, Vector2 center, float cellWidth, float cellHeight, Color fillColor, Color outlineColor)
        {
            float previewSize = Mathf.Clamp(Mathf.Min(cellWidth, cellHeight) * 0.58f, 10f, 26f);
            Rect previewRect = new Rect(
                center.x - previewSize * 0.5f,
                center.y - previewSize * 0.5f,
                previewSize,
                previewSize);

            fillColor.a = Mathf.Max(fillColor.a, 0.38f);
            outlineColor.a = Mathf.Max(outlineColor.a, 0.85f);

            EditorGUI.DrawRect(previewRect, fillColor);

            float outlineWidth = Mathf.Max(1f, Mathf.Round(previewSize * 0.08f));
            EditorGUI.DrawRect(new Rect(previewRect.xMin, previewRect.yMin, previewRect.width, outlineWidth), outlineColor);
            EditorGUI.DrawRect(new Rect(previewRect.xMin, previewRect.yMax - outlineWidth, previewRect.width, outlineWidth), outlineColor);
            EditorGUI.DrawRect(new Rect(previewRect.xMin, previewRect.yMin, outlineWidth, previewRect.height), outlineColor);
            EditorGUI.DrawRect(new Rect(previewRect.xMax - outlineWidth, previewRect.yMin, outlineWidth, previewRect.height), outlineColor);

            if (texture != null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.9f);
                GUI.DrawTexture(previewRect, texture, ScaleMode.ScaleToFit);
                GUI.color = Color.white;
            }
        }

        private bool ShouldDrawPathAtDualGridIntersection(QuickTilemapEditor.Path path)
        {
            return tilemapEditor != null &&
                   tilemapEditor.IsDualGrid() &&
                   path != null &&
                   (path.pathType == QuickTilemapEditor.PathType.Slope ||
                    path.pathType == QuickTilemapEditor.PathType.Stairs);
        }

        private Vector2? GetInspectorPathOverlayPoint(QuickTilemapEditor.Path path, Vector3Int point, Rect paddedGridRect,
                                                      int gridWidth, int gridHeight, float cellWidth, float cellHeight)
        {
            float localX = point.x - (int)gridViewOffset.x + gridWidth / 2f;
            float localY = gridHeight - 1 - (point.y - (int)gridViewOffset.y + gridHeight / 2f);

            if (ShouldDrawPathAtDualGridIntersection(path))
            {
                float pixelX = paddedGridRect.x + localX * cellWidth;
                float pixelY = paddedGridRect.y + (localY + 1f) * cellHeight;

                if (pixelX < paddedGridRect.xMin || pixelX > paddedGridRect.xMax ||
                    pixelY < paddedGridRect.yMin || pixelY > paddedGridRect.yMax)
                    return null;

                return new Vector2(pixelX, pixelY);
            }

            if (localX < 0f || localX >= gridWidth || localY < 0f || localY >= gridHeight)
                return null;

            return new Vector2(
                paddedGridRect.x + (localX + 0.5f) * cellWidth,
                paddedGridRect.y + (localY + 0.5f) * cellHeight);
        }
    }
}
