using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        // NOTE: World-bounds driven sizing, legacy/uniform mapping (gridSize + maskOrigin),
        // rebuild only on paint/erase. Includes small-map helpers + adaptive chunks skeleton.

        /// <summary>
        /// Computes the real occupied bounds (tile space XZ) by merging: base tilemap,
        /// height tilemaps, custom tilemaps referenced by rules, and fallback to
        /// texturePaintMask keys if needed. Returns Bounds with size.x = width (tiles),
        /// size.z = height (tiles).
        /// </summary>
        private Bounds GetLevelWorldBounds()
        {
            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;

            void Consider(BoundsInt b)
            {
                if (b.size.x <= 0 || b.size.y <= 0) return;
                minX = Mathf.Min(minX, b.xMin);
                minY = Mathf.Min(minY, b.yMin);
                maxX = Mathf.Max(maxX, b.xMax - 1); // BoundsInt xMax/yMax are exclusive
                maxY = Mathf.Max(maxY, b.yMax - 1);
            }

            // 1) Base tilemap
            if (targetTilemap != null)
            {
                targetTilemap.CompressBounds();
                Consider(targetTilemap.cellBounds);
            }

            // 2) Height tilemaps
            if (heightTilemaps != null)
            {
                foreach (var tm in heightTilemaps.Values)
                {
                    if (!tm) continue;
                    tm.CompressBounds();
                    Consider(tm.cellBounds);
                }
            }

            // 3) Custom tilemaps from tile rules
            if (tileRules != null)
            {
                foreach (var r in tileRules)
                {
                    if (r == null) continue;
                    var tm = r.useCustomTilemap ? r.customTargetTilemap : null;
                    if (!tm) continue;
                    tm.CompressBounds();
                    Consider(tm.cellBounds);
                }
            }

            // 4) Painted cells fallback (when no tiles yet)
            if (texturePaintMask != null && texturePaintMask.Count > 0)
            {
                foreach (var p in texturePaintMask.Keys)
                {
                    minX = Mathf.Min(minX, p.x);
                    minY = Mathf.Min(minY, p.y);
                    maxX = Mathf.Max(maxX, p.x);
                    maxY = Mathf.Max(maxY, p.y);
                }
            }

            if (minX == int.MaxValue)
            {
                // Nothing found → fallback to current RT size for a sane default
                return new Bounds(Vector3.zero, new Vector3(Mathf.Max(1, gridSize.x), 0f, Mathf.Max(1, gridSize.y)));
            }

            Vector3 center = new Vector3((minX + maxX) * 0.5f, 0f, (minY + maxY) * 0.5f);
            Vector3 size = new Vector3(maxX - minX + 1, 0f, maxY - minY + 1);
            return new Bounds(center, size);
        }

        [SerializeField, Range(256, 8192)] private int maxFullLevelRT = 2048;

        // --- Full-level paint (Editor) ---
        [SerializeField] private bool updateFullLevelInEditor = true;   // ✅ active le rendu sur tout le level en éditeur
        [SerializeField] private Vector3Int levelRenderSize = new Vector3Int(256, 256, 1); // taille RT full-level
        private const int MinFullLevelRT = 256; // taille mini pour la RT full-level (évite les mip/artefacts)

        // ==== Options ====
        [SerializeField] private bool rebuildPreviewOnPaintOnly = true; // ✅ rebuild uniquement au paint/erase
        [SerializeField] private bool enableRuntimeSmallMapAutoRefresh = true; // tick auto runtime pour petites maps
        private const int MinMaskSize = 16; // ↓ réduit 64 → 16 pour permettre les petites grilles
        private const string PaintShaderFinalPerfect = "BEKKOLOCO/PaintShader_FinalPerfect";

        private static readonly int _ActiveTexIndexID = Shader.PropertyToID("_ActiveTexIndex");

        private static bool UsesPaintShaderFinalPerfect(Material material)
        {
            return material != null &&
                   material.shader != null &&
                   material.shader.name == PaintShaderFinalPerfect;
        }

        // ---- Adaptive chunks (Phase 3 skeleton) ----
        [Serializable]
        public class AdaptiveChunkSettings
        {
            public int minChunkSize = 8;
            public int maxChunkSize = 64;
            public bool forceUpdateSmallMaps = true;
        }
        [SerializeField] private AdaptiveChunkSettings adaptiveChunks = new AdaptiveChunkSettings();

        private int GetAdaptiveChunkSize()
        {
            int guess = Mathf.Max(adaptiveChunks.minChunkSize, Mathf.Min(adaptiveChunks.maxChunkSize, Mathf.Max(1, gridSize.x) / 4));
            return Mathf.Clamp(guess, adaptiveChunks.minChunkSize, adaptiveChunks.maxChunkSize);
        }
        private void UpdateWithAdaptiveChunks()
        {
            int chunk = GetAdaptiveChunkSize();
            // TODO: répartir le travail par zones chunk×chunk si/when nécessaire.
        }

        // Rebuild immédiat (appelé depuis Paint/Erase)
        private void RebuildPaintNow()
        {
            if (paintMaskTexture == null)
                CreateRenderTexture();

            UpdatePaintMaskTexture();
            UpdateBlendPreviewMaterial();
            PushPaintMaskGlobals();
        }

        // --- Helpers petites grilles ---
        public void ForceUpdateSmallGrid()
        {
            if (gridSize.x < 64 || gridSize.y < 64)
            {
                RebuildPaintNow();
                UpdatePaintMaskTexture(); // redondant mais sûr si maskOrigin/bounds ont changé
                try { StartCoroutine(DelayedRefreshAllSkirts()); }
                catch (System.Exception ex) { Debug.LogWarning($"[QuickTile] DelayedRefreshAllSkirts failed: {ex.Message}"); }
                try { DeformerSystemExtensions.ForceUpdateAllDeformers(); }
                catch (System.Exception ex) { Debug.LogWarning($"[QuickTile] ForceUpdateAllDeformers failed: {ex.Message}"); }
            }
        }

        /// <summary>Auto-refresh runtime petites cartes (tous les 10 frames).</summary>
        public void SmallMapRuntimeStep()
        {
            if (!Application.isPlaying || !enableRuntimeSmallMapAutoRefresh) return;
            if (gridSize.x >= 64 && gridSize.y >= 64) return;

            if (Time.frameCount % 10 == 0)
            {
                try { StartCoroutine(DelayedRefreshAllSkirts()); }
                catch (System.Exception ex) { Debug.LogWarning($"[QuickTile] Runtime skirt refresh failed: {ex.Message}"); }
                try { DeformerSystemExtensions.ForceUpdateAllDeformers(); }
                catch (System.Exception ex) { Debug.LogWarning($"[QuickTile] Runtime deformer update failed: {ex.Message}"); }
            }
        }

        //──────────────────────────────────────────────────────────────────────────────
        // PUBLIC API (used by other partials)
        //──────────────────────────────────────────────────────────────────────────────

        public void PaintTextureCell(Vector3Int cellPosition, int textureIndex)
        {
            if (textureIndex < 0 || textureIndex >= texturePaintRules.Count) return;

#if UNITY_EDITOR
            Undo.RecordObject(this, "Paint Texture");
#endif
            texturePaintMask[cellPosition] = textureIndex;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
            if (rebuildPreviewOnPaintOnly)
                RebuildPaintNow();
        }

        public void PaintTextureCell(Vector3Int cell, TexturePaintRule rule)
        {
            int index = texturePaintRules.IndexOf(rule);
            if (index < 0) return;

#if UNITY_EDITOR
            Undo.RecordObject(this, "Paint Texture");
#endif
            texturePaintMask[cell] = index;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
            if (rebuildPreviewOnPaintOnly)
                RebuildPaintNow();
        }

        public void PaintTextureCellToRenderTexture(Vector3Int cellPosition, int textureIndex)
        {
            if (paintMaskTexture == null)
                CreateRenderTexture();
            if (textureIndex < 0 || textureIndex >= texturePaintRules.Count)
                return;

            Bounds levelBounds = GetLevelWorldBounds();
            maskOrigin = new Vector2Int(Mathf.FloorToInt(levelBounds.min.x), Mathf.FloorToInt(levelBounds.min.z));

            Texture2D tempTex = new Texture2D(gridSize.x, gridSize.y, TextureFormat.RGBA32, false);
            try
            {
                tempTex.SetPixels(new Color[gridSize.x * gridSize.y]); // clear

                int px = cellPosition.x - maskOrigin.x;
                int py = cellPosition.y - maskOrigin.y;
                if (px >= 0 && px < gridSize.x && py >= 0 && py < gridSize.y)
                {
                    tempTex.SetPixel(px, py, Color.white);
                }
                tempTex.Apply();

                Graphics.Blit(tempTex, paintMaskTexture);
            }
            finally
            {
                SafeDestroy(tempTex);
            }

            PushPaintMaskGlobals();
            UpdateBlendPreviewMaterial();
        }

        public void EraseTextureCell(Vector3Int cellPosition)
        {
#if UNITY_EDITOR
            Undo.RecordObject(this, "Erase Texture");
#endif
            if (texturePaintMask.Remove(cellPosition))
            {
                if (rebuildPreviewOnPaintOnly)
                    RebuildPaintNow();
#if UNITY_EDITOR
                EditorUtility.SetDirty(this);
#endif
            }
        }

        public int GetPaintedTextureIndex(Vector3Int cellPosition)
        {
            return texturePaintMask.TryGetValue(cellPosition, out int idx) ? idx : -1;
        }

        public void SaveCurrentPaintDataToLevel()
        {
            if (levels == null || levels.Count == 0 || currentLevelIndex < 0 || currentLevelIndex >= levels.Count)
                return;

            var currentLevel = levels[currentLevelIndex];
            if (currentLevel.paintedTextures == null)
                currentLevel.paintedTextures = new List<PaintedTextureData>();

            currentLevel.paintedTextures.Clear();
            foreach (var entry in texturePaintMask)
            {
                currentLevel.paintedTextures.Add(new PaintedTextureData
                {
                    position = entry.Key,
                    textureIndex = entry.Value
                });
            }
#if UNITY_EDITOR
            if (texturePaintMask.Count > 0)
                Debug.Log($"💾 Saved {texturePaintMask.Count} painted cells → Level '{currentLevel.levelName}'");
            EditorUtility.SetDirty(this);
#endif
        }

        public void CreateRenderTexture()
        {
            // ✅ Simple : taille = gridSize (PAS * gridScale)
            CreateRenderTexture(gridSize.x, gridSize.y);
        }

        private void CreateRenderTexture(int width, int height)
        {
            if (paintMaskTexture != null)
            {
                paintMaskTexture.Release();

#if UNITY_EDITOR
                if (Application.isPlaying)
                    Destroy(paintMaskTexture);
                else
                    DestroyImmediate(paintMaskTexture);
#else
                Destroy(paintMaskTexture);
#endif
                paintMaskTexture = null;
            }

            paintMaskTexture = new RenderTexture(
                Mathf.Max(1, width),
                Mathf.Max(1, height),
                0,
                RenderTextureFormat.ARGB32
            )
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear // transitions douces
            };
            paintMaskTexture.Create();

