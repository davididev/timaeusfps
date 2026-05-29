using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace Bekkoloco
{
    public partial class QuickTilemapEditorInspector
    {
        private const string PaintShaderFinalPerfect = "BEKKOLOCO/PaintShader_FinalPerfect";
        private string lastTexturePaintUiSignature = string.Empty;
        private bool texturePaintRefreshScheduled;
        private readonly Dictionary<QuickTilemapEditor.TexturePaintRule, TexturePaintRuleDraft> texturePaintRuleDrafts =
            new Dictionary<QuickTilemapEditor.TexturePaintRule, TexturePaintRuleDraft>();

        // Preserve foldout open/close state across UI rebuilds
        private readonly Dictionary<QuickTilemapEditor.TexturePaintRule, bool> settingsFoldoutStates =
            new Dictionary<QuickTilemapEditor.TexturePaintRule, bool>();
        private readonly Dictionary<QuickTilemapEditor.TexturePaintRule, bool> vegetationFoldoutStates =
            new Dictionary<QuickTilemapEditor.TexturePaintRule, bool>();

        private sealed class TexturePaintRuleDraft
        {
            public Texture2D albedo;
            public Texture2D normal;
            public Texture2D height;
            public float textureScale;
            public float blendSharpness;
            public float noiseScale;
            public int noiseType;
            public bool removeVegetation;
            public bool isDirty;
        }

        public static void ApplyPendingTexturePaintDraftsFor(QuickTilemapEditor editor)
        {
            if (editor == null || activeInspectors.Count == 0)
                return;

            foreach (var inspector in activeInspectors.ToList())
            {
                if (inspector == null || inspector.tilemapEditor != editor)
                    continue;

                inspector.ApplyPendingTexturePaintDrafts();
            }
        }

#if UNITY_EDITOR
        private static Material[] FindPaintMaterials(string shaderName)
        {
            List<Material> list = new List<Material>();
            string[] guids = AssetDatabase.FindAssets("t:Material");
            foreach (string guid in guids)
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(
                             AssetDatabase.GUIDToAssetPath(guid));
                if (mat != null && mat.shader != null &&
                    mat.shader.name == shaderName)
                    list.Add(mat);
            }
            return list.ToArray();
        }
#endif

        /*──────────────────────────────────────────────────────────────────*
      *  Material cache & refresh
      *──────────────────────────────────────────────────────────────────*/
        private static Material[] _paintMats;     // Materials that use the paint shader
        private static string[] _paintMatNames;  // Cached names for the popup

        private const double _refreshCooldown = 0.2; // Avoid double‑click spam
        private static double _lastRefresh;
        private const float MinTextureScale = 0.1f;
        private const float MaxTextureScale = 10f;
        private const float MinBlendSharpness = 0.1f;
        private const float MaxBlendSharpness = 20f;
        private const float MinNoiseScale = 0.1f;
        private const float MaxNoiseScale = 10f;

        private static readonly string[] NoiseTypeLabels = new string[]
        {
            "Perlin",
            "Simplex",
            "Gaussian",
            "Voronoi",
            "Value",
            "White",
        };

        private static void RefreshPaintMaterials(string shaderName)
        {
            _lastRefresh = EditorApplication.timeSinceStartup;  // idem throttle
            var mats = new List<Material>();
            foreach (string guid in AssetDatabase.FindAssets("t:Material"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat?.shader?.name == shaderName)
                    mats.Add(mat);
            }
            _paintMats = mats.ToArray();
            _paintMatNames = _paintMats.Length > 0
                           ? _paintMats.Select(m => m.name).ToArray()
                           : new[] { "<none>" };
        }


        /*──────────────────────────────────────────────────────────────────*
         *  Inspector GUI – Ground Texture Painting tab
         *──────────────────────────────────────────────────────────────────*/
        private void DrawTexturePaintTab()
        {
            EditorGUILayout.LabelField("🎨 Ground Texture Painting (Beta)", EditorStyles.boldLabel);
            PruneTexturePaintRuleDrafts();
            NormalizeTexturePaintRuleIndices();
            ResolveTexturePaintRulePreviewTextures();

            // Build the cache the first time the tab is opened.
            if (_paintMats == null) RefreshPaintMaterials(PaintShaderFinalPerfect);

            // Bail out early if no texture rules exist
            if (tilemapEditor.texturePaintRules == null || tilemapEditor.texturePaintRules.Count == 0)
            {
                EditorGUILayout.HelpBox("Aucune texture disponible. Utilise le bouton '+ Add Texture' ci-dessus.",
                                        MessageType.Info);
                return;
            }

            // Main loop over texture‑paint rules
            // ──────────────────────────────────────────────────────────────
            for (int i = 0; i < tilemapEditor.texturePaintRules.Count; i++)
            {
                var rule = tilemapEditor.texturePaintRules[i];
                var draft = GetTexturePaintRuleDraft(rule);
                EditorGUILayout.BeginHorizontal(GUILayout.Height(100));

                /* Select button */
                bool isSelected = rule == tilemapEditor.selectedTextureRule;
                GUI.color = isSelected ? Color.green : Color.gray;
                if (GUILayout.Button("Select", GUILayout.Width(100), GUILayout.Height(80)))
                {
                    tilemapEditor.selectedTextureRule = isSelected ? null : rule;
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                }
                GUI.color = Color.white;

                /* Rule settings */
                EditorGUILayout.BeginVertical();

                // Preview of mix texture + material popup & Update button
                EditorGUILayout.BeginHorizontal();
                var draftAlbedo = draft.albedo;
                DrawLabeledTexture("Albedo", ref draftAlbedo);
                if (draftAlbedo != draft.albedo)
                {
                    draft.albedo = draftAlbedo;
                    draft.isDirty = true;
                }

                var draftNormal = draft.normal;
                DrawLabeledTexture("Normal", ref draftNormal);
                if (draftNormal != draft.normal)
                {
                    draft.normal = draftNormal;
                    draft.isDirty = true;
                }

                var draftHeight = draft.height;
                DrawLabeledTexture("Height", ref draftHeight);
                if (draftHeight != draft.height)
                {
                    draft.height = draftHeight;
                    draft.isDirty = true;
                }

                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField("Ground Material", GUILayout.Width(150));
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.ObjectField(
                    GUIContent.none,
                    rule.material,
                    typeof(Material),
                    false,
                    GUILayout.Width(150),
                    GUILayout.Height(20)
                );
                EditorGUI.EndDisabledGroup();


                if (GUILayout.Button("Update", GUILayout.Width(60)))
                {
                    ApplyTexturePaintRuleToMaterial(rule);
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginChangeCheck();
                float draftTextureScale = EditorGUILayout.Slider("Scale", draft.textureScale, MinTextureScale, MaxTextureScale);
                float draftBlendSharpness = EditorGUILayout.Slider("Blend", draft.blendSharpness, MinBlendSharpness, MaxBlendSharpness);
                float draftNoiseScale = EditorGUILayout.Slider("Noise Scale", draft.noiseScale, MinNoiseScale, MaxNoiseScale);
                if (EditorGUI.EndChangeCheck())
                {
                    draft.textureScale = draftTextureScale;
                    draft.blendSharpness = draftBlendSharpness;
                    draft.noiseScale = draftNoiseScale;
                    draft.isDirty = true;
                    CommitDraftAndApplyLive(rule);
                }

                int newNoiseType = EditorGUILayout.Popup("Noise Type", draft.noiseType, NoiseTypeLabels);
                if (newNoiseType != draft.noiseType)
                {
                    draft.noiseType = newNoiseType;
                    draft.isDirty = true;
                    CommitDraftAndApplyLive(rule);
                }

                bool newRemoveVegetation = EditorGUILayout.Toggle("Remove Vegetation", draft.removeVegetation);
                if (newRemoveVegetation != draft.removeVegetation)
                {
                    draft.removeVegetation = newRemoveVegetation;
                    draft.isDirty = true;
                    EditorUtility.SetDirty(tilemapEditor);
                }

                // ── Vegetation entries (IMGUI) ──
                if (rule.vegetationEntries == null)
                    rule.vegetationEntries = new List<QuickTilemapEditor.VegetationEntry>();

                EditorGUILayout.LabelField("Vegetation", EditorStyles.boldLabel);
                for (int vi = 0; vi < rule.vegetationEntries.Count; vi++)
                {
                    var entry = rule.vegetationEntries[vi];
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    // Only Prefab and Card visible (Grass hidden for now)
                    int modeIdx = entry.mode == QuickTilemapEditor.VegetationMode.Prefab ? 0 : 1;
                    modeIdx = EditorGUILayout.Popup("Mode", modeIdx, new string[] { "Prefab", "Card" });
                    entry.mode = modeIdx == 0 ? QuickTilemapEditor.VegetationMode.Prefab : QuickTilemapEditor.VegetationMode.Card;
                    entry.populateSource = (QuickTilemapEditor.VegetationPopulateSource)EditorGUILayout.EnumPopup("Populate", entry.populateSource);
                    var previousPlacementSurface = entry.placementSurface;
                    entry.placementSurface = (QuickTilemapEditor.VegetationPlacementSurface)EditorGUILayout.EnumPopup("Placement", entry.placementSurface);
                    if (entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt &&
                        previousPlacementSurface != QuickTilemapEditor.VegetationPlacementSurface.Skirt &&
                        Mathf.Approximately(entry.rotationYDegrees, 0f))
                    {
                        entry.rotationYDegrees = 180f;
                    }
                    if (entry.placementSurface != previousPlacementSurface)
                    {
                        EditorUtility.SetDirty(tilemapEditor);
                        if (entry.instances != null && entry.instances.Count > 0)
                            PopulateVegetationForRule(rule, registerUndo: false);
                    }
                    if (entry.mode == QuickTilemapEditor.VegetationMode.Card)
                    {
                        entry.cardTexture = (Texture2D)EditorGUILayout.ObjectField("Texture", entry.cardTexture, typeof(Texture2D), false);
                        entry.cardMaterial = (Material)EditorGUILayout.ObjectField("Material (optional)", entry.cardMaterial, typeof(Material), false);
                        entry.cardTint = EditorGUILayout.ColorField("Tint", entry.cardTint);
                        EditorGUILayout.BeginHorizontal();
                        entry.cardWidth = EditorGUILayout.FloatField("Width", entry.cardWidth);
                        entry.cardHeight = EditorGUILayout.FloatField("Height", entry.cardHeight);
                        EditorGUILayout.EndHorizontal();
                    }
                    else if (entry.mode == QuickTilemapEditor.VegetationMode.Grass)
                    {
                        entry.cardMaterial = (Material)EditorGUILayout.ObjectField("Material (optional)", entry.cardMaterial, typeof(Material), false);
                        entry.cardTint = EditorGUILayout.ColorField("Grass Tint", entry.cardTint);
                        EditorGUILayout.BeginHorizontal();
                        entry.cardWidth = EditorGUILayout.FloatField("Blade Width", entry.cardWidth);
                        entry.cardHeight = EditorGUILayout.FloatField("Blade Height", entry.cardHeight);
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.LabelField("GPU Indirect + Compute Culling", EditorStyles.miniLabel);
                    }
                    else if (entry.mode == QuickTilemapEditor.VegetationMode.Prefab)
                    {
                        entry.prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", entry.prefab, typeof(GameObject), false);
                    }
                    entry.density = EditorGUILayout.Slider("Density", entry.density, 0f, 30f);
                    entry.minScale = EditorGUILayout.FloatField("Min Scale", entry.minScale);
                    entry.maxScale = EditorGUILayout.FloatField("Max Scale", entry.maxScale);
                    entry.randomRotationY = EditorGUILayout.Toggle("Random Y Rot", entry.randomRotationY);
                    float previousRotationYDegrees = entry.rotationYDegrees;
                    entry.rotationYDegrees = EditorGUILayout.FloatField("Rotation Y", entry.rotationYDegrees);
                    float previousSkirtOffset = entry.skirtOffset;
                    if (entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt)
                        entry.skirtOffset = EditorGUILayout.Slider("Skirt Offset", entry.skirtOffset, 0f, 1f);
                    entry.yOffset = EditorGUILayout.FloatField("Y Offset", entry.yOffset);
                    entry.skirtOffset = Mathf.Clamp01(entry.skirtOffset);
                    if (!Mathf.Approximately(previousRotationYDegrees, entry.rotationYDegrees) ||
                        !Mathf.Approximately(previousSkirtOffset, entry.skirtOffset))
                    {
                        EditorUtility.SetDirty(tilemapEditor);
                        if (entry.instances != null && entry.instances.Count > 0)
                            PopulateVegetationForRule(rule, registerUndo: false);
                    }
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Duplicate"))
                    {
                        Undo.RecordObject(tilemapEditor, "Duplicate Vegetation Entry");
                        var clone = new QuickTilemapEditor.VegetationEntry
                        {
                            mode = entry.mode,
                            populateSource = entry.populateSource,
                            placementSurface = entry.placementSurface,
                            prefab = entry.prefab,
                            cardTexture = entry.cardTexture,
                            cardMaterial = entry.cardMaterial,
                            cardTint = entry.cardTint,
                            cardWidth = entry.cardWidth,
                            cardHeight = entry.cardHeight,
                            density = entry.density,
                            minScale = entry.minScale,
                            maxScale = entry.maxScale,
                            randomRotationY = entry.randomRotationY,
                            rotationYDegrees = entry.rotationYDegrees,
                            skirtOffset = entry.skirtOffset,
                            yOffset = entry.yOffset,
                        };
                        rule.vegetationEntries.Insert(vi + 1, clone);
                        EditorUtility.SetDirty(tilemapEditor);
                        break;
                    }
                    if (GUILayout.Button("Remove"))
                    {
                        Undo.RecordObject(tilemapEditor, "Remove Vegetation Entry");
                        rule.vegetationEntries.RemoveAt(vi);
                        EditorUtility.SetDirty(tilemapEditor);
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                }
                if (GUILayout.Button("+ Add Vegetation"))
                {
                    Undo.RecordObject(tilemapEditor, "Add Vegetation Entry");
                    rule.vegetationEntries.Add(new QuickTilemapEditor.VegetationEntry());
                    EditorUtility.SetDirty(tilemapEditor);
                }
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Populate"))
                    PopulateVegetationForRule(rule);
                if (GUILayout.Button("Clear"))
                    ClearVegetationForRule(rule);
                EditorGUILayout.EndHorizontal();

                // Shader validation
                if (rule.material != null && rule.material.shader.name != PaintShaderFinalPerfect)
                {
                    EditorGUILayout.HelpBox($"Material must use shader \"{PaintShaderFinalPerfect}\" (current: {rule.material.shader.name})",
                                            MessageType.Error);
                    if (GUILayout.Button("Clear Material"))
                        rule.material = null;
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
                GUILayout.Space(4);
            }

        }


        private void DrawLabeledTexture(string label, ref Texture2D texture)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(60));
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(60));
            texture = (Texture2D)EditorGUILayout.ObjectField(texture, typeof(Texture2D), false,
                                                              GUILayout.Width(50), GUILayout.Height(50));
            EditorGUILayout.EndVertical();
        }


        private void ApplySkirtVisuals(QuickTilemapEditor.TileRule tileRule)
        {
            UpdateTilemapYOffset(tileRule);
        }


        private void UpdateTilemapYOffset(QuickTilemapEditor.TileRule rule)
        {
            // Calculate render order based on yOffset (multiply by 100 to get an integer value)
            rule.renderOrder = Mathf.RoundToInt(rule.yOffset * 100f);

            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
            {
                Undo.RecordObject(rule.customTargetTilemap.transform, "Adjust Y Offset");
                Vector3 pos = rule.customTargetTilemap.transform.localPosition;
                // Update the Y position to reflect the new offset
                rule.customTargetTilemap.transform.localPosition = new Vector3(pos.x, rule.yOffset, pos.z);

                TilemapRenderer renderer = rule.customTargetTilemap.GetComponent<TilemapRenderer>();
                if (renderer != null)
                    renderer.sortingOrder = rule.renderOrder;

                SkirtManager[] skirtManagers = rule.customTargetTilemap.GetComponentsInChildren<SkirtManager>(true);
                foreach (var skirt in skirtManagers)
                {
                    if (rule.fixBase)
                    {
                        skirt.wallCount = Mathf.FloorToInt(rule.yOffset / skirt.WallStep);

                        skirt.scaleValue = rule.yOffset * 10f;
                    }
                    else
                    {
                        // 👉 Leave the wallCount alone if fixBase is off
                        skirt.scaleValue = 0f;
                    }

                    skirt.ApplyVisuals();
                    EditorUtility.SetDirty(skirt);
                }

            }
            else if (tilemapEditor.heightTilemaps.TryGetValue(rule.yOffset, out Tilemap tilemap))
            {
                Undo.RecordObject(tilemap.transform, "Adjust Y Offset");
                Vector3 pos = tilemap.transform.localPosition;
                tilemap.transform.localPosition = new Vector3(pos.x, rule.yOffset, pos.z);

                TilemapRenderer renderer = tilemap.GetComponent<TilemapRenderer>();
                if (renderer != null)
                    renderer.sortingOrder = rule.renderOrder;
            }

            // Refresh the scene to reflect these changes and keep deformers in sync.
            SceneView.RepaintAll();

            UpdateRadialHillDeformers(rule);
        }

        #region UI Toolkit Methods - Texture Paint

        public VisualElement CreateTexturePaintSection_UIToolkit()
        {
            var container = new VisualElement();
            container.name = "texture-paint-section";
            
            var styleSheet = Resources.Load<StyleSheet>("QuickTilemapEditor");
            if (styleSheet != null) container.styleSheets.Add(styleSheet);

            var header = new Label("🎨 Texture Paint (Beta)");
            header.AddToClassList("section-header");
            container.Add(header);

            var rulesContainer = new VisualElement();
            rulesContainer.name = "texture-rules-list";
            rulesContainer.AddToClassList("rules-scroll");

            container.Add(rulesContainer);

            EnsureTexturePaintRulesFromMaterial();
            RefreshTextureRulesList_UIToolkit(rulesContainer);

            return container;
        }

        private void RefreshTextureRulesList_UIToolkit(VisualElement container)
        {
            container.Clear();
            EnsureTexturePaintRulesFromMaterial();
            PruneTexturePaintRuleDrafts();
            NormalizeTexturePaintRuleIndices();
            ResolveTexturePaintRulePreviewTextures();

            if (tilemapEditor.texturePaintRules == null || tilemapEditor.texturePaintRules.Count == 0)
            {
                var emptyState = new VisualElement();
                emptyState.AddToClassList("empty-state");
                var emptyLabel = new Label("No paint material found in scene.");
                emptyLabel.AddToClassList("empty-state-text");
                emptyState.Add(emptyLabel);
                container.Add(emptyState);
                return;
            }

            for (int i = 0; i < tilemapEditor.texturePaintRules.Count; i++)
            {
                var ruleCard = CreateTextureRuleCard_UIToolkit(i, container);
                container.Add(ruleCard);
            }

            lastTexturePaintUiSignature = BuildTexturePaintUiSignature();
        }

        private VisualElement CreateTextureRuleCard_UIToolkit(int index, VisualElement listContainer)
        {
            var rule = tilemapEditor.texturePaintRules[index];
            var draft = GetTexturePaintRuleDraft(rule);
            bool isSelected = rule == tilemapEditor.selectedTextureRule;

            var card = new VisualElement();
            card.AddToClassList("card");
            if (isSelected) card.AddToClassList("card-selected");

            var headerRow = new VisualElement();
            headerRow.AddToClassList("card-header");
            headerRow.style.flexDirection = FlexDirection.Row;

            string name = rule.material != null ? rule.material.name : $"Texture {index + 1}";
            var title = new Label(name);
            title.AddToClassList("card-title");
            headerRow.Add(title);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            headerRow.Add(spacer);

            var selectBtn = new Button(() => {
                tilemapEditor.selectedTextureRule = isSelected ? null : rule;
                tilemapEditor.selectedTileRuleIndex = -1;
                tilemapEditor.selectedGameObjectRuleIndex = -1;
                tilemapEditor.selectedPathIndex = -1;
                EditorUtility.SetDirty(tilemapEditor);
                RefreshTextureRulesList_UIToolkit(listContainer);
            });
            selectBtn.text = "Select";
            selectBtn.AddToClassList("btn");
            if (isSelected) selectBtn.AddToClassList("btn-primary");
            headerRow.Add(selectBtn);

            var updateBtn = new Button(() => {
                ApplyTexturePaintRuleToMaterial(rule);
                RefreshTextureRulesList_UIToolkit(listContainer);
            });
            updateBtn.text = "Update";
            updateBtn.AddToClassList("btn");
            headerRow.Add(updateBtn);

            card.Add(headerRow);

            var textureRow = new VisualElement();
            textureRow.style.flexDirection = FlexDirection.Row;
            textureRow.style.alignItems = Align.Center;
            textureRow.style.marginTop = 8;

            var textureLabel = new Label("Texture");
            textureLabel.style.minWidth = 60;
            textureLabel.style.marginRight = 8;
            textureLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            textureRow.Add(textureLabel);

            // Square texture preview
            var texturePreview = new Image();
            texturePreview.style.width = 48;
            texturePreview.style.height = 48;
            texturePreview.style.marginRight = 8;
            texturePreview.style.borderLeftWidth = 1;
            texturePreview.style.borderRightWidth = 1;
            texturePreview.style.borderTopWidth = 1;
            texturePreview.style.borderBottomWidth = 1;
            texturePreview.style.borderLeftColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            texturePreview.style.borderRightColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            texturePreview.style.borderTopColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            texturePreview.style.borderBottomColor = new Color(0.3f, 0.3f, 0.3f, 1f);
            texturePreview.image = draft.albedo != null
                ? draft.albedo
                : tilemapEditor.GetResolvedTexturePaintBaseTexture2D();
            textureRow.Add(texturePreview);

            var textureField = new ObjectField();
            textureField.objectType = typeof(Texture2D);
            textureField.allowSceneObjects = false;
            textureField.value = draft.albedo;
            textureField.style.flexGrow = 1;
            textureField.RegisterValueChangedCallback(evt =>
            {
                draft.albedo = evt.newValue as Texture2D;
                draft.isDirty = true;
                texturePreview.image = draft.albedo != null
                    ? draft.albedo
                    : tilemapEditor.GetResolvedTexturePaintBaseTexture2D();
                EditorUtility.SetDirty(tilemapEditor);
            });
            textureRow.Add(textureField);

            card.Add(textureRow);

            var scaleRow = new VisualElement();
            scaleRow.style.flexDirection = FlexDirection.Row;
            scaleRow.style.alignItems = Align.Center;
            scaleRow.style.marginTop = 8;

            var scaleLabel = new Label("Scale");
            scaleLabel.style.minWidth = 60;
            scaleLabel.style.marginRight = 8;
            scaleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            scaleRow.Add(scaleLabel);

            var scaleSlider = new Slider(MinTextureScale, MaxTextureScale);
            scaleSlider.value = Mathf.Clamp(draft.textureScale, MinTextureScale, MaxTextureScale);
            scaleSlider.style.flexGrow = 1;

            var scaleField = new FloatField();
            scaleField.value = scaleSlider.value;
            scaleField.style.width = 70;
            scaleField.style.marginLeft = 8;

            scaleSlider.RegisterValueChangedCallback(evt =>
            {
                float value = Mathf.Clamp(evt.newValue, MinTextureScale, MaxTextureScale);
                draft.textureScale = value;
                draft.isDirty = true;
                scaleField.SetValueWithoutNotify(value);
                CommitDraftAndApplyLive(rule);
            });

            scaleField.RegisterValueChangedCallback(evt =>
            {
                float value = Mathf.Clamp(evt.newValue, MinTextureScale, MaxTextureScale);
                draft.textureScale = value;
                draft.isDirty = true;
                scaleSlider.SetValueWithoutNotify(value);
                scaleField.SetValueWithoutNotify(value);
                CommitDraftAndApplyLive(rule);
            });

            scaleRow.Add(scaleSlider);
            scaleRow.Add(scaleField);
            // ── Collapsible settings foldout ──
            var settingsFoldout = new Foldout();
            settingsFoldout.text = "Settings";
            settingsFoldout.value = settingsFoldoutStates.TryGetValue(rule, out bool settingsOpen) ? settingsOpen : false;
            settingsFoldout.style.marginTop = 4;
            settingsFoldout.RegisterValueChangedCallback(evt =>
            {
                if (evt.target == settingsFoldout)
                    settingsFoldoutStates[rule] = evt.newValue;
            });

            settingsFoldout.Add(scaleRow);

            var blendRow = new VisualElement();
            blendRow.style.flexDirection = FlexDirection.Row;
            blendRow.style.alignItems = Align.Center;
            blendRow.style.marginTop = 8;

            var blendLabel = new Label("Blend");
            blendLabel.style.minWidth = 60;
            blendLabel.style.marginRight = 8;
            blendLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            blendRow.Add(blendLabel);

            var blendSlider = new Slider(MinBlendSharpness, MaxBlendSharpness);
            blendSlider.value = Mathf.Clamp(draft.blendSharpness, MinBlendSharpness, MaxBlendSharpness);
            blendSlider.style.flexGrow = 1;

            var blendField = new FloatField();
            blendField.value = blendSlider.value;
            blendField.style.width = 70;
            blendField.style.marginLeft = 8;

            blendSlider.RegisterValueChangedCallback(evt =>
            {
                float value = Mathf.Clamp(evt.newValue, MinBlendSharpness, MaxBlendSharpness);
                draft.blendSharpness = value;
                draft.isDirty = true;
                blendField.SetValueWithoutNotify(value);
                CommitDraftAndApplyLive(rule);
            });

            blendField.RegisterValueChangedCallback(evt =>
            {
                float value = Mathf.Clamp(evt.newValue, MinBlendSharpness, MaxBlendSharpness);
                draft.blendSharpness = value;
                draft.isDirty = true;
                blendSlider.SetValueWithoutNotify(value);
                blendField.SetValueWithoutNotify(value);
                CommitDraftAndApplyLive(rule);
            });

            blendRow.Add(blendSlider);
            blendRow.Add(blendField);
            settingsFoldout.Add(blendRow);

            var noiseRow = new VisualElement();
            noiseRow.style.flexDirection = FlexDirection.Row;
            noiseRow.style.alignItems = Align.Center;
            noiseRow.style.marginTop = 8;

            var noiseLabel = new Label("Noise Scale");
            noiseLabel.style.minWidth = 60;
            noiseLabel.style.marginRight = 8;
            noiseLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            noiseRow.Add(noiseLabel);

            var noiseSlider = new Slider(MinNoiseScale, MaxNoiseScale);
            noiseSlider.value = Mathf.Clamp(draft.noiseScale, MinNoiseScale, MaxNoiseScale);
            noiseSlider.style.flexGrow = 1;

            var noiseField = new FloatField();
            noiseField.value = noiseSlider.value;
            noiseField.style.width = 70;
            noiseField.style.marginLeft = 8;

            noiseSlider.RegisterValueChangedCallback(evt =>
            {
                float value = Mathf.Clamp(evt.newValue, MinNoiseScale, MaxNoiseScale);
                draft.noiseScale = value;
                draft.isDirty = true;
                noiseField.SetValueWithoutNotify(value);
                CommitDraftAndApplyLive(rule);
            });

            noiseField.RegisterValueChangedCallback(evt =>
            {
                float value = Mathf.Clamp(evt.newValue, MinNoiseScale, MaxNoiseScale);
                draft.noiseScale = value;
                draft.isDirty = true;
                noiseSlider.SetValueWithoutNotify(value);
                noiseField.SetValueWithoutNotify(value);
                CommitDraftAndApplyLive(rule);
            });

            noiseRow.Add(noiseSlider);
            noiseRow.Add(noiseField);
            settingsFoldout.Add(noiseRow);

            // Noise Type dropdown
            var noiseTypeRow = new VisualElement();
            noiseTypeRow.style.flexDirection = FlexDirection.Row;
            noiseTypeRow.style.alignItems = Align.Center;
            noiseTypeRow.style.marginTop = 8;

            var noiseTypeLabel = new Label("Noise Type");
            noiseTypeLabel.style.minWidth = 60;
            noiseTypeLabel.style.marginRight = 8;
            noiseTypeLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            noiseTypeRow.Add(noiseTypeLabel);

            var noiseTypeDropdown = new PopupField<string>(
                new System.Collections.Generic.List<string>(NoiseTypeLabels),
                Mathf.Clamp(draft.noiseType, 0, NoiseTypeLabels.Length - 1));
            noiseTypeDropdown.style.flexGrow = 1;
            noiseTypeDropdown.RegisterValueChangedCallback(evt =>
            {
                draft.noiseType = System.Array.IndexOf(NoiseTypeLabels, evt.newValue);
                draft.isDirty = true;
                CommitDraftAndApplyLive(rule);
            });
            noiseTypeRow.Add(noiseTypeDropdown);
            settingsFoldout.Add(noiseTypeRow);

            var vegetationToggle = new Toggle("Remove Vegetation");
            vegetationToggle.value = draft.removeVegetation;
            vegetationToggle.style.marginTop = 8;
            vegetationToggle.RegisterValueChangedCallback(evt =>
            {
                draft.removeVegetation = evt.newValue;
                draft.isDirty = true;
                EditorUtility.SetDirty(tilemapEditor);
            });
            settingsFoldout.Add(vegetationToggle);

            card.Add(settingsFoldout);

            // ── Vegetation foldout ──
            var vegFoldout = new Foldout();
            vegFoldout.text = "Vegetation";
            vegFoldout.value = vegetationFoldoutStates.TryGetValue(rule, out bool vegOpen) ? vegOpen : false;
            vegFoldout.style.marginTop = 4;
            vegFoldout.RegisterValueChangedCallback(evt =>
            {
                if (evt.target == vegFoldout)
                    vegetationFoldoutStates[rule] = evt.newValue;
            });

            BuildVegetationEntryList_UIToolkit(vegFoldout, rule, listContainer);

            card.Add(vegFoldout);

            return card;
        }

        private void BuildVegetationEntryList_UIToolkit(VisualElement container, QuickTilemapEditor.TexturePaintRule rule, VisualElement listContainer)
        {
            container.Clear();

            // Re-add foldout label content if container is a Foldout (Clear removes children but keeps toggle)
            if (rule.vegetationEntries == null)
                rule.vegetationEntries = new List<QuickTilemapEditor.VegetationEntry>();

            for (int vi = 0; vi < rule.vegetationEntries.Count; vi++)
            {
                int capturedIndex = vi;
                var entry = rule.vegetationEntries[vi];

                var entryCard = new VisualElement();
                entryCard.style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 1f);
                entryCard.style.borderTopLeftRadius = 4;
                entryCard.style.borderTopRightRadius = 4;
                entryCard.style.borderBottomLeftRadius = 4;
                entryCard.style.borderBottomRightRadius = 4;
                entryCard.style.paddingTop = 4;
                entryCard.style.paddingBottom = 4;
                entryCard.style.paddingLeft = 6;
                entryCard.style.paddingRight = 6;
                entryCard.style.marginTop = 4;

                // Row 0: Mode selector + Remove button
                var modeRow = new VisualElement();
                modeRow.style.flexDirection = FlexDirection.Row;
                modeRow.style.alignItems = Align.Center;

                var modeLabel = new Label("Mode");
                modeLabel.style.minWidth = 50;
                modeLabel.style.marginRight = 4;
                modeRow.Add(modeLabel);

                // Only show Prefab and Card (Grass hidden for now)
                var allowedModes = new List<QuickTilemapEditor.VegetationMode>
                    { QuickTilemapEditor.VegetationMode.Prefab, QuickTilemapEditor.VegetationMode.Card };
                int currentIdx = allowedModes.IndexOf(entry.mode);
                if (currentIdx < 0) currentIdx = 1; // default to Card if current mode was Grass
                var modeField = new PopupField<QuickTilemapEditor.VegetationMode>(allowedModes, currentIdx);
                modeField.style.flexGrow = 1;

                var dupEntryBtn = new Button(() =>
                {
                    Undo.RecordObject(tilemapEditor, "Duplicate Vegetation Entry");
                    var src = rule.vegetationEntries[capturedIndex];
                    var clone = new QuickTilemapEditor.VegetationEntry
                    {
                        mode = src.mode,
                        populateSource = src.populateSource,
                        placementSurface = src.placementSurface,
                        prefab = src.prefab,
                        cardTexture = src.cardTexture,
                        cardMaterial = src.cardMaterial,
                        cardTint = src.cardTint,
                        cardWidth = src.cardWidth,
                        cardHeight = src.cardHeight,
                        density = src.density,
                        minScale = src.minScale,
                        maxScale = src.maxScale,
                        randomRotationY = src.randomRotationY,
                        rotationYDegrees = src.rotationYDegrees,
                        skirtOffset = src.skirtOffset,
                        yOffset = src.yOffset,
                    };
                    rule.vegetationEntries.Insert(capturedIndex + 1, clone);
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshTextureRulesList_UIToolkit(listContainer);
                });
                dupEntryBtn.text = "⧉";
                dupEntryBtn.tooltip = "Duplicate";
                dupEntryBtn.style.width = 24;
                dupEntryBtn.style.marginLeft = 4;

                var removeEntryBtn = new Button(() =>
                {
                    Undo.RecordObject(tilemapEditor, "Remove Vegetation Entry");
                    rule.vegetationEntries.RemoveAt(capturedIndex);
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshTextureRulesList_UIToolkit(listContainer);
                });
                removeEntryBtn.text = "✕";
                removeEntryBtn.style.width = 24;
                removeEntryBtn.style.marginLeft = 4;

                modeRow.Add(modeField);
                modeRow.Add(dupEntryBtn);
                modeRow.Add(removeEntryBtn);
                entryCard.Add(modeRow);

                var populateRow = new VisualElement();
                populateRow.style.flexDirection = FlexDirection.Row;
                populateRow.style.alignItems = Align.Center;
                populateRow.style.marginTop = 2;

                var populateLabel = new Label("Populate");
                populateLabel.style.minWidth = 50;
                populateLabel.style.marginRight = 4;
                populateRow.Add(populateLabel);

                var populateField = new EnumField(entry.populateSource);
                populateField.style.flexGrow = 1;
                populateField.RegisterValueChangedCallback(evt =>
                {
                    entry.populateSource = (QuickTilemapEditor.VegetationPopulateSource)evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                populateRow.Add(populateField);
                entryCard.Add(populateRow);

                var placementRow = new VisualElement();
                placementRow.style.flexDirection = FlexDirection.Row;
                placementRow.style.alignItems = Align.Center;
                placementRow.style.marginTop = 2;

                var placementLabel = new Label("Placement");
                placementLabel.style.minWidth = 50;
                placementLabel.style.marginRight = 4;
                placementRow.Add(placementLabel);

                var placementField = new EnumField(entry.placementSurface);
                placementField.style.flexGrow = 1;
                VisualElement skirtOffsetRow = null;
                placementField.RegisterValueChangedCallback(evt =>
                {
                    var previousPlacement = entry.placementSurface;
                    bool needsUiRefresh = false;
                    entry.placementSurface = (QuickTilemapEditor.VegetationPlacementSurface)evt.newValue;
                    if (entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt &&
                        previousPlacement != QuickTilemapEditor.VegetationPlacementSurface.Skirt &&
                        Mathf.Approximately(entry.rotationYDegrees, 0f))
                    {
                        entry.rotationYDegrees = 180f;
                        needsUiRefresh = true;
                    }
                    EditorUtility.SetDirty(tilemapEditor);
                    if (entry.instances != null && entry.instances.Count > 0)
                        PopulateVegetationForRule(rule, registerUndo: false);
                    if (skirtOffsetRow != null)
                        skirtOffsetRow.style.display = entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt
                            ? DisplayStyle.Flex
                            : DisplayStyle.None;
                    if (needsUiRefresh)
                        RefreshTextureRulesList_UIToolkit(listContainer);
                });
                placementRow.Add(placementField);
                entryCard.Add(placementRow);

                skirtOffsetRow = new VisualElement();
                skirtOffsetRow.style.flexDirection = FlexDirection.Row;
                skirtOffsetRow.style.alignItems = Align.Center;
                skirtOffsetRow.style.marginTop = 2;
                skirtOffsetRow.style.display = entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;

                var skirtOffsetLabel = new Label("Skirt Offset");
                skirtOffsetLabel.style.minWidth = 80;
                skirtOffsetLabel.style.marginRight = 4;
                skirtOffsetRow.Add(skirtOffsetLabel);

                var skirtOffsetSlider = new Slider(0f, 1f);
                skirtOffsetSlider.value = Mathf.Clamp01(entry.skirtOffset);
                skirtOffsetSlider.style.flexGrow = 1;

                var skirtOffsetField = new FloatField();
                skirtOffsetField.value = Mathf.Clamp01(entry.skirtOffset);
                skirtOffsetField.style.width = 50;
                skirtOffsetField.style.marginLeft = 4;

                skirtOffsetSlider.RegisterValueChangedCallback(evt =>
                {
                    float value = Mathf.Clamp01(evt.newValue);
                    entry.skirtOffset = value;
                    skirtOffsetField.SetValueWithoutNotify(value);
                    EditorUtility.SetDirty(tilemapEditor);
                    if (entry.instances != null && entry.instances.Count > 0)
                        PopulateVegetationForRule(rule, registerUndo: false);
                });
                skirtOffsetField.RegisterValueChangedCallback(evt =>
                {
                    float value = Mathf.Clamp01(evt.newValue);
                    entry.skirtOffset = value;
                    skirtOffsetSlider.SetValueWithoutNotify(value);
                    skirtOffsetField.SetValueWithoutNotify(value);
                    EditorUtility.SetDirty(tilemapEditor);
                    if (entry.instances != null && entry.instances.Count > 0)
                        PopulateVegetationForRule(rule, registerUndo: false);
                });

                skirtOffsetRow.Add(skirtOffsetSlider);
                skirtOffsetRow.Add(skirtOffsetField);
                entryCard.Add(skirtOffsetRow);

                // ── Card mode fields ──
                var cardFieldsContainer = new VisualElement();
                cardFieldsContainer.style.display =
                    (entry.mode == QuickTilemapEditor.VegetationMode.Card || entry.mode == QuickTilemapEditor.VegetationMode.Grass)
                    ? DisplayStyle.Flex : DisplayStyle.None;

                var texRow = new VisualElement();
                texRow.style.flexDirection = FlexDirection.Row;
                texRow.style.alignItems = Align.Center;
                texRow.style.marginTop = 2;
                texRow.style.display = entry.mode == QuickTilemapEditor.VegetationMode.Grass
                    ? DisplayStyle.None : DisplayStyle.Flex;
                var texLabel = new Label("Texture");
                texLabel.style.minWidth = 50;
                texLabel.style.marginRight = 4;
                texRow.Add(texLabel);
                var texField = new ObjectField();
                texField.objectType = typeof(Texture2D);
                texField.allowSceneObjects = false;
                texField.value = entry.cardTexture;
                texField.style.flexGrow = 1;
                texField.RegisterValueChangedCallback(evt =>
                {
                    entry.cardTexture = evt.newValue as Texture2D;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                texRow.Add(texField);
                cardFieldsContainer.Add(texRow);

                // Card material override (hidden for Grass — uses auto-generated material)
                var matRow = new VisualElement();
                matRow.style.flexDirection = FlexDirection.Row;
                matRow.style.alignItems = Align.Center;
                matRow.style.marginTop = 2;
                matRow.style.display = entry.mode == QuickTilemapEditor.VegetationMode.Grass
                    ? DisplayStyle.None : DisplayStyle.Flex;
                var matLabel = new Label("Material");
                matLabel.style.minWidth = 50;
                matLabel.style.marginRight = 4;
                matRow.Add(matLabel);
                var matField = new ObjectField();
                matField.objectType = typeof(Material);
                matField.allowSceneObjects = false;
                matField.value = entry.cardMaterial;
                matField.style.flexGrow = 1;
                matField.RegisterValueChangedCallback(evt =>
                {
                    entry.cardMaterial = evt.newValue as Material;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                matRow.Add(matField);
                cardFieldsContainer.Add(matRow);

                var tintRow = new VisualElement();
                tintRow.style.flexDirection = FlexDirection.Row;
                tintRow.style.alignItems = Align.Center;
                tintRow.style.marginTop = 2;
                var tintLabel = new Label(entry.mode == QuickTilemapEditor.VegetationMode.Grass ? "Grass Tint" : "Tint");
                tintLabel.style.minWidth = 50;
                tintLabel.style.marginRight = 4;
                tintRow.Add(tintLabel);
                var tintField = new ColorField();
                tintField.value = entry.cardTint;
                tintField.style.flexGrow = 1;
                tintField.RegisterValueChangedCallback(evt =>
                {
                    entry.cardTint = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                tintRow.Add(tintField);
                cardFieldsContainer.Add(tintRow);

                // Grass shape selector (only visible in Grass mode)
                var shapeRow = new VisualElement();
                shapeRow.style.flexDirection = FlexDirection.Row;
                shapeRow.style.alignItems = Align.Center;
                shapeRow.style.marginTop = 2;
                shapeRow.style.display = entry.mode == QuickTilemapEditor.VegetationMode.Grass
                    ? DisplayStyle.Flex : DisplayStyle.None;
                var shapeLabel = new Label("Shape");
                shapeLabel.style.minWidth = 50;
                shapeLabel.style.marginRight = 4;
                shapeRow.Add(shapeLabel);
                var shapeField = new EnumField(entry.grassShape);
                shapeField.style.flexGrow = 1;
                shapeField.RegisterValueChangedCallback(evt =>
                {
                    entry.grassShape = (QuickTilemapEditor.GrassShape)evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                shapeRow.Add(shapeField);
                cardFieldsContainer.Add(shapeRow);

                var cardSizeRow = new VisualElement();
                cardSizeRow.style.flexDirection = FlexDirection.Row;
                cardSizeRow.style.alignItems = Align.Center;
                cardSizeRow.style.marginTop = 2;
                var cwLabel = new Label("Size");
                cwLabel.style.minWidth = 50;
                cwLabel.style.marginRight = 4;
                cardSizeRow.Add(cwLabel);
                var cwField = new FloatField("W");
                cwField.value = entry.cardWidth;
                cwField.style.flexGrow = 1;
                cwField.RegisterValueChangedCallback(evt =>
                {
                    entry.cardWidth = Mathf.Max(0.01f, evt.newValue);
                    EditorUtility.SetDirty(tilemapEditor);
                });
                cardSizeRow.Add(cwField);
                var chField = new FloatField("H");
                chField.value = entry.cardHeight;
                chField.style.flexGrow = 1;
                chField.style.marginLeft = 4;
                chField.RegisterValueChangedCallback(evt =>
                {
                    entry.cardHeight = Mathf.Max(0.01f, evt.newValue);
                    EditorUtility.SetDirty(tilemapEditor);
                });
                cardSizeRow.Add(chField);
                cardFieldsContainer.Add(cardSizeRow);

                entryCard.Add(cardFieldsContainer);

                // ── Prefab mode fields ──
                var prefabFieldsContainer = new VisualElement();
                prefabFieldsContainer.style.display = entry.mode == QuickTilemapEditor.VegetationMode.Prefab
                    ? DisplayStyle.Flex : DisplayStyle.None;

                var prefabRow = new VisualElement();
                prefabRow.style.flexDirection = FlexDirection.Row;
                prefabRow.style.alignItems = Align.Center;
                prefabRow.style.marginTop = 2;
                var prefabLabel = new Label("Prefab");
                prefabLabel.style.minWidth = 50;
                prefabLabel.style.marginRight = 4;
                prefabRow.Add(prefabLabel);
                var prefabField = new ObjectField();
                prefabField.objectType = typeof(GameObject);
                prefabField.allowSceneObjects = false;
                prefabField.value = entry.prefab;
                prefabField.style.flexGrow = 1;
                prefabField.RegisterValueChangedCallback(evt =>
                {
                    entry.prefab = evt.newValue as GameObject;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                prefabRow.Add(prefabField);
                prefabFieldsContainer.Add(prefabRow);

                entryCard.Add(prefabFieldsContainer);

                // Toggle visibility when mode changes
                modeField.RegisterValueChangedCallback(evt =>
                {
                    entry.mode = (QuickTilemapEditor.VegetationMode)evt.newValue;
                    bool isCardOrGrass = entry.mode == QuickTilemapEditor.VegetationMode.Card || entry.mode == QuickTilemapEditor.VegetationMode.Grass;
                    bool isGrass = entry.mode == QuickTilemapEditor.VegetationMode.Grass;
                    cardFieldsContainer.style.display = isCardOrGrass ? DisplayStyle.Flex : DisplayStyle.None;
                    texRow.style.display = isGrass ? DisplayStyle.None : DisplayStyle.Flex;
                    matRow.style.display = isGrass ? DisplayStyle.None : DisplayStyle.Flex;
                    tintLabel.text = isGrass ? "Grass Tint" : "Tint";
                    shapeRow.style.display = isGrass ? DisplayStyle.Flex : DisplayStyle.None;
                    prefabFieldsContainer.style.display = entry.mode == QuickTilemapEditor.VegetationMode.Prefab
                        ? DisplayStyle.Flex : DisplayStyle.None;
                    EditorUtility.SetDirty(tilemapEditor);
                });

                // Row 2: Density
                var densityRow = new VisualElement();
                densityRow.style.flexDirection = FlexDirection.Row;
                densityRow.style.alignItems = Align.Center;
                densityRow.style.marginTop = 2;

                var densityLabel = new Label("Density");
                densityLabel.style.minWidth = 50;
                densityLabel.style.marginRight = 4;
                densityRow.Add(densityLabel);

                var densitySlider = new Slider(0f, 30f);
                densitySlider.value = Mathf.Clamp(entry.density, 0f, 30f);
                densitySlider.style.flexGrow = 1;

                var densityField = new FloatField();
                densityField.value = entry.density;
                densityField.style.width = 50;
                densityField.style.marginLeft = 4;

                densitySlider.RegisterValueChangedCallback(evt =>
                {
                    entry.density = evt.newValue;
                    densityField.SetValueWithoutNotify(evt.newValue);
                    EditorUtility.SetDirty(tilemapEditor);
                });
                densityField.RegisterValueChangedCallback(evt =>
                {
                    float v = Mathf.Clamp(evt.newValue, 0f, 30f);
                    entry.density = v;
                    densitySlider.SetValueWithoutNotify(v);
                    densityField.SetValueWithoutNotify(v);
                    EditorUtility.SetDirty(tilemapEditor);
                });

                densityRow.Add(densitySlider);
                densityRow.Add(densityField);
                entryCard.Add(densityRow);

                // Row 3: Scale Min / Max
                var scaleRow = new VisualElement();
                scaleRow.style.flexDirection = FlexDirection.Row;
                scaleRow.style.alignItems = Align.Center;
                scaleRow.style.marginTop = 2;

                var scaleMinLabel = new Label("Scale");
                scaleMinLabel.style.minWidth = 50;
                scaleMinLabel.style.marginRight = 4;
                scaleRow.Add(scaleMinLabel);

                var minScaleField = new FloatField("Min");
                minScaleField.value = entry.minScale;
                minScaleField.style.flexGrow = 1;
                minScaleField.RegisterValueChangedCallback(evt =>
                {
                    entry.minScale = Mathf.Max(0.01f, evt.newValue);
                    EditorUtility.SetDirty(tilemapEditor);
                });
                scaleRow.Add(minScaleField);

                var maxScaleField = new FloatField("Max");
                maxScaleField.value = entry.maxScale;
                maxScaleField.style.flexGrow = 1;
                maxScaleField.style.marginLeft = 4;
                maxScaleField.RegisterValueChangedCallback(evt =>
                {
                    entry.maxScale = Mathf.Max(0.01f, evt.newValue);
                    EditorUtility.SetDirty(tilemapEditor);
                });
                scaleRow.Add(maxScaleField);

                entryCard.Add(scaleRow);

                // Row 4: Random Rotation + Y Offset
                var optionsRow = new VisualElement();
                optionsRow.style.flexDirection = FlexDirection.Row;
                optionsRow.style.alignItems = Align.Center;
                optionsRow.style.marginTop = 2;

                var rotToggle = new Toggle("Random Y Rot");
                rotToggle.value = entry.randomRotationY;
                rotToggle.RegisterValueChangedCallback(evt =>
                {
                    entry.randomRotationY = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                optionsRow.Add(rotToggle);

                var rotationYField = new FloatField("Rotation Y");
                rotationYField.value = entry.rotationYDegrees;
                rotationYField.style.flexGrow = 1;
                rotationYField.style.marginLeft = 8;
                rotationYField.RegisterValueChangedCallback(evt =>
                {
                    entry.rotationYDegrees = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                    if (entry.instances != null && entry.instances.Count > 0)
                        PopulateVegetationForRule(rule, registerUndo: false);
                });
                optionsRow.Add(rotationYField);

                var yOffsetField = new FloatField("Y Offset");
                yOffsetField.value = entry.yOffset;
                yOffsetField.style.flexGrow = 1;
                yOffsetField.style.marginLeft = 8;
                yOffsetField.RegisterValueChangedCallback(evt =>
                {
                    entry.yOffset = evt.newValue;
                    EditorUtility.SetDirty(tilemapEditor);
                });
                optionsRow.Add(yOffsetField);

                entryCard.Add(optionsRow);
                container.Add(entryCard);
            }

            // Add entry button
            var addBtn = new Button(() =>
            {
                Undo.RecordObject(tilemapEditor, "Add Vegetation Entry");
                rule.vegetationEntries.Add(new QuickTilemapEditor.VegetationEntry());
                EditorUtility.SetDirty(tilemapEditor);
                RefreshTextureRulesList_UIToolkit(listContainer);
            });
            addBtn.text = "+ Add Vegetation";
            addBtn.style.marginTop = 4;
            container.Add(addBtn);

            // Populate / Clear buttons row
            var actionsRow = new VisualElement();
            actionsRow.style.flexDirection = FlexDirection.Row;
            actionsRow.style.marginTop = 6;

            var populateBtn = new Button(() =>
            {
                PopulateVegetationForRule(rule);
            });
            populateBtn.text = "Populate";
            populateBtn.AddToClassList("btn");
            populateBtn.style.flexGrow = 1;
            actionsRow.Add(populateBtn);

            var clearBtn = new Button(() =>
            {
                ClearVegetationForRule(rule);
            });
            clearBtn.text = "Clear";
            clearBtn.AddToClassList("btn");
            clearBtn.style.flexGrow = 1;
            clearBtn.style.marginLeft = 4;
            actionsRow.Add(clearBtn);

            container.Add(actionsRow);

            // Instance count info
            int totalInstances = 0;
            foreach (var e in rule.vegetationEntries)
                totalInstances += e.instances != null ? e.instances.Count : 0;
            if (totalInstances > 0)
            {
                var infoLabel = new Label($"GPU Instances: {totalInstances:N0}");
                infoLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
                infoLabel.style.color = new Color(0.6f, 0.8f, 0.6f, 1f);
                infoLabel.style.marginTop = 2;
                container.Add(infoLabel);
            }
        }

        private void ApplyTexturePaintRuleToMaterial(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (rule == null || tilemapEditor == null)
                return;

            CommitTexturePaintRuleDraft(rule);
            PushTexturePaintRuleValuesToMaterials(rule);

            NormalizeTexturePaintRuleIndices();

            // The Update button also refreshes the paint-mask driven preview pipeline.
            if (tilemapEditor.paintMaskTexture == null)
                tilemapEditor.CreateRenderTexture();

            tilemapEditor.RebuildPaintMaskAndMaterials();
            tilemapEditor.UpdatePaintMaskTexture();
            tilemapEditor.UpdateBlendPreviewMaterial();
            tilemapEditor.PushPaintMaskGlobals();
            tilemapEditor.CacheCurrentLevelTexturePaintRulesForSession();

            if (rule.removeVegetation)
            {
                ClearVegetationForRule(rule);
            }
            else if (HasConfiguredVegetation(rule))
            {
                PopulateVegetationForRule(rule);
            }

            if (rule.material != null)
                EditorUtility.SetDirty(rule.material);
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
            Repaint();
        }

        private void ApplyPendingTexturePaintDrafts()
        {
            if (tilemapEditor == null || texturePaintRuleDrafts.Count == 0)
                return;

            var dirtyRules = texturePaintRuleDrafts
                .Where(kvp => kvp.Key != null && kvp.Value != null && kvp.Value.isDirty)
                .Select(kvp => kvp.Key)
                .ToList();

            if (dirtyRules.Count == 0)
                return;

            foreach (var rule in dirtyRules)
            {
                CommitTexturePaintRuleDraft(rule);
                PushTexturePaintRuleValuesToMaterials(rule);

                if (rule.material != null)
                    EditorUtility.SetDirty(rule.material);
            }

            if (tilemapEditor.paintMaskTexture == null)
                tilemapEditor.CreateRenderTexture();

            tilemapEditor.RebuildPaintMaskAndMaterials();
            tilemapEditor.UpdatePaintMaskTexture();
            tilemapEditor.UpdateBlendPreviewMaterial();
            tilemapEditor.PushPaintMaskGlobals();
            tilemapEditor.CacheCurrentLevelTexturePaintRulesForSession();

            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
            Repaint();
        }

        private void PruneTexturePaintRuleDrafts()
        {
            if (texturePaintRuleDrafts.Count == 0)
                return;

            var validRules = tilemapEditor != null && tilemapEditor.texturePaintRules != null
                ? new HashSet<QuickTilemapEditor.TexturePaintRule>(tilemapEditor.texturePaintRules.Where(r => r != null))
                : new HashSet<QuickTilemapEditor.TexturePaintRule>();

            var staleRules = texturePaintRuleDrafts.Keys
                .Where(rule => rule == null || !validRules.Contains(rule))
                .ToList();

            foreach (var staleRule in staleRules)
                texturePaintRuleDrafts.Remove(staleRule);
        }

        private TexturePaintRuleDraft GetTexturePaintRuleDraft(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (rule == null)
                return null;

            if (!texturePaintRuleDrafts.TryGetValue(rule, out var draft))
            {
                draft = new TexturePaintRuleDraft();
                texturePaintRuleDrafts.Add(rule, draft);
            }

            if (!draft.isDirty)
                CopyTexturePaintRuleToDraft(rule, draft);

            return draft;
        }

        private static void CopyTexturePaintRuleToDraft(
            QuickTilemapEditor.TexturePaintRule rule,
            TexturePaintRuleDraft draft)
        {
            if (rule == null || draft == null)
                return;

            draft.albedo = rule.albedo;
            draft.normal = rule.normal;
            draft.height = rule.height;
            draft.textureScale = rule.textureScale;
            draft.blendSharpness = rule.blendSharpness;
            draft.noiseScale = rule.noiseScale;
            draft.noiseType = rule.noiseType;
            draft.removeVegetation = rule.removeVegetation;
        }

        private void CommitTexturePaintRuleDraft(QuickTilemapEditor.TexturePaintRule rule)
        {
            var draft = GetTexturePaintRuleDraft(rule);
            if (rule == null || draft == null)
                return;

            Undo.RecordObject(tilemapEditor, "Update Texture Paint Rule");
            if (rule.material != null)
                Undo.RecordObject(rule.material, "Update Texture Paint Rule");

            rule.albedo = draft.albedo;
            rule.normal = draft.normal;
            rule.height = draft.height;
            rule.textureScale = draft.textureScale;
            rule.blendSharpness = draft.blendSharpness;
            rule.noiseScale = draft.noiseScale;
            rule.noiseType = draft.noiseType;
            rule.removeVegetation = draft.removeVegetation;

            draft.isDirty = false;
            CopyTexturePaintRuleToDraft(rule, draft);
        }

        private void RemoveTexturePaintRuleDraft(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (rule != null)
                texturePaintRuleDrafts.Remove(rule);
        }

        private void ApplyTexturePaintRuleLive(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (rule == null || tilemapEditor == null)
                return;

            PushTexturePaintRuleValuesToMaterials(rule);
            tilemapEditor.UpdateBlendPreviewMaterial();

            if (rule.material != null)
                EditorUtility.SetDirty(rule.material);
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
            Repaint();
        }

        /// <summary>Commit the current draft values to the rule and push them live to the material/scene.</summary>
        private void CommitDraftAndApplyLive(QuickTilemapEditor.TexturePaintRule rule)
        {
            CommitTexturePaintRuleDraft(rule);
            ApplyTexturePaintRuleLive(rule);
        }

        private void PushTexturePaintRuleValuesToMaterials(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (rule == null || tilemapEditor == null)
                return;

            NormalizeTexturePaintRuleIndices();

            int ruleIndex = tilemapEditor.texturePaintRules != null
                ? tilemapEditor.texturePaintRules.IndexOf(rule)
                : -1;
            if (ruleIndex < 0)
                return;

            int textureSlotIndex = ruleIndex + 1;
            string textureSlotProperty = $"_Tex{textureSlotIndex}";

            var targetMaterial = ResolveTexturePaintRuleTargetMaterial(rule);
            if (targetMaterial == null)
                return;

            if (rule.material != targetMaterial)
                rule.material = targetMaterial;

            ApplyTexturePaintRuleValuesToMaterial(rule, targetMaterial, textureSlotIndex, textureSlotProperty);
        }

        private void ApplyTexturePaintRuleValuesToMaterial(
            QuickTilemapEditor.TexturePaintRule rule,
            Material material,
            int textureSlotIndex,
            string textureSlotProperty)
        {
            if (rule == null || material == null || material.shader == null || material.shader.name != PaintShaderFinalPerfect)
                return;

            if (!material.HasProperty(textureSlotProperty))
                return;

            Texture slotTexture = rule.albedo != null
                ? rule.albedo
                : tilemapEditor.GetResolvedTexturePaintBaseTexture();
            float slotScale = rule.albedo != null
                ? Mathf.Max(MinTextureScale, rule.textureScale)
                : Mathf.Max(MinTextureScale, tilemapEditor.GetResolvedTexturePaintBaseTextureScale());

            material.SetTexture(textureSlotProperty, slotTexture != null ? slotTexture : Texture2D.grayTexture);
            material.SetTextureScale(textureSlotProperty, Vector2.one * slotScale);

            string blendProperty = $"_BlendSharpness{textureSlotIndex}";
            string noiseProperty = $"_NoiseScale{textureSlotIndex}";

            if (material.HasProperty(blendProperty))
                material.SetFloat(blendProperty, Mathf.Clamp(rule.blendSharpness, MinBlendSharpness, MaxBlendSharpness));
            if (textureSlotIndex == 1 && material.HasProperty("_BlendSharpness"))
                material.SetFloat("_BlendSharpness", Mathf.Clamp(rule.blendSharpness, MinBlendSharpness, MaxBlendSharpness));

            if (material.HasProperty(noiseProperty))
                material.SetFloat(noiseProperty, Mathf.Clamp(rule.noiseScale, MinNoiseScale, MaxNoiseScale));
            if (textureSlotIndex == 1 && material.HasProperty("_NoiseScale"))
                material.SetFloat("_NoiseScale", Mathf.Clamp(rule.noiseScale, MinNoiseScale, MaxNoiseScale));

            // Push noise type → _NoiseType
            string noiseTypeProp = $"_NoiseType{textureSlotIndex}";
            if (material.HasProperty(noiseTypeProp))
                material.SetInt(noiseTypeProp, rule.noiseType);
            if (material.HasProperty("_NoiseType"))
                material.SetInt("_NoiseType", rule.noiseType);

            EditorUtility.SetDirty(material);
        }

        private void AddTexturePaintRule()
        {
            if (tilemapEditor.texturePaintRules == null)
                tilemapEditor.texturePaintRules = new List<QuickTilemapEditor.TexturePaintRule>();

            var newRule = new QuickTilemapEditor.TexturePaintRule();
            newRule.textureIndex = tilemapEditor.texturePaintRules.Count;
            newRule.ruleName = $"Texture {newRule.textureIndex + 1}";
            newRule.material = ResolveTexturePaintRuleTargetMaterial(null);
            tilemapEditor.texturePaintRules.Add(newRule);
            NormalizeTexturePaintRuleIndices();
            EditorUtility.SetDirty(tilemapEditor);
        }

        /// <summary>
        /// Auto-populate texturePaintRules from the material's _Tex1.._Tex3 slots.
        /// Creates missing rules, syncs textures, ensures exactly 3 rules.
        /// </summary>
        private void EnsureTexturePaintRulesFromMaterial()
        {
            if (tilemapEditor == null)
                return;

            if (tilemapEditor.texturePaintRules == null)
                tilemapEditor.texturePaintRules = new System.Collections.Generic.List<QuickTilemapEditor.TexturePaintRule>();

            // Find the paint material
            Material paintMat = null;
            foreach (var rule in tilemapEditor.texturePaintRules)
            {
                if (IsPaintShaderFinalPerfectMaterial(rule?.material))
                {
                    paintMat = rule.material;
                    break;
                }
            }
            if (paintMat == null)
            {
                foreach (var mat in EnumerateTexturePaintSceneMaterials())
                {
                    if (IsPaintShaderFinalPerfectMaterial(mat))
                    {
                        paintMat = mat;
                        break;
                    }
                }
            }
            if (paintMat == null)
                return;

            // Ensure we have exactly 3 rules (slots _Tex1, _Tex2, _Tex3)
            bool changed = false;
            while (tilemapEditor.texturePaintRules.Count < 3)
            {
                var newRule = new QuickTilemapEditor.TexturePaintRule();
                newRule.textureIndex = tilemapEditor.texturePaintRules.Count;
                newRule.ruleName = $"Texture {newRule.textureIndex + 1}";
                newRule.material = paintMat;
                tilemapEditor.texturePaintRules.Add(newRule);
                changed = true;
            }

            // Assign material to any rule that doesn't have one
            for (int i = 0; i < 3 && i < tilemapEditor.texturePaintRules.Count; i++)
            {
                var rule = tilemapEditor.texturePaintRules[i];
                if (rule.material == null)
                {
                    rule.material = paintMat;
                    changed = true;
                }
            }

            if (changed)
                EditorUtility.SetDirty(tilemapEditor);
        }

        private void NormalizeTexturePaintRuleIndices()
        {
            if (tilemapEditor == null || tilemapEditor.texturePaintRules == null)
                return;

            for (int i = 0; i < tilemapEditor.texturePaintRules.Count; i++)
            {
                var rule = tilemapEditor.texturePaintRules[i];
                if (rule == null)
                    continue;

                rule.textureIndex = i;
                if (string.IsNullOrWhiteSpace(rule.ruleName))
                    rule.ruleName = $"Texture {i + 1}";
            }
        }

        private void RefreshTexturePaintSectionIfNeeded_UIToolkit(bool force = false)
        {
            if (!useUIToolkit || texturePaintUIToolkitContainer == null)
                return;

            var rulesContainer = texturePaintUIToolkitContainer.Q<VisualElement>("texture-rules-list");
            if (rulesContainer == null)
                return;

            PruneTexturePaintRuleDrafts();
            NormalizeTexturePaintRuleIndices();
            ResolveTexturePaintRulePreviewTextures();

            string signature = BuildTexturePaintUiSignature();
            if (!force && signature == lastTexturePaintUiSignature)
                return;

            if (Event.current != null)
            {
                if (texturePaintRefreshScheduled)
                    return;

                texturePaintRefreshScheduled = true;
                EditorApplication.delayCall += DeferredRefreshTexturePaintSection_UIToolkit;
                return;
            }

            RefreshTextureRulesList_UIToolkit(rulesContainer);
        }

        private void DeferredRefreshTexturePaintSection_UIToolkit()
        {
            texturePaintRefreshScheduled = false;

            if (this == null || tilemapEditor == null)
                return;
            if (!useUIToolkit || texturePaintUIToolkitContainer == null)
                return;

            var rulesContainer = texturePaintUIToolkitContainer.Q<VisualElement>("texture-rules-list");
            if (rulesContainer == null)
                return;

            RefreshTextureRulesList_UIToolkit(rulesContainer);
        }

        private string BuildTexturePaintUiSignature()
        {
            if (tilemapEditor == null || tilemapEditor.texturePaintRules == null)
                return "no-texture-rules";

            var parts = new List<string> { tilemapEditor.texturePaintRules.Count.ToString() };
            for (int i = 0; i < tilemapEditor.texturePaintRules.Count; i++)
            {
                var rule = tilemapEditor.texturePaintRules[i];
                if (rule == null)
                {
                    parts.Add("null");
                    continue;
                }

                parts.Add(string.Join("|",
                    i.ToString(),
                    rule.ruleName ?? "",
                    rule.textureIndex.ToString(),
                    rule.material != null ? rule.material.GetInstanceID().ToString() : "nomat",
                    rule.albedo != null ? rule.albedo.GetInstanceID().ToString() : "noalbedo",
                    rule.textureScale.ToString("0.###"),
                    rule.blendSharpness.ToString("0.###"),
                    rule.noiseScale.ToString("0.###"),
                    rule.removeVegetation ? "veg1" : "veg0"));
            }

            return string.Join(";", parts);
        }

        private void ResolveTexturePaintRulePreviewTextures()
        {
            if (tilemapEditor == null || tilemapEditor.texturePaintRules == null)
                return;

            bool changed = false;

            for (int i = 0; i < tilemapEditor.texturePaintRules.Count; i++)
            {
                var rule = tilemapEditor.texturePaintRules[i];
                if (rule == null)
                    continue;

                var resolvedMaterial = ResolveTexturePaintRuleTargetMaterial(rule);
                if (resolvedMaterial != null && resolvedMaterial != rule.material)
                {
                    rule.material = resolvedMaterial;
                    changed = true;
                }

                if (SyncTexturePaintRuleValuesFromMaterial(rule, i))
                {
                    changed = true;
                }
                else if (!IsMeaningfulTexturePaintPreviewTexture(rule.albedo))
                {
                    var resolvedTexture = TryGetTexturePaintRulePreviewTexture(rule, i);
                    if (resolvedTexture != null && resolvedTexture != rule.albedo)
                    {
                        rule.albedo = resolvedTexture;
                        changed = true;
                    }
                }
            }

            if (changed)
                EditorUtility.SetDirty(tilemapEditor);
        }

        private bool SyncTexturePaintRuleValuesFromMaterial(QuickTilemapEditor.TexturePaintRule rule, int ruleIndex)
        {
            if (rule == null || !IsPaintShaderFinalPerfectMaterial(rule.material))
                return false;

            bool changed = false;
            int slotIndex = ruleIndex + 1;
            string textureProperty = $"_Tex{slotIndex}";
            string blendProperty = $"_BlendSharpness{slotIndex}";
            string noiseProperty = $"_NoiseScale{slotIndex}";

            // Material = save : on lit la texture DEPUIS le material.
            // Si le material n'a rien (null), on garde rule.albedo tel quel (ne pas effacer).
            var materialTexture = GetMeaningfulTexture2D(rule.material, textureProperty);
            if (materialTexture != null && materialTexture != rule.albedo)
            {
                rule.albedo = materialTexture;
                changed = true;
            }

            if (rule.material.HasProperty(textureProperty))
            {
                float textureScale = rule.material.GetTextureScale(textureProperty).x;
                if (textureScale > 0f && !Mathf.Approximately(textureScale, rule.textureScale))
                {
                    rule.textureScale = textureScale;
                    changed = true;
                }
            }

            if (rule.material.HasProperty(blendProperty))
            {
                float blendSharpness = rule.material.GetFloat(blendProperty);
                if (!Mathf.Approximately(blendSharpness, rule.blendSharpness))
                {
                    rule.blendSharpness = blendSharpness;
                    changed = true;
                }
            }
            else if (slotIndex == 1 && rule.material.HasProperty("_BlendSharpness"))
            {
                float blendSharpness = rule.material.GetFloat("_BlendSharpness");
                if (!Mathf.Approximately(blendSharpness, rule.blendSharpness))
                {
                    rule.blendSharpness = blendSharpness;
                    changed = true;
                }
            }

            if (rule.material.HasProperty(noiseProperty))
            {
                float noiseScale = rule.material.GetFloat(noiseProperty);
                if (!Mathf.Approximately(noiseScale, rule.noiseScale))
                {
                    rule.noiseScale = noiseScale;
                    changed = true;
                }
            }
            else if (slotIndex == 1 && rule.material.HasProperty("_NoiseScale"))
            {
                float noiseScale = rule.material.GetFloat("_NoiseScale");
                if (!Mathf.Approximately(noiseScale, rule.noiseScale))
                {
                    rule.noiseScale = noiseScale;
                    changed = true;
                }
            }

            // Sync noise type from material
            string noiseTypeProp = $"_NoiseType{slotIndex}";
            if (rule.material.HasProperty(noiseTypeProp))
            {
                int matNoiseType = rule.material.GetInt(noiseTypeProp);
                if (matNoiseType != rule.noiseType)
                {
                    rule.noiseType = matNoiseType;
                    changed = true;
                }
            }
            else if (rule.material.HasProperty("_NoiseType"))
            {
                int matNoiseType = rule.material.GetInt("_NoiseType");
                if (matNoiseType != rule.noiseType)
                {
                    rule.noiseType = matNoiseType;
                    changed = true;
                }
            }

            return changed;
        }

        private Material TryGetTexturePaintRulePreviewMaterial(int ruleIndex)
        {
            _ = ruleIndex;
            return ResolveTexturePaintRuleTargetMaterial(null);
        }

        private Texture2D TryGetTexturePaintRulePreviewTexture(QuickTilemapEditor.TexturePaintRule rule, int ruleIndex)
        {
            var targetMaterial = ResolveTexturePaintRuleTargetMaterial(rule);
            if (TryGetTexturePaintRulePreviewTextureFromMaterial(targetMaterial, ruleIndex, out var textureFromMaterial))
                return textureFromMaterial;

            return targetMaterial == null && IsMeaningfulTexturePaintPreviewTexture(rule?.albedo)
                ? rule.albedo
                : null;
        }

        private static bool TryGetTexturePaintRulePreviewTextureFromMaterial(Material material, int ruleIndex, out Texture2D texture)
        {
            texture = null;
            if (!IsPaintShaderFinalPerfectMaterial(material))
                return false;

            int slotIndex = ruleIndex + 1;
            string slotProperty = $"_Tex{slotIndex}";

            texture = GetMeaningfulTexture2D(material, slotProperty);
            if (texture != null)
                return true;

            if (material.HasProperty("_Tex0"))
                texture = GetMeaningfulTexture2D(material, "_Tex0");

            return texture != null;
        }

        private IEnumerable<Material> EnumerateTexturePaintPreviewMaterials()
        {
            var seenMaterials = new HashSet<int>();

            foreach (var material in EnumerateTexturePaintSceneMaterials())
            {
                if (IsPaintShaderFinalPerfectMaterial(material) &&
                    seenMaterials.Add(material.GetInstanceID()))
                {
                    yield return material;
                }
            }
        }

        private Material ResolveTexturePaintRuleTargetMaterial(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (IsPaintShaderFinalPerfectMaterial(rule?.material))
                return rule.material;

            if (tilemapEditor?.texturePaintRules != null)
            {
                foreach (var existingRule in tilemapEditor.texturePaintRules)
                {
                    if (existingRule == null || ReferenceEquals(existingRule, rule))
                        continue;

                    if (IsPaintShaderFinalPerfectMaterial(existingRule.material))
                        return existingRule.material;
                }
            }

            foreach (var material in EnumerateTexturePaintSceneMaterials())
            {
                if (IsPaintShaderFinalPerfectMaterial(material))
                    return material;
            }

            return null;
        }

        private IEnumerable<Material> EnumerateTexturePaintSceneMaterials()
        {
            if (tilemapEditor == null)
                yield break;

            var rootIds = new HashSet<int>();
            var roots = new List<Transform>();
            var seenMaterials = new HashSet<int>();

            void AddRoot(Transform root)
            {
                if (root != null && rootIds.Add(root.GetInstanceID()))
                    roots.Add(root);
            }

            AddRoot(tilemapEditor.transform);
            AddRoot(tilemapEditor.transform.parent);
            AddRoot(tilemapEditor.targetTilemap != null ? tilemapEditor.targetTilemap.transform : null);
            AddRoot(tilemapEditor.targetTilemap != null ? tilemapEditor.targetTilemap.transform.parent : null);

            foreach (var root in roots)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                        continue;

                    var sharedMaterials = renderer.sharedMaterials;
                    if (sharedMaterials == null)
                        continue;

                    foreach (var material in sharedMaterials)
                    {
                        if (IsPaintShaderFinalPerfectMaterial(material) &&
                            seenMaterials.Add(material.GetInstanceID()))
                        {
                            yield return material;
                        }
                    }
                }
            }
        }

        private static bool IsPaintShaderFinalPerfectMaterial(Material material)
        {
            return material != null &&
                   material.shader != null &&
                   material.shader.name == PaintShaderFinalPerfect;
        }

        private static bool IsMeaningfulTexturePaintPreviewTexture(Texture2D texture)
        {
            if (texture == null)
                return false;

            if (texture == Texture2D.blackTexture || texture == Texture2D.whiteTexture || texture == Texture2D.grayTexture)
                return false;

#if UNITY_EDITOR
            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (assetPath == "Resources/unity_builtin_extra" || assetPath.StartsWith("Library/"))
                return false;

            if (!assetPath.StartsWith("Assets/") && !assetPath.StartsWith("Packages/"))
                return false;
#endif

            return true;
        }

        private static Texture2D GetMeaningfulTexture2D(Material material, string propertyName)
        {
            if (material == null || string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
                return null;

            var texture = material.GetTexture(propertyName) as Texture2D;
            return IsMeaningfulTexturePaintPreviewTexture(texture) ? texture : null;
        }

        private static bool HasConfiguredVegetation(QuickTilemapEditor.TexturePaintRule rule)
        {
            if (rule?.vegetationEntries == null || rule.vegetationEntries.Count == 0)
                return false;

            foreach (var entry in rule.vegetationEntries)
            {
                if (entry == null)
                    continue;

                if (entry.mode == QuickTilemapEditor.VegetationMode.Prefab && entry.prefab != null)
                    return true;

                if (entry.mode == QuickTilemapEditor.VegetationMode.Card && entry.cardTexture != null)
                    return true;

                if (entry.mode == QuickTilemapEditor.VegetationMode.Grass)
                    return true;
            }

            return false;
        }

        private const float VegetationEdgePadding = 0.18f;
        private static readonly Vector3Int[] VegetationSkirtDirections =
        {
            Vector3Int.right,
            Vector3Int.left,
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0)
        };

        /*──────────────────────────────────────────────────────────────────*
         *  Vegetation spawning (GPU Instanced)
         *──────────────────────────────────────────────────────────────────*/

        /// <summary>
        /// Generate GPU instance data for the cells targeted by each vegetation entry.
        /// No GameObjects are created — rendering is handled by VegetationGPURenderer.
        /// </summary>
        private void PopulateVegetationForRule(
            QuickTilemapEditor.TexturePaintRule rule,
            bool refreshGpuRenderer = true,
            bool registerUndo = true)
        {
            if (tilemapEditor == null || rule == null)
                return;

            if (rule.vegetationEntries == null || rule.vegetationEntries.Count == 0)
            {
                Debug.LogWarning("No vegetation entries configured for this rule.");
                return;
            }

            int ruleIndex = tilemapEditor.texturePaintRules.IndexOf(rule);
            if (ruleIndex < 0)
                return;

            if (registerUndo)
                Undo.RecordObject(tilemapEditor, "Populate Vegetation (GPU)");

            // Clear existing instances
            foreach (var entry in rule.vegetationEntries)
            {
                if (entry?.instances != null)
                    entry.instances.Clear();
            }

            int totalInstances = 0;
            int skippedWithoutGround = 0;
            int skippedInsideDig = 0;
            var uniqueSourceCells = new HashSet<Vector3Int>();

            // Collect active dig volumes to filter placement
            var digVolumes = Object.FindObjectsByType<QuickTileDigVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            // Collect placed GameObject exclusion zones (position XZ + radius²)
            var goExclusions = new List<(Vector3 pos, float radiusSq)>();
            if (tilemapEditor.gameObjectRules != null && tilemapEditor.instantiatedGameObjects != null)
            {
                foreach (var go in tilemapEditor.instantiatedGameObjects)
                {
                    if (go == null) continue;
                    var marker = go.GetComponent<QuickTileMarker>();
                    if (marker == null) continue;
                    int ri = marker.RuleIndex;
                    if (ri < 0 || ri >= tilemapEditor.gameObjectRules.Count) continue;
                    var goRule = tilemapEditor.gameObjectRules[ri];
                    if (goRule == null || goRule.vegetationExclusionRadius <= 0f) continue;
                    float r = goRule.vegetationExclusionRadius;
                    goExclusions.Add((go.transform.position, r * r));
                }
            }

            // Pre-build readable albedo copies for ALL rules (keyed by ruleIndex)
            var readableAlbedoByRule = new Dictionary<int, Texture2D>();
            var flatColorByRule = new Dictionary<int, Color>();
            for (int ri = 0; ri < tilemapEditor.texturePaintRules.Count; ri++)
            {
                var r = tilemapEditor.texturePaintRules[ri];
                if (r == null) continue;
                if (r.albedo != null)
                {
                    if (r.albedo.isReadable)
                    {
                        readableAlbedoByRule[ri] = r.albedo;
                    }
                    else
                    {
                        RenderTexture tmp = RenderTexture.GetTemporary(r.albedo.width, r.albedo.height, 0, RenderTextureFormat.ARGB32);
                        Graphics.Blit(r.albedo, tmp);
                        RenderTexture prev = RenderTexture.active;
                        RenderTexture.active = tmp;
                        var readable = new Texture2D(r.albedo.width, r.albedo.height, TextureFormat.RGBA32, false);
                        readable.ReadPixels(new Rect(0, 0, r.albedo.width, r.albedo.height), 0, 0);
                        readable.Apply();
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(tmp);
                        readableAlbedoByRule[ri] = readable;
                    }
                }
                else if (r.material != null)
                {
                    Color c = Color.white;
                    if (r.material.HasProperty("_BaseColor"))
                        c = r.material.GetColor("_BaseColor");
                    else if (r.material.HasProperty("_Color"))
                        c = r.material.GetColor("_Color");
                    else
                        c = r.material.color;
                    flatColorByRule[ri] = c;
                }
            }

            foreach (var entry in rule.vegetationEntries)
            {
                if (!IsConfiguredVegetationEntry(entry))
                    continue;

                var sourceCells = CollectVegetationSourceCells(rule, ruleIndex, entry);
                uniqueSourceCells.UnionWith(sourceCells);
                int entryInstancesBefore = entry.instances?.Count ?? 0;
                var entrySource = ResolveVegetationPopulateSource(rule, entry);

                foreach (var cell in sourceCells)
                {
                    var populateSource = ResolveVegetationPopulateSource(rule, entry);
                    Vector3 cellCenter = GetPopulateCellCenterWorld(cell, populateSource);
                    // density < 1 → probability of spawning one instance per cell
                    // density >= 1 → spawn that many instances per cell
                    int count;
                    if (entry.density < 1f)
                    {
                        count = Random.value < entry.density ? 1 : 0;
                    }
                    else
                    {
                        count = Mathf.Max(1, Mathf.RoundToInt(entry.density));
                    }
                    for (int n = 0; n < count; n++)
                    {
                        float ox = Random.Range(-0.5f, 0.5f);
                        float oz = Random.Range(-0.5f, 0.5f);
                        ApplyVegetationEdgePadding(cell, sourceCells, ref ox, ref oz);

                        if (!TryResolveVegetationPlacementPosition(
                                cell,
                                cellCenter,
                                ox,
                                oz,
                                entry.yOffset,
                                entry.skirtOffset,
                                entry.placementSurface,
                                populateSource,
                                out Vector3 pos,
                                out float placementRotation))
                        {
                            skippedWithoutGround++;
                            continue;
                        }

                        // Skip instances that fall inside a dig volume
                        if (VegetationGPURenderer.IsInsideAnyDigVolume(pos, digVolumes))
                        {
                            skippedInsideDig++;
                            continue;
                        }

                        // Skip instances too close to placed GameObjects
                        bool tooCloseToGO = false;
                        foreach (var (goPos, rSq) in goExclusions)
                        {
                            float dx = pos.x - goPos.x;
                            float dz = pos.z - goPos.z;
                            if (dx * dx + dz * dz < rSq)
                            {
                                tooCloseToGO = true;
                                break;
                            }
                        }
                        if (tooCloseToGO) continue;

                        float rotationOffset = entry.rotationYDegrees * Mathf.Deg2Rad;
                        float rot = entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt
                            ? placementRotation + rotationOffset
                            : ((entry.randomRotationY ? Random.Range(0f, Mathf.PI * 2f) : 0f) + rotationOffset);
                        float scale = Random.Range(entry.minScale, entry.maxScale);

                        // Sample ground color for grass base-to-tip gradient
                        uint packedGround = 0xFFFFFFFF; // default white
                        if (entry.mode == QuickTilemapEditor.VegetationMode.Grass)
                        {
                            Color groundCol = Color.white;
                            // Use texturePaintMask (cell → ruleIndex) — same coordinate space as sourceCells
                            int groundRuleIdx = -1;
                            if (tilemapEditor.texturePaintMask != null &&
                                tilemapEditor.texturePaintMask.TryGetValue(cell, out int maskIdx))
                            {
                                groundRuleIdx = maskIdx;
                            }

                            if (groundRuleIdx >= 0)
                            {
                                if (readableAlbedoByRule.TryGetValue(groundRuleIdx, out Texture2D groundTex))
                                {
                                    var groundRule = tilemapEditor.texturePaintRules[groundRuleIdx];
                                    float tScale = Mathf.Max(groundRule?.textureScale ?? 1f, 0.001f);
                                    float u = (pos.x / tScale) % 1f; if (u < 0f) u += 1f;
                                    float v = (pos.z / tScale) % 1f; if (v < 0f) v += 1f;
                                    groundCol = groundTex.GetPixelBilinear(u, v);
                                }
                                else if (flatColorByRule.TryGetValue(groundRuleIdx, out Color flatCol))
                                {
                                    groundCol = flatCol;
                                }
                            }
                            else
                            {
                                // Unpainted cell — use the base texture if available (rule index 0)
                                if (readableAlbedoByRule.Count > 0)
                                {
                                    // Try current rule first, then rule 0
                                    int fallbackIdx = readableAlbedoByRule.ContainsKey(ruleIndex) ? ruleIndex : 0;
                                    if (readableAlbedoByRule.TryGetValue(fallbackIdx, out Texture2D fallbackTex))
                                    {
                                        var fbRule = tilemapEditor.texturePaintRules[fallbackIdx];
                                        float tScale = Mathf.Max(fbRule?.textureScale ?? 1f, 0.001f);
                                        float u = (pos.x / tScale) % 1f; if (u < 0f) u += 1f;
                                        float v = (pos.z / tScale) % 1f; if (v < 0f) v += 1f;
                                        groundCol = fallbackTex.GetPixelBilinear(u, v);
                                    }
                                }
                            }


                            packedGround = QuickTilemapEditor.VegetationInstanceData.PackColor(groundCol);
                        }

                        entry.instances.Add(new QuickTilemapEditor.VegetationInstanceData
                        {
                            position = pos,
                            rotation = rot,
                            scale = scale,
                            packedGroundColor = packedGround,
                        });
                        totalInstances++;
                    }
                }
            }

            // Clean up temporary readable texture copies
            foreach (var kvp in readableAlbedoByRule)
            {
                if (kvp.Key >= 0 && kvp.Key < tilemapEditor.texturePaintRules.Count)
                {
                    var origRule = tilemapEditor.texturePaintRules[kvp.Key];
                    if (origRule?.albedo != null && kvp.Value != origRule.albedo)
                        Object.DestroyImmediate(kvp.Value);
                }
            }

            // Push to GPU renderer
            if (refreshGpuRenderer)
                RefreshVegetationGPURenderer();

            EditorUtility.SetDirty(tilemapEditor);
            if (refreshGpuRenderer)
                SceneView.RepaintAll();
            string skippedSuffix = "";
            if (skippedWithoutGround > 0)
                skippedSuffix += $" ({skippedWithoutGround} skipped without ground hit)";
            if (skippedInsideDig > 0)
                skippedSuffix += $" ({skippedInsideDig} skipped inside dig volumes)";
        }

        private static bool IsConfiguredVegetationEntry(QuickTilemapEditor.VegetationEntry entry)
        {
            if (entry == null)
                return false;

            if (entry.mode == QuickTilemapEditor.VegetationMode.Prefab)
                return entry.prefab != null;

            if (entry.mode == QuickTilemapEditor.VegetationMode.Card)
                return entry.cardTexture != null || entry.cardMaterial != null;

            if (entry.mode == QuickTilemapEditor.VegetationMode.Grass)
                return true;  // Grass only needs a color — no texture or material required

            return false;
        }

        private QuickTilemapEditor.VegetationPopulateSource ResolveVegetationPopulateSource(
            QuickTilemapEditor.TexturePaintRule rule,
            QuickTilemapEditor.VegetationEntry entry)
        {
            if (entry == null)
                return QuickTilemapEditor.VegetationPopulateSource.PaintedCells;

            if (entry.populateSource != QuickTilemapEditor.VegetationPopulateSource.Auto)
                return entry.populateSource;

            // If the rule has no albedo, it acts as a base-texture stand-in → unpainted ground.
            if (rule != null && rule.albedo == null)
                return QuickTilemapEditor.VegetationPopulateSource.UnpaintedGround;

            // If the rule has an albedo but zero painted cells in the mask,
            // it represents the main/base texture → use unpainted ground cells.
            if (rule != null && tilemapEditor?.texturePaintMask != null)
            {
                int ruleIndex = tilemapEditor.texturePaintRules.IndexOf(rule);
                if (ruleIndex >= 0)
                {
                    bool hasPaintedCells = false;
                    foreach (var kvp in tilemapEditor.texturePaintMask)
                    {
                        if (kvp.Value == ruleIndex)
                        {
                            hasPaintedCells = true;
                            break;
                        }
                    }
                    if (!hasPaintedCells)
                        return QuickTilemapEditor.VegetationPopulateSource.UnpaintedGround;
                }
            }

            return QuickTilemapEditor.VegetationPopulateSource.PaintedCells;
        }

        private HashSet<Vector3Int> CollectVegetationSourceCells(
            QuickTilemapEditor.TexturePaintRule rule,
            int ruleIndex,
            QuickTilemapEditor.VegetationEntry entry)
        {
            var source = ResolveVegetationPopulateSource(rule, entry);
            if (source == QuickTilemapEditor.VegetationPopulateSource.PaintedCells)
                return CollectPaintedVegetationCells(ruleIndex);

            if (source == QuickTilemapEditor.VegetationPopulateSource.UnpaintedGround)
                return CollectUnpaintedOccupiedCells();

            HashSet<Vector3Int> cells = CollectGroundDomainCells();
            if (source == QuickTilemapEditor.VegetationPopulateSource.WholeGround)
                return cells;

            if (tilemapEditor?.texturePaintMask == null || tilemapEditor.texturePaintMask.Count == 0)
                return cells;

            cells.RemoveWhere(cell => tilemapEditor.texturePaintMask.ContainsKey(cell));
            return cells;
        }

        /// <summary>
        /// Collects cells that have actual tiles in tilemaps or procedural surfaces
        /// but are NOT claimed by any texture paint rule in the mask.
        /// This is the correct domain for the main/base texture vegetation.
        /// </summary>
        private HashSet<Vector3Int> CollectUnpaintedOccupiedCells()
        {
            var cells = new HashSet<Vector3Int>();
            if (tilemapEditor == null)
                return cells;

            // Only include cells that actually have tiles (real ground)
            foreach (var cell in EnumerateOccupiedGroundCells())
                cells.Add(cell);

            foreach (var cell in EnumerateProceduralSurfaceCells())
                cells.Add(cell);

            // Remove cells that are painted with any texture rule
            if (tilemapEditor.texturePaintMask != null && tilemapEditor.texturePaintMask.Count > 0)
                cells.RemoveWhere(cell => tilemapEditor.texturePaintMask.ContainsKey(cell));

            return cells;
        }

        private HashSet<Vector3Int> CollectPaintedVegetationCells(int ruleIndex)
        {
            var cells = new HashSet<Vector3Int>();
            if (tilemapEditor?.texturePaintMask == null)
                return cells;

            foreach (var kvp in tilemapEditor.texturePaintMask)
            {
                if (kvp.Value == ruleIndex)
                    cells.Add(kvp.Key);
            }

            return cells;
        }

        private HashSet<Vector3Int> CollectGroundDomainCells()
        {
            var cells = new HashSet<Vector3Int>();
            if (tilemapEditor == null)
                return cells;

            foreach (var cell in EnumerateOccupiedGroundCells())
                cells.Add(cell);

            foreach (var cell in EnumerateProceduralSurfaceCells())
                cells.Add(cell);

            if (tilemapEditor.texturePaintMask != null)
            {
                foreach (var kvp in tilemapEditor.texturePaintMask)
                    cells.Add(kvp.Key);
            }

            if (TryGetKnownLevelCellBounds(out BoundsInt bounds))
            {
                foreach (var rawCell in bounds.allPositionsWithin)
                {
                    var cell = new Vector3Int(rawCell.x, rawCell.y, 0);
                    if (cells.Contains(cell))
                        continue;

                    if (HasGroundTileAtCell(cell) || HasGroundHitAtCell(cell))
                        cells.Add(cell);
                }
            }

            if (cells.Count == 0)
            {
                foreach (var cell in EnumerateFallbackGridCells())
                {
                    if (HasGroundHitAtCell(cell))
                        cells.Add(cell);
                }
            }

            return cells;
        }

        private IEnumerable<Vector3Int> EnumerateOccupiedGroundCells()
        {
            if (tilemapEditor == null)
                yield break;

            var seen = new HashSet<Vector3Int>();

            foreach (var cell in EnumerateTilemapCells(tilemapEditor.targetTilemap, seen))
                yield return cell;

            foreach (var tilemap in tilemapEditor.GetAllCustomTilemaps())
            {
                foreach (var cell in EnumerateTilemapCells(tilemap, seen))
                    yield return cell;
            }
        }

        private IEnumerable<Vector3Int> EnumerateProceduralSurfaceCells()
        {
            if (tilemapEditor == null)
                yield break;

            var seen = new HashSet<Vector3Int>();
            var renderers = tilemapEditor.GetComponentsInChildren<ProceduralTileRenderer>(true);
            foreach (var proceduralRenderer in renderers)
            {
                if (proceduralRenderer == null || proceduralRenderer.actsAsDigLayer)
                    continue;

                Mesh mesh = proceduralRenderer.GetCombinedMesh();
                if (mesh == null || mesh.subMeshCount == 0 || mesh.vertexCount == 0)
                    continue;

                Tilemap sourceTilemap = proceduralRenderer.sourceTilemap != null
                    ? proceduralRenderer.sourceTilemap
                    : proceduralRenderer.GetComponentInParent<Tilemap>();
                if (sourceTilemap == null)
                    continue;

                MeshFilter meshFilter = proceduralRenderer.GetComponentInChildren<MeshFilter>(true);
                if (meshFilter == null || meshFilter.sharedMesh != mesh)
                    continue;

                int[] triangles = mesh.GetTriangles(0);
                Vector3[] vertices = mesh.vertices;
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    Vector3 localCentroid =
                        (vertices[triangles[i]] + vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3f;
                    Vector3 worldCentroid = meshFilter.transform.TransformPoint(localCentroid);
                    Vector3Int cell = sourceTilemap.WorldToCell(worldCentroid);

                    if (seen.Add(cell))
                        yield return cell;
                }
            }
        }

        private bool TryGetKnownLevelCellBounds(out BoundsInt bounds)
        {
            bounds = new BoundsInt();
            if (tilemapEditor == null)
                return false;

            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;

            void Consider(BoundsInt cellBounds)
            {
                if (cellBounds.size.x <= 0 || cellBounds.size.y <= 0)
                    return;

                minX = Mathf.Min(minX, cellBounds.xMin);
                minY = Mathf.Min(minY, cellBounds.yMin);
                maxX = Mathf.Max(maxX, cellBounds.xMax - 1);
                maxY = Mathf.Max(maxY, cellBounds.yMax - 1);
            }

            foreach (var tilemap in GetVegetationTilemaps())
            {
                if (tilemap == null)
                    continue;

                tilemap.CompressBounds();
                Consider(tilemap.cellBounds);
            }

            if (tilemapEditor.texturePaintMask != null)
            {
                foreach (var paintedCell in tilemapEditor.texturePaintMask.Keys)
                {
                    minX = Mathf.Min(minX, paintedCell.x);
                    minY = Mathf.Min(minY, paintedCell.y);
                    maxX = Mathf.Max(maxX, paintedCell.x);
                    maxY = Mathf.Max(maxY, paintedCell.y);
                }
            }

            if (minX == int.MaxValue)
                return false;

            bounds = new BoundsInt(minX, minY, 0, maxX - minX + 1, maxY - minY + 1, 1);
            return true;
        }

        private IEnumerable<Tilemap> GetVegetationTilemaps()
        {
            if (tilemapEditor == null)
                yield break;

            var seen = new HashSet<Tilemap>();

            if (tilemapEditor.targetTilemap != null && seen.Add(tilemapEditor.targetTilemap))
                yield return tilemapEditor.targetTilemap;

            if (tilemapEditor.heightTilemaps != null)
            {
                foreach (var tilemap in tilemapEditor.heightTilemaps.Values)
                {
                    if (tilemap != null && seen.Add(tilemap))
                        yield return tilemap;
                }
            }

            foreach (var tilemap in tilemapEditor.GetAllCustomTilemaps())
            {
                if (tilemap != null && seen.Add(tilemap))
                    yield return tilemap;
            }
        }

        private Vector3 GetVegetationCellCenterWorld(Vector3Int cell)
        {
            foreach (var tilemap in GetVegetationTilemaps())
            {
                if (tilemap != null && tilemap.HasTile(cell))
                {
                    Vector3 center = tilemap.GetCellCenterWorld(cell);
                    return new Vector3(center.x, 0f, center.z);
                }
            }

            if (tilemapEditor?.targetTilemap != null)
            {
                Vector3 center = tilemapEditor.targetTilemap.GetCellCenterWorld(cell);
                return new Vector3(center.x, 0f, center.z);
            }

            return new Vector3(cell.x + 0.5f, 0f, cell.y + 0.5f);
        }

        private bool HasGroundTileAtCell(Vector3Int cell)
        {
            foreach (var tilemap in GetVegetationTilemaps())
            {
                if (tilemap != null && tilemap.HasTile(cell))
                    return true;
            }

            return false;
        }

        private bool HasGroundHitAtCell(Vector3Int cell)
        {
            Vector3 center = GetVegetationCellCenterWorld(cell);
            return TryGetVegetationGroundPosition(center.x, center.z, 0f, out _);
        }

        private static void ApplyVegetationEdgePadding(
            Vector3Int cell,
            HashSet<Vector3Int> sourceCells,
            ref float offsetX,
            ref float offsetZ)
        {
            if (sourceCells == null || sourceCells.Count == 0)
                return;

            float minX = -0.5f;
            float maxX = 0.5f;
            float minZ = -0.5f;
            float maxZ = 0.5f;

            if (!sourceCells.Contains(cell + Vector3Int.left))
                minX += VegetationEdgePadding;

            if (!sourceCells.Contains(cell + Vector3Int.right))
                maxX -= VegetationEdgePadding;

            if (!sourceCells.Contains(cell + new Vector3Int(0, -1, 0)))
                minZ += VegetationEdgePadding;

            if (!sourceCells.Contains(cell + new Vector3Int(0, 1, 0)))
                maxZ -= VegetationEdgePadding;

            if (minX > maxX)
            {
                float centerX = (minX + maxX) * 0.5f;
                minX = centerX;
                maxX = centerX;
            }

            if (minZ > maxZ)
            {
                float centerZ = (minZ + maxZ) * 0.5f;
                minZ = centerZ;
                maxZ = centerZ;
            }

            offsetX = Mathf.Clamp(offsetX, minX, maxX);
            offsetZ = Mathf.Clamp(offsetZ, minZ, maxZ);
        }

        private bool TryResolveVegetationSkirtPlacement(
            Vector3Int cell,
            float offsetX,
            float offsetZ,
            float yOffset,
            float skirtOffset,
            out Vector3 position,
            out float rotation)
        {
            position = Vector3.zero;
            rotation = 0f;

            if (tilemapEditor == null)
                return false;

            var rankedDirections = new List<Vector3Int>(4);
            Vector3Int primaryDir;
            Vector3Int secondaryDir;
            Vector3Int tertiaryDir;
            Vector3Int quaternaryDir;

            if (Mathf.Abs(offsetX) >= Mathf.Abs(offsetZ))
            {
                primaryDir = offsetX >= 0f ? Vector3Int.right : Vector3Int.left;
                secondaryDir = offsetZ >= 0f ? new Vector3Int(0, 1, 0) : new Vector3Int(0, -1, 0);
                tertiaryDir = -secondaryDir;
                quaternaryDir = -primaryDir;
            }
            else
            {
                primaryDir = offsetZ >= 0f ? new Vector3Int(0, 1, 0) : new Vector3Int(0, -1, 0);
                secondaryDir = offsetX >= 0f ? Vector3Int.right : Vector3Int.left;
                tertiaryDir = -secondaryDir;
                quaternaryDir = -primaryDir;
            }

            rankedDirections.Add(primaryDir);
            rankedDirections.Add(secondaryDir);
            rankedDirections.Add(tertiaryDir);
            rankedDirections.Add(quaternaryDir);

            foreach (Vector3Int fallbackDir in VegetationSkirtDirections)
            {
                if (!rankedDirections.Contains(fallbackDir))
                    rankedDirections.Add(fallbackDir);
            }

            foreach (Vector3Int direction in rankedDirections)
            {
                Vector3Int exteriorCell = cell + direction;
                if (tilemapEditor.HasSupportingTileAtCell(exteriorCell))
                    continue;

                if (!tilemapEditor.TryResolveSkirtPlacement(exteriorCell, out _, out Vector3 skirtPos, out _))
                    continue;

                float tangentJitter = 0.8f;
                if (direction.x != 0)
                    skirtPos.z += offsetZ * tangentJitter;
                else
                    skirtPos.x += offsetX * tangentJitter;

                skirtPos += new Vector3(direction.x, 0f, direction.y) * Mathf.Clamp01(skirtOffset);
                skirtPos.y += yOffset;
                position = skirtPos;
                rotation = Mathf.Atan2(direction.x, direction.y);
                return true;
            }

            return false;
        }

        private bool TryResolveVegetationPlacementPosition(
            Vector3Int cell,
            Vector3 cellCenter,
            float offsetX,
            float offsetZ,
            float yOffset,
            float skirtOffset,
            QuickTilemapEditor.VegetationPlacementSurface placementSurface,
            QuickTilemapEditor.VegetationPopulateSource populateSource,
            out Vector3 position,
            out float rotation)
        {
            if (placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt)
                return TryResolveVegetationSkirtPlacement(cell, offsetX, offsetZ, yOffset, skirtOffset, out position, out rotation);

            float x = cellCenter.x + offsetX;
            float z = cellCenter.z + offsetZ;
            rotation = 0f;

            if (TryGetVegetationGroundPosition(x, z, yOffset, out position))
                return true;

            float fallbackY = GetVegetationCellBaseY(cell);
            if (!float.IsNaN(fallbackY) &&
                (populateSource == QuickTilemapEditor.VegetationPopulateSource.PaintedCells ||
                 populateSource == QuickTilemapEditor.VegetationPopulateSource.Auto ||
                 populateSource == QuickTilemapEditor.VegetationPopulateSource.UnpaintedGround))
            {
                position = new Vector3(x, fallbackY + yOffset, z);
                return true;
            }

            if (populateSource == QuickTilemapEditor.VegetationPopulateSource.PaintedCells ||
                populateSource == QuickTilemapEditor.VegetationPopulateSource.Auto ||
                populateSource == QuickTilemapEditor.VegetationPopulateSource.UnpaintedGround)
            {
                position = new Vector3(x, yOffset, z);
                return true;
            }

            position = Vector3.zero;
            rotation = 0f;
            return false;
        }

        private Vector3 GetPopulateCellCenterWorld(
            Vector3Int cell,
            QuickTilemapEditor.VegetationPopulateSource populateSource)
        {
            if (populateSource == QuickTilemapEditor.VegetationPopulateSource.PaintedCells ||
                populateSource == QuickTilemapEditor.VegetationPopulateSource.Auto)
            {
                return new Vector3(cell.x + 0.5f, 0f, cell.y + 0.5f);
            }

            // UnpaintedGround: dual-grid tiles are offset by -0.5 from regular cells,
            // so use cell origin (no +0.5) to align with the actual mesh positions.
            if (populateSource == QuickTilemapEditor.VegetationPopulateSource.UnpaintedGround)
            {
                return new Vector3(cell.x, 0f, cell.y);
            }

            return GetVegetationCellCenterWorld(cell);
        }

        private static bool TryGetVegetationGroundPosition(float x, float z, float yOffset, out Vector3 position)
        {
            Vector3 probe = new Vector3(x, 100f, z);
            if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 250f))
            {
                position = hit.point + Vector3.up * yOffset;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private float GetVegetationCellBaseY(Vector3Int cell)
        {
            bool found = false;
            float highestY = float.MinValue;

            foreach (var tilemap in GetVegetationTilemaps())
            {
                if (tilemap == null || !tilemap.HasTile(cell))
                    continue;

                highestY = Mathf.Max(highestY, tilemap.CellToWorld(cell).y);
                found = true;
            }

            if (found)
                return highestY;

            return float.NaN;
        }

        private static IEnumerable<Vector3Int> EnumerateTilemapCells(Tilemap tilemap, HashSet<Vector3Int> seen)
        {
            if (tilemap == null)
                yield break;

            tilemap.CompressBounds();
            BoundsInt bounds = tilemap.cellBounds;
            foreach (var cell in bounds.allPositionsWithin)
            {
                if (!tilemap.HasTile(cell))
                    continue;

                if (seen.Add(cell))
                    yield return cell;
            }
        }

        private IEnumerable<Vector3Int> EnumerateFallbackGridCells()
        {
            if (tilemapEditor == null)
                yield break;

            if (TryGetKnownLevelCellBounds(out BoundsInt bounds))
            {
                foreach (var rawCell in bounds.allPositionsWithin)
                    yield return new Vector3Int(rawCell.x, rawCell.y, 0);
                yield break;
            }

            int halfWidth = Mathf.Max(1, tilemapEditor.gridSize.x) / 2;
            int halfHeight = Mathf.Max(1, tilemapEditor.gridSize.y) / 2;

            for (int x = -halfWidth; x < -halfWidth + Mathf.Max(1, tilemapEditor.gridSize.x); x++)
            {
                for (int y = -halfHeight; y < -halfHeight + Mathf.Max(1, tilemapEditor.gridSize.y); y++)
                {
                    yield return new Vector3Int(x, y, 0);
                }
            }
        }

        /// <summary>Clear all GPU instance data for the given rule.</summary>
        private void ClearVegetationForRule(
            QuickTilemapEditor.TexturePaintRule rule,
            bool refreshGpuRenderer = true,
            bool registerUndo = true)
        {
            if (tilemapEditor == null || rule == null)
                return;

            if (registerUndo)
                Undo.RecordObject(tilemapEditor, "Clear Vegetation (GPU)");

            if (rule.vegetationEntries != null)
            {
                foreach (var entry in rule.vegetationEntries)
                {
                    if (entry?.instances != null)
                        entry.instances.Clear();
                }
            }

            if (refreshGpuRenderer)
                RefreshVegetationGPURenderer();

            EditorUtility.SetDirty(tilemapEditor);
            if (refreshGpuRenderer)
                SceneView.RepaintAll();
        }

        private void SyncVegetationAfterTexturePaintStroke(bool registerUndo = false)
        {
            if (tilemapEditor?.texturePaintRules == null || tilemapEditor.texturePaintRules.Count == 0)
                return;

            bool hasRelevantRules = false;
            foreach (var rule in tilemapEditor.texturePaintRules)
            {
                if (rule == null)
                    continue;

                if (rule.removeVegetation || HasConfiguredVegetation(rule))
                {
                    hasRelevantRules = true;
                    break;
                }
            }

            if (!hasRelevantRules)
                return;

            if (registerUndo)
                Undo.RecordObject(tilemapEditor, "Sync Vegetation (GPU)");

            foreach (var rule in tilemapEditor.texturePaintRules)
            {
                if (rule == null)
                    continue;

                if (rule.removeVegetation)
                {
                    ClearVegetationForRule(rule, refreshGpuRenderer: false, registerUndo: false);
                }
                else if (HasConfiguredVegetation(rule))
                {
                    PopulateVegetationForRule(rule, refreshGpuRenderer: false, registerUndo: false);
                }
            }

            RefreshVegetationGPURenderer();
            EditorUtility.SetDirty(tilemapEditor);
            SceneView.RepaintAll();
        }

        /// <summary>Ensure VegetationGPURenderer component exists and rebuild draw groups.</summary>
        private void RefreshVegetationGPURenderer()
        {
            if (tilemapEditor == null) return;

            var renderer = tilemapEditor.GetComponent<VegetationGPURenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<VegetationGPURenderer>(tilemapEditor.gameObject);
            }

            // Auto-assign shaders if missing
            if (renderer.cullingShader == null)
            {
                var cs = FindAssetByName<ComputeShader>("VegetationCulling");
                if (cs != null) renderer.cullingShader = cs;
            }
            if (renderer.instancedShader == null)
            {
                renderer.instancedShader = Shader.Find("BEKKOLOCO/VegetationInstanced");
            }
            if (renderer.grassShader == null)
            {
                renderer.grassShader = Shader.Find("BEKKOLOCO/VegetationGrass");
            }

            renderer.RebuildFromRules(tilemapEditor.texturePaintRules);
        }

        private static T FindAssetByName<T>(string name) where T : Object
        {
            string[] guids = AssetDatabase.FindAssets($"{name} t:{typeof(T).Name}");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }
            return null;
        }

        #endregion
    }
}
