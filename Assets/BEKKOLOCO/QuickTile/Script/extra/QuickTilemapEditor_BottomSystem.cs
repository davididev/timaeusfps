// QuickTilemapEditor_BottomSystem.cs
// Adds per-rule “Bottom/Skirt” controls + hooks into existing SkirtManager
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        // UI presets for the underside shape
        public enum BottomShape { Cone, Rounded, Blob, Flat, CustomCurve }

        [System.Serializable]
        public class BottomSettings
        {
            public bool enabled = true;
            public BottomShape shape = BottomShape.Rounded;
            [Range(1, 20)] public int size = 8;                       // UX size 1..20
            public AnimationCurve profile = AnimationCurve.EaseInOut(0, 1, 1, 0); // t:0 top radius, t:1 bottom
            public Material material;                                 // (optionnel)
        }

        // ----- RUNTIME / EDITOR HOOKS -----

        /// Call when the rule changes (size/preset/curve) or when pressing "Add/Refresh"
        public void ApplyBottomAndRefresh(TileRule rule)
        {
            if (rule == null) return;
            var tm = rule.useCustomTilemap ? rule.customTargetTilemap : targetTilemap;
            if (tm == null) return;

            var skirt = EnsureSkirtManager(tm);
            if (skirt == null) return;

            // Map UI -> SkirtManager
            ApplyBottomSettingsToSkirt(skirt, rule);

            // Ask SkirtManager to update visuals/mesh (already used elsewhere in the project)
            // (SkirtManager exposes wallCount, WallStep, scaleValue, ApplyVisuals, Generate) :contentReference[oaicite:0]{index=0}
            skirt.ApplyVisuals();
            skirt.Generate();

#if UNITY_EDITOR
            EditorUtility.SetDirty(skirt);
            SceneView.RepaintAll();
#endif
        }

        private SkirtManager EnsureSkirtManager(Tilemap tm)
        {
            if (tm == null) return null;

            var existing = tm.GetComponentInChildren<SkirtManager>(true);
            if (existing != null) return existing;

            var go = new GameObject("Skirts");
            go.transform.SetParent(tm.transform, false);
            return go.AddComponent<SkirtManager>();
        }

        private void ApplyBottomSettingsToSkirt(SkirtManager skirt, TileRule rule)
        {
            // Height in “steps” for the wall stack. With Fixed Base we already compute this elsewhere. :contentReference[oaicite:1]{index=1}
            int count;
            float step = Mathf.Max(0.01f, skirt.WallStep);

            if (rule.fixBase)
            {
                // lock to base height (yOffset)
                count = Mathf.FloorToInt(rule.yOffset / step);
                skirt.wallCount = Mathf.Max(0, count);
                skirt.scaleValue = rule.yOffset * 10f; // keep current convention used in inspector hooks :contentReference[oaicite:2]{index=2}
            }
            else
            {
                // use rule.sizeY as “number of rings”
                count = Mathf.RoundToInt(Mathf.Max(0f, rule.sizeY));
                skirt.wallCount = Mathf.Max(0, count);

                // map UX size 1..20 to the same “rounded corner” scalar SkirtManager uses
                float t = Mathf.Clamp01(rule.bottom.size / 20f);
                switch (rule.bottom.shape)
                {
                    case BottomShape.Cone: t = 0f; break;                         // sharp
                    case BottomShape.Rounded: t = Mathf.Clamp01(t); break;           // round bevel
                    case BottomShape.Blob: t = Mathf.Clamp01(t * 1.25f); break;   // chunkier
                    case BottomShape.Flat: t = 0.5f; break;                        // small lip + flat cap
                    case BottomShape.CustomCurve: /* keep t as slider seed */ break;
                }

                // Reuse tileRule.roundedCorner convention for skirts already in code paths
                rule.roundedCorner = t;
                skirt.scaleValue = t;
            }
        }
    }
}