#if UNITY_EDITOR
            Debug.Log($"🎨 RenderTexture créée : {width}x{height} tiles (gridScale={gridScale})");
#endif

            PushPaintMaskGlobals();
        }

        //──────────────────────────────────────────────────────────────────────────────
        // CORE REBUILD / MAPPING
        //──────────────────────────────────────────────────────────────────────────────

        // Legacy/uniform mapping + préservation textures – SANS rebuild auto en Edit Mode
        public void RebuildPaintMaskAndMaterials()
        {
            if (Application.isPlaying || !rebuildPreviewOnPaintOnly)
                RebuildPaintNow();

            if (texturePaintRules == null || texturePaintRules.Count == 0)
                return;

            // Base stays on _Tex0. Texture paint rules map to _Tex1/_Tex2/_Tex3.
            Texture tex0Fallback = GetResolvedTexturePaintBaseTexture();
            float tex0Scale = GetResolvedTexturePaintBaseTextureScale();
            Texture tex1Fallback = (texturePaintRules.Count > 0) ? GetTexturePaintRuleTextureOrBase(texturePaintRules[0], tex0Fallback) : tex0Fallback;
            Texture tex2Fallback = (texturePaintRules.Count > 1) ? GetTexturePaintRuleTextureOrBase(texturePaintRules[1], tex0Fallback) : tex0Fallback;
            Texture tex3Fallback = (texturePaintRules.Count > 2) ? GetTexturePaintRuleTextureOrBase(texturePaintRules[2], tex0Fallback) : tex0Fallback;

            Vector2 worldToMaskScale = new Vector2(1f / Mathf.Max(1, gridSize.x), 1f / Mathf.Max(1, gridSize.y));
            Vector2 worldToMaskOffset = new Vector2(0.5f, 0.5f);

            foreach (var rule in texturePaintRules)
            {
                var mat = rule?.material;
                if (!UsesPaintShaderFinalPerfect(mat)) continue;

                // _Tex0 = base → on ne touche pas (l'utilisateur la gère dans le material)
                // _Tex1.._Tex3 = rules → toujours push depuis les texturePaintRules
                mat.SetTexture("_Tex1", tex1Fallback);
                mat.SetTextureScale("_Tex1", Vector2.one * ((texturePaintRules.Count > 0) ? GetTexturePaintRuleScaleOrBase(texturePaintRules[0], tex0Scale) : tex0Scale));
                mat.SetTexture("_Tex2", tex2Fallback);
                mat.SetTextureScale("_Tex2", Vector2.one * ((texturePaintRules.Count > 1) ? GetTexturePaintRuleScaleOrBase(texturePaintRules[1], tex0Scale) : tex0Scale));
                if (mat.HasProperty("_Tex3"))
                {
                    mat.SetTexture("_Tex3", tex3Fallback);
                    mat.SetTextureScale("_Tex3", Vector2.one * ((texturePaintRules.Count > 2) ? GetTexturePaintRuleScaleOrBase(texturePaintRules[2], tex0Scale) : tex0Scale));
                }

                mat.SetVector("_WorldToMaskScale", worldToMaskScale);
                mat.SetVector("_WorldToMaskOffset", worldToMaskOffset);

                // Push emission textures
                if (rule.emission != null)
                    mat.SetTexture("_Emission", rule.emission);

                // Push emission color
                mat.SetColor("_EmissionColor", rule.emissionColor);

                // Push texture scale (float → uniform Vector4)
                mat.SetFloat("_TexScale", rule.textureScale);
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
#endif
        }

        // ======== UPDATED: RGBA one-hot (4 couches) ========
        public void UpdatePaintMaskTexture()
        {
            // Si rien à peindre → clear et push
            if (texturePaintMask == null || texturePaintMask.Count == 0)
            {
                if (paintMaskTexture == null)
                    CreateRenderTexture(updateFullLevelInEditor ? Mathf.Max(MinFullLevelRT, gridSize.x) : gridSize.x,
                                        updateFullLevelInEditor ? Mathf.Max(MinFullLevelRT, gridSize.y) : gridSize.y);

                Graphics.Blit(Texture2D.blackTexture, paintMaskTexture);
                PushPaintMaskGlobals();
                UpdateBlendPreviewMaterial();
                return;
            }

            // Bounds réels du level
            Bounds levelBounds = GetLevelWorldBounds();
            int levelW = Mathf.CeilToInt(levelBounds.size.x);
            int levelH = Mathf.CeilToInt(levelBounds.size.z);

            // AABB du paintmask
            int minX = texturePaintMask.Keys.Min(c => c.x);
            int minY = texturePaintMask.Keys.Min(c => c.y);
            int maxX = texturePaintMask.Keys.Max(c => c.x);
            int maxY = texturePaintMask.Keys.Max(c => c.y);
            int needW = maxX - minX + 1;
            int needH = maxY - minY + 1;

            bool useFullLevel = updateFullLevelInEditor && !Application.isPlaying;
            maskOrigin = new Vector2Int(Mathf.FloorToInt(levelBounds.min.x), Mathf.FloorToInt(levelBounds.min.z));

            // Taille cible en TUILES (pas liée à gridScale)
            int baseW = useFullLevel ? Mathf.Max(needW, levelW) : needW;
            int baseH = useFullLevel ? Mathf.Max(needH, levelH) : needH;

            int targetW = useFullLevel
                ? Mathf.NextPowerOfTwo(Mathf.Max(MinFullLevelRT, baseW))
                : Mathf.NextPowerOfTwo(Mathf.Max(MinMaskSize, baseW));
            int targetH = useFullLevel
                ? Mathf.NextPowerOfTwo(Mathf.Max(MinFullLevelRT, baseH))
                : Mathf.NextPowerOfTwo(Mathf.Max(MinMaskSize, baseH));

            if (useFullLevel)
            {
                targetW = Mathf.Min(targetW, maxFullLevelRT);
                targetH = Mathf.Min(targetH, maxFullLevelRT);
            }

            bool needAlloc = paintMaskTexture == null ||
                             targetW != paintMaskTexture.width ||
                             targetH != paintMaskTexture.height;

            if (needAlloc)
            {
                if (useFullLevel)
                {
                    levelRenderSize = new Vector3Int(targetW, targetH, 1);
                    CreateRenderTexture(levelRenderSize.x, levelRenderSize.y);
#if UNITY_EDITOR
                    Debug.Log($"🗺️ Full-level RT resize → {targetW}x{targetH} tiles");
#endif
                }
                else
                {
                    CreateRenderTexture(targetW, targetH);
#if UNITY_EDITOR
                    Debug.Log($"🎨 Grid RT resize → {targetW}x{targetH} tiles");
#endif
                }
            }

            // 1 pixel = 1 tuile, RGBA one-hot : 0→R, 1→G, 2→B, 3→A
            int texW = paintMaskTexture.width;
            int texH = paintMaskTexture.height;

            Texture2D tmp = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
            tmp.SetPixels(new Color[texW * texH]); // Clear (0,0,0,0)

            foreach (var kvp in texturePaintMask)
            {
                int px = kvp.Key.x - maskOrigin.x;
                int py = kvp.Key.y - maskOrigin.y;
                if ((uint)px >= (uint)texW || (uint)py >= (uint)texH) continue;

                int ti = kvp.Value; // 0..3
                Color c = Color.clear;
                switch (ti)
                {
                    case 0: c = new Color(1, 0, 0, 0); break; // R
                    case 1: c = new Color(0, 1, 0, 0); break; // G
                    case 2: c = new Color(0, 0, 1, 0); break; // B
                    case 3: c = new Color(0, 0, 0, 1); break; // A
                    default: c = Color.clear; break;
                }
                tmp.SetPixel(px, py, c);
            }

            tmp.Apply();
            Graphics.Blit(tmp, paintMaskTexture);
            SafeDestroy(tmp);

            PushPaintMaskGlobals();
            UpdateBlendPreviewMaterial();
        }

        public void UpdateBlendPreviewMaterial()
        {
            if (texturePaintRules == null || texturePaintRules.Count == 0) return;

            if (blendPreviewMaterial == null)
            {
                var shader = Shader.Find("BEKKOLOCO/PaintShader_FinalPerfect");
                if (shader == null) { Debug.LogError("❌ Shader 'BEKKOLOCO/PaintShader_FinalPerfect' not found."); return; }
                blendPreviewMaterial = new Material(shader);
            }

            // Masque
            blendPreviewMaterial.SetTexture("_MaskTex", paintMaskTexture);

            Texture baseTexture = GetResolvedTexturePaintBaseTexture();
            float baseTextureScale = GetResolvedTexturePaintBaseTextureScale();

            blendPreviewMaterial.SetTexture("_Tex0", baseTexture);
            blendPreviewMaterial.SetTexture("_Tex1",
                (texturePaintRules.Count > 0) ? GetTexturePaintRuleTextureOrBase(texturePaintRules[0], baseTexture) : baseTexture);
            blendPreviewMaterial.SetTexture("_Tex2",
                (texturePaintRules.Count > 1) ? GetTexturePaintRuleTextureOrBase(texturePaintRules[1], baseTexture) : baseTexture);
            blendPreviewMaterial.SetTexture("_Tex3",
                (texturePaintRules.Count > 2) ? GetTexturePaintRuleTextureOrBase(texturePaintRules[2], baseTexture) : baseTexture);

            // Emission textures: base on _Emission0, rules → _Emission1.._Emission3
            if (blendPreviewMaterial.HasProperty("_Emission0"))
                blendPreviewMaterial.SetTexture("_Emission0", Texture2D.blackTexture);
            for (int i = 0; i < 3; i++)
            {
                int slot = i + 1;
                string emissionProp = $"_Emission{slot}";
                if (!blendPreviewMaterial.HasProperty(emissionProp)) continue;
                var emissionTex = (i < texturePaintRules.Count && texturePaintRules[i].emission)
                    ? texturePaintRules[i].emission
                    : Texture2D.blackTexture;
                blendPreviewMaterial.SetTexture(emissionProp, emissionTex);
            }

            // Emission colors: base on _EmissionColor0, rules → _EmissionColor1.._EmissionColor3
            if (blendPreviewMaterial.HasProperty("_EmissionColor0"))
                blendPreviewMaterial.SetColor("_EmissionColor0", Color.black);
            for (int i = 0; i < 3; i++)
            {
                int slot = i + 1;
                string emissionColorProp = $"_EmissionColor{slot}";
                if (!blendPreviewMaterial.HasProperty(emissionColorProp)) continue;
                var emissionColor = (i < texturePaintRules.Count)
                    ? texturePaintRules[i].emissionColor
                    : Color.black;
                blendPreviewMaterial.SetColor(emissionColorProp, emissionColor);
            }

            // Base scale on _TexScale0, painted rules on _TexScale1.._TexScale3.
            blendPreviewMaterial.SetFloat("_TexScale0", baseTextureScale);
            blendPreviewMaterial.SetFloat("_TexScale1",
                (texturePaintRules.Count > 0) ? GetTexturePaintRuleScaleOrBase(texturePaintRules[0], baseTextureScale) : baseTextureScale);
            blendPreviewMaterial.SetFloat("_TexScale2",
                (texturePaintRules.Count > 1) ? GetTexturePaintRuleScaleOrBase(texturePaintRules[1], baseTextureScale) : baseTextureScale);
            blendPreviewMaterial.SetFloat("_TexScale3",
                (texturePaintRules.Count > 2) ? GetTexturePaintRuleScaleOrBase(texturePaintRules[2], baseTextureScale) : baseTextureScale);

            // Per-painted-texture blend/noise controls (_BlendSharpness1.. / _NoiseScale1..)
            for (int i = 0; i < texturePaintRules.Count; i++)
            {
                int slotIndex = i + 1;
                string blendProp = $"_BlendSharpness{slotIndex}";
                string noiseProp = $"_NoiseScale{slotIndex}";

                if (blendPreviewMaterial.HasProperty(blendProp))
                    blendPreviewMaterial.SetFloat(blendProp, texturePaintRules[i].blendSharpness);
                if (blendPreviewMaterial.HasProperty(noiseProp))
                    blendPreviewMaterial.SetFloat(noiseProp, texturePaintRules[i].noiseScale);
            }

            if (texturePaintRules.Count > 0)
            {
                if (blendPreviewMaterial.HasProperty("_BlendSharpness"))
                    blendPreviewMaterial.SetFloat("_BlendSharpness", texturePaintRules[0].blendSharpness);
                if (blendPreviewMaterial.HasProperty("_NoiseScale"))
                    blendPreviewMaterial.SetFloat("_NoiseScale", texturePaintRules[0].noiseScale);
            }

            // Push noise type → blendPreviewMaterial
            for (int i = 0; i < texturePaintRules.Count; i++)
            {
                int slotIdx = i + 1;
                string noiseTypeProp = $"_NoiseType{slotIdx}";
                if (blendPreviewMaterial.HasProperty(noiseTypeProp))
                    blendPreviewMaterial.SetInt(noiseTypeProp, texturePaintRules[i].noiseType);
            }
            if (texturePaintRules.Count > 0 && blendPreviewMaterial.HasProperty("_NoiseType"))
                blendPreviewMaterial.SetInt("_NoiseType", texturePaintRules[0].noiseType);

            // Mapping monde -> masque
            Vector2 worldToMaskScale = new Vector2(1f / gridSize.x, 1f / gridSize.y);
            Vector2 worldToMaskOffset = new Vector2(0.5f, 0.5f);
            blendPreviewMaterial.SetVector("_WorldToMaskScale", worldToMaskScale);
            blendPreviewMaterial.SetVector("_WorldToMaskOffset", new Vector4(worldToMaskOffset.x, worldToMaskOffset.y, 0, 0));

            // Index actif → preview + matériaux des règles
            int activeIdx = (selectedTextureRule != null) ? texturePaintRules.IndexOf(selectedTextureRule) : 0;
            if (activeIdx < 0) activeIdx = 0;
            blendPreviewMaterial.SetInt(_ActiveTexIndexID, activeIdx);

            foreach (var rule in texturePaintRules)
            {
                if (!UsesPaintShaderFinalPerfect(rule.material)) continue;
                rule.material.SetTexture("_MaskTex", paintMaskTexture);
                rule.material.SetVector("_WorldToMaskScale", new Vector2(1f / gridSize.x, 1f / gridSize.y));
                rule.material.SetVector("_WorldToMaskOffset", new Vector2(0.5f, 0.5f));
                rule.material.SetInt(_ActiveTexIndexID, activeIdx);

                // Push toutes les textures albedo + scale des règles → _Tex1.._Tex3
                for (int i = 0; i < texturePaintRules.Count && i < 3; i++)
                {
                    int slotIndex = i + 1;
                    string texProp = $"_Tex{slotIndex}";
                    if (rule.material.HasProperty(texProp))
                    {
                        var tex = (texturePaintRules[i].albedo != null)
                            ? (Texture)texturePaintRules[i].albedo
                            : baseTexture;
                        rule.material.SetTexture(texProp, tex);
                        rule.material.SetTextureScale(texProp,
                            Vector2.one * GetTexturePaintRuleScaleOrBase(texturePaintRules[i], baseTextureScale));
                    }
                    string scaleProp = $"_TexScale{slotIndex}";
                    if (rule.material.HasProperty(scaleProp))
                        rule.material.SetFloat(scaleProp, GetTexturePaintRuleScaleOrBase(texturePaintRules[i], baseTextureScale));
                }

                for (int i = 0; i < texturePaintRules.Count; i++)
                {
                    int slotIndex = i + 1;
                    string blendProp = $"_BlendSharpness{slotIndex}";
                    string noiseProp = $"_NoiseScale{slotIndex}";

                    if (rule.material.HasProperty(blendProp))
                        rule.material.SetFloat(blendProp, texturePaintRules[i].blendSharpness);
                    if (rule.material.HasProperty(noiseProp))
                        rule.material.SetFloat(noiseProp, texturePaintRules[i].noiseScale);
                }

                if (texturePaintRules.Count > 0)
                {
                    if (rule.material.HasProperty("_BlendSharpness"))
                        rule.material.SetFloat("_BlendSharpness", texturePaintRules[0].blendSharpness);
                    if (rule.material.HasProperty("_NoiseScale"))
                        rule.material.SetFloat("_NoiseScale", texturePaintRules[0].noiseScale);
                }

                // Push noise type to rule materials
                for (int i = 0; i < texturePaintRules.Count; i++)
                {
                    int slotIdx = i + 1;
                    string noiseTypeProp = $"_NoiseType{slotIdx}";
                    if (rule.material.HasProperty(noiseTypeProp))
                        rule.material.SetInt(noiseTypeProp, texturePaintRules[i].noiseType);
                }
                if (texturePaintRules.Count > 0 && rule.material.HasProperty("_NoiseType"))
                    rule.material.SetInt("_NoiseType", texturePaintRules[0].noiseType);
            }
        }

        public bool Debug_LegacyMaskMapping = false; // optional switch-back for testing

        private Vector2 GetPaintMaskWorldToMaskScale(int texW, int texH)
        {
            float scaleFactor = Mathf.Max(1, gridScale);
            return new Vector2(
                1f / (Mathf.Max(1, texW) * scaleFactor),
                1f / (Mathf.Max(1, texH) * scaleFactor)
            );
        }

        private Vector2 GetPaintMaskWorldToMaskOffset(int texW, int texH, bool includeLevelOffset = true)
        {
            texW = Mathf.Max(1, texW);
            texH = Mathf.Max(1, texH);

            Vector2 offset = new Vector2(
                -maskOrigin.x / (float)texW,
                -maskOrigin.y / (float)texH
            );

            // Dual-grid procedural tiles are positioned at (cell - 0.5),
            // so the paint mask must be shifted by one texel to keep the
            // painted cell centered on the rendered surface.
            if (!Debug_LegacyMaskMapping && IsDualGrid())
            {
                offset.x += 1f / texW;
                offset.y += 1f / texH;
            }

            if (includeLevelOffset && centerOriginToSurfaceMass && targetTilemap != null && targetTilemap.transform.parent != null)
            {
                float scaleFactor = Mathf.Max(1, gridScale);
                Vector3 levelOffset = targetTilemap.transform.parent.position;
                offset.x -= (levelOffset.x / scaleFactor) / texW;
                offset.y -= (levelOffset.z / scaleFactor) / texH;
            }

            return offset;
        }

        public void PushPaintMaskGlobals()
        {
            if (paintMaskTexture == null) return;

            // ✅ Bilinear pour transitions douces
            paintMaskTexture.filterMode = FilterMode.Bilinear;

            int texW = Mathf.Max(1, paintMaskTexture.width);
            int texH = Mathf.Max(1, paintMaskTexture.height);
            Vector2 scale = GetPaintMaskWorldToMaskScale(texW, texH);
            Vector2 offset = GetPaintMaskWorldToMaskOffset(texW, texH);

            Shader.SetGlobalTexture("_QT_PaintMask", paintMaskTexture);
            Shader.SetGlobalVector("_QT_WorldToMaskScale", scale);
            Shader.SetGlobalVector("_QT_WorldToMaskOffset", offset);

#if UNITY_EDITOR
         //   Debug.Log($"🎨 GLOBALS (Bilinear) → Scale:{scale} Offset:{offset} tex:{texW}x{texH} gridScale:{scaleFactor} maskOrigin:{maskOrigin}");
#endif
        }

        public Texture2D GetResolvedTexturePaintBaseTexture2D()
        {
            return GetResolvedTexturePaintBaseTexture() as Texture2D;
        }

        public float GetResolvedTexturePaintBaseTextureScale()
        {
            if (texturePaintRules != null)
            {
                foreach (var rule in texturePaintRules)
                {
                    var material = rule?.material;
                    if (!UsesPaintShaderFinalPerfect(material) || !material.HasProperty("_Tex0"))
                        continue;

                    float baseTextureScale = material.GetTextureScale("_Tex0").x;
                    if (baseTextureScale > 0f)
                        return baseTextureScale;
                }
            }

            return 1f;
        }

        public Texture GetResolvedTexturePaintBaseTexture()
        {
            if (texturePaintRules != null)
            {
                foreach (var rule in texturePaintRules)
                {
                    var material = rule?.material;
                    if (!UsesPaintShaderFinalPerfect(material) || !material.HasProperty("_Tex0"))
                        continue;

                    var materialBaseTexture = material.GetTexture("_Tex0");
                    if (materialBaseTexture != null)
                        return materialBaseTexture;
                }
            }

            return Texture2D.grayTexture;
        }

        private Texture GetTexturePaintRuleTextureOrBase(TexturePaintRule rule, Texture baseTexture)
        {
            if (rule != null && rule.albedo != null)
                return rule.albedo;

            return baseTexture != null ? baseTexture : Texture2D.grayTexture;
        }

        private float GetTexturePaintRuleScaleOrBase(TexturePaintRule rule, float baseScale)
        {
            if (rule != null && rule.albedo != null)
                return Mathf.Max(0.1f, rule.textureScale);

            return Mathf.Max(0.1f, baseScale);
        }
    }
}
