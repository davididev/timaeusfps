using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        private static readonly Vector3Int[] SkirtPlacementDirections =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0)
        };

        [System.NonSerialized] private Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();
        [System.NonSerialized] private Dictionary<string, Tilemap> tilemapByNameCache;
        [System.NonSerialized] private Dictionary<string, int> ruleIndexByIdCache;

        [ContextMenu("QuickTile → Clear Prefab Cache")]
        public void ClearPrefabCache()
        {
            prefabCache?.Clear();
        }

        private void RebuildTilemapNameCache()
        {
            if (tilemapByNameCache == null)
                tilemapByNameCache = new Dictionary<string, Tilemap>();
            else
                tilemapByNameCache.Clear();

            if (heightTilemaps != null)
            {
                foreach (var tm in heightTilemaps.Values)
                {
                    if (tm != null)
                        tilemapByNameCache[tm.name] = tm;
                }
            }
            if (targetTilemap != null && !tilemapByNameCache.ContainsKey(targetTilemap.name))
                tilemapByNameCache[targetTilemap.name] = targetTilemap;
        }

        private void RebuildRuleIndexCache()
        {
            if (ruleIndexByIdCache == null)
                ruleIndexByIdCache = new Dictionary<string, int>();
            else
                ruleIndexByIdCache.Clear();

            if (gameObjectRules == null) return;

            for (int i = 0; i < gameObjectRules.Count; i++)
            {
                var r = gameObjectRules[i];
                if (r != null && !string.IsNullOrEmpty(r.id))
                    ruleIndexByIdCache[r.id] = i;
            }
        }

