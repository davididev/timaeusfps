// Assets/BEKKOLOCO/QuickTile/Script/extra/QuickTilemapEditor_LevelSystem.cs
// Partial: Level & JSON load/save logic — behavior preserved 1:1.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;
using Bekkoloco.DOTS;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        [NonSerialized] private bool _forceLevelReload;
        [NonSerialized] private bool _suppressReloadAfterSave;

        // ---------------------------------------------------------------------
        // Tile assets lookup
        // ---------------------------------------------------------------------
        public Dictionary<string, TileBase> BuildTileDictionary()
        {
            Dictionary<string, TileBase> dict = new Dictionary<string, TileBase>();
#if UNITY_EDITOR
            string[] guids = AssetDatabase.FindAssets("t:TileBase");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TileBase tile = AssetDatabase.LoadAssetAtPath<TileBase>(path);
                if (tile != null && !dict.ContainsKey(tile.name))
                    dict.Add(tile.name, tile);
            }
#else
            TileBase[] tiles = Resources.LoadAll<TileBase>("");
            foreach (TileBase tile in tiles)
            {
                if (!dict.ContainsKey(tile.name))
                    dict.Add(tile.name, tile);
            }
#endif
            return dict;
        }

        // ---------------------------------------------------------------------
        // Editor: Load a level by index (JSON in TextAsset)
        // ---------------------------------------------------------------------
        public void LoadLevel(int index, Dictionary<string, TileBase> tileDict)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && index == currentLevelIndex && !_forceLevelReload)
                return;
#endif

            if (levels == null || levels.Count == 0)
            {
                Debug.LogWarning("No levels defined. Creating a default level entry.");
                if (levels == null) levels = new List<LevelData>();

                var defaultLevel = new LevelData
                {
                    levelName = "DefaultLevel",
                    properties = new List<LevelProperty>(),
                    paintedTextures = new List<PaintedTextureData>(),
                    centerOriginToSurfaceMass = true
                };

                levels.Add(defaultLevel);
                currentLevelIndex = 0;

                ClearCurrentLevel();
                return;
            }

            if (index < 0 || index >= levels.Count)
            {
                Debug.LogWarning($"Invalid level index {index}. Clamping to valid range [0, {levels.Count - 1}]");
                index = Mathf.Clamp(index, 0, levels.Count - 1);
            }

#if UNITY_EDITOR
            CacheCurrentLevelTexturePaintRulesForSession();
#endif
            currentLevelIndex = index;
            LevelData level = levels[currentLevelIndex];

            EraseAllGameObjects();
            ClearTexturePaintRuntimeState(clearRules: false);

            if (level.jsonFile == null)
            {
                string cleanName = level.levelName.Replace(".json", "").Replace(".bytes", "");
                TextAsset jsonAsset = Resources.Load<TextAsset>($"Levels/{cleanName}");

                if (jsonAsset == null)
                {
                    Debug.LogWarning($"No JSON file found for level {cleanName}. Creating empty level.");
                    ClearCurrentLevel();
                    return;
                }

                level.jsonFile = jsonAsset;
            }

            string cleanLevelName = level.levelName.Replace(".json", "").Replace(".bytes", "");
            string tempPath = System.IO.Path.Combine(Application.temporaryCachePath, cleanLevelName + ".json");

            try { File.WriteAllText(tempPath, level.jsonFile.text); }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to write temp file: {ex.Message}");
                ClearCurrentLevel();
                return;
            }

            if (tileDict == null || tileDict.Count == 0)
            {
                tileDict = new Dictionary<string, TileBase>();
                foreach (TileBase t in Resources.LoadAll<TileBase>(""))
                    if (!tileDict.ContainsKey(t.name))
                        tileDict.Add(t.name, t);
            }

            try { LoadTilemapFromJson(tempPath, tileDict); }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to load tilemap from JSON: {ex.Message}");
                ClearCurrentLevel();
                return;
            }

            if (level.paintedTextures != null && level.paintedTextures.Count > 0)
            {
                foreach (var pt in level.paintedTextures)
                    if (pt.textureIndex >= 0 && pt.textureIndex < texturePaintRules.Count)
                        PaintTextureCell(pt.position, texturePaintRules[pt.textureIndex]);
            }
            else
            {
                texturePaintMask.Clear();
            }

            RebuildPaintMaskAndMaterials();
            RefreshAllRadialHillDeformerBindings();
            RebuildAllProceduralMeshes();
            EnsureNavMeshSurface();
            // _navSurface.BuildNavMesh();
            StartCoroutine(DelayedNavMeshBuild(0.5f));

            //    Debug.Log($"Successfully loaded level: {level.levelName}");
        }

        private IEnumerator DelayedNavMeshBuild(float delay)
        {
            if (_navSurface == null)
            {
                Debug.LogWarning("[QuickTilemapEditor] NavMeshSurface is null, cannot build NavMesh");
                yield break;
            }


            // Attendre que les RadialHillDeformers appliquent leurs déformations
            yield return new WaitForSeconds(delay);

            // Construire le NavMesh avec le terrain déformé
            _navSurface.BuildNavMesh();
        }

        private Path RestorePathFromData(PathData pd)
        {
            if (pd == null)
                return null;

            var restoredPath = new Path
            {
                points = pd.points != null ? new List<Vector3Int>(pd.points) : new List<Vector3Int>(),
                color = pd.color,
                isVisible = pd.isVisible,
                pathType = pd.pathType,
                stairSteps = pd.stairSteps,
                stairAutoSteps = pd.stairAutoSteps,
                stairStepDepth = pd.stairStepDepth > 0.0001f ? pd.stairStepDepth : 1f,
                slopeWidth = pd.slopeWidth,
                smoothTransition = pd.smoothTransition,
                slopeSideSkirtEnabled = pd.slopeSideSkirtEnabled,
                slopeSideSkirtWidth = pd.slopeSideSkirtWidth,
                slopeSideSkirtHeight = pd.slopeSideSkirtHeight,
                slopeSideSkirtSegments = pd.slopeSideSkirtSegments,
                slopeSideSkirtUVScale = pd.slopeSideSkirtUVScale,
                slopeSideSkirtUVOffsetY = pd.slopeSideSkirtUVOffsetY,
                bridgeHeight = pd.bridgeHeight,
                bridgeWidth = pd.bridgeWidth,
                bridgeProfile = pd.bridgeProfile,
                bridgeCurve = pd.bridgeCurve,
                bridgeSteps = pd.bridgeSteps,
                bridgeRailings = pd.bridgeRailings,
                bridgeRailThickness = pd.bridgeRailThickness,
                bridgeRailSpread = pd.bridgeRailSpread,
                bridgeRailEndExtension = pd.bridgeRailEndExtension,
                bridgeRailYOffset = pd.bridgeRailYOffset,
                bridgeRailUvOffsetY = pd.bridgeRailUvOffsetY,
                bridgeRailCurveFollow = pd.bridgeRailCurveFollow,
                enableTrackMesh = pd.enableTrackMesh,
                trackPoints = pd.trackPoints != null ? new List<TrackPoint>(pd.trackPoints) : new List<TrackPoint>(),
                trackWidth = pd.trackWidth,
                trackSubdivisions = pd.trackSubdivisions,
                trackUVTilingY = pd.trackUVTilingY
            };

            restoredPath.slopeSurfaceMaterial = LoadPathMaterial(pd.slopeSurfaceMaterialPath);
            restoredPath.slopeWallMaterial = LoadPathMaterial(pd.slopeWallMaterialPath);
            restoredPath.stairRailMaterial = LoadPathMaterial(pd.stairRailMaterialPath);
            restoredPath.bridgeMaterial = LoadPathMaterial(pd.bridgeMaterialPath);
            restoredPath.trackMaterial = LoadPathMaterial(pd.trackMaterialPath);

            return restoredPath;
        }

        private Material LoadPathMaterial(string materialPath)
        {
            if (string.IsNullOrEmpty(materialPath))
                return null;

#if UNITY_EDITOR
            return AssetDatabase.LoadAssetAtPath<Material>(materialPath);
#else
            return LoadRuntimeMaterial(materialPath);
#endif
        }

        private static Material LoadRuntimeMaterial(string materialPath)
        {
            if (string.IsNullOrWhiteSpace(materialPath))
                return null;

            string materialName = System.IO.Path.GetFileNameWithoutExtension(materialPath);
            if (string.IsNullOrWhiteSpace(materialName))
                return null;

            return Resources.Load<Material>($"QuickTile/Material/{materialName}")
                ?? Resources.Load<Material>(materialName);
        }

        private static bool TilemapHasAnyTiles(Tilemap tilemap)
        {
            if (tilemap == null)
                return false;

            tilemap.CompressBounds();
            foreach (Vector3Int pos in tilemap.cellBounds.allPositionsWithin)
            {
                if (tilemap.GetTile(pos) != null)
                    return true;
            }

            return false;
        }

        private List<Tilemap> CollectTilemapsForSave()
        {
            var collected = new List<Tilemap>();
            var seen = new HashSet<Tilemap>();

            void AddTilemap(Tilemap tilemap)
            {
                if (tilemap == null)
                    return;

                if (seen.Add(tilemap))
                    collected.Add(tilemap);
            }

            AddTilemap(targetTilemap);

            if (tileRules != null)
            {
                foreach (var rule in tileRules)
                {
                    if (!ShouldPersistTileRule(rule) || !rule.useCustomTilemap || rule.customTargetTilemap == null)
                        continue;

                    rule.customTargetTilemapName = rule.customTargetTilemap.name;
                    AddTilemap(rule.customTargetTilemap);
                }
            }

            Transform root = targetTilemap != null
                ? (targetTilemap.transform.parent ?? targetTilemap.transform)
                : transform;

            foreach (var tilemap in root.GetComponentsInChildren<Tilemap>(true))
            {
                if (tilemap == null || seen.Contains(tilemap))
                    continue;

                // Preserve any legacy/orphan layer only if it still carries real tile data.
                if (TilemapHasAnyTiles(tilemap))
                    AddTilemap(tilemap);
            }

            return collected;
        }

        private static Vector3Int ApplyCellOffset(Vector3Int position, Vector3Int cellOffset)
        {
            return new Vector3Int(position.x + cellOffset.x, position.y + cellOffset.y, position.z);
        }

        private static PaintedTextureData ClonePaintedTextureData(PaintedTextureData source)
        {
            if (source == null)
                return null;

            return new PaintedTextureData
            {
                position = source.position,
                textureIndex = source.textureIndex
            };
        }

        private static List<PaintedTextureData> ClonePaintedTextures(List<PaintedTextureData> source)
        {
            var cloned = new List<PaintedTextureData>();
            if (source == null)
                return cloned;

            foreach (var entry in source)
            {
                var clone = ClonePaintedTextureData(entry);
                if (clone != null)
                    cloned.Add(clone);
            }

            return cloned;
        }

        private static LevelProperty CloneLevelProperty(LevelProperty source)
        {
            if (source == null)
                return null;

            return new LevelProperty
            {
                key = source.key,
                type = source.type,
                value = source.value,
                position = source.position,
                textureIndex = source.textureIndex
            };
        }

        private static List<LevelProperty> CloneLevelProperties(List<LevelProperty> source)
        {
            var cloned = new List<LevelProperty>();
            if (source == null)
                return cloned;

            foreach (var entry in source)
            {
                var clone = CloneLevelProperty(entry);
                if (clone != null)
                    cloned.Add(clone);
            }

            return cloned;
        }

        private static TrackPoint CloneTrackPoint(TrackPoint source)
        {
            if (source == null)
                return null;

            return new TrackPoint
            {
                gridPosition = source.gridPosition,
                snapToGround = source.snapToGround,
                rotation = source.rotation,
                width = source.width
            };
        }

        private Vector3Int CalculateSurfaceMassCellOffsetForSave()
        {
            float sumX = 0f;
            float sumY = 0f;
            int tileCount = 0;

            foreach (Tilemap tilemap in CollectTilemapsForSave())
            {
                if (tilemap == null)
                    continue;

                tilemap.CompressBounds();
                foreach (Vector3Int pos in tilemap.cellBounds.allPositionsWithin)
                {
                    if (tilemap.GetTile(pos) == null)
                        continue;

                    sumX += pos.x + 0.5f;
                    sumY += pos.y + 0.5f;
                    tileCount++;
                }
            }

            if (tileCount == 0)
                return Vector3Int.zero;

            return new Vector3Int(
                -Mathf.RoundToInt(sumX / tileCount),
                -Mathf.RoundToInt(sumY / tileCount),
                0);
        }

        private void ApplyCellOffsetToSaveData(SaveData saveData, Vector3Int cellOffset)
        {
            if (saveData == null || cellOffset == Vector3Int.zero)
                return;

            if (saveData.tilemaps != null)
            {
                foreach (var tilemapData in saveData.tilemaps)
                {
                    if (tilemapData?.tiles == null)
                        continue;

                    foreach (var tileInfo in tilemapData.tiles)
                    {
                        if (tileInfo == null)
                            continue;

                        tileInfo.x += cellOffset.x;
                        tileInfo.y += cellOffset.y;
                    }
                }
            }

            if (saveData.paintedTextures != null)
            {
                foreach (var paintedTexture in saveData.paintedTextures)
                {
                    if (paintedTexture == null)
                        continue;

                    paintedTexture.position = ApplyCellOffset(paintedTexture.position, cellOffset);
                }
            }

            if (saveData.placedObjects != null)
            {
                foreach (var placedObject in saveData.placedObjects)
                {
                    if (placedObject == null)
                        continue;

                    placedObject.position = ApplyCellOffset(placedObject.position, cellOffset);
                    placedObject.skirtAnchorCell = ApplyCellOffset(placedObject.skirtAnchorCell, cellOffset);
                }
            }

            if (saveData.paths != null)
            {
                foreach (var path in saveData.paths)
                {
                    if (path == null)
                        continue;

                    if (path.points != null)
                    {
                        for (int i = 0; i < path.points.Count; i++)
                            path.points[i] = ApplyCellOffset(path.points[i], cellOffset);
                    }

                    if (path.trackPoints != null)
                    {
                        foreach (var trackPoint in path.trackPoints)
                        {
                            if (trackPoint == null)
                                continue;

                            trackPoint.gridPosition = ApplyCellOffset(trackPoint.gridPosition, cellOffset);
                        }
                    }
                }
            }

            if (saveData.texturePaintRulesList != null)
            {
                foreach (var textureRuleData in saveData.texturePaintRulesList)
                {
                    if (textureRuleData?.vegetationEntries == null)
                        continue;

                    foreach (var vegetationEntry in textureRuleData.vegetationEntries)
                    {
                        if (vegetationEntry?.instances == null)
                            continue;

                        for (int i = 0; i < vegetationEntry.instances.Count; i++)
                        {
                            var instance = vegetationEntry.instances[i];
                            instance.position += new Vector3(cellOffset.x, 0f, cellOffset.y);
                            vegetationEntry.instances[i] = instance;
                        }
                    }
                }
            }

            if (saveData.levelProperties != null)
            {
                foreach (var property in saveData.levelProperties)
                {
                    if (property == null)
                        continue;

                    property.position = ApplyCellOffset(property.position, cellOffset);
                }
            }
        }

        private static bool ShouldApplyLegacySurfaceMassCentering(SaveData saveData)
        {
            return saveData != null &&
                   saveData.centerOriginToSurfaceMass &&
                   !saveData.positionsCenteredInCells;
        }

        private bool ShouldPersistTileRule(TileRule rule)
        {
            if (rule == null)
                return false;

            if (!rule.useCustomTilemap)
                return true;

            if (rule.customTargetTilemap == null)
                return false;

            return TilemapHasAnyTiles(rule.customTargetTilemap);
        }

        private void RemoveEmptyCustomTileRules()
        {
            if (tileRules == null || tileRules.Count == 0)
                return;

            tileRules.RemoveAll(rule => !ShouldPersistTileRule(rule));
            selectedTileRuleIndex = tileRules.Count > 0
                ? Mathf.Clamp(selectedTileRuleIndex, 0, tileRules.Count - 1)
                : -1;
        }

        private void DestroyLoadedOrphanEmptyTilemaps(Dictionary<string, Tilemap> tilemapsByName)
        {
            if (tilemapsByName == null || tilemapsByName.Count == 0)
                return;

            var referenced = new HashSet<Tilemap>();
            if (targetTilemap != null)
                referenced.Add(targetTilemap);

            if (tileRules != null)
            {
                foreach (var rule in tileRules)
                {
                    if (rule != null && rule.useCustomTilemap && rule.customTargetTilemap != null)
                        referenced.Add(rule.customTargetTilemap);
                }
            }

            var toDestroy = new List<Tilemap>();
            foreach (var tilemap in tilemapsByName.Values.Distinct())
            {
                if (tilemap == null || referenced.Contains(tilemap))
                    continue;

                if (!TilemapHasAnyTiles(tilemap))
                    toDestroy.Add(tilemap);
            }

            if (toDestroy.Count == 0)
                return;

            foreach (var tilemap in toDestroy)
            {
                if (heightTilemaps != null)
                {
                    foreach (var key in heightTilemaps.Where(kvp => kvp.Value == tilemap).Select(kvp => kvp.Key).ToList())
                        heightTilemaps.Remove(key);
                }

                if (tilemapsByName != null)
                {
                    foreach (var key in tilemapsByName.Where(kvp => kvp.Value == tilemap).Select(kvp => kvp.Key).ToList())
                        tilemapsByName.Remove(key);
                }

                SafeDestroy(tilemap.gameObject);
            }
        }

