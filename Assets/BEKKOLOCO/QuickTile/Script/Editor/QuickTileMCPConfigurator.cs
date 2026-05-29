using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.UIElements;

namespace Bekkoloco
{
    /// <summary>
    /// One-click setup for exposing QuickTile operations as Unity MCP tools.
    /// Talks to com.unity.ai.assistant via reflection (its settings types are internal),
    /// so this file compiles fine even when the package isn't installed.
    /// </summary>
    public static class QuickTileMCPConfigurator
    {
        public static readonly string[] ToolNames =
        {
            "Bekkoloco_QuickTile_GetStatus",
            "Bekkoloco_QuickTile_GetConcepts",
            "Bekkoloco_QuickTile_ListLevels",
            "Bekkoloco_QuickTile_LoadLevel",
            "Bekkoloco_QuickTile_LoadLevelByName",
            "Bekkoloco_QuickTile_CreateLevel",
            "Bekkoloco_QuickTile_SaveCurrentLevel",
            "Bekkoloco_QuickTile_ListTileRules",
            "Bekkoloco_QuickTile_ListGameObjectRules",
            "Bekkoloco_QuickTile_ListTexturePaintRules",
        };

        const string k_PackageId = "com.unity.ai.assistant";
        const string k_SettingsPath = "Project/AI/Unity MCP Server";
        const string k_McpToolAttributeType = "Unity.AI.MCP.Editor.ToolRegistry.McpToolAttribute, Unity.AI.MCP.Editor";
        const string k_SettingsManagerType = "Unity.AI.MCP.Editor.Settings.MCPSettingsManager, Unity.AI.MCP.Editor";

        public static bool IsPackageInstalled() =>
            Type.GetType(k_McpToolAttributeType) != null;

        public static bool AreOurToolsCompiled()
        {
            // If our MCP asmdef compiled, the tools class exists in ScriptAssemblies.
            return Type.GetType("Bekkoloco.QuickTile.MCP.QuickTileMCPTools, Bekkoloco.QuickTile.MCP.Editor") != null;
        }

        public static bool AreAllToolsEnabled()
        {
            var settings = GetSettings();
            if (settings == null) return false;

            var isEnabledMethod = settings.GetType().GetMethod("IsToolEnabled",
                BindingFlags.Public | BindingFlags.Instance);
            if (isEnabledMethod == null) return false;

            foreach (var name in ToolNames)
            {
                var result = isEnabledMethod.Invoke(settings, new object[] { name });
                if (result is bool b && !b) return false;
                if (!(result is bool)) return false;
            }
            return true;
        }

        public static bool SetAllToolsEnabled(bool enabled)
        {
            var settings = GetSettings();
            if (settings == null) return false;

            var setMethod = settings.GetType().GetMethod("SetToolEnabled",
                BindingFlags.Public | BindingFlags.Instance);
            if (setMethod == null) return false;

            foreach (var name in ToolNames)
                setMethod.Invoke(settings, new object[] { name, enabled });

            var managerType = Type.GetType(k_SettingsManagerType);
            managerType?.GetMethod("MarkDirty", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);
            managerType?.GetMethod("SaveSettings", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);

            var registryType = Type.GetType("Unity.AI.MCP.Editor.ToolRegistry.McpToolRegistry, Unity.AI.MCP.Editor");
            registryType?.GetMethod("RefreshTools", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, null);

            return true;
        }

        static object GetSettings()
        {
            var managerType = Type.GetType(k_SettingsManagerType);
            var prop = managerType?.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null);
        }

        // -------- Package install --------

        static AddRequest s_AddRequest;

        public static void InstallPackage()
        {
            if (s_AddRequest != null) return;
            s_AddRequest = Client.Add(k_PackageId);
            EditorApplication.update += PollAddRequest;
        }

        static void PollAddRequest()
        {
            if (s_AddRequest == null || !s_AddRequest.IsCompleted) return;
            EditorApplication.update -= PollAddRequest;
            if (s_AddRequest.Status == StatusCode.Success)
                Debug.Log($"[QuickTile] Installed {s_AddRequest.Result.packageId}. Unity will recompile; tools will appear after recompile.");
            else
                Debug.LogError($"[QuickTile] Failed to install {k_PackageId}: {s_AddRequest.Error?.message}");
            s_AddRequest = null;
        }

        public static void OpenSettings()
        {
            if (!SettingsService.OpenProjectSettings(k_SettingsPath))
                Debug.LogWarning("[QuickTile] Could not open Unity MCP settings. Check Project Settings > AI > Unity MCP.");
        }

        // -------- Claude Code skill install --------

        const string k_SkillName = "quicktile";

        static string SkillSourcePath
        {
            get
            {
                // Resolve a stable path regardless of package vs project-asset install.
                var asm = typeof(QuickTileMCPConfigurator).Assembly;
                string scriptDir = null;
                var guids = AssetDatabase.FindAssets("QuickTileMCPConfigurator t:MonoScript");
                if (guids != null && guids.Length > 0)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guids[0]);
                    scriptDir = Path.GetDirectoryName(assetPath);
                }
                if (string.IsNullOrEmpty(scriptDir))
                    scriptDir = "Assets/BEKKOLOCO/QuickTile/Script/Editor";

