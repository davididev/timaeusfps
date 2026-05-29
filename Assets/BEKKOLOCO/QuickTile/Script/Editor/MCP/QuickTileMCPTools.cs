using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.AI.MCP.Editor.Helpers;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEngine;

namespace Bekkoloco.QuickTile.MCP
{
    public static class QuickTileMCPTools
    {
        const string k_EditorTypeName = "Bekkoloco.QuickTilemapEditor, Assembly-CSharp";
        const string k_Group = "quicktile";

        // ---------- Parameter classes ----------

        public class EmptyParams { }

        public class LoadLevelParams
        {
            [McpDescription("Zero-based index of the level to load.", Required = true)]
            public int Index { get; set; }
        }

        public class LoadLevelByNameParams
        {
            [McpDescription("Name of the level to load. Case-sensitive.", Required = true)]
            public string Name { get; set; }
        }

        public class CreateLevelParams
        {
            [McpDescription("Optional level name. Defaults to 'LevelN' where N is the next number.")]
            public string Name { get; set; }
        }

        // ---------- Tools ----------

        [McpTool("Bekkoloco.QuickTile.GetStatus",
            "Return overview of the first Bekkoloco.QuickTilemapEditor found in the open scene: host GameObject, level count, current level, tile rule and game object rule counts.",
            "Get QuickTile editor status",
            Groups = new[] { k_Group })]
        public static object GetStatus(EmptyParams _)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var levels = GetField(editor, "levels") as IList;
            int currentIndex = GetFieldInt(editor, "currentLevelIndex", -1);
            var tileRules = GetField(editor, "tileRules") as IList;
            var goRules = GetField(editor, "gameObjectRules") as IList;

            string currentName = null;
            if (levels != null && currentIndex >= 0 && currentIndex < levels.Count)
                currentName = GetMemberString(levels[currentIndex], "levelName");

            return Response.Success(
                $"QuickTile editor on '{editor.gameObject.name}' — {levels?.Count ?? 0} level(s), {tileRules?.Count ?? 0} tile rule(s).",
                new
                {
                    gameObject = editor.gameObject.name,
                    levelCount = levels?.Count ?? 0,
                    currentLevelIndex = currentIndex,
                    currentLevelName = currentName,
                    tileRuleCount = tileRules?.Count ?? 0,
                    gameObjectRuleCount = goRules?.Count ?? 0
                });
        }

        [McpTool("Bekkoloco.QuickTile.ListLevels",
            "List all levels defined on the first Bekkoloco.QuickTilemapEditor in the scene.",
            "List QuickTile levels",
            Groups = new[] { k_Group })]
        public static object ListLevels(EmptyParams _)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var levels = GetField(editor, "levels") as IList;
            int currentIndex = GetFieldInt(editor, "currentLevelIndex", -1);

            var entries = new List<object>();
            if (levels != null)
            {
                for (int i = 0; i < levels.Count; i++)
                {
                    entries.Add(new
                    {
                        index = i,
                        name = GetMemberString(levels[i], "levelName"),
                        isCurrent = i == currentIndex
                    });
                }
            }