#if UNITY_EDITOR
        private GameObject LoadPrefabSmart(GameObjectRule rule, PlacedObject po, int ruleIndex)
        {
            if (rule.prefab != null)
            {
                if (PrefabUtility.GetPrefabAssetType(rule.prefab) == PrefabAssetType.NotAPrefab)
                {
                    Debug.LogError($"[QuickTile] Rule {ruleIndex}: '{rule.prefab.name}' is not a valid prefab.");
                    return null;
                }
                return rule.prefab;
            }

            if (!string.IsNullOrEmpty(rule.prefabResourcePath))
            {
                if (prefabCache.TryGetValue(rule.prefabResourcePath, out GameObject cached))
                {
                    if (cached != null) return cached;
                    prefabCache.Remove(rule.prefabResourcePath);
                }

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(rule.prefabResourcePath);
                if (prefabAsset != null)
                {
                    prefabCache[rule.prefabResourcePath] = prefabAsset;
                    if (rule.prefab == null)
                    {
                        rule.prefab = prefabAsset;
                        EditorUtility.SetDirty(this);
                    }
                    return prefabAsset;
                }

                GameObject resourcePrefab = TryLoadFromResources(rule.prefabResourcePath);
                if (resourcePrefab != null)
                {
                    prefabCache[rule.prefabResourcePath] = resourcePrefab;
                    if (rule.prefab == null)
                    {
                        rule.prefab = resourcePrefab;
                        EditorUtility.SetDirty(this);
                    }
                    return resourcePrefab;
                }

                Debug.LogError($"[QuickTile] Rule {ruleIndex}: failed to load prefab from path '{rule.prefabResourcePath}'.");
                return null;
            }

            if (po != null && !string.IsNullOrEmpty(po.prefabResourcePath))
            {
                if (prefabCache.TryGetValue(po.prefabResourcePath, out GameObject cached))
                    return cached;

                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(po.prefabResourcePath);
                if (prefabAsset != null)
                {
                    prefabCache[po.prefabResourcePath] = prefabAsset;
                    return prefabAsset;
                }

                Debug.LogError($"[QuickTile] PlacedObject at {po.position}: invalid prefabResourcePath '{po.prefabResourcePath}'.");
                return null;
            }

            Debug.LogError($"[QuickTile] Rule {ruleIndex}: no valid prefab source (null reference and empty path).");
            return null;
        }

        private GameObject TryLoadFromResources(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            string resourcePath = assetPath;
            int resourcesIndex = resourcePath.LastIndexOf("Resources/");
            if (resourcesIndex >= 0)
                resourcePath = resourcePath.Substring(resourcesIndex + "Resources/".Length);

            if (resourcePath.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                resourcePath = resourcePath.Substring(0, resourcePath.Length - 7);

            return Resources.Load<GameObject>(resourcePath);
        }

        private GameObject InstantiatePrefabSafe(GameObject prefab, int ruleIndex, Vector3Int position)
        {
            if (prefab == null) return null;

            try
            {
                GameObject instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                if (instance == null)
                {
                    Debug.LogError($"[QuickTile] Rule {ruleIndex} at {position}: PrefabUtility.InstantiatePrefab returned null for '{prefab.name}'.");
                }
                return instance;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[QuickTile] Rule {ruleIndex} at {position}: failed to instantiate '{prefab.name}': {ex.Message}");
                return null;
            }
        }
#endif

        public void ClearPlacedObjects()
        {
            if (placedObjects != null) placedObjects.Clear();
        }

        public bool HasSupportingTileAtCell(Vector3Int cellPos)
        {
            return TryGetHighestSupportingSurface(cellPos, out _, out _, out _);
        }

        private bool TryGetHighestSupportingSurface(Vector3Int cellPos, out Tilemap parentTilemap, out float surfaceY, out TileRule sourceRule)
        {
            parentTilemap = null;
            surfaceY = float.MinValue;
            sourceRule = null;

            if (targetTilemap != null && targetTilemap.GetTile(cellPos) != null)
            {
                surfaceY = SafeCellToWorld(cellPos).y;
                parentTilemap = targetTilemap;
                sourceRule = FindTileRuleForTilemap(targetTilemap);
            }

            foreach (Tilemap tilemap in GetAllCustomTilemaps())
            {
                if (tilemap == null || tilemap.GetTile(cellPos) == null)
                    continue;

                float candidateY = tilemap.transform.position.y;
                if (candidateY >= surfaceY)
                {
                    surfaceY = candidateY;
                    parentTilemap = tilemap;
                    sourceRule = FindTileRuleForTilemap(tilemap);
                }
            }

            return parentTilemap != null;
        }

        private TileRule FindTileRuleForTilemap(Tilemap tilemap)
        {
            if (tilemap == null || tileRules == null)
                return null;

            TileRule fallback = null;

            foreach (TileRule rule in tileRules)
            {
                if (rule == null)
                    continue;

                Tilemap ruleTilemap = rule.useCustomTilemap && rule.customTargetTilemap != null
                    ? rule.customTargetTilemap
                    : targetTilemap;

                if (ruleTilemap != tilemap)
                    continue;

                if (fallback == null)
                    fallback = rule;

                if (rule.meshMode == MeshMode.Procedural)
                    return rule;
            }

            return fallback;
        }

        private float GetSkirtPlacementDrop(TileRule sourceRule)
        {
            if (sourceRule != null &&
                sourceRule.meshMode == MeshMode.Procedural &&
                sourceRule.proceduralSettings != null &&
                sourceRule.proceduralSettings.skirtEnabled)
            {
                return Mathf.Max(0.05f, sourceRule.proceduralSettings.skirtHeight * 0.5f);
            }

            return 0.24f;
        }

        public float GetRuleInstanceYOffsetForPlacement(GameObjectRule rule, GameObjectPlacementSurface placementSurface, Tilemap parentTilemap)
        {
            float ruleYOffset = rule != null ? rule.yOffset : 0f;
            if (placementSurface != GameObjectPlacementSurface.Skirt)
                return ruleYOffset;

            return ruleYOffset - GetSkirtPlacementDrop(FindTileRuleForTilemap(parentTilemap));
        }

        public bool TryResolveSkirtPlacement(Vector3Int placementCell, out Tilemap parentTilemap, out Vector3 worldPos, out Vector3Int anchorCell)
        {
            parentTilemap = null;
            worldPos = GetPlacementWorldPos(placementCell);
            anchorCell = placementCell;

            if (HasSupportingTileAtCell(placementCell))
                return false;

            float bestSurfaceY = float.MinValue;
            TileRule bestSourceRule = null;

            foreach (Vector3Int dir in SkirtPlacementDirections)
            {
                Vector3Int candidateAnchor = placementCell - dir;
                if (!TryGetHighestSupportingSurface(candidateAnchor, out Tilemap candidateTilemap, out float candidateSurfaceY, out TileRule candidateRule))
                    continue;

                if (candidateSurfaceY < bestSurfaceY)
                    continue;

                bestSurfaceY = candidateSurfaceY;
                parentTilemap = candidateTilemap;
                anchorCell = candidateAnchor;
                bestSourceRule = candidateRule;
            }

            if (parentTilemap == null)
                return false;

            Vector3 anchorWorld = GetPlacementWorldPos(anchorCell);
            Vector3 exteriorWorld = GetPlacementWorldPos(placementCell);
            worldPos = Vector3.Lerp(anchorWorld, exteriorWorld, 0.5f);
            worldPos.y = bestSurfaceY - GetSkirtPlacementDrop(bestSourceRule);
            return true;
        }

        public bool TryResolveNewGameObjectPlacement(GameObjectRule rule, Vector3Int placementCell, out Tilemap parentTilemap, out Vector3 worldPos, out Vector3Int skirtAnchorCell, out string failureMessage)
        {
            parentTilemap = null;
            worldPos = GetPlacementWorldPos(placementCell);
            skirtAnchorCell = placementCell;
            failureMessage = null;

            if (rule == null)
            {
                failureMessage = "No GameObject rule selected.";
                return false;
            }

            if (rule.placementSurface == GameObjectPlacementSurface.Skirt)
            {
                if (HasSupportingTileAtCell(placementCell))
                {
                    failureMessage = "Pour placer un objet sur la skirt, clique sur une case vide juste au bord de la surface.";
                    return false;
                }

                if (!TryResolveSkirtPlacement(placementCell, out parentTilemap, out worldPos, out skirtAnchorCell))
                {
                    failureMessage = "Aucune skirt valide trouvée ici. Clique sur une case vide adjacente à une surface.";
                    return false;
                }

                worldPos.y = parentTilemap.transform.position.y + GetRuleInstanceYOffsetForPlacement(rule, GameObjectPlacementSurface.Skirt, parentTilemap);
                return true;
            }

            float highestY = float.MinValue;
            if (TryGetHighestSupportingSurface(placementCell, out Tilemap supportingTilemap, out float supportingSurfaceY, out _))
            {
                parentTilemap = supportingTilemap;
                highestY = supportingSurfaceY;
            }

            if (rule.placeOnGround && highestY == float.MinValue)
            {
                failureMessage = "No tile exists at this location. This object is set to 'Place on Ground' only.";
                return false;
            }

            if (parentTilemap == null)
                parentTilemap = targetTilemap ?? GetAllCustomTilemaps().FirstOrDefault(tm => tm != null);

            if (highestY == float.MinValue)
                highestY = 0f;

            worldPos = GetPlacementWorldPos(placementCell);
            worldPos.y = highestY + rule.yOffset;
            return true;
        }

        public bool TryResolvePlacedObjectWorldPosition(PlacedObject placedObject, GameObjectRule rule, out Tilemap parentTilemap, out Vector3 worldPos)
        {
            parentTilemap = null;
            worldPos = Vector3.zero;

            if (placedObject == null)
                return false;

            if (placedObject.placementSurface == GameObjectPlacementSurface.Skirt)
            {
                Vector3Int anchorCell = placedObject.skirtAnchorCell;
                if (anchorCell == placedObject.position)
                {
                    if (!TryResolveSkirtPlacement(placedObject.position, out parentTilemap, out worldPos, out _))
                        return false;
                }
                else
                {
                    if (!TryGetHighestSupportingSurface(anchorCell, out parentTilemap, out _, out _))
                    {
                        if (!TryResolveSkirtPlacement(placedObject.position, out parentTilemap, out worldPos, out anchorCell))
                            return false;
                    }
                    else
                    {
                        Vector3 anchorWorld = GetPlacementWorldPos(anchorCell);
                        Vector3 exteriorWorld = GetPlacementWorldPos(placedObject.position);
                        worldPos = Vector3.Lerp(anchorWorld, exteriorWorld, 0.5f);
                    }
                }

                if (parentTilemap == null)
                    return false;

                worldPos.y = parentTilemap.transform.position.y + placedObject.instanceYOffset;
                return true;
            }

            parentTilemap = ResolveParentTilemap(placedObject);
            if (parentTilemap == null)
                return false;

            worldPos = GetPlacementWorldPos(placedObject.position);
            worldPos.y = parentTilemap.transform.position.y + placedObject.instanceYOffset;
            return true;
        }


        private void InstantiateGameObjects()
        {

            if (gameObjectRules == null)
            {
                Debug.LogWarning("gameObjectRules is null, initializing empty list");
                gameObjectRules = new List<GameObjectRule>();
                return;
            }

            if (placedObjects == null)
            {
                Debug.LogWarning("placedObjects is null, initializing empty list");
                placedObjects = new List<PlacedObject>();
                return;
            }

            instantiatedGameObjects ??= new List<GameObject>();

            ValidateAndCleanData();

            bool mergedHeights = MergeLegacyGameObjectHeights();
            bool upgradedOffsets = UpgradeLegacyInstanceYOffsets();
#if UNITY_EDITOR
            if ((mergedHeights || upgradedOffsets) && !Application.isPlaying)
                EditorUtility.SetDirty(this);
#endif

            RebuildTilemapNameCache();
            RebuildRuleIndexCache();

            foreach (var rule in gameObjectRules)
            {
                if (rule != null)
                {
                    rule.instanceOffsets ??= new List<InstanceOffset>();
                    rule.instanceOffsets.Clear();
                }
            }

            var keepById = new Dictionary<string, GameObject>();
            foreach (var go in instantiatedGameObjects.ToArray())
            {
                if (go == null) continue;
                var marker = go.GetComponent<QuickTileMarker>();
                if (marker == null) { SafeDestroy(go); instantiatedGameObjects.Remove(go); continue; }
                // si lâ€™instance appartient Ã  cet Ã©diteur et existe encore cÃ´tÃ© data â†’ on garde
                var po = placedObjects.FirstOrDefault(p => p.UniqueId == marker.PlacedObjectId);
                if (po != null && po.ruleIndex >= 0 && po.ruleIndex < gameObjectRules.Count)
                    keepById[po.UniqueId] = go;
                else
                {
                    SafeDestroy(go);
                    instantiatedGameObjects.Remove(go);
                }
            }

            foreach (PlacedObject po in placedObjects)
            {
                if (po == null) continue;
                if (po.ruleIndex < 0 || po.ruleIndex >= gameObjectRules.Count)
                {
                    Debug.LogWarning($"[QT-Repop] Invalid ruleIndex {po.ruleIndex} for PlacedObject at {po.position}.");
                    continue;
                }

                if (keepById.ContainsKey(po.UniqueId)) { continue; }

                // FIND RULE BY ID FIRST
                GameObjectRule rule = null;
                int currentRuleIndex = -1;

                if (!string.IsNullOrEmpty(po.ruleId))
                {
                    if (ruleIndexByIdCache != null && ruleIndexByIdCache.TryGetValue(po.ruleId, out int cachedIdx))
                    {
                        currentRuleIndex = cachedIdx;
                        rule = gameObjectRules[cachedIdx];
                    }
                }

                // FALLBACK TO INDEX (Legacy)
                if (rule == null)
                {
                    if (po.ruleIndex >= 0 && po.ruleIndex < gameObjectRules.Count)
                    {
                        rule = gameObjectRules[po.ruleIndex];
                        currentRuleIndex = po.ruleIndex;
                        
                        // Auto-fix ID for next time
                        if (rule != null)
                        {
                            if (string.IsNullOrEmpty(rule.id)) rule.id = System.Guid.NewGuid().ToString();
                            po.ruleId = rule.id;
                        }
                    }
                }

                if (rule == null) { Debug.LogWarning($"No valid rule for PlacedObject at {po.position} (ruleIndex={po.ruleIndex}), skipping."); continue; }

                // Keep the object's index in sync for other systems that might rely on it casually
                po.ruleIndex = currentRuleIndex;

                if (!TryResolvePlacedObjectWorldPosition(po, rule, out Tilemap parentTilemap, out Vector3 worldPos))
                {
                    Debug.LogWarning($"No valid placement found for PlacedObject at {po.position}, skipping");
                    continue;
                }

                // ⛑ Sanity check: extreme Y offsets are almost certainly from a
                // resync that ran before/after CenterLevelToSurfaceMassXZ moved the container.
                if (Mathf.Abs(po.instanceYOffset) > 500f)
                {
                    po.instanceYOffset = GetRuleInstanceYOffsetForPlacement(rule, po.placementSurface, parentTilemap);
                    po.MarkInstanceYOffsetUpgraded();

                    if (!TryResolvePlacedObjectWorldPosition(po, rule, out parentTilemap, out worldPos))
                        continue;
                }

#if UNITY_EDITOR
                GameObject prefab = LoadPrefabSmart(rule, po, po.ruleIndex);
                GameObject placedGO = prefab != null ? InstantiatePrefabSafe(prefab, po.ruleIndex, po.position) : null;

                if (placedGO != null)
                {
                    try
                    {
                        ConfigureGameObject(placedGO, parentTilemap, worldPos, po, rule);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"Failed to configure GameObject at {po.position}: {ex.Message}\n{ex.StackTrace}");
                        SafeDestroy(placedGO);
                        po.isValid = false;
                    }
                }
                else
                {
                    // âš ï¸ ne pas laisser dâ€™orphelin data
                    po.isValid = false;
                    Debug.LogError($"âŒ Instantiate failed â†’ marking PlacedObject invalid: rule {po.ruleIndex} at {po.position}");
                }
#else
                if (!string.IsNullOrEmpty(rule.prefabResourcePath))
                {
                    StartCoroutine(LoadAndInstantiateAddressable(rule.prefabResourcePath, parentTilemap, worldPos, po, rule));
                }
                else if (!string.IsNullOrEmpty(po.prefabResourcePath))
                {
                    StartCoroutine(LoadAndInstantiateAddressable(po.prefabResourcePath, parentTilemap, worldPos, po, rule));
                }
                else
                {
                    Debug.LogWarning($"Prefab resource path is missing for rule index {po.ruleIndex}. Cannot load Addressable prefab.");
                }
#endif
            }

        }

        private void ApplyInverseScaleToGameObject(GameObject go)
        {
            if (go == null) return;
            float scale = Mathf.Max(1, gridScale);
            float inverseScale = 1f / scale;
            go.transform.localScale = new Vector3(inverseScale, inverseScale, inverseScale);
        }

        private void ConfigureGameObject(GameObject placedGO, Tilemap parentTilemap, Vector3 worldPos, PlacedObject po, GameObjectRule rule)
        {
            placedGO.transform.SetParent(parentTilemap.transform, false);
            placedGO.transform.localPosition = parentTilemap.transform.InverseTransformPoint(worldPos);
            placedGO.transform.localRotation = Quaternion.Euler(0, po.rotation, 0);
            ApplyInverseScaleToGameObject(placedGO);

            instantiatedGameObjects.Add(placedGO);
            placedGO.SetActive(rule.isVisible);

            // Marqueur dâ€™identitÃ©
            var marker = placedGO.GetComponent<QuickTileMarker>() ?? placedGO.AddComponent<QuickTileMarker>();
            marker.Initialize(po.UniqueId, GetInstanceID().ToString(), po.ruleIndex, po.position);

            var newOffset = new InstanceOffset { instanceObject = placedGO, yOffset = po.instanceYOffset };
            if (!rule.instanceOffsets.Exists(off => off.instanceObject == placedGO))
                rule.instanceOffsets.Add(newOffset);

            ConfigurePathFollower(placedGO, po);

            // Follow deformed ground Y — attach/remove component based on rule toggle
            bool allowFollowDeformationY = rule.followDeformationY &&
                                           po.placementSurface != GameObjectPlacementSurface.Skirt;

            if (allowFollowDeformationY)
            {
                if (placedGO.GetComponent<FollowDeformedGround>() == null)
                    placedGO.AddComponent<FollowDeformedGround>();
            }
            else
            {
                var existing = placedGO.GetComponent<FollowDeformedGround>();
                if (existing != null) SafeDestroy(existing);
            }

            RefreshAllRadialHillDeformerBindings();
        }

        private void ValidateAndCleanData()
        {
            // Ensure all rules have IDs
            bool rulesModified = false;
            if (gameObjectRules != null)
            {
                foreach (var rule in gameObjectRules)
                {
                    if (rule != null && string.IsNullOrEmpty(rule.id))
                    {
                        rule.id = System.Guid.NewGuid().ToString();
                        rulesModified = true;
                    }
                }
            }

            if (placedObjects == null) return;

            var localRuleIndexById = new Dictionary<string, int>();
            if (gameObjectRules != null)
            {
                for (int i = 0; i < gameObjectRules.Count; i++)
                {
                    var r = gameObjectRules[i];
                    if (r != null && !string.IsNullOrEmpty(r.id))
                        localRuleIndexById[r.id] = i;
                }
            }

            // Migrate PlacedObjects to use IDs
            bool objectsModified = false;
            foreach (var po in placedObjects)
            {
                // 1. If no ID but valid index, migrate to ID
                if (string.IsNullOrEmpty(po.ruleId) && po.ruleIndex >= 0 && po.ruleIndex < gameObjectRules.Count)
                {
                    var rule = gameObjectRules[po.ruleIndex];
                    if (rule != null)
                    {
                        po.ruleId = rule.id;
                        objectsModified = true;
                    }
                }

                // 2. If ID exists, validate/repair index (just in case we need it for UI/fallback)
                bool hasMatchingRule = false;
                if (!string.IsNullOrEmpty(po.ruleId) && localRuleIndexById.TryGetValue(po.ruleId, out int foundIndex))
                {
                    hasMatchingRule = true;
                    if (foundIndex != po.ruleIndex)
                        po.ruleIndex = foundIndex;
                }

                // Validate final state
                if (!string.IsNullOrEmpty(po.ruleId))
                    po.isValid = hasMatchingRule;
                else
                    po.isValid = (po.ruleIndex >= 0 && po.ruleIndex < gameObjectRules.Count);
            }

            if (rulesModified || objectsModified)
            {
#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
            }

            // supprime les entrées manifestement corrompues
            placedObjects.RemoveAll(po => !po.isValid && string.IsNullOrEmpty(po.parentTilemapName));
        }

        [ContextMenu("QuickTile → Resync Instances")]
        public void ResyncInstancesSafe()
        {
            var markers = FindObjectsByType<QuickTileMarker>(FindObjectsSortMode.None);
            var validIds = placedObjects.Select(p => p.UniqueId).ToHashSet();
            string editorId = GetInstanceID().ToString();

            // Cleanup orphans (instances without data or from another editor).
            // Missing ones are handled by InstantiateGameObjects() below.
            foreach (var m in markers)
            {
                if (m == null) continue;
                if (!validIds.Contains(m.PlacedObjectId) || m.EditorInstanceId != editorId)
                    SafeDestroy(m.gameObject);
            }

            InstantiateGameObjects();
        }

        private void ConfigurePathFollower(GameObject placedGO, PlacedObject po)
        {
            PathFollower pf = placedGO.GetComponent<PathFollower>();
            if (pf != null && po.pathIndex > 0 && po.pathIndex <= paths.Count)
            {
                int pathIndex = po.pathIndex - 1;
                pf.SetPathIndex(pathIndex);

                var worldPath = paths[pathIndex].points
                    .Select(p => {
                        Vector3 wp = GetPlacementWorldPos(p);
                        return new Vector2(wp.x, wp.z);
                    })
                    .ToList();
                pf.SetPath(worldPath);
                if (Application.isPlaying) pf.StartMoving();
            }
        }

        public void UpdateGameObjectRuleOnScene(GameObjectRule rule, int ruleIndex)
        {
            if (rule == null || rule.prefab == null) return;

            var rulePlacedObjectIds = placedObjects
                .Where(po => po != null && po.ruleIndex == ruleIndex)
                .Select(po => po.UniqueId)
                .ToHashSet();

            var toRemove = instantiatedGameObjects
                .Where(go =>
                {
                    if (go == null) return false;

                    var marker = go.GetComponent<QuickTileMarker>();
                    if (marker != null && rulePlacedObjectIds.Contains(marker.PlacedObjectId))
                        return true;

                    Tilemap tm = go.transform.parent?.GetComponent<Tilemap>();
                    if (tm == null) return false;
                    Vector3Int cell = SafeWorldToCell(go.transform.position, tm);
                    return placedObjects.Any(po => po.position == cell && po.ruleIndex == ruleIndex);
                })
                .ToList();

#if UNITY_EDITOR
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("QuickTile: Update GameObject Rule");
            Undo.RegisterCompleteObjectUndo(this, "QuickTile: Update GameObject Rule");
            foreach (var go in toRemove) Undo.DestroyObjectImmediate(go);
#else
            foreach (var go in toRemove) Destroy(go);
#endif
            instantiatedGameObjects.RemoveAll(go => toRemove.Contains(go));

            InstantiateGameObjects();

#if UNITY_EDITOR
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(this);
            SceneView.RepaintAll();
#endif
        }

        public void PlaceGameObjectDot(Vector3Int position, int ruleIndex, Color color)
        {
            if (!placedObjects.Exists(p => p.position == position))
            {
                float initialYOffset = 0f;
                string ruleId = "";
                if (ruleIndex >= 0 && ruleIndex < gameObjectRules.Count && gameObjectRules[ruleIndex] != null)
                {
                    var rule = gameObjectRules[ruleIndex];
                    initialYOffset = rule.yOffset;
                    if (string.IsNullOrEmpty(rule.id)) rule.id = System.Guid.NewGuid().ToString();
                    ruleId = rule.id;
                }

                var placedObject = new PlacedObject
                {
                    position = position,
                    ruleIndex = ruleIndex,
                    ruleId = ruleId,
                    color = color
                };
                placedObject.instanceYOffset = initialYOffset;
                placedObject.MarkInstanceYOffsetUpgraded();
                placedObjects.Add(placedObject);
#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
            }
        }

        public void EraseGameObjectDot(Vector3Int gridPosition)
        {
            var removedPlacedObjectIds = placedObjects
                .Where(p => p != null && p.position == gridPosition)
                .Select(p => p.UniqueId)
                .ToHashSet();

            placedObjects.RemoveAll(p => p != null && p.position == gridPosition);

            bool removedSceneObject = false;
            for (int i = instantiatedGameObjects.Count - 1; i >= 0; i--)
            {
                GameObject go = instantiatedGameObjects[i];
                if (go == null)
                {
                    instantiatedGameObjects.RemoveAt(i);
                    continue;
                }

                if (!ShouldEraseInstantiatedGameObject(go, gridPosition, removedPlacedObjectIds))
                    continue;

                instantiatedGameObjects.RemoveAt(i);
                RemoveInstanceOffsetReferences(go);
                removedSceneObject = true;
#if UNITY_EDITOR
                Undo.DestroyObjectImmediate(go);
#else
                Destroy(go);
#endif
            }

            CleanupNullInstanceOffsets();

#if UNITY_EDITOR
            if (removedPlacedObjectIds.Count > 0 || removedSceneObject)
            {
                EditorUtility.SetDirty(this);
                SceneView.RepaintAll();
            }
#endif
        }

        private bool ShouldEraseInstantiatedGameObject(GameObject go, Vector3Int gridPosition, HashSet<string> removedPlacedObjectIds)
        {
            if (go == null) return false;

            var marker = go.GetComponent<QuickTileMarker>();
            if (marker != null)
            {
                if (marker.EditorInstanceId == GetInstanceID().ToString())
                {
                    if (!string.IsNullOrEmpty(marker.PlacedObjectId) && removedPlacedObjectIds.Contains(marker.PlacedObjectId))
                        return true;

                    if (marker.GridPosition == gridPosition)
                        return true;
                }
            }

            Tilemap parentTilemap = go.transform.parent ? go.transform.parent.GetComponent<Tilemap>() : null;
            if (parentTilemap == null) return false;

            Vector3Int cell = SafeWorldToCell(go.transform.position, parentTilemap);
            return cell == gridPosition;
        }

        private void RemoveInstanceOffsetReferences(GameObject instanceObject)
        {
            if (instanceObject == null || gameObjectRules == null) return;

            foreach (var rule in gameObjectRules)
            {
                if (rule?.instanceOffsets == null) continue;
                rule.instanceOffsets.RemoveAll(offset => offset == null || offset.instanceObject == null || offset.instanceObject == instanceObject);
            }
        }

        private void CleanupNullInstanceOffsets()
        {
            if (gameObjectRules == null) return;

            foreach (var rule in gameObjectRules)
            {
                if (rule?.instanceOffsets == null) continue;
                rule.instanceOffsets.RemoveAll(offset => offset == null || offset.instanceObject == null);
            }
        }

        public void EraseAllGameObjects()
        {
            ClearPlacedObjects();
            paths.Clear();

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
            for (int i = instantiatedGameObjects.Count - 1; i >= 0; i--)
            {
                var go = instantiatedGameObjects[i];
                if (go != null) Undo.DestroyObjectImmediate(go);
            }
            instantiatedGameObjects.Clear();

            Transform container = GetPrefabContainer();
            if (container != null)
            {
                for (int i = container.childCount - 1; i >= 0; i--)
                {
                    var child = container.GetChild(i).gameObject;
                    if (child != null) Undo.DestroyObjectImmediate(child);
                }
            }
#else
            for (int i = instantiatedGameObjects.Count - 1; i >= 0; i--)
            {
                var go = instantiatedGameObjects[i];
                if (go != null) GameObject.Destroy(go);
            }
            instantiatedGameObjects.Clear();

            Transform container = GetPrefabContainer();
            if (container != null)
            {
                for (int i = container.childCount - 1; i >= 0; i--)
                {
                    var child = container.GetChild(i).gameObject;
                    if (child != null) GameObject.Destroy(child);
                }
            }
#endif
        }

        public void ResynchronizeGameObjectsFromScene()
        {
            instantiatedGameObjects.Clear();

            var markers = FindObjectsByType<QuickTileMarker>(FindObjectsSortMode.None)
                .Where(m => m.EditorInstanceId == GetInstanceID().ToString());

            var validIds = placedObjects.Select(p => p.UniqueId).ToHashSet();

            foreach (var m in markers)
            {
                if (!validIds.Contains(m.PlacedObjectId))
                {
                    // orphelin â†’ nettoyage
                    SafeDestroy(m.gameObject);
                    continue;
                }
                instantiatedGameObjects.Add(m.gameObject);
            }

            // RÃ©instancier les manquants
            var present = new HashSet<string>(instantiatedGameObjects
                .Select(go => go.GetComponent<QuickTileMarker>())
                .Where(mm => mm != null)
                .Select(mm => mm.PlacedObjectId));

            bool needsInstantiate = false;
            foreach (var po in placedObjects)
                if (!present.Contains(po.UniqueId))
                    needsInstantiate = true;

            if (needsInstantiate)
                InstantiateGameObjects();
        }

        void ResyncEditorStateFromScene()
        {
            var prevPathLookup = new Dictionary<Vector3Int, int>();
            foreach (var p in placedObjects)
                prevPathLookup[p.position] = p.pathIndex;

            placedObjects.Clear();

            var tempPlacedObjects = new List<PlacedObject>();

            foreach (GameObject go in instantiatedGameObjects)
            {
                if (go == null) continue;

                int ruleIndex = -1;

                // Prefer QuickTileMarker (stable, non-ambiguous) over fragile name matching
                var marker = go.GetComponent<QuickTileMarker>();
                if (marker != null && marker.RuleIndex >= 0 && marker.RuleIndex < gameObjectRules.Count)
                    ruleIndex = marker.RuleIndex;

                if (ruleIndex == -1)
                {
                    ruleIndex = gameObjectRules.FindIndex(r =>
                        r.prefab != null && go.name.Contains(r.prefab.name));
                }

                if (ruleIndex == -1) continue;

                Tilemap parentMap = go.transform.parent
                                         ? go.transform.parent.GetComponent<Tilemap>()
                                         : null;
                if (parentMap == null) continue;

                Vector3Int cellPos = SafeWorldToCell(go.transform.position, parentMap);

                int pathIdx = -1;
                if (prevPathLookup.TryGetValue(cellPos, out int prev))
                    pathIdx = prev;
                else
                {
                    PathFollower pf = go.GetComponent<PathFollower>();
                    if (pf != null && pf.GetPathIndex() >= 0)
                        pathIdx = pf.GetPathIndex() + 1;
                }

                float relativeOffset = ComputeInstanceYOffset(parentMap, cellPos, go.transform.position);
                
                // Get or generate ID for the rule
                var rule = gameObjectRules[ruleIndex];
                if (string.IsNullOrEmpty(rule.id)) rule.id = System.Guid.NewGuid().ToString();

                var resynced = new PlacedObject
                {
                    position = cellPos,
                    rotation = go.transform.eulerAngles.y,
                    color = Color.white,
                    ruleIndex = ruleIndex,
                    ruleId = rule.id,
                    parentTilemapName = parentMap.name,
                    pathIndex = pathIdx
                };
                resynced.instanceYOffset = relativeOffset;
                resynced.MarkInstanceYOffsetUpgraded();
                tempPlacedObjects.Add(resynced);
            }

            var uniquePlacedObjects = new Dictionary<Vector3Int, PlacedObject>();
            foreach (var obj in tempPlacedObjects)
                uniquePlacedObjects[obj.position] = obj;

            placedObjects.AddRange(uniquePlacedObjects.Values);

            targetTilemap?.CompressBounds();
            foreach (var tm in heightTilemaps.Values)
                tm?.CompressBounds();
        }

        public bool MergeLegacyGameObjectHeights()
        {
            if (gameObjectRules == null) return false;

            bool changed = false;
            foreach (var rule in gameObjectRules)
            {
                if (rule != null && rule.MergeLegacyHeightIntoOffset())
                    changed = true;
            }

            return changed;
        }

        public bool UpgradeLegacyInstanceYOffsets()
        {
            if (placedObjects == null) return false;

            bool changed = false;

            foreach (var po in placedObjects)
            {
                if (po == null || !po.NeedsInstanceYOffsetUpgrade) continue;

                Tilemap parentTilemap = ResolveParentTilemap(po);
                if (parentTilemap == null) continue;

                Vector3 cellCenter = GetPlacementWorldPos(po.position);
                float parentWorldY = parentTilemap.transform.position.y;
                float worldY = parentWorldY + po.instanceYOffset;
                float newOffset = worldY - cellCenter.y;
                po.instanceYOffset = newOffset;
                po.MarkInstanceYOffsetUpgraded();
                changed = true;
            }

            return changed;
        }

        public Tilemap ResolveParentTilemap(PlacedObject po)
        {
            if (po == null)
                return targetTilemap;

            Tilemap parentTilemap = null;

            if (!string.IsNullOrEmpty(po.parentTilemapName))
            {
                if (tilemapByNameCache != null && tilemapByNameCache.TryGetValue(po.parentTilemapName, out Tilemap cachedTm) && cachedTm != null)
                {
                    parentTilemap = cachedTm;
                }
                else if (targetTilemap != null && targetTilemap.name == po.parentTilemapName)
                {
                    parentTilemap = targetTilemap;
                }
                else if (heightTilemaps != null)
                {
                    parentTilemap = heightTilemaps.Values.FirstOrDefault(tm => tm != null && tm.name == po.parentTilemapName);
                }

                if (parentTilemap == null && targetTilemap != null)
                    Debug.LogWarning($"Tilemap '{po.parentTilemapName}' not found, defaulting to targetTilemap.");
            }

            if (parentTilemap == null && targetTilemap != null)
                parentTilemap = targetTilemap;

            if (parentTilemap == null && heightTilemaps != null && heightTilemaps.Count > 0)
                parentTilemap = heightTilemaps.Values.FirstOrDefault(tm => tm != null);

            return parentTilemap;
        }

        public float ComputeInstanceYOffset(Tilemap parentTilemap, Vector3Int cellPosition, Vector3 worldPosition)
        {
            if (parentTilemap == null)
                return 0f;

            // In dual-grid mode, GetPlacementWorldPos returns Y=0 which doesn't
            // reflect the tilemap surface height.  Use the tilemap's own world-Y
            // as the reference so the offset captures only per-instance adjustments.
            if (IsDualGrid())
                return worldPosition.y - parentTilemap.transform.position.y;

            Vector3 cellCenter = GetPlacementWorldPos(cellPosition);
            return worldPosition.y - cellCenter.y;
        }

#if UNITY_EDITOR
        [ContextMenu("QuickTile â†’ Fix Missing Instance YOffsets")]
        void FixMissingInstanceYOffsets()
        {
            if (placedObjects == null || gameObjectRules == null) return;

            for (int i = 0; i < placedObjects.Count; i++)
            {
                var po = placedObjects[i];
                if (po == null) continue;

                bool missing = Mathf.Approximately(po.instanceYOffset, 0f);
                bool ruleOk = (po.ruleIndex >= 0 && po.ruleIndex < gameObjectRules.Count);

                if (missing && ruleOk)
                {
                    var rule = gameObjectRules[po.ruleIndex];
                    po.instanceYOffset = rule != null ? rule.yOffset : 0f;
                    po.MarkInstanceYOffsetUpgraded();
                }
            }

            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif

    }
}