                // SkillsDir sits at QuickTile/Skills/quicktile/SKILL.md, two levels up from Script/Editor.
                var skillDir = Path.GetFullPath(Path.Combine(scriptDir, "..", "..", "Skills", k_SkillName));
                return Path.Combine(skillDir, "SKILL.md");
            }
        }

        static string SkillDestinationDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "skills", k_SkillName);

        static string SkillDestinationPath =>
            Path.Combine(SkillDestinationDir, "SKILL.md");

        public enum SkillState { SourceMissing, NotInstalled, OutOfDate, Installed }

        public static SkillState GetSkillState()
        {
            if (!File.Exists(SkillSourcePath)) return SkillState.SourceMissing;
            if (!File.Exists(SkillDestinationPath)) return SkillState.NotInstalled;
            try
            {
                var src = File.ReadAllText(SkillSourcePath);
                var dst = File.ReadAllText(SkillDestinationPath);
                return src == dst ? SkillState.Installed : SkillState.OutOfDate;
            }
            catch { return SkillState.NotInstalled; }
        }

        public static bool InstallSkill()
        {
            var src = SkillSourcePath;
            if (!File.Exists(src))
            {
                Debug.LogError($"[QuickTile] Skill source not found at {src}");
                return false;
            }
            try
            {
                Directory.CreateDirectory(SkillDestinationDir);
                File.Copy(src, SkillDestinationPath, overwrite: true);
                Debug.Log($"[QuickTile] Installed Claude Code skill to {SkillDestinationPath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[QuickTile] Failed to install skill: {e.Message}");
                return false;
            }
        }

        public static void RevealSkillInFinder()
        {
            if (Directory.Exists(SkillDestinationDir))
                EditorUtility.RevealInFinder(SkillDestinationPath);
        }

        // -------- Inspector panel --------

        [MenuItem("Bekkoloco/QuickTile/Setup MCP Tools", priority = 100)]
        public static void SetupMCPToolsMenu()
        {
            if (!IsPackageInstalled())
            {
                if (EditorUtility.DisplayDialog(
                    "Install Unity MCP?",
                    $"The package '{k_PackageId}' is required to expose QuickTile as MCP tools.\n\nInstall now?",
                    "Install", "Cancel"))
                {
                    InstallPackage();
                }
                return;
            }

            if (!AreOurToolsCompiled())
            {
                EditorUtility.DisplayDialog("Wait for recompile",
                    "QuickTile MCP tools are not yet compiled. Unity may still be recompiling — try again in a few seconds.",
                    "OK");
                return;
            }

            if (SetAllToolsEnabled(true))
            {
                EditorUtility.DisplayDialog("QuickTile MCP Tools enabled",
                    $"{ToolNames.Length} tools enabled. Opening the Unity MCP settings page to confirm.",
                    "OK");
                OpenSettings();
            }
            else
            {
                EditorUtility.DisplayDialog("Could not enable tools",
                    "Failed to access MCP settings via reflection. Enable tools manually in Project Settings > AI > Unity MCP.",
                    "OK");
            }
        }

        /// <summary>Builds a compact panel (status + buttons) that the QuickTile inspector can embed.</summary>
        public static VisualElement BuildInspectorPanel()
        {
            var root = new VisualElement();
            root.style.flexDirection = FlexDirection.Column;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.marginBottom = 4;
            root.style.borderTopWidth = 1;
            root.style.borderBottomWidth = 1;
            root.style.borderTopColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f, 1f));
            root.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f, 1f));
            root.style.backgroundColor = new StyleColor(new Color(0.10f, 0.10f, 0.10f, 1f));

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;

            var dot = new VisualElement();
            dot.style.width = 10;
            dot.style.height = 10;
            dot.style.borderTopLeftRadius = 5;
            dot.style.borderTopRightRadius = 5;
            dot.style.borderBottomLeftRadius = 5;
            dot.style.borderBottomRightRadius = 5;
            dot.style.marginRight = 6;
            headerRow.Add(dot);

            var label = new Label("MCP (Unity AI Assistant)");
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.flexGrow = 1;
            headerRow.Add(label);

            var toggle = new Toggle { text = "Enable QuickTile tools" };
            toggle.style.marginLeft = 4;
            headerRow.Add(toggle);

            root.Add(headerRow);

            var details = new VisualElement();
            details.style.flexDirection = FlexDirection.Column;
            root.Add(details);

            var statusLabel = new Label();
            statusLabel.style.fontSize = 11;
            statusLabel.style.marginTop = 4;
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            statusLabel.style.color = new StyleColor(new Color(0.75f, 0.75f, 0.75f, 1f));
            details.Add(statusLabel);

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.marginTop = 6;

            var primaryBtn = new Button();
            primaryBtn.style.flexGrow = 1;
            primaryBtn.style.marginRight = 4;

            var openBtn = new Button(OpenSettings) { text = "Open MCP Settings" };
            openBtn.style.flexGrow = 0;

            buttonRow.Add(primaryBtn);
            buttonRow.Add(openBtn);
            details.Add(buttonRow);

            // Claude Code skill install row
            var skillRow = new VisualElement();
            skillRow.style.flexDirection = FlexDirection.Row;
            skillRow.style.alignItems = Align.Center;
            skillRow.style.marginTop = 6;

            var skillLabel = new Label();
            skillLabel.style.fontSize = 11;
            skillLabel.style.flexGrow = 1;
            skillLabel.style.color = new StyleColor(new Color(0.75f, 0.75f, 0.75f, 1f));
            skillRow.Add(skillLabel);

            var skillBtn = new Button();
            skillBtn.style.flexGrow = 0;
            skillBtn.style.marginLeft = 4;
            skillRow.Add(skillBtn);

            var skillRevealBtn = new Button(RevealSkillInFinder) { text = "↗" };
            skillRevealBtn.tooltip = "Reveal ~/.claude/skills/quicktile in Finder";
            skillRevealBtn.style.flexGrow = 0;
            skillRevealBtn.style.width = 28;
            skillRevealBtn.style.marginLeft = 4;
            skillRow.Add(skillRevealBtn);

            details.Add(skillRow);

            void Refresh()
            {
                bool pkg = IsPackageInstalled();
                bool compiled = AreOurToolsCompiled();
                bool enabled = pkg && compiled && AreAllToolsEnabled();

                dot.style.backgroundColor = new StyleColor(
                    !pkg ? new Color(0.6f, 0.2f, 0.2f, 1f) :
                    !compiled ? new Color(0.9f, 0.65f, 0.2f, 1f) :
                    enabled ? new Color(0.29f, 0.87f, 0.5f, 1f) :
                              new Color(0.6f, 0.6f, 0.2f, 1f));

                bool showDetails;
                if (!pkg)
                {
                    statusLabel.text = "AI Assistant package not installed.";
                    primaryBtn.text = "Install com.unity.ai.assistant";
                    primaryBtn.SetEnabled(true);
                    openBtn.SetEnabled(false);
                    toggle.SetEnabled(false);
                    toggle.SetValueWithoutNotify(false);
                    showDetails = true;
                }
                else if (!compiled)
                {
                    statusLabel.text = "Unity is recompiling… QuickTile MCP tools will appear shortly.";
                    primaryBtn.text = "Refresh";
                    primaryBtn.SetEnabled(true);
                    openBtn.SetEnabled(true);
                    toggle.SetEnabled(false);
                    toggle.SetValueWithoutNotify(false);
                    showDetails = true;
                }
                else
                {
                    statusLabel.text = enabled
                        ? $"{ToolNames.Length} QuickTile tools active. MCP clients (Claude Code, Cursor…) can drive the editor."
                        : "Installed & compiled. Toggle on to expose QuickTile operations via MCP.";
                    primaryBtn.text = enabled ? "Disable tools" : "Enable tools";
                    primaryBtn.SetEnabled(true);
                    openBtn.SetEnabled(true);
                    toggle.SetEnabled(true);
                    toggle.SetValueWithoutNotify(enabled);
                    showDetails = enabled;
                }

                details.style.display = showDetails ? DisplayStyle.Flex : DisplayStyle.None;

                // Skill install row
                var skillState = GetSkillState();
                switch (skillState)
                {
                    case SkillState.SourceMissing:
                        skillLabel.text = "Claude Code skill: source SKILL.md missing in asset.";
                        skillBtn.text = "—";
                        skillBtn.SetEnabled(false);
                        skillRevealBtn.SetEnabled(false);
                        break;
                    case SkillState.NotInstalled:
                        skillLabel.text = "Claude Code skill not installed.";
                        skillBtn.text = "Install skill";
                        skillBtn.SetEnabled(true);
                        skillRevealBtn.SetEnabled(false);
                        break;
                    case SkillState.OutOfDate:
                        skillLabel.text = "Claude Code skill installed — update available.";
                        skillBtn.text = "Update skill";
                        skillBtn.SetEnabled(true);
                        skillRevealBtn.SetEnabled(true);
                        break;
                    case SkillState.Installed:
                        skillLabel.text = "Claude Code skill: installed & up to date.";
                        skillBtn.text = "Reinstall";
                        skillBtn.SetEnabled(true);
                        skillRevealBtn.SetEnabled(true);
                        break;
                }
            }

            primaryBtn.clicked += () =>
            {
                if (!IsPackageInstalled()) { InstallPackage(); }
                else if (!AreOurToolsCompiled()) { AssetDatabase.Refresh(); }
                else { SetAllToolsEnabled(!AreAllToolsEnabled()); }
                Refresh();
            };

            toggle.RegisterValueChangedCallback(evt =>
            {
                if (!IsPackageInstalled() || !AreOurToolsCompiled()) { Refresh(); return; }
                SetAllToolsEnabled(evt.newValue);
                Refresh();
            });

            skillBtn.clicked += () => { InstallSkill(); Refresh(); };

            Refresh();
            root.schedule.Execute(Refresh).Every(2000);
            return root;
        }
    }
}
