
using UnityEngine;
using UnityEditor;
using UnityEngine.Tilemaps;
using System;
using System.Collections.Generic;
using UnityEditorInternal;
using System.IO;
using System.Linq;
using UnityEngine.UIElements;
using UnityEditor.UIElements;
using Bekkoloco.DOTS;

using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.GUI;

using UnityEditor.Formats.Fbx.Exporter;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;


using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace Bekkoloco
{
    public static class QuickTilemapEditorTools
    {
        private static AddRequest tilemapRequest;
        private static AddRequest extrasRequest;
        private static AddRequest navRequest;
        private static AddRequest addressablesRequest;
        private static AddRequest splinesRequest;

        // 🧩 Menu: Open the editor
        [MenuItem("Bekkoloco/QuickTile/👉 Open Quick Tile Editor", priority = 0)]
        public static void OpenQuickTilemapEditor()
        {
            QuickTilemapEditor existingEditor = Object.FindFirstObjectByType<QuickTilemapEditor>();
            if (existingEditor != null && existingEditor.GetComponent<Grid>() != null)
            {
                Selection.activeGameObject = existingEditor.gameObject;
                return;
            }

            GameObject go = new GameObject("QuickTilemapEditor");
            Grid grid = go.AddComponent<Grid>();
            grid.cellSwizzle = Grid.CellSwizzle.XZY;
            go.AddComponent<QuickTilemapEditor>();

            Selection.activeGameObject = go;
        }

        // 🧩 Menu: Install all required packages
        [MenuItem("Bekkoloco/QuickTile/🔧 Install Required Packages", priority = 20)]
        public static void InstallTilemapPackages()
        {
            // Install 'com.unity.2d.tilemap'
            tilemapRequest = Client.Add("com.unity.2d.tilemap");
            EditorApplication.update += TilemapProgress;
        }

        private static void TilemapProgress()
        {
            if (tilemapRequest.IsCompleted)
            {
                if (tilemapRequest.Status == StatusCode.Success)
                {
                    // Install 'com.unity.2d.tilemap.extras'
                    extrasRequest = Client.Add("com.unity.2d.tilemap.extras");
                    EditorApplication.update += ExtrasProgress;
                }
                else
                {
                    //Debug.Log(Error("❌ Failed to install com.unity.2d.tilemap: " + tilemapRequest.Error.message);
                }

                EditorApplication.update -= TilemapProgress;
            }
        }

        private static void ExtrasProgress()
        {
            if (extrasRequest.IsCompleted)
            {
                if (extrasRequest.Status == StatusCode.Success)
                {
                    // Install 'com.unity.ai.navigation'
                    navRequest = Client.Add("com.unity.ai.navigation");
                    EditorApplication.update += NavMeshProgress;
                }
                else
                {
                    //Debug.Log(Error("❌ Failed to install com.unity.2d.tilemap.extras: " + extrasRequest.Error.message);
                }

                EditorApplication.update -= ExtrasProgress;
            }
        }

        private static void NavMeshProgress()
        {
            if (navRequest.IsCompleted)
            {
                if (navRequest.Status == StatusCode.Success)
                {
                    // Install 'com.unity.addressables'
                    addressablesRequest = Client.Add("com.unity.addressables");
                    EditorApplication.update += AddressablesProgress;
                }
                else
                {
                    //Debug.Log(Error("❌ Failed to install com.unity.ai.navigation: " + navRequest.Error.message);
                }

                EditorApplication.update -= NavMeshProgress;
            }
        }

        private static void AddressablesProgress()
        {
            if (addressablesRequest.IsCompleted)
            {
                if (addressablesRequest.Status == StatusCode.Success)
                {
                    // Install 'com.unity.splines' (required by SplineMesh)
                    splinesRequest = Client.Add("com.unity.splines");
                    EditorApplication.update += SplinesProgress;
                }
                else
                {
                    //Debug.Log(Error("❌ Failed to install com.unity.addressables: " + addressablesRequest.Error.message);
                }

                EditorApplication.update -= AddressablesProgress;
            }
        }

        private static void SplinesProgress()
        {
            if (splinesRequest.IsCompleted)
            {
                if (splinesRequest.Status == StatusCode.Success)
                {
                    Debug.Log("✅ QuickTile: All required packages installed successfully!");
                }
                else
                {
                    Debug.LogError("❌ Failed to install com.unity.splines: " + splinesRequest.Error.message);
                }

                EditorApplication.update -= SplinesProgress;
            }
        }
    }





    [CustomEditor(typeof(QuickTilemapEditor))]
    public partial class QuickTilemapEditorInspector : Editor
    {
        #region Inspector Variables

        private static readonly HashSet<QuickTilemapEditorInspector> activeInspectors =
            new HashSet<QuickTilemapEditorInspector>();

        private bool isSelectionMode = false;
        private bool panToolActive = false;
        private List<Vector3Int> selectedCells = new List<Vector3Int>();
        private bool isDraggingSelection = false;
        private Vector3Int dragStartCell;
        private Vector2 dragStartMousePos;
        private Vector3Int selectionOffset = Vector3Int.zero;


        private Vector3Int tempSelectionPos = Vector3Int.zero;
        private Color tempSelectionColor = Color.white;
        private float tempSelectionTime = 0f;
        private const float TEMP_SELECTION_DURATION = 1.5f;

        // Cached material for path LineRenderers (avoids per-path Material leak)
        private static Material _pathLineMaterial;
        private static Material GetPathLineMaterial()
        {
            if (_pathLineMaterial == null)
                _pathLineMaterial = new Material(Shader.Find("Sprites/Default"));
            return _pathLineMaterial;
        }


        private static AddRequest fbxExporterRequest;

        [InitializeOnLoadMethod]
        static void InitializeOnLoad()
        {
            // Clean up static material on domain reload to prevent leak
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (_pathLineMaterial != null)
                {
                    Object.DestroyImmediate(_pathLineMaterial);
                    _pathLineMaterial = null;
                }
            };

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }


        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                // 1) Désélectionne pour éviter que l’Inspector référence un GO détruit juste après
                UnityEditor.Selection.activeObject = null;

                // 2) On peut sauvegarder des données sérialisées (pas de Destroy/Instantiate ici)
                var editors = Object.FindObjectsByType<QuickTilemapEditor>(FindObjectsSortMode.None);
                foreach (var ed in editors)
                {
                    // Sauvegardes “safe” (purement data)
                    ed.CaptureDeformerHandlesForSave();
                    // Si tu as un “AutoSaveLevels()” pure data, c'est ici.
                    // NE PAS appeler Instantiate/Clear ou Apply visuel ici.
                }

                // Ne rien toucher à la hiérarchie/instances dans ExitingEditMode.
                return;
            }

            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                // Rien à faire : DOTS + GOSpawner gèrent les instances en runtime.
                return;
            }

            if (state == PlayModeStateChange.EnteredEditMode)
            {
                // Si tu veux regénérer les préviews en Éditeur, fais-le après un delayCall
                // pour laisser l’Inspector se stabiliser.
                EditorApplication.delayCall += () =>
                {
                    var editors = Object.FindObjectsByType<QuickTilemapEditor>(FindObjectsSortMode.None);
                    foreach (var ed in editors)
                    {
                        // Ici tu peux regénérer les prévisualisations EDITOR si besoin,
                        // mais pas indispensable pour corriger le bug Play.
                    }
                };
            }
        }


        /*
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Save when entering play mode (before it actually starts)
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                Debug.Log("[QuickTile] Entering Play Mode - Auto-saving levels...");

                // Find all QuickTilemapEditors in the scene
                QuickTilemapEditor[] editors = Object.FindObjectsByType<QuickTilemapEditor>(FindObjectsSortMode.None);

                foreach (var editor in editors)
                {
                    var inspector = Editor.CreateEditor(editor) as QuickTilemapEditorInspector;
                    if (inspector != null)
                    {
                        if (!string.IsNullOrEmpty(inspector.customLevelName))
                        {
                            inspector.TrySaveCurrentLevel();
                            Debug.Log($"[QuickTile] Auto-saved level: {inspector.customLevelName}");
                        }
                        Object.DestroyImmediate(inspector);
                    }
                }
            }
        }
        */


        private ReorderableList tileRulesList;
        private ReorderableList pathsList;
        private List<GameObjectRuleUIState> gameObjectRuleUIStates = new List<GameObjectRuleUIState>();
        public VisualTreeAsset visualTree;
        public Texture2D headerImage;
        Vector2 scrollTab = Vector2.zero;
        private ReorderableList propertyList;
        private QuickTilemapEditor tilemapEditor;
        private List<TileRuleUIState> tileRuleUIStates = new List<TileRuleUIState>();
        private enum EraseMode { Select, All }
        private EraseMode eraseMode = EraseMode.Select;
        private SerializedProperty targetTilemapProperty, activeTileProperty, recentTilesProperty, gridSizeProperty, cellSizeProperty, editorEnabledProperty, brushSizeProperty, useCustomSizeProperty;
        private Dictionary<string, TileBase> tileDict = new Dictionary<string, TileBase>();
        Dictionary<Vector3Int, List<int>> gameObjectsToMove = new Dictionary<Vector3Int, List<int>>();
        Dictionary<Vector3Int, Dictionary<int, TileData>> allTilesToMove = new Dictionary<Vector3Int, Dictionary<int, TileData>>();

        private bool drawMode = true;
        private GUIContent drawButtonContent, eraseButtonContent, eraseAllButtonContent;
        private int customWidth = 32, customHeight = 32;
        private int selectedTab = 0;
        private int lastSelectedTileRuleIndex = -1;
        private int lastSelectedTextureRuleMemoryIndex = -1;
        private int lastSelectedGameObjectRuleIndex = -1;
        private int lastSelectedPathMemoryIndex = -1;

        private const string AutoCenterLevelLabel = "Center Drawing In Grid On Save";
        private const string AutoCenterLevelTooltip = "On save, shifts tiles, paths, objects and painted textures together so the drawing is centered in grid coordinates.";
        private Vector2 scrollPosition;
        private Vector2 gridViewOffset = Vector2.zero;
        private GUIStyle overlayButtonStyle;
        private GUIStyle overlayLabelStyle;
        private GUIStyle overlayStatusLabelStyle;
        private const float autoScrollThreshold = 20f, autoScrollSpeed = 5f;
        public bool fixBase = false;
        private bool _showLevelProperties = true;

        public string customLevelName = "";

        struct TileData
        {
            public TileBase tile;
            public Color color;
            public Tilemap targetMap;
            public Matrix4x4 transform;
        }


        #endregion

        #region Unity Methods


        [InitializeOnLoadMethod]
        static void OnProjectLoadedInEditor()
        {
            EditorApplication.delayCall += () =>
            {
                var editors = Object.FindObjectsByType<QuickTilemapEditor>(FindObjectsSortMode.None);
                foreach (var editor in editors)
                {
                    if (editor != null)
                    {
                        editor.RestorePrefabReferences();
                        editor.ResynchronizeGameObjectsFromScene();
                        EditorUtility.SetDirty(editor);
                    }
                }
            };
        }



        // Set to true to enable UI Toolkit for supported tabs
        private bool useUIToolkit = true;
        private UnityEngine.UIElements.VisualElement tileRulesUIToolkitContainer;
        private UnityEngine.UIElements.VisualElement gameObjectRulesUIToolkitContainer;
        private UnityEngine.UIElements.VisualElement pathUIToolkitContainer;
        private UnityEngine.UIElements.VisualElement texturePaintUIToolkitContainer;
        private UnityEngine.UIElements.VisualElement levelManagerUIToolkitContainer;
        private UnityEngine.UIElements.VisualElement uiToolkitTabContent;
        private UnityEngine.UIElements.VisualElement uiToolkitTabBar;

        public override VisualElement CreateInspectorGUI()
        {
            VisualElement root = new VisualElement();
            root.name = "quick-tile-inspector-root";
            root.style.minWidth = 470;

            // Load and apply the dark theme stylesheet
            var styleSheet = UnityEngine.Resources.Load<UnityEngine.UIElements.StyleSheet>("QuickTilemapEditor");
            if (styleSheet != null)
            {
                root.styleSheets.Add(styleSheet);
                root.AddToClassList("quick-tile-inspector");
            }

            if (visualTree != null)
                visualTree.CloneTree(root);

            Texture2D headerTex = headerImage;
            if (headerTex == null)
            {
                string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
                string scriptDirectory = System.IO.Path.GetDirectoryName(scriptPath);
                string imagePath = System.IO.Path.Combine(scriptDirectory, "header.png").Replace("\\", "/");
                headerTex = AssetDatabase.LoadAssetAtPath<Texture2D>(imagePath);
            }

            // Create UI Toolkit tab content container (hidden by default)
            if (useUIToolkit)
            {
                // MCP (Unity AI Assistant) setup panel
                root.Add(QuickTileMCPConfigurator.BuildInspectorPanel());

                // Create Level Manager section with logo integrated
                var levelManager = CreateLevelManagerSection_UIToolkit(headerTex);
                root.Add(levelManager);

                // Create the stable drawing system section (V1 only)
                var drawingSystem = CreateDrawingSystemSection_UIToolkit();
                root.Add(drawingSystem);

                // Create tab bar
                uiToolkitTabBar = CreateTabBar_UIToolkit();
                root.Add(uiToolkitTabBar);

                uiToolkitTabContent = new UnityEngine.UIElements.VisualElement();
                uiToolkitTabContent.name = "ui-toolkit-tab-content";

                // Create the TileRules section using UI Toolkit
                tileRulesUIToolkitContainer = CreateTileRulesSection_UIToolkit();
                tileRulesUIToolkitContainer.style.display = UnityEngine.UIElements.DisplayStyle.None;
                uiToolkitTabContent.Add(tileRulesUIToolkitContainer);

                // Create the GameObjectRules section using UI Toolkit
                gameObjectRulesUIToolkitContainer = CreateGameObjectRulesSection_UIToolkit();
                gameObjectRulesUIToolkitContainer.style.display = UnityEngine.UIElements.DisplayStyle.None;
                uiToolkitTabContent.Add(gameObjectRulesUIToolkitContainer);

                // Create the Path section using UI Toolkit
                pathUIToolkitContainer = CreatePathSection_UIToolkit();
                pathUIToolkitContainer.style.display = UnityEngine.UIElements.DisplayStyle.None;
                uiToolkitTabContent.Add(pathUIToolkitContainer);

                // Create the TexturePaint section using UI Toolkit
                texturePaintUIToolkitContainer = CreateTexturePaintSection_UIToolkit();
                texturePaintUIToolkitContainer.style.display = UnityEngine.UIElements.DisplayStyle.None;
                uiToolkitTabContent.Add(texturePaintUIToolkitContainer);

                root.Add(uiToolkitTabContent);

                // Initial visibility
                UpdateUIToolkitVisibility();
            }

            // Add existing IMGUI inspector code (will be gradually replaced)
            IMGUIContainer imguiContainer = new IMGUIContainer(() => { OnInspectorGUI(); });
            imguiContainer.name = "imgui-container";
            root.Add(imguiContainer);

            return root;
        }

        /// <summary>
        /// Shows/hides UI Toolkit content based on selected tab
        /// </summary>
        private void UpdateUIToolkitVisibility()
        {
            if (!useUIToolkit) return;
            
            bool showTiles = selectedTab == 0;
            bool showGameObjects = selectedTab == 2;
            
            if (tileRulesUIToolkitContainer != null)
            {
                tileRulesUIToolkitContainer.style.display = showTiles 
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
                if (showTiles)
                {
                    var tileRulesList = tileRulesUIToolkitContainer.Q("tile-rules-list");
                    if (tileRulesList != null)
                        RefreshTileRulesList_UIToolkit(tileRulesList);
                }
            }
            
            if (gameObjectRulesUIToolkitContainer != null)
            {
                gameObjectRulesUIToolkitContainer.style.display = showGameObjects 
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
                if (showGameObjects)
                {
                    var gameObjectRulesList = gameObjectRulesUIToolkitContainer.Q("gameobject-rules-list");
                    if (gameObjectRulesList != null)
                        RefreshGameObjectRulesList_UIToolkit(gameObjectRulesList);
                }
            }

            if (pathUIToolkitContainer != null)
            {
                pathUIToolkitContainer.style.display = (selectedTab == 3)
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;
                if (selectedTab == 3)
                {
                    var pathsList = pathUIToolkitContainer.Q("paths-list");
                    if (pathsList != null)
                        RefreshPathList_UIToolkit(pathsList);
                }
            }

            if (texturePaintUIToolkitContainer != null)
            {
                bool showTexture = selectedTab == 1;
                texturePaintUIToolkitContainer.style.display = showTexture
                    ? UnityEngine.UIElements.DisplayStyle.Flex 
                    : UnityEngine.UIElements.DisplayStyle.None;

                if (showTexture)
                    RefreshTexturePaintSectionIfNeeded_UIToolkit(true);
            }
        }

        private bool IsTilesTabActive() => selectedTab == 0 && !isSelectionMode;
        private bool IsTextureTabActive() => selectedTab == 1 && !isSelectionMode;
        private bool IsGameObjectsTabActive() => selectedTab == 2 && !isSelectionMode;
        private bool IsPathTabActive() => selectedTab == 3 && !isSelectionMode;

        private void SetInspectorTab(int tabIndex, bool preserveToolState = false)
        {
            CacheSelectionForTab(selectedTab);
            selectedTab = Mathf.Clamp(tabIndex, 0, 3);
            SyncSelectionToActiveTab();

            if (!preserveToolState && !isSelectionMode)
                drawMode = true;

            serializedObject.Update();
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(tilemapEditor);

            UpdateUIToolkitVisibility();
            if (uiToolkitTabBar != null)
                UpdateTabBarSelection(uiToolkitTabBar);
            SceneView.RepaintAll();
            Repaint();
        }

        private void SyncSelectionToActiveTab()
        {
            if (tilemapEditor == null)
                return;

            switch (selectedTab)
            {
                case 0:
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                    tilemapEditor.selectedTextureRule = null;
                    tilemapEditor.selectedTextureRuleIndex = -1;

                    if (tilemapEditor.tileRules != null && tilemapEditor.tileRules.Count > 0)
                    {
                        int tileIndex = tilemapEditor.selectedTileRuleIndex;
                        if (tileIndex < 0 || tileIndex >= tilemapEditor.tileRules.Count)
                            tileIndex = lastSelectedTileRuleIndex;
                        if (tileIndex < 0 || tileIndex >= tilemapEditor.tileRules.Count)
                            tileIndex = 0;

                        tilemapEditor.selectedTileRuleIndex = tileIndex;
                        lastSelectedTileRuleIndex = tileIndex;

                        var tileRule = tilemapEditor.tileRules[tilemapEditor.selectedTileRuleIndex];
                        if (tileRule != null)
                            tilemapEditor.activeTile = tileRule.tile;
                    }
                    else
                    {
                        tilemapEditor.selectedTileRuleIndex = -1;
                    }
                    break;

                case 1:
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;

                    if (tilemapEditor.texturePaintRules != null && tilemapEditor.texturePaintRules.Count > 0)
                    {
                        int textureIndex = tilemapEditor.selectedTextureRule != null
                            ? tilemapEditor.texturePaintRules.IndexOf(tilemapEditor.selectedTextureRule)
                            : -1;

                        if (textureIndex < 0 && tilemapEditor.selectedTextureRuleIndex >= 0 && tilemapEditor.selectedTextureRuleIndex < tilemapEditor.texturePaintRules.Count)
                            textureIndex = tilemapEditor.selectedTextureRuleIndex;

                        if (textureIndex < 0 && lastSelectedTextureRuleMemoryIndex >= 0 && lastSelectedTextureRuleMemoryIndex < tilemapEditor.texturePaintRules.Count)
                            textureIndex = lastSelectedTextureRuleMemoryIndex;

                        if (textureIndex < 0 || textureIndex >= tilemapEditor.texturePaintRules.Count)
                            textureIndex = 0;

                        tilemapEditor.selectedTextureRuleIndex = textureIndex;
                        tilemapEditor.selectedTextureRule = tilemapEditor.texturePaintRules[textureIndex];
                        lastSelectedTextureRuleMemoryIndex = textureIndex;
                    }
                    else
                    {
                        tilemapEditor.selectedTextureRule = null;
                        tilemapEditor.selectedTextureRuleIndex = -1;
                    }
                    break;

                case 2:
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                    tilemapEditor.selectedTextureRule = null;
                    tilemapEditor.selectedTextureRuleIndex = -1;

                    if (tilemapEditor.gameObjectRules != null && tilemapEditor.gameObjectRules.Count > 0)
                    {
                        int gameObjectIndex = tilemapEditor.selectedGameObjectRuleIndex;
                        if (gameObjectIndex < 0 || gameObjectIndex >= tilemapEditor.gameObjectRules.Count)
                            gameObjectIndex = lastSelectedGameObjectRuleIndex;
                        if (gameObjectIndex < 0 || gameObjectIndex >= tilemapEditor.gameObjectRules.Count)
                            gameObjectIndex = 0;

                        tilemapEditor.selectedGameObjectRuleIndex = gameObjectIndex;
                        lastSelectedGameObjectRuleIndex = gameObjectIndex;
                    }
                    else
                    {
                        tilemapEditor.selectedGameObjectRuleIndex = -1;
                    }
                    break;

                case 3:
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedTextureRule = null;
                    tilemapEditor.selectedTextureRuleIndex = -1;

                    if (tilemapEditor.paths != null && tilemapEditor.paths.Count > 0)
                    {
                        int pathIndex = tilemapEditor.selectedPathIndex;
                        if (pathIndex < 0 || pathIndex >= tilemapEditor.paths.Count)
                            pathIndex = lastSelectedPathMemoryIndex;
                        if (pathIndex < 0 || pathIndex >= tilemapEditor.paths.Count)
                            pathIndex = 0;

                        tilemapEditor.selectedPathIndex = pathIndex;
                        lastSelectedPathMemoryIndex = pathIndex;
                    }
                    else
                    {
                        tilemapEditor.selectedPathIndex = -1;
                    }
                    break;
            }
        }

        private void CacheSelectionForTab(int tabIndex)
        {
            if (tilemapEditor == null)
                return;

            switch (tabIndex)
            {
                case 0:
                    if (tilemapEditor.selectedTileRuleIndex >= 0)
                        lastSelectedTileRuleIndex = tilemapEditor.selectedTileRuleIndex;
                    break;
                case 1:
                    if (tilemapEditor.selectedTextureRule != null && tilemapEditor.texturePaintRules != null)
                    {
                        int textureIndex = tilemapEditor.texturePaintRules.IndexOf(tilemapEditor.selectedTextureRule);
                        if (textureIndex >= 0)
                            lastSelectedTextureRuleMemoryIndex = textureIndex;
                    }
                    else if (tilemapEditor.selectedTextureRuleIndex >= 0)
                    {
                        lastSelectedTextureRuleMemoryIndex = tilemapEditor.selectedTextureRuleIndex;
                    }
                    break;
                case 2:
                    if (tilemapEditor.selectedGameObjectRuleIndex >= 0)
                        lastSelectedGameObjectRuleIndex = tilemapEditor.selectedGameObjectRuleIndex;
                    break;
                case 3:
                    if (tilemapEditor.selectedPathIndex >= 0)
                        lastSelectedPathMemoryIndex = tilemapEditor.selectedPathIndex;
                    break;
            }
        }


        private VisualElement CreateTabBar_UIToolkit()
        {
            var tabBar = new VisualElement();
            tabBar.name = "tab-bar";
            tabBar.style.flexDirection = FlexDirection.Row;
            tabBar.style.marginBottom = 8;
            tabBar.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.1f, 1f));
            tabBar.style.paddingLeft = 4;
            tabBar.style.paddingRight = 4;
            tabBar.style.paddingTop = 4;
            tabBar.style.paddingBottom = 4;

            string[] tabNames = { "Tiles", "Paint & Plants 🌱", "Objects", "Path" };
            string[] tabIcons =
            {
                "tile_small_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png",
                "texture_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png",
                "box_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png",
                "timeline_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png"
            };

            int[] visualTabOrder = { 0, 2, 3, 1 };

            for (int visualIndex = 0; visualIndex < visualTabOrder.Length; visualIndex++)
            {
                int tabIndex = visualTabOrder[visualIndex];
                var tabBtn = new Button(() => {
                    SetInspectorTab(tabIndex);
                    UpdateTabBarSelection(tabBar);
                });
                tabBtn.name = $"tab-btn-{tabIndex}";
                tabBtn.style.flexGrow = 1;
                tabBtn.style.height = 32;
                tabBtn.style.marginRight = visualIndex < visualTabOrder.Length - 1 ? 2 : 0;
                tabBtn.style.borderTopLeftRadius = 4;
                tabBtn.style.borderTopRightRadius = 4;
                tabBtn.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
                tabBtn.style.color = new StyleColor(Color.white);
                tabBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
                tabBtn.style.justifyContent = Justify.Center;
                tabBtn.style.alignItems = Align.Center;
                tabBtn.text = string.Empty;
                tabBtn.Clear();

                var contentRow = new VisualElement();
                contentRow.style.flexDirection = FlexDirection.Row;
                contentRow.style.alignItems = Align.Center;
                contentRow.style.justifyContent = Justify.Center;
                contentRow.style.flexGrow = 1;
                contentRow.pickingMode = PickingMode.Ignore;

                Texture2D iconTexture = LoadOverlayIconTexture(tabIcons[tabIndex]);
                if (iconTexture != null)
                {
                    var iconImage = new Image { image = iconTexture };
                    iconImage.scaleMode = ScaleMode.ScaleToFit;
                    iconImage.pickingMode = PickingMode.Ignore;
                    iconImage.style.width = 18;
                    iconImage.style.height = 18;
                    iconImage.style.marginRight = 6;
                    contentRow.Add(iconImage);
                }

                var label = new Label(tabNames[tabIndex]);
                label.style.color = new StyleColor(Color.white);
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.fontSize = 10;
                label.pickingMode = PickingMode.Ignore;
                contentRow.Add(label);
                tabBtn.Add(contentRow);
                
                if (tabIndex == selectedTab)
                    tabBtn.style.backgroundColor = new StyleColor(new Color(0.25f, 0.6f, 0.9f, 1f));
                
                tabBar.Add(tabBtn);
            }

            return tabBar;
        }

        private void UpdateTabBarSelection(VisualElement tabBar)
        {
            for (int i = 0; i < 4; i++)
            {
                var btn = tabBar.Q<Button>($"tab-btn-{i}");
                if (btn != null)
                {
                    btn.style.backgroundColor = (i == selectedTab)
                        ? new StyleColor(new Color(0.25f, 0.6f, 0.9f, 1f))
                        : new StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
                }
            }
        }

        private bool levelManagerExpanded = false;

        private VisualElement CreateLevelManagerSection_UIToolkit(Texture2D logoTexture = null)
        {
            var container = new VisualElement();
            levelManagerUIToolkitContainer = container;
            container.name = "level-manager";
            container.AddToClassList("card");
            // Override CSS with inline styles - GREEN border, BLACK background
            container.style.borderTopWidth = 2;
            container.style.borderBottomWidth = 2;
            container.style.borderLeftWidth = 4;
            container.style.borderRightWidth = 2;
            container.style.borderTopColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f)); // #4ADE80 green
            container.style.borderBottomColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            container.style.borderLeftColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            container.style.borderRightColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            container.style.backgroundColor = new StyleColor(new Color(0.05f, 0.05f, 0.05f, 1f)); // Pure black

            // Load stylesheet
            var styleSheet = Resources.Load<StyleSheet>("QuickTilemapEditor");
            if (styleSheet != null) container.styleSheets.Add(styleSheet);

            // ── Main layout: logo left | controls right ──
            var mainRow = new VisualElement();
            mainRow.style.flexDirection = FlexDirection.Row;
            mainRow.style.alignItems = Align.Center;

            // Logo on the left
            if (logoTexture != null)
            {
                var logoImage = new Image { image = logoTexture };
                logoImage.scaleMode = ScaleMode.ScaleToFit;
                logoImage.style.width = 70;
                logoImage.style.height = 70;
                logoImage.style.marginRight = 8;
                logoImage.style.flexShrink = 0;
                mainRow.Add(logoImage);
            }

            // Controls column (right side): row 1 = dropdown, row 2 = buttons
            var controlsColumn = new VisualElement();
            controlsColumn.style.flexGrow = 1;
            controlsColumn.style.flexDirection = FlexDirection.Column;

            // ── Row 1: Level dropdown ──
            var dropdownRow = new VisualElement();
            dropdownRow.style.flexDirection = FlexDirection.Row;
            dropdownRow.style.alignItems = Align.Center;
            dropdownRow.style.marginBottom = 4;

            var levelChoices = new System.Collections.Generic.List<string>();
            int currentIdx = 0;
            if (tilemapEditor?.levels != null)
            {
                for (int i = 0; i < tilemapEditor.levels.Count; i++)
                {
                    levelChoices.Add(tilemapEditor.levels[i].levelName);
                    if (i == tilemapEditor.currentLevelIndex) currentIdx = i;
                }
            }
            if (levelChoices.Count == 0) levelChoices.Add("No Level");

            var levelDropdown = new DropdownField(levelChoices, currentIdx);
            levelDropdown.name = "level-dropdown";
            levelDropdown.style.flexGrow = 1;
            levelDropdown.style.height = 28;
            levelDropdown.RegisterValueChangedCallback(evt => {
                if (tilemapEditor?.levels == null) return;
                int newIndex = tilemapEditor.levels.FindIndex(l => l.levelName == evt.newValue);
                if (newIndex >= 0 && newIndex < tilemapEditor.levels.Count && newIndex != tilemapEditor.currentLevelIndex)
                {
                    var tileDict = tilemapEditor.BuildTileDictionary();
                    tilemapEditor.LoadLevel(newIndex, tileDict);
                    SyncInspectorAfterLevelChange(container);
                }
            });
            dropdownRow.Add(levelDropdown);

            // Prev button (right of dropdown, paired with Next)
            var prevBtn = new Button(() => {
                tilemapEditor?.LoadPreviousLevel();
                SyncInspectorAfterLevelChange(container);
            });
            prevBtn.AddToClassList("btn");
            prevBtn.AddToClassList("btn-icon");
            prevBtn.style.marginLeft = 4;
            prevBtn.tooltip = "Previous level";
            SetUIToolkitHeaderButtonIcon(prevBtn, "chevron_right_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png", 180f);
            dropdownRow.Add(prevBtn);

            // Next button (right of Prev)
            var nextBtn = new Button(() => {
                tilemapEditor?.LoadNextLevel();
                SyncInspectorAfterLevelChange(container);
            });
            nextBtn.AddToClassList("btn");
            nextBtn.AddToClassList("btn-icon");
            nextBtn.style.marginLeft = 2;
            nextBtn.tooltip = "Next level";
            SetUIToolkitHeaderButtonIcon(nextBtn, "chevron_right_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png");
            dropdownRow.Add(nextBtn);

            controlsColumn.Add(dropdownRow);

            // ── Row 2: Expand (left) | spacer | Reload, Save, + (right) ──
            var buttonsRow = new VisualElement();
            buttonsRow.style.flexDirection = FlexDirection.Row;
            buttonsRow.style.alignItems = Align.Center;

            // Expand/collapse triangle (Unity foldout style) — first, far left
            var expandBtn = new Foldout();
            expandBtn.name = "expand-btn";
            expandBtn.text = "";
            expandBtn.value = levelManagerExpanded;
            expandBtn.style.width = 12;
            expandBtn.style.minWidth = 12;
            expandBtn.RegisterValueChangedCallback(evt => {
                levelManagerExpanded = evt.newValue;
                RefreshLevelManagerContent(container);
            });
            buttonsRow.Add(expandBtn);

            var levelSettingsLabel = new Label("Level Settings");
            levelSettingsLabel.style.marginLeft = 1;
            levelSettingsLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            levelSettingsLabel.style.color = new StyleColor(new Color(0.82f, 0.88f, 0.95f, 1f));
            levelSettingsLabel.style.fontSize = 12;
            levelSettingsLabel.style.alignSelf = Align.Center;
            buttonsRow.Add(levelSettingsLabel);

            // Spacer pushes remaining buttons to the right
            var btnSpacer = new VisualElement();
            btnSpacer.style.flexGrow = 1;
            buttonsRow.Add(btnSpacer);

            // Reload button
            var reloadBtn = new Button(() => {
                if (tilemapEditor?.levels != null && tilemapEditor.currentLevelIndex >= 0 &&
                    tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
                {
                    tilemapEditor.ReloadCurrentLevel();
                    SyncInspectorAfterLevelChange(container);
                //    Debug.Log("🔄 Level Reloaded");
                }
            });
            reloadBtn.AddToClassList("btn");
            reloadBtn.AddToClassList("btn-icon");
            reloadBtn.tooltip = "Reload level";
            SetUIToolkitHeaderButtonIcon(reloadBtn, "cached_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png");
            buttonsRow.Add(reloadBtn);

            // Save button
            var saveBtn = new Button(() => {
                if (tilemapEditor?.levels != null && tilemapEditor.currentLevelIndex >= 0 &&
                    tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
                {
                    string path = tilemapEditor.GetCurrentLevelSaveAssetPath();
                    if (string.IsNullOrEmpty(path))
                    {
                        Debug.LogError("[QuickTile] Could not resolve a valid save path for the current level.");
                        return;
                    }

                    tilemapEditor.SaveTilemapToJson(path);
                    AssetDatabase.Refresh();
                //    Debug.Log($"✅ Saved: {path}");
                }
            });
            saveBtn.AddToClassList("btn");
            saveBtn.AddToClassList("btn-icon");
            saveBtn.tooltip = "Save level";
            SetUIToolkitHeaderButtonIcon(saveBtn, "save_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24 (1).png");
            saveBtn.style.borderTopWidth = 2;
            saveBtn.style.borderBottomWidth = 2;
            saveBtn.style.borderLeftWidth = 2;
            saveBtn.style.borderRightWidth = 2;
            saveBtn.style.borderTopColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            saveBtn.style.borderBottomColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            saveBtn.style.borderLeftColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            saveBtn.style.borderRightColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f));
            saveBtn.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
            saveBtn.style.marginLeft = 2;
            buttonsRow.Add(saveBtn);

            // Autosave version browser button
            var autosaveBtn = new Button(() => {
                if (tilemapEditor?.levels == null || tilemapEditor.currentLevelIndex < 0 ||
                    tilemapEditor.currentLevelIndex >= tilemapEditor.levels.Count)
                {
                    Debug.LogWarning("[QuickTile] No level selected.");
                    return;
                }

                var files = tilemapEditor.GetAutosaveFilesForLevel();
                if (files == null || files.Count == 0)
                {
                    Debug.Log("[QuickTile] No autosave versions found for this level.");
                    return;
                }

                var menu = new GenericMenu();
                string levelName = tilemapEditor.levels[tilemapEditor.currentLevelIndex].levelName;
                menu.AddDisabledItem(new GUIContent($"Autosaves for \"{levelName}\" ({files.Count})"));
                menu.AddSeparator("");

                foreach (var file in files)
                {
                    string timestamp = QuickTilemapEditor.ParseAutosaveTimestamp(file);
                    var counts = QuickTilemapEditor.PeekAutosaveCounts(file);
                    string label = $"{timestamp}  |  rules:{counts.rules}  placed:{counts.placed}  tiles:{counts.tileRules}";

                    string capturedFile = file; // capture for closure
                    menu.AddItem(new GUIContent(label), false, () => {
                        if (EditorUtility.DisplayDialog(
                            "Restore Autosave",
                            $"Load autosave from {timestamp}?\n\nrules: {counts.rules}\nplaced objects: {counts.placed}\ntile rules: {counts.tileRules}\n\nThis will replace the current level data.",
                            "Restore", "Cancel"))
                        {
                            tilemapEditor.LoadFromAutosave(capturedFile);
                            SyncInspectorAfterLevelChange(container);
                        }
                    });
                }

                menu.ShowAsContext();
            });
            autosaveBtn.tooltip = "Browse autosave versions";
            autosaveBtn.AddToClassList("btn");
            autosaveBtn.AddToClassList("btn-icon");
            SetUIToolkitHeaderButtonIcon(autosaveBtn, "commit_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png");
            autosaveBtn.style.marginLeft = 2;
            autosaveBtn.style.borderTopWidth = 2;
            autosaveBtn.style.borderBottomWidth = 2;
            autosaveBtn.style.borderLeftWidth = 2;
            autosaveBtn.style.borderRightWidth = 2;
            autosaveBtn.style.borderTopColor = new StyleColor(new Color(0.9f, 0.65f, 0.2f, 1f));
            autosaveBtn.style.borderBottomColor = new StyleColor(new Color(0.9f, 0.65f, 0.2f, 1f));
            autosaveBtn.style.borderLeftColor = new StyleColor(new Color(0.9f, 0.65f, 0.2f, 1f));
            autosaveBtn.style.borderRightColor = new StyleColor(new Color(0.9f, 0.65f, 0.2f, 1f));
            autosaveBtn.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
            buttonsRow.Add(autosaveBtn);

            // New level button (+)
            var newLevelBtn = new Button(() => {
                if (tilemapEditor?.levels == null) return;
                var newLevel = new QuickTilemapEditor.LevelData {
                    levelName = $"Level{tilemapEditor.levels.Count + 1}",
                    properties = new System.Collections.Generic.List<QuickTilemapEditor.LevelProperty>(),
                    paintedTextures = new System.Collections.Generic.List<QuickTilemapEditor.PaintedTextureData>()
                };
                tilemapEditor.levels.Add(newLevel);
                var tileDict = tilemapEditor.BuildTileDictionary();
                tilemapEditor.LoadLevel(tilemapEditor.levels.Count - 1, tileDict);
                EditorUtility.SetDirty(tilemapEditor);
                SyncInspectorAfterLevelChange(container);
            });
            newLevelBtn.text = "+";
            newLevelBtn.AddToClassList("btn");
            newLevelBtn.AddToClassList("btn-icon");
            newLevelBtn.style.marginLeft = 4;
            newLevelBtn.style.fontSize = 16;
            newLevelBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            buttonsRow.Add(newLevelBtn);

            controlsColumn.Add(buttonsRow);
            mainRow.Add(controlsColumn);
            container.Add(mainRow);

            // Expanded content (created only when expanded)
            var expandedContent = new VisualElement();
            expandedContent.name = "expanded-content";
            expandedContent.style.display = levelManagerExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            expandedContent.style.marginTop = 8;
            expandedContent.style.paddingLeft = 24;
            
            PopulateLevelManagerExpandedContent(expandedContent);
            container.Add(expandedContent);

            return container;
        }

        private void SetUIToolkitHeaderButtonIcon(Button button, string assetFileName, float rotationDegrees = 0f)
        {
            if (button == null)
                return;

            button.text = string.Empty;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;
            button.Clear();

            Texture2D iconTexture = LoadOverlayIconTexture(assetFileName);
            if (iconTexture == null)
                return;

            var iconImage = new Image { image = iconTexture };
            iconImage.scaleMode = ScaleMode.ScaleToFit;
            iconImage.pickingMode = PickingMode.Ignore;
            iconImage.style.width = 18;
            iconImage.style.height = 18;
            iconImage.style.alignSelf = Align.Center;

            if (Mathf.Abs(rotationDegrees) > 0.01f)
                iconImage.style.rotate = new Rotate(new Angle(rotationDegrees));

            button.Add(iconImage);
        }

        private void RefreshLevelManagerContent(VisualElement container)
        {
            if (container == null) return;

            var expandFoldout = container.Q<Foldout>("expand-btn");
            if (expandFoldout != null)
                expandFoldout.SetValueWithoutNotify(levelManagerExpanded);

            // Update dropdown choices AND selection
            var levelDropdown = container.Q<DropdownField>("level-dropdown");
            if (levelDropdown != null && tilemapEditor?.levels != null)
            {
                var newChoices = new System.Collections.Generic.List<string>();
                for (int i = 0; i < tilemapEditor.levels.Count; i++)
                    newChoices.Add(tilemapEditor.levels[i].levelName);
                if (newChoices.Count == 0) newChoices.Add("No Level");

                levelDropdown.choices = newChoices;

                if (tilemapEditor.currentLevelIndex >= 0 && tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
                    levelDropdown.SetValueWithoutNotify(tilemapEditor.levels[tilemapEditor.currentLevelIndex].levelName);
            }

            var expandedContent = container.Q("expanded-content");
            if (expandedContent != null)
            {
                expandedContent.style.display = levelManagerExpanded ? DisplayStyle.Flex : DisplayStyle.None;
                if (levelManagerExpanded)
                {
                    expandedContent.Clear();
                    PopulateLevelManagerExpandedContent(expandedContent);
                }
            }

            // Refresh the GameObjectRules list so it reflects the newly loaded level
            if (gameObjectRulesUIToolkitContainer != null)
            {
                var rulesListContainer = gameObjectRulesUIToolkitContainer.Q("gameobject-rules-list");
                if (rulesListContainer != null)
                    RefreshGameObjectRulesList_UIToolkit(rulesListContainer);
            }

            // Refresh the TileRules list so it reflects the newly loaded level
            if (tileRulesUIToolkitContainer != null)
            {
                var tileRulesListContainer = tileRulesUIToolkitContainer.Q("tile-rules-list");
                if (tileRulesListContainer != null)
                    RefreshTileRulesList_UIToolkit(tileRulesListContainer);
            }

            UpdateUIToolkitVisibility();
            SceneView.RepaintAll();
            Repaint();
        }

        private void SyncInspectorAfterLevelChange(VisualElement levelManagerContainer = null)
        {
            ResetSelectionVisuals();
            EndBrushStroke_V2();

            isDrawingV2 = false;
            isPanningV2 = false;
            isDraggingSelectionV2 = false;
            isBoxSelectingV2 = false;
            hasLastInteractedCellV2 = false;
            hasHoveredCellV2 = false;
            selectionOffsetV2 = Vector3Int.zero;

            tilemapEditor?.ClearPreviewObject();

            if (tilemapEditor?.levels != null &&
                tilemapEditor.currentLevelIndex >= 0 &&
                tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
            {
                customLevelName = tilemapEditor.levels[tilemapEditor.currentLevelIndex].levelName;
            }

            SetupPropertyList();

            var targetContainer = levelManagerContainer ?? levelManagerUIToolkitContainer;
            if (targetContainer != null)
                RefreshLevelManagerContent(targetContainer);
            else
            {
                UpdateUIToolkitVisibility();
                SceneView.RepaintAll();
                Repaint();
            }
        }

        private QuickTilemapEditor.LevelData GetCurrentLevelData()
        {
            if (tilemapEditor?.levels == null)
                return null;

            int index = tilemapEditor.currentLevelIndex;
            if (index < 0 || index >= tilemapEditor.levels.Count)
                return null;

            return tilemapEditor.levels[index];
        }

        private void ApplyCenterToSurfaceMassSetting(bool enabled)
        {
            if (tilemapEditor == null)
                return;

            Undo.RecordObject(tilemapEditor, enabled ? "Enable Center Drawing On Save" : "Disable Center Drawing On Save");
            tilemapEditor.centerOriginToSurfaceMass = enabled;

            var currentLevel = GetCurrentLevelData();
            if (currentLevel != null)
                currentLevel.centerOriginToSurfaceMass = enabled;

            EditorUtility.SetDirty(tilemapEditor);
        }

        private void MarkCurrentLevelUiDirty()
        {
            if (tilemapEditor == null)
                return;

            EditorUtility.SetDirty(tilemapEditor);
            serializedObject.Update();
            serializedObject.ApplyModifiedProperties();
        }

        private void NormalizeLevelPropertyValue(QuickTilemapEditor.LevelProperty property)
        {
            if (property == null)
                return;

            property.value ??= string.Empty;

            switch (property.type)
            {
                case QuickTilemapEditor.PropertyType.Int:
                    if (!int.TryParse(property.value, out int intValue))
                        intValue = 0;
                    property.value = intValue.ToString();
                    break;
                case QuickTilemapEditor.PropertyType.Float:
                    if (!float.TryParse(property.value, out float floatValue))
                        floatValue = 0f;
                    property.value = floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case QuickTilemapEditor.PropertyType.Bool:
                    if (!bool.TryParse(property.value, out bool boolValue))
                        boolValue = false;
                    property.value = boolValue.ToString();
                    break;
                default:
                    break;
            }
        }

        private VisualElement CreateLevelPropertyRow(
            QuickTilemapEditor.LevelData currentLevel,
            QuickTilemapEditor.LevelProperty property,
            int propertyIndex,
            VisualElement refreshTarget)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginTop = 4;

            var keyField = new TextField();
            keyField.value = property.key ?? string.Empty;
            keyField.style.flexGrow = 1;
            keyField.style.minWidth = 140;
            keyField.style.maxWidth = 240;
            keyField.style.marginRight = 6;
            keyField.RegisterValueChangedCallback(evt => {
                property.key = evt.newValue;
                MarkCurrentLevelUiDirty();
            });
            row.Add(keyField);

            var typeField = new EnumField(property.type);
            typeField.style.width = 90;
            typeField.style.marginRight = 6;
            typeField.RegisterValueChangedCallback(evt => {
                property.type = (QuickTilemapEditor.PropertyType)evt.newValue;
                NormalizeLevelPropertyValue(property);
                MarkCurrentLevelUiDirty();
                RefreshLevelManagerContent(refreshTarget);
            });
            row.Add(typeField);

            switch (property.type)
            {
                case QuickTilemapEditor.PropertyType.Int:
                    int intValue = int.TryParse(property.value, out int parsedInt) ? parsedInt : 0;
                    var intField = new IntegerField();
                    intField.value = intValue;
                    intField.style.flexGrow = 1;
                    intField.style.marginRight = 6;
                    intField.RegisterValueChangedCallback(evt => {
                        property.value = evt.newValue.ToString();
                        MarkCurrentLevelUiDirty();
                    });
                    row.Add(intField);
                    break;

                case QuickTilemapEditor.PropertyType.Float:
                    float floatValue = float.TryParse(property.value, out float parsedFloat) ? parsedFloat : 0f;
                    var floatField = new FloatField();
                    floatField.value = floatValue;
                    floatField.style.flexGrow = 1;
                    floatField.style.marginRight = 6;
                    floatField.RegisterValueChangedCallback(evt => {
                        property.value = evt.newValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        MarkCurrentLevelUiDirty();
                    });
                    row.Add(floatField);
                    break;

                case QuickTilemapEditor.PropertyType.Bool:
                    bool boolValue = bool.TryParse(property.value, out bool parsedBool) && parsedBool;
                    var boolField = new Toggle();
                    boolField.value = boolValue;
                    boolField.style.flexGrow = 1;
                    boolField.style.marginRight = 6;
                    boolField.RegisterValueChangedCallback(evt => {
                        property.value = evt.newValue.ToString();
                        MarkCurrentLevelUiDirty();
                    });
                    row.Add(boolField);
                    break;

                default:
                    var valueField = new TextField();
                    valueField.value = property.value ?? string.Empty;
                    valueField.style.flexGrow = 1;
                    valueField.style.marginRight = 6;
                    valueField.RegisterValueChangedCallback(evt => {
                        property.value = evt.newValue;
                        MarkCurrentLevelUiDirty();
                    });
                    row.Add(valueField);
                    break;
            }

            var removeButton = new Button(() => {
                if (currentLevel.properties == null || propertyIndex < 0 || propertyIndex >= currentLevel.properties.Count)
                    return;

                currentLevel.properties.RemoveAt(propertyIndex);
                MarkCurrentLevelUiDirty();
                RefreshLevelManagerContent(refreshTarget);
            });
            removeButton.text = "✖";
            removeButton.AddToClassList("btn");
            removeButton.AddToClassList("btn-icon");
            removeButton.AddToClassList("btn-danger");
            row.Add(removeButton);

            return row;
        }

        private void PopulateLevelManagerExpandedContent(VisualElement container)
        {
            if (tilemapEditor?.levels == null) return;

            var currentLevel = tilemapEditor.currentLevelIndex >= 0 && tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count
                ? tilemapEditor.levels[tilemapEditor.currentLevelIndex] : null;
            var refreshTarget = container.parent as VisualElement ?? levelManagerUIToolkitContainer;

            // === LEVELS HEADER ===
            var levelsHeader = new VisualElement();
            levelsHeader.style.flexDirection = FlexDirection.Row;
            levelsHeader.style.marginBottom = 4;

            var levelsLabel = new Label($"📂 Levels ({tilemapEditor.levels.Count})");
            levelsLabel.style.color = new StyleColor(new Color(0.5f, 0.8f, 1f));
            levelsLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            levelsLabel.style.flexGrow = 1;
            levelsHeader.Add(levelsLabel);
            container.Add(levelsHeader);

            // === CURRENT LEVEL DETAILS ===
            if (currentLevel != null)
            {
                var levelCard = new VisualElement();
                levelCard.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.1f, 1f));
                levelCard.style.paddingLeft = 8;
                levelCard.style.paddingRight = 8;
                levelCard.style.paddingTop = 6;
                levelCard.style.paddingBottom = 6;
                levelCard.style.marginBottom = 4;
                levelCard.style.borderLeftWidth = 3;
                levelCard.style.borderLeftColor = new StyleColor(new Color(0.29f, 0.87f, 0.5f, 1f)); // Green

                // Level Name
                var nameRow = new VisualElement();
                nameRow.style.flexDirection = FlexDirection.Row;
                nameRow.style.marginBottom = 4;
                nameRow.style.alignItems = Align.Center;

                var nameLabel = new Label("Level Name");
                nameLabel.style.width = 100;
                nameLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
                nameRow.Add(nameLabel);

                var nameField = new TextField();
                nameField.value = currentLevel.levelName;
                nameField.style.flexGrow = 1;
                nameField.RegisterValueChangedCallback(evt => {
                    currentLevel.levelName = evt.newValue;
                    MarkCurrentLevelUiDirty();
                    RefreshLevelManagerContent(refreshTarget);
                });
                nameRow.Add(nameField);
                levelCard.Add(nameRow);

                // Json File
                var jsonRow = new VisualElement();
                jsonRow.style.flexDirection = FlexDirection.Row;
                jsonRow.style.marginBottom = 4;
                jsonRow.style.alignItems = Align.Center;

                var jsonLabel = new Label("Json File");
                jsonLabel.style.width = 100;
                jsonLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
                jsonRow.Add(jsonLabel);

                var jsonField = new ObjectField();
                jsonField.objectType = typeof(TextAsset);
                jsonField.value = currentLevel.jsonFile;
                jsonField.style.flexGrow = 1;
                jsonField.RegisterValueChangedCallback(evt => {
                    currentLevel.jsonFile = evt.newValue as TextAsset;
                    MarkCurrentLevelUiDirty();
                });
                jsonRow.Add(jsonField);
                levelCard.Add(jsonRow);

                currentLevel.properties ??= new System.Collections.Generic.List<QuickTilemapEditor.LevelProperty>();

                var propertiesFoldout = new Foldout();
                propertiesFoldout.text = $"Properties ({currentLevel.properties.Count})";
                propertiesFoldout.value = _showLevelProperties;
                propertiesFoldout.style.marginBottom = 6;
                propertiesFoldout.RegisterValueChangedCallback(evt => {
                    _showLevelProperties = evt.newValue;
                });

                var addPropertyButton = new Button(() => {
                    currentLevel.properties.Add(new QuickTilemapEditor.LevelProperty
                    {
                        key = $"property_{currentLevel.properties.Count + 1}",
                        type = QuickTilemapEditor.PropertyType.String,
                        value = string.Empty
                    });
                    MarkCurrentLevelUiDirty();
                    RefreshLevelManagerContent(refreshTarget);
                });
                addPropertyButton.text = "+ Add Property";
                addPropertyButton.AddToClassList("btn");
                addPropertyButton.style.marginTop = 4;
                addPropertyButton.style.marginBottom = 4;
                propertiesFoldout.Add(addPropertyButton);

                for (int propertyIndex = 0; propertyIndex < currentLevel.properties.Count; propertyIndex++)
                {
                    var property = currentLevel.properties[propertyIndex];
                    NormalizeLevelPropertyValue(property);
                    propertiesFoldout.Add(CreateLevelPropertyRow(currentLevel, property, propertyIndex, refreshTarget));
                }

                levelCard.Add(propertiesFoldout);

                // Center Origin checkbox (per level)
                var originRow = new VisualElement();
                originRow.style.flexDirection = FlexDirection.Row;
                originRow.style.alignItems = Align.Center;

                var originLabel = new Label(AutoCenterLevelLabel);
                originLabel.tooltip = AutoCenterLevelTooltip;
                originLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
                originLabel.style.flexGrow = 1;
                originRow.Add(originLabel);

                var originToggle = new Toggle();
                originToggle.tooltip = AutoCenterLevelTooltip;
                originToggle.value = currentLevel.centerOriginToSurfaceMass;
                originToggle.RegisterValueChangedCallback(evt => {
                    ApplyCenterToSurfaceMassSetting(evt.newValue);
                    RefreshLevelManagerContent(refreshTarget);
                });
                originRow.Add(originToggle);
                levelCard.Add(originRow);

                container.Add(levelCard);
            }

            // === LEVEL LIST ===
            for (int i = 0; i < tilemapEditor.levels.Count; i++)
            {
                int levelIndex = i;
                var level = tilemapEditor.levels[i];
                bool isCurrent = i == tilemapEditor.currentLevelIndex;

                var levelRow = new VisualElement();
                levelRow.style.flexDirection = FlexDirection.Row;
                levelRow.style.marginBottom = 2;

                var levelBtn = new Button(() => {
                    var tileDict = tilemapEditor.BuildTileDictionary();
                    tilemapEditor.LoadLevel(levelIndex, tileDict);
                    SyncInspectorAfterLevelChange(container.parent);
                });
                levelBtn.text = $"{i + 1}. {level.levelName}";
                levelBtn.style.flexGrow = 1;
                levelBtn.style.height = 24;
                levelBtn.style.backgroundColor = isCurrent
                    ? new StyleColor(new Color(0.25f, 0.6f, 0.9f, 1f))
                    : new StyleColor(new Color(0.18f, 0.18f, 0.18f, 1f));
                levelBtn.style.color = new StyleColor(Color.white);
                levelRow.Add(levelBtn);

                container.Add(levelRow);
            }

            // === ADD/REMOVE BUTTONS ===
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.FlexEnd;
            btnRow.style.marginTop = 8;

            var addBtn = new Button(() => {
                var newLevel = new QuickTilemapEditor.LevelData {
                    levelName = $"Level{tilemapEditor.levels.Count + 1}",
                    properties = new System.Collections.Generic.List<QuickTilemapEditor.LevelProperty>(),
                    paintedTextures = new System.Collections.Generic.List<QuickTilemapEditor.PaintedTextureData>()
                };
                tilemapEditor.levels.Add(newLevel);
                // Load the new (empty) level — clears the scene
                var tileDict = tilemapEditor.BuildTileDictionary();
                tilemapEditor.LoadLevel(tilemapEditor.levels.Count - 1, tileDict);
                EditorUtility.SetDirty(tilemapEditor);
                SyncInspectorAfterLevelChange(container.parent);
            });
            addBtn.text = "+";
            addBtn.style.width = 32;
            addBtn.style.height = 24;
            addBtn.style.backgroundColor = new StyleColor(new Color(0.3f, 0.5f, 0.3f, 1f));
            addBtn.style.color = new StyleColor(Color.white);
            btnRow.Add(addBtn);

            var removeBtn = new Button(() => {
                if (tilemapEditor.levels.Count > 1 && tilemapEditor.currentLevelIndex >= 0)
                {
                    tilemapEditor.levels.RemoveAt(tilemapEditor.currentLevelIndex);
                    tilemapEditor.currentLevelIndex = Mathf.Max(0, tilemapEditor.currentLevelIndex - 1);
                    EditorUtility.SetDirty(tilemapEditor);
                    RefreshLevelManagerContent(container.parent);
                }
            });
            removeBtn.text = "-";
            removeBtn.style.width = 32;
            removeBtn.style.height = 24;
            removeBtn.style.marginLeft = 4;
            removeBtn.style.backgroundColor = new StyleColor(new Color(0.5f, 0.3f, 0.3f, 1f));
            removeBtn.style.color = new StyleColor(Color.white);
            btnRow.Add(removeBtn);

            container.Add(btnRow);

            // === RELOAD BUTTON ===
            var reloadBtn = new Button(() => {
                tilemapEditor?.ReloadCurrentLevel();
                SyncInspectorAfterLevelChange(container.parent);
            });
            reloadBtn.text = "🔄 Reload Level";
            reloadBtn.style.marginTop = 8;
            reloadBtn.style.height = 28;
            reloadBtn.style.backgroundColor = new StyleColor(new Color(0.3f, 0.3f, 0.5f, 1f));
            reloadBtn.style.color = new StyleColor(Color.white);
            container.Add(reloadBtn);
        }


        private void AddHeaderImage(VisualElement root, Texture2D texture)
        {
            Image headerImageElement = new Image { image = texture };
            headerImageElement.style.width = Length.Percent(100);
            headerImageElement.scaleMode = ScaleMode.ScaleToFit;
            headerImageElement.style.marginBottom = 10;
            headerImageElement.style.maxHeight = 80; // Fixed max height to save space

            root.Insert(0, headerImageElement);
        }


        private List<QuickTilemapEditor.LevelData> DiscoverSavedLevelsFromResources()
        {
            var discovered = new List<QuickTilemapEditor.LevelData>();
            const string levelsFolder = "Assets/BEKKOLOCO/QuickTile/Resources/Levels";

            if (!AssetDatabase.IsValidFolder(levelsFolder))
                return discovered;

            string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { levelsFolder });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                string normalized = assetPath.Replace("\\", "/");
                if (normalized.Contains("/Autosaves/"))
                    continue;

                string extension = Path.GetExtension(normalized);
                if (!string.Equals(extension, ".bytes", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
                    continue;

                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(normalized);
                if (asset == null)
                    continue;

                discovered.Add(new QuickTilemapEditor.LevelData
                {
                    levelName = Path.GetFileNameWithoutExtension(normalized),
                    jsonFile = asset,
                    properties = new List<QuickTilemapEditor.LevelProperty>(),
                    paintedTextures = new List<QuickTilemapEditor.PaintedTextureData>(),
                    centerOriginToSurfaceMass = true
                });
            }

            return discovered
                .OrderBy(level => level.levelName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void OnEnable()
        {
            tilemapEditor = target as QuickTilemapEditor;
            if (tilemapEditor == null)
            {
                Debug.LogError("❌ The target of the inspector is not a QuickTilemapEditor or is null.");
                return;
            }

            activeInspectors.Add(this);

            // 🔑 SAFETY: Ensure levels list is always initialized
            if (tilemapEditor.levels == null)
            {
                tilemapEditor.levels = new List<QuickTilemapEditor.LevelData>();
                Debug.Log("Initialized empty levels list");
            }

            // 🔑 SAFETY: Validate currentLevelIndex
            if (tilemapEditor.levels.Count == 0)
            {
                var savedLevels = DiscoverSavedLevelsFromResources();
                if (savedLevels.Count > 0)
                {
                    tilemapEditor.levels.AddRange(savedLevels);
                    tilemapEditor.currentLevelIndex = 0;
                    EditorUtility.SetDirty(tilemapEditor);
                    Debug.Log($"[QuickTile] Restored {savedLevels.Count} saved level(s) from Assets/BEKKOLOCO/QuickTile/Resources/Levels.");
                }
                else
                {
                    var defaultLevel = new QuickTilemapEditor.LevelData
                    {
                        levelName = "Level1",
                        properties = new List<QuickTilemapEditor.LevelProperty>(),
                        paintedTextures = new List<QuickTilemapEditor.PaintedTextureData>(),
                        centerOriginToSurfaceMass = true
                    };
                    tilemapEditor.levels.Add(defaultLevel);
                    tilemapEditor.currentLevelIndex = 0;
                    EditorUtility.SetDirty(tilemapEditor);
                    Debug.Log("No levels found, auto-created 'Level1' as default.");
                }
            }
            else if (tilemapEditor.currentLevelIndex < 0 || tilemapEditor.currentLevelIndex >= tilemapEditor.levels.Count)
            {
                Debug.LogWarning($"Invalid currentLevelIndex: {tilemapEditor.currentLevelIndex}. Clamping to valid range.");
                tilemapEditor.currentLevelIndex = Mathf.Clamp(tilemapEditor.currentLevelIndex, 0, tilemapEditor.levels.Count - 1);
            }

            // 🔑 Restore automatically the prefabs and resynchronize
            if (tilemapEditor.gameObjectRules != null)
            {
                tilemapEditor.RestorePrefabReferences();
                tilemapEditor.ResynchronizeGameObjectsFromScene();
                bool migratedHeights = tilemapEditor.MergeLegacyGameObjectHeights();
                bool migratedOffsets = tilemapEditor.UpgradeLegacyInstanceYOffsets();
                if (migratedHeights || migratedOffsets)
                    EditorUtility.SetDirty(tilemapEditor);
            }

            tilemapEditor.CleanupLegacyPathVisuals();

            /*
            if (tilemapEditor.levels == null || tilemapEditor.levels.Count == 0)
                tilemapEditor.currentLevelIndex = -1;
            */

            targetTilemapProperty = serializedObject.FindProperty("targetTilemap");
            activeTileProperty = serializedObject.FindProperty("activeTile");
            recentTilesProperty = serializedObject.FindProperty("recentTiles");
            gridSizeProperty = serializedObject.FindProperty("gridSize");
            cellSizeProperty = serializedObject.FindProperty("cellSize");
            editorEnabledProperty = serializedObject.FindProperty("editorEnabled");
            brushSizeProperty = serializedObject.FindProperty("brushSize");
            useCustomSizeProperty = serializedObject.FindProperty("useCustomSize");

            customWidth = tilemapEditor.gridSize.x;
            customHeight = tilemapEditor.gridSize.y;

            drawButtonContent = new GUIContent(" Draw", EditorGUIUtility.IconContent("Grid.PaintTool").image, "Paint with selected tile");
            eraseButtonContent = new GUIContent(" Erase Select", EditorGUIUtility.IconContent("Grid.EraserTool").image, "Erase tiles");
            eraseAllButtonContent = new GUIContent(" Erase All", EditorGUIUtility.IconContent("Grid.EraserTool").image, "Erase all tiles");


            // Get the levels property collapsible
            SerializedProperty levelsProp = serializedObject.FindProperty("levels");
            if (levelsProp != null && levelsProp.arraySize > 0 && tilemapEditor.currentLevelIndex < levelsProp.arraySize)
            {
                SerializedProperty levelProperties = levelsProp.GetArrayElementAtIndex(tilemapEditor.currentLevelIndex)
                                                               .FindPropertyRelative("properties");
                propertyList = new ReorderableList(serializedObject, levelProperties, true, true, true, true);


                propertyList.drawHeaderCallback = (Rect rect) => { /* do nothing */ };


                propertyList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
                {
                    SerializedProperty element = propertyList.serializedProperty.GetArrayElementAtIndex(index);
                    rect.y += 2;
                    float fieldWidth = rect.width / 3;

                    EditorGUI.PropertyField(new Rect(rect.x, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight),
                                              element.FindPropertyRelative("key"), GUIContent.none);
                    EditorGUI.PropertyField(new Rect(rect.x + fieldWidth, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight),
                                              element.FindPropertyRelative("type"), GUIContent.none);

                    SerializedProperty typeProp = element.FindPropertyRelative("type");
                    SerializedProperty valueProp = element.FindPropertyRelative("value");

                    if ((QuickTilemapEditor.PropertyType)typeProp.enumValueIndex == QuickTilemapEditor.PropertyType.Bool)
                    {
                        bool boolVal = valueProp.stringValue.ToLower() == "true";
                        bool newBool = EditorGUI.Toggle(new Rect(rect.x + 2 * fieldWidth, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight), boolVal);
                        valueProp.stringValue = newBool ? "true" : "false";
                    }
                    else
                    {
                        EditorGUI.PropertyField(new Rect(rect.x + 2 * fieldWidth, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight),
                                                  valueProp, GUIContent.none);
                    }
                };

            }
            else
            {
                //  //Debug.Log(Warning("Levels list is empty. Please add at least one level.");
                propertyList = null;
            }

            // Setup property list only if we have valid levels
            if (tilemapEditor.levels != null && tilemapEditor.levels.Count > 0 &&
                tilemapEditor.currentLevelIndex >= 0 && tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
            {
                SetupPropertyList();
                customLevelName = tilemapEditor.levels[tilemapEditor.currentLevelIndex].levelName;
            }
            else
            {
                propertyList = null;
                customLevelName = "";
            }

            LoadAllTiles();
            EnsureTileRuleUIStateCount();
        }

        private void OnDisable()
        {
            activeInspectors.Remove(this);
        }






        private void UpdateRadialHillDeformers(QuickTilemapEditor.TileRule rule)
        {
            if (rule?.customTargetTilemap == null) return;

            // Trouve tous les RadialHillDeformer enfants de cette tilemap
            var deformers = rule.customTargetTilemap.GetComponentsInChildren<RadialHillDeformer>(true);

            foreach (var deformer in deformers)
            {
                // Applique la formule : worldMinY = yOffset - sizeY
                float newWorldMinY = rule.yOffset - rule.sizeY;
                deformer.worldMinY = newWorldMinY;
                deformer.clampWorldMinY = true; // Active le clamp automatiquement

                // Force la re-application de la déformation
                deformer.SyncWithTileRuleRuntime();

                EditorUtility.SetDirty(deformer);
            }
        }


        private void ValidateAndRestorePrefabs()
        {
            if (tilemapEditor.gameObjectRules == null) return;

            bool needsRestore = false;
            foreach (var rule in tilemapEditor.gameObjectRules)
            {
                if (rule.prefab == null && !string.IsNullOrEmpty(rule.prefabResourcePath))
                {
                    needsRestore = true;
                    break;
                }
            }

            if (needsRestore)
            {
                Debug.Log("🔄 Restoring missing prefab references...");
                tilemapEditor.RestorePrefabReferences();
                EditorUtility.SetDirty(tilemapEditor);
            }
        }


        private void SetupPropertyList()
        {
            SerializedProperty levelsProp = serializedObject.FindProperty("levels");

            // Validate the levels array and currentLevelIndex explicitly:
            if (levelsProp == null || levelsProp.arraySize == 0 || tilemapEditor.currentLevelIndex < 0 || tilemapEditor.currentLevelIndex >= levelsProp.arraySize)
            {
                propertyList = null;
                //  //Debug.Log(Warning($"Levels array is empty or currentLevelIndex is invalid: currentLevelIndex={tilemapEditor.currentLevelIndex}, arraySize={levelsProp?.arraySize}");
                return;
            }

            // Proceed safely :
            SerializedProperty levelProperties = levelsProp
                .GetArrayElementAtIndex(tilemapEditor.currentLevelIndex)
                .FindPropertyRelative("properties");

            propertyList = new ReorderableList(serializedObject, levelProperties, true, true, true, true);

            propertyList.drawHeaderCallback = (Rect rect) =>
            {
                EditorGUI.LabelField(rect, "Level Properties");
            };

            propertyList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                SerializedProperty element = propertyList.serializedProperty.GetArrayElementAtIndex(index);
                rect.y += 2;
                float fieldWidth = rect.width / 3;

                EditorGUI.PropertyField(new Rect(rect.x, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight),
                    element.FindPropertyRelative("key"), GUIContent.none);

                EditorGUI.PropertyField(new Rect(rect.x + fieldWidth, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight),
                    element.FindPropertyRelative("type"), GUIContent.none);

                SerializedProperty typeProp = element.FindPropertyRelative("type");
                SerializedProperty valueProp = element.FindPropertyRelative("value");

                if ((QuickTilemapEditor.PropertyType)typeProp.enumValueIndex == QuickTilemapEditor.PropertyType.Bool)
                {
                    bool boolVal = valueProp.stringValue.ToLower() == "true";
                    bool newBool = EditorGUI.Toggle(new Rect(rect.x + 2 * fieldWidth, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight), boolVal);
                    valueProp.stringValue = newBool ? "true" : "false";
                }
                else
                {
                    EditorGUI.PropertyField(new Rect(rect.x + 2 * fieldWidth, rect.y, fieldWidth - 5, EditorGUIUtility.singleLineHeight),
                        valueProp, GUIContent.none);
                }
            };
        }





        private void LoadAllTiles()
        {
            tileDict.Clear();
            string[] guids = AssetDatabase.FindAssets("t:TileBase");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TileBase tile = AssetDatabase.LoadAssetAtPath<TileBase>(path);
                if (tile != null && !tileDict.ContainsKey(tile.name))
                    tileDict.Add(tile.name, tile);
            }
        }

        private void ProcessAddressablesForGameObjectRules()
        {
            QuickTilemapEditor tilemapEditor = (QuickTilemapEditor)target;

            // Iterate through each GameObjectRule and add it to Addressables if necessary
            foreach (var goRule in tilemapEditor.gameObjectRules)
            {
                if (goRule.prefab != null)
                {
                    // Call the AddPrefabToAddressables method to add the prefab and set the path
                    tilemapEditor.AddPrefabToAddressables(goRule.prefab);
                }
            }

            // Mark the editor as dirty to save changes
            EditorUtility.SetDirty(tilemapEditor);

            // Notify the user that the process is complete
            //Debug.Log(("Addressable paths updated for all GameObjectRules.");
        }


        private bool HandleSaveShortcut()
        {
            Event currentEvent = Event.current;
            if (currentEvent == null)
                return false;

            bool handled = false;

            // Debug pour voir si les événements arrivent
            if (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.S)
            {
                Debug.Log($"[QuickTile] Key S pressed - Modifiers: {currentEvent.modifiers}");
            }

            if (currentEvent.type == EventType.ValidateCommand && currentEvent.commandName == "Save")
            {
                Debug.Log("[QuickTile] ValidateCommand Save");
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.ExecuteCommand && currentEvent.commandName == "Save")
            {
                Debug.Log("[QuickTile] ExecuteCommand Save");
                currentEvent.Use();
                handled = TrySaveCurrentLevel();
            }
            else if (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.S)
            {
                if ((currentEvent.modifiers & (EventModifiers.Command | EventModifiers.Control)) != 0)
                {
                    Debug.Log("[QuickTile] Ctrl+S detected, trying to save...");
                    currentEvent.Use();
                    handled = TrySaveCurrentLevel();
                }
            }

            if (handled)
            {
                GUIUtility.ExitGUI();
            }

            return handled;
        }

        public bool TrySaveCurrentLevel()
        {
            QuickTilemapEditor tilemapEditor = (QuickTilemapEditor)target;

            string trimmedName = customLevelName.Trim();
            if (string.IsNullOrEmpty(trimmedName))
            {
                EditorUtility.DisplayDialog("Error", "Please enter a level name.", "OK");
                return false;
            }

            customLevelName = trimmedName;

            var existingLevel = tilemapEditor.levels.Find(l => l.levelName == customLevelName);
            if (existingLevel == null)
            {
                existingLevel = new QuickTilemapEditor.LevelData
                {
                    levelName = customLevelName,
                    properties = new List<QuickTilemapEditor.LevelProperty>()
                };
                tilemapEditor.levels.Add(existingLevel);
            }

            tilemapEditor.currentLevelIndex = tilemapEditor.levels.IndexOf(existingLevel);

            string folderPath = Path.Combine(Application.dataPath, "BEKKOLOCO/QuickTile/Resources/Levels");
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            string fullPath = Path.Combine(folderPath, customLevelName + ".bytes");

            ProcessAddressablesForGameObjectRules();

            tilemapEditor.SaveTilemapToJson(fullPath);
            AssetDatabase.Refresh();

            string assetPath = $"Assets/BEKKOLOCO/QuickTile/Resources/Levels/{customLevelName}.bytes";
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);

            existingLevel.jsonFile = asset;
            tilemapEditor.isVirtual = false;

            serializedObject.Update();
            serializedObject.ApplyModifiedProperties();

            EditorUtility.SetDirty(tilemapEditor);
            AssetDatabase.SaveAssets();

            Debug.Log($"[QuickTile] Saved level '{customLevelName}' to {assetPath}");

            foreach (var goRule in tilemapEditor.gameObjectRules)
            {
                if (goRule.prefab != null)
                {
                    AddPrefabToAddressables(goRule.prefab);
                }
            }

            return true;
        }

        public override void OnInspectorGUI()
        {
            HandleTexturePicker();

            if (HandleSaveShortcut())
            {
                return;
            }

            // When using UI Toolkit, skip all IMGUI drawing (UI Toolkit handles everything)
            if (useUIToolkit)
            {
                RefreshTexturePaintSectionIfNeeded_UIToolkit();
                serializedObject.Update();
                serializedObject.ApplyModifiedProperties();
                return;
            }

            if (!string.IsNullOrEmpty(tilemapEditor.loadedJsonContent))
            {
                GUILayout.Space(10);
                EditorGUILayout.LabelField("Raw Level JSON Content", EditorStyles.boldLabel);

                // Display the raw JSON content in a scrollable text area
                EditorGUILayout.TextArea(tilemapEditor.loadedJsonContent, GUILayout.Height(200)); // Adjust height as needed
            }

            if (tilemapEditor.levels != null)
            {
                if (GUILayout.Button(" ˚｡⋆ Create New Empty Level 🧼 ⋆｡˚",
                          GUILayout.Height(30),
                          GUILayout.ExpandWidth(true)))
                {
                    tilemapEditor.ClearCurrentLevel();

                    // 🧹 DESTROY Tilemap GameObjects for TileRules
                    foreach (var rule in tilemapEditor.tileRules)
                    {
                        if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                        {
                            GameObject tilemapGO = rule.customTargetTilemap.gameObject;
                            if (tilemapGO != null)
                            {
                                Undo.DestroyObjectImmediate(tilemapGO); // safer in editor
                            }
                        }
                    }

                    // 🧹 Clear lists
                    tilemapEditor.tileRules.Clear();               // Clear all tile rules
                    tilemapEditor.gameObjectRules.Clear();          // Clear all GameObject rules
                    tilemapEditor.texturePaintRules.Clear();        // Clear all texture paint rules
                    tilemapEditor.paths.Clear();                    // Clear paths
                    tilemapEditor.placedObjects.Clear();            // Clear placed object data
                    tilemapEditor.instantiatedGameObjects.Clear();  // Clear instantiated prefabs
                    tilemapEditor.texturePaintMask.Clear();         // Clear texture mask CPU side

                    // 🧹 Also clear GPU RenderTexture (black out)
                    if (tilemapEditor.paintMaskTexture != null)
                    {
                        var old = RenderTexture.active;
                        RenderTexture.active = tilemapEditor.paintMaskTexture;
                        GL.Clear(true, true, Color.black);
                        RenderTexture.active = old;
                    }

                    tilemapEditor.UpdateBlendPreviewMaterial();     // Refresh blending preview

                    // Reset selection
                    tilemapEditor.selectedTileRuleIndex = -1;
                    tilemapEditor.selectedGameObjectRuleIndex = -1;
                    tilemapEditor.selectedPathIndex = -1;
                    tilemapEditor.selectedTextureRule = null;

                    Repaint();
                    return;
                }


                EditorGUILayout.Space();
            }


            GUIStyle bigBoldLabel = new GUIStyle(EditorStyles.boldLabel);
            bigBoldLabel.fontSize = 18; // set desired font size
            EditorGUILayout.LabelField("📂 Level Manager", bigBoldLabel);

            EditorGUI.BeginChangeCheck();
            bool newAutoReload = EditorGUILayout.Toggle(
                new GUIContent("🔄 Auto-Reload Level",
                "Décocher pour travailler sur le niveau sans rechargement automatique. Vos modifications sur la scène seront conservées."),
                tilemapEditor.autoReloadOnLevelChange
            );
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(tilemapEditor, "Toggle Auto Reload");
                tilemapEditor.autoReloadOnLevelChange = newAutoReload;
                EditorUtility.SetDirty(tilemapEditor);
            }

            // Message d'avertissement quand l'auto-reload est désactivé
            if (!tilemapEditor.autoReloadOnLevelChange)
            {
                EditorGUILayout.HelpBox(
                    "⚠️ Rechargement automatique DÉSACTIVÉ\n" +
                    "• Vos modifications sur la scène sont conservées\n" +
                    "• Le niveau ne se recharge pas automatiquement\n" +
                    "• Utilisez le bouton 'Force Reload' ci-dessous pour recharger manuellement",
                    MessageType.Warning
                );
            }

            EditorGUILayout.Space(5);



            GUILayout.Space(10);

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Level Name:", GUILayout.Width(80));
            customLevelName = EditorGUILayout.TextField(customLevelName);
            EditorGUILayout.EndHorizontal();

            serializedObject.Update();
            serializedObject.ApplyModifiedProperties();


            int count = tilemapEditor.levels?.Count ?? 0;
            if (tilemapEditor.currentLevelIndex >= count)
            {
                if (count > 0)
                {
                    tilemapEditor.currentLevelIndex = count - 1;
                    tilemapEditor.LoadLevel(tilemapEditor.currentLevelIndex, tileDict);
                    SyncInspectorAfterLevelChange();
                }
                else
                {
                    tilemapEditor.ClearCurrentLevel();
                }

                SetupPropertyList();
                Repaint();
            }


            SerializedProperty levelsProp = serializedObject.FindProperty("levels");


            if (levelsProp != null && levelsProp.arraySize > 0 && propertyList != null)
            {


                // 1) Draw custom foldout for level properties
                _showLevelProperties = EditorGUILayout.Foldout(_showLevelProperties, "Level Properties", true);
                if (_showLevelProperties)
                {
                    EditorGUI.BeginChangeCheck();
                    bool newCenterToSurfaceMass = EditorGUILayout.Toggle(
                        new GUIContent(AutoCenterLevelLabel, AutoCenterLevelTooltip),
                        tilemapEditor.centerOriginToSurfaceMass
                    );
                    if (EditorGUI.EndChangeCheck())
                        ApplyCenterToSurfaceMassSetting(newCenterToSurfaceMass);

                    // 2) Draw the reorderable list here
                    propertyList.DoLayoutList();
                }

                // 3) Finally, draw the whole levels property if needed
                EditorGUILayout.PropertyField(levelsProp, true);

                var currentLevelData = GetCurrentLevelData();
                if (currentLevelData != null &&
                    currentLevelData.centerOriginToSurfaceMass != tilemapEditor.centerOriginToSurfaceMass)
                {
                    ApplyCenterToSurfaceMassSetting(currentLevelData.centerOriginToSurfaceMass);
                }
            }




            else
            {

                EditorGUILayout.HelpBox("No levels created yet.", MessageType.Info);
            }

            EditorGUILayout.BeginHorizontal();

            // Save Button Code
            if (GUILayout.Button("💾 Save", GUILayout.Height(30), GUILayout.ExpandWidth(true)))
            {
                if (TrySaveCurrentLevel())
                {
                    GUIUtility.ExitGUI();
                }
            }






            // ——— Load Button ———
            if (GUILayout.Button("📂 Load", GUILayout.Height(30), GUILayout.ExpandWidth(true)))
            {
                string folder = Path.Combine(Application.dataPath, "BEKKOLOCO/QuickTile/Resources/Levels");
                string path = EditorUtility.OpenFilePanel("Load Level", folder, "bytes");
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        // Extract level name from file
                        string fileName = Path.GetFileNameWithoutExtension(path);

                        // SAFETY: Ensure levels list exists
                        if (tilemapEditor.levels == null)
                        {
                            tilemapEditor.levels = new List<QuickTilemapEditor.LevelData>();
                        }

                        // Check if this level already exists in our levels list
                        int existingIndex = tilemapEditor.levels.FindIndex(l => l.levelName == fileName);

                        if (existingIndex >= 0)
                        {
                            // Level exists, select it
                            tilemapEditor.currentLevelIndex = existingIndex;
                            Debug.Log($"Found existing level '{fileName}' at index {existingIndex}");
                        }
                        else
                        {
                            // Create new level entry
                            var newLevel = new QuickTilemapEditor.LevelData
                            {
                                levelName = fileName,
                                properties = new List<QuickTilemapEditor.LevelProperty>(),
                                paintedTextures = new List<QuickTilemapEditor.PaintedTextureData>(),
                                centerOriginToSurfaceMass = true // default value
                            };

                            // Try to load the JSON file as TextAsset for the level
                            string assetPath = $"Assets/BEKKOLOCO/QuickTile/Resources/Levels/{fileName}.bytes";
                            TextAsset jsonAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
                            if (jsonAsset != null)
                            {
                                newLevel.jsonFile = jsonAsset;
                            }

                            tilemapEditor.levels.Add(newLevel);
                            tilemapEditor.currentLevelIndex = tilemapEditor.levels.Count - 1;

                            Debug.Log($"Created new level '{fileName}' at index {tilemapEditor.currentLevelIndex}");
                        }

                        // Now it's safe to load the tilemap using the existing tileDict
                        tilemapEditor.LoadTilemapFromJson(path, tileDict);

                        tilemapEditor.isVirtual = false;
                        AssetDatabase.Refresh();
                        customLevelName = fileName;

                        // Update the property list for the new/selected level
                        SyncInspectorAfterLevelChange();

                        Debug.Log($"Successfully loaded level: {fileName}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Failed to load level: {ex.Message}");
                        EditorUtility.DisplayDialog("Load Error",
                            $"Failed to load level:\n{ex.Message}", "OK");
                    }
                }
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.EndHorizontal();


            // Bouton de rechargement forcé (visible uniquement quand auto-reload est désactivé)
            if (!tilemapEditor.autoReloadOnLevelChange)
            {
                EditorGUILayout.Space(5);
                GUI.backgroundColor = new Color(1f, 0.8f, 0.4f); // Couleur orange/jaune
                if (GUILayout.Button("🔄 Force Reload Current Level", GUILayout.Height(28)))
                {
                    Undo.RecordObject(tilemapEditor, "Force Reload Level");
                    tilemapEditor.ReloadCurrentLevel();
                    SyncInspectorAfterLevelChange();
                    GUIUtility.ExitGUI();
                }
                GUI.backgroundColor = Color.white; // Reset couleur
            }



            EditorGUILayout.BeginHorizontal();

            // Replace the Previous button code:
            if (GUILayout.Button("◀ Previous"))
            {
                if (tilemapEditor.levels != null && tilemapEditor.levels.Count > 0)
                {
                    tilemapEditor.PreviousLevel(tileDict);  // Use existing tileDict

                    // Ensure currentLevelIndex is still valid after the operation
                    if (tilemapEditor.currentLevelIndex >= 0 && tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
                    {
                        SyncInspectorAfterLevelChange();
                    }
                }
                else
                {
                    Debug.LogWarning("No levels available to navigate.");
                }
            }

            // Level selection dropdown - make it safer
            if (tilemapEditor.levels != null && tilemapEditor.levels.Count > 0)
            {
                string[] levelNames = tilemapEditor.levels
                    .Select(level => "Current Level: " + level.levelName)
                    .ToArray();

                // Ensure currentLevelIndex is valid before using it in Popup
                int safeCurrentIndex = Mathf.Clamp(tilemapEditor.currentLevelIndex, 0, levelNames.Length - 1);
                if (safeCurrentIndex != tilemapEditor.currentLevelIndex)
                {
                    Debug.LogWarning($"Corrected invalid currentLevelIndex from {tilemapEditor.currentLevelIndex} to {safeCurrentIndex}");
                    tilemapEditor.currentLevelIndex = safeCurrentIndex;
                }

                int newIndex = EditorGUILayout.Popup(tilemapEditor.currentLevelIndex, levelNames);

                if (newIndex != tilemapEditor.currentLevelIndex && newIndex >= 0 && newIndex < tilemapEditor.levels.Count)
                {
                    tilemapEditor.LoadLevel(newIndex, tileDict);  // Use existing tileDict
                    SyncInspectorAfterLevelChange();
                }
            }

            // Replace the Next button code:
            if (GUILayout.Button(" Next  ▶"))
            {
                if (tilemapEditor.levels != null && tilemapEditor.levels.Count > 0)
                {
                    tilemapEditor.NextLevel(tileDict);  // Use existing tileDict

                    // Ensure currentLevelIndex is still valid after the operation
                    if (tilemapEditor.currentLevelIndex >= 0 && tilemapEditor.currentLevelIndex < tilemapEditor.levels.Count)
                    {
                        SyncInspectorAfterLevelChange();
                    }
                }
                else
                {
                    Debug.LogWarning("No levels available to navigate.");
                }
            }


            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            GUILayout.Space(10);

            if (isSelectionMode)
            {
                bigBoldLabel.fontSize = 18; // set desired font size
                EditorGUILayout.LabelField("✂️ Edit Mode (Beta)", bigBoldLabel);

            }
            else
            {
                bigBoldLabel.fontSize = 18; // set desired font size
                EditorGUILayout.LabelField("✏️ Drawing System", bigBoldLabel);

            }

            // Tab buttons row
            GUIContent[] tabs = new GUIContent[]
            {
                new GUIContent(" Tiles", LoadOverlayIconTexture("tile_small_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png")),
                new GUIContent(" Paint & Plants 🌱", LoadOverlayIconTexture("texture_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png")),
                new GUIContent(" Objects", LoadOverlayIconTexture("box_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png")),
                new GUIContent(" Path", LoadOverlayIconTexture("timeline_24dp_E3E3E3_FILL0_wght400_GRAD0_opsz24.png"))
            };
            int[] visualTabOrder = { 0, 2, 3, 1 };
            GUIContent[] displayTabs = visualTabOrder.Select(idx => tabs[idx]).ToArray();

            // Save original tab selection
            int oldVisualTab = Array.IndexOf(visualTabOrder, selectedTab);

            // Get the new tab selection
            int newVisualTab = GUILayout.Toolbar(oldVisualTab, displayTabs, GUILayout.Height(30));

            // If the tab has changed
            if (newVisualTab != oldVisualTab && newVisualTab >= 0 && newVisualTab < visualTabOrder.Length)
            {
                SetInspectorTab(visualTabOrder[newVisualTab]);
            }

            EditorGUILayout.Space();

            /******************************/

            GUILayout.Space(10);





            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            DrawTilemapGrid();

         

            EditorGUILayout.BeginHorizontal();
            string[] presetOptions = new string[] { "⊞ Grid Size 7x7", "⊞ Grid Size 16x16", "⊞ Grid Size 32x32", "⊞ Grid Size 64x64", "⊞ Custom Grid View" };
            int selectedPreset = EditorGUILayout.Popup(
                GetCurrentPresetIndex(tilemapEditor.gridSize),
                presetOptions
            );
            Vector3Int newGridSize = tilemapEditor.gridSize;
            switch (selectedPreset)
            {
                case 0: newGridSize = new Vector3Int(7, 7, 1); tilemapEditor.useCustomSize = false; break;
                case 1: newGridSize = new Vector3Int(16, 16, 1); tilemapEditor.useCustomSize = false; break;
                case 2: newGridSize = new Vector3Int(32, 32, 1); tilemapEditor.useCustomSize = false; break;
                case 3: newGridSize = new Vector3Int(64, 64, 1); tilemapEditor.useCustomSize = false; break;
                case 4: tilemapEditor.useCustomSize = true; break;
            }

            // Scale dropdown inside the existing horizontal layout
            EditorGUILayout.LabelField("Scale", GUILayout.Width(40));

            int newScale = EditorGUILayout.IntPopup(
                tilemapEditor.gridScale,
                Enumerable.Range(1, 10).Select(i => $"x{i}").ToArray(),
                Enumerable.Range(1, 10).ToArray(),
                GUILayout.Width(60)
            );

            if (newScale != tilemapEditor.gridScale)
            {
                Undo.RecordObject(tilemapEditor, "Change Grid Scale");

                int oldScale = tilemapEditor.gridScale;
                tilemapEditor.gridScale = newScale;

                tilemapEditor.ApplyGridScale();

#if UNITY_EDITOR
                Debug.Log($"🎨 Grid Scale changed: x{oldScale} → x{newScale}");
#endif

                EditorUtility.SetDirty(tilemapEditor);
            }


            if (GUILayout.Button("✴︎ Center View", GUILayout.Width(100), GUILayout.Height(18)))
            {
                gridViewOffset = Vector2.zero;
                Repaint();
            }

            EditorGUILayout.EndHorizontal();

            if (tilemapEditor.useCustomSize)
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
                    newGridSize = new Vector3Int(customWidth, customHeight, 1);
                    gridSizeProperty.vector3IntValue = newGridSize;

                    // ✅ NOUVEAU: Force update pour petites grilles custom
                    if (newGridSize.x < 64 || newGridSize.y < 64)
                    {
                        tilemapEditor.ForceUpdateSmallGrid();
                    }

                    EditorUtility.SetDirty(tilemapEditor);
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();
            }
            else if (newGridSize != tilemapEditor.gridSize)
            {
                Undo.RecordObject(tilemapEditor, "Change Grid Size Preset");
                gridSizeProperty.vector3IntValue = newGridSize;

                // ✅ NOUVEAU: Force update pour petites grilles preset
                if (newGridSize.x < 64 || newGridSize.y < 64)
                {
                    tilemapEditor.ForceUpdateSmallGrid();
                }

                EditorUtility.SetDirty(tilemapEditor);
                Repaint();
            }

            GUILayout.Space(10);

            EditorGUILayout.EndVertical();



            if (isSelectionMode)
            {
                EditorGUILayout.LabelField("🖱️ Left BT to Select, re-click inside selection to move it, Right BT also moves selected", EditorStyles.boldLabel);
            }
            else
            {
                string helpText;

                if (selectedTab == 0)
                {
                    helpText = (Event.current != null && Event.current.shift)
                        ? "🖱️ ⇧ Shift + Click to pick the tile layer under the cursor"
                        : "🖱️ Tiles mode: draw/erase only tiles, ⇧ Shift + Click to pick a tile layer";
                }
                else if (selectedTab == 1)
                {
                    helpText = "🖱️ Texture mode: draw/erase only painted textures";
                }
                else if (selectedTab == 2)
                {
                    helpText = "🖱️ Object mode: place/erase only GameObjects";
                }
                else
                {
                    helpText = "🖱️ Path mode: edit only the selected path";
                }

                EditorGUILayout.LabelField(helpText, EditorStyles.boldLabel);

                // === OLD IMGUI Content Types section — only show when NOT using UI Toolkit ===
                if (!useUIToolkit)
                {
                    GUILayout.Space(10);
                    bigBoldLabel.fontSize = 18;
                    EditorGUILayout.LabelField("🎨 Content Types", bigBoldLabel);

                    GUILayout.Space(10);

                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    /*******************************/

                    // 🔑 ADD THE BUTTONS HERE - RIGHT AFTER THE TABS
                    switch (selectedTab)
                    {
                        case 0: // Tiles tab
                            if (GUILayout.Button("+ Add Tile Rule", GUILayout.Height(30)))
                            {
                                AddTileRule();
                            }
                            break;

                        case 1: // Texture tab
                            const string requiredShader = "BEKKOLOCO/PaintShader_FinalPerfect";

                            // 1) Scan initial (ou après clic refresh)
                            if (_paintMats == null)
                                RefreshPaintMaterials(requiredShader);

                            // 2) Nombre de matériaux trouvés
                            int materialCount = _paintMats.Length;
                            GUILayout.Label($"Materials disponibles : {materialCount}", EditorStyles.boldLabel);

                            // 3) Prépare la liste de règles si nécessaire
                            if (tilemapEditor.texturePaintRules == null)
                                tilemapEditor.texturePaintRules = new List<QuickTilemapEditor.TexturePaintRule>();

                            // 4) Bouton Refresh
                            if (GUILayout.Button("↻ Refresh list", GUILayout.Height(30)))
                            {
                                RefreshPaintMaterials(requiredShader);
                                Repaint();
                            }
                            break;

                        case 2: // GameObjects tab
                            if (GUILayout.Button("+ Add GameObject", GUILayout.Height(30)))
                            {
                                // 1) Deselect other modes
                                tilemapEditor.selectedTileRuleIndex = -1;
                                tilemapEditor.selectedTextureRule = null;
                                tilemapEditor.selectedPathIndex = -1;

                                // 2) Create & add the new GameObject rule
                                var newGoRule = new QuickTilemapEditor.GameObjectRule
                                {
                                    id = System.Guid.NewGuid().ToString()
                                };
                                tilemapEditor.gameObjectRules.Add(newGoRule);

                                // 3) Select it
                                tilemapEditor.selectedGameObjectRuleIndex = tilemapEditor.gameObjectRules.Count - 1;
                            }
                            break;

                        case 3: // Path tab
                            if (GUILayout.Button("+ Add Path", GUILayout.Height(30)))
                            {
                                if (tilemapEditor.paths == null)
                                {
                                    tilemapEditor.paths = new List<QuickTilemapEditor.Path>();
                                }

                                var newPath = new QuickTilemapEditor.Path();
                                newPath.points = new List<Vector3Int>();
                                newPath.color = Color.yellow;
                                tilemapEditor.paths.Add(newPath);
                                tilemapEditor.selectedPathIndex = tilemapEditor.paths.Count - 1;

                                // Deselect other tools for clarity
                                tilemapEditor.selectedTileRuleIndex = -1;
                                tilemapEditor.selectedGameObjectRuleIndex = -1;
                                tilemapEditor.selectedTextureRule = null;

                                // Ensure we're in draw mode
                                drawMode = true;

                                tilemapEditor.CleanupLegacyPathVisuals();

                                EditorUtility.SetDirty(tilemapEditor);
                            }
                            break;
                    }
                    // Draw the actual IMGUI content sections
                    switch (selectedTab)
                    {
                        case 0: DrawTileRulesSection(); break;
                        case 1: DrawTexturePaintTab(); break;
                        case 2: DrawGameObjectRulesSection(); break;
                        case 3: DrawPathSection(); break;
                    }

                    // ── Slope UI (always visible below tabs) ──
                    EditorGUILayout.EndVertical();
                } // end !useUIToolkit
            }


            serializedObject.ApplyModifiedProperties();
        }

        #endregion

    }
}
