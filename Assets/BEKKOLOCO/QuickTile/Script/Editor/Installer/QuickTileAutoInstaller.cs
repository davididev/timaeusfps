using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Bekkoloco.QuickTile.Installer
{
    [InitializeOnLoad]
    public static class QuickTileAutoInstaller
    {
        const string k_PromptedPrefKey = "Bekkoloco.QuickTile.AutoInstaller.Prompted.v1";

        static readonly string[] RequiredPackages =
        {
            "com.unity.addressables",
            "com.unity.ai.navigation",
            "com.unity.2d.tilemap",
            "com.unity.2d.tilemap.extras",
            "com.unity.entities",
            "com.unity.burst",
            "com.unity.collections",
            "com.unity.mathematics",
            "com.unity.formats.fbx",
        };

        static ListRequest s_ListRequest;
        static AddAndRemoveRequest s_AddRequest;
        static List<string> s_Missing;

        static QuickTileAutoInstaller()
        {
            EditorApplication.delayCall += CheckOnStartup;
        }

        static void CheckOnStartup()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += CheckOnStartup;
                return;
            }
            StartScan();
        }

        [MenuItem("Bekkoloco/QuickTile/🔧 Check & Install Required Packages", priority = 21)]
        public static void ManualCheck()
        {
            EditorPrefs.DeleteKey(k_PromptedPrefKey);
            StartScan();
        }

        static void StartScan()
        {
            if (s_ListRequest != null) return;
            s_ListRequest = Client.List(offlineMode: true, includeIndirectDependencies: false);
            EditorApplication.update += PollList;
        }

        static void PollList()
        {
            if (s_ListRequest == null || !s_ListRequest.IsCompleted) return;
            EditorApplication.update -= PollList;

            if (s_ListRequest.Status != StatusCode.Success)
            {
                s_ListRequest = null;
                return;
            }

            var installed = new HashSet<string>(s_ListRequest.Result.Select(p => p.name));
            s_Missing = RequiredPackages.Where(id => !installed.Contains(id)).ToList();
            s_ListRequest = null;

            if (s_Missing.Count == 0) return;

            bool alreadyPrompted = EditorPrefs.GetBool(k_PromptedPrefKey, false);
            if (alreadyPrompted) return;

            string list = string.Join("\n  • ", s_Missing);
            bool ok = EditorUtility.DisplayDialog(
                "QuickTile — Missing Packages",
                "QuickTile needs the following Unity packages to compile:\n\n  • " + list +
                "\n\nInstall them now?",
                "Install",
                "Later");

            EditorPrefs.SetBool(k_PromptedPrefKey, true);
            if (ok) InstallMissing();
        }

        static void InstallMissing()
        {
            if (s_Missing == null || s_Missing.Count == 0) return;
            s_AddRequest = Client.AddAndRemove(s_Missing.ToArray(), null);
            EditorApplication.update += PollAdd;
        }

        static void PollAdd()
        {
            if (s_AddRequest == null || !s_AddRequest.IsCompleted) return;
            EditorApplication.update -= PollAdd;

            if (s_AddRequest.Status == StatusCode.Success)
                Debug.Log("[QuickTile] Required packages installed successfully.");
            else
                Debug.LogError("[QuickTile] Failed to install packages: " + s_AddRequest.Error?.message);

            s_AddRequest = null;
            s_Missing = null;
        }
    }
}
