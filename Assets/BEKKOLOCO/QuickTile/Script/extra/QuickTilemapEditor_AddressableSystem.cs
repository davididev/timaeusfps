using System.Collections;
using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        public string NormalizePrefabPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

#if UNITY_EDITOR
            if (System.IO.File.Exists(path) || AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                return path;
#endif
            string resourcePath = path;

            if (resourcePath.EndsWith(".prefab"))
                resourcePath = resourcePath.Substring(0, resourcePath.Length - 7);

            if (resourcePath.Contains("Resources/"))
            {
                int idx = resourcePath.IndexOf("Resources/") + "Resources/".Length;
                resourcePath = resourcePath.Substring(idx);
            }

            return resourcePath;
        }

        private IEnumerator LoadAndInstantiateAddressable(string prefabPath, Tilemap parentTilemap, Vector3 worldPos, PlacedObject po, GameObjectRule rule)
        {
            GameObject tempIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tempIndicator.transform.position = worldPos;
            tempIndicator.transform.localScale = Vector3.one * 0.3f;

            AsyncOperationHandle<GameObject> handle = default;
            bool loadStarted = false;

            try
            {
                handle = Addressables.LoadAssetAsync<GameObject>(prefabPath);
                loadStarted = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"❌ Exception starting addressable load: {prefabPath}, Error: {ex.Message}");
                Object.Destroy(tempIndicator);
                yield break;
            }

            if (loadStarted)
            {
                while (!handle.IsDone)
                {
                    if (tempIndicator != null)
                        tempIndicator.transform.Rotate(0, 10f, 0);
                    yield return null;
                }

                try
                {
                    if (handle.Status == AsyncOperationStatus.Succeeded)
                    {
                        GameObject prefab = handle.Result;
                        GameObject placedGO = Object.Instantiate(prefab, worldPos, Quaternion.Euler(0, po.rotation, 0));
                        ConfigureGameObject(placedGO, parentTilemap, worldPos, po, rule);
                    }
                    else
                    {
                        Debug.LogError($"❌ Failed to load Addressable prefab at path: {prefabPath}, Status: {handle.Status}");
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"❌ Exception processing addressable result: {prefabPath}, Error: {ex.Message}");
                }
                finally
                {
                    // Release the Addressable handle to prevent memory leak
                    Addressables.Release(handle);
                }
            }

            if (tempIndicator != null)
                Object.Destroy(tempIndicator);
        }

#if UNITY_EDITOR
        public void AddPrefabToAddressables(GameObject prefab)
        {
            if (prefab == null) return;

            string assetPath = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning($"Could not find asset path for prefab {prefab.name}");
                return;
            }

            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;

            if (settings == null)
            {
                settings = AddressableAssetSettings.Create(
                    AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                    AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName,
                    true,
                    true);

                if (settings.DefaultGroup == null)
                {
                    string groupName = "Default Local Group";
                    AddressableAssetGroup defaultGroup = settings.CreateGroup(
                        groupName, false, false, true, null);

                    defaultGroup.AddSchema<BundledAssetGroupSchema>();
                    defaultGroup.AddSchema<ContentUpdateGroupSchema>();

                    settings.DefaultGroup = defaultGroup;
                }
            }

            AddressableAssetEntry entry = settings.FindAssetEntry(guid);
            if (entry == null)
            {
                entry = settings.CreateOrMoveEntry(guid, settings.DefaultGroup);
            }

            GameObjectRule ruleToUpdate = gameObjectRules.Find(r => r.prefab == prefab);
            if (ruleToUpdate != null)
            {
                ruleToUpdate.prefabResourcePath = assetPath;
                EditorUtility.SetDirty(this);
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryAdded, entry, true);
            AssetDatabase.SaveAssets();
        }

        public void RestorePrefabReferences()
        {
            bool hasChanges = false;

            foreach (var rule in gameObjectRules)
            {
                if (rule.prefab == null && !string.IsNullOrEmpty(rule.prefabResourcePath))
                {
                    GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(rule.prefabResourcePath);
                    if (prefabAsset != null)
                    {
                        rule.prefab = prefabAsset;
                        hasChanges = true;
                        Debug.Log($"✅ Restored prefab reference: {prefabAsset.name}");
                    }
                    else
                    {
                        Debug.LogWarning($"⚠️ Could not restore prefab from path: {rule.prefabResourcePath}");
                    }
                }
            }

            if (hasChanges) EditorUtility.SetDirty(this);
        }
#endif
    }
}
