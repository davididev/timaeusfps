using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using Bekkoloco.DOTS;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        private const string DefaultBridgeMaterialPath = "Assets/BEKKOLOCO/QuickTile/Material/bridge.mat";
        private readonly List<bool> pathCardExpandedStates = new List<bool>();

        private void EnsurePathCardExpandedStateCount()
        {
            if (tilemapEditor?.paths == null) return;

            while (pathCardExpandedStates.Count < tilemapEditor.paths.Count)
                pathCardExpandedStates.Add(false);

            while (pathCardExpandedStates.Count > tilemapEditor.paths.Count)
                pathCardExpandedStates.RemoveAt(pathCardExpandedStates.Count - 1);
        }

        private void SetPathCardExpandedExclusive(int index, bool expanded, bool refreshUIToolkit = true)
        {
            if (tilemapEditor?.paths == null || index < 0 || index >= tilemapEditor.paths.Count)
                return;

            EnsurePathCardExpandedStateCount();

            for (int i = 0; i < pathCardExpandedStates.Count; i++)
                pathCardExpandedStates[i] = false;

            if (expanded)
            {
                pathCardExpandedStates[index] = true;
                tilemapEditor.selectedPathIndex = index;
                tilemapEditor.selectedTileRuleIndex = -1;
                tilemapEditor.selectedGameObjectRuleIndex = -1;
                tilemapEditor.selectedTextureRule = null;
                drawMode = true;
            }

            EditorUtility.SetDirty(tilemapEditor);

            if (refreshUIToolkit && pathUIToolkitContainer != null)
            {
                var pathsList = pathUIToolkitContainer.Q<VisualElement>("paths-list");
                if (pathsList != null)
                    RefreshPathList_UIToolkit(pathsList);
            }

            Repaint();
        }

        private void ApplyDefaultBridgeSettings(QuickTilemapEditor.Path path)
        {
            if (path == null) return;

            path.bridgeWidth = 1f;
            path.bridgeProfile = QuickTilemapEditor.BridgeProfile.Curved;
            path.bridgeCurve = 0.32f;
            path.bridgeSteps = 6;
            path.bridgeRailings = true;
            path.bridgeRailThickness = 0.316f;
            path.bridgeRailSpread = 0f;
            path.bridgeRailEndExtension = 0f;
            path.bridgeRailYOffset = 0f;
            path.bridgeRailUvOffsetY = -0.858f;
            path.bridgeRailCurveFollow = 0f;
            path.bridgeMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultBridgeMaterialPath);
        }

        private void EnsureDefaultStairRailMaterial(QuickTilemapEditor.Path path)
        {
            if (path == null || path.stairRailMaterial != null)
                return;

            path.stairRailMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultBridgeMaterialPath);
        }

        private static string GetVisibilityToggleIcon(bool isVisible)
        {
            return isVisible ? "🙉" : "🙈";
        }

        private void DrawPathSection()
        {
            EditorGUILayout.LabelField("Path & links", EditorStyles.boldLabel);

            // Initialize paths if needed
            EnsurePathsInitialized();

            if (tilemapEditor.paths == null || tilemapEditor.paths.Count == 0)
            {
                EditorGUILayout.BeginVertical();
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                GUIStyle centeredBigLabel = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 20,
                    padding = new RectOffset(-20, -20, -10, -10)
                };

                EditorGUILayout.LabelField("Please add a Path \n↓", centeredBigLabel);

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
            }
            else
            {
                for (int i = 0; i < tilemapEditor.paths.Count; i++)
                {
                    if (tilemapEditor.paths[i] == null)
                    {
                        tilemapEditor.paths[i] = new QuickTilemapEditor.Path();
                        tilemapEditor.paths[i].points = new List<Vector3Int>();
                        tilemapEditor.paths[i].color = Color.yellow;
                        EditorUtility.SetDirty(tilemapEditor);
                    }

                    EditorGUILayout.BeginHorizontal();
                    GUI.color = (tilemapEditor.selectedPathIndex == i) ? Color.green : Color.gray;
                    if (GUILayout.Button("Select Path " + (i + 1), GUILayout.Width(100), GUILayout.Height(80)))
                    {
                        tilemapEditor.selectedPathIndex = i;
                        tilemapEditor.selectedTileRuleIndex = -1;
                        tilemapEditor.selectedGameObjectRuleIndex = -1;
                        tilemapEditor.selectedTextureRule = null;

                        // Ensure we're in draw mode when selecting a path
                        drawMode = true;
                    }

                    GUI.color = Color.white;

                    if (GUILayout.Button("Close Path", GUILayout.Width(100)))
                    {
                        var path = tilemapEditor.paths[i];
                        if (path.points != null && path.points.Count > 0 &&
                           (path.points.Count <= 1 || path.points[0] != path.points[path.points.Count - 1]))
                        {
                            if (path.points.Count > 1)
                            {
                                path.points.Add(path.points[0]);
                                EditorUtility.SetDirty(tilemapEditor);
                            }
                        }
                    }

                    if (GUILayout.Button(GetVisibilityToggleIcon(tilemapEditor.paths[i].isVisible), GUILayout.Width(28)))
                    {
                        TogglePathVisibility(i);
                    }

                    // Add some debug info about the path
                    EditorGUILayout.LabelField($"Points: {tilemapEditor.paths[i].points?.Count ?? 0}",
                                              GUILayout.Width(80));

                    bool removePath = GUILayout.Button("Remove", GUILayout.Width(80));
                    if (removePath)
                    {
                        RemovePath(i);
                    }

                    EditorGUILayout.EndHorizontal();

                    if (removePath)
                        break;

                    // Path settings - shown when this path is selected
                    if (tilemapEditor.selectedPathIndex == i)
                    {
                        var path = tilemapEditor.paths[i];

                        EditorGUILayout.BeginVertical();
                        EditorGUI.indentLevel++;

                        // ── Path Type dropdown ──
                        var oldType = path.pathType;
                        path.pathType = (QuickTilemapEditor.PathType)EditorGUILayout.EnumPopup("Type", path.pathType);
                        if (oldType != path.pathType)
                        {
                            if (path.pathType == QuickTilemapEditor.PathType.Bridge)
                                ApplyDefaultBridgeSettings(path);
                            else if (path.pathType == QuickTilemapEditor.PathType.Stairs)
                                EnsureDefaultStairRailMaterial(path);

                            // Auto-enable track mesh for Track type
                            if (path.pathType == QuickTilemapEditor.PathType.Track)
                                path.enableTrackMesh = true;
                            else
                                path.enableTrackMesh = false;
                            tilemapEditor.RebuildAllTrackMeshes();
                            EditorUtility.SetDirty(tilemapEditor);
                        }

                        EditorGUILayout.Space(4);

                        // ── Type-specific settings ──
                        EditorGUI.BeginChangeCheck();
                        switch (path.pathType)
                        {
                            case QuickTilemapEditor.PathType.Slope:
                                path.slopeWidth = EditorGUILayout.IntSlider("Width", Mathf.RoundToInt(path.slopeWidth), 1, 4);
                                path.smoothTransition = EditorGUILayout.Toggle("Smooth Transition", path.smoothTransition);
                                path.slopeSideSkirtEnabled = EditorGUILayout.Toggle("Side Skirt", path.slopeSideSkirtEnabled);
                                if (path.slopeSideSkirtEnabled)
                                {
                                    path.slopeSideSkirtWidth = EditorGUILayout.Slider("Skirt Width", path.slopeSideSkirtWidth, 0f, 0.3f);
                                    path.slopeSideSkirtHeight = EditorGUILayout.Slider("Skirt Height", path.slopeSideSkirtHeight, 0f, 0.5f);
                                    path.slopeSideSkirtSegments = EditorGUILayout.IntSlider("Skirt Segments", path.slopeSideSkirtSegments, 1, 8);
                                    path.slopeSideSkirtUVScale = EditorGUILayout.Slider("Skirt UV Scale", path.slopeSideSkirtUVScale, 0.1f, 10f);
                                }
                                path.slopeSurfaceMaterial = EditorGUILayout.ObjectField("Surface Material", path.slopeSurfaceMaterial, typeof(Material), false) as Material;
                                path.slopeWallMaterial = EditorGUILayout.ObjectField("Wall Material", path.slopeWallMaterial, typeof(Material), false) as Material;
                                break;

                            case QuickTilemapEditor.PathType.Stairs:
                                EnsureDefaultStairRailMaterial(path);
                                path.stairAutoSteps = EditorGUILayout.Toggle("Auto Steps", path.stairAutoSteps);
                                if (path.stairAutoSteps)
                                    path.stairStepDepth = EditorGUILayout.Slider("Step Length", path.stairStepDepth, 0.1f, 4f);
                                else
                                    path.stairSteps = EditorGUILayout.IntSlider("Steps", path.stairSteps, 2, 16);
                                path.slopeWidth = EditorGUILayout.IntSlider("Width", Mathf.RoundToInt(path.slopeWidth), 1, 4);
                                path.bridgeRailings = EditorGUILayout.Toggle("Railings", path.bridgeRailings);
                                if (path.bridgeRailings)
                                {
                                    path.bridgeRailThickness = EditorGUILayout.Slider("Rail Thickness", path.bridgeRailThickness, 0.02f, 0.5f);
                                    path.bridgeRailSpread = EditorGUILayout.Slider("Rail Spread", path.bridgeRailSpread, -1f, 1f);
                                    path.bridgeRailEndExtension = EditorGUILayout.Slider("Rail End Extension", path.bridgeRailEndExtension, 0f, 2f);
                                    path.bridgeRailYOffset = EditorGUILayout.Slider("Rail Y Offset", path.bridgeRailYOffset, -1f, 1f);
                                    path.stairRailMaterial = EditorGUILayout.ObjectField("Rail Material", path.stairRailMaterial, typeof(Material), false) as Material;
                                }
                                path.slopeSurfaceMaterial = EditorGUILayout.ObjectField("Surface Material", path.slopeSurfaceMaterial, typeof(Material), false) as Material;
                                path.slopeWallMaterial = EditorGUILayout.ObjectField("Wall Material", path.slopeWallMaterial, typeof(Material), false) as Material;
                                break;

                            case QuickTilemapEditor.PathType.Bridge:
                                path.bridgeWidth = EditorGUILayout.IntSlider("Width", Mathf.RoundToInt(path.bridgeWidth), 1, 4);
                                path.bridgeProfile = (QuickTilemapEditor.BridgeProfile)EditorGUILayout.EnumPopup("Profile", path.bridgeProfile);
                                path.bridgeCurve = EditorGUILayout.Slider("Curve", path.bridgeCurve, -5f, 5f);
                                if (path.bridgeProfile == QuickTilemapEditor.BridgeProfile.Stepped)
                                    path.bridgeSteps = EditorGUILayout.IntSlider("Steps", path.bridgeSteps, 2, 16);
                                path.bridgeRailings = EditorGUILayout.Toggle("Railings", path.bridgeRailings);
                                path.bridgeRailThickness = EditorGUILayout.Slider("Rail Thickness", path.bridgeRailThickness, 0.02f, 0.5f);
                                path.bridgeRailSpread = EditorGUILayout.Slider("Rail Spread", path.bridgeRailSpread, -1f, 1f);
                                path.bridgeRailEndExtension = EditorGUILayout.Slider("Rail End Extension", path.bridgeRailEndExtension, 0f, 2f);
                                path.bridgeRailYOffset = EditorGUILayout.Slider("Rail Y Offset", path.bridgeRailYOffset, -1f, 1f);
                                path.bridgeRailUvOffsetY = EditorGUILayout.Slider("Rail UV Offset Y", path.bridgeRailUvOffsetY, -1f, 1f);
                                path.bridgeRailCurveFollow = EditorGUILayout.Slider("Rail Curve Follow", path.bridgeRailCurveFollow, 0f, 2f);
                                path.bridgeMaterial = EditorGUILayout.ObjectField("Bridge Material", path.bridgeMaterial, typeof(Material), false) as Material;
                                break;

                            case QuickTilemapEditor.PathType.Track:
                                // Enable Track Mesh toggle
                                bool oldEnableTrackMesh = path.enableTrackMesh;
                                path.enableTrackMesh = EditorGUILayout.Toggle("Enable Track Mesh", path.enableTrackMesh);
                                if (oldEnableTrackMesh != path.enableTrackMesh)
                                {
                                    SyncTrackPoints(path);
                                    EditorUtility.SetDirty(tilemapEditor);
                                }

                                if (path.enableTrackMesh)
                                {
                                    EditorGUILayout.Space();
                                    path.trackWidth = EditorGUILayout.FloatField("Default Width", path.trackWidth);
                                    path.trackSubdivisions = EditorGUILayout.IntSlider("Subdivisions", path.trackSubdivisions, 1, 20);
                                    path.trackMaterial = EditorGUILayout.ObjectField("Track Material", path.trackMaterial, typeof(Material), false) as Material;
                                    path.trackUVTilingY = EditorGUILayout.FloatField("UV Tiling Y", path.trackUVTilingY);

                                    EditorGUILayout.Space();
                                    path.trackPointsFoldout = EditorGUILayout.Foldout(path.trackPointsFoldout, "Track Points (" + path.trackPoints.Count + ")");
                                    if (path.trackPointsFoldout)
                                    {
                                        EditorGUI.indentLevel++;
                                        for (int ptIdx = 0; ptIdx < path.trackPoints.Count; ptIdx++)
                                        {
                                            var trackPoint = path.trackPoints[ptIdx];
                                            EditorGUILayout.LabelField($"Point {ptIdx}", EditorStyles.boldLabel);
                                            EditorGUI.indentLevel++;
                                            trackPoint.snapToGround = EditorGUILayout.Toggle("Snap to Ground", trackPoint.snapToGround);
                                            trackPoint.rotation = EditorGUILayout.FloatField("Rotation", trackPoint.rotation);
                                            trackPoint.width = EditorGUILayout.FloatField("Width", trackPoint.width);
                                            EditorGUI.indentLevel--;
                                            EditorGUILayout.Space(5);
                                        }
                                        EditorGUI.indentLevel--;
                                    }
                                    EditorUtility.SetDirty(tilemapEditor);
                                }
                                break;

                            case QuickTilemapEditor.PathType.Move:
                            default:
                                EditorGUILayout.HelpBox("Movement path only. Assign GameObjects to follow this path.", MessageType.Info);
                                break;
                        }
                        if (EditorGUI.EndChangeCheck())
                        {
                            tilemapEditor.RebuildAllTrackMeshes();
                            EditorUtility.SetDirty(tilemapEditor);
                        }

                        EditorGUI.indentLevel--;
                        EditorGUILayout.EndVertical();
                        EditorGUILayout.Space();
                    }
                }
            }
        }

        private void HandlePathAssignment(Vector3Int cellPos)
        {
            // Only process if we're in path selection mode and have a valid path selected
            if (tilemapEditor.selectedPathIndex < 0 || tilemapEditor.selectedPathIndex >= tilemapEditor.paths.Count)
                return;

            // Check if there's a game object at this position
            bool objectFound = false;
            foreach (var placedObj in tilemapEditor.placedObjects)
            {
                if (placedObj.position == cellPos)
                {
                    objectFound = true;

                    // If object already has this path, toggle it off
                    if (placedObj.pathIndex == tilemapEditor.selectedPathIndex + 1)
                    {
                        if (EditorUtility.DisplayDialog("Remove from Path",
                            $"Remove object at {cellPos} from path {tilemapEditor.selectedPathIndex + 1}?",
                            "Yes", "No"))
                        {
                            tilemapEditor.AssignPathToObject(cellPos, -1); // -1 means no path
                            EditorUtility.SetDirty(tilemapEditor);
                            //Debug.Log(($"Removed object at {cellPos} from path {tilemapEditor.selectedPathIndex + 1}");
                        }
                    }
                    // Otherwise assign the currently selected path
                    else
                    {
                        tilemapEditor.AssignPathToObject(cellPos, tilemapEditor.selectedPathIndex);
                        EditorUtility.SetDirty(tilemapEditor);
                        //Debug.Log(($"Assigned path {tilemapEditor.selectedPathIndex + 1} to object at {cellPos}");
                    }
                    break;
                }
            }

            if (!objectFound)
            {
                EditorUtility.DisplayDialog("No Object Found",
                    $"No game object found at position {cellPos}. Place an object here first, then assign it to a path.",
                    "OK");
            }
        }


        private void EnsurePathsInitialized()
        {
            if (tilemapEditor.paths == null)
            {
                tilemapEditor.paths = new List<QuickTilemapEditor.Path>();
                EditorUtility.SetDirty(tilemapEditor);
            }

            // Add cleanup for any invalid/null entries
            for (int i = tilemapEditor.paths.Count - 1; i >= 0; i--)
            {
                if (tilemapEditor.paths[i] == null)
                {
                    tilemapEditor.paths.RemoveAt(i);
                    continue;
                }

                if (tilemapEditor.paths[i].points == null)
                {
                    tilemapEditor.paths[i].points = new List<Vector3Int>();
                }
            }
        }


        private void SafeUpdateGameObjectPathFollower(Vector3Int cellPos, QuickTilemapEditor.Path path)
        {
            if (path == null || path.points == null || path.points.Count == 0)
            {
                //Debug.Log(Warning("Cannot update PathFollower: invalid path data");
                return;
            }

            // Safety checks for the tilemapEditor
            if (tilemapEditor == null || tilemapEditor.instantiatedGameObjects == null)
            {
                //Debug.Log(Error("QuickTilemapEditor reference is invalid or instantiatedGameObjects is null");
                return;
            }

            // Safety check for targetTilemap
            if (tilemapEditor.targetTilemap == null)
            {
                //Debug.Log(Error("Target tilemap is null, cannot convert path points to world coordinates");
                return;
            }

            // Find any GameObjects at this cell position
            // We'll clean up nulls afterward in one pass
            foreach (GameObject go in tilemapEditor.instantiatedGameObjects)
            {
                // Check for null references (destroyed GameObjects)
                if (go == null)
                {
                    continue;
                }

                // Check for valid transform & parent
                if (go.transform == null || go.transform.parent == null)
                {
                    //Debug.Log(Warning($"GameObject has null transform or parent: {go.name}");
                    continue;
                }

                Tilemap parentMap = go.transform.parent.GetComponent<Tilemap>();
                if (parentMap == null) continue;

                Vector3Int goCell = tilemapEditor.SafeWorldToCell(go.transform.position, parentMap);

                // If this GameObject is at the cell position
                if (goCell == cellPos)
                {
                    try
                    {
                        // Get or add PathFollower component
                        PathFollower pf = go.GetComponent<PathFollower>();
                        if (pf == null)
                        {
                            pf = go.AddComponent<PathFollower>();
                        }

                        // Set path index
                        pf.SetPathIndex(tilemapEditor.selectedPathIndex);

                        // Safely convert path points to world coordinates
                        List<Vector2> worldPath = new List<Vector2>();
                        foreach (var p in path.points)
                        {
                            Vector3 worldPos = tilemapEditor.GetPathWorldPos(path, p);
                            worldPath.Add(new Vector2(worldPos.x, worldPos.z));
                        }

                        // Set the path
                        pf.SetPath(worldPath);

                        // Start moving if in play mode
                        if (Application.isPlaying)
                        {
                            pf.StartMoving();
                        }

                        EditorUtility.SetDirty(go);
                        //Debug.Log(($"Updated PathFollower on GameObject {go.name} at {cellPos} - Path has {worldPath.Count} points");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Error updating PathFollower: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }

            // Clean up any null references in the instantiatedGameObjects list
            int removedCount = tilemapEditor.instantiatedGameObjects.RemoveAll(go => go == null);

            if (removedCount > 0)
            {
                EditorUtility.SetDirty(tilemapEditor);
            }
        }


        private void SafeHandlePathEvents(Vector2 mousePos, Rect paddedGridRect, int gridWidth, int gridHeight, float cellWidth, float cellHeight, Vector2 gridViewOffset, Event evt)
        {
            if (tilemapEditor.selectedPathIndex == -1)
                return;

            try
            {
                // Ensure the path index is valid
                if (tilemapEditor.selectedPathIndex >= tilemapEditor.paths.Count)
                {
                    //Debug.Log(Error($"Selected path index {tilemapEditor.selectedPathIndex} is out of range (paths count: {tilemapEditor.paths.Count})");
                    return;
                }

                var activePath = tilemapEditor.paths[tilemapEditor.selectedPathIndex];
                if (activePath == null)
                {
                    //Debug.Log(Error("Selected path is null");
                    return;
                }

                if (activePath.points == null)
                {
                    activePath.points = new List<Vector3Int>();
                }

                // Calculate cell position from mouse position
                int x = Mathf.FloorToInt((mousePos.x - paddedGridRect.x) / cellWidth);
                int y = gridHeight - 1 - Mathf.FloorToInt((mousePos.y - paddedGridRect.y) / cellHeight);
                Vector3Int cellPos = new Vector3Int(
                    x - gridWidth / 2 + (int)gridViewOffset.x,
                    y - gridHeight / 2 + (int)gridViewOffset.y,
                    0);

                if (drawMode)
                {
                    // In draw mode: add point to path if it doesn't exist
                    if (!activePath.points.Contains(cellPos))
                    {
                        activePath.points.Add(cellPos);
                        SyncTrackPoints(activePath);

                        // Update any GameObjects at this position
                        SafeUpdateGameObjectPathFollower(cellPos, activePath);

                        // Rebuild meshes for Slope/Stairs/Bridge/Track paths
                        tilemapEditor.RebuildAllTrackMeshes();

                        EditorUtility.SetDirty(tilemapEditor);
                        evt.Use();

                        // Make sure to repaint the scene view to show the updated path
                        SceneView.RepaintAll();
                    }
                }
                else
                {
                    // In erase mode: remove point from path if it exists
                    if (activePath.points.Contains(cellPos))
                    {
                        activePath.points.Remove(cellPos);
                        SyncTrackPoints(activePath);
                        tilemapEditor.RebuildAllTrackMeshes();
                        EditorUtility.SetDirty(tilemapEditor);
                        evt.Use();

                        // Make sure to repaint the scene view to show the updated path
                        SceneView.RepaintAll();
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Error in path handling: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ShowTemporarySelectionFeedback(Vector3Int cellPos, Color color)
        {
            tempSelectionPos = cellPos;
            tempSelectionColor = color;
            tempSelectionTime = (float)EditorApplication.timeSinceStartup + TEMP_SELECTION_DURATION;

            // Schedule a single delayed repaint for the selection feedback
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.timeSinceStartup < tempSelectionTime)
                    Repaint();
            };
        }

        private void SyncTrackPoints(QuickTilemapEditor.Path path)
        {
            if (path == null || path.points == null)
                return;

            int pointCount = path.points.Count;
            int trackPointCount = path.trackPoints != null ? path.trackPoints.Count : 0;

            // If trackPoints list doesn't exist, create it
            if (path.trackPoints == null)
                path.trackPoints = new List<QuickTilemapEditor.TrackPoint>();

            // Add missing track points
            if (trackPointCount < pointCount)
            {
                for (int i = trackPointCount; i < pointCount; i++)
                {
                    var newTrackPoint = new QuickTilemapEditor.TrackPoint()
                    {
                        gridPosition = path.points[i],
                        snapToGround = true,
                        rotation = 0f,
                        width = 1f
                    };
                    path.trackPoints.Add(newTrackPoint);
                }
            }
            // Remove extra track points if points list is shorter
            else if (trackPointCount > pointCount)
            {
                path.trackPoints.RemoveRange(pointCount, trackPointCount - pointCount);
            }

            // Sync grid positions
            for (int i = 0; i < path.trackPoints.Count && i < path.points.Count; i++)
            {
                path.trackPoints[i].gridPosition = path.points[i];
            }
        }

        #region UI Toolkit Methods - Path

        public VisualElement CreatePathSection_UIToolkit()
        {
            var container = new VisualElement();
            container.name = "path-section";
            
            var styleSheet = Resources.Load<StyleSheet>("QuickTilemapEditor");
            if (styleSheet != null) container.styleSheets.Add(styleSheet);

            var header = new Label("🔗 Path & Links");
            header.AddToClassList("section-header");
            container.Add(header);

            // Ensure paths are initialized
            EnsurePathsInitialized();

            var pathsContainer = new VisualElement();
            pathsContainer.name = "paths-list";
            pathsContainer.AddToClassList("rules-scroll");

            var addButton = new Button(() => {
                AddPath();
                RefreshPathList_UIToolkit(pathsContainer);
            });
            addButton.text = "+ Add Path";
            addButton.AddToClassList("btn-add");
            container.Add(addButton);

            container.Add(pathsContainer);

            RefreshPathList_UIToolkit(pathsContainer);

            return container;
        }

        private void RefreshPathList_UIToolkit(VisualElement container)
        {
            container.Clear();
            EnsurePathCardExpandedStateCount();
            
            if (tilemapEditor.paths == null || tilemapEditor.paths.Count == 0)
            {
                var emptyState = new VisualElement();
                emptyState.AddToClassList("empty-state");
                var emptyLabel = new Label("No paths created yet.");
                emptyLabel.AddToClassList("empty-state-text");
                emptyState.Add(emptyLabel);
                container.Add(emptyState);
                return;
            }

            for (int i = 0; i < tilemapEditor.paths.Count; i++)
            {
                var pathCard = CreatePathCard_UIToolkit(i, container);
                container.Add(pathCard);
            }
        }

        private VisualElement CreatePathCard_UIToolkit(int index, VisualElement listContainer)
        {
            var path = tilemapEditor.paths[index];
            bool isSelected = tilemapEditor.selectedPathIndex == index;
            bool isExpanded = index >= 0 && index < pathCardExpandedStates.Count && pathCardExpandedStates[index];

            var card = new VisualElement();
            card.AddToClassList("card");
            if (isSelected) card.AddToClassList("card-selected");
            
            // Border color matches path color or default
            card.style.borderLeftWidth = 4;
            card.style.borderLeftColor = new StyleColor(path != null ? path.color : Color.yellow);

            var headerRow = new VisualElement();
            headerRow.AddToClassList("card-header");
            headerRow.style.flexDirection = FlexDirection.Row;

            var titleGroup = new VisualElement();
            titleGroup.style.flexDirection = FlexDirection.Row;
            titleGroup.style.alignItems = Align.Center;
            titleGroup.style.flexGrow = 1;

            // Title
            var title = new Label($"Path {index + 1}");
            title.AddToClassList("card-title");
            titleGroup.Add(title);

            var typeBadge = new Label(GetPathTypeDisplayName(path));
            typeBadge.style.marginLeft = 8;
            typeBadge.style.paddingLeft = 8;
            typeBadge.style.paddingRight = 8;
            typeBadge.style.paddingTop = 3;
            typeBadge.style.paddingBottom = 3;
            typeBadge.style.borderTopLeftRadius = 999;
            typeBadge.style.borderTopRightRadius = 999;
            typeBadge.style.borderBottomLeftRadius = 999;
            typeBadge.style.borderBottomRightRadius = 999;
            typeBadge.style.backgroundColor = new StyleColor(GetPathTypeBadgeColor(path.pathType));
            typeBadge.style.color = new StyleColor(Color.white);
            typeBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
            typeBadge.style.fontSize = 11;
            titleGroup.Add(typeBadge);

            headerRow.Add(titleGroup);

            // Point count
            var countLabel = new Label($"{path?.points?.Count ?? 0} pts");
            countLabel.style.marginRight = 10;
            countLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            headerRow.Add(countLabel);

            var settingsBtn = new Button(() => {
                SetPathCardExpandedExclusive(index, !isExpanded);
            });
            settingsBtn.text = isExpanded ? "▼ Settings" : "▶ Settings";
            settingsBtn.AddToClassList("btn");
            headerRow.Add(settingsBtn);

            // Select Button
            var selectBtn = new Button(() => {
                tilemapEditor.selectedPathIndex = index;
                tilemapEditor.selectedTileRuleIndex = -1;
                tilemapEditor.selectedGameObjectRuleIndex = -1;
                tilemapEditor.selectedTextureRule = null;
                drawMode = true;
                EditorUtility.SetDirty(tilemapEditor);
                RefreshPathList_UIToolkit(listContainer); // Refresh to update selection state
            });
            selectBtn.text = "Select";
            selectBtn.AddToClassList("btn");
            if (isSelected) selectBtn.AddToClassList("btn-primary");
            headerRow.Add(selectBtn);

            // Visibility toggle
            var visibilityBtn = new Button(() => {
                TogglePathVisibility(index);
                RefreshPathList_UIToolkit(listContainer);
            });
            visibilityBtn.text = GetVisibilityToggleIcon(path.isVisible);
            visibilityBtn.AddToClassList("btn");
            visibilityBtn.AddToClassList("btn-icon");
            headerRow.Add(visibilityBtn);

            // Close Path Button
            var closeBtn = new Button(() => {
                if (path.points != null && path.points.Count > 1)
                {
                   if (path.points[0] != path.points[path.points.Count - 1])
                   {
                        path.points.Add(path.points[0]);
                        EditorUtility.SetDirty(tilemapEditor);
                        RefreshPathList_UIToolkit(listContainer);
                   }
                }
            });
            closeBtn.text = "Close Loop";
            closeBtn.AddToClassList("btn");
            headerRow.Add(closeBtn);

            // Delete Button
            var deleteBtn = new Button(() => {
                // Logic retrieved from original Remove block
                RemovePath(index);
                RefreshPathList_UIToolkit(listContainer);
            });
            deleteBtn.text = "✖";
            deleteBtn.AddToClassList("btn");
            deleteBtn.AddToClassList("btn-icon");
            deleteBtn.AddToClassList("btn-danger");
            headerRow.Add(deleteBtn);

            card.Add(headerRow);

            // Details section - shown when path is selected
            if (isExpanded)
            {
                var detailsContainer = new VisualElement();
                detailsContainer.style.marginTop = 10;
                detailsContainer.style.paddingLeft = 10;
                detailsContainer.style.borderLeftWidth = 2;
                detailsContainer.style.borderLeftColor = new StyleColor(new Color(0.5f, 0.5f, 0.5f, 0.3f));

                // ── Path Type dropdown ──
                var typeField = new EnumField("Type", path.pathType);
                typeField.RegisterValueChangedCallback(evt =>
                {
                    var oldType = path.pathType;
                    path.pathType = (QuickTilemapEditor.PathType)evt.newValue;
                    if (oldType != path.pathType && path.pathType == QuickTilemapEditor.PathType.Bridge)
                        ApplyDefaultBridgeSettings(path);
                    if (path.pathType == QuickTilemapEditor.PathType.Stairs)
                        EnsureDefaultStairRailMaterial(path);
                    if (path.pathType == QuickTilemapEditor.PathType.Track)
                        path.enableTrackMesh = true;
                    else
                        path.enableTrackMesh = false;
                    tilemapEditor.RebuildAllTrackMeshes();
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshPathList_UIToolkit(listContainer);
                });
                detailsContainer.Add(typeField);

                // ── Type-specific settings ──
                switch (path.pathType)
                {
                    case QuickTilemapEditor.PathType.Slope:
                        AddSliderInt(detailsContainer, "Width", Mathf.RoundToInt(path.slopeWidth), 1, 4, v => { path.slopeWidth = v; });
                        AddToggle(detailsContainer, "Smooth Transition", path.smoothTransition, v => { path.smoothTransition = v; });
                        AddToggle(detailsContainer, "Side Skirt", path.slopeSideSkirtEnabled, v => { path.slopeSideSkirtEnabled = v; });
                        if (path.slopeSideSkirtEnabled)
                        {
                            AddSliderFloat(detailsContainer, "Skirt Width", path.slopeSideSkirtWidth, 0f, 0.3f, v => { path.slopeSideSkirtWidth = v; });
                            AddSliderFloat(detailsContainer, "Skirt Height", path.slopeSideSkirtHeight, 0f, 0.5f, v => { path.slopeSideSkirtHeight = v; });
                            AddSliderInt(detailsContainer, "Skirt Segments", path.slopeSideSkirtSegments, 1, 8, v => { path.slopeSideSkirtSegments = v; });
                            AddSliderFloat(detailsContainer, "Skirt UV Scale", path.slopeSideSkirtUVScale, 0.1f, 10f, v => { path.slopeSideSkirtUVScale = v; });
                        }
                        AddMaterialField(detailsContainer, "Surface Material", path.slopeSurfaceMaterial, v => { path.slopeSurfaceMaterial = v; });
                        AddMaterialField(detailsContainer, "Wall Material", path.slopeWallMaterial, v => { path.slopeWallMaterial = v; });
                        break;

                    case QuickTilemapEditor.PathType.Stairs:
                        EnsureDefaultStairRailMaterial(path);
                        AddToggle(detailsContainer, "Auto Steps", path.stairAutoSteps, v => { path.stairAutoSteps = v; });
                        if (path.stairAutoSteps)
                            AddSliderFloat(detailsContainer, "Step Length", path.stairStepDepth, 0.1f, 4f, v => { path.stairStepDepth = v; });
                        else
                            AddSliderInt(detailsContainer, "Steps", path.stairSteps, 2, 16, v => { path.stairSteps = v; });
                        AddSliderInt(detailsContainer, "Width", Mathf.RoundToInt(path.slopeWidth), 1, 4, v => { path.slopeWidth = v; });
                        AddToggle(detailsContainer, "Railings", path.bridgeRailings, v => { path.bridgeRailings = v; });
                        if (path.bridgeRailings)
                        {
                            AddSliderFloat(detailsContainer, "Rail Thickness", path.bridgeRailThickness, 0.02f, 0.5f, v => { path.bridgeRailThickness = v; });
                            AddSliderFloat(detailsContainer, "Rail Spread", path.bridgeRailSpread, -1f, 1f, v => { path.bridgeRailSpread = v; });
                            AddSliderFloat(detailsContainer, "Rail End Extension", path.bridgeRailEndExtension, 0f, 2f, v => { path.bridgeRailEndExtension = v; });
                            AddSliderFloat(detailsContainer, "Rail Y Offset", path.bridgeRailYOffset, -1f, 1f, v => { path.bridgeRailYOffset = v; });
                            AddMaterialField(detailsContainer, "Rail Material", path.stairRailMaterial, v => { path.stairRailMaterial = v; });
                        }
                        AddMaterialField(detailsContainer, "Surface Material", path.slopeSurfaceMaterial, v => { path.slopeSurfaceMaterial = v; });
                        AddMaterialField(detailsContainer, "Wall Material", path.slopeWallMaterial, v => { path.slopeWallMaterial = v; });
                        break;

                    case QuickTilemapEditor.PathType.Bridge:
                        AddSliderInt(detailsContainer, "Width", Mathf.RoundToInt(path.bridgeWidth), 1, 4, v => { path.bridgeWidth = v; });
                        var bridgeProfileField = new EnumField("Profile", path.bridgeProfile);
                        bridgeProfileField.RegisterValueChangedCallback(evt =>
                        {
                            path.bridgeProfile = (QuickTilemapEditor.BridgeProfile)evt.newValue;
                            tilemapEditor.RebuildAllTrackMeshes();
                            EditorUtility.SetDirty(tilemapEditor);
                            RefreshPathList_UIToolkit(listContainer);
                        });
                        detailsContainer.Add(bridgeProfileField);
                        AddSliderFloat(detailsContainer, "Curve", path.bridgeCurve, -5f, 5f, v => { path.bridgeCurve = v; });
                        if (path.bridgeProfile == QuickTilemapEditor.BridgeProfile.Stepped)
                            AddSliderInt(detailsContainer, "Steps", path.bridgeSteps, 2, 16, v => { path.bridgeSteps = v; });
                        AddToggle(detailsContainer, "Railings", path.bridgeRailings, v => { path.bridgeRailings = v; });
                        AddSliderFloat(detailsContainer, "Rail Thickness", path.bridgeRailThickness, 0.02f, 0.5f, v => { path.bridgeRailThickness = v; });
                        AddSliderFloat(detailsContainer, "Rail Spread", path.bridgeRailSpread, -1f, 1f, v => { path.bridgeRailSpread = v; });
                        AddSliderFloat(detailsContainer, "Rail End Extension", path.bridgeRailEndExtension, 0f, 2f, v => { path.bridgeRailEndExtension = v; });
                        AddSliderFloat(detailsContainer, "Rail Y Offset", path.bridgeRailYOffset, -1f, 1f, v => { path.bridgeRailYOffset = v; });
                        AddSliderFloat(detailsContainer, "Rail UV Offset Y", path.bridgeRailUvOffsetY, -1f, 1f, v => { path.bridgeRailUvOffsetY = v; });
                        AddSliderFloat(detailsContainer, "Rail Curve Follow", path.bridgeRailCurveFollow, 0f, 2f, v => { path.bridgeRailCurveFollow = v; });
                        AddMaterialField(detailsContainer, "Bridge Material", path.bridgeMaterial, v => { path.bridgeMaterial = v; });
                        break;

                    case QuickTilemapEditor.PathType.Track:
                        BuildTrackUI(detailsContainer, path, listContainer);
                        break;

                    case QuickTilemapEditor.PathType.Move:
                    default:
                        var infoLabel = new Label("Movement path only.\nAssign GameObjects to follow this path.");
                        infoLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
                        infoLabel.style.whiteSpace = WhiteSpace.Normal;
                        infoLabel.style.marginTop = 4;
                        detailsContainer.Add(infoLabel);
                        break;
                }

                card.Add(detailsContainer);
            }

            return card;
        }

        private string GetPathTypeDisplayName(QuickTilemapEditor.Path path)
        {
            if (path == null)
                return "Move";

            switch (path.pathType)
            {
                case QuickTilemapEditor.PathType.Slope:
                    return "Slope";
                case QuickTilemapEditor.PathType.Stairs:
                    return "Stairs";
                case QuickTilemapEditor.PathType.Bridge:
                    return "Bridge";
                case QuickTilemapEditor.PathType.Track:
                    return "Track";
                case QuickTilemapEditor.PathType.Move:
                default:
                    return "Move";
            }
        }

        private Color GetPathTypeBadgeColor(QuickTilemapEditor.PathType pathType)
        {
            switch (pathType)
            {
                case QuickTilemapEditor.PathType.Slope:
                    return new Color(0.42f, 0.72f, 0.29f, 0.95f);
                case QuickTilemapEditor.PathType.Stairs:
                    return new Color(0.38f, 0.59f, 0.84f, 0.95f);
                case QuickTilemapEditor.PathType.Bridge:
                    return new Color(0.74f, 0.49f, 0.23f, 0.95f);
                case QuickTilemapEditor.PathType.Track:
                    return new Color(0.54f, 0.46f, 0.79f, 0.95f);
                case QuickTilemapEditor.PathType.Move:
                default:
                    return new Color(0.40f, 0.40f, 0.40f, 0.95f);
            }
        }

        // ── UIToolkit helper methods for path type settings ──

        private void AddSliderFloat(VisualElement parent, string label, float value, float min, float max, System.Action<float> onChange)
        {
            var slider = new Slider(label, min, max);
            slider.value = value;
            slider.showInputField = true;
            slider.RegisterValueChangedCallback(evt => { onChange(evt.newValue); tilemapEditor.RebuildAllTrackMeshes(); EditorUtility.SetDirty(tilemapEditor); });
            parent.Add(slider);
        }

        private void AddSliderInt(VisualElement parent, string label, int value, int min, int max, System.Action<int> onChange)
        {
            var slider = new SliderInt(label, min, max);
            slider.value = value;
            slider.showInputField = true;
            slider.RegisterValueChangedCallback(evt => { onChange(evt.newValue); tilemapEditor.RebuildAllTrackMeshes(); EditorUtility.SetDirty(tilemapEditor); });
            parent.Add(slider);
        }

        private void AddToggle(VisualElement parent, string label, bool value, System.Action<bool> onChange)
        {
            var toggle = new Toggle(label);
            toggle.value = value;
            toggle.RegisterValueChangedCallback(evt => { onChange(evt.newValue); tilemapEditor.RebuildAllTrackMeshes(); EditorUtility.SetDirty(tilemapEditor); });
            parent.Add(toggle);
        }

        private void AddMaterialField(VisualElement parent, string label, Material value, System.Action<Material> onChange)
        {
            var field = new ObjectField(label);
            field.objectType = typeof(Material);
            field.value = value;
            field.RegisterValueChangedCallback(evt => { onChange(evt.newValue as Material); tilemapEditor.RebuildAllTrackMeshes(); EditorUtility.SetDirty(tilemapEditor); });
            parent.Add(field);
        }

        private void BuildTrackUI(VisualElement detailsContainer, QuickTilemapEditor.Path path, VisualElement listContainer)
        {
            var trackMeshToggle = new Toggle("Enable Track Mesh");
            trackMeshToggle.value = path.enableTrackMesh;
            trackMeshToggle.RegisterValueChangedCallback(evt =>
            {
                path.enableTrackMesh = evt.newValue;
                SyncTrackPoints(path);
                EditorUtility.SetDirty(tilemapEditor);
                RefreshPathList_UIToolkit(listContainer);
            });
            detailsContainer.Add(trackMeshToggle);

            if (path.enableTrackMesh)
            {
                var trackWidthField = new FloatField("Default Width");
                trackWidthField.value = path.trackWidth;
                trackWidthField.RegisterValueChangedCallback(evt => { path.trackWidth = evt.newValue; EditorUtility.SetDirty(tilemapEditor); });
                detailsContainer.Add(trackWidthField);

                var subdivisionsField = new IntegerField("Subdivisions");
                subdivisionsField.value = path.trackSubdivisions;
                subdivisionsField.RegisterValueChangedCallback(evt => { path.trackSubdivisions = Mathf.Clamp(evt.newValue, 1, 20); EditorUtility.SetDirty(tilemapEditor); });
                detailsContainer.Add(subdivisionsField);

                var materialField = new ObjectField("Track Material");
                materialField.objectType = typeof(Material);
                materialField.value = path.trackMaterial;
                materialField.RegisterValueChangedCallback(evt => { path.trackMaterial = evt.newValue as Material; EditorUtility.SetDirty(tilemapEditor); });
                detailsContainer.Add(materialField);

                var uvTilingField = new FloatField("UV Tiling Y");
                uvTilingField.value = path.trackUVTilingY;
                uvTilingField.RegisterValueChangedCallback(evt => { path.trackUVTilingY = evt.newValue; EditorUtility.SetDirty(tilemapEditor); });
                detailsContainer.Add(uvTilingField);

                var pointsFoldout = new Foldout();
                pointsFoldout.text = $"Track Points ({path.trackPoints.Count})";
                pointsFoldout.value = false;

                for (int ptIdx = 0; ptIdx < path.trackPoints.Count; ptIdx++)
                {
                    int capturedIdx = ptIdx;
                    var trackPoint = path.trackPoints[ptIdx];
                    var pointContainer = new VisualElement();
                    pointContainer.style.marginTop = 5;
                    pointContainer.style.marginLeft = 15;

                    var pointLabel = new Label($"Point {ptIdx}");
                    pointLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    pointContainer.Add(pointLabel);

                    var snapToggle = new Toggle("Snap to Ground");
                    snapToggle.value = trackPoint.snapToGround;
                    snapToggle.RegisterValueChangedCallback(evt => { path.trackPoints[capturedIdx].snapToGround = evt.newValue; EditorUtility.SetDirty(tilemapEditor); });
                    pointContainer.Add(snapToggle);

                    var rotationField = new FloatField("Rotation");
                    rotationField.value = trackPoint.rotation;
                    rotationField.RegisterValueChangedCallback(evt => { path.trackPoints[capturedIdx].rotation = evt.newValue; EditorUtility.SetDirty(tilemapEditor); });
                    pointContainer.Add(rotationField);

                    var widthField = new FloatField("Width");
                    widthField.value = trackPoint.width;
                    widthField.RegisterValueChangedCallback(evt => { path.trackPoints[capturedIdx].width = evt.newValue; EditorUtility.SetDirty(tilemapEditor); });
                    pointContainer.Add(widthField);

                    pointsFoldout.Add(pointContainer);
                }
                detailsContainer.Add(pointsFoldout);
            }
        }

        private void AddPath()
        {
            if (tilemapEditor.paths == null)
                tilemapEditor.paths = new List<QuickTilemapEditor.Path>();

            var newPath = new QuickTilemapEditor.Path();
            newPath.points = new List<Vector3Int>();
            newPath.trackPoints = new List<QuickTilemapEditor.TrackPoint>();
            newPath.color = Color.yellow;
            tilemapEditor.paths.Add(newPath);
            SetPathCardExpandedExclusive(tilemapEditor.paths.Count - 1, true, false);
            EditorUtility.SetDirty(tilemapEditor);
        }

        private void TogglePathVisibility(int index)
        {
            if (tilemapEditor.paths == null || index < 0 || index >= tilemapEditor.paths.Count)
                return;

            Undo.RecordObject(tilemapEditor, "Toggle Path Visibility");

            var path = tilemapEditor.paths[index];
            path.isVisible = !path.isVisible;
            tilemapEditor.ApplyPathGeneratedObjectVisibility(index);
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        private void RemovePath(int index)
        {
            if (tilemapEditor.paths == null || index < 0 || index >= tilemapEditor.paths.Count)
                return;

            Undo.RecordObject(tilemapEditor, "Remove Path");

            // First clean up any path followers that reference this path
            if (tilemapEditor.paths[index].points != null && tilemapEditor.paths[index].points.Count > 0)
            {
                foreach (var obj in tilemapEditor.instantiatedGameObjects)
                {
                    if (obj == null) continue;

                    PathFollower pf = obj.GetComponent<PathFollower>();
                    if (pf != null && pf.GetPathIndex() == index)
                    {
                        pf.StopMoving();
                        pf.SetPathIndex(-1);
                        EditorUtility.SetDirty(obj);
                    }
                }
            }

            tilemapEditor.paths.RemoveAt(index);
            if (tilemapEditor.selectedPathIndex == index)
                tilemapEditor.selectedPathIndex = -1;
            else if (tilemapEditor.selectedPathIndex > index)
                tilemapEditor.selectedPathIndex--;

            // Update path references in placed objects
            foreach (var placedObj in tilemapEditor.placedObjects)
            {
                if (placedObj.pathIndex == index + 1)
                {
                    placedObj.pathIndex = -1;
                }
                else if (placedObj.pathIndex > index + 1)
                {
                    placedObj.pathIndex--;
                }
            }
            
            // Destroy visual container object if exists
            var container = tilemapEditor.GetPrefabContainer();
            Transform existingPath = container?.Find($"Path_{index + 1}");
            if (existingPath)
            {
                Undo.RegisterCompleteObjectUndo(existingPath.gameObject, "Remove Path");
                Undo.DestroyObjectImmediate(existingPath.gameObject);
            }

            tilemapEditor.RefreshAllPathFollowers();
            tilemapEditor.RebuildAllTrackMeshes();
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        #endregion
    }
}