#if UNITY_EDITOR
        public string GetCurrentLevelSaveAssetPath()
        {
            if (levels == null || currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
                return null;

            return GetPreferredLevelSaveAssetPath(levels[currentLevelIndex]);
        }

        private string GetPreferredLevelSaveAssetPath(LevelData level)
        {
            if (level == null)
                return "Assets/BEKKOLOCO/QuickTile/Resources/Levels/DefaultLevel.bytes";

            if (level.jsonFile != null)
            {
                string existingAssetPath = AssetDatabase.GetAssetPath(level.jsonFile);
                if (!string.IsNullOrEmpty(existingAssetPath))
                    return existingAssetPath;
            }

            string cleanName = SanitizeLevelFileStem(level.levelName);
            string bytesPath = $"Assets/BEKKOLOCO/QuickTile/Resources/Levels/{cleanName}.bytes";
            string jsonPath = $"Assets/BEKKOLOCO/QuickTile/Resources/Levels/{cleanName}.json";

            if (AssetDatabase.LoadAssetAtPath<TextAsset>(bytesPath) != null)
                return bytesPath;
            if (AssetDatabase.LoadAssetAtPath<TextAsset>(jsonPath) != null)
                return jsonPath;

            return bytesPath;
        }

        private static string SanitizeLevelFileStem(string levelName)
        {
            if (string.IsNullOrEmpty(levelName))
                return "DefaultLevel";

            return levelName.Replace(".json", "").Replace(".bytes", "");
        }

        private static string ResolveSaveFileSystemPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return filePath;

            string normalized = filePath.Replace("\\", "/");
            if (System.IO.Path.IsPathRooted(normalized))
                return normalized;

            if (normalized.StartsWith("Assets/"))
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", normalized));

            return System.IO.Path.GetFullPath(normalized);
        }

        private static string ConvertToAssetPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return null;

            string normalized = filePath.Replace("\\", "/");
            if (normalized.StartsWith("Assets/"))
                return normalized;

            string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..")).Replace("\\", "/");
            string fullPath = System.IO.Path.GetFullPath(normalized).Replace("\\", "/");

            if (!fullPath.StartsWith(projectRoot + "/") && fullPath != projectRoot)
                return null;

            string relative = fullPath.Substring(projectRoot.Length).TrimStart('/');
            return relative.StartsWith("Assets/") ? relative : null;
        }

        private void RefreshSavedLevelAssetReference(string filePath)
        {
            string assetPath = ConvertToAssetPath(filePath);
            if (string.IsNullOrEmpty(assetPath))
                return;

            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
            if (levels != null && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
            {
                LevelData level = levels[currentLevelIndex];
                if (level != null)
                {
                    string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                    bool isAutosave = !string.IsNullOrEmpty(autoSavePrefix) && fileName.StartsWith(autoSavePrefix);

                    if (isAutosave)
                    {
                        // ⛑ Auto-save: do NOT overwrite level.jsonFile reference.
                        // Autosaves are recovery snapshots, not the primary level data.
                        // The user can explicitly restore via the autosave browser (📂).
                    }
                    else
                    {
                        // Manual save: update jsonFile reference and level name
                        level.jsonFile = asset;
                        level.levelName = fileName;
                    }
                }
            }

            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
#endif

        public TextAsset GetLevelJsonAsset(string levelName)
        {
#if UNITY_EDITOR
            return levels.Find(l => l.levelName == levelName)?.jsonFile;
#else
            return Resources.Load<TextAsset>($"Levels/{levelName}");
#endif
        }

        private IEnumerator DelayedReloadLevel()
        {
            yield return null;
            Dictionary<string, TileBase> tileDict = BuildTileDictionary();
            LoadLevel(currentLevelIndex, tileDict);
        }

        public void ReloadCurrentLevel()
        {
            if (levels == null || levels.Count == 0 || currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
                return;

#if UNITY_EDITOR
            // 🛟 Safety: always snapshot current in-memory state to autosaves before a destructive reload,
            // so the user can recover via the autosave browser if the reload was unintended.
            try { __QT_Auto_SafetySnapshotBeforeReload(); }
            catch (Exception ex) { Debug.LogWarning($"[QuickTile] Pre-reload safety snapshot failed: {ex.Message}"); }

            EditorUtility.DisplayProgressBar("Reloading Level",
                $"Reloading {levels[currentLevelIndex].levelName}", 0.2f);

            try
            {
#endif
#if UNITY_EDITOR
                RestorePrefabReferences();
#endif

                EraseAllGameObjects();

                Dictionary<string, TileBase> tileDict = BuildTileDictionary();
                bool previousForceReload = _forceLevelReload;
                _forceLevelReload = true;
                try { LoadLevel(currentLevelIndex, tileDict); }
                finally { _forceLevelReload = previousForceReload; }

                RebuildPaintMaskAndMaterials();
                FixChildScales();
                InstantiateGameObjects();
                RefreshAllRadialHillDeformerBindings();
                RebuildAllProceduralMeshes();
                InitializePathFollowersAfterLoad();

                StartCoroutine(DelayedRefreshAllSkirts());

#if UNITY_EDITOR
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error reloading level: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                SceneView.RepaintAll();
            }
#endif
        }

        public void NextLevel(Dictionary<string, TileBase> tileDict) => LoadLevel(currentLevelIndex + 1, tileDict);
        public void PreviousLevel(Dictionary<string, TileBase> tileDict) => LoadLevel(currentLevelIndex - 1, tileDict);

        public void LoadNextLevel()
        {
            int newIndex = Mathf.Clamp(currentLevelIndex + 1, 0, levels.Count - 1);
            if (newIndex != currentLevelIndex)
            {
                Dictionary<string, TileBase> tileDict = BuildTileDictionary();
                LoadLevel(newIndex, tileDict);
            }
        }

        public void LoadPreviousLevel()
        {
            int newIndex = Mathf.Clamp(currentLevelIndex - 1, 0, levels.Count - 1);
            if (newIndex != currentLevelIndex)
            {
                Dictionary<string, TileBase> tileDict = BuildTileDictionary();
                LoadLevel(newIndex, tileDict);
            }
        }

        // ---------------------------------------------------------------------
        // Runtime streaming from Resources/Levels/<name>.json
        // ---------------------------------------------------------------------
        public void LoadLevelByNameRuntime(string levelName)
        {
            StartCoroutine(LoadLevelByNameRuntimeCoroutine(levelName));
        }

        private IEnumerator LoadLevelByNameRuntimeCoroutine(string levelName)
        {
            runtimeLoadProgress = 0f;

            string cleanName = levelName.Replace(".bytes", "").Replace(".json", "");

           // FollowerObject.ResetRuntimeState();
            EraseAllGameObjects();
            ClearTexturePaintRuntimeState(clearRules: false);

            TextAsset jsonAsset = Resources.Load<TextAsset>($"Levels/{cleanName}");
            if (jsonAsset == null)
            {
                Debug.LogError($"⚠️ Level JSON not found in Resources/Levels: {cleanName}");
                yield break;
            }

            Dictionary<string, TileBase> tileDict = new Dictionary<string, TileBase>();
            foreach (TileBase t in Resources.LoadAll<TileBase>(""))
                if (!tileDict.ContainsKey(t.name)) tileDict.Add(t.name, t);

            if (levels.Count == 0)
                levels.Add(new LevelData { levelName = cleanName, jsonFile = jsonAsset, properties = new List<LevelProperty>() });

            currentLevelIndex = levels.FindIndex(l => l.levelName == cleanName);
            if (currentLevelIndex < 0)
            {
                levels.Add(new LevelData { levelName = cleanName, jsonFile = jsonAsset, properties = new List<LevelProperty>() });
                currentLevelIndex = levels.Count - 1;
            }

            yield return null;

            LevelData level = levels[currentLevelIndex];
            var saveData = JsonUtility.FromJson<SaveData>(jsonAsset.text);

            centerOriginToSurfaceMass = saveData.centerOriginToSurfaceMass;

            Transform gridParent = targetTilemap.transform.parent ?? targetTilemap.transform;
            for (int i = gridParent.childCount - 1; i >= 0; i--)
                Destroy(gridParent.GetChild(i).gameObject);

            GameObject tmGO = new GameObject("TargetTilemap");
            tmGO.transform.SetParent(gridParent, false);
            targetTilemap = tmGO.AddComponent<Tilemap>();
            tmGO.AddComponent<TilemapRenderer>();

            heightTilemaps.Clear();
            placedObjects.Clear();
            paths.Clear();

            yield return null;

            tileRules.Clear();
            foreach (TileRuleData rd in saveData.tileRules)
            {
                var newRule = new TileRule
                {
                    ruleName = rd.ruleName ?? "",
                    tile = tileDict.TryGetValue(rd.tileName, out var tb) ? tb : null,
                    color = rd.color,
                    isVisible = rd.isVisible,
                    roundedCorner = rd.roundedCorner,
                    yOffset = rd.yOffset,
                    useCustomTilemap = rd.useCustomTilemap,
                    isDigLayer = rd.isDigLayer,
                    isDiggable = rd.isDiggable,
                    isUndiggable = rd.isUndiggable,
                    renderOrder = rd.renderOrder,
                    customTargetTilemapName = rd.customTargetTilemapName ?? "",
                    fixBase = rd.fixBase,
                    sizeY = rd.sizeY,
                    savedDeformerHandles = (rd.savedDeformerHandles != null)
                        ? new List<DeformerHandleData>(rd.savedDeformerHandles)
                        : new List<DeformerHandleData>(),
                    enableMove = rd.enableMove,
                    moveOffset = rd.moveOffset,
                    movePause = rd.movePause,
                    meshMode = rd.meshMode,
                };
                newRule.bottom.enabled = rd.bottomEnabled;
                newRule.bottom.shape = System.Enum.IsDefined(typeof(QuickTilemapEditor.BottomShape), rd.bottomShape)
                    ? (QuickTilemapEditor.BottomShape)rd.bottomShape
                    : QuickTilemapEditor.BottomShape.Rounded;
                newRule.bottom.size = Mathf.Clamp(rd.bottomSize, 1, 20);
                if (rd.bottomProfile != null && rd.bottomProfile.keys != null && rd.bottomProfile.keys.Length > 0)
                {
                    newRule.bottom.profile = new AnimationCurve(rd.bottomProfile.keys)
                    {
                        preWrapMode = rd.bottomProfile.preWrapMode,
                        postWrapMode = rd.bottomProfile.postWrapMode
                    };
                }
                else
                {
                    newRule.bottom.profile = AnimationCurve.EaseInOut(0, 1, 1, 0);
                }
                // Restore procedural settings
                if (newRule.proceduralSettings == null)
                    newRule.proceduralSettings = new ProceduralTileMeshGenerator.ProceduralMeshSettings();
                newRule.proceduralSettings.radius = rd.proceduralRadius;
                newRule.proceduralSettings.depth = rd.proceduralDepth;
                newRule.proceduralSettings.curveSegments = rd.proceduralCurveSegments;
                newRule.proceduralSettings.skirtEnabled = rd.proceduralSkirtEnabled;
                newRule.proceduralSettings.skirtWidth = rd.proceduralSkirtWidth;
                newRule.proceduralSettings.skirtHeight = rd.proceduralSkirtHeight;
                newRule.proceduralSettings.skirtSegments = rd.proceduralSkirtSegments;
                newRule.proceduralSettings.skirtUVScale = rd.proceduralSkirtUVScale;
                newRule.proceduralSettings.skirtUVOffsetY = rd.proceduralSkirtUVOffsetY;
                newRule.proceduralSettings.skirtMaterialMode = (SkirtMaterialMode)rd.proceduralSkirtMaterialMode;
                newRule.proceduralSettings.skirtMaskCurve =
                    rd.proceduralSkirtMaskCurve != null && rd.proceduralSkirtMaskCurve.keys != null && rd.proceduralSkirtMaskCurve.length > 0
                        ? new AnimationCurve(rd.proceduralSkirtMaskCurve.keys)
                        {
                            preWrapMode = rd.proceduralSkirtMaskCurve.preWrapMode,
                            postWrapMode = rd.proceduralSkirtMaskCurve.postWrapMode
                        }
                        : ProceduralTileMeshGenerator.CreateDefaultSkirtMaskCurve();
                // Bottom cap settings
                newRule.proceduralSettings.bottomMode = (BottomMode)rd.proceduralBottomMode;
                newRule.proceduralSettings.bottomBevelInset = rd.proceduralBottomBevelInset;
                newRule.proceduralSettings.bottomBevelDepth = rd.proceduralBottomBevelDepth;
                newRule.proceduralSettings.bottomBevelSegments = rd.proceduralBottomBevelSegments;
                newRule.proceduralSettings.bottomBevelProfile = (BevelProfile)rd.proceduralBottomBevelProfile;
                newRule.proceduralSettings.bottomNoiseScale = rd.proceduralBottomNoiseScale;
                newRule.proceduralSettings.bottomNoiseAmplitude = rd.proceduralBottomNoiseAmplitude;
                newRule.proceduralSettings.bottomIslandSharpness = rd.proceduralBottomIslandSharpness;
                newRule.proceduralSettings.bottomIslandSmooth = rd.proceduralBottomIslandSmooth;
                newRule.proceduralSettings.bottomNoiseResolution = rd.proceduralBottomNoiseResolution;
                newRule.proceduralSettings.bottomNoiseSeed = rd.proceduralBottomNoiseSeed;
#if UNITY_EDITOR
                if (!string.IsNullOrEmpty(rd.bottomMaterialPath))
                    newRule.bottom.material = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rd.bottomMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralFloorMaterialPath))
                    newRule.proceduralFloorMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rd.proceduralFloorMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralWallMaterialPath))
                    newRule.proceduralWallMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rd.proceduralWallMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralCeilingMaterialPath))
                    newRule.proceduralCeilingMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rd.proceduralCeilingMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralBottomMaterialPath))
                    newRule.proceduralBottomMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rd.proceduralBottomMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralDigMaterialPath))
                    newRule.proceduralDigMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(rd.proceduralDigMaterialPath);
                ApplyDefaultProceduralMaterials(newRule);
