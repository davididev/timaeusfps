// QuickTileRuntimeSaveLoad.cs
// Thin wrapper around SaveTilemapToJson / LoadTilemapFromJson that targets
// Application.persistentDataPath so levels painted in Play Mode survive between
// runs. LoadTilemapFromJson expects a (tileName → TileBase) dictionary; we
// build it from the editor's current tileRules, which means any TileBase
// referenced by a saved level must still be present as a rule on the editor
// (or added to extraTiles) at load time. In a standalone build, those
// references must come from scene-serialized fields, Resources, or Addressables.

using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Bekkoloco
{
    [DisallowMultipleComponent]
    public class QuickTileRuntimeSaveLoad : MonoBehaviour
    {
        public const string DefaultFolder = "QuickTileLevels";
        public const string Extension = ".json";

        [Header("Target")]
        public QuickTilemapEditor editor;

        [Header("Options")]
        [Tooltip("Extra tiles to expose to the loader by name, on top of the ones from tileRules.")]
        public List<TileBase> extraTiles = new List<TileBase>();

        void Reset()
        {
            if (editor == null) editor = GetComponent<QuickTilemapEditor>();
        }

        public static string GetLevelPath(string levelName)
        {
            string safe = MakeSafeFileName(levelName);
            string dir = Path.Combine(Application.persistentDataPath, DefaultFolder);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, safe + Extension);
        }

        public bool Save(string levelName)
        {
            if (editor == null || string.IsNullOrWhiteSpace(levelName)) return false;
            string path = GetLevelPath(levelName);
            try
            {
                editor.SaveTilemapToJson(path);
                return File.Exists(path);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[QuickTileRuntimeSaveLoad] Save failed for '{levelName}': {ex.Message}");
                return false;
            }
        }

        public bool Load(string levelName)
        {
            if (editor == null || string.IsNullOrWhiteSpace(levelName)) return false;
            string path = GetLevelPath(levelName);
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[QuickTileRuntimeSaveLoad] File not found: {path}");
                return false;
            }

            var dict = BuildTileDict();
            try
            {
                editor.LoadTilemapFromJson(path, dict);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[QuickTileRuntimeSaveLoad] Load failed for '{levelName}': {ex.Message}");
                return false;
            }
        }

        public bool Delete(string levelName)
        {
            string path = GetLevelPath(levelName);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); return true; }
            catch (System.Exception ex)
            {
                Debug.LogError($"[QuickTileRuntimeSaveLoad] Delete failed for '{levelName}': {ex.Message}");
                return false;
            }
        }

        public List<string> ListLevels()
        {
            var list = new List<string>();
            string dir = Path.Combine(Application.persistentDataPath, DefaultFolder);
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*" + Extension))
                list.Add(Path.GetFileNameWithoutExtension(f));
            return list;
        }

        Dictionary<string, TileBase> BuildTileDict()
        {
            var dict = new Dictionary<string, TileBase>();
            if (editor != null && editor.tileRules != null)
            {
                foreach (var rule in editor.tileRules)
                {
                    if (rule?.tile == null) continue;
                    dict[rule.tile.name] = rule.tile;
                }
            }
            foreach (var t in extraTiles)
            {
                if (t == null) continue;
                dict[t.name] = t;
            }
            return dict;
        }

        static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
