using System.Collections.Generic;
using System.Linq;
using System.Collections;
using System;
using UnityEngine;
using UnityEngine.Tilemaps;
using Bekkoloco.DOTS;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        [System.NonSerialized] private int proceduralSyncBatchDepth = 0;
        [System.NonSerialized] private readonly HashSet<TileRule> pendingProceduralSyncRules = new HashSet<TileRule>();

        private static Material _defaultProceduralFloorMaterial;
        private static Material _defaultProceduralWallMaterial;
        private static Material _defaultProceduralSkirtMaterial;
        private static Material _defaultProceduralDigMaterial;

        private static readonly string[] DefaultProceduralFloorMaterialPaths =
        {
            "Assets/BEKKOLOCO/QuickTile/Material/Grass.mat"
        };

        private static readonly string[] DefaultProceduralWallMaterialPaths =
        {
            "Assets/BEKKOLOCO/QuickTile/Material/Stone wall.mat"
        };

        private static readonly string[] DefaultProceduralSkirtMaterialPaths =
        {
            "Assets/BEKKOLOCO/QuickTile/Material/Grass_skirt.mat"
        };

        private static readonly string[] DefaultProceduralDigMaterialPaths =
        {
            "Assets/BEKKOLOCO/QuickTile/Material/Digg.mat"
        };

        // ─────────────────────────────────────
        // Tilemap creation / selection
        // ─────────────────────────────────────

        public void EnsureTargetTilemap()
        {
            if (targetTilemap != null) return;

            GameObject parent = transform.parent != null ? transform.parent.gameObject : gameObject;
            GameObject tmGO = new GameObject("TargetTilemap");
            tmGO.hideFlags = HideFlags.HideInHierarchy;

            tmGO.transform.SetParent(parent.transform);
            tmGO.transform.localPosition = new Vector3(0, -1000f, 0);
            tmGO.transform.localScale = Vector3.one;

            targetTilemap = tmGO.AddComponent<Tilemap>();
            tmGO.AddComponent<TilemapRenderer>();
        }

        public Tilemap GetTilemapForHeight(float height)
        {
            height = Mathf.Round(height * 100f) / 100f; // snap to 0.01
            if (heightTilemaps.TryGetValue(height, out Tilemap existingTm))
                return existingTm;

            GameObject tilemapObj = new GameObject($"Tilemap_Height_{height}");

            if (targetTilemap != null && targetTilemap.transform.parent != null)
                tilemapObj.transform.SetParent(targetTilemap.transform.parent);

            tilemapObj.transform.localPosition = new Vector3(0, height, 0);
            tilemapObj.transform.localScale = Vector3.one;

            Tilemap tilemap = tilemapObj.AddComponent<Tilemap>();
            TilemapRenderer renderer = tilemapObj.AddComponent<TilemapRenderer>();

            if (targetTilemap != null)
            {
                var trgRend = targetTilemap.GetComponent<TilemapRenderer>();
                if (trgRend != null)
                {
                    renderer.sortingLayerID = trgRend.sortingLayerID;
                    renderer.sortingOrder = trgRend.sortingOrder;
                }
            }

            heightTilemaps[height] = tilemap;
            return tilemap;
        }

        public Tilemap CreateTilemapForRule(TileRule rule)
        {
            EnsureTargetTilemap();

            string tileName = rule.tile != null ? rule.tile.name : "NewRule";
            string name = $"Tilemap_Rule_{tileName}_{rule.yOffset}";

            GameObject newTilemapObj = new GameObject(name);
            GameObject grid = GameObject.Find("Grid");

            newTilemapObj.transform.SetParent(grid ? grid.transform : this.transform);
            newTilemapObj.transform.localPosition = new Vector3(0, rule.yOffset, 0);
            newTilemapObj.transform.localRotation = Quaternion.identity;
            newTilemapObj.transform.localScale = Vector3.one;

            Tilemap tilemap = newTilemapObj.AddComponent<Tilemap>();
            TilemapRenderer renderer = newTilemapObj.AddComponent<TilemapRenderer>();

            renderer.sortingLayerID = targetTilemap.GetComponent<TilemapRenderer>().sortingLayerID;
            renderer.sortingOrder = rule.renderOrder;

            return tilemap;
        }

        // ─────────────────────────────────────
        // Painting / erasing
        // ─────────────────────────────────────

        private Tilemap GetTilemapForRule(TileRule rule)
        {
            if (rule == null) return targetTilemap;

            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                return rule.customTargetTilemap;

            if (Mathf.Abs(rule.yOffset) > 0.001f)
            {
                if (!heightTilemaps.TryGetValue(rule.yOffset, out Tilemap heightTilemap) || heightTilemap == null)
                {
                    heightTilemap = CreateTilemapForRule(rule);
                    heightTilemaps[rule.yOffset] = heightTilemap;
                }

                return heightTilemap;
            }

            return targetTilemap;
        }

        private bool SyncRuleYOffsetFromTilemapTransform(TileRule rule, Tilemap tilemap = null)
        {
            if (rule == null)
                return false;

            tilemap ??= GetTilemapForRule(rule);
            if (tilemap == null)
                return false;

            float localY = tilemap.transform.localPosition.y;
            if (Mathf.Approximately(rule.yOffset, localY))
                return false;

            rule.yOffset = localY;

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif

            return true;
        }

        private void SetProceduralSourceRenderersVisible(Tilemap tilemap, bool visible, bool renderAsDigPreview = false)
        {
            if (tilemap == null) return;

            var tilemapGO = tilemap.gameObject;

            foreach (var renderer in tilemapGO.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer == null) continue;
                if (renderer.GetComponentInParent<ProceduralTileRenderer>() != null) continue;
                if (renderer.transform.parent != null && renderer.transform.parent.name == "ProceduralTiles") continue;
                if (IsRendererPartOfDeformerHandle(renderer.transform, tilemap.transform)) continue;
                // Don't hide placed GameObjects (golems, props, etc.)
                if (renderer.GetComponentInParent<QuickTileMarker>() != null) continue;
                bool isSkirtRenderer = renderer.GetComponentInParent<SkirtManager>() != null;
                renderer.enabled = visible && (!renderAsDigPreview || !isSkirtRenderer);
            }

            foreach (var renderer in tilemapGO.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null) continue;
                if (IsRendererPartOfDeformerHandle(renderer.transform, tilemap.transform)) continue;
                // Don't hide placed GameObjects (golems, props, etc.)
                if (renderer.GetComponentInParent<QuickTileMarker>() != null) continue;
                bool isSkirtRenderer = renderer.GetComponentInParent<SkirtManager>() != null;
                renderer.enabled = visible && (!renderAsDigPreview || !isSkirtRenderer);
            }

            foreach (var renderer in tilemapGO.GetComponentsInChildren<TilemapRenderer>(true))
            {
                if (renderer == null) continue;
                renderer.enabled = visible;
            }
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

        public bool RuleActsAsDigSource(TileRule rule)
        {
            return rule != null &&
                   rule.isDigLayer &&
                   rule.useCustomTilemap &&
                   rule.customTargetTilemap != null;
        }

        public bool RuleReceivesDig(TileRule rule)
        {
            return rule != null &&
                   !rule.isUndiggable;
        }

        public bool TryGetRuleWorldYRange(TileRule rule, out float minY, out float maxY)
        {
            minY = 0f;
            maxY = 0f;

            if (rule == null)
                return false;

            Tilemap ruleTilemap = GetTilemapForRule(rule);
            SyncRuleYOffsetFromTilemapTransform(rule, ruleTilemap);

            float topY = ruleTilemap != null ? ruleTilemap.transform.position.y : rule.yOffset;
            float depth = rule.fixBase
                ? Mathf.Max(0f, Mathf.Abs(rule.yOffset))
                : Mathf.Max(0f, rule.sizeY);
            float bottomY = topY - depth;

            minY = Mathf.Min(topY, bottomY);
            maxY = Mathf.Max(topY, bottomY);
            return true;
        }

        public bool RulesOverlapWorldY(TileRule first, TileRule second)
        {
            if (!TryGetRuleWorldYRange(first, out float firstMinY, out float firstMaxY) ||
                !TryGetRuleWorldYRange(second, out float secondMinY, out float secondMaxY))
                return false;

            return firstMaxY >= secondMinY && secondMaxY >= firstMinY;
        }

        private List<Tilemap> CollectDigSourceTilemapsForRule(TileRule targetRule)
        {
            var digTilemaps = new List<Tilemap>();
            if (tileRules == null || targetRule == null)
                return digTilemaps;

            if (!RuleReceivesDig(targetRule))
                return digTilemaps;

            Tilemap targetRuleTilemap = GetTilemapForRule(targetRule);

            foreach (var rule in tileRules)
            {
                if (!RuleActsAsDigSource(rule)) continue;
                if (ReferenceEquals(rule, targetRule)) continue;
                if (!RulesOverlapWorldY(targetRule, rule)) continue;

                var digTilemap = rule.customTargetTilemap;
                if (digTilemap == null || ReferenceEquals(digTilemap, targetRuleTilemap)) continue;
                if (digTilemaps.Contains(digTilemap)) continue;

                digTilemaps.Add(digTilemap);
            }

            return digTilemaps;
        }

        private List<QuickTileDigVolume> CollectDigVolumesForRule(TileRule targetRule)
        {
            var sceneDigVolumes = new List<QuickTileDigVolume>();
            if (targetRule == null || !RuleReceivesDig(targetRule))
                return sceneDigVolumes;

            Tilemap targetTilemap = GetTilemapForRule(targetRule);
            var targetScene = targetTilemap != null ? targetTilemap.gameObject.scene : default;

            var allDigVolumes = FindObjectsByType<QuickTileDigVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (allDigVolumes == null || allDigVolumes.Length == 0)
                return sceneDigVolumes;

            foreach (var digVolume in allDigVolumes)
            {
                if (digVolume == null || !digVolume.isActiveAndEnabled || !digVolume.gameObject.activeInHierarchy)
                    continue;

                if (targetScene.IsValid() && digVolume.gameObject.scene != targetScene)
                    continue;

                sceneDigVolumes.Add(digVolume);
            }

            return sceneDigVolumes;
        }

        private IEnumerable<TileRule> EnumerateProceduralSyncTargets(TileRule changedRule)
        {
            if (tileRules == null)
                yield break;

            if (changedRule == null)
            {
                foreach (var rule in tileRules)
                {
                    if (rule == null || rule.meshMode != MeshMode.Procedural) continue;
                    yield return rule;
                }
                yield break;
            }

            bool changedRuleActsAsDig = RuleActsAsDigSource(changedRule);

            if (changedRule.meshMode == MeshMode.Procedural)
                yield return changedRule;

            if (!changedRuleActsAsDig)
                yield break;

            foreach (var rule in tileRules)
            {
                if (rule == null || ReferenceEquals(rule, changedRule)) continue;
                if (rule.meshMode != MeshMode.Procedural) continue;
                if (RuleActsAsDigSource(rule)) continue;
                if (!RuleReceivesDig(rule)) continue;
                if (!RulesOverlapWorldY(rule, changedRule)) continue;

                yield return rule;
            }
        }

        public void ApplyDefaultProceduralMaterials(TileRule rule, bool onlyIfMissing = true)
        {
            if (rule == null) return;

            Material floorMaterial = LoadDefaultProceduralMaterial(
                ref _defaultProceduralFloorMaterial,
                DefaultProceduralFloorMaterialPaths,
                "Grass.mat");
            Material wallMaterial = LoadDefaultProceduralMaterial(
                ref _defaultProceduralWallMaterial,
                DefaultProceduralWallMaterialPaths,
                "Stone wall.mat");
            Material skirtMaterial = LoadDefaultProceduralMaterial(
                ref _defaultProceduralSkirtMaterial,
                DefaultProceduralSkirtMaterialPaths,
                "Grass_skirt.mat");
            Material digMaterial = LoadDefaultProceduralMaterial(
                ref _defaultProceduralDigMaterial,
                DefaultProceduralDigMaterialPaths,
                "Digg.mat");

            if ((!onlyIfMissing || rule.proceduralFloorMaterial == null) && floorMaterial != null)
                rule.proceduralFloorMaterial = floorMaterial;

            if ((!onlyIfMissing || rule.proceduralWallMaterial == null) && wallMaterial != null)
                rule.proceduralWallMaterial = wallMaterial;

            if ((!onlyIfMissing || rule.proceduralCeilingMaterial == null) && skirtMaterial != null)
                rule.proceduralCeilingMaterial = skirtMaterial;

            // Keep the bottom coherent with the cliff/wall material when not explicitly set.
            if ((!onlyIfMissing || rule.proceduralBottomMaterial == null) && wallMaterial != null)
                rule.proceduralBottomMaterial = wallMaterial;

            if ((!onlyIfMissing || rule.proceduralDigMaterial == null) && digMaterial != null)
                rule.proceduralDigMaterial = digMaterial;
        }

        private static Material LoadDefaultProceduralMaterial(ref Material cache, string[] preferredPaths, string fallbackFileName)
        {
            if (cache != null) return cache;

            foreach (var path in preferredPaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

#if UNITY_EDITOR
                cache = AssetDatabase.LoadAssetAtPath<Material>(path);
#else
                cache = LoadRuntimeQuickTileMaterial(path);
#endif
                if (cache != null)
                    return cache;
            }

            string fallbackName = System.IO.Path.GetFileNameWithoutExtension(fallbackFileName);
#if UNITY_EDITOR
            foreach (var guid in AssetDatabase.FindAssets($"{fallbackName} t:Material"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(System.IO.Path.GetFileName(path), fallbackFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                cache = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (cache != null)
                    return cache;
            }
#else
            cache = LoadRuntimeQuickTileMaterial(fallbackName);
            if (cache != null)
                return cache;
#endif

            return null;
        }

        private static Material LoadRuntimeQuickTileMaterial(string materialPath)
        {
            if (string.IsNullOrWhiteSpace(materialPath))
                return null;

            string materialName = System.IO.Path.GetFileNameWithoutExtension(materialPath);
            if (string.IsNullOrWhiteSpace(materialName))
                return null;

            return Resources.Load<Material>($"QuickTile/Material/{materialName}")
                ?? Resources.Load<Material>(materialName);
        }

        public void SyncProceduralRendererForRule(TileRule rule)
        {
            if (rule == null || rule.meshMode != MeshMode.Procedural) return;

            ApplyDefaultProceduralMaterials(rule);

            Tilemap tilemap = GetTilemapForRule(rule);
            if (tilemap == null)
            {
                Debug.LogWarning("[QuickTile] No tilemap assigned to this rule. Cannot rebuild procedural meshes.");
                return;
            }

            SyncRuleYOffsetFromTilemapTransform(rule, tilemap);

            if (rule.proceduralSettings == null)
                rule.proceduralSettings = new ProceduralTileMeshGenerator.ProceduralMeshSettings();

            var renderer = tilemap.GetComponentInChildren<ProceduralTileRenderer>(true);
            if (renderer == null)
            {
                var rendererGO = new GameObject("ProceduralRenderer");
                rendererGO.transform.SetParent(tilemap.transform, false);
                renderer = rendererGO.AddComponent<ProceduralTileRenderer>();
            }

            renderer.sourceTilemap = tilemap;
            renderer.settings = rule.proceduralSettings;
            renderer.floorMaterial = rule.proceduralFloorMaterial;
            renderer.wallMaterial = rule.proceduralWallMaterial;
            renderer.ceilingMaterial = rule.proceduralCeilingMaterial;
            renderer.bottomMaterial = rule.proceduralBottomMaterial;
            renderer.digPreviewMaterial = rule.proceduralDigMaterial;
            renderer.yOffset = rule.yOffset;
            renderer.fixBase = rule.fixBase;
            renderer.sizeY = rule.sizeY;
            renderer.actsAsDigLayer = RuleActsAsDigSource(rule);
            renderer.digVolumes = renderer.actsAsDigLayer
                ? new List<QuickTileDigVolume>()
                : CollectDigVolumesForRule(rule);
            bool hasSceneDigVolumes = renderer.digVolumes != null && renderer.digVolumes.Count > 0;
            renderer.digTilemaps = renderer.actsAsDigLayer || hasSceneDigVolumes
                ? new List<Tilemap>()
                : CollectDigSourceTilemapsForRule(rule);
            renderer.Rebuild();

            // Procedural output now carries the dig preview, so source renderers stay hidden.
            SetProceduralSourceRenderersVisible(tilemap, false, renderer.actsAsDigLayer);

            var combinedRenderer = renderer.GetComponentInChildren<MeshRenderer>(true);
            if (combinedRenderer != null)
                combinedRenderer.enabled = rule.isVisible;

#if UNITY_EDITOR
            EditorUtility.SetDirty(tilemap);
            EditorUtility.SetDirty(renderer);
#endif
        }

        public void SyncProceduralRenderersAffectedByRule(TileRule changedRule)
        {
            RequestProceduralSync(changedRule);
        }

        public void SyncAllProceduralRenderers()
        {
            foreach (var rule in EnumerateProceduralSyncTargets(null))
                SyncProceduralRendererForRule(rule);

            // Schedule a debounced vegetation refresh (runs only once user stops moving things)
            ScheduleVegetationRefresh();
        }

#if UNITY_EDITOR
        private static bool _vegRefreshQueued;
        private static double _vegRefreshRequestTime;
        private const double VEG_REFRESH_DEBOUNCE = 0.4; // seconds of inactivity before refreshing

        /// <summary>
        /// Schedule a debounced vegetation refresh. If multiple calls happen in quick
        /// succession (e.g. dragging a DigVolume), only the last one actually executes,
        /// after a short pause. This avoids N×M raycasts on every frame.
        /// </summary>
        private void ScheduleVegetationRefresh()
        {
            _vegRefreshRequestTime = UnityEditor.EditorApplication.timeSinceStartup;

            if (_vegRefreshQueued) return;
            _vegRefreshQueued = true;

            UnityEditor.EditorApplication.update += VegetationRefreshTick;
        }

        private void VegetationRefreshTick()
        {
            double elapsed = UnityEditor.EditorApplication.timeSinceStartup - _vegRefreshRequestTime;
            if (elapsed < VEG_REFRESH_DEBOUNCE)
                return; // Still waiting for user to stop moving

            // Debounce expired → run the refresh now
            UnityEditor.EditorApplication.update -= VegetationRefreshTick;
            _vegRefreshQueued = false;

            ExecuteVegetationRefresh();
        }

        private void ExecuteVegetationRefresh()
        {
            var vegRenderer = GetComponent<VegetationGPURenderer>();
            if (vegRenderer == null) return;

            bool hasVegetation = false;
            if (texturePaintRules != null)
            {
                foreach (var rule in texturePaintRules)
                {
                    if (rule?.vegetationEntries == null) continue;
                    foreach (var entry in rule.vegetationEntries)
                    {
                        if (entry.instances != null && entry.instances.Count > 0)
                        {
                            hasVegetation = true;
                            break;
                        }
                    }
                    if (hasVegetation) break;
                }
            }

            if (!hasVegetation) return;

            vegRenderer.RefreshVegetationPositions(texturePaintRules);
        }
#else
        private void ScheduleVegetationRefresh() { } // No-op at runtime for now
#endif

        public void BeginProceduralSyncBatch()
        {
            proceduralSyncBatchDepth++;
        }

        public void EndProceduralSyncBatch()
        {
            if (proceduralSyncBatchDepth <= 0)
                return;

            proceduralSyncBatchDepth--;
            if (proceduralSyncBatchDepth > 0)
                return;

            foreach (var rule in pendingProceduralSyncRules)
                SyncProceduralRendererForRule(rule);

            pendingProceduralSyncRules.Clear();
        }

        private void RequestProceduralSync(TileRule rule)
        {
            foreach (var targetRule in EnumerateProceduralSyncTargets(rule))
            {
                if (proceduralSyncBatchDepth > 0)
                {
                    pendingProceduralSyncRules.Add(targetRule);
                    continue;
                }

                SyncProceduralRendererForRule(targetRule);
            }
        }

        public void PaintTile(Vector3Int position, TileRule rule)
        {
            if (rule == null || rule.tile == null) return;

            Tilemap targetMap = GetTilemapForRule(rule);

            if (targetMap == null)
            {
                Debug.LogError("Target tilemap is null. Ensure it's initialized.");
                return;
            }

            targetMap.SetTile(position, rule.tile);
            targetMap.SetColor(position, rule.color);
            targetMap.SetTileFlags(position, TileFlags.None);

            if (rule.useCustomTilemap && !heightTilemaps.ContainsKey(rule.yOffset))
                heightTilemaps[rule.yOffset] = targetMap;

#if UNITY_EDITOR
            skirtsNeedRefresh = true;
            EditorUtility.SetDirty(targetMap);
#endif

            RequestProceduralSync(rule);
        }

        public void EraseTileAtSelectedLayer(Vector3Int position)
        {
            Tilemap targetMap = null;
            var rule = GetSelectedTileRule();
            if (rule != null && rule.tile != null)
            {
                targetMap = rule.useCustomTilemap && rule.customTargetTilemap != null
                    ? rule.customTargetTilemap
                    : Mathf.Abs(rule.yOffset) > 0.001f
                        ? (heightTilemaps.ContainsKey(rule.yOffset) ? heightTilemaps[rule.yOffset] : (heightTilemaps[rule.yOffset] = CreateTilemapForRule(rule)))
                        : targetTilemap;
            }
            else
                targetMap = targetTilemap;

            if (targetMap != null)
            {
#if UNITY_EDITOR
                Undo.RecordObject(targetMap, "Erase Tile");
#endif
                targetMap.SetTile(position, null);

#if UNITY_EDITOR
                EditorUtility.SetDirty(targetMap);
#endif
            }

#if UNITY_EDITOR
            skirtsNeedRefresh = true;
#endif

            RequestProceduralSync(rule);
        }

        public void EraseTileAtAllHeights(Vector3Int position)
        {
            if (targetTilemap != null)
            {
#if UNITY_EDITOR
                Undo.RecordObject(targetTilemap, "Erase Tile");
#endif
                targetTilemap.SetTile(position, null);
            }

            foreach (var tilemap in heightTilemaps.Values)
            {
#if UNITY_EDITOR
                Undo.RecordObject(tilemap, "Erase Tile");
#endif
                tilemap.SetTile(position, null);
            }

            foreach (var rule in tileRules)
            {
                if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                {
#if UNITY_EDITOR
                    Undo.RecordObject(rule.customTargetTilemap, "Erase Tile");
#endif
                    rule.customTargetTilemap.SetTile(position, null);
                }
            }

#if UNITY_EDITOR
            skirtsNeedRefresh = true;
#endif

            foreach (var rule in tileRules)
                RequestProceduralSync(rule);
        }

        public void UpdateTileRuleOnScene(TileRule rule)
        {
            if (rule == null || rule.tile == null)
                return;

            Tilemap targetMap = rule.useCustomTilemap && rule.customTargetTilemap != null
                ? rule.customTargetTilemap
                : targetTilemap;

            BoundsInt bounds = targetMap.cellBounds;
            foreach (Vector3Int pos in bounds.allPositionsWithin)
            {
                if (targetMap.GetTile(pos) != null)
                {
                    targetMap.SetTile(pos, rule.tile);
                    targetMap.SetColor(pos, rule.color);
                    targetMap.SetTileFlags(pos, TileFlags.None);
                }
            }

            Vector3 currentPos = targetMap.transform.localPosition;
            targetMap.transform.localPosition = new Vector3(currentPos.x, rule.yOffset, currentPos.z);
            targetMap.transform.localRotation = Quaternion.identity;

            TilemapRenderer renderer = targetMap.GetComponent<TilemapRenderer>();
            if (renderer != null)
                renderer.sortingOrder = rule.renderOrder;

#if UNITY_EDITOR
            EditorUtility.SetDirty(targetMap);
#endif
        }

        // ─────────────────────────────────────
        // Tilemap selection helpers
        // ─────────────────────────────────────

        public Tilemap GetParentTilemapForPosition(Vector3 worldPos)
        {
            if (targetTilemap == null) return null;
            Vector3Int cellPos = SafeWorldToCell(worldPos, targetTilemap);
            Tilemap parentTilemap = targetTilemap;
            float closestHeightDiff = float.MaxValue;
            float cellHighestY = GetHighestTileYAt(cellPos);

            foreach (Tilemap tm in GetAllCustomTilemaps())
            {
                float heightDiff = Mathf.Abs(tm.transform.position.y - cellHighestY);
                if (heightDiff < closestHeightDiff)
                {
                    closestHeightDiff = heightDiff;
                    parentTilemap = tm;
                }
            }
            return parentTilemap;
        }

        private float GetHighestTileYAt(Vector3Int pos)
        {
            float highestY = float.MinValue;
            if (targetTilemap.GetTile(pos) != null)
                highestY = Mathf.Max(highestY, targetTilemap.CellToWorld(pos).y);
            foreach (Tilemap tm in GetAllCustomTilemaps())
                if (tm.GetTile(pos) != null)
                    highestY = Mathf.Max(highestY, tm.CellToWorld(pos).y);
            return highestY == float.MinValue ? 0f : highestY;
        }

        public IEnumerable<Tilemap> GetAllCustomTilemaps()
        {
            HashSet<Tilemap> customMaps = new HashSet<Tilemap>();
            foreach (var tm in heightTilemaps.Values)
                customMaps.Add(tm);
            foreach (var rule in tileRules)
                if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                    customMaps.Add(rule.customTargetTilemap);
            return customMaps;
        }

        public TileRule GetSelectedTileRule()
        {
            if (selectedTileRuleIndex >= 0 && selectedTileRuleIndex < tileRules.Count)
                return tileRules[selectedTileRuleIndex];
            return null;
        }

        private void CreateMissingTileRules()
        {
            Dictionary<string, TileBase> localTileDict = BuildTileDictionary();

            var allTilemaps = new List<Tilemap>(GetAllCustomTilemaps());
            if (targetTilemap != null)
                allTilemaps.Add(targetTilemap);

            foreach (var tilemap in allTilemaps)
            {
                if (tilemap == null || tilemap.name == "TargetTilemap") continue;
                if (!TilemapHasAnyTiles(tilemap)) continue;

                bool hasRule = tileRules.Any(rule =>
                    rule.useCustomTilemap &&
                    rule.customTargetTilemap == tilemap);

                if (!hasRule)
                {
                    string tilemapName = tilemap.name;

                    string tileName = "";
                    float yOffset = 0f;

                    if (tilemapName.StartsWith("Tilemap_Rule_"))
                    {
                        string remainder = tilemapName.Substring("Tilemap_Rule_".Length);
                        int lastUnderscoreIndex = remainder.LastIndexOf('_');
                        if (lastUnderscoreIndex > 0)
                        {
                            tileName = remainder.Substring(0, lastUnderscoreIndex);
                            string yOffsetStr = remainder.Substring(lastUnderscoreIndex + 1);

                            if (!float.TryParse(yOffsetStr, out yOffset))
                            {
                                yOffset = tilemap.transform.localPosition.y;
                            }
                        }
                        else
                        {
                            tileName = remainder;
                            yOffset = tilemap.transform.localPosition.y;
                        }
                    }
                    else
                    {
                        tileName = tilemapName;
                        yOffset = tilemap.transform.localPosition.y;
                    }

                    TileBase foundTile = null;
                    if (!string.IsNullOrEmpty(tileName))
                    {
                        if (localTileDict.TryGetValue(tileName, out foundTile))
                        {
                            // exact match
                        }
                        else
                        {
                            var partialMatch = localTileDict.FirstOrDefault(kvp =>
                                kvp.Key.Contains(tileName) || tileName.Contains(kvp.Key));

                            if (partialMatch.Value != null)
                            {
                                foundTile = partialMatch.Value;
                                Debug.Log($"Found partial tile match: {partialMatch.Key} for {tileName}");
                            }
                        }
                    }

                    var newRule = new TileRule
                    {
                        tile = foundTile,
                        useCustomTilemap = true,
                        customTargetTilemap = tilemap,
                        customTargetTilemapName = tilemapName,
                        yOffset = yOffset,
                        color = foundTile != null ? Color.white : Color.red,
                        renderOrder = Mathf.RoundToInt(yOffset * 100f),
                        sizeY = 1f,
                        fixBase = false,
                        isVisible = true
                    };

                    tileRules.Add(newRule);

                    string status = foundTile != null ? "with tile reference" : "WITHOUT tile reference (marked red)";
                    Debug.Log($"✅ Auto-created tile rule for '{tilemapName}' {status}");

                    if (foundTile == null)
                    {
                        Debug.LogWarning($"⚠️ Could not find tile asset for '{tileName}'. Please assign manually in the inspector.");
                    }
                }
            }

            if (selectedTileRuleIndex < 0 && tileRules.Count > 0)
            {
                selectedTileRuleIndex = 0;
            }
        }

        // ─────────────────────────────────────
        // Skirts
        // ─────────────────────────────────────

        public void ForceRegenerateAllSkirts()
        {
            var skirts = Resources.FindObjectsOfTypeAll<SkirtManager>()
                .Where(skirt => skirt != null && skirt.gameObject.scene.IsValid());

            foreach (var skirt in skirts)
            {
                var map = skirt.transform.parent ? skirt.transform.parent.GetComponent<Tilemap>() : null;
                if (map == null) continue;

                var rule = tileRules.FirstOrDefault(r => r.customTargetTilemap == map);
                if (rule == null) continue;

                if (rule.fixBase)
                {
                    skirt.wallCount = Mathf.FloorToInt(rule.yOffset / skirt.WallStep);
                    skirt.scaleValue = rule.yOffset * 10f;
                }
                else
                {
                    skirt.wallCount = Mathf.RoundToInt(rule.sizeY);
                    skirt.scaleValue = rule.sizeY;
                }

                skirt.ApplyVisuals();
            }
        }

        public void RefreshAllSkirts()
        {
            var skirts = Resources.FindObjectsOfTypeAll<SkirtManager>()
                .Where(skirt => skirt != null && skirt.gameObject.scene.IsValid());
            foreach (var skirt in skirts)
            {
                skirt.SyncWithTileRuleRuntime();
                skirt.Generate();
            }
        }

        private IEnumerator DelayedRefreshAllSkirts()
        {
            yield return new WaitForSecondsRealtime(0.2f);
            ForceRegenerateAllSkirts();
        }

        private void UpdateSkirtManagers(TileRule rule)
        {
            if (rule.customTargetTilemap == null) return;

            var skirts = rule.customTargetTilemap.GetComponentsInChildren<SkirtManager>(true);
            foreach (var skirt in skirts)
            {
                if (rule.fixBase)
                {
                    skirt.wallCount = Mathf.FloorToInt(rule.yOffset / skirt.WallStep);
                    skirt.scaleValue = rule.yOffset * 10f;
                }
                else
                {
                    skirt.wallCount = Mathf.RoundToInt(rule.sizeY);
                    skirt.scaleValue = rule.sizeY;
                }
                skirt.ApplyVisuals();
            }
        }
    }

}