#else
                if (!string.IsNullOrEmpty(rd.bottomMaterialPath))
                    newRule.bottom.material = LoadRuntimeMaterial(rd.bottomMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralFloorMaterialPath))
                    newRule.proceduralFloorMaterial = LoadRuntimeMaterial(rd.proceduralFloorMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralWallMaterialPath))
                    newRule.proceduralWallMaterial = LoadRuntimeMaterial(rd.proceduralWallMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralCeilingMaterialPath))
                    newRule.proceduralCeilingMaterial = LoadRuntimeMaterial(rd.proceduralCeilingMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralBottomMaterialPath))
                    newRule.proceduralBottomMaterial = LoadRuntimeMaterial(rd.proceduralBottomMaterialPath);
                if (!string.IsNullOrEmpty(rd.proceduralDigMaterialPath))
                    newRule.proceduralDigMaterial = LoadRuntimeMaterial(rd.proceduralDigMaterialPath);
                ApplyDefaultProceduralMaterials(newRule);
#endif
                tileRules.Add(newRule);
            }
            selectedTileRuleIndex = tileRules.Count > 0 ? 0 : -1;

            gameObjectRules = saveData.gameObjectRules;

#if UNITY_EDITOR
            foreach (var rule in gameObjectRules)
                if (rule.prefab == null && !string.IsNullOrEmpty(rule.prefabResourcePath))
                    rule.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(rule.prefabResourcePath);
