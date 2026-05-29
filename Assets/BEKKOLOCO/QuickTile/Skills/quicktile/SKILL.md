---
name: quicktile
description: >
  Drive the BEKKOLOCO QuickTile Unity editor asset via Unity MCP tools
  (Bekkoloco_QuickTile_*). Covers levels, tile rules (mesh layers),
  deformers, GameObject rules (prefab placement), texture paint rules,
  and paths. Use when the user mentions: "quicktile", "tile rule",
  "tilemap editor", "niveau/level quicktile", "deformer", "dig layer",
  "diggable", "mesh layer", "BEKKOLOCO", "procedural tile mesh",
  "paint texture on tilemap", or asks Claude to inspect/modify a
  QuickTilemapEditor component in the scene.
user-invokable: true
argument-hint: "[status|list|explain|save|load <index>|create <name>]"
---

# QuickTile skill

Drives the BEKKOLOCO QuickTile asset in Unity via Unity MCP.
The user's project lives at
`/Users/richy/Documents/UNITY/ASSET STORE/Assets/BEKKOLOCO`.

## When this skill applies

Trigger whenever the user wants to *inspect* or *modify* the QuickTile
tilemap editor in their open Unity scene. Common phrases:

- "what's in my tilemap"
- "list the levels / tile rules / prefabs"
- "create a new level called X"
- "load level Y"
- "save"
- "what does this tile rule do"
- "qu'est-ce qu'un deformer / layer / dig layer"

Do **not** trigger for generic Unity questions (scene management,
scripting) — those belong to the regular Unity MCP tools
(`Unity_ManageScene`, `Unity_ReadConsole`, etc.).

## Prerequisites

1. `com.unity.ai.assistant` installed in the Unity project.
2. Unity Editor open, with the project's MCP bridge running
   (green dot in `Project Settings > AI > Unity MCP Server`).
3. QuickTile MCP tools enabled — the asset has an inspector panel at
   the top of `QuickTilemapEditor` with an **Enable QuickTile tools**
   toggle. If the user says the tools aren't showing, ask them to
   check that toggle (or run the menu `Bekkoloco > QuickTile > Setup
   MCP Tools`).

If any of those preconditions fail, **stop** and tell the user what's
missing — don't guess.

## Available tools

All tools are `Bekkoloco_QuickTile_*` (MCP sanitisation replaces dots
with underscores). They act on the **first** `Bekkoloco.QuickTilemapEditor`
MonoBehaviour found in the currently open scene.

### Read-only

| Tool | Returns |
|---|---|
| `Bekkoloco_QuickTile_GetConcepts` | Domain primer (Level, TileRule, Deformer, GameObjectRule, TexturePaintRule, Path). Call this first if unfamiliar. |
| `Bekkoloco_QuickTile_GetStatus` | Host GameObject name, level/rule/gameobject counts, current level index + name. |
| `Bekkoloco_QuickTile_ListLevels` | All levels with index, name, isCurrent. |
| `Bekkoloco_QuickTile_ListTileRules` | Mesh layers with name, tile, meshMode, yOffset, sizeY, isDigLayer, isDiggable, deformerCount, renderOrder, etc. |
| `Bekkoloco_QuickTile_ListGameObjectRules` | Prefab placement rules (prefab, placementSurface Top/Skirt, yOffset, rotation flags). |
| `Bekkoloco_QuickTile_ListTexturePaintRules` | Surface texture materials with scale, blend, albedo, noise. |

### Mutating (EditMode only — never call in Play mode)

| Tool | Params | Effect |
|---|---|---|
| `Bekkoloco_QuickTile_LoadLevel` | `Index` (int) | Switches current level. |
| `Bekkoloco_QuickTile_LoadLevelByName` | `Name` (string) | Same, by case-sensitive name. |
| `Bekkoloco_QuickTile_CreateLevel` | `Name` (string, optional) | Creates a new level and switches to it. |
| `Bekkoloco_QuickTile_SaveCurrentLevel` | — | Writes current level to its JSON TextAsset. |

## Domain primer (concise)

Rather than re-explaining, call `Bekkoloco_QuickTile_GetConcepts` once
per session — it returns a structured JSON with `summary`, `concepts[]`,
`typicalWorkflow`, and `notes`. Quote the relevant parts back to the
user only if they ask.

Key mental model:

- **Editor** = one `QuickTilemapEditor` MonoBehaviour per scene.
- **Level** = saved state (JSON). Editor holds N levels, one current.
- **Tile Rule** = one mesh layer (procedural or custom). Has Y offset,
  height, dig flags, and a list of **deformers** (RadialHillDeformer
  GameObjects that push the mesh up/down).
- **Dig Layer** = tile rule that *carves* volume out of overlapping
  Diggable layers. `isUndiggable = true` opts a layer out entirely.
- **GameObject Rule** = a paintable prefab with placement config.
- **TexturePaint Rule** = a material set (albedo/normal/height) painted
  on the top surface of the terrain.
- **Path** = grid-point sequence that generates a spline mesh. Not
  exposed via MCP yet — if the user asks, say so.

## Typical workflow

```
1. Bekkoloco_QuickTile_GetStatus   → confirm editor is in scene
2. Bekkoloco_QuickTile_GetConcepts → (once per session if unfamiliar)
3. Bekkoloco_QuickTile_ListLevels / ListTileRules / …
4. modify (LoadLevel, CreateLevel, …)
5. Bekkoloco_QuickTile_SaveCurrentLevel  ← always save after mutations
```

## Guidelines

- **Always GetStatus first** on a new session. If `found=false`, the
  scene has no editor; tell the user to open a scene with one.
- **Never call mutating tools in Play mode** — they will no-op.
  If the user is in Play mode, ask them to exit first.
- **Save is not automatic.** After any `LoadLevel`/`CreateLevel` and
  the user's subsequent edits, remind them to
  `Bekkoloco_QuickTile_SaveCurrentLevel` or it'll be lost on reload.
- **Don't invent tools.** If the user asks for something not in the
  table above (e.g. "paint a tile at position X"), say it's not
  exposed and suggest they do it manually in Unity. The MCP surface
  is intentionally small.
- **Match the user's language.** The user writes in French —
  respond in French too, but keep tool names English (they're the
  actual MCP identifiers).

## Arguments

When invoked as `/quicktile <arg>`:

| Arg | Action |
|---|---|
| `status` | Call GetStatus and summarise in one sentence. |
| `list` | Call ListLevels + ListTileRules + ListGameObjectRules and show a compact table. |
| `explain` | Call GetConcepts and render a short French summary. |
| `save` | Call SaveCurrentLevel. |
| `load <index>` | Call LoadLevel with that index. |
| `create <name>` | Call CreateLevel with that name. |

With no arg, assume the user wants a `status` + `list` overview.

## Failure handling

If a tool returns `{ "success": false, "code": "..." }`, don't retry
blindly. Surface the `code` and `data.hint` to the user in plain
language. Common codes:

- `NO_EDITOR_IN_SCENE` → open a scene with a QuickTilemapEditor.
- `INVALID_INDEX` → the index you picked is out of range; list levels
  again.
- `NO_SAVE_PATH` → the current level has no `jsonFile` asset yet;
  the user has to save-as in the inspector first.

If a tool simply isn't available in the MCP server's list, tell the
user to flip the **Enable QuickTile tools** toggle in the inspector
(top of the QuickTilemapEditor component).
