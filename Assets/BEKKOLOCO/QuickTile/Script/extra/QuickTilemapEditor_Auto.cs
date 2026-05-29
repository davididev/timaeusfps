// Assets/BEKKOLOCO/QuickTile/Script/extra/QuickTilemapEditor_Auto.cs
// Auto-save + live-sync (Editor only) for QuickTile.
// Play Mode safe: skips all editor-only ops while playing or about to play.

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using IOPath = System.IO.Path; // avoid conflict with QuickTilemapEditor.Path
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor : MonoBehaviour
    {
#if UNITY_EDITOR
        // ─────────────────────────────────────────────────────────────
        // Config flags (self-declared for drop-in safety)
        // ─────────────────────────────────────────────────────────────
        [Header("Editor Automation (QuickTile)")]
        [Tooltip("Update placedObjects live when moving Scene GameObjects.")]
        public bool liveSyncWhileDragging = true;

        [Tooltip("Periodic auto-save of the whole level (Editor only).")]
        public bool autoSave = true;

        [Tooltip("Minimum idle time after a change before saving (seconds).")]
        [Min(0.1f)] public float autoSaveDebounceSec = 1.0f;

        [Tooltip("Minimum interval between two autosaves (seconds).")]
        [Min(3f)] public float autoSaveIntervalSec = 20f;

        [Tooltip("Perform an auto-save right before entering Play Mode.")]
        public bool autoSaveBeforePlay = true;

        [Tooltip("Output folder for autosaves.")]
        public string autoSaveFolder = "Assets/BEKKOLOCO/QuickTile/Resources/Levels/Autosaves";

        [Tooltip("Max autosave files to keep (rotation).")]
        [Range(1, 50)] public int autoSaveKeep = 10;

        [Tooltip("Filename prefix for autosaves.")]
        public string autoSavePrefix = "QT_Auto";

        [Tooltip("Show a Debug.Log after each autosave.")]
        public bool showAutoSaveNotifications = true;

        // ─────────────────────────────────────────────────────────────
        // Internal state
        // ─────────────────────────────────────────────────────────────
        private readonly Dictionary<GameObject, Vector3Int> _lastCellByGO = new();
        private readonly Dictionary<Tilemap, Vector3> _lastTilemapLocalPosition = new();
        private int _lastComputedStateHash = 0;
        private int _lastSavedStateHash = 0;
        private double _lastChangeAt = 0;
        private double _lastSavedAt = 0;
        private double _nextTickAt = 0;

        // ─────────────────────────────────────────────────────────────
        // Bootstrap: editor update & playmode hooks
        // ─────────────────────────────────────────────────────────────
        [InitializeOnLoadMethod]
        private static void __QT_Auto_Bootstrap()
        {
            // Prevent duplicate subscriptions on domain reload
            EditorApplication.update -= __QT_Auto_GlobalUpdate;
            EditorApplication.update += __QT_Auto_GlobalUpdate;
            EditorApplication.playModeStateChanged -= __QT_Auto_PlayModeStateChanged;
            EditorApplication.playModeStateChanged += __QT_Auto_PlayModeStateChanged;
        }

        private static QuickTilemapEditor _cachedAutoEditor;
        private static double _lastAutoCacheLookup;

        private static QuickTilemapEditor GetCachedEditor()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_cachedAutoEditor == null || now - _lastAutoCacheLookup > 2.0)
            {
                _cachedAutoEditor = FindFirstObjectByType<QuickTilemapEditor>();
                _lastAutoCacheLookup = now;
            }
            return _cachedAutoEditor;
        }

        private static void __QT_Auto_GlobalUpdate()
        {
            // ⛑ Skip entirely if playing or about to switch play mode
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var editor = GetCachedEditor();
            if (editor == null) return;
            editor.__QT_Auto_Tick();
        }

        private static void __QT_Auto_PlayModeStateChanged(PlayModeStateChange state)
        {
            _cachedAutoEditor = null; // invalidate cache on play mode change
            var editor = FindFirstObjectByType<QuickTilemapEditor>();
            if (editor == null) return;

            // Pre-play autosave happens while still in Edit Mode
            if (state == PlayModeStateChange.ExitingEditMode && editor.autoSave && editor.autoSaveBeforePlay)
            {
                try { editor.__QT_Auto_DoSave("BeforePlay"); }
                catch (Exception ex) { Debug.LogError($"[QuickTile AutoSave] Pre-play failed: {ex.Message}"); }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Per-frame editor tick (throttled)
        // ─────────────────────────────────────────────────────────────
        private void __QT_Auto_Tick()
        {
            // ⛑ Safety: do nothing if playing (or about to)
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextTickAt) return;
            _nextTickAt = now + 0.10; // ~10 fps

            if (liveSyncWhileDragging) __QT_LiveSyncMovedObjects();
            __QT_LiveSyncMovedTilemaps();
            if (autoSave) __QT_AutoSaveTick();
        }

        // ─────────────────────────────────────────────────────────────
        // 1) Live Sync — drag any instantiated prefab, we update placedObjects
        // ─────────────────────────────────────────────────────────────
        private void __QT_LiveSyncMovedObjects()
        {
            if (instantiatedGameObjects == null || instantiatedGameObjects.Count == 0) return;

            bool anyChange = false;

            foreach (var go in instantiatedGameObjects)
            {
                if (go == null) continue;

                var parentTilemap = go.transform.parent ? go.transform.parent.GetComponent<Tilemap>() : null;
                if (parentTilemap == null) continue;

                var marker = go.GetComponent<QuickTileMarker>();
                if (marker != null)
                {
                    var placedObject = placedObjects.FirstOrDefault(p => p != null && p.UniqueId == marker.PlacedObjectId);
                    if (placedObject != null && placedObject.placementSurface == GameObjectPlacementSurface.Skirt)
                    {
                        _lastCellByGO[go] = placedObject.position;
                        continue;
                    }
                }

                Vector3Int newCell = SafeWorldToCell(go.transform.position, parentTilemap);

                if (!_lastCellByGO.TryGetValue(go, out var prevCell))
                {
                    _lastCellByGO[go] = newCell;
                    continue;
                }

                if (newCell != prevCell)
                {
                    string prevTilemapName = parentTilemap.name;

                    // reparent if the object moved to another height layer
                    var correctParent = GetParentTilemapForPosition(go.transform.position);
                    if (correctParent != null && correctParent != parentTilemap)
                    {
                        Undo.SetTransformParent(go.transform, correctParent.transform, "Move Prefab (Reparent)");
                        parentTilemap = correctParent;
                    }

                    __QT_UpdatePlacedObjectFromMove(go, prevCell, newCell, parentTilemap, prevTilemapName);

                    _lastCellByGO[go] = newCell;
                    anyChange = true;
                }
            }

            if (anyChange)
            {
                RefreshAllPathFollowers();   // PathSystem
                skirtsNeedRefresh = true;    // flag for SkirtManager

                // ⛑ Mark dirty only in Edit Mode
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorUtility.SetDirty(this);
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
                }

                _lastChangeAt = EditorApplication.timeSinceStartup;
            }
        }

        private void __QT_UpdatePlacedObjectFromMove(GameObject go,
                                                     Vector3Int prevCell,
                                                     Vector3Int newCell,
                                                     Tilemap newParent,
                                                     string prevParentName)
        {
            PlacedObject po = null;
            var marker = go.GetComponent<QuickTileMarker>();
            if (marker != null)
                po = placedObjects.FirstOrDefault(p => p.UniqueId == marker.PlacedObjectId);
            else
                po = placedObjects.FirstOrDefault(p => p.position == prevCell); // fallback
        

            if (po == null)
            {
                float legacyOffset = ComputeInstanceYOffset(newParent, newCell, go.transform.position);
                var newPO = new PlacedObject
                {
                    position = newCell,
                    rotation = go.transform.eulerAngles.y,
                    color = Color.white,
                    ruleIndex = (marker != null ? marker.RuleIndex
                       : gameObjectRules.FindIndex(r => r.prefab && go.name.Contains(r.prefab.name))),
                    parentTilemapName = newParent ? newParent.name : (targetTilemap ? targetTilemap.name : "Tilemap"),
                    pathIndex = -1
                };
                newPO.instanceYOffset = legacyOffset;
                newPO.MarkInstanceYOffsetUpgraded();
                placedObjects.Add(newPO);
             // Installer/mettre à jour le marqueur si absent
            (go.GetComponent<QuickTileMarker>() ?? go.AddComponent<QuickTileMarker>())
            .Initialize(newPO.UniqueId, GetInstanceID().ToString(), newPO.ruleIndex, newCell);
                return;
            }

            po.position = newCell;
            po.parentTilemapName = newParent ? newParent.name : po.parentTilemapName;
            po.rotation = go.transform.eulerAngles.y;
            po.instanceYOffset = ComputeInstanceYOffset(newParent, newCell, go.transform.position);
            po.MarkInstanceYOffsetUpgraded();

             // Garder le marqueur en phase
            if (marker != null)
             marker.Initialize(po.UniqueId, GetInstanceID().ToString(), po.ruleIndex, po.position);
        }

        private void __QT_LiveSyncMovedTilemaps()
        {
            if (tileRules == null || tileRules.Count == 0)
                return;

            bool anyChange = false;
            BeginProceduralSyncBatch();
            try
            {
                foreach (var rule in tileRules)
                {
                    if (rule == null)
                        continue;

                    Tilemap tilemap = GetTilemapForRule(rule);
                    if (tilemap == null)
                        continue;

                    Vector3 currentLocalPosition = tilemap.transform.localPosition;
                    if (!_lastTilemapLocalPosition.TryGetValue(tilemap, out Vector3 previousLocalPosition))
                    {
                        _lastTilemapLocalPosition[tilemap] = currentLocalPosition;
                        SyncRuleYOffsetFromTilemapTransform(rule, tilemap);
                        continue;
                    }

                    if ((currentLocalPosition - previousLocalPosition).sqrMagnitude <= 0.000001f)
                        continue;

                    _lastTilemapLocalPosition[tilemap] = currentLocalPosition;
                    SyncRuleYOffsetFromTilemapTransform(rule, tilemap);
                    RequestProceduralSync(rule);
                    anyChange = true;
                }
            }
            finally
            {
                EndProceduralSyncBatch();
            }

            if (!anyChange)
                return;

            // Tilemap heights feed PathMeshBuilder via GetSurfaceYAtCell(),
            // so slopes / stairs / bridges must be rebuilt when a tilemap moves.
            RebuildAllTrackMeshes();
            skirtsNeedRefresh = true;
            EditorUtility.SetDirty(this);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            SceneView.RepaintAll();
            _lastChangeAt = EditorApplication.timeSinceStartup;
        }

        // ─────────────────────────────────────────────────────────────
        // 2) Auto-Save — periodic & debounced
        // ─────────────────────────────────────────────────────────────
        private void __QT_AutoSaveTick()
        {
            // ⛑ Safety: do nothing if playing (or about to)
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            double now = EditorApplication.timeSinceStartup;

            int curHash = __QT_ComputeStateHash();
            if (curHash != _lastComputedStateHash)
            {
                _lastComputedStateHash = curHash;
                _lastChangeAt = now;
            }

            double idle = now - _lastChangeAt;
            if (idle < Math.Max(0.5, autoSaveDebounceSec)) return;

            if (now - _lastSavedAt < Math.Max(2.0, autoSaveIntervalSec)) return;

            if (_lastComputedStateHash == _lastSavedStateHash) return;

            try
            {
                __QT_Auto_DoSave("Auto");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QuickTile AutoSave] Failed: {ex.Message}");
            }
        }

        private int __QT_ComputeStateHash()
        {
            unchecked
            {
                int h = 17;

                h = h * 23 + placedObjects.Count;
                for (int i = 0; i < placedObjects.Count; i++)
                {
                    var p = placedObjects[i];
                    h = h * 23 + p.position.x;
                    h = h * 23 + p.position.y;
                    h = h * 23 + p.position.z;
                    h = h * 23 + p.ruleIndex;
                    h = h * 23 + p.pathIndex;
                    h = h * 23 + (p.parentTilemapName?.GetHashCode() ?? 0);
                }

                h = h * 23 + texturePaintMask.Count;

                var maps = GetComponentsInChildren<Tilemap>(true);
                h = h * 23 + maps.Length;
                for (int i = 0; i < maps.Length; i++)
                    h = h * 23 + (maps[i] ? maps[i].name.GetHashCode() : 0);

                return h;
            }
        }

        private void __QT_Auto_DoSave(string suffix)
        {
            // ⛑ Safety: called from EditMode (before play or idle autosave)
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            ResyncEditorStateFromScene();   // LevelSystem
            EnsureNavMeshSurface();         // CoreMethods

            // ⛑ Guard: don't autosave empty data when the level's jsonFile has content
            if (levels != null && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
            {
                var lv = levels[currentLevelIndex];
                bool dataIsEmpty = (gameObjectRules == null || gameObjectRules.Count == 0)
                                && (placedObjects == null || placedObjects.Count == 0);

                if (dataIsEmpty && lv.jsonFile != null && !string.IsNullOrEmpty(lv.jsonFile.text))
                {
                    // Peek at the existing file to see if it had real data
                    try
                    {
                        var existing = JsonUtility.FromJson<SaveData>(lv.jsonFile.text);
                        bool existingHasData = (existing.gameObjectRules != null && existing.gameObjectRules.Count > 0)
                                            || (existing.placedObjects != null && existing.placedObjects.Count > 0);
                        if (existingHasData)
                        {
                            Debug.LogWarning($"[QuickTile] ⛑ Autosave skipped for '{lv.levelName}': current data is empty but saved file has objects. Preventing data corruption.");
                            return;
                        }
                    }
                    catch { /* JSON parse failed — allow save */ }
                }
            }

            string levelBase = (levels != null && levels.Count > 0 && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
                ? levels[currentLevelIndex].levelName
                : "DefaultLevel";

            string clean = levelBase.Replace(".json", "").Replace(".bytes", "");
            string folder = string.IsNullOrEmpty(autoSaveFolder)
                ? "Assets/BEKKOLOCO/QuickTile/Resources/Levels/Autosaves"
                : autoSaveFolder;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fileName = $"{autoSavePrefix}_{clean}_{timestamp}.bytes";
            string fullPath = IOPath.Combine(folder, fileName).Replace("\\", "/");

            bool previousSuppressReloadAfterSave = _suppressReloadAfterSave;
            _suppressReloadAfterSave = true;
            try { SaveTilemapToJson(fullPath); }    // LevelSystem
            finally { _suppressReloadAfterSave = previousSuppressReloadAfterSave; }

            string resolvedPath = ResolveSaveFileSystemPath(fullPath);
            string assetPath = ConvertToAssetPath(fullPath);
            if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
            {
                Debug.LogWarning($"[QuickTile AutoSave] Save skipped import because the file was not created: {fullPath}");
                return;
            }

            AssetDatabase.SaveAssets();
            if (!string.IsNullOrEmpty(assetPath))
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            _lastSavedAt = EditorApplication.timeSinceStartup;
            _lastSavedStateHash = _lastComputedStateHash;

            __QT_PruneAutosaves(folder, clean);

        }

        /// <summary>
        /// Forces an autosave of the current in-memory state before a destructive reload.
        /// Skips throttling and dirty-hash checks so the user always has a fresh snapshot,
        /// which they can restore via the autosave browser if the reload was unintended.
        /// </summary>
        internal void __QT_Auto_SafetySnapshotBeforeReload()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!autoSave) return;

            // Only snapshot when there's actual data — avoid writing empty files.
            bool hasData = (placedObjects != null && placedObjects.Count > 0)
                        || (texturePaintMask != null && texturePaintMask.Count > 0)
                        || (gameObjectRules != null && gameObjectRules.Count > 0);
            if (!hasData) return;

            try
            {
                __QT_Auto_DoSave("PreReload");
                if (showAutoSaveNotifications)
                    Debug.Log("[QuickTile] 🛟 Safety snapshot saved before reload. Restore via 📂 Browse Autosaves if needed.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuickTile] Safety snapshot failed: {ex.Message}");
            }
        }

        private void __QT_PruneAutosaves(string folder, string levelBase)
        {
            try
            {
                var pattern = $"{autoSavePrefix}_{levelBase.Replace(".json", "").Replace(".bytes", "")}_*.bytes";
                var files = Directory.GetFiles(folder, pattern).OrderByDescending(f => f).ToList();

                if (files.Count <= Math.Max(1, autoSaveKeep)) return;

                foreach (var f in files.Skip(autoSaveKeep))
                {
                    File.Delete(f);

                    string meta = f + ".meta";
                    if (File.Exists(meta))
                        File.Delete(meta);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuickTile] Could not prune autosaves: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Autosave version browser
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a list of autosave file paths for the given level name,
        /// sorted newest first. Each entry is the full filesystem path.
        /// </summary>
        public List<string> GetAutosaveFilesForLevel(string levelName = null)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(autoSaveFolder) || !Directory.Exists(autoSaveFolder))
                return result;

            if (string.IsNullOrEmpty(levelName))
            {
                if (levels == null || currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
                    return result;
                levelName = levels[currentLevelIndex].levelName;
            }

            string clean = levelName.Replace(".json", "").Replace(".bytes", "");
            string pattern = $"{autoSavePrefix}_{clean}_*.bytes";

            try
            {
                result = Directory.GetFiles(autoSaveFolder, pattern)
                    .OrderByDescending(f => f)
                    .ToList();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[QuickTile] Could not list autosaves: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Loads a specific autosave file as the current level.
        /// This replaces the current level's jsonFile reference.
        /// </summary>
        public void LoadFromAutosave(string autosavePath)
        {
            if (!File.Exists(autosavePath))
            {
                Debug.LogError($"[QuickTile] Autosave file not found: {autosavePath}");
                return;
            }

            string assetPath = ConvertToAssetPath(autosavePath);
            if (string.IsNullOrEmpty(assetPath))
            {
                // Try reading directly from disk
                try
                {
                    string json = File.ReadAllText(autosavePath);
                    if (levels != null && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
                    {
                        // Create a temporary TextAsset-like approach: write to temp, load
                        string tempPath = IOPath.Combine(Application.temporaryCachePath, "qt_restore_temp.json");
                        File.WriteAllText(tempPath, json);

                        var tileDict = BuildTileDictionary();
                        EraseAllGameObjects();
                        LoadTilemapFromJson(tempPath, tileDict);
                        Debug.Log($"[QuickTile] ✅ Restored from autosave: {IOPath.GetFileName(autosavePath)}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[QuickTile] Failed to load autosave: {ex.Message}");
                }
                return;
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);

            if (asset == null)
            {
                Debug.LogError($"[QuickTile] Could not load autosave as TextAsset: {assetPath}");
                return;
            }

            if (levels != null && currentLevelIndex >= 0 && currentLevelIndex < levels.Count)
            {
                levels[currentLevelIndex].jsonFile = asset;
                var tileDict = BuildTileDictionary();
                bool previousForceReload = _forceLevelReload;
                _forceLevelReload = true;
                try { LoadLevel(currentLevelIndex, tileDict); }
                finally { _forceLevelReload = previousForceReload; }
                Debug.Log($"[QuickTile] ✅ Restored from autosave: {IOPath.GetFileName(autosavePath)}");
                EditorUtility.SetDirty(this);
            }
        }

        /// <summary>
        /// Parses a human-readable timestamp from an autosave filename.
        /// e.g. "QT_Auto_Level3_20260329_103220.bytes" → "2026-03-29 10:32:20"
        /// </summary>
        public static string ParseAutosaveTimestamp(string filePath)
        {
            string name = IOPath.GetFileNameWithoutExtension(filePath);
            // Expected format: PREFIX_LEVELNAME_YYYYMMDD_HHMMSS
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore < 0) return name;

            int secondLastUnderscore = name.LastIndexOf('_', lastUnderscore - 1);
            if (secondLastUnderscore < 0) return name;

            string datePart = name.Substring(secondLastUnderscore + 1, lastUnderscore - secondLastUnderscore - 1);
            string timePart = name.Substring(lastUnderscore + 1);

            if (datePart.Length == 8 && timePart.Length == 6)
            {
                return $"{datePart.Substring(0,4)}-{datePart.Substring(4,2)}-{datePart.Substring(6,2)} " +
                       $"{timePart.Substring(0,2)}:{timePart.Substring(2,2)}:{timePart.Substring(4,2)}";
            }
            return name;
        }

        /// <summary>
        /// Peeks into an autosave JSON to report counts without full deserialization.
        /// Returns (gameObjectRuleCount, placedObjectCount, tileRuleCount).
        /// </summary>
        public static (int rules, int placed, int tileRules) PeekAutosaveCounts(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                var data = JsonUtility.FromJson<SaveData>(json);
                return (
                    data.gameObjectRules?.Count ?? 0,
                    data.placedObjects?.Count ?? 0,
                    data.tileRules?.Count ?? 0
                );
            }
            catch
            {
                return (0, 0, 0);
            }
        }
#endif // UNITY_EDITOR
    }
}