#endif

            if (saveData.paths != null)
            {
                foreach (PathData pd in saveData.paths)
                {
                    Path newPath = RestorePathFromData(pd);
                    if (newPath != null)
                        paths.Add(newPath);
                }
            }

            level.paintedTextures.Clear();
            if (saveData.paintedTextures != null)
                level.paintedTextures.AddRange(saveData.paintedTextures);

            yield return null;

            const float TILE_SEGMENT = 0.60f;
            const float OBJECT_SEGMENT = 0.25f;
            const float TEX_SEGMENT = 0.15f;

            var tilemapsByName = new Dictionary<string, Tilemap>();

            foreach (TilemapData tmData in saveData.tilemaps)
            {
                Tilemap targetMap;
                if (tmData.tilemapName == targetTilemap.name)
                    targetMap = targetTilemap;
                else
                {
                    GameObject newTM = new GameObject(tmData.tilemapName);
                    newTM.transform.SetParent(gridParent, false);
                    newTM.transform.localPosition = new Vector3(0, tmData.height, 0);
                    targetMap = newTM.AddComponent<Tilemap>();
                    newTM.AddComponent<TilemapRenderer>();
                }

                targetMap.ClearAllTiles();
                targetMap.transform.localPosition = new Vector3(0, tmData.height, 0);
                targetMap.transform.localRotation = Quaternion.identity;

                int tileCounter = 0;
                int totalTiles = tmData.tiles.Count;
                foreach (TileInfo info in tmData.tiles)
                {
                    if (tileDict.TryGetValue(info.tileName, out TileBase tile))
                    {
                        Vector3Int pos = new Vector3Int(info.x, info.y, info.z);
                        targetMap.SetTile(pos, tile);
                        targetMap.SetColor(pos, info.color);
                        targetMap.SetTileFlags(pos, TileFlags.None);
                    }
                    tileCounter++;
                    if (tileCounter % 1000 == 0)
                    {
                        runtimeLoadProgress = TILE_SEGMENT * tileCounter / totalTiles;
                        yield return null;
                    }
                }

                // Indexation pendant que targetMap est en scope
                heightTilemaps[tmData.height] = targetMap;
                tilemapsByName[tmData.tilemapName] = targetMap;
            }

            // ===== ASSIGNATION DES TILEMAPS AUX RÈGLES =====
            for (int i = 0; i < tileRules.Count; i++)
            {
                var rule = tileRules[i];

                if (!rule.useCustomTilemap)
                {
                    rule.customTargetTilemap = targetTilemap;
                    continue;
                }

                bool assigned = false;

                // a) nom explicite sauvegardé
                if (!string.IsNullOrEmpty(rule.customTargetTilemapName) &&
                    tilemapsByName.TryGetValue(rule.customTargetTilemapName, out var explicitMap))
                {
                    rule.customTargetTilemap = explicitMap;
                    rule.yOffset = explicitMap.transform.localPosition.y;
                    assigned = true;
                }

                // b) pattern attendu "Tilemap_Rule_{tile}_{yOffset}"
                if (!assigned)
                {
                    string expectedName = $"Tilemap_Rule_{(rule.tile ? rule.tile.name : "NewRule")}_{rule.yOffset}";
                    if (tilemapsByName.TryGetValue(expectedName, out var expectedMap))
                    {
                        rule.customTargetTilemap = expectedMap;
                        rule.yOffset = expectedMap.transform.localPosition.y;
                        assigned = true;
                    }
                }

                // c) fallback par hauteur exacte
                if (!assigned)
                {
                    foreach (var kvp in heightTilemaps)
                    {
                        if (Mathf.Abs(rule.yOffset - kvp.Key) < 0.01f)
                        {
                            rule.customTargetTilemap = kvp.Value;
                            rule.yOffset = kvp.Key;
                            assigned = true;
                            break;
                        }
                    }
                }

                // d) dernier recours : trouver une map qui contient ce tile
                if (!assigned)
                {
                    foreach (var kvp in tilemapsByName)
                    {
                        var candidate = kvp.Value;
                        var bounds = candidate.cellBounds;
                        bool found = false;
                        foreach (var pos in bounds.allPositionsWithin)
                        {
                            var t = candidate.GetTile(pos);
                            if (t != null && rule.tile != null && t.name == rule.tile.name)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (found)
                        {
                            rule.customTargetTilemap = candidate;
                            rule.yOffset = candidate.transform.localPosition.y;
                            assigned = true;
                            break;
                        }
                    }
                }

                // e) sinon, créer une tilemap dédiée
                if (!assigned)
                {
                    rule.customTargetTilemap = CreateTilemapForRule(rule);
                    if (rule.customTargetTilemap != null)
                    {
                        heightTilemaps[rule.yOffset] = rule.customTargetTilemap;
                        tilemapsByName[rule.customTargetTilemap.name] = rule.customTargetTilemap;
                    }
                }

                // Mise à jour du tri visuel
                if (rule.customTargetTilemap != null)
                {
                    rule.renderOrder = Mathf.RoundToInt(rule.yOffset * 100f);
                    var r = rule.customTargetTilemap.GetComponent<TilemapRenderer>();
                    if (r) r.sortingOrder = rule.renderOrder;
                }
            }

            RemoveEmptyCustomTileRules();
            DestroyLoadedOrphanEmptyTilemaps(tilemapsByName);

            yield return null; // Laisser le temps aux GameObjects de s'initialiser


            runtimeLoadProgress = TILE_SEGMENT;
            yield return null;

            // ===== OBJETS PLACÉS =====
            int objCounter = 0;
            int totalObjs = saveData.placedObjects.Count;
            foreach (GameObjectData god in saveData.placedObjects)
            {
                var placed = new PlacedObject
                {
                    position = god.position,
                    ruleIndex = god.ruleIndex,
                    color = god.color,
                    pathIndex = god.pathIndex,
                    rotation = god.rotation,
                    parentTilemapName = god.parentTilemapName,
                    prefabResourcePath = god.prefabPath,
                    placementSurface = god.placementSurface,
                    skirtAnchorCell = god.skirtAnchorCell
                };
                placed.instanceYOffset = god.instanceYOffset;
                if (god.instanceYOffsetVersion < PlacedObject.CurrentInstanceYOffsetVersion)
                    placed.MarkInstanceYOffsetLegacy();
                else
                    placed.MarkInstanceYOffsetUpgraded();
                placedObjects.Add(placed);

                objCounter++;
                if (objCounter % 50 == 0)
                {
                    runtimeLoadProgress = TILE_SEGMENT + OBJECT_SEGMENT * objCounter / totalObjs;
                    yield return null;
                }
            }

            foreach (var rule in gameObjectRules)
                rule.instanceOffsets.Clear();

            EnsureDeformerComponentsForLoad();
            RestoreDeformerHandlesAfterLoad();
            RefreshAllRadialHillDeformerBindings();
            ApplyDeformerSettingsAfterLoad(saveData.tileRules);
            RebuildAllProceduralMeshes();
            InstantiateGameObjects();
            runtimeLoadProgress = TILE_SEGMENT + OBJECT_SEGMENT;
            yield return null;

            // ===== TEXTURES PEINTES =====
            int texCounter = 0;
            int totalTex = level.paintedTextures.Count;
            foreach (var pt in level.paintedTextures)
            {
                if (pt.textureIndex >= 0 && pt.textureIndex < texturePaintRules.Count)
                    PaintTextureCell(pt.position, pt.textureIndex);

                texCounter++;
                if (texCounter % 100 == 0)
                {
                    runtimeLoadProgress = TILE_SEGMENT + OBJECT_SEGMENT + TEX_SEGMENT * texCounter / totalTex;
                    yield return null;
                }
            }

            RebuildPaintMaskAndMaterials();
            yield return null;

            // ===== FINALISATION =====
            InitializePathFollowersAfterLoad();
            FixChildScales();
            StartCoroutine(DelayedRefreshAllSkirts());
            yield return null;

            EnsureNavMeshSurface();
            // _navSurface.BuildNavMesh();
            StartCoroutine(DelayedNavMeshBuild(0.5f));

            if (ShouldApplyLegacySurfaceMassCentering(saveData))
                CenterLevelToSurfaceMassXZ(targetTilemap.transform.parent);
            else
            {
                if (targetTilemap.transform.parent != null)
                    targetTilemap.transform.parent.position = new Vector3(0, targetTilemap.transform.parent.position.y, 0);
            }

            runtimeLoadProgress = 1f;
        }

        // ---------------------------------------------------------------------
        // Save JSON to disk (Editor)
        // ---------------------------------------------------------------------
        public void SaveTilemapToJson(string filePath)
        {
            FixPathIndicesBeforeSaving();

            if (targetTilemap == null) return;

            if (levels == null || levels.Count == 0)
            {
#if UNITY_EDITOR
                EditorUtility.DisplayDialog("No Level Defined", "Please create a level before saving.", "OK");
#endif
                Debug.LogError("No levels defined. Create a level before saving.");
                return;
            }

            if (currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
            {
#if UNITY_EDITOR
                EditorUtility.DisplayDialog("Invalid Level Selection", "No valid level is selected. Please select one before saving.", "OK");
#endif
                Debug.LogError("Invalid level index. No level is selected.");
                return;
            }

#if UNITY_EDITOR
            ApplyPendingTexturePaintDraftsFromActiveInspectors();
#endif

            // Resync scene → placedObjects (source of truth)
            // (only in editor; harmless in player)
            ResyncEditorStateFromScene();
            bool upgradedOffsetsForSave = UpgradeLegacyInstanceYOffsets();
#if UNITY_EDITOR
            if (upgradedOffsetsForSave)
                EditorUtility.SetDirty(this);
#endif

            LevelData level = levels[currentLevelIndex];
            Vector3Int bakedCenterOffset = Vector3Int.zero;
            bool bakeCenteredPositionsOnSave = centerOriginToSurfaceMass;
            bool shouldReloadToApplyBakedCentering = false;

            // Painted textures snapshot
            level.paintedTextures ??= new List<PaintedTextureData>();
            level.paintedTextures.Clear();

            foreach (var entry in texturePaintMask)
            {
                level.paintedTextures.Add(new PaintedTextureData
                {
                    position = entry.Key,
                    textureIndex = entry.Value
                });
            }

            // Aggregate all tilemaps in hierarchy
            List<TilemapData> allTilemapsData = new List<TilemapData>();
            foreach (Tilemap tm in CollectTilemapsForSave())
            {
                if (tm == null) continue;

                tm.CompressBounds();

                TilemapData data = new TilemapData
                {
                    tilemapName = tm.name,
                    height = tm.transform.localPosition.y
                };

                foreach (Vector3Int pos in tm.cellBounds.allPositionsWithin)
                {
                    TileBase tile = tm.GetTile(pos);
                    if (tile != null)
                    {
                        data.tiles.Add(new TileInfo(pos.x, pos.y, pos.z, tile.name)
                        {
                            color = tm.GetColor(pos)
                        });
                    }
                }

                allTilemapsData.Add(data);
            }

#if UNITY_EDITOR
            foreach (var rule in gameObjectRules)
            {
                if (rule.prefab != null)
                {
                    string assetPath = AssetDatabase.GetAssetPath(rule.prefab);
                    if (!string.IsNullOrEmpty(assetPath))
                        rule.prefabResourcePath = assetPath;
                    else
                        Debug.LogWarning($"⚠️ Could not get asset path for prefab: {rule.prefab.name}");
                }
            }
#endif

            List<GameObjectData> placedObjectsData = new List<GameObjectData>();

            foreach (var po in placedObjects)
            {
                if (po.ruleIndex >= 0 && po.ruleIndex < gameObjectRules.Count)
                {
                    var rule = gameObjectRules[po.ruleIndex];

                    if (string.IsNullOrEmpty(rule.prefabResourcePath) && rule.prefab != null)
                    {
#if UNITY_EDITOR
                        rule.prefabResourcePath = AssetDatabase.GetAssetPath(rule.prefab);
#else
                        rule.prefabResourcePath = rule.prefab.name;
#endif
                    }

                    placedObjectsData.Add(new GameObjectData
                    {
                        position = po.position,
                        ruleIndex = po.ruleIndex,
                        color = po.color,
                        pathIndex = po.pathIndex,
                        rotation = po.rotation,
                        parentTilemapName = po.parentTilemapName ?? targetTilemap.name,
                        instanceYOffset = po.instanceYOffset,
                        instanceYOffsetVersion = PlacedObject.CurrentInstanceYOffsetVersion,
                        prefabPath = rule.prefabResourcePath,
                        placementSurface = po.placementSurface,
                        skirtAnchorCell = po.skirtAnchorCell
                    });
                }
            }

            CaptureDeformerHandlesForSave();

            // ── Snapshot texture paint rules into serializable data ──
            var texturePaintRulesData = new List<TexturePaintRuleData>();
#if UNITY_EDITOR
            texturePaintRulesData = BuildTexturePaintRulesDataSnapshot();
#endif

            var serializableTileRules = tileRules
                .Where(ShouldPersistTileRule)
                .ToList();

            SaveData saveData = new SaveData
            {
                tilemaps = allTilemapsData,
                paintedTextures = ClonePaintedTextures(level.paintedTextures),
                texturePaintRulesList = texturePaintRulesData,
                placedObjects = placedObjectsData,
                tileRules = serializableTileRules.Select(rule => new TileRuleData
                {
                    ruleName = rule.ruleName ?? "",
                    tileName = rule.tile != null ? rule.tile.name : "",
                    color = rule.color,
                    isVisible = rule.isVisible,
                    roundedCorner = rule.roundedCorner,
                    yOffset = rule.yOffset,
                    useCustomTilemap = rule.useCustomTilemap,
                    isDigLayer = rule.isDigLayer,
                    isDiggable = rule.isDiggable,
                    isUndiggable = rule.isUndiggable,
                    customTargetTilemapName = rule.useCustomTilemap && rule.customTargetTilemap != null
                        ? rule.customTargetTilemap.name : "",
                    renderOrder = rule.renderOrder,
                    sizeY = rule.sizeY,
                    fixBase = rule.fixBase,
                    savedDeformerHandles = (rule.savedDeformerHandles != null)
                        ? new List<DeformerHandleData>(rule.savedDeformerHandles)
                        : new List<DeformerHandleData>(),
                    enableMove = rule.enableMove,
                    moveOffset = rule.moveOffset,
                    movePause = rule.movePause,
                    bottomEnabled = rule.bottom != null ? rule.bottom.enabled : true,
                    bottomShape = rule.bottom != null ? (int)rule.bottom.shape : (int)QuickTilemapEditor.BottomShape.Rounded,
                    bottomSize = rule.bottom != null ? rule.bottom.size : 8,
                    bottomProfile = rule.bottom != null && rule.bottom.profile != null
                        ? new AnimationCurve(rule.bottom.profile.keys)
                        {
                            preWrapMode = rule.bottom.profile.preWrapMode,
                            postWrapMode = rule.bottom.profile.postWrapMode
                        }
                        : AnimationCurve.EaseInOut(0, 1, 1, 0),
#if UNITY_EDITOR
                    bottomMaterialPath = rule.bottom != null && rule.bottom.material != null
                        ? UnityEditor.AssetDatabase.GetAssetPath(rule.bottom.material)
                        : "",
#endif
                    // ── Procedural mesh mode ──
                    meshMode = rule.meshMode,
                    proceduralRadius = rule.proceduralSettings != null ? rule.proceduralSettings.radius : 0.3f,
                    proceduralDepth = rule.proceduralSettings != null ? rule.proceduralSettings.depth : 0.4f,
                    proceduralCurveSegments = rule.proceduralSettings != null ? rule.proceduralSettings.curveSegments : 8,
                    proceduralSkirtEnabled = rule.proceduralSettings != null ? rule.proceduralSettings.skirtEnabled : true,
                    proceduralSkirtWidth = rule.proceduralSettings != null ? rule.proceduralSettings.skirtWidth : 0.155f,
                    proceduralSkirtHeight = rule.proceduralSettings != null ? rule.proceduralSettings.skirtHeight : 0.485f,
                    proceduralSkirtSegments = rule.proceduralSettings != null ? rule.proceduralSettings.skirtSegments : 2,
                    proceduralSkirtUVScale = rule.proceduralSettings != null ? rule.proceduralSettings.skirtUVScale : 1f,
                    proceduralSkirtUVOffsetY = rule.proceduralSettings != null ? rule.proceduralSettings.skirtUVOffsetY : 0.389f,
                    proceduralSkirtMaterialMode = rule.proceduralSettings != null ? (int)rule.proceduralSettings.skirtMaterialMode : 0,
                    proceduralSkirtMaskCurve = rule.proceduralSettings != null && rule.proceduralSettings.skirtMaskCurve != null && rule.proceduralSettings.skirtMaskCurve.length > 0
                        ? new AnimationCurve(rule.proceduralSettings.skirtMaskCurve.keys)
                        {
                            preWrapMode = rule.proceduralSettings.skirtMaskCurve.preWrapMode,
                            postWrapMode = rule.proceduralSettings.skirtMaskCurve.postWrapMode
                        }
                        : ProceduralTileMeshGenerator.CreateDefaultSkirtMaskCurve(),
#if UNITY_EDITOR
                    proceduralFloorMaterialPath = rule.proceduralFloorMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(rule.proceduralFloorMaterial) : "",
                    proceduralWallMaterialPath = rule.proceduralWallMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(rule.proceduralWallMaterial) : "",
                    proceduralCeilingMaterialPath = rule.proceduralCeilingMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(rule.proceduralCeilingMaterial) : "",
                    proceduralBottomMaterialPath = rule.proceduralBottomMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(rule.proceduralBottomMaterial) : "",
                    proceduralDigMaterialPath = rule.proceduralDigMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(rule.proceduralDigMaterial) : "",
#endif
                    // ── Bottom cap settings ──
                    proceduralBottomMode = rule.proceduralSettings != null ? (int)rule.proceduralSettings.bottomMode : 0,
                    proceduralBottomBevelInset = rule.proceduralSettings != null ? rule.proceduralSettings.bottomBevelInset : 0.1f,
                    proceduralBottomBevelDepth = rule.proceduralSettings != null ? rule.proceduralSettings.bottomBevelDepth : 0.08f,
                    proceduralBottomBevelSegments = rule.proceduralSettings != null ? rule.proceduralSettings.bottomBevelSegments : 4,
                    proceduralBottomBevelProfile = rule.proceduralSettings != null ? (int)rule.proceduralSettings.bottomBevelProfile : 0,
                    proceduralBottomNoiseScale = rule.proceduralSettings != null ? rule.proceduralSettings.bottomNoiseScale : 2f,
                    proceduralBottomNoiseAmplitude = rule.proceduralSettings != null ? rule.proceduralSettings.bottomNoiseAmplitude : 0.3f,
                    proceduralBottomIslandSharpness = rule.proceduralSettings != null ? rule.proceduralSettings.bottomIslandSharpness : 2f,
                    proceduralBottomIslandSmooth = rule.proceduralSettings != null ? rule.proceduralSettings.bottomIslandSmooth : 0.3f,
                    proceduralBottomNoiseResolution = rule.proceduralSettings != null ? rule.proceduralSettings.bottomNoiseResolution : 8,
                    proceduralBottomNoiseSeed = rule.proceduralSettings != null ? rule.proceduralSettings.bottomNoiseSeed : 0f,
                    // ── Deformer settings ──
                    deformerShape = GetDeformerSettingForRule(rule, d => (int)d.shape, 0),
                    deformerRadius = GetDeformerSettingForRule(rule, d => d.radius, 5f),
                    deformerFalloff = GetDeformerSettingForRule(rule, d => (int)d.falloff, 2),
                    deformerGaussianSharpness = GetDeformerSettingForRule(rule, d => d.gaussianSharpness, 1.5f),
                    deformerHeightPerUnitY = GetDeformerSettingForRule(rule, d => d.heightPerUnitY, 1f),
                    deformerYDeformRatio = GetDeformerSettingForRule(rule, d => d.yDeformRatio, 1f),
                    deformerInvertDirection = GetDeformerSettingForRule(rule, d => d.invertDirection, false),
                    deformerLinkRadiusToScale = GetDeformerSettingForRule(rule, d => d.linkRadiusToScale, false),
                    deformerRadiusLinkMode = GetDeformerSettingForRule(rule, d => (int)d.radiusLinkMode, 0),
                    deformerUseYMin = GetDeformerSettingForRule(rule, d => d.useYMin, false),
                    deformerYMin = GetDeformerSettingForRule(rule, d => d.yMin, 0f),
                    deformerYMinRelativeToHandle = GetDeformerSettingForRule(rule, d => d.yMinRelativeToHandle, false),
                    deformerYFeather = GetDeformerSettingForRule(rule, d => d.yFeather, 0f),
                    deformerClampWorldMinY = GetDeformerSettingForRule(rule, d => d.clampWorldMinY, false),
                    deformerWorldMinY = GetDeformerSettingForRule(rule, d => d.worldMinY, 0f),
                    deformerYMinFalloff = GetDeformerSettingForRule(rule, d => (int)d.yMinFalloff, 2),
                    deformerYMinGaussianSharpness = GetDeformerSettingForRule(rule, d => d.yMinGaussianSharpness, 1.5f),
                }).ToList(),
                paths = paths.Select(path => new PathData
                {
                    points = path.points != null ? new List<Vector3Int>(path.points) : new List<Vector3Int>(),
                    color = path.color,
                    isVisible = path.isVisible,
                    pathType = path.pathType,
                    stairSteps = path.stairSteps,
                    stairAutoSteps = path.stairAutoSteps,
                    stairStepDepth = path.stairStepDepth,
                    slopeWidth = path.slopeWidth,
                    smoothTransition = path.smoothTransition,
                    slopeSideSkirtEnabled = path.slopeSideSkirtEnabled,
                    slopeSideSkirtWidth = path.slopeSideSkirtWidth,
                    slopeSideSkirtHeight = path.slopeSideSkirtHeight,
                    slopeSideSkirtSegments = path.slopeSideSkirtSegments,
                    slopeSideSkirtUVScale = path.slopeSideSkirtUVScale,
                    slopeSideSkirtUVOffsetY = path.slopeSideSkirtUVOffsetY,
                    bridgeHeight = path.bridgeHeight,
                    bridgeWidth = path.bridgeWidth,
                    bridgeProfile = path.bridgeProfile,
                    bridgeCurve = path.bridgeCurve,
                    bridgeSteps = path.bridgeSteps,
                    bridgeRailings = path.bridgeRailings,
                    bridgeRailThickness = path.bridgeRailThickness,
                    bridgeRailSpread = path.bridgeRailSpread,
                    bridgeRailEndExtension = path.bridgeRailEndExtension,
                    bridgeRailYOffset = path.bridgeRailYOffset,
                    bridgeRailUvOffsetY = path.bridgeRailUvOffsetY,
                    bridgeRailCurveFollow = path.bridgeRailCurveFollow,
                    enableTrackMesh = path.enableTrackMesh,
                    trackPoints = path.trackPoints != null
                        ? path.trackPoints.Select(CloneTrackPoint).Where(trackPoint => trackPoint != null).ToList()
                        : new List<TrackPoint>(),
                    trackWidth = path.trackWidth,
                    trackSubdivisions = path.trackSubdivisions,
                    trackUVTilingY = path.trackUVTilingY,
#if UNITY_EDITOR
                    slopeSurfaceMaterialPath = path.slopeSurfaceMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(path.slopeSurfaceMaterial) : "",
                    slopeWallMaterialPath = path.slopeWallMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(path.slopeWallMaterial) : "",
                    stairRailMaterialPath = path.stairRailMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(path.stairRailMaterial) : "",
                    bridgeMaterialPath = path.bridgeMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(path.bridgeMaterial) : "",
                    trackMaterialPath = path.trackMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(path.trackMaterial) : "",
#endif
                }).ToList(),
                levelProperties = CloneLevelProperties(level.properties),
                gameObjectRules = gameObjectRules,
                centerOriginToSurfaceMass = centerOriginToSurfaceMass,
                positionsCenteredInCells = false
            };

            if (bakeCenteredPositionsOnSave)
            {
                bakedCenterOffset = CalculateSurfaceMassCellOffsetForSave();
                ApplyCellOffsetToSaveData(saveData, bakedCenterOffset);
                saveData.positionsCenteredInCells = true;

                Transform levelContainer = targetTilemap != null ? targetTilemap.transform.parent : null;
                bool containerHasOffset = levelContainer != null &&
                                          (!Mathf.Approximately(levelContainer.position.x, 0f) ||
                                           !Mathf.Approximately(levelContainer.position.z, 0f));

                shouldReloadToApplyBakedCentering = bakedCenterOffset != Vector3Int.zero || containerHasOffset;
            }

#if UNITY_EDITOR
            if (currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
            {
                var currentLevel = levels[currentLevelIndex];
                if (currentLevel != null)
                    StoreSessionTexturePaintRulesForLevel(currentLevel, texturePaintRulesData);
            }
#endif


            string json = JsonUtility.ToJson(saveData, true);

#if UNITY_EDITOR
            string resolvedFilePath = ResolveSaveFileSystemPath(filePath);
#else
            string resolvedFilePath = filePath;
#endif

            string directory = System.IO.Path.GetDirectoryName(resolvedFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(resolvedFilePath, json);

#if UNITY_EDITOR
            RefreshSavedLevelAssetReference(resolvedFilePath);
#endif

            if (level.centerOriginToSurfaceMass != centerOriginToSurfaceMass)
            {
                level.centerOriginToSurfaceMass = centerOriginToSurfaceMass;
                shouldReloadToApplyBakedCentering = true;
            }

            if (shouldReloadToApplyBakedCentering && !_suppressReloadAfterSave)
                ReloadCurrentLevel();
        }

#if UNITY_EDITOR
        private void ApplyPendingTexturePaintDraftsFromActiveInspectors()
        {
            try
            {
                var inspectorType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("Bekkoloco.QuickTilemapEditorInspector", false))
                    .FirstOrDefault(type => type != null);

                var applyMethod = inspectorType?.GetMethod(
                    "ApplyPendingTexturePaintDraftsFor",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                applyMethod?.Invoke(null, new object[] { this });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuickTile] Failed to apply pending texture drafts before save: {ex.Message}");
            }
        }
#endif

        // ---------------------------------------------------------------------
        // Debug helper
        // ---------------------------------------------------------------------
        public void DebugTilemapAssignments()
        {
            for (int i = 0; i < tileRules.Count; i++)
            {
                var rule = tileRules[i];
                if (rule.useCustomTilemap && rule.customTargetTilemap == null)
                {
                    Debug.LogError($"❌ ERROR: Rule {i} uses custom tilemap but has no tilemap assigned!");
                }
            }
        }

        // =====================================================================
        //                      EXTRA: Missing methods (added)
        // =====================================================================

        // Clears all tiles, layers, placed objects and paths
        public void ClearCurrentLevel()
        {
            // 1) Clear base tilemap
            if (targetTilemap != null)
            {
#if UNITY_EDITOR
                Undo.RecordObject(targetTilemap, "Clear Base Tilemap");
#endif
                targetTilemap.ClearAllTiles();
            }

            // 2) Destroy any custom height tilemaps
            foreach (var tm in heightTilemaps.Values)
            {
                if (tm != null)
                {
#if UNITY_EDITOR
                    Undo.DestroyObjectImmediate(tm.gameObject);
#else
                    Destroy(tm.gameObject);
#endif
                }
            }
            heightTilemaps.Clear();

            // 3) Remove all placed prefabs & path data
#if UNITY_EDITOR
            Undo.RecordObject(this, "Clear Placed Objects & Paths");
#endif
            EraseAllGameObjects();
            paths.Clear();
            ClearTexturePaintRuntimeState(clearRules: true);

#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        private void ClearTexturePaintRuntimeState(bool clearRules)
        {
            texturePaintMask ??= new Dictionary<Vector3Int, int>();
            texturePaintMask.Clear();

            if (paintMaskTexture != null)
                Graphics.Blit(Texture2D.blackTexture, paintMaskTexture);

            if (texturePaintRules != null)
            {
                foreach (var rule in texturePaintRules)
                {
                    if (rule?.vegetationEntries == null)
                        continue;

                    foreach (var entry in rule.vegetationEntries)
                        entry?.instances?.Clear();
                }

                if (clearRules)
                    texturePaintRules.Clear();
            }

            if (clearRules)
            {
                selectedTextureRule = null;
                selectedTextureRuleIndex = -1;
            }

            var vegetationRenderer = GetComponent<VegetationGPURenderer>();
            if (vegetationRenderer != null)
                vegetationRenderer.ClearAllGroups();

            PushPaintMaskGlobals();
        }

        // Load from JSON path (Editor/runtime helper)
        public void LoadTilemapFromJson(string filePath, Dictionary<string, TileBase> tileDict)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"File not found: {filePath}");
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);
                if (string.IsNullOrEmpty(json))
                {
                    Debug.LogError("JSON file is empty or corrupted");
                    return;
                }

                SaveData saveData;
                try
                {
                    saveData = JsonUtility.FromJson<SaveData>(json);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"JSON deserialization failed: {ex.Message}");
                    Debug.LogError($"JSON content: {json}");
                    return;
                }

                if (saveData == null)
                {
                    Debug.LogError("SaveData is null after JSON parsing");
                    return;
                }
                if (saveData.tilemaps == null)
                {
                    Debug.LogError("SaveData.tilemaps is null");
                    return;
                }

                ClearTexturePaintRuntimeState(clearRules: false);

                if (levels == null || levels.Count == 0)
                {
                    Debug.LogError("No levels defined. Cannot load tilemap from JSON.");
                    return;
                }
                if (currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
                {
                    Debug.LogError($"Invalid currentLevelIndex: {currentLevelIndex}. Valid range is 0 to {levels.Count - 1}. Clamping to valid range.");
                    currentLevelIndex = Mathf.Clamp(currentLevelIndex, 0, levels.Count - 1);
                }

                LevelData level = levels[currentLevelIndex];
                level.paintedTextures ??= new List<PaintedTextureData>();
                level.paintedTextures.Clear();

                // ── Restore texture paint rules BEFORE painted textures ──
#if UNITY_EDITOR
                var textureRuleSource = GetTexturePaintRulesForCurrentLevel(saveData.texturePaintRulesList);
                texturePaintRules.Clear();
                if (textureRuleSource != null && textureRuleSource.Count > 0)
                {
                    foreach (var data in textureRuleSource)
                    {
                        if (data == null) continue;
                        texturePaintRules.Add(new TexturePaintRule
                        {
                            ruleName      = data.ruleName ?? "",
                            textureIndex  = data.textureIndex,
                            material      = !string.IsNullOrEmpty(data.materialPath)  ? UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(data.materialPath)    : null,
                            albedo        = !string.IsNullOrEmpty(data.albedoPath)     ? UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(data.albedoPath)     : null,
                            normal        = !string.IsNullOrEmpty(data.normalPath)     ? UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(data.normalPath)     : null,
                            height        = !string.IsNullOrEmpty(data.heightPath)     ? UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(data.heightPath)     : null,
                            emission      = !string.IsNullOrEmpty(data.emissionPath)   ? UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(data.emissionPath)   : null,
                            emissionColor = data.emissionColor,
                            textureScale  = data.textureScale,
                            blendSharpness = data.blendSharpness > 0f ? data.blendSharpness : 1f,
                            noiseScale     = data.noiseScale > 0f ? data.noiseScale : 1f,
                            noiseType      = data.noiseType,
                            removeVegetation = data.removeVegetation,
                            vegetationEntries = LoadVegetationEntries(data.vegetationEntries),
                        });
                    }
                }
                else
                {
                    selectedTextureRule = null;
                    selectedTextureRuleIndex = -1;
                }
#endif

                // Restore painted textures
                if (saveData.paintedTextures != null && saveData.paintedTextures.Count > 0)
                {
                    foreach (var paintedTextureData in saveData.paintedTextures)
                    {
                        if (paintedTextureData == null) continue;

                        level.paintedTextures.Add(new PaintedTextureData
                        {
                            position = paintedTextureData.position,
                            textureIndex = paintedTextureData.textureIndex
                        });

                        if (paintedTextureData.textureIndex >= 0 && paintedTextureData.textureIndex < texturePaintRules.Count)
                        {
                            var rule = texturePaintRules[paintedTextureData.textureIndex];
                            if (rule != null) PaintTextureCell(paintedTextureData.position, rule);
                        }
                    }
                }

                this.centerOriginToSurfaceMass = saveData.centerOriginToSurfaceMass;
                bool shouldApplyLegacySurfaceMassCentering = ShouldApplyLegacySurfaceMassCentering(saveData);
                Transform parent = targetTilemap.transform.parent;

                // Clear all children under Grid/parent
                for (int i = parent.childCount - 1; i >= 0; i--)
                    SafeDestroy(parent.GetChild(i).gameObject);

                // Recreate the target tilemap
                GameObject tmGO = new GameObject("TargetTilemap");
                tmGO.hideFlags = HideFlags.HideInHierarchy;
                tmGO.transform.SetParent(parent.transform);
                tmGO.transform.localPosition = Vector3.zero;
                targetTilemap = tmGO.AddComponent<Tilemap>();
                targetTilemap.name = "TargetTilemap";
                tmGO.AddComponent<TilemapRenderer>();

                heightTilemaps.Clear();
                EraseAllGameObjects();

                // Paths FIRST
                if (saveData.paths != null)
                {
                    paths.Clear();
                    foreach (var pd in saveData.paths)
                    {
                        if (pd == null || pd.points == null) continue;
                        Path p = RestorePathFromData(pd);
                        if (p == null) continue;
                        paths.Add(p);
                    }
                }

                // Tile rules (before creating tilemaps)
                tileRules.Clear();
                if (saveData.tileRules != null)
                {
                    foreach (var ruleData in saveData.tileRules)
                    {
                        if (ruleData == null) continue;
                        var rule = new TileRule
                        {
                            ruleName = ruleData.ruleName ?? "",
                            tile = (tileDict != null && tileDict.ContainsKey(ruleData.tileName)) ? tileDict[ruleData.tileName] : null,
                            color = ruleData.color,
                            isVisible = ruleData.isVisible,
                            roundedCorner = ruleData.roundedCorner,
                            yOffset = ruleData.yOffset,
                            useCustomTilemap = ruleData.useCustomTilemap,
                            isDigLayer = ruleData.isDigLayer,
                            isDiggable = ruleData.isDiggable,
                            isUndiggable = ruleData.isUndiggable,
                            renderOrder = ruleData.renderOrder,
                            customTargetTilemapName = ruleData.customTargetTilemapName ?? "",
                            fixBase = ruleData.fixBase,
                            sizeY = ruleData.sizeY,
                            savedDeformerHandles = (ruleData.savedDeformerHandles != null)
                                ? new List<DeformerHandleData>(ruleData.savedDeformerHandles)
                                : new List<DeformerHandleData>(),
                            enableMove = ruleData.enableMove,
                            moveOffset = ruleData.moveOffset,
                            movePause = ruleData.movePause,
                            meshMode = ruleData.meshMode,
                        };

                        // Restore procedural settings
                        if (rule.proceduralSettings == null)
                            rule.proceduralSettings = new ProceduralTileMeshGenerator.ProceduralMeshSettings();
                        rule.bottom.enabled = ruleData.bottomEnabled;
                        rule.bottom.shape = System.Enum.IsDefined(typeof(QuickTilemapEditor.BottomShape), ruleData.bottomShape)
                            ? (QuickTilemapEditor.BottomShape)ruleData.bottomShape
                            : QuickTilemapEditor.BottomShape.Rounded;
                        rule.bottom.size = Mathf.Clamp(ruleData.bottomSize, 1, 20);
                        if (ruleData.bottomProfile != null && ruleData.bottomProfile.keys != null && ruleData.bottomProfile.keys.Length > 0)
                        {
                            rule.bottom.profile = new AnimationCurve(ruleData.bottomProfile.keys)
                            {
                                preWrapMode = ruleData.bottomProfile.preWrapMode,
                                postWrapMode = ruleData.bottomProfile.postWrapMode
                            };
                        }
                        else
                        {
                            rule.bottom.profile = AnimationCurve.EaseInOut(0, 1, 1, 0);
                        }
                        rule.proceduralSettings.radius = ruleData.proceduralRadius;
                        rule.proceduralSettings.depth = ruleData.proceduralDepth;
                        rule.proceduralSettings.curveSegments = ruleData.proceduralCurveSegments;
                        rule.proceduralSettings.skirtEnabled = ruleData.proceduralSkirtEnabled;
                        rule.proceduralSettings.skirtWidth = ruleData.proceduralSkirtWidth;
                        rule.proceduralSettings.skirtHeight = ruleData.proceduralSkirtHeight;
                        rule.proceduralSettings.skirtSegments = ruleData.proceduralSkirtSegments;
                        rule.proceduralSettings.skirtUVScale = ruleData.proceduralSkirtUVScale;
                        rule.proceduralSettings.skirtUVOffsetY = ruleData.proceduralSkirtUVOffsetY;
                        rule.proceduralSettings.skirtMaterialMode = (SkirtMaterialMode)ruleData.proceduralSkirtMaterialMode;
                        rule.proceduralSettings.skirtMaskCurve =
                            ruleData.proceduralSkirtMaskCurve != null && ruleData.proceduralSkirtMaskCurve.keys != null && ruleData.proceduralSkirtMaskCurve.length > 0
                                ? new AnimationCurve(ruleData.proceduralSkirtMaskCurve.keys)
                                {
                                    preWrapMode = ruleData.proceduralSkirtMaskCurve.preWrapMode,
                                    postWrapMode = ruleData.proceduralSkirtMaskCurve.postWrapMode
                                }
                                : ProceduralTileMeshGenerator.CreateDefaultSkirtMaskCurve();
                        // Bottom cap settings
                        rule.proceduralSettings.bottomMode = (BottomMode)ruleData.proceduralBottomMode;
                        rule.proceduralSettings.bottomBevelInset = ruleData.proceduralBottomBevelInset;
                        rule.proceduralSettings.bottomBevelDepth = ruleData.proceduralBottomBevelDepth;
                        rule.proceduralSettings.bottomBevelSegments = ruleData.proceduralBottomBevelSegments;
                        rule.proceduralSettings.bottomBevelProfile = (BevelProfile)ruleData.proceduralBottomBevelProfile;
                        rule.proceduralSettings.bottomNoiseScale = ruleData.proceduralBottomNoiseScale;
                        rule.proceduralSettings.bottomNoiseAmplitude = ruleData.proceduralBottomNoiseAmplitude;
                        rule.proceduralSettings.bottomIslandSharpness = ruleData.proceduralBottomIslandSharpness;
                        rule.proceduralSettings.bottomIslandSmooth = ruleData.proceduralBottomIslandSmooth;
                        rule.proceduralSettings.bottomNoiseResolution = ruleData.proceduralBottomNoiseResolution;
                        rule.proceduralSettings.bottomNoiseSeed = ruleData.proceduralBottomNoiseSeed;
#if UNITY_EDITOR
                        if (!string.IsNullOrEmpty(ruleData.bottomMaterialPath))
                            rule.bottom.material = AssetDatabase.LoadAssetAtPath<Material>(ruleData.bottomMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralFloorMaterialPath))
                            rule.proceduralFloorMaterial = AssetDatabase.LoadAssetAtPath<Material>(ruleData.proceduralFloorMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralWallMaterialPath))
                            rule.proceduralWallMaterial = AssetDatabase.LoadAssetAtPath<Material>(ruleData.proceduralWallMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralCeilingMaterialPath))
                            rule.proceduralCeilingMaterial = AssetDatabase.LoadAssetAtPath<Material>(ruleData.proceduralCeilingMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralBottomMaterialPath))
                            rule.proceduralBottomMaterial = AssetDatabase.LoadAssetAtPath<Material>(ruleData.proceduralBottomMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralDigMaterialPath))
                            rule.proceduralDigMaterial = AssetDatabase.LoadAssetAtPath<Material>(ruleData.proceduralDigMaterialPath);
                        ApplyDefaultProceduralMaterials(rule);
