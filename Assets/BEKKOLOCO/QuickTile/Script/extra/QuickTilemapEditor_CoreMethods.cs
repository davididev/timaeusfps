using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Tilemaps;
using Unity.AI.Navigation;
using System.Linq;
using Bekkoloco.DOTS;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        private void OffsetVegetationInstancesAndRebuild(Vector3 worldDelta)
        {
            if (worldDelta.sqrMagnitude <= 0.000001f || texturePaintRules == null)
                return;

            bool movedAnyInstance = false;

            foreach (var rule in texturePaintRules)
            {
                if (rule?.vegetationEntries == null)
                    continue;

                foreach (var entry in rule.vegetationEntries)
                {
                    if (entry?.instances == null || entry.instances.Count == 0)
                        continue;

                    for (int i = 0; i < entry.instances.Count; i++)
                    {
                        var instance = entry.instances[i];
                        instance.position += worldDelta;
                        entry.instances[i] = instance;
                    }

                    movedAnyInstance = true;
                }
            }

            if (!movedAnyInstance)
                return;

            var vegetationRenderer = GetComponent<VegetationGPURenderer>();
            if (vegetationRenderer != null)
                vegetationRenderer.RebuildFromRules(texturePaintRules);

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
            SceneView.RepaintAll();
#endif
        }

        private void Awake()
        {
            if (tileRules == null || tileRules.Count == 0)
            {
                TileRule defaultRule = new TileRule();
                defaultRule.tile = activeTile;
                defaultRule.useCustomTilemap = false;
                defaultRule.yOffset = 0f;
                defaultRule.renderOrder = 0;
                tileRules = new System.Collections.Generic.List<TileRule> { defaultRule };
                selectedTileRuleIndex = 0;
            }
        }

        private void Start()
        {
            if (levels != null && levels.Count > 0)
            {
                currentLevelIndex = Mathf.Clamp(currentLevelIndex, 0, levels.Count - 1);

#if UNITY_EDITOR
                RestorePrefabReferences();
                ResynchronizeGameObjectsFromScene();
#endif
                var tileDict = BuildTileDictionary();

#if UNITY_EDITOR
                LoadLevel(currentLevelIndex, tileDict);
#else
                string levelName = levels[currentLevelIndex].levelName;
                LoadLevelByNameRuntime(levelName);
                ApplyGridScale();
#endif
                RebuildPaintMaskAndMaterials();
            }
            else
            {
                Debug.LogError("No levels found. Ensure levels are set up before starting gameplay.");
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Scaling
        // ──────────────────────────────────────────────────────────────
        private void FixChildScales()
        {
            foreach (var tm in GetAllCustomTilemaps().Concat(new[] { targetTilemap }))
                if (tm != null) tm.transform.localScale = Vector3.one;
        }

        public void ApplyGridScale()
        {
            float scale = Mathf.Max(1, gridScale);
            Grid grid = FindGrid();
            Transform scaleRoot = grid != null
                ? grid.transform
                : (targetTilemap != null && targetTilemap.transform.parent != null
                    ? targetTilemap.transform.parent
                    : transform);

            scaleRoot.localScale = new Vector3(scale, scale, scale);
            FixChildScales();

            float inverseScale = 1f / scale;
            foreach (GameObject go in instantiatedGameObjects)
            {
                if (go != null)
                {
                    go.transform.localScale = new Vector3(inverseScale, inverseScale, inverseScale);
                }
            }

            SyncAllProceduralRenderers();
            RefreshAllSkirts();
            RebuildPaintMaskAndMaterials();
            UpdatePaintMaskTexture();
            UpdateBlendPreviewMaterial();
            PushPaintMaskGlobals();
            RefreshAllPathFollowers();
            RebuildAllTrackMeshes();

#if UNITY_EDITOR
            try { DeformerSystemExtensions.ForceUpdateAllDeformers(); }
            catch (Exception ex) { Debug.LogWarning($"[QuickTile] ForceUpdateAllDeformers failed after scale change: {ex.Message}"); }

            EditorUtility.SetDirty(this);
            if (grid != null)
                EditorUtility.SetDirty(grid);
            if (targetTilemap != null)
                EditorUtility.SetDirty(targetTilemap);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            SceneView.RepaintAll();
#endif
        }

        // ──────────────────────────────────────────────────────────────
        // Navmesh + helpers
        // ──────────────────────────────────────────────────────────────
        public float GetLowestTileY()
        {
            float lowestY = float.MaxValue;

            if (targetTilemap != null)
            {
                BoundsInt bounds = targetTilemap.cellBounds;
                foreach (Vector3Int pos in bounds.allPositionsWithin)
                {
                    TileBase tile = targetTilemap.GetTile(pos);
                    if (tile != null)
                    {
                        lowestY = Mathf.Min(lowestY, targetTilemap.transform.position.y);
                    }
                }
            }

            foreach (Tilemap tm in GetAllCustomTilemaps())
            {
                BoundsInt bounds = tm.cellBounds;
                foreach (Vector3Int pos in bounds.allPositionsWithin)
                {
                    TileBase tile = tm.GetTile(pos);
                    if (tile != null)
                    {
                        Vector3 worldPos = tm.CellToWorld(pos);
                        lowestY = Mathf.Min(lowestY, worldPos.y);
                    }
                }
            }

            return (lowestY == float.MaxValue) ? 0f : lowestY;
        }

        private void CenterLevelToSurfaceMassXZ(Transform levelContainer)
        {
            if (levelContainer == null)
                return;

            Vector3 previousPosition = levelContainer.position;
            Vector3 sum = Vector3.zero;
            int count = 0;
            var visitedTilemaps = new System.Collections.Generic.HashSet<Tilemap>();

            var allTilemaps = GetAllCustomTilemaps().Concat(new[] { targetTilemap });
            foreach (Tilemap tm in allTilemaps)
            {
                if (tm == null || !visitedTilemaps.Add(tm)) continue;
                BoundsInt bounds = tm.cellBounds;
                foreach (Vector3Int pos in bounds.allPositionsWithin)
                {
                    if (tm.GetTile(pos) != null)
                    {
                        Vector3 worldPos = tm.GetCellCenterWorld(pos);
                        sum += new Vector3(worldPos.x, worldPos.y, 0f);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                Vector3 centerMassWorld = sum / count;
                Vector3 newPos = new Vector3(
                    levelContainer.position.x - centerMassWorld.x,
                    levelContainer.position.y - centerMassWorld.y,
                    levelContainer.position.z);
                levelContainer.position = newPos;
                OffsetVegetationInstancesAndRebuild(newPos - previousPosition);
            }
        }

        /// <summary>
        /// Public wrapper for CenterLevelToSurfaceMassXZ, callable from the Inspector.
        /// </summary>
        public void CenterLevelToSurfaceMassXZ_Public()
        {
            if (targetTilemap != null && targetTilemap.transform.parent != null)
                CenterLevelToSurfaceMassXZ(targetTilemap.transform.parent);
        }

        private void EnsureNavMeshSurface()
        {
            if (_navSurface == null)
            {
                if (targetTilemap == null || targetTilemap.transform.parent == null)
                    return;
                var root = targetTilemap.transform.parent.gameObject;
                _navSurface = root.GetComponent<NavMeshSurface>()
                              ?? root.AddComponent<NavMeshSurface>();
            }
        }

        private void RequestNavMeshRebuild(float delay = 0.1f)
        {
            EnsureNavMeshSurface();
            if (_navSurface == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                QueueEditorNavMeshRebuild();
                return;
            }
#endif

            if (_navMeshRebuildCoroutine != null)
                StopCoroutine(_navMeshRebuildCoroutine);

            _navMeshRebuildCoroutine = StartCoroutine(RebuildNavMeshAfterDelay(delay));
        }

        private IEnumerator RebuildNavMeshAfterDelay(float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            DeformerSystemExtensions.ForceUpdateAllDeformers();
            yield return null;

            if (_navSurface != null)
                _navSurface.BuildNavMesh();

            _navMeshRebuildCoroutine = null;
        }

#if UNITY_EDITOR
        private void QueueEditorNavMeshRebuild()
        {
            if (_navMeshRebuildQueuedInEditor)
                return;

            _navMeshRebuildQueuedInEditor = true;
            EditorApplication.delayCall -= RebuildQueuedNavMeshInEditor;
            EditorApplication.delayCall += RebuildQueuedNavMeshInEditor;
        }

        private void RebuildQueuedNavMeshInEditor()
        {
            EditorApplication.delayCall -= RebuildQueuedNavMeshInEditor;
            _navMeshRebuildQueuedInEditor = false;

            if (this == null)
                return;

            EnsureNavMeshSurface();
            if (_navSurface == null)
                return;

            DeformerSystemExtensions.ForceUpdateAllDeformers();
            _navSurface.BuildNavMesh();
            EditorUtility.SetDirty(_navSurface);

            if (gameObject != null && gameObject.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif

#if UNITY_EDITOR
        // ──────────────────────────────────────────────────────────────
        // Move preview helpers (Editor only)
        // ──────────────────────────────────────────────────────────────

        private class MovePreviewSession
        {
            public TileRule rule;
            public Transform targetTransform;
            public Vector3 originalLocalPosition;
            public double startTime;
            public bool animate;
            public bool returning;
        }

        [NonSerialized] private MovePreviewSession activeMovePreview;

        private const float MovePreviewHoldDuration = 0.75f;
        private const float MovePreviewAnimDuration = 0.75f;

        private void OnDisable()
        {
            StopMovePreviewInternal();
        }

        private void OnDestroy()
        {
            // Clean up runtime Material instances to prevent memory leaks
            if (blendPreviewMaterial != null)
            {
                SafeDestroy(blendPreviewMaterial);
                blendPreviewMaterial = null;
            }
            if (paintMaskMaterial != null)
            {
                SafeDestroy(paintMaskMaterial);
                paintMaskMaterial = null;
            }
            if (paintMaskTexture != null)
            {
                paintMaskTexture.Release();
                SafeDestroy(paintMaskTexture);
                paintMaskTexture = null;
            }
        }

        public void PreviewMoveOffsetPosition(TileRule rule)
        {
            StartMovePreview(rule, animate: false);
        }

        public void PreviewMoveAnimation(TileRule rule)
        {
            StartMovePreview(rule, animate: true);
        }

        private void StartMovePreview(TileRule rule, bool animate)
        {
            if (rule == null)
                return;

            if (Application.isPlaying)
                return;

            Transform target = ResolveMovePreviewTarget(rule);
            if (target == null)
            {
                Debug.LogWarning("[QuickTilemapEditor] Unable to preview move offset because no target tilemap was found for the selected rule.");
                return;
            }

            StopMovePreviewInternal();

            activeMovePreview = new MovePreviewSession
            {
                rule = rule,
                targetTransform = target,
                originalLocalPosition = target.localPosition,
                startTime = EditorApplication.timeSinceStartup,
                animate = animate,
                returning = false
            };

            if (!animate)
            {
                ApplyMovePreviewOffset(rule.moveOffset);
            }

            EditorApplication.update += HandleMovePreviewUpdate;
            SceneView.RepaintAll();
        }

        private Transform ResolveMovePreviewTarget(TileRule rule)
        {
            if (rule == null)
                return null;

            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                return rule.customTargetTilemap.transform;

            if (!rule.useCustomTilemap && targetTilemap != null)
                return targetTilemap.transform;

            return null;
        }

        private void HandleMovePreviewUpdate()
        {
            if (activeMovePreview == null)
            {
                EditorApplication.update -= HandleMovePreviewUpdate;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!activeMovePreview.animate)
            {
                if (now - activeMovePreview.startTime >= MovePreviewHoldDuration)
                {
                    ApplyMovePreviewOffset(Vector3.zero);
                    StopMovePreviewInternal();
                    SceneView.RepaintAll();
                }
                return;
            }

            double elapsed = now - activeMovePreview.startTime;
            float t = Mathf.Clamp01((float)(elapsed / MovePreviewAnimDuration));

            if (!activeMovePreview.returning)
            {
                Vector3 offset = Vector3.Lerp(Vector3.zero, activeMovePreview.rule.moveOffset, t);
                ApplyMovePreviewOffset(offset);

                if (t >= 1f)
                {
                    activeMovePreview.returning = true;
                    activeMovePreview.startTime = now;
                }
            }
            else
            {
                Vector3 offset = Vector3.Lerp(activeMovePreview.rule.moveOffset, Vector3.zero, t);
                ApplyMovePreviewOffset(offset);

                if (t >= 1f)
                {
                    StopMovePreviewInternal();
                    SceneView.RepaintAll();
                }
            }
        }

        private void ApplyMovePreviewOffset(Vector3 offset)
        {
            if (activeMovePreview == null)
                return;

            if (activeMovePreview.targetTransform == null)
            {
                StopMovePreviewInternal();
                return;
            }

            activeMovePreview.targetTransform.localPosition = activeMovePreview.originalLocalPosition + offset;
        }

        private void StopMovePreviewInternal()
        {
            if (activeMovePreview == null)
                return;

            if (activeMovePreview.targetTransform != null)
            {
                activeMovePreview.targetTransform.localPosition = activeMovePreview.originalLocalPosition;
            }

            EditorApplication.update -= HandleMovePreviewUpdate;
            activeMovePreview = null;
        }
#endif

        // ──────────────────────────────────────────────────────────────
        // Editor hooks
        // ──────────────────────────────────────────────────────────────
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        static void InitEditorUpdate()
        {
            // Prevent duplicate subscriptions on domain reload
            EditorApplication.update -= EditorUpdate;
            EditorApplication.update += EditorUpdate;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;

            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                var editor = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
                if (editor != null)
                {
                    editor.SaveCurrentPaintDataToLevel();
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                // NOTE: Le reload se fait ici (pas à ExitingPlayMode) car
                // Application.isPlaying est encore true pendant ExitingPlayMode,
                // ce qui empêche RestoreDeformerHandlesAfterLoad() de créer
                // les handles 3D visibles (flèches UI) en éditeur.
                // Le PlaymodeListener fait déjà ReloadCurrentLevel() à EnteredEditMode,
                // donc on ne fait ici que le rebuild des matériaux/paint.
                EditorApplication.delayCall += () =>
                {
                    var editor = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
                    if (editor != null)
                    {
                        editor.RebuildPaintMaskAndMaterials();
                        SceneView.RepaintAll();
                    }
                };
            }
        }

        private static void OnSceneSaved(Scene scene)
        {
            var editor = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
            if (editor != null)
            {
                editor.SaveCurrentPaintDataToLevel();
            }
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            EditorApplication.delayCall += () =>
            {
                var editor = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
                if (editor != null)
                {
                    editor.RebuildPaintMaskAndMaterials();
                    SceneView.RepaintAll();
                }
            };
        }

        private void OnValidate()
        {
            if (Application.isPlaying) return;
            if (!gameObject.scene.IsValid()) return;

            EditorApplication.delayCall += () =>
            {
                if (this == null || !gameObject.scene.IsValid()) return;

                // Keep Grid.cellSwizzle in sync with gridStyle (3D ↔ 2.5D).
                ApplyGridStyleToGrid();

                RebuildPaintMaskAndMaterials();
                needsRefreshPreview = true;
                SceneView.RepaintAll();
            };
        }


        private static void OnEditorRefresh()
        {
            var editor = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
            if (editor != null)
            {
                if (!editor.autoReloadOnLevelChange)
                    return;

                editor.ReloadCurrentLevel();
                editor.RebuildPaintMaskAndMaterials();
            }
        }

        private static QuickTilemapEditor _cachedEditorInstance;
        private static double _lastEditorCacheLookup;

        private static void EditorUpdate()
        {
            // Cache editor lookup — refresh every 2 seconds to avoid per-frame cost
            double now = EditorApplication.timeSinceStartup;
            if (_cachedEditorInstance == null || now - _lastEditorCacheLookup > 2.0)
            {
                _cachedEditorInstance = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
                _lastEditorCacheLookup = now;
            }
            var editor = _cachedEditorInstance;
            if (editor == null) return;

            if (editor.needsRefreshPreview)
            {
                editor.UpdatePaintMaskTexture();
                editor.UpdateBlendPreviewMaterial();
                editor.needsRefreshPreview = false;
            }

            if (editor.skirtsNeedRefresh)
            {
                editor.RebuildPaintMaskAndMaterials();
                editor.skirtsNeedRefresh = false;
                SceneView.RepaintAll();
            }
        }

        public void RequestSkirtRefresh() { skirtsNeedRefresh = true; }
#endif
    }
}
