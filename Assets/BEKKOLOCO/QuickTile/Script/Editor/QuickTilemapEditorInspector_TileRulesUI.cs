using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using UnityEditorInternal;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEditor.SceneManagement;
using Bekkoloco.DOTS;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
using System;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        private class TileRuleUIState
        {
            public bool expanded = false;
            public bool placementExpanded = true;
            public bool meshModeExpanded = false;
            public bool proceduralSettingsExpanded = false;
            public bool animationExpanded = false;
            public int selectedTab = 0; // 0=Placement, 1=Deformers
        }

        private void EnsureTileRuleUIStateCount()
        {
            if (tilemapEditor?.tileRules == null) return;

            while (tileRuleUIStates.Count < tilemapEditor.tileRules.Count)
                tileRuleUIStates.Add(new TileRuleUIState());

            while (tileRuleUIStates.Count > tilemapEditor.tileRules.Count)
                tileRuleUIStates.RemoveAt(tileRuleUIStates.Count - 1);
        }

        private void SelectTileRuleExclusive(int index, bool refreshUIToolkit = true)
        {
            if (tilemapEditor?.tileRules == null || index < 0 || index >= tilemapEditor.tileRules.Count)
                return;

            EnsureTileRuleUIStateCount();

            tilemapEditor.selectedTileRuleIndex = index;
            tilemapEditor.selectedGameObjectRuleIndex = -1;
            tilemapEditor.selectedPathIndex = -1;
            tilemapEditor.selectedTextureRule = null;
            tilemapEditor.selectedTextureRuleIndex = -1;

            EditorUtility.SetDirty(tilemapEditor);

            if (refreshUIToolkit && tileRulesUIToolkitContainer != null)
                RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);

            Repaint();
        }

        private void SetTileRuleSettingsExpandedExclusive(int index, bool expanded, bool refreshUIToolkit = true)
        {
            if (tilemapEditor?.tileRules == null || index < 0 || index >= tilemapEditor.tileRules.Count)
                return;

            EnsureTileRuleUIStateCount();

            for (int i = 0; i < tileRuleUIStates.Count; i++)
                tileRuleUIStates[i].expanded = false;

            if (expanded)
            {
                tileRuleUIStates[index].expanded = true;
                SelectTileRuleExclusive(index, false);
            }
            else
            {
                EditorUtility.SetDirty(tilemapEditor);
            }

            if (refreshUIToolkit && tileRulesUIToolkitContainer != null)
                RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);

            Repaint();
        }

        private static bool BottomCapUsesWallMaterial(QuickTilemapEditor.TileRule rule, Material previousWallMaterial)
        {
            if (rule == null) return false;
            return rule.proceduralBottomMaterial == null || rule.proceduralBottomMaterial == previousWallMaterial;
        }

        private static Material GetCurrentSkirtMaterial(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null)
                return null;

            if (rule.proceduralCeilingMaterial != null)
                return rule.proceduralCeilingMaterial;

            if (rule.proceduralFloorMaterial != null)
                return rule.proceduralFloorMaterial;

            return rule.proceduralWallMaterial;
        }

        private void MoveTileRule(int index, int direction)
        {
            int targetIndex = index + direction;
            if (tilemapEditor?.tileRules == null) return;
            if (targetIndex < 0 || targetIndex >= tilemapEditor.tileRules.Count) return;

            (tilemapEditor.tileRules[targetIndex], tilemapEditor.tileRules[index]) =
                (tilemapEditor.tileRules[index], tilemapEditor.tileRules[targetIndex]);

            (tileRuleUIStates[targetIndex], tileRuleUIStates[index]) =
                (tileRuleUIStates[index], tileRuleUIStates[targetIndex]);

            if (tilemapEditor.selectedTileRuleIndex == index)
                tilemapEditor.selectedTileRuleIndex = targetIndex;
            else if (tilemapEditor.selectedTileRuleIndex == targetIndex)
                tilemapEditor.selectedTileRuleIndex = index;

            EditorUtility.SetDirty(tilemapEditor);
        }

        private bool RemoveTileRule(int index)
        {
            if (tilemapEditor?.tileRules == null || index < 0 || index >= tilemapEditor.tileRules.Count)
                return false;

            var tileRule = tilemapEditor.tileRules[index];

            if (EditorUtility.DisplayDialog(
                "Delete Tile Rule",
                "Are you sure you want to delete this tile rule? This will remove the associated tilemap if it exists.",
                "Yes", "Cancel"))
            {
                UnlinkAllHandlesOfRule(tileRule);

                if (tileRule.deformerObjects != null)
                {
                    foreach (var h in tileRule.deformerObjects)
                        if (h != null) Undo.DestroyObjectImmediate(h);
                    tileRule.deformerObjects.Clear();
                }

                // Destroy the custom tilemap (and its procedural renderer child)
                if (tileRule.useCustomTilemap && tileRule.customTargetTilemap != null)
                {
                    // Also remove from heightTilemaps dictionary
                    float heightKey = tileRule.customTargetTilemap.transform.localPosition.y;
                    if (tilemapEditor.heightTilemaps.ContainsKey(heightKey) &&
                        tilemapEditor.heightTilemaps[heightKey] == tileRule.customTargetTilemap)
                        tilemapEditor.heightTilemaps.Remove(heightKey);

                    Undo.DestroyObjectImmediate(tileRule.customTargetTilemap.gameObject);
                }
                else if (!tileRule.useCustomTilemap && tileRule.tile != null && tilemapEditor.targetTilemap != null)
                {
                    // Rule uses shared tilemap — erase its tiles
                    var tm = tilemapEditor.targetTilemap;
                    BoundsInt bounds = tm.cellBounds;
                    foreach (var pos in bounds.allPositionsWithin)
                    {
                        TileBase t = tm.GetTile(pos);
                        if (t != null && t.name == tileRule.tile.name)
                            tm.SetTile(pos, null);
                    }
                }

                if (tilemapEditor.selectedTileRuleIndex == index)
                    tilemapEditor.selectedTileRuleIndex = -1;

                bool removedDigLayer = tileRule.isDigLayer;
                tilemapEditor.tileRules.RemoveAt(index);
                if (removedDigLayer)
                    tilemapEditor.SyncAllProceduralRenderers();
                EnsureTileRuleUIStateCount();
                return true;
            }

            return false;
        }

        private void AddTileRule()
        {
            if (tilemapEditor == null) return;

            tilemapEditor.selectedGameObjectRuleIndex = -1;
            tilemapEditor.selectedPathIndex = -1;
            tilemapEditor.selectedTextureRule = null;

            var newRule = new QuickTilemapEditor.TileRule
            {
                useCustomTilemap = true,
                yOffset = tilemapEditor.tileRules.Count > 0
                    ? tilemapEditor.tileRules[tilemapEditor.tileRules.Count - 1].yOffset + 0.25f
                    : 0f,
                ruleName = "New Rule",
                color = Random.ColorHSV(0f, 1f, 0.3f, 0.5f, 0.8f, 1f)
            };

            if (tilemapEditor.tileRules.Count > 0)
            {
                var prevRule = tilemapEditor.tileRules[tilemapEditor.tileRules.Count - 1];
                if (prevRule.tile != null)
                    newRule.tile = prevRule.tile;
                else if (tilemapEditor.activeTile != null)
                    newRule.tile = tilemapEditor.activeTile;
            }
            else if (tilemapEditor.activeTile != null)
            {
                newRule.tile = tilemapEditor.activeTile;
            }
            else
            {
                string[] guids = AssetDatabase.FindAssets("t:TileBase");
                if (guids.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    TileBase defaultTile = AssetDatabase.LoadAssetAtPath<TileBase>(path);
                    newRule.tile = defaultTile;
                    tilemapEditor.activeTile = defaultTile;
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Tile Rule Alert",
                        "No tile available to assign as default. Please assign a tile to the tile layer.",
                        "OK");
                    return;
                }
            }

            tilemapEditor.ApplyDefaultProceduralMaterials(newRule);
            newRule.customTargetTilemap = tilemapEditor.CreateTilemapForRule(newRule);
            tilemapEditor.tileRules.Add(newRule);
            SetTileRuleSettingsExpandedExclusive(tilemapEditor.tileRules.Count - 1, true, false);
            tilemapEditor.gridSize = new Vector3Int(32, 32, 1);

            if (tilemapEditor.tileRules.Count > 1)
            {
                var previousRule = tilemapEditor.tileRules[tilemapEditor.tileRules.Count - 2];
                if (previousRule.customTargetTilemap != null && newRule.customTargetTilemap != null)
                    newRule.customTargetTilemap.size = previousRule.customTargetTilemap.size;
            }

            tilemapEditor.needsRefreshPreview = true;
            EnsureTileRuleUIStateCount();
            EditorUtility.SetDirty(tilemapEditor);
        }

        private int GetCurrentPresetIndex(Vector3Int currentSize)
        {
            if (tilemapEditor.useCustomSize) return 4;
            if (currentSize.x == 7 && currentSize.y == 7) return 0;
            if (currentSize.x == 16 && currentSize.y == 16) return 1;
            if (currentSize.x == 32 && currentSize.y == 32) return 2;
            if (currentSize.x == 64 && currentSize.y == 64) return 3;
            tilemapEditor.useCustomSize = true;
            customWidth = currentSize.x;
            customHeight = currentSize.y;
            return 4;
        }

        private void DrawDeformerObjectsUI(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null)
                return;

            DrawBottomUI(rule);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Move", EditorStyles.boldLabel);
            rule.enableMove = EditorGUILayout.Toggle("Enable Move", rule.enableMove);

            using (new EditorGUI.DisabledScope(!rule.enableMove))
            {
                EditorGUI.indentLevel++;
                rule.moveOffset = EditorGUILayout.Vector3Field("Move Offset", rule.moveOffset);
                EditorGUILayout.HelpBox("The move offset is added to the tile's base position when the animation plays.", MessageType.Info);
                float pause = EditorGUILayout.FloatField("Pause (s)", rule.movePause);

                rule.movePause = Mathf.Max(0f, pause);

                EditorGUILayout.Space(2f);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Preview Offset Position"))
                {
                    tilemapEditor.PreviewMoveOffsetPosition(rule);
                }

                if (GUILayout.Button("Preview Animation"))
                {
                    tilemapEditor.PreviewMoveAnimation(rule);
                }
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Deformer Objects", EditorStyles.boldLabel);

            // Sécurité: toujours avoir une liste
            if (rule.deformerObjects == null) rule.deformerObjects = new List<GameObject>();

            // Chaque entrée = GameObject + Select + Remove
            for (int i = 0; i < rule.deformerObjects.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();

                rule.deformerObjects[i] = (GameObject)EditorGUILayout.ObjectField(
                    rule.deformerObjects[i], typeof(GameObject), true, GUILayout.ExpandWidth(true));

                using (new EditorGUI.DisabledScope(rule.deformerObjects[i] == null))
                {
                    if (GUILayout.Button("Select", GUILayout.Width(70)))
                    {
                        var go = rule.deformerObjects[i];
                        if (go != null)
                        {
                            Selection.activeGameObject = go;
                            EditorGUIUtility.PingObject(go);
                        }
                    }
                }

                if (GUILayout.Button("Remove", GUILayout.Width(80)))
                {
                    var go = rule.deformerObjects[i];
                    Undo.RecordObject(tilemapEditor, "Remove Deformer Object");
                    if (go != null)
                    {
                        UnlinkHandleFromRadialHillDeformers(rule, go); // ⬅️ important
                        Undo.DestroyObjectImmediate(go);
                    }
                    rule.deformerObjects.RemoveAt(i);
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshProceduralMeshesForLayerChange(rule, rule.isDigLayer);
                    Repaint();
                    EditorGUILayout.EndHorizontal();
                    break;
                }


                EditorGUILayout.EndHorizontal();
            }

            // Ligne d’actions (Add)
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Add Deformer", GUILayout.Width(110)))
            {
                CreateDeformerCube(rule);
            }
            EditorGUILayout.EndHorizontal();

            // ── Deformer Settings (expose key RadialHillDeformer params) ──
            var deformer = FindDeformerForRule(rule);
            if (deformer != null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Deformer Settings", EditorStyles.boldLabel);

                EditorGUI.BeginChangeCheck();

                deformer.shape = (DOTSDeformShape)EditorGUILayout.EnumPopup("Shape", deformer.shape);
                deformer.radius = EditorGUILayout.FloatField("Radius", deformer.radius);
                deformer.falloff = (DOTSFalloff)EditorGUILayout.EnumPopup("Falloff", deformer.falloff);
                if (deformer.falloff == DOTSFalloff.Gaussian)
                    deformer.gaussianSharpness = EditorGUILayout.Slider("Gaussian Sharpness", deformer.gaussianSharpness, 0.1f, 4f);
                deformer.heightPerUnitY = EditorGUILayout.FloatField("Height Per Unit Y", deformer.heightPerUnitY);
                deformer.yDeformRatio = EditorGUILayout.FloatField("Y Deform Ratio", deformer.yDeformRatio);
                deformer.invertDirection = EditorGUILayout.Toggle("Invert Direction", deformer.invertDirection);

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(deformer, "Change Deformer Settings");
                    EditorUtility.SetDirty(deformer);
                    deformer.ForceUpdate();
                }
            }
        }

        /// <summary>
        /// Finds the RadialHillDeformer associated with a rule's tilemap.
        /// </summary>
        private RadialHillDeformer FindDeformerForRule(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null) return null;
            Transform root = null;
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                root = rule.customTargetTilemap.transform;
            else if (tilemapEditor != null && tilemapEditor.targetTilemap != null)
                root = tilemapEditor.targetTilemap.transform;
            if (root == null) return null;
            return root.GetComponentInChildren<RadialHillDeformer>(true);
        }

        // Crée un handle "UI_Arrow" enfant de la tilemap de la règle
        private void CreateDeformerCube(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null)
                return;

            // Parent = tilemap custom si dispo, sinon tilemap de base
            Transform parent = null;
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                parent = rule.customTargetTilemap.transform;
            else if (tilemapEditor != null && tilemapEditor.targetTilemap != null)
                parent = tilemapEditor.targetTilemap.transform;

            if (parent == null)
            {
                EditorUtility.DisplayDialog("No Tilemap",
                    "Aucune tilemap parente trouvée pour attacher le handle.", "OK");
                return;
            }

            // Position de départ: centre de la cellule (0,0) + yOffset visuel
            Vector3 startWorld = parent.position;
            var parentMap = parent.GetComponent<Tilemap>();
            if (parentMap != null)
                startWorld = parentMap.GetCellCenterWorld(Vector3Int.zero);

            startWorld.y = parent.position.y + rule.yOffset;

            // Création du handle avec le prefab UI_Arrow
            GameObject handle = null;

            // Essayer de charger le prefab UI_Arrow
            GameObject arrowPrefab = Resources.Load<GameObject>("UI_Arrow");
            if (arrowPrefab != null)
            {
                handle = PrefabUtility.InstantiatePrefab(arrowPrefab) as GameObject;
                Undo.RegisterCreatedObjectUndo(handle, "Add Deformer Arrow");
                handle.name = "ArrowHandle";
            }
            else
            {
                // Fallback: utiliser un cube si le prefab n'est pas trouvé
                Debug.LogWarning("UI_Arrow prefab not found in Resources folder. Using cube fallback.");
                handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Undo.RegisterCreatedObjectUndo(handle, "Add Deformer Cube");
                handle.name = "CubeHandle";
            }

            // Configuration du handle
            handle.transform.SetPositionAndRotation(startWorld, Quaternion.identity);
            handle.transform.SetParent(parent, true);
            handle.transform.localScale = Vector3.one; // ✅ Scale de 1 au lieu de 0.25f

            // Optionnel: retirer le collider si tu veux juste un gizmo
            var col = handle.GetComponent<Collider>();
            if (col) Object.DestroyImmediate(col);

            // Ajout à la liste locale de la règle
            if (rule.deformerObjects == null)
                rule.deformerObjects = new List<GameObject>();
            rule.deformerObjects.Add(handle);

            // 🔗 Auto-attach RadialHillDeformer if missing on the tilemap
            EnsureRadialHillDeformerOnTilemap(rule);

            // 🔗 SYNC immédiate : ajoute ce handle dans additionalHandles
            // de tous les RadialHillDeformer présents sous la tilemap de la règle
            LinkHandleToRadialHillDeformers(rule, handle);

            EditorUtility.SetDirty(tilemapEditor);
            RefreshProceduralMeshesForLayerChange(rule, rule.isDigLayer);
            Selection.activeGameObject = handle;
            EditorGUIUtility.PingObject(handle);
        }


        private void UnlinkHandleFromRadialHillDeformers(QuickTilemapEditor.TileRule rule, GameObject handle)
        {
            if (rule == null || handle == null) return;

            Transform root = null;
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                root = rule.customTargetTilemap.transform;
            else if (tilemapEditor != null && tilemapEditor.targetTilemap != null)
                root = tilemapEditor.targetTilemap.transform;

            if (root == null) return;

            var deformers = root.GetComponentsInChildren<RadialHillDeformer>(true);
            var t = handle.transform;
            foreach (var deformer in deformers)
            {
                if (deformer?.additionalHandlesList == null) continue;
                if (deformer.additionalHandlesList.Contains(t))
                {
                    Undo.RecordObject(deformer, "Remove Additional Handle");
                    deformer.additionalHandlesList.Remove(t);
                    EditorUtility.SetDirty(deformer);
                }
            }
        }


        private void UnlinkAllHandlesOfRule(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null || rule.deformerObjects == null) return;
            foreach (var h in rule.deformerObjects)
                UnlinkHandleFromRadialHillDeformers(rule, h);
        }

        // ⬇️ REPLACE your existing method with this
        private void LinkHandleToRadialHillDeformers(QuickTilemapEditor.TileRule rule, GameObject handle)
        {
            if (rule == null || handle == null) return;

            Transform root = null;
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                root = rule.customTargetTilemap.transform;
            else if (tilemapEditor != null && tilemapEditor.targetTilemap != null)
                root = tilemapEditor.targetTilemap.transform;

            if (root == null) return;

            var deformers = root.GetComponentsInChildren<RadialHillDeformer>(true);
            var t = handle.transform;
            foreach (var deformer in deformers)
            {
                // IMPORTANT: additionalHandles is List<Transform>
                if (deformer.additionalHandlesList == null)
                    deformer.additionalHandlesList = new List<Transform>();

                if (!deformer.additionalHandlesList.Contains(t))
                {
                    Undo.RecordObject(deformer, "Add Additional Handle");
                    deformer.additionalHandlesList.Add(t);
                    EditorUtility.SetDirty(deformer);
                }
            }
        }


        /// <summary>
        /// Ensures a RadialHillDeformer component exists on the tilemap of this rule.
        /// If missing, automatically adds one so deformer handles can work immediately.
        /// </summary>
        private void EnsureRadialHillDeformerOnTilemap(QuickTilemapEditor.TileRule rule)
        {
            if (rule == null) return;

            Transform root = null;
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                root = rule.customTargetTilemap.transform;
            else if (tilemapEditor != null && tilemapEditor.targetTilemap != null)
                root = tilemapEditor.targetTilemap.transform;

            if (root == null) return;

            // Check if there's already a RadialHillDeformer anywhere under this root
            var existing = root.GetComponentInChildren<RadialHillDeformer>(true);
            if (existing != null) return; // already present

            // Auto-add RadialHillDeformer on the tilemap root
            Undo.RecordObject(root.gameObject, "Auto-add RadialHillDeformer");
            var deformer = Undo.AddComponent<RadialHillDeformer>(root.gameObject);

            // Sensible defaults
            deformer.runtimeStaticMode = true;
            deformer.runtimeInitDelay = 0.1f;
            deformer.linkRadiusToScale = true;
            deformer.radiusLinkMode = DOTSRadiusLinkMode.MultiplyByScale;
            deformer.radius = 5f;
            deformer.falloff = DOTSFalloff.SmoothStep;
            deformer.heightPerUnitY = 0.7f;
            deformer.useHandleZero = true;
            deformer.useYMin = true;
            deformer.yMin = -0.3f;
            deformer.yFeather = -0.16f;
            deformer.compensateLocalScaleY = true;
            deformer.yDeformRatio = 1.11f;
            deformer.clampWorldMinY = true;
            deformer.worldMinY = rule.yOffset - rule.sizeY;
            deformer.recalcNormals = true;
            deformer.updateMeshCollider = true;

            EditorUtility.SetDirty(deformer);
            Debug.Log($"[QuickTile] Auto-attached RadialHillDeformer on '{root.name}' for rule deformer handles.");
        }

        private void DrawTileRulesSection()
        {
            EditorGUILayout.LabelField("Tilemap Rules", EditorStyles.boldLabel);

            EnsureTileRuleUIStateCount();

            if (tilemapEditor.tileRules == null || tilemapEditor.tileRules.Count == 0)
            {
                EditorGUILayout.BeginVertical();
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                GUIStyle centeredBigLabel = new GUIStyle(EditorStyles.boldLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 20,
                    padding = new RectOffset(-50, -50, -10, -10)
                };

                EditorGUILayout.LabelField("Please add a Tile Rule \n ↓", centeredBigLabel);

                GUILayout.FlexibleSpace();

                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
            }

            else
            {
                for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
                {
                    var tileRule = tilemapEditor.tileRules[i];
                    var uiState = tileRuleUIStates[i];

                    bool isSelectedTileRule = tilemapEditor.selectedTileRuleIndex == i;
                    Color previousBackgroundColor = GUI.backgroundColor;
                    if (isSelectedTileRule)
                        GUI.backgroundColor = new Color(0.8f, 1f, 0.8f, 1f);

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.BeginHorizontal();

                    string title = $"{i + 1}. {(tileRule.tile != null ? tileRule.tile.name : "Tile Rule")}";
                    bool wasExpanded = uiState.expanded;
                    uiState.expanded = EditorGUILayout.Foldout(uiState.expanded, title, true);
                    if (!wasExpanded && uiState.expanded)
                        SelectTileRuleExclusive(i, false);

                    GUILayout.FlexibleSpace();

                    EditorGUILayout.LabelField("Color", GUILayout.Width(42));
                    var newHeaderColor = EditorGUILayout.ColorField(tileRule.color, GUILayout.Width(80));
                    if (newHeaderColor != tileRule.color)
                        tileRule.color = newHeaderColor;

                    if (GUILayout.Button(GetVisibilityToggleIcon(tileRule.isVisible), GUILayout.Width(28)))
                    {
                        tileRule.isVisible = !tileRule.isVisible;
                        ToggleTilemapVisibility(tileRule);
                        EditorUtility.SetDirty(tilemapEditor);
                        SceneView.RepaintAll();
                    }

                    /*
                    using (new EditorGUI.DisabledScope(i == 0))
                    {
                        if (GUILayout.Button("▲", GUILayout.Width(22)))
                        {
                            GUI.backgroundColor = previousBackgroundColor;
                            MoveTileRule(i, -1);
                            continue;
                        }
                    }

                    using (new EditorGUI.DisabledScope(i == tilemapEditor.tileRules.Count - 1))
                    {
                        if (GUILayout.Button("▼", GUILayout.Width(22)))
                        {
                            GUI.backgroundColor = previousBackgroundColor;
                            MoveTileRule(i, 1);
                            continue;
                        }
                    }
                    */

                    if (GUILayout.Button("Select", GUILayout.Width(70)))
                    {
                        SelectTileRuleExclusive(i, false);
                    }

                    if (GUILayout.Button("✖", GUILayout.Width(24)))
                    {
                        if (RemoveTileRule(i))
                        {
                            GUI.backgroundColor = previousBackgroundColor;
                            break;
                        }
                    }

                    EditorGUILayout.EndHorizontal();

                    if (uiState.expanded)
                    {
                        EditorGUI.indentLevel++;
                        DrawTileRuleContent(tileRule, uiState);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.EndVertical();
                    GUI.backgroundColor = previousBackgroundColor;
                    EditorGUILayout.Space(4);
                }
            }
        }

        private void DrawTileRuleContent(QuickTilemapEditor.TileRule tileRule, TileRuleUIState uiState)
        {
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70f;

            uiState.placementExpanded = EditorGUILayout.Foldout(uiState.placementExpanded, "Placement", true);
            if (uiState.placementExpanded)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                if (tileRule.meshMode != QuickTilemapEditor.MeshMode.Procedural)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Tile", GUILayout.Width(50));
                    tileRule.tile = (TileBase)EditorGUILayout.ObjectField(tileRule.tile, typeof(TileBase), false, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Update", GUILayout.Width(60)))
                    {
                        tilemapEditor.UpdateTileRuleOnScene(tileRule);
                    }

                    GUI.color = Color.cyan;
                    if (GUILayout.Button("📦 FBX", GUILayout.Width(50)))
                    {
                        ExportTileRuleToFBX(tileRule);
                    }
                    GUI.color = Color.white;
                    EditorGUILayout.EndHorizontal();
                }

                /*
                EditorGUILayout.BeginHorizontal();
                bool previousUseCustomTilemap = tileRule.useCustomTilemap;
                Tilemap previousCustomTilemap = tileRule.customTargetTilemap;
                tileRule.useCustomTilemap = EditorGUILayout.ToggleLeft("Custom Map", tileRule.useCustomTilemap, GUILayout.Width(100));
                using (new EditorGUI.DisabledScope(!tileRule.useCustomTilemap))
                {
                    tileRule.customTargetTilemap = (Tilemap)EditorGUILayout.ObjectField(tileRule.customTargetTilemap, typeof(Tilemap), true, GUILayout.ExpandWidth(true));
                }
                EditorGUILayout.EndHorizontal();

                if (previousUseCustomTilemap != tileRule.useCustomTilemap || previousCustomTilemap != tileRule.customTargetTilemap)
                {
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshProceduralMeshesForLayerChange(tileRule, tileRule.isDigLayer);
                }

                EditorGUILayout.BeginHorizontal();
                bool previousDigLayer = tileRule.isDigLayer;
                tileRule.isDigLayer = EditorGUILayout.ToggleLeft("Dig Layer", tileRule.isDigLayer, GUILayout.Width(100));
                if (tileRule.isDigLayer)
                    EditorGUILayout.LabelField("This layer subtracts from overlapping procedural layers.", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                if (previousDigLayer != tileRule.isDigLayer)
                {
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshProceduralMeshesForLayerChange(tileRule, true);
                }

                EditorGUILayout.BeginHorizontal();
                string digStateLabel = tileRule.isDiggable ? "Diggable" : "Undiggable";
                if (GUILayout.Button(digStateLabel, GUILayout.Width(100)))
                {
                    tileRule.isDiggable = !tileRule.isDiggable;
                    tileRule.isUndiggable = !tileRule.isDiggable;
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshProceduralMeshesForLayerChange(tileRule, tileRule.isDigLayer);
                }

                EditorGUILayout.LabelField(
                    tileRule.isDiggable ? "Receives Dig Layers." : "Ignores Dig Layers.",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                if (tileRule.isDigLayer && (!tileRule.useCustomTilemap || tileRule.customTargetTilemap == null))
                {
                    EditorGUILayout.HelpBox("Dig Layer works on dedicated tilemap layers. Enable Custom Map and assign a tilemap layer to make this rule carve other procedural layers.", MessageType.Info);
                }
                */

                EditorGUILayout.BeginHorizontal();
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Y Offset", GUILayout.Width(60));
                    if (GUILayout.Button("-", GUILayout.Width(25)))
                    {
                        tileRule.yOffset -= 0.25f;
                        UpdateTilemapYOffset(tileRule);
                        RefreshProceduralMeshesForLayerChange(tileRule);
                    }
                    float newYOffset = EditorGUILayout.FloatField(tileRule.yOffset, GUILayout.Width(60));
                    if (!Mathf.Approximately(newYOffset, tileRule.yOffset))
                    {
                        tileRule.yOffset = newYOffset;
                        UpdateTilemapYOffset(tileRule);
                        RefreshProceduralMeshesForLayerChange(tileRule);
                    }
                    if (GUILayout.Button("+", GUILayout.Width(25)))
                    {
                        tileRule.yOffset += 0.25f;
                        UpdateTilemapYOffset(tileRule);
                        RefreshProceduralMeshesForLayerChange(tileRule);
                    }
                    EditorGUILayout.EndHorizontal();
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Size Y", GUILayout.Width(50));

                    using (new EditorGUI.DisabledScope(tileRule.fixBase))
                    {
                        if (GUILayout.Button("-", GUILayout.Width(25)))
                        {
                            tileRule.sizeY = Mathf.Max(0f, tileRule.sizeY - 0.5f);
                            ApplySkirtVisuals(tileRule);
                            RefreshProceduralMeshesForLayerChange(tileRule);
                        }

                        float newSize = EditorGUILayout.FloatField(tileRule.sizeY, GUILayout.Width(60));
                        newSize = Mathf.Clamp(Mathf.Round(newSize * 2f) / 2f, 0f, 100f);
                        if (!Mathf.Approximately(newSize, tileRule.sizeY))
                        {
                            tileRule.sizeY = newSize;
                            ApplySkirtVisuals(tileRule);
                            RefreshProceduralMeshesForLayerChange(tileRule);
                        }

                        if (GUILayout.Button("+", GUILayout.Width(25)))
                        {
                            tileRule.sizeY = Mathf.Min(100f, tileRule.sizeY + 0.5f);
                            ApplySkirtVisuals(tileRule);
                            RefreshProceduralMeshesForLayerChange(tileRule);
                        }
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(tileRule.fixBase ? "Fixed base: ON" : "Fixed base: OFF", GUILayout.Width(150)))
                {
                    tileRule.fixBase = !tileRule.fixBase;

                    if (tileRule.customTargetTilemap != null)
                    {
                        var skirts = tileRule.customTargetTilemap.GetComponentsInChildren<SkirtManager>(true);
                        foreach (var skirt in skirts)
                        {
                            skirt.wallCount = tileRule.fixBase
                                ? Mathf.FloorToInt(tileRule.yOffset / skirt.WallStep)
                                : Mathf.RoundToInt(tileRule.roundedCorner);

                            skirt.scaleValue = tileRule.fixBase
                                ? tileRule.yOffset * 10f
                                : tileRule.roundedCorner;

                            skirt.ApplyVisuals();
                            EditorUtility.SetDirty(skirt);
                        }
                    }

                    SceneView.RepaintAll();
                    Repaint();
                    RefreshProceduralMeshesForLayerChange(tileRule);
                }

                if (tileRule.fixBase)
                    EditorGUILayout.LabelField("Size Y disabled when Fixed base is ON.", EditorStyles.miniLabel);

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }

            // ── Mesh Mode (Custom / Procedural) ──
            uiState.meshModeExpanded = EditorGUILayout.Foldout(uiState.meshModeExpanded, "Mesh Mode", true);
            if (uiState.meshModeExpanded)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawMeshModeUI(tileRule, uiState);
                EditorGUILayout.EndVertical();
            }

            uiState.animationExpanded = EditorGUILayout.Foldout(uiState.animationExpanded, "Skirt, Move & Deformers", true);
            if (uiState.animationExpanded)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                DrawDeformerObjectsUI(tileRule);
                EditorGUILayout.EndVertical();
            }

            EditorGUIUtility.labelWidth = previousLabelWidth;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Mesh Mode UI: Custom (prefab) vs Procedural (generated)
        // ─────────────────────────────────────────────────────────────────────
        private void DrawMeshModeUI(QuickTilemapEditor.TileRule rule, TileRuleUIState uiState)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Mode", GUILayout.Width(50));
            var newMode = (QuickTilemapEditor.MeshMode)EditorGUILayout.EnumPopup(rule.meshMode);
            if (newMode != rule.meshMode)
            {
                Undo.RecordObject(tilemapEditor, "Change Mesh Mode");
                rule.meshMode = newMode;
                EditorUtility.SetDirty(tilemapEditor);

                if (rule.meshMode == QuickTilemapEditor.MeshMode.Procedural)
                {
                    SetCustomTileRenderersVisible(rule, false);
                    RebuildProceduralMeshes(rule);
                }
                else
                {
                    SetCustomTileRenderersVisible(rule, true);
                    RemoveProceduralRenderer(rule);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (rule.meshMode == QuickTilemapEditor.MeshMode.Procedural)
            {
                EditorGUILayout.Space(4);
                uiState.proceduralSettingsExpanded = EditorGUILayout.Foldout(
                    uiState.proceduralSettingsExpanded,
                    "Procedural Settings",
                    true);

                if (uiState.proceduralSettingsExpanded)
                {
                    var s = rule.proceduralSettings;
                    EditorGUI.BeginChangeCheck();

                    s.radius = EditorGUILayout.Slider("Corner Radius", s.radius, 0f, 0.5f);
                    // depth is driven by sizeY
                    s.curveSegments = EditorGUILayout.IntSlider("Curve Segments", s.curveSegments, 2, 20);

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Skirt (curved overhang)", EditorStyles.boldLabel);
                    s.skirtEnabled = EditorGUILayout.Toggle("Skirt Enabled", s.skirtEnabled);
                    if (s.skirtEnabled)
                    {
                        s.EnsureSkirtMaskCurve();
                        s.skirtWidth = EditorGUILayout.Slider("Skirt Width", s.skirtWidth, 0f, 0.3f);
                        s.skirtHeight = EditorGUILayout.Slider("Skirt Height", s.skirtHeight, 0f, 0.5f);
                        s.skirtSegments = EditorGUILayout.IntSlider("Skirt Segments", s.skirtSegments, 1, 8);
                        s.skirtUVScale = EditorGUILayout.Slider("Skirt UV Scale", s.skirtUVScale, 0.1f, 10f);
                        s.skirtUVOffsetY = EditorGUILayout.Slider("Skirt UV Offset Y", s.skirtUVOffsetY, -1f, 1f);
                        s.skirtMaterialMode = (SkirtMaterialMode)EditorGUILayout.EnumPopup("Skirt Mode", s.skirtMaterialMode);
                        if (s.skirtMaterialMode == SkirtMaterialMode.UseFloorMaterialWithMask)
                        {
                            s.skirtMaskCurve = EditorGUILayout.CurveField("Edit Skirt Mask", s.skirtMaskCurve);
                        }
                        else
                        {
                            using (new EditorGUI.DisabledScope(true))
                            {
                                EditorGUILayout.ObjectField(
                                    "Current Skirt Material",
                                    GetCurrentSkirtMaterial(rule),
                                    typeof(Material),
                                    false);
                            }
                        }
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Dessous", EditorStyles.boldLabel);
                    s.bottomMode = (BottomMode)EditorGUILayout.EnumPopup("Forme du dessous", s.bottomMode);
                    if (s.bottomMode == BottomMode.Bevel)
                    {
                        s.bottomBevelProfile = (BevelProfile)EditorGUILayout.EnumPopup("Profil du biseau", s.bottomBevelProfile);
                        s.bottomBevelInset = EditorGUILayout.Slider("Rayon du biseau", s.bottomBevelInset, 0f, 1f);
                        s.bottomBevelDepth = EditorGUILayout.Slider("Profondeur du biseau", s.bottomBevelDepth, 0f, 2f);
                        s.bottomBevelSegments = EditorGUILayout.IntSlider("Segments du biseau", s.bottomBevelSegments, 1, 8);
                    }
                    else if (s.bottomMode == BottomMode.IslandNoise)
                    {
                        s.bottomNoiseScale = EditorGUILayout.Slider("Noise Scale", s.bottomNoiseScale, 0.5f, 10f);
                        s.bottomNoiseAmplitude = EditorGUILayout.Slider("Noise Amplitude", s.bottomNoiseAmplitude, 0f, 10f);
                        s.bottomIslandSharpness = EditorGUILayout.Slider("Island Sharpness", s.bottomIslandSharpness, 0.3f, 5f);
                        s.bottomIslandSmooth = EditorGUILayout.Slider("Island Smooth", s.bottomIslandSmooth, 0f, 1f);
                        s.bottomNoiseResolution = EditorGUILayout.IntSlider("Noise Resolution", s.bottomNoiseResolution, 1, 16);
                        s.bottomNoiseSeed = EditorGUILayout.FloatField("Noise Seed", s.bottomNoiseSeed);
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Procedural Materials", EditorStyles.boldLabel);

                    rule.proceduralFloorMaterial = (Material)EditorGUILayout.ObjectField(
                        "Floor (top cap)", rule.proceduralFloorMaterial, typeof(Material), false);
                    var previousWallMaterial = rule.proceduralWallMaterial;
                    bool bottomWasLinkedToWall = BottomCapUsesWallMaterial(rule, previousWallMaterial);
                    var newWallMaterial = (Material)EditorGUILayout.ObjectField(
                        "Walls (sides)", rule.proceduralWallMaterial, typeof(Material), false);
                    rule.proceduralWallMaterial = newWallMaterial;
                    if (bottomWasLinkedToWall)
                        rule.proceduralBottomMaterial = newWallMaterial;
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(
                            "Current Skirt Material",
                            GetCurrentSkirtMaterial(rule),
                            typeof(Material),
                            false);
                    }
                    if (s.bottomMode != BottomMode.None)
                    {
                        rule.proceduralBottomMaterial = (Material)EditorGUILayout.ObjectField(
                            "Materiau du dessous", rule.proceduralBottomMaterial, typeof(Material), false);
                    }
                    rule.proceduralDigMaterial = (Material)EditorGUILayout.ObjectField(
                        "Dig Preview", rule.proceduralDigMaterial, typeof(Material), false);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EditorUtility.SetDirty(tilemapEditor);
                        if (tilemapEditor != null)
                            EditorSceneManager.MarkSceneDirty(tilemapEditor.gameObject.scene);
                    }

                    EditorGUILayout.Space(4);
                    GUI.color = Color.cyan;
                    if (GUILayout.Button("Generate / Rebuild Procedural Meshes", GUILayout.Height(26)))
                    {
                        RebuildProceduralMeshes(rule);
                    }
                    GUI.color = Color.white;
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Custom mode: uses tile prefab meshes as usual.", MessageType.Info);
            }
        }

        private void RebuildProceduralMeshes(QuickTilemapEditor.TileRule rule)
        {
            RefreshProceduralMeshesForLayerChange(rule);
        }

        private void RefreshProceduralMeshesForLayerChange(QuickTilemapEditor.TileRule rule, bool forceAll = false)
        {
            if (tilemapEditor == null || rule == null)
                return;

            if (forceAll || rule.isDigLayer)
            {
                tilemapEditor.SyncAllProceduralRenderers();
                return;
            }

            tilemapEditor.SyncProceduralRenderersAffectedByRule(rule);
        }

        /// <summary>
        /// Show/hide bottom cap sub-settings based on the selected BottomMode.
        /// Biseau controls are shown only for Biseau, noise controls only for IslandNoise.
        /// </summary>
        private void UpdateBottomCapVisibility(UnityEngine.UIElements.VisualElement container, BottomMode mode)
        {
            foreach (var child in container.Children())
            {
                bool show = false;
                string n = child.name ?? "";
                if (n.StartsWith("bevel"))
                    show = (mode == BottomMode.Bevel);
                else if (n.StartsWith("noise"))
                    show = (mode == BottomMode.IslandNoise);
                child.style.display = show
                    ? UnityEngine.UIElements.DisplayStyle.Flex
                    : UnityEngine.UIElements.DisplayStyle.None;
            }
        }

        /// <summary>
        /// Show/hide the custom tile renderers (ground/skirt MeshRenderers) on the tilemap.
        /// Used when switching between Custom and Procedural mesh modes.
        /// </summary>
        private void SetCustomTileRenderersVisible(QuickTilemapEditor.TileRule rule, bool visible)
        {
            if (rule.customTargetTilemap == null)
            {
                Debug.LogWarning("[QuickTile] SetCustomTileRenderersVisible: customTargetTilemap is null");
                return;
            }

            var tilemapGO = rule.customTargetTilemap.gameObject;
            int meshCount = 0;

            // 1) Hide/show all MeshRenderers under the tilemap (ground/skirt 3D meshes)
            var meshRenderers = tilemapGO.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in meshRenderers)
            {
                // Skip anything that belongs to the procedural system
                if (r.GetComponentInParent<ProceduralTileRenderer>() != null) continue;
                if (r.transform.parent != null && r.transform.parent.name == "ProceduralTiles") continue;
                if (IsRendererPartOfDeformerHandle(r.transform, tilemapGO.transform)) continue;

                r.enabled = visible;
                meshCount++;
            }

            // 2) Hide/show all SkinnedMeshRenderers (in case some tiles use skinned meshes)
            var skinnedRenderers = tilemapGO.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var r in skinnedRenderers)
            {
                if (IsRendererPartOfDeformerHandle(r.transform, tilemapGO.transform)) continue;
                r.enabled = visible;
                meshCount++;
            }

            // 3) Hide/show the TilemapRenderer (2D tile rendering)
            var tilemapRenderers = tilemapGO.GetComponentsInChildren<UnityEngine.Tilemaps.TilemapRenderer>(true);
            foreach (var tr in tilemapRenderers)
            {
                tr.enabled = visible;
                meshCount++;
            }

            // 4) Also check parent — if tilemap is child of the main QuickTilemapEditor,
            //    there might be sibling renderers that are logically part of this tile rule
            if (tilemapGO.transform.parent != null)
            {
                // Look for SkirtManager siblings that belong to this tilemap
                var parent = tilemapGO.transform.parent;
                for (int i = 0; i < parent.childCount; i++)
                {
                    var sibling = parent.GetChild(i);
                    var skirt = sibling.GetComponent<SkirtManager>();
                    if (skirt != null)
                    {
                        // Check if this skirt is associated with our tilemap
                        var siblingRenderers = sibling.GetComponentsInChildren<MeshRenderer>(true);
                        foreach (var r in siblingRenderers)
                        {
                            r.enabled = visible;
                            meshCount++;
                        }
                    }
                }
            }

            Debug.Log($"[QuickTile] SetCustomTileRenderersVisible({visible}): toggled {meshCount} renderers on '{tilemapGO.name}'");
            SceneView.RepaintAll();
        }

        private static bool IsRendererPartOfDeformerHandle(Transform rendererTransform, Transform tilemapRoot)
        {
            if (rendererTransform == null || tilemapRoot == null)
                return false;

            var deformers = tilemapRoot.GetComponentsInChildren<RadialHillDeformer>(true);
            if (deformers == null || deformers.Length == 0)
                return false;

            foreach (var deformer in deformers)
            {
                if (deformer == null)
                    continue;

                if (IsTransformHandleOrChild(rendererTransform, deformer.handle))
                    return true;

                if (deformer.additionalHandlesList == null)
                    continue;

                foreach (var handle in deformer.additionalHandlesList)
                {
                    if (IsTransformHandleOrChild(rendererTransform, handle))
                        return true;
                }
            }

            return false;
        }

        private static bool IsTransformHandleOrChild(Transform candidate, Transform handle)
        {
            if (candidate == null || handle == null)
                return false;

            var current = candidate;
            while (current != null)
            {
                if (current == handle)
                    return true;

                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// Remove the ProceduralTileRenderer and its generated meshes when switching back to Custom mode.
        /// </summary>
        private void RemoveProceduralRenderer(QuickTilemapEditor.TileRule rule)
        {
            if (rule.customTargetTilemap == null) return;

            var tilemap = rule.customTargetTilemap;
            var renderer = tilemap.GetComponentInChildren<ProceduralTileRenderer>(true);
            if (renderer != null)
            {
                // This will call OnDestroy which clears instances and mesh cache
                Undo.DestroyObjectImmediate(renderer.gameObject);
            }

            // Re-enable the TilemapRenderer so tiles are visible again in Custom mode
            var tmRenderer = tilemap.GetComponent<TilemapRenderer>();
            if (tmRenderer != null) tmRenderer.enabled = true;

            SceneView.RepaintAll();
        }

        // Put this inside QuickTilemapEditorInspector class
        private void DrawBottomUI(QuickTilemapEditor.TileRule rule)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Bottom / Skirt", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                rule.bottom.enabled = EditorGUILayout.Toggle("Enable", rule.bottom.enabled, GUILayout.Width(200));

                EditorGUILayout.LabelField("Shape", GUILayout.Width(45));
                rule.bottom.shape = (QuickTilemapEditor.BottomShape)
                    EditorGUILayout.EnumPopup(rule.bottom.shape, GUILayout.Width(120));

                EditorGUILayout.LabelField("Size", GUILayout.Width(30));
                int newSize = EditorGUILayout.IntSlider(rule.bottom.size, 1, 20);
                if (newSize != rule.bottom.size) { rule.bottom.size = newSize; }
            }

            // Spline / custom curve (for later procedural profiles)
            if (rule.bottom.shape == QuickTilemapEditor.BottomShape.CustomCurve)
            {
                rule.bottom.profile = EditorGUILayout.CurveField(
                    new GUIContent("Profile (top→bottom)"),
                    rule.bottom.profile, Color.green, new Rect(0, 0, 1, 1));
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preset: Cone")) { rule.bottom.shape = QuickTilemapEditor.BottomShape.Cone; }
            if (GUILayout.Button("Rounded")) { rule.bottom.shape = QuickTilemapEditor.BottomShape.Rounded; }
            if (GUILayout.Button("Blob")) { rule.bottom.shape = QuickTilemapEditor.BottomShape.Blob; }
            if (GUILayout.Button("Flat")) { rule.bottom.shape = QuickTilemapEditor.BottomShape.Flat; }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(!rule.bottom.enabled))
            {
                if (GUILayout.Button("Add / Refresh Bottom", GUILayout.Height(22)))
                    tilemapEditor.ApplyBottomAndRefresh(rule); // calls into the new partial
            }
        }


        private void ExportTileRuleToFBX(QuickTilemapEditor.TileRule rule)
        {
            if (rule?.customTargetTilemap == null)
            {
                EditorUtility.DisplayDialog("Export error",
                    "Cette règle ne possède pas de Tilemap personnalisée.", "OK");
                return;
            }

            // ✅ NOUVEAU: Vérifier qu'il y a des tiles à exporter
            Tilemap tilemap = rule.customTargetTilemap;
            BoundsInt bounds = tilemap.cellBounds;

            // Compter les tiles non-nulles
            int tileCount = 0;
            for (int x = bounds.xMin; x < bounds.xMax; x++)
            {
                for (int y = bounds.yMin; y < bounds.yMax; y++)
                {
                    Vector3Int position = new Vector3Int(x, y, 0);
                    TileBase tile = tilemap.GetTile(position);
                    if (tile != null)
                    {
                        tileCount++;
                    }
                }
            }

            Debug.Log($"🔢 Nombre de tiles trouvées: {tileCount}");

            if (bounds.size.x == 0 || bounds.size.y == 0 || tileCount == 0)
            {
                EditorUtility.DisplayDialog("Export Error",
                    $"Impossible d'exporter: Aucune tile trouvée dans cette tilemap.\n\n" +
                    $"Bounds: {bounds}\n" +
                    $"Tiles count: {tileCount}\n\n" +
                    "Assurez-vous que la tilemap contient des tiles avant d'exporter.", "OK");
                return;
            }

            string tileName = rule.tile != null ? rule.tile.name : "TileRule";
            string defaultName = $"{tileName}_Y{rule.yOffset}.fbx";
            string path = EditorUtility.SaveFilePanel("Export FBX", "", defaultName, "fbx");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                // Afficher une barre de progression
                EditorUtility.DisplayProgressBar("Exporting TileRule", $"Preparing {tileName}...", 0.1f);

                GameObject root = new GameObject("FBX_Export");
                root.transform.position = Vector3.zero;

                // Copier la tilemap et ses enfants
                GameObject tilemapCopy = Instantiate(rule.customTargetTilemap.gameObject, root.transform);
                tilemapCopy.name = $"{tileName}_Tilemap";

                // ✅ NOUVEAU: Inclure les SkirtManagers si présents
                SkirtManager[] skirts = tilemapCopy.GetComponentsInChildren<SkirtManager>(true);
                if (skirts.Length > 0)
                {
                    EditorUtility.DisplayProgressBar("Exporting TileRule", "Including skirts...", 0.5f);
                    Debug.Log($"🎽 {skirts.Length} SkirtManager(s) trouvé(s)");

                    foreach (var skirt in skirts)
                    {
                        // S'assurer que les skirts sont correctement générés
                        skirt.ApplyVisuals();
                    }
                }

                EditorUtility.DisplayProgressBar("Exporting TileRule", "Writing FBX file...", 0.8f);

                // Vérifier si ModelExporter est disponible
                try
                {
                    ModelExporter.ExportObject(path, root);
                    Debug.Log($"✅ Export FBX terminé avec ModelExporter: {path}");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"⚠️ ModelExporter failed: {ex.Message}");

                    // Fallback: Essayer avec l'API réflexion
                    var exporterType = System.Type.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor");
                    if (exporterType != null)
                    {
                        var exportModelMethod = exporterType.GetMethod("ExportObject",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

                        if (exportModelMethod != null)
                        {
                            exportModelMethod.Invoke(null, new object[] { path, root });
                            Debug.Log($"✅ Export FBX terminé avec Reflection API: {path}");
                        }
                        else
                        {
                            throw new System.Exception("FBX Exporter method not found");
                        }
                    }
                    else
                    {
                        // Dernier fallback: créer un prefab
                        string tempPrefabPath = "Assets/TempExport.prefab";
                        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, tempPrefabPath);

                        EditorUtility.DisplayDialog("Export Info",
                            $"FBX Exporter not available. Created prefab instead at:\n{tempPrefabPath}\n\n" +
                            "To export as FBX, install the FBX Exporter package from Package Manager.", "OK");

                        Object.DestroyImmediate(root);
                        EditorUtility.ClearProgressBar();
                        return;
                    }
                }

                Object.DestroyImmediate(root);

                EditorUtility.DisplayProgressBar("Exporting TileRule", "Complete!", 1.0f);

                // Petit délai pour voir le message de completion
                System.Threading.Thread.Sleep(500);
                EditorUtility.ClearProgressBar();

                EditorUtility.RevealInFinder(path);
                Debug.Log($"✅ Export FBX terminé : {path}");
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Export Error",
                    $"Failed to export TileRule:\n{ex.Message}", "OK");
                Debug.LogError($"❌ Export failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ToggleTilemapVisibility(QuickTilemapEditor.TileRule rule)
        {
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
            {
                // Masquer/afficher la tilemap dans la scène
                rule.customTargetTilemap.gameObject.SetActive(rule.isVisible);

                // Optionnel : Masquer aussi le renderer pour un contrôle plus fin
                var renderer = rule.customTargetTilemap.GetComponent<TilemapRenderer>();
                if (renderer != null)
                {
                    renderer.enabled = rule.isVisible;
                }
            }
            else if (tilemapEditor.heightTilemaps.ContainsKey(rule.yOffset))
            {
                var tilemap = tilemapEditor.heightTilemaps[rule.yOffset];
                if (tilemap != null)
                {
                    tilemap.gameObject.SetActive(rule.isVisible);

                    var renderer = tilemap.GetComponent<TilemapRenderer>();
                    if (renderer != null)
                    {
                        renderer.enabled = rule.isVisible;
                    }
                }
            }
        }

        #region UI Toolkit Methods

        /// <summary>
        /// Creates the Tile Rules section using UI Toolkit (VisualElements).
        /// Call this from CreateInspectorGUI() instead of DrawTileRulesSection().
        /// </summary>
        public UnityEngine.UIElements.VisualElement CreateTileRulesSection_UIToolkit()
        {
            var container = new UnityEngine.UIElements.VisualElement();
            container.name = "tile-rules-section";
            
            // Load and apply stylesheet
            var styleSheet = UnityEngine.Resources.Load<UnityEngine.UIElements.StyleSheet>("QuickTilemapEditor");
            if (styleSheet != null)
                container.styleSheets.Add(styleSheet);

            // Header
            var header = new UnityEngine.UIElements.Label("💠 Tile Rules");
            header.AddToClassList("section-header");
            container.Add(header);

            // Rules container (will be populated dynamically)
            var rulesContainer = new UnityEngine.UIElements.VisualElement();
            rulesContainer.name = "tile-rules-list";
            rulesContainer.AddToClassList("rules-scroll");

            // Add button (above the list)
            var addButton = new UnityEngine.UIElements.Button(() => {
                AddTileRule();
                RefreshTileRulesList_UIToolkit(rulesContainer);
            });
            addButton.text = "+ Add Tile Rule";
            addButton.AddToClassList("btn-add");
            container.Add(addButton);

            container.Add(rulesContainer);

            // Populate rules
            RefreshTileRulesList_UIToolkit(rulesContainer);

            return container;
        }

        /// <summary>
        /// Refreshes the tile rules list in the UI Toolkit container
        /// </summary>
	        private void RefreshTileRulesList_UIToolkit(UnityEngine.UIElements.VisualElement container)
	        {
	            if (container == null)
	                return;

	            if (container.name != "tile-rules-list")
	            {
	                var rulesList = container.Q("tile-rules-list");
	                if (rulesList != null)
	                    container = rulesList;
	            }

	            container.Clear();
	            EnsureTileRuleUIStateCount();

            if (tilemapEditor?.tileRules == null || tilemapEditor.tileRules.Count == 0)
            {
                var emptyState = new UnityEngine.UIElements.VisualElement();
                emptyState.AddToClassList("empty-state");
                var emptyLabel = new UnityEngine.UIElements.Label("No tile rules yet.\nClick '+ Add Tile Rule' to create one.");
                emptyLabel.AddToClassList("empty-state-text");
                emptyState.Add(emptyLabel);
                container.Add(emptyState);
                return;
            }

            for (int i = 0; i < tilemapEditor.tileRules.Count; i++)
            {
                var ruleCard = CreateTileRuleCard_UIToolkit(i);
                container.Add(ruleCard);
            }
        }

        /// <summary>
        /// Creates a single tile rule card using UI Toolkit
        /// </summary>
        private UnityEngine.UIElements.VisualElement CreateTileRuleCard_UIToolkit(int index)
        {
            var tileRule = tilemapEditor.tileRules[index];
            var uiState = tileRuleUIStates[index];
            bool isSelected = tilemapEditor.selectedTileRuleIndex == index;

            // Card container
            var card = new UnityEngine.UIElements.VisualElement();
            card.name = $"tile-rule-{index}";
            card.AddToClassList("card");
            card.AddToClassList("tile-rule-card");
            if (isSelected) card.AddToClassList("card-selected");
            
            // Set left border color to rule color
            card.style.borderLeftColor = new UnityEngine.UIElements.StyleColor(tileRule.color);

            // Header row
            var headerRow = new UnityEngine.UIElements.VisualElement();
            headerRow.AddToClassList("card-header");
            headerRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;

            // Color indicator
            var colorIndicator = new UnityEngine.UIElements.VisualElement();
            colorIndicator.AddToClassList("color-indicator");
            colorIndicator.style.backgroundColor = new UnityEngine.UIElements.StyleColor(tileRule.color);
            headerRow.Add(colorIndicator);

            // Title
            string tileName = tileRule.tile != null ? tileRule.tile.name : "Tile Rule";
            if (tileRule.isDigLayer)
                tileName += " [Dig]";
            else if (tileRule.isDiggable)
                tileName += " [Diggable]";
            if (tileRule.isUndiggable)
                tileName += " [No Dig]";
            var title = new UnityEngine.UIElements.Label($"{index + 1}. {tileName}");
            title.AddToClassList("card-title");
            headerRow.Add(title);

            // Spacer
            var spacer = new UnityEngine.UIElements.VisualElement();
            spacer.style.flexGrow = 1;
            headerRow.Add(spacer);

            // Visibility toggle
            var visBtn = new UnityEngine.UIElements.Button(() => {
                tileRule.isVisible = !tileRule.isVisible;
                ToggleTilemapVisibility(tileRule);
                EditorUtility.SetDirty(tilemapEditor);
            });
            visBtn.text = GetVisibilityToggleIcon(tileRule.isVisible);
            visBtn.AddToClassList("btn");
            visBtn.AddToClassList("btn-icon");
            headerRow.Add(visBtn);

            var settingsBtn = new UnityEngine.UIElements.Button(() =>
            {
                SetTileRuleSettingsExpandedExclusive(index, !uiState.expanded);
            });
            settingsBtn.text = uiState.expanded ? "▼ Settings" : "▶ Settings";
            settingsBtn.AddToClassList("btn");
            headerRow.Add(settingsBtn);

            /*
            // Move up
            var upBtn = new UnityEngine.UIElements.Button(() => MoveTileRule(index, -1));
            upBtn.text = "▲";
            upBtn.AddToClassList("btn");
            upBtn.AddToClassList("btn-icon");
            upBtn.SetEnabled(index > 0);
            headerRow.Add(upBtn);

            // Move down
            var downBtn = new UnityEngine.UIElements.Button(() => MoveTileRule(index, 1));
            downBtn.text = "▼";
            downBtn.AddToClassList("btn");
            downBtn.AddToClassList("btn-icon");
            downBtn.SetEnabled(index < tilemapEditor.tileRules.Count - 1);
            headerRow.Add(downBtn);
            */

            // Select button
            var selectBtn = new UnityEngine.UIElements.Button(() => {
                SelectTileRuleExclusive(index);
            });
            selectBtn.text = "Select";
            selectBtn.AddToClassList("btn");
            if (isSelected) selectBtn.AddToClassList("btn-primary");
            headerRow.Add(selectBtn);

	            // Delete button
	            var deleteBtn = new UnityEngine.UIElements.Button(() => RemoveTileRule(index));
	            deleteBtn.text = "✖";
	            deleteBtn.AddToClassList("btn");
	            deleteBtn.AddToClassList("btn-icon");
	            deleteBtn.AddToClassList("btn-danger");
	            headerRow.Add(deleteBtn);

	            card.Add(headerRow);

	            // var summaryRow = CreateTileRuleSummaryRow_UIToolkit(index, tileRule, uiState);
	            // summaryRow.style.display = uiState.expanded
	            //     ? UnityEngine.UIElements.DisplayStyle.Flex
	            //     : UnityEngine.UIElements.DisplayStyle.None;
	            // card.Add(summaryRow);

	            var detailsContainer = new UnityEngine.UIElements.VisualElement();
	            detailsContainer.style.display = uiState.expanded
	                ? UnityEngine.UIElements.DisplayStyle.Flex
	                : UnityEngine.UIElements.DisplayStyle.None;
	            detailsContainer.style.flexDirection = UnityEngine.UIElements.FlexDirection.Column;
	            detailsContainer.style.paddingLeft = 10;
	            detailsContainer.style.paddingRight = 10;
	            detailsContainer.style.paddingBottom = 10;

	            var content = CreateTileRuleContent_UIToolkit(index, tileRule, uiState);
	            detailsContainer.Add(content);
	            card.Add(detailsContainer);

	            return card;
	        }

	        private UnityEngine.UIElements.VisualElement CreateTileRuleSummaryRow_UIToolkit(
	            int index,
	            QuickTilemapEditor.TileRule tileRule,
	            TileRuleUIState uiState)
	        {
	            var summaryRow = new UnityEngine.UIElements.VisualElement();
	            summaryRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
	            summaryRow.style.alignItems = UnityEngine.UIElements.Align.FlexStart;
	            summaryRow.style.paddingLeft = 10;
	            summaryRow.style.paddingRight = 10;
	            summaryRow.style.paddingBottom = 8;
	            summaryRow.style.marginTop = 6;

	            var rightColumn = new UnityEngine.UIElements.VisualElement();
	            rightColumn.style.flexGrow = 1;
	            rightColumn.style.flexDirection = UnityEngine.UIElements.FlexDirection.Column;

	            var summaryTitle = new UnityEngine.UIElements.Label("Placement");
	            summaryTitle.style.fontSize = 11;
	            summaryTitle.style.color = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.65f, 0.72f, 0.8f, 1f));
	            summaryTitle.style.marginBottom = 6;
	            rightColumn.Add(summaryTitle);

	            rightColumn.Add(CreateTileRuleHeightSummaryControls_UIToolkit(tileRule));
	            summaryRow.Add(rightColumn);

	            return summaryRow;
	        }

	        private UnityEngine.UIElements.VisualElement CreateTileRuleHeightSummaryControls_UIToolkit(QuickTilemapEditor.TileRule tileRule)
	        {
	            var controlsRow = new UnityEngine.UIElements.VisualElement();
	            controlsRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
	            controlsRow.style.alignItems = UnityEngine.UIElements.Align.Center;
	            controlsRow.style.flexWrap = UnityEngine.UIElements.Wrap.Wrap;

	            UnityEngine.UIElements.VisualElement CreateGroup(
	                string labelText,
	                float value,
	                bool enabled,
	                Action<float> setValue,
	                float step,
	                float minValue,
	                float maxValue,
	                float labelWidth)
	            {
	                const string chevronIconAsset = "chevron_right_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png";

	                var group = new UnityEngine.UIElements.VisualElement();
	                group.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
	                group.style.alignItems = UnityEngine.UIElements.Align.Center;
	                group.style.flexGrow = 0;
	                group.style.flexShrink = 0;
	                group.style.marginRight = 8;
	                group.style.marginBottom = 2;

	                var label = new UnityEngine.UIElements.Label(labelText);
	                label.style.minWidth = labelWidth;
	                label.style.width = labelWidth;
	                label.style.color = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.85f, 0.85f, 0.85f, 1f));
	                label.style.fontSize = 12;
	                group.Add(label);

	                var field = new UnityEngine.UIElements.FloatField();
	                field.value = value;
	                field.style.width = 50;
	                field.style.marginLeft = 0;
	                field.style.marginRight = 6;
	                field.SetEnabled(enabled);
	                field.RegisterValueChangedCallback(evt =>
	                {
	                    float nextValue = Mathf.Clamp(Mathf.Round(evt.newValue / step) * step, minValue, maxValue);
	                    field.SetValueWithoutNotify(nextValue);
	                    setValue(nextValue);
	                });
	                group.Add(field);

	                var plusBtn = new UnityEngine.UIElements.Button(() =>
	                {
	                    float nextValue = Mathf.Clamp(value + step, minValue, maxValue);
	                    setValue(nextValue);
	                });
	                plusBtn.AddToClassList("btn");
	                plusBtn.style.width = 30;
	                plusBtn.style.height = 30;
	                plusBtn.style.marginRight = 2;
	                plusBtn.tooltip = $"Increase {labelText}";
	                SetUIToolkitHeaderButtonIcon(plusBtn, chevronIconAsset, -90f);
	                plusBtn.SetEnabled(enabled);
	                group.Add(plusBtn);

	                var minusBtn = new UnityEngine.UIElements.Button(() =>
	                {
	                    float nextValue = Mathf.Clamp(value - step, minValue, maxValue);
	                    setValue(nextValue);
	                });
	                minusBtn.AddToClassList("btn");
	                minusBtn.style.width = 30;
	                minusBtn.style.height = 30;
	                minusBtn.tooltip = $"Decrease {labelText}";
	                SetUIToolkitHeaderButtonIcon(minusBtn, chevronIconAsset, 90f);
	                minusBtn.SetEnabled(enabled);
	                group.Add(minusBtn);

	                return group;
	            }

	            controlsRow.Add(CreateGroup(
	                "Y Offset",
	                tileRule.yOffset,
	                true,
	                newValue =>
	                {
	                    tileRule.yOffset = newValue;
	                    UpdateTilemapYOffset(tileRule);
	                    RefreshProceduralMeshesForLayerChange(tileRule);
	                    EditorUtility.SetDirty(tilemapEditor);
	                    if (tileRulesUIToolkitContainer != null)
	                        RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
	                },
	                0.25f,
	                -100f,
	                100f,
	                52f));

	            controlsRow.Add(CreateGroup(
	                "Size Y",
	                tileRule.sizeY,
	                !tileRule.fixBase,
	                newValue =>
	                {
	                    tileRule.sizeY = newValue;
	                    ApplySkirtVisuals(tileRule);
	                    RefreshProceduralMeshesForLayerChange(tileRule);
	                    EditorUtility.SetDirty(tilemapEditor);
	                    if (tileRulesUIToolkitContainer != null)
	                        RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
	                },
	                0.5f,
	                0f,
	                100f,
	                42f));

	            return controlsRow;
	        }

	        /// <summary>
	        /// Creates the expandable content for a tile rule card
	        /// </summary>
        private UnityEngine.UIElements.VisualElement CreateTileRuleContent_UIToolkit(int index, QuickTilemapEditor.TileRule tileRule, TileRuleUIState uiState)
        {
            var content = new UnityEngine.UIElements.VisualElement();
            content.AddToClassList("card-body");

            // === INTERNAL TABS ===
            var tabBar = new UnityEngine.UIElements.VisualElement();
            tabBar.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            tabBar.style.marginBottom = 8;
            
            var placementTabBtn = new UnityEngine.UIElements.Button();
            placementTabBtn.text = "📍 Placement";
            placementTabBtn.style.flexGrow = 1;
            placementTabBtn.style.height = 24;
            placementTabBtn.style.marginRight = 2;
            placementTabBtn.style.borderTopLeftRadius = 4;
            placementTabBtn.style.borderTopRightRadius = 4;
            
            var moveTabBtn = new UnityEngine.UIElements.Button();
            moveTabBtn.text = "🔄 Move";
            moveTabBtn.style.flexGrow = 1;
            moveTabBtn.style.height = 24;
            moveTabBtn.style.marginRight = 2;
            moveTabBtn.style.borderTopLeftRadius = 4;
            moveTabBtn.style.borderTopRightRadius = 4;
            
	            var textureTabBtn = new UnityEngine.UIElements.Button();
	            textureTabBtn.text = "🎨 Texture";
	            textureTabBtn.style.flexGrow = 1;
	            textureTabBtn.style.height = 24;
	            textureTabBtn.style.borderTopLeftRadius = 4;
	            textureTabBtn.style.borderTopRightRadius = 4;

	            var deformersTabBtn = new UnityEngine.UIElements.Button();
	            deformersTabBtn.text = "🔧 Deformers";
	            deformersTabBtn.style.flexGrow = 1;
	            deformersTabBtn.style.height = 24;
	            deformersTabBtn.style.marginRight = 2;
	            deformersTabBtn.style.borderTopLeftRadius = 4;
	            deformersTabBtn.style.borderTopRightRadius = 4;

            // Tab content containers
            var placementContent = new UnityEngine.UIElements.VisualElement();
            placementContent.name = "placement-content";
            
            var moveContent = new UnityEngine.UIElements.VisualElement();
            moveContent.name = "move-content";
            
            var textureContent = new UnityEngine.UIElements.VisualElement();
            textureContent.name = "texture-content";
            
            var deformersContent = new UnityEngine.UIElements.VisualElement();
            deformersContent.name = "deformers-content";

            // Update tab visuals
            System.Action updateTabVisuals = () => {
                placementTabBtn.style.backgroundColor = uiState.selectedTab == 0 
                    ? new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.25f, 0.6f, 0.9f, 1f))
                    : new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.15f, 0.15f, 0.15f, 1f));
                placementTabBtn.style.color = new UnityEngine.UIElements.StyleColor(UnityEngine.Color.white);
                
                moveTabBtn.style.backgroundColor = uiState.selectedTab == 1 
                    ? new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.25f, 0.6f, 0.9f, 1f))
                    : new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.15f, 0.15f, 0.15f, 1f));
                moveTabBtn.style.color = new UnityEngine.UIElements.StyleColor(UnityEngine.Color.white);

                textureTabBtn.style.backgroundColor = uiState.selectedTab == 3 
                    ? new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.25f, 0.6f, 0.9f, 1f))
                    : new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.15f, 0.15f, 0.15f, 1f));
                textureTabBtn.style.color = new UnityEngine.UIElements.StyleColor(UnityEngine.Color.white);
                
                deformersTabBtn.style.backgroundColor = uiState.selectedTab == 2 
                    ? new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.25f, 0.6f, 0.9f, 1f))
                    : new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.15f, 0.15f, 0.15f, 1f));
                deformersTabBtn.style.color = new UnityEngine.UIElements.StyleColor(UnityEngine.Color.white);

                placementContent.style.display = uiState.selectedTab == 0 
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
                moveContent.style.display = uiState.selectedTab == 1 
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
                textureContent.style.display = uiState.selectedTab == 3 
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
                deformersContent.style.display = uiState.selectedTab == 2 
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
            };

            placementTabBtn.clicked += () => { uiState.selectedTab = 0; updateTabVisuals(); };
            moveTabBtn.clicked += () => { uiState.selectedTab = 1; updateTabVisuals(); };
            textureTabBtn.clicked += () => { uiState.selectedTab = 3; updateTabVisuals(); };
            deformersTabBtn.clicked += () => { uiState.selectedTab = 2; updateTabVisuals(); };

	            tabBar.Add(placementTabBtn);
	            tabBar.Add(moveTabBtn);
	            tabBar.Add(deformersTabBtn);
	            tabBar.Add(textureTabBtn);
	            content.Add(tabBar);

            // === PLACEMENT TAB CONTENT ===
            // Tile field row
            var tileRow = new UnityEngine.UIElements.VisualElement();
            tileRow.AddToClassList("field-row");
            tileRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            
            var tileLabel = new UnityEngine.UIElements.Label("Tile");
            tileLabel.AddToClassList("field-label");
            tileRow.Add(tileLabel);

            var tileField = new UnityEditor.UIElements.ObjectField();
            tileField.objectType = typeof(TileBase);
            tileField.value = tileRule.tile;
	            tileField.style.flexGrow = 1;
	            tileField.RegisterValueChangedCallback(evt => {
	                tileRule.tile = evt.newValue as TileBase;
	                EditorUtility.SetDirty(tilemapEditor);
	                if (tileRulesUIToolkitContainer != null)
	                    RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
	            });
	            tileRow.Add(tileField);

            var updateBtn = new UnityEngine.UIElements.Button(() => tilemapEditor.UpdateTileRuleOnScene(tileRule));
            updateBtn.text = "Update";
            updateBtn.AddToClassList("btn");
            tileRow.Add(updateBtn);

            var fbxBtn = new UnityEngine.UIElements.Button(() => ExportTileRuleToFBX(tileRule));
            fbxBtn.text = "📦 FBX";
            fbxBtn.AddToClassList("btn");
            fbxBtn.AddToClassList("btn-primary");
            tileRow.Add(fbxBtn);

            if (tileRule.meshMode != QuickTilemapEditor.MeshMode.Procedural)
                placementContent.Add(tileRow);

	            /*
	            // Custom Tilemap row
	            var customTilemapRow = new UnityEngine.UIElements.VisualElement();
            customTilemapRow.AddToClassList("field-row");
            customTilemapRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;

            UnityEditor.UIElements.ObjectField customTilemapField = null;
            UnityEngine.UIElements.HelpBox digHelp = null;
            UnityEngine.UIElements.Button digStateBtn = null;
            UnityEngine.UIElements.Button customMapBtn = null;
            UnityEngine.UIElements.Button digLayerBtn = null;

            void RefreshCustomMapButton()
            {
                if (customMapBtn == null) return;
                customMapBtn.text = tileRule.useCustomTilemap ? "Custom Map: ON" : "Custom Map: OFF";
            }

            void RefreshDigLayerButton()
            {
                if (digLayerBtn == null) return;
                digLayerBtn.text = tileRule.isDigLayer ? "Dig Layer: ON" : "Dig Layer: OFF";
            }

            void RefreshDigStateButton()
            {
                if (digStateBtn == null) return;
                digStateBtn.text = tileRule.isDiggable ? "Diggable" : "Undiggable";
            }

            customMapBtn = new UnityEngine.UIElements.Button(() => {
                tileRule.useCustomTilemap = !tileRule.useCustomTilemap;
                if (customTilemapField != null)
                    customTilemapField.SetEnabled(tileRule.useCustomTilemap);
                if (digHelp != null)
                {
                    digHelp.style.display = tileRule.isDigLayer && (!tileRule.useCustomTilemap || tileRule.customTargetTilemap == null)
                        ? UnityEngine.UIElements.DisplayStyle.Flex
                        : UnityEngine.UIElements.DisplayStyle.None;
                }
                RefreshCustomMapButton();
                EditorUtility.SetDirty(tilemapEditor);
                RefreshProceduralMeshesForLayerChange(tileRule, tileRule.isDigLayer);
            });
            customMapBtn.AddToClassList("btn");
            customMapBtn.style.width = 120;
            RefreshCustomMapButton();
            customTilemapRow.Add(customMapBtn);

            customTilemapField = new UnityEditor.UIElements.ObjectField();
            customTilemapField.objectType = typeof(Tilemap);
            customTilemapField.value = tileRule.customTargetTilemap;
            customTilemapField.style.flexGrow = 1;
            customTilemapField.SetEnabled(tileRule.useCustomTilemap);
            customTilemapField.RegisterValueChangedCallback(evt => {
                tileRule.customTargetTilemap = evt.newValue as Tilemap;
                if (digHelp != null)
                {
                    digHelp.style.display = tileRule.isDigLayer && (!tileRule.useCustomTilemap || tileRule.customTargetTilemap == null)
                        ? UnityEngine.UIElements.DisplayStyle.Flex
                        : UnityEngine.UIElements.DisplayStyle.None;
                }
                EditorUtility.SetDirty(tilemapEditor);
                RefreshProceduralMeshesForLayerChange(tileRule, tileRule.isDigLayer);
            });
            customTilemapRow.Add(customTilemapField);
            placementContent.Add(customTilemapRow);

            var digRow = new UnityEngine.UIElements.VisualElement();
            digRow.AddToClassList("field-row");
            digRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;

            digLayerBtn = new UnityEngine.UIElements.Button(() => {
                tileRule.isDigLayer = !tileRule.isDigLayer;
                if (digHelp != null)
                {
                    digHelp.style.display = tileRule.isDigLayer && (!tileRule.useCustomTilemap || tileRule.customTargetTilemap == null)
                        ? UnityEngine.UIElements.DisplayStyle.Flex
                        : UnityEngine.UIElements.DisplayStyle.None;
                }
                RefreshDigLayerButton();
                EditorUtility.SetDirty(tilemapEditor);
                RefreshProceduralMeshesForLayerChange(tileRule, true);
            });
            digLayerBtn.AddToClassList("btn");
            digLayerBtn.style.width = 120;
            RefreshDigLayerButton();
            digRow.Add(digLayerBtn);

            var digInfo = new UnityEngine.UIElements.Label("Subtracts from overlapping procedural layers.");
            digInfo.style.flexGrow = 1;
            digInfo.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Italic;
            digRow.Add(digInfo);
            placementContent.Add(digRow);

            var digStateRow = new UnityEngine.UIElements.VisualElement();
            digStateRow.AddToClassList("field-row");
            digStateRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;

            var digStateInfo = new UnityEngine.UIElements.Label();
            digStateInfo.style.flexGrow = 1;
            digStateInfo.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Italic;

            void RefreshDigStateInfo()
            {
                digStateInfo.text = tileRule.isDiggable
                    ? "Receives Dig Layers."
                    : "Ignores Dig Layers.";
            }

            digStateBtn = new UnityEngine.UIElements.Button(() => {
                tileRule.isDiggable = !tileRule.isDiggable;
                tileRule.isUndiggable = !tileRule.isDiggable;
                RefreshDigStateButton();
                RefreshDigStateInfo();
                EditorUtility.SetDirty(tilemapEditor);
                RefreshProceduralMeshesForLayerChange(tileRule, tileRule.isDigLayer);
            });
            digStateBtn.AddToClassList("btn");
            digStateBtn.style.width = 120;
            RefreshDigStateButton();
            digStateRow.Add(digStateBtn);

            RefreshDigStateInfo();
            digStateRow.Add(digStateInfo);
            placementContent.Add(digStateRow);

            digHelp = new UnityEngine.UIElements.HelpBox(
                "Dig Layer works on dedicated tilemap layers. Enable Custom Map and assign a tilemap layer to make this rule carve other procedural layers.",
                UnityEngine.UIElements.HelpBoxMessageType.Info);
            digHelp.style.display = tileRule.isDigLayer && (!tileRule.useCustomTilemap || tileRule.customTargetTilemap == null)
                ? UnityEngine.UIElements.DisplayStyle.Flex
                : UnityEngine.UIElements.DisplayStyle.None;
            placementContent.Add(digHelp);
            */

            // Fix Base button row
            var fixBaseRow = new UnityEngine.UIElements.VisualElement();
            fixBaseRow.AddToClassList("field-row");
            fixBaseRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            UnityEngine.UIElements.Label fixBaseInfo = null;
            UnityEngine.UIElements.Button fixBaseBtn = null;

            fixBaseBtn = new UnityEngine.UIElements.Button(() => {
                tileRule.fixBase = !tileRule.fixBase;
                if (tileRule.customTargetTilemap != null)
                {
                    var skirts = tileRule.customTargetTilemap.GetComponentsInChildren<SkirtManager>(true);
                    foreach (var skirt in skirts)
                    {
                        skirt.wallCount = tileRule.fixBase
                            ? Mathf.FloorToInt(tileRule.yOffset / skirt.WallStep)
                            : Mathf.RoundToInt(tileRule.roundedCorner);
                        skirt.scaleValue = tileRule.fixBase
                            ? tileRule.yOffset * 10f
                            : tileRule.roundedCorner;
                        skirt.ApplyVisuals();
                        EditorUtility.SetDirty(skirt);
                    }
                }
                fixBaseBtn.text = tileRule.fixBase ? "Fixed base: ON" : "Fixed base: OFF";
                if (tileRulesUIToolkitContainer != null)
                    RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
                if (fixBaseInfo != null)
                {
                    fixBaseInfo.style.display = tileRule.fixBase
                        ? UnityEngine.UIElements.DisplayStyle.Flex
                        : UnityEngine.UIElements.DisplayStyle.None;
                }
                EditorUtility.SetDirty(tilemapEditor);
                RefreshProceduralMeshesForLayerChange(tileRule);
                SceneView.RepaintAll();
            });
            fixBaseBtn.text = tileRule.fixBase ? "Fixed base: ON" : "Fixed base: OFF";
            fixBaseBtn.AddToClassList("btn");
            fixBaseBtn.style.width = 150;
            fixBaseRow.Add(fixBaseBtn);
            placementContent.Add(fixBaseRow);

            fixBaseInfo = new UnityEngine.UIElements.Label("Size Y disabled when Fixed base is ON.");
            fixBaseInfo.style.display = tileRule.fixBase
                ? UnityEngine.UIElements.DisplayStyle.Flex
                : UnityEngine.UIElements.DisplayStyle.None;
            fixBaseInfo.style.color = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.65f, 0.65f, 0.65f, 1f));
            fixBaseInfo.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Italic;
            placementContent.Add(fixBaseInfo);

            // Move Y Offset and Size Y here
            var heightRow = new UnityEngine.UIElements.VisualElement();
            heightRow.AddToClassList("field-row");
            heightRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            heightRow.Add(CreateTileRuleHeightSummaryControls_UIToolkit(tileRule));
            placementContent.Add(heightRow);

            // === MESH MODE SECTION ===
            var meshModeSeparator = new UnityEngine.UIElements.VisualElement();
            meshModeSeparator.style.height = 1;
            meshModeSeparator.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.3f, 0.3f, 0.3f, 1f));
            meshModeSeparator.style.marginTop = 8;
            meshModeSeparator.style.marginBottom = 8;
            placementContent.Add(meshModeSeparator);

            var meshModeLabel = new UnityEngine.UIElements.Label("Mesh Mode");
            meshModeLabel.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
            meshModeLabel.style.marginBottom = 4;
            placementContent.Add(meshModeLabel);

            // Mode dropdown (Custom / Procedural)
            var meshModeRow = new UnityEngine.UIElements.VisualElement();
            meshModeRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            meshModeRow.AddToClassList("field-row");

            var modeLabel = new UnityEngine.UIElements.Label("Mode");
            modeLabel.AddToClassList("field-label");
            meshModeRow.Add(modeLabel);

            var localTileRule = tileRule; // capture for lambda
            var meshModeField = new UnityEngine.UIElements.EnumField(localTileRule.meshMode);
            meshModeField.style.flexGrow = 1;
            meshModeField.RegisterValueChangedCallback(evt => {
                Undo.RecordObject(tilemapEditor, "Change Mesh Mode");
                localTileRule.meshMode = (QuickTilemapEditor.MeshMode)evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);

                // Auto-rebuild when switching to Procedural
                if (localTileRule.meshMode == QuickTilemapEditor.MeshMode.Procedural)
                {
                    SetCustomTileRenderersVisible(localTileRule, false);
                    RebuildProceduralMeshes(localTileRule);
                }
                else
                {
                    // Back to Custom: show originals, remove procedural
                    SetCustomTileRenderersVisible(localTileRule, true);
                    RemoveProceduralRenderer(localTileRule);
                }

                // Rebuild the inspector to show/hide procedural settings
                RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
            });
            meshModeRow.Add(meshModeField);
            placementContent.Add(meshModeRow);

            // Procedural settings (only visible when mode == Procedural)
            if (tileRule.meshMode == QuickTilemapEditor.MeshMode.Procedural)
            {
                var procSettings = tileRule.proceduralSettings;

                var proceduralFoldout = new UnityEngine.UIElements.Foldout();
                proceduralFoldout.text = "Procedural Settings";
                proceduralFoldout.value = uiState.proceduralSettingsExpanded;
                proceduralFoldout.style.marginTop = 6;
                proceduralFoldout.style.marginBottom = 4;
                proceduralFoldout.RegisterValueChangedCallback(evt => uiState.proceduralSettingsExpanded = evt.newValue);
                placementContent.Add(proceduralFoldout);

                var proceduralContent = new UnityEngine.UIElements.VisualElement();
                proceduralFoldout.Add(proceduralContent);

                // Corner Radius slider
                var radiusSlider = new UnityEngine.UIElements.Slider("Corner Radius", 0f, 0.5f);
                radiusSlider.value = procSettings.radius;
                radiusSlider.showInputField = true;
                radiusSlider.RegisterValueChangedCallback(evt => {
                    procSettings.radius = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(radiusSlider);

                // Curve Segments (depth is driven by sizeY)
                var curveSegSlider = new UnityEngine.UIElements.SliderInt("Curve Segments", 2, 20);
                curveSegSlider.value = procSettings.curveSegments;
                curveSegSlider.showInputField = true;
                curveSegSlider.RegisterValueChangedCallback(evt => {
                    procSettings.curveSegments = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(curveSegSlider);

                // Skirt section
                var skirtLabel = new UnityEngine.UIElements.Label("Skirt (curved overhang)");
                skirtLabel.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
                skirtLabel.style.marginTop = 6;
                skirtLabel.style.marginBottom = 4;
                proceduralContent.Add(skirtLabel);

                var skirtDetailsContainer = new UnityEngine.UIElements.VisualElement();

                var skirtEnabledToggle = new UnityEngine.UIElements.Toggle("Skirt Enabled");
                skirtEnabledToggle.value = procSettings.skirtEnabled;
                skirtEnabledToggle.RegisterValueChangedCallback(evt => {
                    procSettings.skirtEnabled = evt.newValue;
                    skirtDetailsContainer.style.display = evt.newValue
                        ? UnityEngine.UIElements.DisplayStyle.Flex
                        : UnityEngine.UIElements.DisplayStyle.None;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(skirtEnabledToggle);

                skirtDetailsContainer.style.display = procSettings.skirtEnabled
                    ? UnityEngine.UIElements.DisplayStyle.Flex
                    : UnityEngine.UIElements.DisplayStyle.None;

                procSettings.EnsureSkirtMaskCurve();

                var skirtWidthSlider = new UnityEngine.UIElements.Slider("Skirt Width", 0f, 0.3f);
                skirtWidthSlider.value = procSettings.skirtWidth;
                skirtWidthSlider.showInputField = true;
                skirtWidthSlider.RegisterValueChangedCallback(evt => {
                    procSettings.skirtWidth = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                skirtDetailsContainer.Add(skirtWidthSlider);

                var skirtHeightSlider = new UnityEngine.UIElements.Slider("Skirt Height", 0f, 0.5f);
                skirtHeightSlider.value = procSettings.skirtHeight;
                skirtHeightSlider.showInputField = true;
                skirtHeightSlider.RegisterValueChangedCallback(evt => {
                    procSettings.skirtHeight = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                skirtDetailsContainer.Add(skirtHeightSlider);

                var skirtSegSlider = new UnityEngine.UIElements.SliderInt("Skirt Segments", 1, 8);
                skirtSegSlider.value = procSettings.skirtSegments;
                skirtSegSlider.showInputField = true;
                skirtSegSlider.RegisterValueChangedCallback(evt => {
                    procSettings.skirtSegments = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                skirtDetailsContainer.Add(skirtSegSlider);

                var skirtUVScaleSlider = new UnityEngine.UIElements.Slider("Skirt UV Scale", 0.1f, 10f);
                skirtUVScaleSlider.value = procSettings.skirtUVScale;
                skirtUVScaleSlider.showInputField = true;
                skirtUVScaleSlider.RegisterValueChangedCallback(evt => {
                    procSettings.skirtUVScale = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                skirtDetailsContainer.Add(skirtUVScaleSlider);

                var skirtUVOffsetSlider = new UnityEngine.UIElements.Slider("Skirt UV Offset Y", -1f, 1f);
                skirtUVOffsetSlider.value = procSettings.skirtUVOffsetY;
                skirtUVOffsetSlider.showInputField = true;
                skirtUVOffsetSlider.RegisterValueChangedCallback(evt => {
                    procSettings.skirtUVOffsetY = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                skirtDetailsContainer.Add(skirtUVOffsetSlider);

                var skirtModeField = new UnityEngine.UIElements.EnumField("Skirt Mode", procSettings.skirtMaterialMode);
                skirtDetailsContainer.Add(skirtModeField);

                var currentSkirtMaterialField = new UnityEditor.UIElements.ObjectField("Current Skirt Material");
                currentSkirtMaterialField.objectType = typeof(Material);
                currentSkirtMaterialField.value = GetCurrentSkirtMaterial(tileRule);
                currentSkirtMaterialField.SetEnabled(false);
                skirtDetailsContainer.Add(currentSkirtMaterialField);

                var skirtMaskCurveField = new UnityEditor.UIElements.CurveField("Edit Skirt Mask");
                skirtMaskCurveField.value = procSettings.skirtMaskCurve;
                skirtDetailsContainer.Add(skirtMaskCurveField);

                void RefreshSkirtModeVisibility()
                {
                    bool usesMask = procSettings.skirtMaterialMode == SkirtMaterialMode.UseFloorMaterialWithMask;
                    currentSkirtMaterialField.style.display = usesMask
                        ? UnityEngine.UIElements.DisplayStyle.None
                        : UnityEngine.UIElements.DisplayStyle.Flex;
                    skirtMaskCurveField.style.display = usesMask
                        ? UnityEngine.UIElements.DisplayStyle.Flex
                        : UnityEngine.UIElements.DisplayStyle.None;
                    currentSkirtMaterialField.SetValueWithoutNotify(GetCurrentSkirtMaterial(tileRule));
                }

                skirtModeField.RegisterValueChangedCallback(evt => {
                    procSettings.skirtMaterialMode = (SkirtMaterialMode)evt.newValue;
                    RefreshSkirtModeVisibility();
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });

                skirtMaskCurveField.RegisterValueChangedCallback(evt => {
                    procSettings.skirtMaskCurve = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });

                RefreshSkirtModeVisibility();

                proceduralContent.Add(skirtDetailsContainer);

                // Bottom section
                var bottomCapLabel = new UnityEngine.UIElements.Label("Dessous");
                bottomCapLabel.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
                bottomCapLabel.style.marginTop = 6;
                bottomCapLabel.style.marginBottom = 4;
                proceduralContent.Add(bottomCapLabel);

                var bottomDetailsContainer = new UnityEngine.UIElements.VisualElement();

                var bottomModeField = new UnityEngine.UIElements.EnumField("Forme du dessous", procSettings.bottomMode);
                bottomModeField.RegisterValueChangedCallback(evt => {
                    procSettings.bottomMode = (BottomMode)evt.newValue;
                    // Show/hide sub-settings based on mode
                    UpdateBottomCapVisibility(bottomDetailsContainer, procSettings.bottomMode);
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(bottomModeField);

                // Biseau settings
                var bevelProfileField = new UnityEngine.UIElements.EnumField("Profil du biseau", procSettings.bottomBevelProfile);
                bevelProfileField.name = "bevel-profile";
                bevelProfileField.RegisterValueChangedCallback(evt => {
                    procSettings.bottomBevelProfile = (BevelProfile)evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(bevelProfileField);

                var bevelInsetSlider = new UnityEngine.UIElements.Slider("Rayon du biseau", 0f, 1f);
                bevelInsetSlider.value = procSettings.bottomBevelInset;
                bevelInsetSlider.showInputField = true;
                bevelInsetSlider.name = "bevel-inset";
                bevelInsetSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomBevelInset = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(bevelInsetSlider);

                var bevelDepthSlider = new UnityEngine.UIElements.Slider("Profondeur du biseau", 0f, 2f);
                bevelDepthSlider.value = procSettings.bottomBevelDepth;
                bevelDepthSlider.showInputField = true;
                bevelDepthSlider.name = "bevel-depth";
                bevelDepthSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomBevelDepth = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(bevelDepthSlider);

                var bevelSegSlider = new UnityEngine.UIElements.SliderInt("Segments du biseau", 1, 8);
                bevelSegSlider.value = procSettings.bottomBevelSegments;
                bevelSegSlider.showInputField = true;
                bevelSegSlider.name = "bevel-segments";
                bevelSegSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomBevelSegments = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(bevelSegSlider);

                // IslandNoise settings
                var noiseScaleSlider = new UnityEngine.UIElements.Slider("Noise Scale", 0.5f, 10f);
                noiseScaleSlider.value = procSettings.bottomNoiseScale;
                noiseScaleSlider.showInputField = true;
                noiseScaleSlider.name = "noise-scale";
                noiseScaleSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomNoiseScale = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(noiseScaleSlider);

                var noiseAmpSlider = new UnityEngine.UIElements.Slider("Noise Amplitude", 0f, 10f);
                noiseAmpSlider.value = procSettings.bottomNoiseAmplitude;
                noiseAmpSlider.showInputField = true;
                noiseAmpSlider.name = "noise-amplitude";
                noiseAmpSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomNoiseAmplitude = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(noiseAmpSlider);

                var sharpnessSlider = new UnityEngine.UIElements.Slider("Island Sharpness", 0.3f, 5f);
                sharpnessSlider.value = procSettings.bottomIslandSharpness;
                sharpnessSlider.showInputField = true;
                sharpnessSlider.name = "noise-sharpness";
                sharpnessSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomIslandSharpness = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(sharpnessSlider);

                var smoothSlider = new UnityEngine.UIElements.Slider("Island Smooth", 0f, 1f);
                smoothSlider.value = procSettings.bottomIslandSmooth;
                smoothSlider.showInputField = true;
                smoothSlider.name = "noise-smooth";
                smoothSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomIslandSmooth = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(smoothSlider);

                var noiseResSlider = new UnityEngine.UIElements.SliderInt("Noise Resolution", 1, 16);
                noiseResSlider.value = procSettings.bottomNoiseResolution;
                noiseResSlider.showInputField = true;
                noiseResSlider.name = "noise-resolution";
                noiseResSlider.RegisterValueChangedCallback(evt => {
                    procSettings.bottomNoiseResolution = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(noiseResSlider);

                var noiseSeedField = new UnityEngine.UIElements.FloatField("Noise Seed");
                noiseSeedField.value = procSettings.bottomNoiseSeed;
                noiseSeedField.name = "noise-seed";
                noiseSeedField.RegisterValueChangedCallback(evt => {
                    procSettings.bottomNoiseSeed = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                bottomDetailsContainer.Add(noiseSeedField);

                proceduralContent.Add(bottomDetailsContainer);
                UpdateBottomCapVisibility(bottomDetailsContainer, procSettings.bottomMode);

                // Materials section
                var matLabel = new UnityEngine.UIElements.Label("Procedural Materials");
                matLabel.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
                matLabel.style.marginTop = 6;
                matLabel.style.marginBottom = 4;
                proceduralContent.Add(matLabel);

                var floorMatField = new UnityEditor.UIElements.ObjectField("Floor (top cap)");
                floorMatField.objectType = typeof(Material);
                floorMatField.value = tileRule.proceduralFloorMaterial;
                UnityEditor.UIElements.ObjectField skirtMatField = null;
                floorMatField.RegisterValueChangedCallback(evt => {
                    tileRule.proceduralFloorMaterial = evt.newValue as Material;
                    currentSkirtMaterialField?.SetValueWithoutNotify(GetCurrentSkirtMaterial(tileRule));
                    skirtMatField?.SetValueWithoutNotify(GetCurrentSkirtMaterial(tileRule));
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(floorMatField);

                var wallMatField = new UnityEditor.UIElements.ObjectField("Walls (sides)");
                wallMatField.objectType = typeof(Material);
                wallMatField.value = tileRule.proceduralWallMaterial;
                UnityEditor.UIElements.ObjectField bottomMatField = null;
                wallMatField.RegisterValueChangedCallback(evt => {
                    var previousWallMaterial = tileRule.proceduralWallMaterial;
                    bool bottomWasLinkedToWall = BottomCapUsesWallMaterial(tileRule, previousWallMaterial);
                    tileRule.proceduralWallMaterial = evt.newValue as Material;
                    if (bottomWasLinkedToWall)
                    {
                        tileRule.proceduralBottomMaterial = tileRule.proceduralWallMaterial;
                        bottomMatField?.SetValueWithoutNotify(tileRule.proceduralBottomMaterial);
                    }
                    currentSkirtMaterialField?.SetValueWithoutNotify(GetCurrentSkirtMaterial(tileRule));
                    skirtMatField?.SetValueWithoutNotify(GetCurrentSkirtMaterial(tileRule));
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(wallMatField);

                skirtMatField = new UnityEditor.UIElements.ObjectField("Current Skirt Material");
                skirtMatField.objectType = typeof(Material);
                skirtMatField.value = GetCurrentSkirtMaterial(tileRule);
                skirtMatField.SetEnabled(false);
                proceduralContent.Add(skirtMatField);

                bottomMatField = new UnityEditor.UIElements.ObjectField("Bottom cap");
                bottomMatField.objectType = typeof(Material);
                bottomMatField.value = tileRule.proceduralBottomMaterial;
                bottomMatField.RegisterValueChangedCallback(evt => {
                    tileRule.proceduralBottomMaterial = evt.newValue as Material;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(bottomMatField);

                var digMatField = new UnityEditor.UIElements.ObjectField("Dig Preview");
                digMatField.objectType = typeof(Material);
                digMatField.value = tileRule.proceduralDigMaterial;
                digMatField.RegisterValueChangedCallback(evt => {
                    tileRule.proceduralDigMaterial = evt.newValue as Material;
                    EditorUtility.SetDirty(tilemapEditor);
                    RebuildProceduralMeshes(tileRule);
                });
                proceduralContent.Add(digMatField);

                // Rebuild button
                var rebuildBtn = new UnityEngine.UIElements.Button(() => {
                    RebuildProceduralMeshes(tileRule);
                });
                rebuildBtn.text = "Generate / Rebuild Procedural Meshes";
                rebuildBtn.style.height = 26;
                rebuildBtn.style.marginTop = 6;
                rebuildBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0f, 0.7f, 0.9f, 1f));
                rebuildBtn.style.color = new UnityEngine.UIElements.StyleColor(UnityEngine.Color.white);
                proceduralContent.Add(rebuildBtn);
            }
            else
            {
                var customInfo = new UnityEngine.UIElements.Label("Custom mode: uses tile prefab meshes as usual.");
                customInfo.style.color = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.6f, 0.6f, 0.6f, 1f));
                customInfo.style.marginTop = 4;
                customInfo.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Italic;
                placementContent.Add(customInfo);
            }

            content.Add(placementContent);

            // === MOVE TAB CONTENT ===
            var moveToggle = new UnityEngine.UIElements.Toggle("Enable Move");
            moveToggle.value = tileRule.enableMove;
            moveToggle.RegisterValueChangedCallback(evt => {
                tileRule.enableMove = evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);
            });
            moveContent.Add(moveToggle);

            var moveOffsetField = new UnityEngine.UIElements.Vector3Field("Move Offset");
            moveOffsetField.value = tileRule.moveOffset;
            moveOffsetField.RegisterValueChangedCallback(evt => {
                tileRule.moveOffset = evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);
            });
            moveContent.Add(moveOffsetField);

            var pauseField = new UnityEngine.UIElements.FloatField("Pause (s)");
            pauseField.value = tileRule.movePause;
            pauseField.RegisterValueChangedCallback(evt => {
                tileRule.movePause = Mathf.Max(0f, evt.newValue);
                EditorUtility.SetDirty(tilemapEditor);
            });
            moveContent.Add(pauseField);

            // Preview buttons row
            var previewRow = new UnityEngine.UIElements.VisualElement();
            previewRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
            previewRow.style.marginTop = 8;

            var previewOffsetBtn = new UnityEngine.UIElements.Button(() => tilemapEditor.PreviewMoveOffsetPosition(tileRule));
            previewOffsetBtn.text = "Preview Offset";
            previewOffsetBtn.AddToClassList("btn");
            previewRow.Add(previewOffsetBtn);

            var previewAnimBtn = new UnityEngine.UIElements.Button(() => tilemapEditor.PreviewMoveAnimation(tileRule));
            previewAnimBtn.text = "Preview Animation";
            previewAnimBtn.AddToClassList("btn");
            previewRow.Add(previewAnimBtn);

            moveContent.Add(previewRow);
            content.Add(moveContent);

            // === DEFORMERS TAB CONTENT ===
            var deformersLabel = new UnityEngine.UIElements.Label("Deformer Objects");
            deformersLabel.AddToClassList("subsection-header");
            deformersContent.Add(deformersLabel);

            // Deformer objects list
            if (tileRule.deformerObjects == null) 
                tileRule.deformerObjects = new System.Collections.Generic.List<GameObject>();

            var deformersList = new UnityEngine.UIElements.VisualElement();
            deformersList.name = "deformers-list";

            for (int i = 0; i < tileRule.deformerObjects.Count; i++)
            {
                int idx = i; // Capture for closure
                var deformerRow = new UnityEngine.UIElements.VisualElement();
                deformerRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
                deformerRow.style.marginBottom = 4;

                var deformerField = new UnityEditor.UIElements.ObjectField();
                deformerField.objectType = typeof(GameObject);
                deformerField.value = tileRule.deformerObjects[idx];
                deformerField.style.flexGrow = 1;
                deformerField.RegisterValueChangedCallback(evt => {
                    tileRule.deformerObjects[idx] = evt.newValue as GameObject;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                deformerRow.Add(deformerField);

                var selectBtn = new UnityEngine.UIElements.Button(() => {
                    var go = tileRule.deformerObjects[idx];
                    if (go != null)
                    {
                        Selection.activeGameObject = go;
                        EditorGUIUtility.PingObject(go);
                    }
                });
                selectBtn.text = "Select";
                selectBtn.AddToClassList("btn");
                selectBtn.style.width = 70;
                selectBtn.SetEnabled(tileRule.deformerObjects[idx] != null);
                deformerRow.Add(selectBtn);

                var removeBtn = new UnityEngine.UIElements.Button(() => {
                    var go = tileRule.deformerObjects[idx];
                    Undo.RecordObject(tilemapEditor, "Remove Deformer Object");
                    if (go != null)
                    {
                        UnlinkHandleFromRadialHillDeformers(tileRule, go);
                        Undo.DestroyObjectImmediate(go);
                    }
                    tileRule.deformerObjects.RemoveAt(idx);
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshProceduralMeshesForLayerChange(tileRule, tileRule.isDigLayer);
                    // Refresh the UI
                    if (tileRulesUIToolkitContainer != null)
                        RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
                });
                removeBtn.text = "Remove";
                removeBtn.AddToClassList("btn");
                removeBtn.AddToClassList("btn-danger");
                removeBtn.style.width = 80;
                deformerRow.Add(removeBtn);

                deformersList.Add(deformerRow);
            }
            deformersContent.Add(deformersList);

            var addDeformerBtn = new UnityEngine.UIElements.Button(() => {
                CreateDeformerCube(tileRule);
                if (tileRulesUIToolkitContainer != null)
                    RefreshTileRulesList_UIToolkit(tileRulesUIToolkitContainer);
            });
            addDeformerBtn.text = "+ Add Deformer";
            addDeformerBtn.AddToClassList("btn");
            addDeformerBtn.AddToClassList("btn-primary");
            deformersContent.Add(addDeformerBtn);

            // ── Deformer Settings (expose key RadialHillDeformer params) ──
            var deformerForSettings = FindDeformerForRule(tileRule);
            if (deformerForSettings != null)
            {
                var settingsLabel = new UnityEngine.UIElements.Label("Deformer Settings");
                settingsLabel.AddToClassList("subsection-header");
                settingsLabel.style.marginTop = 8;
                deformersContent.Add(settingsLabel);

                var shapeField = new UnityEngine.UIElements.EnumField("Shape", deformerForSettings.shape);
                shapeField.RegisterValueChangedCallback(evt => {
                    Undo.RecordObject(deformerForSettings, "Change Deformer Shape");
                    deformerForSettings.shape = (DOTSDeformShape)evt.newValue;
                    EditorUtility.SetDirty(deformerForSettings);
                    deformerForSettings.ForceUpdate();
                });
                deformersContent.Add(shapeField);

                var radiusField = new UnityEngine.UIElements.FloatField("Radius");
                radiusField.value = deformerForSettings.radius;
                radiusField.RegisterValueChangedCallback(evt => {
                    Undo.RecordObject(deformerForSettings, "Change Deformer Radius");
                    deformerForSettings.radius = evt.newValue;
                    EditorUtility.SetDirty(deformerForSettings);
                    deformerForSettings.ForceUpdate();
                });
                deformersContent.Add(radiusField);

                var falloffField = new UnityEngine.UIElements.EnumField("Falloff", deformerForSettings.falloff);
                falloffField.RegisterValueChangedCallback(evt => {
                    Undo.RecordObject(deformerForSettings, "Change Deformer Falloff");
                    deformerForSettings.falloff = (DOTSFalloff)evt.newValue;
                    EditorUtility.SetDirty(deformerForSettings);
                    deformerForSettings.ForceUpdate();
                });
                deformersContent.Add(falloffField);

                var heightField = new UnityEngine.UIElements.FloatField("Height Per Unit Y");
                heightField.value = deformerForSettings.heightPerUnitY;
                heightField.RegisterValueChangedCallback(evt => {
                    Undo.RecordObject(deformerForSettings, "Change Deformer HeightPerUnitY");
                    deformerForSettings.heightPerUnitY = evt.newValue;
                    EditorUtility.SetDirty(deformerForSettings);
                    deformerForSettings.ForceUpdate();
                });
                deformersContent.Add(heightField);

                var ratioField = new UnityEngine.UIElements.FloatField("Y Deform Ratio");
                ratioField.value = deformerForSettings.yDeformRatio;
                ratioField.RegisterValueChangedCallback(evt => {
                    Undo.RecordObject(deformerForSettings, "Change Deformer YDeformRatio");
                    deformerForSettings.yDeformRatio = evt.newValue;
                    EditorUtility.SetDirty(deformerForSettings);
                    deformerForSettings.ForceUpdate();
                });
                deformersContent.Add(ratioField);

                var invertToggle = new UnityEngine.UIElements.Toggle("Invert Direction");
                invertToggle.value = deformerForSettings.invertDirection;
                invertToggle.RegisterValueChangedCallback(evt => {
                    Undo.RecordObject(deformerForSettings, "Change Deformer Invert");
                    deformerForSettings.invertDirection = evt.newValue;
                    EditorUtility.SetDirty(deformerForSettings);
                    deformerForSettings.ForceUpdate();
                });
                deformersContent.Add(invertToggle);
            }

            // === TEXTURE TAB CONTENT ===
            // === TEXTURE TAB CONTENT ===
            var textureHelpBox = new UnityEngine.UIElements.Label("Manage separate textures for Top (Grass), Wall (Skirt), and Bottom (Cliff). Click the preview to choose the scope.");
            textureHelpBox.style.marginBottom = 10;
            textureHelpBox.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
            textureContent.Add(textureHelpBox);

            // Find reference to skirt first to check validity (optional, for safety)
            // Removed erroneous IMGUI call
            
            Action<string, int> CreateTextureSection = (label, typeIdx) => {
                var box = new UnityEngine.UIElements.VisualElement();
                box.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.2f, 0.2f, 0.2f, 0.5f));
                box.style.paddingLeft = 5; box.style.paddingRight = 5; box.style.paddingTop = 5; box.style.paddingBottom = 5;
                box.style.marginBottom = 8;
                box.style.borderTopLeftRadius = 4;
                box.style.borderTopRightRadius = 4;
                box.style.borderBottomLeftRadius = 4;
                box.style.borderBottomRightRadius = 4;

                var lbl = new UnityEngine.UIElements.Label(label);
                lbl.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
                lbl.style.marginBottom = 4;
                box.Add(lbl);

                var row = new UnityEngine.UIElements.VisualElement();
                row.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
                row.style.alignItems = UnityEngine.UIElements.Align.Center;

                var previewColumn = new UnityEngine.UIElements.VisualElement();
                previewColumn.style.width = 88;
                previewColumn.style.marginRight = 8;
                previewColumn.style.flexShrink = 0;

                var previewButton = new UnityEngine.UIElements.Button(() => {
                    ShowTileRuleTextureScopeMenu(index, typeIdx);
                });
                previewButton.style.height = 64;
                previewButton.style.paddingLeft = 4;
                previewButton.style.paddingRight = 4;
                previewButton.style.paddingTop = 4;
                previewButton.style.paddingBottom = 4;
                previewButton.style.unityTextAlign = TextAnchor.MiddleCenter;
                previewButton.tooltip = "Current material texture. Click to choose whether to update only this rule or every rule using the same material.";

                var previewTexture = GetTileRuleSurfacePreviewTexture(tileRule, typeIdx);
                if (previewTexture != null)
                {
                    var previewImage = new UnityEngine.UIElements.Image();
                    previewImage.image = previewTexture;
                    previewImage.scaleMode = ScaleMode.ScaleToFit;
                    previewImage.style.flexGrow = 1;
                    previewImage.style.height = 56;
                    previewButton.Add(previewImage);
                }
                else
                {
                    previewButton.text = "Pick";
                }

                var previewCaption = new UnityEngine.UIElements.Label("Preview");
                previewCaption.style.unityTextAlign = TextAnchor.MiddleCenter;
                previewCaption.style.fontSize = 10;
                previewCaption.style.opacity = 0.7f;
                previewCaption.style.marginTop = 2;

                previewColumn.Add(previewButton);
                previewColumn.Add(previewCaption);
                row.Add(previewColumn);

                var actionsColumn = new UnityEngine.UIElements.VisualElement();
                actionsColumn.style.flexGrow = 1;
                actionsColumn.style.flexDirection = UnityEngine.UIElements.FlexDirection.Column;

                var scopeRow = new UnityEngine.UIElements.VisualElement();
                scopeRow.style.flexDirection = UnityEngine.UIElements.FlexDirection.Row;
                scopeRow.style.alignItems = UnityEngine.UIElements.Align.Center;

                var btnLocal = new UnityEngine.UIElements.Button(() => {
                    BeginTileRuleTexturePicker(index, typeIdx, false);
                });
                btnLocal.text = "This Rule";
                btnLocal.style.flexGrow = 1;
                btnLocal.tooltip = "Create a local material copy for this rule, then change only its texture.";
                scopeRow.Add(btnLocal);

                var btnMaster = new UnityEngine.UIElements.Button(() => {
                    BeginTileRuleTexturePicker(index, typeIdx, true);
                });
                btnMaster.text = "Same Material";
                btnMaster.style.flexGrow = 1;
                btnMaster.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new UnityEngine.Color(0.2f, 0.4f, 0.3f, 1f));
                btnMaster.tooltip = "Edit the shared material texture so every rule using this material updates too.";
                scopeRow.Add(btnMaster);

                var material = GetTileRuleSurfacePreviewMaterial(tileRule, typeIdx);

                var materialLabel = new UnityEngine.UIElements.Label(material != null ? material.name : "No material");
                materialLabel.style.fontSize = 10;
                materialLabel.style.opacity = 0.7f;
                materialLabel.style.marginTop = 4;
                materialLabel.style.unityTextAlign = TextAnchor.MiddleLeft;

                actionsColumn.Add(scopeRow);
                actionsColumn.Add(materialLabel);
                row.Add(actionsColumn);

                box.Add(row);
                textureContent.Add(box);
            };

            CreateTextureSection("🌿 Grass (Top)", 0);
            CreateTextureSection("🧱 Skirt (Wall)", 1);
            CreateTextureSection("⛰️ Cliff (Bottom)", 2);
            
            // "Update All" button just in case
            var updateAllBtn = new UnityEngine.UIElements.Button(() => {
                BeginTileRuleTexturePicker(index, 3, true);
            });
            updateAllBtn.text = "🔄 Update All Surfaces";
            updateAllBtn.tooltip = "Pick one texture and apply it to Top, Wall, and Bottom. Shared materials are updated only once.";
            updateAllBtn.style.marginTop = 10;
            textureContent.Add(updateAllBtn);

            content.Add(textureContent);
            content.Add(deformersContent);

            // Initial tab state
            updateTabVisuals();

            return content;
        }

        #endregion
    }
}