#else
                        if (!string.IsNullOrEmpty(ruleData.bottomMaterialPath))
                            rule.bottom.material = LoadRuntimeMaterial(ruleData.bottomMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralFloorMaterialPath))
                            rule.proceduralFloorMaterial = LoadRuntimeMaterial(ruleData.proceduralFloorMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralWallMaterialPath))
                            rule.proceduralWallMaterial = LoadRuntimeMaterial(ruleData.proceduralWallMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralCeilingMaterialPath))
                            rule.proceduralCeilingMaterial = LoadRuntimeMaterial(ruleData.proceduralCeilingMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralBottomMaterialPath))
                            rule.proceduralBottomMaterial = LoadRuntimeMaterial(ruleData.proceduralBottomMaterialPath);
                        if (!string.IsNullOrEmpty(ruleData.proceduralDigMaterialPath))
                            rule.proceduralDigMaterial = LoadRuntimeMaterial(ruleData.proceduralDigMaterialPath);
                        ApplyDefaultProceduralMaterials(rule);
#endif

                        tileRules.Add(rule);
                    }
                }
                selectedTileRuleIndex = tileRules.Count > 0 ? 0 : -1;

                // GameObject rules
                if (saveData.gameObjectRules != null) gameObjectRules = new List<GameObjectRule>(saveData.gameObjectRules);
                else gameObjectRules = new List<GameObjectRule>();

