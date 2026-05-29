using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using System.Linq;
using UnityEditorInternal;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        private class GameObjectRuleUIState
        {
            public bool expanded = false;
            public bool offsetsExpanded = false;
        }

        private void EnsureGameObjectRuleUIStateCount()
        {
            if (tilemapEditor?.gameObjectRules == null) return;

            while (gameObjectRuleUIStates.Count < tilemapEditor.gameObjectRules.Count)
                gameObjectRuleUIStates.Add(new GameObjectRuleUIState());

            while (gameObjectRuleUIStates.Count > tilemapEditor.gameObjectRules.Count)
                gameObjectRuleUIStates.RemoveAt(gameObjectRuleUIStates.Count - 1);
        }

        private void SetGameObjectRuleSettingsExpandedExclusive(int index, bool expanded, bool refreshUIToolkit = true)
        {
            if (tilemapEditor?.gameObjectRules == null || index < 0 || index >= tilemapEditor.gameObjectRules.Count)
                return;

            EnsureGameObjectRuleUIStateCount();

            for (int i = 0; i < gameObjectRuleUIStates.Count; i++)
                gameObjectRuleUIStates[i].expanded = false;

            if (expanded)
            {
                gameObjectRuleUIStates[index].expanded = true;
                tilemapEditor.selectedGameObjectRuleIndex = index;
                tilemapEditor.selectedTileRuleIndex = -1;
                tilemapEditor.selectedPathIndex = -1;
                tilemapEditor.selectedTextureRule = null;
            }

            EditorUtility.SetDirty(tilemapEditor);

            if (refreshUIToolkit && gameObjectRulesUIToolkitContainer != null)
            {
                var rulesList = gameObjectRulesUIToolkitContainer.Q<VisualElement>("gameobject-rules-list");
                if (rulesList != null)
                    RefreshGameObjectRulesList_UIToolkit(rulesList);
            }

            Repaint();
        }

        public void AddPrefabToAddressables(GameObject prefab)
        {
            // Get the asset path
            string assetPath = AssetDatabase.GetAssetPath(prefab);

            // Get the GUID of the asset
            string guid = AssetDatabase.AssetPathToGUID(assetPath);

            // Get the Addressable Asset Settings
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;

            // Ensure settings are not null
            if (settings == null)
            {
                //Debug.Log(Warning("Addressable Asset Settings are not available.");
                return;
            }

            // Find the asset entry for the prefab
            AddressableAssetEntry entry = settings.FindAssetEntry(guid);

            if (entry != null)
            {
                // If the entry already exists, log and skip adding
                //Debug.Log(($"{prefab.name} is already in Addressables. Skipping...");
                return; // Skip if already added
            }

            // If it's not already addressable, create a new entry
            entry = settings.CreateOrMoveEntry(guid, settings.DefaultGroup);
            //Debug.Log(($"Added {prefab.name} to Addressables.");

            // Mark the asset entry as addressable and add to the catalog
            if (entry != null)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryAdded, entry, true);
                AssetDatabase.SaveAssets(); // Make sure the assets are saved
            }

        }

        private void DrawGameObjectRulesSection()
        {
            EditorGUILayout.LabelField("GameObject", EditorStyles.boldLabel);

            if (tilemapEditor.gameObjectRules == null)
            {
                tilemapEditor.gameObjectRules = new List<QuickTilemapEditor.GameObjectRule>();
                EditorUtility.SetDirty(tilemapEditor);
            }

            EnsureGameObjectRuleUIStateCount();

            if (tilemapEditor.gameObjectRules.Count == 0)
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

                EditorGUILayout.LabelField("Please add a GameObject \n ↓", centeredBigLabel);

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
                return;
            }

            List<int> rulesToRemove = new List<int>();

            for (int i = 0; i < tilemapEditor.gameObjectRules.Count; i++)
            {
                if (tilemapEditor.gameObjectRules[i] == null)
                {
                    rulesToRemove.Add(i);
                    continue;
                }

                var goRule = tilemapEditor.gameObjectRules[i];
                var uiState = gameObjectRuleUIStates[i];

                bool isSelectedRule = tilemapEditor.selectedGameObjectRuleIndex == i;
                Color previousBackgroundColor = GUI.backgroundColor;
                if (isSelectedRule)
                    GUI.backgroundColor = new Color(0.8f, 1f, 0.8f, 1f);

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();

                string title = $"{i + 1}. {(goRule.prefab != null ? goRule.prefab.name : "GameObject Rule")}";
                uiState.expanded = EditorGUILayout.Foldout(uiState.expanded, title, true);

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField("Color", GUILayout.Width(42));
                var newHeaderColor = EditorGUILayout.ColorField(goRule.color, GUILayout.Width(80));
                if (newHeaderColor != goRule.color)
                    goRule.color = newHeaderColor;

                EditorGUILayout.LabelField("Height", GUILayout.Width(48));
                float newYOffset = EditorGUILayout.FloatField(goRule.yOffset, GUILayout.Width(50));
                if (!Mathf.Approximately(newYOffset, goRule.yOffset))
                {
                    Undo.RecordObject(tilemapEditor, "Change GameObject Y Offset");
                    goRule.yOffset = newYOffset;
                    ApplyGameObjectRuleVerticalSettings(goRule, i);
                }

                if (GUILayout.Button(GetVisibilityToggleIcon(goRule.isVisible), GUILayout.Width(28)))
                {
                    ToggleGameObjectRuleVisibility(goRule, i);
                }

                if (GUILayout.Button("Select", GUILayout.Width(70)))
                {
                    tilemapEditor.selectedGameObjectRuleIndex = i;
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                    tilemapEditor.selectedTextureRule = null;
                }

                bool ruleDeleted = false;
                if (GUILayout.Button("✖", GUILayout.Width(24)))
                {
                    if (EditorUtility.DisplayDialog(
                            "Delete GameObject Rule",
                            "Are you sure you want to delete this rule and all its spawned objects?",
                            "Yes", "Cancel"))
                    {
                        HandleGameObjectRulePreRemoval(i);
                        rulesToRemove.Add(i);
                        ruleDeleted = true;
                    }
                }

                EditorGUILayout.EndHorizontal();

                if (!ruleDeleted && uiState.expanded)
                {
                    EditorGUI.indentLevel++;
                    DrawGameObjectRuleContent(goRule, i, uiState);
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
                GUI.backgroundColor = previousBackgroundColor;
                EditorGUILayout.Space(4);

                if (ruleDeleted)
                    continue;
            }

            if (rulesToRemove.Count > 0)
            {
                rulesToRemove.Sort();
                rulesToRemove.Reverse();

                foreach (int index in rulesToRemove)
                {
                    if (index < 0 || index >= tilemapEditor.gameObjectRules.Count)
                        continue;

                    tilemapEditor.gameObjectRules.RemoveAt(index);
                    if (index < gameObjectRuleUIStates.Count)
                        gameObjectRuleUIStates.RemoveAt(index);

                    if (tilemapEditor.selectedGameObjectRuleIndex == index)
                        tilemapEditor.selectedGameObjectRuleIndex = -1;
                    else if (tilemapEditor.selectedGameObjectRuleIndex > index)
                        tilemapEditor.selectedGameObjectRuleIndex--;
                }

                EditorUtility.SetDirty(tilemapEditor);
            }
        }

        private void CleanUpInstanceOffsets(QuickTilemapEditor.GameObjectRule rule)
        {
            if (rule.instanceOffsets == null)
            {
                rule.instanceOffsets = new List<QuickTilemapEditor.InstanceOffset>();
                return;
            }

            // Remove any null instances
            for (int i = rule.instanceOffsets.Count - 1; i >= 0; i--)
            {
                if (rule.instanceOffsets[i].instanceObject == null)
                {
                    rule.instanceOffsets.RemoveAt(i);
                }
            }
        }

        private void ApplyGameObjectRuleVerticalSettings(QuickTilemapEditor.GameObjectRule rule, int ruleIndex)
        {
            if (tilemapEditor == null || rule == null) return;

            if (tilemapEditor.placedObjects != null)
            {
                foreach (var po in tilemapEditor.placedObjects)
                {
                    if (po != null && po.ruleIndex == ruleIndex)
                    {
                        Tilemap parentTilemap = tilemapEditor.ResolveParentTilemap(po);
                        po.instanceYOffset = tilemapEditor.GetRuleInstanceYOffsetForPlacement(rule, po.placementSurface, parentTilemap);
                        po.MarkInstanceYOffsetUpgraded();
                    }
                }
            }

            if (rule.instanceOffsets != null)
            {
                foreach (var offset in rule.instanceOffsets)
                {
                    if (offset != null)
                        offset.yOffset = rule.yOffset;
                }
            }

            Dictionary<string, QuickTilemapEditor.PlacedObject> placedLookup = null;
            if (tilemapEditor.placedObjects != null)
            {
                placedLookup = tilemapEditor.placedObjects
                    .Where(po => po != null)
                    .ToDictionary(po => po.UniqueId, po => po);
            }

            if (tilemapEditor.instantiatedGameObjects != null && placedLookup != null)
            {
                foreach (var go in tilemapEditor.instantiatedGameObjects)
                {
                    if (go == null) continue;
                    var marker = go.GetComponent<QuickTileMarker>();
                    if (marker == null || marker.RuleIndex != ruleIndex) continue;

                    if (!placedLookup.TryGetValue(marker.PlacedObjectId, out var poData))
                        continue;

                    var follower = go.GetComponent<FollowDeformedGround>();
                    bool allowFollowDeformationY = rule.followDeformationY &&
                                                   poData.placementSurface != QuickTilemapEditor.GameObjectPlacementSurface.Skirt;

                    if (allowFollowDeformationY)
                    {
                        if (follower == null)
                            go.AddComponent<FollowDeformedGround>();
                    }
                    else if (follower != null)
                    {
                        Object.DestroyImmediate(follower);
                        follower = null;
                    }

                    if (allowFollowDeformationY && follower != null && follower.enabled)
                        continue;

                    if (!tilemapEditor.TryResolvePlacedObjectWorldPosition(poData, rule, out Tilemap parentTilemap, out Vector3 worldPos))
                        continue;

                    go.transform.SetParent(parentTilemap.transform, false);
                    go.transform.position = worldPos;

                    if (rule.instanceOffsets != null)
                    {
                        var offsetEntry = rule.instanceOffsets.FirstOrDefault(entry => entry != null && entry.instanceObject == go);
                        if (offsetEntry != null)
                            offsetEntry.yOffset = poData.instanceYOffset;
                    }

                    EditorUtility.SetDirty(go);
                }
            }

            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        private void ToggleGameObjectRuleVisibility(QuickTilemapEditor.GameObjectRule rule, int ruleIndex)
        {
            if (tilemapEditor == null || rule == null) return;

            rule.isVisible = !rule.isVisible;

            if (tilemapEditor.instantiatedGameObjects != null)
            {
                foreach (var go in tilemapEditor.instantiatedGameObjects)
                {
                    if (go == null) continue;
                    var marker = go.GetComponent<QuickTileMarker>();
                    if (marker != null && marker.RuleIndex == ruleIndex)
                    {
                        go.SetActive(rule.isVisible);
                        EditorUtility.SetDirty(go);
                    }
                }
            }

            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        private void HandleGameObjectRulePreRemoval(int index)
        {
            if (tilemapEditor?.gameObjectRules == null) return;
            if (index < 0 || index >= tilemapEditor.gameObjectRules.Count) return;

            var ruleToRemove = tilemapEditor.gameObjectRules[index];
            if (ruleToRemove != null)
            {
                if (ruleToRemove.instanceOffsets != null)
                {
                    foreach (var offset in ruleToRemove.instanceOffsets)
                    {
                        if (offset?.instanceObject != null)
                            Undo.DestroyObjectImmediate(offset.instanceObject);
                    }
                }

                if (tilemapEditor.instantiatedGameObjects != null && ruleToRemove.instanceOffsets != null)
                {
                    var instanceSet = new HashSet<GameObject>(
                        ruleToRemove.instanceOffsets
                            .Where(off => off?.instanceObject != null)
                            .Select(off => off.instanceObject));
                    tilemapEditor.instantiatedGameObjects.RemoveAll(go =>
                        go == null || instanceSet.Contains(go));
                }
            }

            tilemapEditor.placedObjects?.RemoveAll(po => po.ruleIndex == index);
        }

        private void DrawGameObjectRuleContent(QuickTilemapEditor.GameObjectRule goRule, int ruleIndex, GameObjectRuleUIState uiState)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Prefab", GUILayout.Width(50));
            goRule.prefab = (GameObject)EditorGUILayout.ObjectField(goRule.prefab, typeof(GameObject), false, GUILayout.ExpandWidth(true));

            if (GUILayout.Button("Update", GUILayout.Width(75)))
            {
                tilemapEditor.UpdateGameObjectRuleOnScene(goRule, ruleIndex);
            }
            EditorGUILayout.EndHorizontal();

            var newPlacementSurface = (QuickTilemapEditor.GameObjectPlacementSurface)EditorGUILayout.EnumPopup(
                "Placement",
                goRule.placementSurface);
            if (newPlacementSurface != goRule.placementSurface)
            {
                goRule.placementSurface = newPlacementSurface;
                EditorUtility.SetDirty(tilemapEditor);
            }

            EditorGUILayout.BeginHorizontal();
            goRule.randomizeRotationY = EditorGUILayout.ToggleLeft("Randomize Rotation Y", goRule.randomizeRotationY, GUILayout.Width(170));
            if (GUILayout.Button(goRule.placeOnGround ? "Place on Ground: ON" : "Place on Ground: OFF", GUILayout.Width(180)))
                goRule.placeOnGround = !goRule.placeOnGround;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            bool newFollowDeform = EditorGUILayout.ToggleLeft(
                new GUIContent("Follow Deformation Y", "Object follows the deformed mesh height (e.g. trees on hills)"),
                goRule.followDeformationY, GUILayout.Width(170));
            if (newFollowDeform != goRule.followDeformationY)
            {
                goRule.followDeformationY = newFollowDeform;
                EditorUtility.SetDirty(tilemapEditor);
            }
            EditorGUILayout.LabelField(new GUIContent("Veg Exclusion", "Radius around this object where vegetation won't spawn"), GUILayout.Width(85));
            goRule.vegetationExclusionRadius = EditorGUILayout.FloatField(goRule.vegetationExclusionRadius, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();

            DrawInstanceOffsetList(goRule, ruleIndex, uiState);

            EditorGUILayout.EndVertical();
        }

        private void DrawInstanceOffsetList(QuickTilemapEditor.GameObjectRule goRule, int ruleIndex, GameObjectRuleUIState uiState)
        {
            if (goRule.instanceOffsets == null)
                goRule.instanceOffsets = new List<QuickTilemapEditor.InstanceOffset>();

            SerializedProperty offsetsProperty = null;

            try
            {
                SerializedProperty gameObjectRulesProperty = serializedObject.FindProperty("gameObjectRules");
                if (gameObjectRulesProperty != null && gameObjectRulesProperty.isArray && ruleIndex < gameObjectRulesProperty.arraySize)
                {
                    SerializedProperty gameObjectRuleProperty = gameObjectRulesProperty.GetArrayElementAtIndex(ruleIndex);
                    offsetsProperty = gameObjectRuleProperty?.FindPropertyRelative("instanceOffsets");
                }
            }
            catch (System.Exception)
            {
                offsetsProperty = null;
            }

            if (offsetsProperty == null)
            {
                EditorGUILayout.HelpBox("Instance offsets data is not available", MessageType.Info);
                return;
            }

            CleanUpInstanceOffsets(goRule);

            try
            {
                ReorderableList offsetsList = new ReorderableList(serializedObject, offsetsProperty, true, true, true, true);
                offsetsList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "GameObject");
                offsetsList.drawElementCallback = (rect, index, isActive, isFocused) =>
                {
                    if (index >= offsetsProperty.arraySize) return;
                    SerializedProperty element = offsetsProperty.GetArrayElementAtIndex(index);
                    if (element == null) return;

                    rect.y += 2f;

                    const float labelW = 55f;
                    const float gap = 5f;
                    float objW = rect.width * 0.55f;
                    float offsetFieldW = rect.width - objW - labelW - gap * 3f;

                    SerializedProperty instProp = element.FindPropertyRelative("instanceObject");
                    GameObject instanceReference = null;
                    if (instProp != null)
                    {
                        EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, EditorGUIUtility.singleLineHeight), "Instance");
                        EditorGUI.PropertyField(
                            new Rect(rect.x + labelW + gap, rect.y, objW - labelW - gap, EditorGUIUtility.singleLineHeight),
                            instProp,
                            GUIContent.none);
                        instanceReference = instProp.objectReferenceValue as GameObject;
                    }

                    SerializedProperty yProp = element.FindPropertyRelative("yOffset");
                    if (yProp != null)
                    {
                        EditorGUI.LabelField(new Rect(rect.x + objW + gap, rect.y, labelW, EditorGUIUtility.singleLineHeight), "Offset Y:");
                        float newY = EditorGUI.FloatField(
                            new Rect(rect.x + objW + labelW + gap * 2f, rect.y, offsetFieldW, EditorGUIUtility.singleLineHeight),
                            GUIContent.none,
                            yProp.floatValue);

                        if (!Mathf.Approximately(newY, yProp.floatValue))
                        {
                            yProp.floatValue = newY;
                            UpdatePlacedObjectYOffsetFromInstance(instanceReference, newY);
                        }
                    }
                };

                offsetsList.elementHeight = EditorGUIUtility.singleLineHeight + 4f;

                uiState.offsetsExpanded = EditorGUILayout.Foldout(uiState.offsetsExpanded, "Instance Offsets", true);
                if (uiState.offsetsExpanded)
                {
                    offsetsList.DoLayoutList();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Error displaying instance offsets: {ex.Message}");
                EditorGUILayout.HelpBox("Error displaying instance offsets. See console for details.", MessageType.Warning);
            }
        }

        private void UpdatePlacedObjectYOffsetFromInstance(GameObject instanceObject, float newYOffset)
        {
            if (instanceObject == null || tilemapEditor == null) return;

            var marker = instanceObject.GetComponent<QuickTileMarker>();
            if (marker != null && tilemapEditor.placedObjects != null)
            {
                foreach (var po in tilemapEditor.placedObjects)
                {
                    if (po != null && po.UniqueId == marker.PlacedObjectId)
                    {
                        po.instanceYOffset = newYOffset;
                        break;
                    }
                }
            }

            Vector3 localPos = instanceObject.transform.localPosition;
            localPos.y = newYOffset;
            instanceObject.transform.localPosition = localPos;

            EditorUtility.SetDirty(instanceObject);
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        #region UI Toolkit Methods - GameObjects

        public VisualElement CreateGameObjectRulesSection_UIToolkit()
        {
            var container = new VisualElement();
            container.name = "gameobject-rules-section";
            
            var styleSheet = Resources.Load<StyleSheet>("QuickTilemapEditor");
            if (styleSheet != null) container.styleSheets.Add(styleSheet);

            var header = new Label("🎮 GameObjects");
            header.AddToClassList("section-header");
            container.Add(header);

            var rulesContainer = new VisualElement();
            rulesContainer.name = "gameobject-rules-list";
            rulesContainer.AddToClassList("rules-scroll");

            var addButton = new Button(() => {
                AddGameObjectRule();
                RefreshGameObjectRulesList_UIToolkit(rulesContainer);
            });
            addButton.text = "+ Add GameObject Rule";
            addButton.AddToClassList("btn-add");
            container.Add(addButton);

            container.Add(rulesContainer);

            RefreshGameObjectRulesList_UIToolkit(rulesContainer);

            return container;
        }

        private void RefreshGameObjectRulesList_UIToolkit(VisualElement container)
        {
            container.Clear();
            EnsureGameObjectRuleUIStateCount();

            if (tilemapEditor?.gameObjectRules == null || tilemapEditor.gameObjectRules.Count == 0)
            {
                var emptyState = new VisualElement();
                emptyState.AddToClassList("empty-state");
                var emptyLabel = new Label("No GameObject rules yet.");
                emptyLabel.AddToClassList("empty-state-text");
                emptyState.Add(emptyLabel);
                container.Add(emptyState);
                return;
            }

            for (int i = 0; i < tilemapEditor.gameObjectRules.Count; i++)
            {
                var ruleCard = CreateGameObjectRuleCard_UIToolkit(i, container);
                container.Add(ruleCard);
            }
        }

        private VisualElement CreateGameObjectRuleCard_UIToolkit(int index, VisualElement listContainer)
        {
            var goRule = tilemapEditor.gameObjectRules[index];
            var uiState = gameObjectRuleUIStates[index];
            bool isSelected = tilemapEditor.selectedGameObjectRuleIndex == index;

            var card = new VisualElement();
            card.name = $"go-rule-{index}";
            card.AddToClassList("card");
            if (isSelected) card.AddToClassList("card-selected");
            card.style.borderLeftWidth = 4;
            card.style.borderLeftColor = new StyleColor(goRule.color);

            var headerRow = new VisualElement();
            headerRow.AddToClassList("card-header");
            headerRow.style.flexDirection = FlexDirection.Row;

            var colorIndicator = new VisualElement();
            colorIndicator.AddToClassList("color-indicator");
            colorIndicator.style.backgroundColor = new StyleColor(goRule.color);
            headerRow.Add(colorIndicator);

            string prefabName = goRule.prefab != null ? goRule.prefab.name : "GameObject Rule";
            var title = new Label($"{index + 1}. {prefabName}");
            title.AddToClassList("card-title");
            headerRow.Add(title);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            headerRow.Add(spacer);

            var visibilityBtn = new Button(() =>
            {
                ToggleGameObjectRuleVisibility(goRule, index);
                RefreshGameObjectRulesList_UIToolkit(listContainer);
            });
            visibilityBtn.text = GetVisibilityToggleIcon(goRule.isVisible);
            visibilityBtn.tooltip = goRule.isVisible ? "Hide spawned objects" : "Show spawned objects";
            visibilityBtn.AddToClassList("btn");
            visibilityBtn.AddToClassList("btn-icon");
            headerRow.Add(visibilityBtn);

            var settingsBtn = new Button(() =>
            {
                SetGameObjectRuleSettingsExpandedExclusive(index, !uiState.expanded);
            });
            settingsBtn.text = uiState.expanded ? "▼ Settings" : "▶ Settings";
            settingsBtn.AddToClassList("btn");
            headerRow.Add(settingsBtn);

            var selectBtn = new Button(() => {
                tilemapEditor.selectedGameObjectRuleIndex = index;
                tilemapEditor.selectedTileRuleIndex = -1;
                tilemapEditor.selectedPathIndex = -1;
                tilemapEditor.selectedTextureRule = null;
                EditorUtility.SetDirty(tilemapEditor);
                RefreshGameObjectRulesList_UIToolkit(listContainer);
            });
            selectBtn.text = "Select";
            selectBtn.AddToClassList("btn");
            if (isSelected) selectBtn.AddToClassList("btn-primary");
            headerRow.Add(selectBtn);

            var deleteBtn = new Button(() => {
                if (EditorUtility.DisplayDialog(
                    "Delete GameObject Rule",
                    $"Delete rule \"{prefabName}\" and all its spawned objects?",
                    "Yes", "Cancel"))
                {
                    HandleGameObjectRulePreRemoval(index);

                    tilemapEditor.gameObjectRules.RemoveAt(index);
                    if (index < gameObjectRuleUIStates.Count)
                        gameObjectRuleUIStates.RemoveAt(index);

                    if (tilemapEditor.selectedGameObjectRuleIndex == index)
                        tilemapEditor.selectedGameObjectRuleIndex = -1;
                    else if (tilemapEditor.selectedGameObjectRuleIndex > index)
                        tilemapEditor.selectedGameObjectRuleIndex--;

                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshGameObjectRulesList_UIToolkit(listContainer);
                }
            });
            deleteBtn.text = "✖";
            deleteBtn.AddToClassList("btn");
            deleteBtn.style.width = 28;
            deleteBtn.style.marginLeft = 4;
            deleteBtn.style.color = new StyleColor(new Color(1f, 0.4f, 0.4f, 1f));
            headerRow.Add(deleteBtn);

            card.Add(headerRow);

            var detailsContainer = new VisualElement();
            detailsContainer.style.display = uiState.expanded ? DisplayStyle.Flex : DisplayStyle.None;
            detailsContainer.style.flexDirection = FlexDirection.Column;

            var prefabRow = new VisualElement();
            prefabRow.style.flexDirection = FlexDirection.Row;
            prefabRow.style.alignItems = Align.Center;
            prefabRow.style.marginTop = 8;

            var prefabLabel = new Label("Prefab");
            prefabLabel.style.minWidth = 60;
            prefabLabel.style.marginRight = 8;
            prefabLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            prefabRow.Add(prefabLabel);

            var prefabField = new ObjectField();
            prefabField.objectType = typeof(GameObject);
            prefabField.allowSceneObjects = false;
            prefabField.value = goRule.prefab;
            prefabField.style.flexGrow = 1;
            prefabField.RegisterValueChangedCallback(evt =>
            {
                goRule.prefab = evt.newValue as GameObject;
                title.text = $"{index + 1}. {(goRule.prefab != null ? goRule.prefab.name : "GameObject Rule")}";
                EditorUtility.SetDirty(tilemapEditor);
            });
            prefabRow.Add(prefabField);

            var updateBtn = new Button(() =>
            {
                tilemapEditor.UpdateGameObjectRuleOnScene(goRule, index);
                EditorUtility.SetDirty(tilemapEditor);
            });
            updateBtn.text = "Update";
            updateBtn.AddToClassList("btn");
            updateBtn.style.marginLeft = 8;
            prefabRow.Add(updateBtn);

            detailsContainer.Add(prefabRow);

            var placementRow = new VisualElement();
            placementRow.style.flexDirection = FlexDirection.Row;
            placementRow.style.alignItems = Align.Center;
            placementRow.style.marginTop = 8;

            var placementLabel = new Label("Placement");
            placementLabel.style.minWidth = 60;
            placementLabel.style.marginRight = 8;
            placementLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            placementRow.Add(placementLabel);

            var placementField = new EnumField(goRule.placementSurface);
            placementField.style.flexGrow = 1;
            placementField.RegisterValueChangedCallback(evt =>
            {
                goRule.placementSurface = (QuickTilemapEditor.GameObjectPlacementSurface)evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);
            });
            placementRow.Add(placementField);

            detailsContainer.Add(placementRow);

            var optionsRow = new VisualElement();
            optionsRow.style.flexDirection = FlexDirection.Row;
            optionsRow.style.alignItems = Align.Center;
            optionsRow.style.marginTop = 8;

            var randomToggle = new Toggle("Random Y");
            randomToggle.value = goRule.randomizeRotationY;
            randomToggle.style.marginRight = 12;
            randomToggle.RegisterValueChangedCallback(evt =>
            {
                goRule.randomizeRotationY = evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);
            });
            optionsRow.Add(randomToggle);

            var groundToggle = new Toggle("Place On Ground");
            groundToggle.value = goRule.placeOnGround;
            groundToggle.style.marginRight = 12;
            groundToggle.RegisterValueChangedCallback(evt =>
            {
                goRule.placeOnGround = evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);
            });
            optionsRow.Add(groundToggle);

            var followToggle = new Toggle("Follow Deformation Y");
            followToggle.value = goRule.followDeformationY;
            followToggle.RegisterValueChangedCallback(evt =>
            {
                goRule.followDeformationY = evt.newValue;
                EditorUtility.SetDirty(tilemapEditor);
            });
            optionsRow.Add(followToggle);

            detailsContainer.Add(optionsRow);

            var offsetRow = new VisualElement();
            offsetRow.style.flexDirection = FlexDirection.Row;
            offsetRow.style.alignItems = Align.Center;
            offsetRow.style.marginTop = 8;

            var offsetLabel = new Label("Y Offset");
            offsetLabel.style.minWidth = 60;
            offsetLabel.style.marginRight = 8;
            offsetLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            offsetRow.Add(offsetLabel);

            var offsetField = new FloatField();
            offsetField.value = goRule.yOffset;
            offsetField.style.width = 100;
            offsetField.RegisterValueChangedCallback(evt =>
            {
                if (Mathf.Approximately(goRule.yOffset, evt.newValue))
                    return;

                Undo.RecordObject(tilemapEditor, "Change GameObject Y Offset");
                goRule.yOffset = evt.newValue;
                ApplyGameObjectRuleVerticalSettings(goRule, index);
                EditorUtility.SetDirty(tilemapEditor);
            });
            offsetRow.Add(offsetField);

            detailsContainer.Add(offsetRow);
            card.Add(detailsContainer);
            return card;
        }

        private void AddGameObjectRule()
        {
            if (tilemapEditor == null) return;

            tilemapEditor.selectedTileRuleIndex = -1;
            tilemapEditor.selectedPathIndex = -1;
            tilemapEditor.selectedTextureRule = null;

            var newRule = new QuickTilemapEditor.GameObjectRule
            {
                id = System.Guid.NewGuid().ToString(),
                color = Random.ColorHSV(0f, 1f, 0.3f, 0.5f, 0.8f, 1f),
                yOffset = 0f,
                isVisible = true
            };

            if (tilemapEditor.gameObjectRules == null)
                tilemapEditor.gameObjectRules = new List<QuickTilemapEditor.GameObjectRule>();

            tilemapEditor.gameObjectRules.Add(newRule);
            tilemapEditor.selectedGameObjectRuleIndex = tilemapEditor.gameObjectRules.Count - 1;
            
            EnsureGameObjectRuleUIStateCount();
            SetGameObjectRuleSettingsExpandedExclusive(tilemapEditor.gameObjectRules.Count - 1, true, false);
            EditorUtility.SetDirty(tilemapEditor);
        }

        #endregion
    }
}