            return Response.Success($"{entries.Count} level(s).",
                new { levels = entries.ToArray(), currentIndex });
        }

        [McpTool("Bekkoloco.QuickTile.LoadLevel",
            "Load a level by its zero-based index on the first Bekkoloco.QuickTilemapEditor in the scene. Edit mode only.",
            "Load a QuickTile level",
            Groups = new[] { k_Group })]
        public static object LoadLevel(LoadLevelParams p)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var levels = GetField(editor, "levels") as IList;
            if (levels == null) return Response.Error("NO_LEVELS_FIELD");
            if (p.Index < 0 || p.Index >= levels.Count)
                return Response.Error("INVALID_INDEX", new { given = p.Index, count = levels.Count });

            try
            {
                InvokeLoadLevel(editor, p.Index);
                EditorUtility.SetDirty(editor);
                return Response.Success(
                    $"Loaded level {p.Index}: {GetMemberString(levels[p.Index], "levelName")}");
            }
            catch (Exception e)
            {
                return Response.Error("LOAD_FAILED", new { error = e.Message });
            }
        }

        [McpTool("Bekkoloco.QuickTile.LoadLevelByName",
            "Load a level by its name on the first Bekkoloco.QuickTilemapEditor in the scene. Edit mode only.",
            "Load a QuickTile level by name",
            Groups = new[] { k_Group })]
        public static object LoadLevelByName(LoadLevelByNameParams p)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var levels = GetField(editor, "levels") as IList;
            if (levels == null) return Response.Error("NO_LEVELS_FIELD");
            if (string.IsNullOrEmpty(p.Name)) return Response.Error("NAME_REQUIRED");

            int foundIndex = -1;
            for (int i = 0; i < levels.Count; i++)
            {
                if (GetMemberString(levels[i], "levelName") == p.Name) { foundIndex = i; break; }
            }
            if (foundIndex < 0) return Response.Error("NOT_FOUND", new { requested = p.Name });

            try
            {
                InvokeLoadLevel(editor, foundIndex);
                EditorUtility.SetDirty(editor);
                return Response.Success($"Loaded level '{p.Name}' (index {foundIndex}).",
                    new { index = foundIndex, name = p.Name });
            }
            catch (Exception e)
            {
                return Response.Error("LOAD_FAILED", new { error = e.Message });
            }
        }

        [McpTool("Bekkoloco.QuickTile.CreateLevel",
            "Create a new level on the first Bekkoloco.QuickTilemapEditor in the scene and switch to it. Returns the new index.",
            "Create a QuickTile level",
            Groups = new[] { k_Group })]
        public static object CreateLevel(CreateLevelParams p)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var type = editor.GetType();
            var levels = GetField(editor, "levels") as IList;
            if (levels == null) return Response.Error("NO_LEVELS_FIELD");

            var levelDataType = type.GetNestedType("LevelData")
                ?? Type.GetType("Bekkoloco.QuickTilemapEditor+LevelData, Assembly-CSharp");
            if (levelDataType == null) return Response.Error("LEVELDATA_TYPE_MISSING");

            object level;
            try { level = Activator.CreateInstance(levelDataType); }
            catch (Exception e) { return Response.Error("LEVELDATA_CTOR_FAILED", new { error = e.Message }); }

            string levelName = string.IsNullOrWhiteSpace(p.Name)
                ? $"Level{levels.Count + 1}"
                : p.Name.Trim();
            SetField(level, "levelName", levelName);
            TryInitListField(level, "properties", type, "LevelProperty");
            TryInitListField(level, "paintedTextures", type, "PaintedTextureData");
            SetField(level, "centerOriginToSurfaceMass", true);

            levels.Add(level);
            int newIndex = levels.Count - 1;

            try { InvokeLoadLevel(editor, newIndex); }
            catch { /* level was still added; still return success */ }
            EditorUtility.SetDirty(editor);

            return Response.Success($"Created level '{levelName}' at index {newIndex}.",
                new { index = newIndex, name = levelName });
        }

        [McpTool("Bekkoloco.QuickTile.SaveCurrentLevel",
            "Save the current level of the first Bekkoloco.QuickTilemapEditor to its JSON asset. Edit mode only.",
            "Save current QuickTile level",
            Groups = new[] { k_Group })]
        public static object SaveCurrentLevel(EmptyParams _)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var type = editor.GetType();
            var pathMethod = type.GetMethod("GetCurrentLevelSaveAssetPath");
            string path = pathMethod?.Invoke(editor, null) as string;
            if (string.IsNullOrEmpty(path)) return Response.Error("NO_SAVE_PATH",
                new { hint = "Ensure a level is selected and has a jsonFile reference." });

            var saveMethod = type.GetMethod("SaveTilemapToJson", new[] { typeof(string) });
            if (saveMethod == null) return Response.Error("SAVE_METHOD_MISSING");

            try
            {
                saveMethod.Invoke(editor, new object[] { path });
                AssetDatabase.Refresh();
                return Response.Success($"Saved current level to {path}", new { path });
            }
            catch (TargetInvocationException e)
            {
                return Response.Error("SAVE_FAILED", new { error = e.InnerException?.Message ?? e.Message });
            }
            catch (Exception e)
            {
                return Response.Error("SAVE_FAILED", new { error = e.Message });
            }
        }

        [McpTool("Bekkoloco.QuickTile.ListTileRules",
            "List tile rules (mesh layers) of the first Bekkoloco.QuickTilemapEditor in the scene. Each tile rule is one procedural/custom mesh layer with its own material, height, dig behaviour and deformer list. Use Bekkoloco.QuickTile.GetConcepts for a primer.",
            "List QuickTile tile rules",
            Groups = new[] { k_Group })]
        public static object ListTileRules(EmptyParams _)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var rules = GetField(editor, "tileRules") as IList;
            var entries = new List<object>();
            if (rules != null)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    if (rule == null) continue;

                    var deformers = GetField(rule, "deformerObjects") as IList;
                    var savedHandles = GetField(rule, "savedDeformerHandles") as IList;
                    var meshModeObj = GetField(rule, "meshMode");
                    string meshMode = meshModeObj?.ToString();

                    var tile = GetField(rule, "tile") as UnityEngine.Object;

                    entries.Add(new
                    {
                        index = i,
                        name = GetMemberString(rule, "ruleName") ?? $"Rule {i}",
                        tile = tile != null ? tile.name : null,
                        meshMode,
                        yOffset = GetMemberFloat(rule, "yOffset"),
                        sizeY = GetMemberFloat(rule, "sizeY"),
                        fixBase = GetMemberBool(rule, "fixBase"),
                        isVisible = GetMemberBool(rule, "isVisible"),
                        isDigLayer = GetMemberBool(rule, "isDigLayer"),
                        isDiggable = GetMemberBool(rule, "isDiggable"),
                        isUndiggable = GetMemberBool(rule, "isUndiggable"),
                        enableMove = GetMemberBool(rule, "enableMove"),
                        renderOrder = GetFieldInt(rule, "renderOrder"),
                        deformerCount = (deformers?.Count ?? 0) + (savedHandles?.Count ?? 0)
                    });
                }
            }
            return Response.Success($"{entries.Count} tile rule(s).",
                new { tileRules = entries.ToArray() });
        }

        [McpTool("Bekkoloco.QuickTile.ListGameObjectRules",
            "List GameObject (prefab placement) rules of the first Bekkoloco.QuickTilemapEditor in the scene. Each rule defines a prefab the user can paint onto the terrain (trees, rocks, props) with placement config.",
            "List QuickTile GameObject rules",
            Groups = new[] { k_Group })]
        public static object ListGameObjectRules(EmptyParams _)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var rules = GetField(editor, "gameObjectRules") as IList;
            var entries = new List<object>();
            if (rules != null)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    if (rule == null) continue;
                    var prefab = GetField(rule, "prefab") as UnityEngine.Object;
                    entries.Add(new
                    {
                        index = i,
                        id = GetMemberString(rule, "id"),
                        prefab = prefab != null ? prefab.name : null,
                        prefabResourcePath = GetMemberString(rule, "prefabResourcePath"),
                        placementSurface = GetField(rule, "placementSurface")?.ToString(),
                        yOffset = GetMemberFloat(rule, "yOffset"),
                        isVisible = GetMemberBool(rule, "isVisible"),
                        randomizeRotationY = GetMemberBool(rule, "randomizeRotationY"),
                        placeOnGround = GetMemberBool(rule, "placeOnGround"),
                        followDeformationY = GetMemberBool(rule, "followDeformationY"),
                        vegetationExclusionRadius = GetMemberFloat(rule, "vegetationExclusionRadius")
                    });
                }
            }
            return Response.Success($"{entries.Count} gameobject rule(s).",
                new { gameObjectRules = entries.ToArray() });
        }

        [McpTool("Bekkoloco.QuickTile.ListTexturePaintRules",
            "List TexturePaint rules of the first Bekkoloco.QuickTilemapEditor in the scene. Each rule is a material / texture set the user can paint onto the terrain top surface (albedo, normal, height, emission).",
            "List QuickTile texture paint rules",
            Groups = new[] { k_Group })]
        public static object ListTexturePaintRules(EmptyParams _)
        {
            var editor = FindEditor();
            if (editor == null) return NoEditorError();

            var rules = GetField(editor, "texturePaintRules") as IList;
            var entries = new List<object>();
            if (rules != null)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    if (rule == null) continue;
                    var mat = GetField(rule, "material") as UnityEngine.Object;
                    var albedo = GetField(rule, "albedo") as UnityEngine.Object;
                    entries.Add(new
                    {
                        index = i,
                        name = GetMemberString(rule, "ruleName") ?? $"Texture {i}",
                        material = mat != null ? mat.name : null,
                        albedo = albedo != null ? albedo.name : null,
                        textureScale = GetMemberFloat(rule, "textureScale"),
                        blendSharpness = GetMemberFloat(rule, "blendSharpness"),
                        noiseScale = GetMemberFloat(rule, "noiseScale"),
                        removeVegetation = GetMemberBool(rule, "removeVegetation")
                    });
                }
            }
            return Response.Success($"{entries.Count} texture paint rule(s).",
                new { texturePaintRules = entries.ToArray() });
        }

        [McpTool("Bekkoloco.QuickTile.GetConcepts",
            "Return a concise primer on the Bekkoloco QuickTile domain model: what levels, tile rules (layers), deformers, gameobject rules, texture paint rules and paths mean. Call this FIRST when you don't know the tool's domain.",
            "Explain QuickTile concepts",
            Groups = new[] { k_Group })]
        public static object GetConcepts(EmptyParams _)
        {
            return Response.Success("QuickTile domain model", new
            {
                summary = "BEKKOLOCO QuickTile is an editor that lets the user 'paint' 3D stylized terrain on Unity Tilemaps. It builds procedural meshes from tile rules, and lets the user drop prefabs and paint texture materials on top.",
                concepts = new[]
                {
                    new {
                        name = "Level",
                        fields = new[] { "levelName", "jsonFile", "properties", "paintedTextures" },
                        description = "A saved state of the whole tilemap. One editor holds a list of levels; only one is current at a time. A level serializes to a JSON TextAsset. Use ListLevels / LoadLevel / CreateLevel / SaveCurrentLevel."
                    },
                    new {
                        name = "Tile Rule (Layer)",
                        fields = new[] { "ruleName", "tile", "meshMode (Custom|Procedural)", "yOffset", "sizeY",
                            "fixBase", "isDigLayer", "isDiggable", "isUndiggable", "renderOrder", "deformerObjects" },
                        description = "One mesh layer. Users paint tiles into a layer; QuickTile then builds a procedural (or custom) mesh from those tiles. Dig Layers carve volume out of overlapping Diggable layers. Use ListTileRules."
                    },
                    new {
                        name = "Deformer",
                        fields = new[] { "GameObject with Bekkoloco.DOTS.RadialHillDeformer component", "handle positions (savedDeformerHandles)" },
                        description = "A point-and-radius gadget attached to a tile rule that pushes the procedural mesh up or down (hills / craters). Each tile rule owns a list of deformer objects; they only affect that rule's mesh."
                    },
                    new {
                        name = "GameObject Rule",
                        fields = new[] { "id (GUID)", "prefab", "placementSurface (Top|Skirt)", "yOffset",
                            "randomizeRotationY", "placeOnGround", "followDeformationY", "vegetationExclusionRadius" },
                        description = "A prefab-placement rule. Users 'paint' instances of a prefab (tree, rock, prop) onto the terrain. placementSurface decides whether it snaps to the top of the mesh or its side (skirt). Use ListGameObjectRules."
                    },
                    new {
                        name = "TexturePaint Rule",
                        fields = new[] { "ruleName", "material", "albedo", "normal", "height", "emission",
                            "textureScale", "blendSharpness", "noiseScale", "vegetationEntries" },
                        description = "A material / texture set for painting the terrain surface (mud, grass, sand…). Blended via shader on the top cap. Use ListTexturePaintRules."
                    },
                    new {
                        name = "Path",
                        fields = new[] { "points (Vector3Int list)", "trackPoints" },
                        description = "An ordered series of grid points used to generate spline-based meshes (roads, fences, tracks). Not yet exposed via MCP."
                    }
                },
                typicalWorkflow = new[]
                {
                    "1. GetStatus to confirm an editor exists in the scene.",
                    "2. ListLevels; LoadLevel / CreateLevel as needed.",
                    "3. ListTileRules to see the mesh layers; ListGameObjectRules for prefabs; ListTexturePaintRules for surface textures.",
                    "4. SaveCurrentLevel after any modification to persist to JSON."
                },
                notes = new[]
                {
                    "Editor is a MonoBehaviour in the scene (Bekkoloco.QuickTilemapEditor) — all tools target the first instance found.",
                    "Many operations are EditMode-only and will no-op in play mode.",
                    "The inspector has a toggle to expose or hide these tools from MCP clients."
                }
            });
        }

        // ---------- Reflection helpers ----------

        static object NoEditorError() =>
            Response.Error("NO_EDITOR_IN_SCENE",
                new { hint = "Open a scene containing a GameObject with a Bekkoloco.QuickTilemapEditor component." });

        static Component FindEditor()
        {
            var type = Type.GetType(k_EditorTypeName);
            if (type == null) return null;

            var all = Resources.FindObjectsOfTypeAll(type);
            foreach (var obj in all)
            {
                if (obj is Component c && c != null
                    && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded)
                    return c;
            }
            return null;
        }

        static void InvokeLoadLevel(Component editor, int index)
        {
            var type = editor.GetType();
            var buildDict = type.GetMethod("BuildTileDictionary");
            object tileDict = buildDict?.Invoke(editor, null);
            var loadLevel = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "LoadLevel" && m.GetParameters().Length == 2);
            if (loadLevel == null)
                throw new InvalidOperationException("LoadLevel(int, Dictionary) method not found.");
            loadLevel.Invoke(editor, new[] { (object)index, tileDict });
        }

        static object GetField(object target, string name)
        {
            if (target == null) return null;
            return target.GetType()
                .GetField(name, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(target);
        }

        static int GetFieldInt(object target, string name, int fallback = 0)
        {
            var v = GetField(target, name);
            return v is int i ? i : fallback;
        }

        static void SetField(object target, string name, object value)
        {
            target?.GetType()
                .GetField(name, BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue(target, value);
        }

        static string GetMemberString(object target, string name)
        {
            if (target == null) return null;
            var t = target.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(target)?.ToString();
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(target)?.ToString();
        }

        static float GetMemberFloat(object target, string name)
        {
            var v = GetField(target, name);
            return v is float f ? f : 0f;
        }

        static bool GetMemberBool(object target, string name)
        {
            var v = GetField(target, name);
            return v is bool b && b;
        }

        static void TryInitListField(object target, string fieldName, Type containingType, string elementTypeName)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return;
            if (field.GetValue(target) != null) return;

            var elementType = containingType.GetNestedType(elementTypeName)
                ?? Type.GetType($"Bekkoloco.QuickTilemapEditor+{elementTypeName}, Assembly-CSharp");
            if (elementType == null) return;

            var listType = typeof(List<>).MakeGenericType(elementType);
            field.SetValue(target, Activator.CreateInstance(listType));
        }
    }
}