#if UNITY_EDITOR
                foreach (var rule in gameObjectRules)
                {
                    if (rule != null && rule.prefab == null && !string.IsNullOrEmpty(rule.prefabResourcePath))
                        rule.prefab = AssetDatabase.LoadAssetAtPath<GameObject>(rule.prefabResourcePath);
                }
#endif

                // Create tilemaps and index them
                var tilemapsByName = new Dictionary<string, Tilemap>();

                foreach (var tmData in saveData.tilemaps)
                {
                    if (tmData == null) continue;

                    Tilemap targetMap;
                    if (tmData.tilemapName == "TargetTilemap")
                    {
                        targetMap = targetTilemap;
                        targetMap.ClearAllTiles();
                        targetMap.transform.localPosition = new Vector3(0, tmData.height, 0);
                        targetMap.transform.localRotation = Quaternion.identity;
                    }
                    else
                    {
                        GameObject newTilemapObj = new GameObject(tmData.tilemapName);
                        newTilemapObj.transform.SetParent(parent);
                        newTilemapObj.transform.localPosition = new Vector3(0, tmData.height, 0);
                        newTilemapObj.transform.localRotation = Quaternion.identity;
                        targetMap = newTilemapObj.AddComponent<Tilemap>();

                        var renderer = newTilemapObj.AddComponent<TilemapRenderer>();
                        renderer.sortingOrder = Mathf.RoundToInt(tmData.height * 100f);
                    }

                    if (tmData.tiles != null)
                    {
                        foreach (var info in tmData.tiles)
                        {
                            if (info == null || tileDict == null) continue;
                            Vector3Int pos = new Vector3Int(info.x, info.y, info.z);
                            if (tileDict.TryGetValue(info.tileName, out TileBase tile))
                            {
                                targetMap.SetTile(pos, tile);
                                targetMap.SetColor(pos, info.color);
                                targetMap.SetTileFlags(pos, TileFlags.None);
                            }
                        }
                    }

                    heightTilemaps[tmData.height] = targetMap;
                    tilemapsByName[tmData.tilemapName] = targetMap;
                }

                // Assign tilemaps to rules AFTER creation
                for (int i = 0; i < tileRules.Count; i++)
                {
                    var rule = tileRules[i];

                    if (!rule.useCustomTilemap)
                    {
                        rule.customTargetTilemap = targetTilemap;
                    }
                    else if (rule.tile != null)
                    {
                        bool assigned = false;

                        if (!string.IsNullOrEmpty(rule.customTargetTilemapName) &&
                            tilemapsByName.TryGetValue(rule.customTargetTilemapName, out Tilemap explicitMap))
                        {
                            rule.customTargetTilemap = explicitMap;
                            rule.yOffset = explicitMap.transform.localPosition.y;
                            assigned = true;
                        }

                        if (!assigned)
                        {
                            string expectedName = $"Tilemap_Rule_{rule.tile.name}_{rule.yOffset}";
                            if (tilemapsByName.TryGetValue(expectedName, out Tilemap expectedMap))
                            {
                                rule.customTargetTilemap = expectedMap;
                                rule.yOffset = expectedMap.transform.localPosition.y;
                                assigned = true;
                            }
                        }

                        if (!assigned)
                        {
                            foreach (var kvp in heightTilemaps)
                            {
                                float savedHeight = kvp.Key;
                                Tilemap heightMap = kvp.Value;
                                if (Mathf.Abs(rule.yOffset - savedHeight) < 0.01f)
                                {
                                    rule.customTargetTilemap = heightMap;
                                    rule.yOffset = savedHeight;
                                    assigned = true;
                                    break;
                                }
                            }
                        }

                        if (!assigned)
                        {
                            foreach (var kvp in tilemapsByName)
                            {
                                Tilemap candidateMap = kvp.Value;
                                bool foundTileInMap = false;

                                BoundsInt bounds = candidateMap.cellBounds;
                                foreach (Vector3Int pos in bounds.allPositionsWithin)
                                {
                                    TileBase tileAtPos = candidateMap.GetTile(pos);
                                    if (tileAtPos != null && rule.tile != null && tileAtPos.name == rule.tile.name)
                                    {
                                        foundTileInMap = true;
                                        break;
                                    }
                                }

                                if (foundTileInMap)
                                {
                                    rule.customTargetTilemap = candidateMap;
                                    rule.yOffset = candidateMap.transform.localPosition.y;
                                    assigned = true;
                                    break;
                                }
                            }
                        }

                        if (!assigned)
                        {
                            rule.customTargetTilemap = CreateTilemapForRule(rule);
                            if (rule.customTargetTilemap != null)
                            {
                                heightTilemaps[rule.yOffset] = rule.customTargetTilemap;
                                tilemapsByName[rule.customTargetTilemap.name] = rule.customTargetTilemap;
                                assigned = true;
                            }
                        }
                    }

                if (rule.customTargetTilemap != null)
                {
                    rule.renderOrder = Mathf.RoundToInt(rule.yOffset * 100f);
                    var renderer = rule.customTargetTilemap.GetComponent<TilemapRenderer>();
                    if (renderer != null) renderer.sortingOrder = rule.renderOrder;
                }
            }

            foreach (var rule in tileRules)
            {
                    if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                    {
                        rule.customTargetTilemap.transform.localRotation = Quaternion.identity;
                        var renderer = rule.customTargetTilemap.GetComponent<TilemapRenderer>();
                        if (renderer != null) renderer.sortingOrder = rule.renderOrder;
                    }
                }

                // Update skirts
                foreach (var rule in tileRules)
                {
                    if (rule.customTargetTilemap != null)
                    {
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

                EnsureDeformerComponentsForLoad();
                RestoreDeformerHandlesAfterLoad();

                RefreshAllRadialHillDeformerBindings();

                ApplyDeformerSettingsAfterLoad(saveData.tileRules);

                // Rebuild procedural meshes for rules that use Procedural mode
                RebuildAllProceduralMeshes();

                RemoveEmptyCustomTileRules();
                DestroyLoadedOrphanEmptyTilemaps(tilemapsByName);

                // Only synthesize missing rules for legacy saves that had tilemaps
                // but no serialized tileRules section.
                if (saveData.tileRules == null || saveData.tileRules.Count == 0)
                    CreateMissingTileRules();

                // Restore placed objects
                placedObjects.Clear();
                if (saveData.placedObjects != null)
                {
                    foreach (var god in saveData.placedObjects)
                    {
                        if (god == null) continue;
                        var placed = new PlacedObject
                        {
                            position = god.position,
                            ruleIndex = god.ruleIndex,
                            color = god.color,
                            pathIndex = god.pathIndex,
                            rotation = god.rotation,
                            parentTilemapName = god.parentTilemapName ?? "",
                            prefabResourcePath = god.prefabPath ?? "",
                            placementSurface = god.placementSurface,
                            skirtAnchorCell = god.skirtAnchorCell
                        };
                        placed.instanceYOffset = god.instanceYOffset;
                        if (god.instanceYOffsetVersion < PlacedObject.CurrentInstanceYOffsetVersion)
                            placed.MarkInstanceYOffsetLegacy();
                        else
                            placed.MarkInstanceYOffsetUpgraded();
                        placedObjects.Add(placed);
                    }
                }

                foreach (var rule in gameObjectRules)
                {
                    if (rule == null) continue;
                    rule.instanceOffsets ??= new List<InstanceOffset>();
                    rule.instanceOffsets.Clear();
                }

                InstantiateGameObjects();

                if (levels != null && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
                {
                    if (saveData.levelProperties != null)
                        levels[currentLevelIndex].properties = saveData.levelProperties;
                    else if (levels[currentLevelIndex].properties == null)
                        levels[currentLevelIndex].properties = new List<LevelProperty>();
                }

                Transform levelContainer = targetTilemap.transform.parent;

                if (shouldApplyLegacySurfaceMassCentering)
                    CenterLevelToSurfaceMassXZ(levelContainer);
                else
                    levelContainer.position = new Vector3(0, levelContainer.position.y, 0);

                // Diagnostic: check objects survive centering
                if (instantiatedGameObjects != null)
                {
                    int alive = 0, dead = 0;
                    foreach (var go in instantiatedGameObjects)
                    {
                        if (go != null) { alive++; }
                        else dead++;
                    }
                }

                if (texturePaintMask.Count > 0)
                {
                    UpdatePaintMaskTexture();
                    UpdateBlendPreviewMaterial();
                    PushPaintMaskGlobals();
                }

                // ...
                RebuildPaintMaskAndMaterials();
#if UNITY_EDITOR
                ResolveTexturePaintRulesAfterLoad();
                ApplyTexturePaintRulesToEditorMaterials();
                if (texturePaintMask.Count > 0)
                    UpdateBlendPreviewMaterial();
                RebuildVegetationGPURendererIfNeeded();
#endif
                InitializePathFollowersAfterLoad();
                FixChildScales();

                // ===== INSTALLATION DES ANIMATIONS DANS LoadTilemapFromJson =====

                foreach (var rule in tileRules)
                {
                    // On anime uniquement les règles qui poussent vers une tilemap dédiée
                    if (!rule.useCustomTilemap || rule.customTargetTilemap == null)
                        continue;

                    var tm = rule.customTargetTilemap;

                    // Nettoyage d’orientation & base locale
                    tm.transform.localRotation = Quaternion.identity;
                    var baseLocal = tm.transform.localPosition;

                    // Récupère / ajoute l’animator
                    var animator = tm.GetComponent<TilemapMoveAnimator>();
                    if (animator == null)
                        animator = tm.gameObject.AddComponent<TilemapMoveAnimator>();

                    // Active/désactive selon la règle
                    if (rule.enableMove && rule.moveOffset.sqrMagnitude > 1e-6f)
                    {
                        animator.enabled = true;
                        // NOTE: la signature Configure(base, offset, pause, duration=0.75f)
                        animator.Configure(baseLocal, rule.moveOffset, rule.movePause, 0.75f);
                    }
                    else
                    {
                        animator.ResetToBase(baseLocal);
                        animator.enabled = false;
                    }
                }

                // ===== FIN DU CODE D'ANIMATION =====

                // Optional: DebugTilemapAssignments();

            }
            catch (Exception ex)
            {
                Debug.LogError($"Critical error in LoadTilemapFromJson: {ex.Message}");
                Debug.LogError($"Stack trace: {ex.StackTrace}");
                ClearCurrentLevel();
            }
        }

        // -----------------------------------------------------------------
        // Deformer settings helpers for save/load
        // -----------------------------------------------------------------

        /// <summary>
        /// Find the RadialHillDeformer for a given rule's tilemap.
        /// </summary>
        private RadialHillDeformer FindDeformerForRuleRuntime(TileRule rule)
        {
            if (rule == null) return null;
            Transform root = null;
            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                root = rule.customTargetTilemap.transform;
            else if (targetTilemap != null)
                root = targetTilemap.transform;
            if (root == null) return null;
            return root.GetComponentInChildren<RadialHillDeformer>(true);
        }

        /// <summary>
        /// Read a deformer property for save serialization.
        /// Returns defaultVal if no deformer is found.
        /// </summary>
        private T GetDeformerSettingForRule<T>(TileRule rule, System.Func<RadialHillDeformer, T> getter, T defaultVal)
        {
            var d = FindDeformerForRuleRuntime(rule);
            return d != null ? getter(d) : defaultVal;
        }

        /// <summary>
        /// Apply saved deformer settings to all rules after load.
        /// Call this AFTER RestoreDeformerHandlesAfterLoad() + RefreshAllRadialHillDeformerBindings().
        /// </summary>
        private void ApplyDeformerSettingsAfterLoad(List<TileRuleData> ruleDataList)
        {
            if (ruleDataList == null || tileRules == null) return;

            for (int i = 0; i < tileRules.Count && i < ruleDataList.Count; i++)
            {
                var rule = tileRules[i];
                var rd = ruleDataList[i];
                if (rule == null || rd == null) continue;

                var deformer = FindDeformerForRuleRuntime(rule);
                if (deformer == null) continue;

                deformer.shape = (DOTSDeformShape)rd.deformerShape;
                deformer.radius = rd.deformerRadius;
                deformer.falloff = (DOTSFalloff)rd.deformerFalloff;
                deformer.gaussianSharpness = rd.deformerGaussianSharpness;
                deformer.heightPerUnitY = rd.deformerHeightPerUnitY;
                deformer.yDeformRatio = rd.deformerYDeformRatio;
                deformer.invertDirection = rd.deformerInvertDirection;
                deformer.linkRadiusToScale = rd.deformerLinkRadiusToScale;
                deformer.radiusLinkMode = (DOTSRadiusLinkMode)rd.deformerRadiusLinkMode;
                deformer.useYMin = rd.deformerUseYMin;
                deformer.yMin = rd.deformerYMin;
                deformer.yMinRelativeToHandle = rd.deformerYMinRelativeToHandle;
                deformer.yFeather = rd.deformerYFeather;
                deformer.clampWorldMinY = rd.deformerClampWorldMinY;
                deformer.worldMinY = rd.deformerWorldMinY;
                deformer.yMinFalloff = (DOTSFalloff)rd.deformerYMinFalloff;
                deformer.yMinGaussianSharpness = rd.deformerYMinGaussianSharpness;

                // Re-apply deformation with new settings
                deformer.Apply();
            }
        }

#if UNITY_EDITOR
        private const string PaintShaderFinalPerfectEditor = "BEKKOLOCO/PaintShader_FinalPerfect";

        private static List<TexturePaintRuleData> CloneTexturePaintRuleDataList(List<TexturePaintRuleData> source)
        {
            var clone = new List<TexturePaintRuleData>();
            if (source == null)
                return clone;

            foreach (var data in source)
            {
                if (data == null)
                    continue;

                clone.Add(new TexturePaintRuleData
                {
                    ruleName = data.ruleName ?? "",
                    textureIndex = data.textureIndex,
                    materialPath = data.materialPath ?? "",
                    albedoPath = data.albedoPath ?? "",
                    normalPath = data.normalPath ?? "",
                    heightPath = data.heightPath ?? "",
                    emissionPath = data.emissionPath ?? "",
                    emissionColor = data.emissionColor,
                    textureScale = data.textureScale,
                    blendSharpness = data.blendSharpness,
                    noiseScale = data.noiseScale,
                    noiseType = data.noiseType,
                    removeVegetation = data.removeVegetation,
                    vegetationEntries = CloneVegetationEntryDataList(data.vegetationEntries),
                });
            }

            return clone;
        }

        private string GetTexturePaintRuleCacheKey(LevelData level)
        {
            if (level == null)
                return null;

            if (level.jsonFile != null)
            {
                string assetPath = AssetDatabase.GetAssetPath(level.jsonFile);
                if (!string.IsNullOrWhiteSpace(assetPath))
                    return assetPath;
            }

            if (!string.IsNullOrWhiteSpace(level.levelName))
                return level.levelName.Trim();

            return null;
        }

        private void StoreSessionTexturePaintRulesForLevel(LevelData level, List<TexturePaintRuleData> snapshot)
        {
            string cacheKey = GetTexturePaintRuleCacheKey(level);
            if (string.IsNullOrEmpty(cacheKey))
                return;

            sessionTexturePaintRulesByLevel ??= new Dictionary<string, List<TexturePaintRuleData>>();
            sessionTexturePaintRulesByLevel[cacheKey] = CloneTexturePaintRuleDataList(snapshot);
        }

        private List<TexturePaintRuleData> BuildTexturePaintRulesDataSnapshot()
        {
            var texturePaintRulesData = new List<TexturePaintRuleData>();
            if (texturePaintRules == null)
                return texturePaintRulesData;

            for (int i = 0; i < texturePaintRules.Count; i++)
            {
                var tpr = texturePaintRules[i];
                if (tpr == null)
                    continue;

                var resolvedMaterial = ResolveTexturePaintRuleEditorMaterial(tpr, i);
                var resolvedAlbedo = ResolveTexturePaintRuleEditorTexture(tpr, i);
                if (!IsPaintShaderFinalPerfectEditorMaterial(tpr.material) && resolvedMaterial != null)
                    tpr.material = resolvedMaterial;
                if (!IsMeaningfulEditorTexture(tpr.albedo) && resolvedAlbedo != null)
                    tpr.albedo = resolvedAlbedo;

                texturePaintRulesData.Add(new TexturePaintRuleData
                {
                    ruleName = tpr.ruleName ?? "",
                    textureIndex = i,
                    materialPath = resolvedMaterial != null ? AssetDatabase.GetAssetPath(resolvedMaterial) : "",
                    albedoPath = resolvedAlbedo != null ? AssetDatabase.GetAssetPath(resolvedAlbedo) : "",
                    normalPath = tpr.normal != null ? AssetDatabase.GetAssetPath(tpr.normal) : "",
                    heightPath = tpr.height != null ? AssetDatabase.GetAssetPath(tpr.height) : "",
                    emissionPath = tpr.emission != null ? AssetDatabase.GetAssetPath(tpr.emission) : "",
                    emissionColor = tpr.emissionColor,
                    textureScale = tpr.textureScale,
                    blendSharpness = tpr.blendSharpness,
                    noiseScale = tpr.noiseScale,
                    noiseType = tpr.noiseType,
                    removeVegetation = tpr.removeVegetation,
                    vegetationEntries = SaveVegetationEntries(tpr.vegetationEntries),
                });
            }

            return texturePaintRulesData;
        }

        public void CacheCurrentLevelTexturePaintRulesForSession()
        {
            if (levels == null || currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
                return;

            var level = levels[currentLevelIndex];
            if (level == null)
                return;

            StoreSessionTexturePaintRulesForLevel(level, BuildTexturePaintRulesDataSnapshot());
        }

        private List<TexturePaintRuleData> GetTexturePaintRulesForCurrentLevel(List<TexturePaintRuleData> savedRules)
        {
            if (savedRules != null)
                return CloneTexturePaintRuleDataList(savedRules);

            if (levels != null && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
            {
                var level = levels[currentLevelIndex];
                string cacheKey = GetTexturePaintRuleCacheKey(level);
                if (!string.IsNullOrEmpty(cacheKey) &&
                    sessionTexturePaintRulesByLevel != null &&
                    sessionTexturePaintRulesByLevel.TryGetValue(cacheKey, out var cachedRules))
                {
                    return CloneTexturePaintRuleDataList(cachedRules);
                }
            }

            return savedRules;
        }

        private static bool IsPaintShaderFinalPerfectEditorMaterial(Material material)
        {
            return material != null &&
                   material.shader != null &&
                   material.shader.name == PaintShaderFinalPerfectEditor;
        }

        private static bool IsMeaningfulEditorTexture(Texture2D texture)
        {
            if (texture == null)
                return false;

            if (texture == Texture2D.blackTexture || texture == Texture2D.whiteTexture || texture == Texture2D.grayTexture)
                return false;

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath))
                return false;

            if (assetPath == "Resources/unity_builtin_extra" || assetPath.StartsWith("Library/"))
                return false;

            if (!assetPath.StartsWith("Assets/") && !assetPath.StartsWith("Packages/"))
                return false;

            return true;
        }

        private void ResolveTexturePaintRulesAfterLoad()
        {
            if (texturePaintRules == null)
                return;

            for (int i = 0; i < texturePaintRules.Count; i++)
            {
                var rule = texturePaintRules[i];
                if (rule == null)
                    continue;

                rule.textureIndex = i;
                if (string.IsNullOrWhiteSpace(rule.ruleName))
                    rule.ruleName = $"Texture {i + 1}";

                if (!IsPaintShaderFinalPerfectEditorMaterial(rule.material))
                    rule.material = ResolveTexturePaintRuleEditorMaterial(rule, i);

                SyncTexturePaintRuleFromEditorMaterial(rule, i);
            }
        }

        private Material ResolveTexturePaintRuleEditorMaterial(TexturePaintRule rule, int ruleIndex)
        {
            _ = ruleIndex;

            if (IsPaintShaderFinalPerfectEditorMaterial(rule?.material))
                return rule.material;

            if (texturePaintRules != null)
            {
                foreach (var existingRule in texturePaintRules)
                {
                    if (existingRule == null || ReferenceEquals(existingRule, rule))
                        continue;

                    if (IsPaintShaderFinalPerfectEditorMaterial(existingRule.material))
                        return existingRule.material;
                }
            }

            foreach (var material in FindEditorScenePaintMaterials())
            {
                if (IsPaintShaderFinalPerfectEditorMaterial(material))
                    return material;
            }

            return null;
        }

        private Texture2D ResolveTexturePaintRuleEditorTexture(TexturePaintRule rule, int ruleIndex)
        {
            var material = ResolveTexturePaintRuleEditorMaterial(rule, ruleIndex);
            if (TryGetTexturePaintRuleTextureFromMaterial(material, ruleIndex, out var textureFromMaterial))
                return textureFromMaterial;

            return material == null && IsMeaningfulEditorTexture(rule?.albedo) ? rule.albedo : null;
        }

        private void SyncTexturePaintRuleFromEditorMaterial(TexturePaintRule rule, int ruleIndex)
        {
            if (rule == null)
                return;

            var material = ResolveTexturePaintRuleEditorMaterial(rule, ruleIndex);
            if (!IsPaintShaderFinalPerfectEditorMaterial(material))
                return;

            rule.material = material;

            int slotIndex = ruleIndex + 1;
            string textureProperty = $"_Tex{slotIndex}";
            string blendProperty = $"_BlendSharpness{slotIndex}";
            string noiseProperty = $"_NoiseScale{slotIndex}";

            var albedo = GetMeaningfulTexture2DFromMaterial(material, textureProperty);
            rule.albedo = albedo;

            if (material.HasProperty(textureProperty))
            {
                Vector2 scale = material.GetTextureScale(textureProperty);
                if (scale.x > 0f)
                    rule.textureScale = scale.x;
            }

            if (material.HasProperty(blendProperty))
                rule.blendSharpness = material.GetFloat(blendProperty);
            else if (slotIndex == 1 && material.HasProperty("_BlendSharpness"))
                rule.blendSharpness = material.GetFloat("_BlendSharpness");

            if (material.HasProperty(noiseProperty))
                rule.noiseScale = material.GetFloat(noiseProperty);
            else if (slotIndex == 1 && material.HasProperty("_NoiseScale"))
                rule.noiseScale = material.GetFloat("_NoiseScale");
        }

        private void ApplyTexturePaintRulesToEditorMaterials()
        {
            if (texturePaintRules == null)
                return;

            for (int i = 0; i < texturePaintRules.Count; i++)
            {
                var rule = texturePaintRules[i];
                if (rule == null)
                    continue;

                var resolvedMaterial = ResolveTexturePaintRuleEditorMaterial(rule, i);
                if (!IsPaintShaderFinalPerfectEditorMaterial(resolvedMaterial))
                    continue;

                if (rule.material != resolvedMaterial)
                    rule.material = resolvedMaterial;

                int slotIndex = i + 1;
                string textureProperty = $"_Tex{slotIndex}";
                string blendProperty = $"_BlendSharpness{slotIndex}";
                string noiseProperty = $"_NoiseScale{slotIndex}";

                if (!resolvedMaterial.HasProperty(textureProperty))
                    continue;

                Texture slotTexture = rule.albedo != null
                    ? rule.albedo
                    : GetResolvedTexturePaintBaseTexture();
                float slotScale = rule.albedo != null
                    ? Mathf.Max(0.0001f, rule.textureScale)
                    : Mathf.Max(0.0001f, GetResolvedTexturePaintBaseTextureScale());

                resolvedMaterial.SetTexture(textureProperty, slotTexture != null ? slotTexture : Texture2D.grayTexture);
                resolvedMaterial.SetTextureScale(textureProperty, Vector2.one * slotScale);

                if (resolvedMaterial.HasProperty(blendProperty))
                    resolvedMaterial.SetFloat(blendProperty, rule.blendSharpness);
                else if (slotIndex == 1 && resolvedMaterial.HasProperty("_BlendSharpness"))
                    resolvedMaterial.SetFloat("_BlendSharpness", rule.blendSharpness);

                if (resolvedMaterial.HasProperty(noiseProperty))
                    resolvedMaterial.SetFloat(noiseProperty, rule.noiseScale);
                else if (slotIndex == 1 && resolvedMaterial.HasProperty("_NoiseScale"))
                    resolvedMaterial.SetFloat("_NoiseScale", rule.noiseScale);

                EditorUtility.SetDirty(resolvedMaterial);
            }
        }

        private static bool TryGetTexturePaintRuleTextureFromMaterial(Material material, int ruleIndex, out Texture2D texture)
        {
            texture = null;
            if (material == null || material.shader == null || material.shader.name != PaintShaderFinalPerfectEditor)
                return false;

            int slotIndex = ruleIndex + 1;
            string propertyName = $"_Tex{slotIndex}";

            texture = GetMeaningfulTexture2DFromMaterial(material, propertyName);
            return texture != null;
        }

        private static Texture2D GetMeaningfulTexture2DFromMaterial(Material material, string propertyName)
        {
            if (material == null || string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
                return null;

            var texture = material.GetTexture(propertyName) as Texture2D;
            return IsMeaningfulEditorTexture(texture) ? texture : null;
        }

        private IEnumerable<Material> FindEditorPaintMaterials()
        {
            foreach (var material in FindEditorScenePaintMaterials())
            {
                if (IsPaintShaderFinalPerfectEditorMaterial(material))
                {
                    yield return material;
                }
            }
        }

        private IEnumerable<Material> FindEditorScenePaintMaterials()
        {
            var rootIds = new HashSet<int>();
            var roots = new List<Transform>();
            var seenMaterials = new HashSet<int>();

            void AddRoot(Transform root)
            {
                if (root != null && rootIds.Add(root.GetInstanceID()))
                    roots.Add(root);
            }

            AddRoot(transform);
            AddRoot(transform.parent);
            AddRoot(targetTilemap != null ? targetTilemap.transform : null);
            AddRoot(targetTilemap != null ? targetTilemap.transform.parent : null);

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
                        if (IsPaintShaderFinalPerfectEditorMaterial(material) &&
                            seenMaterials.Add(material.GetInstanceID()))
                        {
                            yield return material;
                        }
                    }
                }
            }
        }
        /// <summary>Rebuild VegetationGPURenderer draw groups from current rules.
        /// Always clears previous groups so stale vegetation from another level is removed.</summary>
        private void RebuildVegetationGPURendererIfNeeded()
        {
            var renderer = GetComponent<VegetationGPURenderer>();

            // No rules at all — just clear if renderer exists
            if (texturePaintRules == null)
            {
                if (renderer != null) renderer.ClearAllGroups();
                return;
            }

            // Always ensure renderer exists so we can clear + rebuild
            if (renderer == null) renderer = gameObject.AddComponent<VegetationGPURenderer>();

            if (renderer.cullingShader == null)
            {
                string[] guids = AssetDatabase.FindAssets("VegetationCulling t:ComputeShader");
                foreach (string guid in guids)
                {
                    var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(AssetDatabase.GUIDToAssetPath(guid));
                    if (cs != null) { renderer.cullingShader = cs; break; }
                }
            }
            if (renderer.instancedShader == null)
                renderer.instancedShader = Shader.Find("BEKKOLOCO/VegetationInstanced");

            renderer.RefreshVegetationPositions(texturePaintRules);
            renderer.RebuildFromRules(texturePaintRules);
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Vegetation serialization helpers
         *──────────────────────────────────────────────────────────────────*/

        private static List<VegetationEntryData> SaveVegetationEntries(List<VegetationEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                return new List<VegetationEntryData>();

            var result = new List<VegetationEntryData>();
            foreach (var e in entries)
            {
                if (e == null) continue;
                result.Add(new VegetationEntryData
                {
                    mode = (int)e.mode,
                    populateSource = (int)e.populateSource,
                    placementSurface = (int)e.placementSurface,
                    prefabPath = e.prefab != null ? AssetDatabase.GetAssetPath(e.prefab) : "",
                    cardTexturePath = e.cardTexture != null ? AssetDatabase.GetAssetPath(e.cardTexture) : "",
                    cardMaterialPath = e.cardMaterial != null ? AssetDatabase.GetAssetPath(e.cardMaterial) : "",
                    cardTint = e.cardTint,
                    cardWidth = e.cardWidth,
                    cardHeight = e.cardHeight,
                    density = e.density,
                    minScale = e.minScale,
                    maxScale = e.maxScale,
                    randomRotationY = e.randomRotationY,
                    rotationYDegrees = e.rotationYDegrees,
                    skirtOffset = e.skirtOffset,
                    yOffset = e.yOffset,
                    instances = e.instances != null ? new List<VegetationInstanceData>(e.instances) : new List<VegetationInstanceData>(),
                });
            }
            return result;
        }

        private static List<VegetationEntry> LoadVegetationEntries(List<VegetationEntryData> dataList)
        {
            if (dataList == null || dataList.Count == 0)
                return new List<VegetationEntry>();

            var result = new List<VegetationEntry>();
            foreach (var d in dataList)
            {
                if (d == null) continue;
                result.Add(new VegetationEntry
                {
                    mode = (VegetationMode)d.mode,
                    populateSource = System.Enum.IsDefined(typeof(VegetationPopulateSource), d.populateSource)
                        ? (VegetationPopulateSource)d.populateSource
                        : VegetationPopulateSource.Auto,
                    placementSurface = System.Enum.IsDefined(typeof(VegetationPlacementSurface), d.placementSurface)
                        ? (VegetationPlacementSurface)d.placementSurface
                        : VegetationPlacementSurface.Ground,
                    prefab = !string.IsNullOrEmpty(d.prefabPath)
                        ? AssetDatabase.LoadAssetAtPath<GameObject>(d.prefabPath)
                        : null,
                    cardTexture = !string.IsNullOrEmpty(d.cardTexturePath)
                        ? AssetDatabase.LoadAssetAtPath<Texture2D>(d.cardTexturePath)
                        : null,
                    cardMaterial = !string.IsNullOrEmpty(d.cardMaterialPath)
                        ? AssetDatabase.LoadAssetAtPath<Material>(d.cardMaterialPath)
                        : null,
                    cardTint = d.cardTint,
                    cardWidth = d.cardWidth,
                    cardHeight = d.cardHeight,
                    density = d.density,
                    minScale = d.minScale,
                    maxScale = d.maxScale,
                    randomRotationY = d.randomRotationY,
                    rotationYDegrees = d.rotationYDegrees,
                    skirtOffset = d.skirtOffset,
                    yOffset = d.yOffset,
                    instances = d.instances != null ? new List<VegetationInstanceData>(d.instances) : new List<VegetationInstanceData>(),
                });
            }
            return result;
        }

        private static List<VegetationEntryData> CloneVegetationEntryDataList(List<VegetationEntryData> source)
        {
            if (source == null || source.Count == 0)
                return new List<VegetationEntryData>();

            var clone = new List<VegetationEntryData>();
            foreach (var d in source)
            {
                if (d == null) continue;
                clone.Add(new VegetationEntryData
                {
                    mode = d.mode,
                    populateSource = d.populateSource,
                    placementSurface = d.placementSurface,
                    prefabPath = d.prefabPath ?? "",
                    cardTexturePath = d.cardTexturePath ?? "",
                    cardMaterialPath = d.cardMaterialPath ?? "",
                    cardTint = d.cardTint,
                    cardWidth = d.cardWidth,
                    cardHeight = d.cardHeight,
                    density = d.density,
                    minScale = d.minScale,
                    maxScale = d.maxScale,
                    randomRotationY = d.randomRotationY,
                    rotationYDegrees = d.rotationYDegrees,
                    skirtOffset = d.skirtOffset,
                    yOffset = d.yOffset,
                    instances = d.instances != null ? new List<VegetationInstanceData>(d.instances) : new List<VegetationInstanceData>(),
                });
            }
            return clone;
        }
#endif
    }
}
