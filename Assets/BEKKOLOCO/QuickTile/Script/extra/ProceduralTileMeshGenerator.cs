// ProceduralTileMeshGenerator.cs
// Port of the QuickTexture HTML 3D dual-grid tile generator (App.tsx) to Unity C#.
// Generates the same 4 tile shapes procedurally: Full, Corner, Edge, InnerCorner.
// Parametric radius (rounded corners) and depth (thickness).
// Skirt = curved overhang on exterior edges for natural terrain look.

using UnityEngine;
using System.Collections.Generic;

namespace Bekkoloco
{
    /// <summary>
    /// The 4 procedural tile types from the dual-grid system.
    /// Matches the HTML version's TileType exactly.
    /// </summary>
    public enum ProceduralTileType
    {
        Full,
        Corner,
        Edge,
        InnerCorner
    }

    /// <summary>
    /// Result of the dual-grid tile mapping (16-case lookup).
    /// null means empty cell.
    /// </summary>
    public class DualGridTileResult
    {
        public ProceduralTileType type;
        public float rotationDeg;       // rotation in degrees (Y axis in Unity)
        public bool isDiagonal;          // two Corner meshes at 180° (cases 6 & 9)
    }

    /// <summary>
    /// Bottom cap mode for procedural tiles.
    /// </summary>
    public enum BottomMode
    {
        [InspectorName("None")]
        None,           // No bottom cap (see-through from below)
        [InspectorName("Flat")]
        Flat,           // Flat cap at Y=0
        [InspectorName("Bevel")]
        Bevel,          // Beveled/rounded inward cap at Y=0
        [InspectorName("Rocky Island")]
        IslandNoise     // Rocky floating-island underside using noise displacement
    }

    public enum BevelProfile
    {
        [InspectorName("Convexe")]
        Convex,         // Bulges outward (rounded)
        [InspectorName("Concave")]
        Concave         // Curves inward (scoop)
    }

    public enum SkirtMaterialMode
    {
        [InspectorName("Current Skirt Material")]
        CurrentSkirtMaterial,
        [InspectorName("Use Floor Material + Mask")]
        UseFloorMaterialWithMask
    }

    /// <summary>
    /// Generates procedural tile meshes matching the QuickTexture HTML version.
    /// Top cap + exterior side walls + optional skirt (curved overhang) + optional bottom cap.
    /// </summary>
    public static class ProceduralTileMeshGenerator
    {
        private static readonly float[] LegacyDefaultSkirtMaskTimes = { 0f, 0.22f, 0.5f, 0.78f, 1f };
        private static readonly float[] LegacyDefaultSkirtMaskValues = { 0.78f, 0.72f, 0.61f, 0.7f, 0.76f };

        // ─────────────────────────────────────────────
        // Dual-Grid Tile Mapping (16 cases, 4-bit neighbor mask)
        // Same as TILE_MAPPING in App.tsx
        // Key = 4-bit mask: tl(8) | tr(4) | bl(2) | br(1) — matches HTML App.tsx
        // ─────────────────────────────────────────────

        private static readonly DualGridTileResult[] TileMapping = new DualGridTileResult[16]
        {
            /* 0  */ null,
            /* 1  */ new DualGridTileResult { type = ProceduralTileType.Corner,      rotationDeg = 0f,    isDiagonal = false },
            /* 2  */ new DualGridTileResult { type = ProceduralTileType.Corner,      rotationDeg = -90f,  isDiagonal = false },
            /* 3  */ new DualGridTileResult { type = ProceduralTileType.Edge,        rotationDeg = -90f,  isDiagonal = false },
            /* 4  */ new DualGridTileResult { type = ProceduralTileType.Corner,      rotationDeg = 90f,   isDiagonal = false },
            /* 5  */ new DualGridTileResult { type = ProceduralTileType.Edge,        rotationDeg = 0f,    isDiagonal = false },
            /* 6  */ new DualGridTileResult { type = ProceduralTileType.Corner,      rotationDeg = 90f,   isDiagonal = true  }, // Diagonal
            /* 7  */ new DualGridTileResult { type = ProceduralTileType.InnerCorner, rotationDeg = 0f,    isDiagonal = false },
            /* 8  */ new DualGridTileResult { type = ProceduralTileType.Corner,      rotationDeg = 180f,  isDiagonal = false },
            /* 9  */ new DualGridTileResult { type = ProceduralTileType.Corner,      rotationDeg = 0f,    isDiagonal = true  }, // Diagonal
            /* 10 */ new DualGridTileResult { type = ProceduralTileType.Edge,        rotationDeg = 180f,  isDiagonal = false },
            /* 11 */ new DualGridTileResult { type = ProceduralTileType.InnerCorner, rotationDeg = -90f,  isDiagonal = false },
            /* 12 */ new DualGridTileResult { type = ProceduralTileType.Edge,        rotationDeg = 90f,   isDiagonal = false },
            /* 13 */ new DualGridTileResult { type = ProceduralTileType.InnerCorner, rotationDeg = 90f,   isDiagonal = false },
            /* 14 */ new DualGridTileResult { type = ProceduralTileType.InnerCorner, rotationDeg = 180f,  isDiagonal = false },
            /* 15 */ new DualGridTileResult { type = ProceduralTileType.Full,        rotationDeg = 0f,    isDiagonal = false },
        };

        /// <summary>
        /// Look up the tile mapping for a given 4-bit neighbor mask.
        /// Returns null for empty cells (mask = 0).
        /// </summary>
        public static DualGridTileResult GetTileForMask(int mask)
        {
            if (mask < 0 || mask > 15) return null;
            return TileMapping[mask];
        }

        /// <summary>
        /// Compute the 4-bit neighbor mask for a dual-grid cell.
        /// </summary>
        public static int ComputeDualGridMask(System.Func<int, int, bool> isFilled, int dualX, int dualY)
        {
            // Match HTML convention exactly (App.tsx lines 503-507):
            //   tl = (x-1, y-1) → bit 3   tr = (x, y-1) → bit 2
            //   bl = (x-1, y)   → bit 1   br = (x, y)   → bit 0
            int mask = 0;
            if (isFilled(dualX - 1, dualY - 1)) mask |= 8;  // tl → bit 3
            if (isFilled(dualX,     dualY - 1)) mask |= 4;  // tr → bit 2
            if (isFilled(dualX - 1, dualY))     mask |= 2;  // bl → bit 1
            if (isFilled(dualX,     dualY))     mask |= 1;  // br → bit 0
            return mask;
        }

        // ─────────────────────────────────────────────
        // Mesh Generation
        // ─────────────────────────────────────────────

        [System.Serializable]
        public class ProceduralMeshSettings
        {
            public ProceduralMeshSettings()
            {
                skirtMaskCurve = CreateDefaultSkirtMaskCurve();
            }

            [Tooltip("Corner radius (0 = sharp, 0.5 = max round). Maps to 'r' in HTML version.")]
            [Range(0f, 0.5f)] public float radius = 0.3f;

            [Tooltip("Extrusion depth (thickness of the tile).")]
            [Range(0.01f, 5f)] public float depth = 0.4f;

            [Tooltip("Resolution of quadratic curves (corners)")]
            [Range(2, 16)] public int curveSegments = 8;

            [Header("Skirt (curved overhang on exterior edges)")]
            [Tooltip("Enable or disable the skirt (curved overhang).")]
            public bool skirtEnabled = true;

            [Tooltip("How far outward the skirt extends from the tile boundary.")]
            [Range(0f, 0.3f)] public float skirtWidth = 0.155f;

            [Tooltip("How far down the skirt drops from the top cap.")]
            [Range(0f, 0.5f)] public float skirtHeight = 0.485f;

            [Tooltip("Number of segments for the skirt curve.")]
            [Range(1, 8)] public int skirtSegments = 2;

            [Tooltip("UV scale for the skirt texture.")]
            [Range(0.1f, 10f)] public float skirtUVScale = 1f;

            [Tooltip("UV Y offset for the skirt texture.")]
            [Range(-1f, 1f)] public float skirtUVOffsetY = 0.389f;

            [Tooltip("How the skirt chooses its visible material.")]
            public SkirtMaterialMode skirtMaterialMode = SkirtMaterialMode.CurrentSkirtMaterial;

            [Tooltip("Curve used to generate the local skirt mask when using the floor material display mode.")]
            public AnimationCurve skirtMaskCurve;

            [Header("Bottom Cap")]
            [Tooltip("Bottom cap style: None, Flat, Biseau, or IslandNoise (rocky floating island).")]
            public BottomMode bottomMode = BottomMode.None;

            [Tooltip("Rayon du biseau pour le mode Biseau.")]
            [Range(0f, 1f)] public float bottomBevelInset = 0.1f;

            [Tooltip("Profondeur du biseau (jusqu'ou le dessous descend).")]
            [Range(0f, 2f)] public float bottomBevelDepth = 0.15f;

            [Tooltip("Nombre de segments utilises pour arrondir le biseau.")]
            [Range(1, 8)] public int bottomBevelSegments = 4;

            [Tooltip("Profil du biseau: Convex (bombe) ou Concave (creuse).")]
            public BevelProfile bottomBevelProfile = BevelProfile.Convex;

            [Tooltip("Noise scale for IslandNoise mode (smaller = larger rock features).")]
            [Range(0.5f, 10f)] public float bottomNoiseScale = 0.96f;

            [Tooltip("Maximum downward displacement for IslandNoise mode.")]
            [Range(0f, 10f)] public float bottomNoiseAmplitude = 5.98f;

            [Tooltip("Sharpness of the island point. Low (0.5) = flat dome, High (5) = sharp stalactite.")]
            [Range(0.3f, 5f)] public float bottomIslandSharpness = 1.99f;

            [Tooltip("How much the wall bottom blends into the island shape. 0 = hard edge, 1 = fully smooth.")]
            [Range(0f, 1f)] public float bottomIslandSmooth = 0f;

            [Tooltip("Noise grid resolution for IslandNoise mode. 1 = very low-poly, 16 = denser mesh.")]
            [Range(1, 16)] public int bottomNoiseResolution = 1;

            [Tooltip("Noise seed offset for IslandNoise mode.")]
            public float bottomNoiseSeed = 0f;

            public void EnsureSkirtMaskCurve()
            {
                if (skirtMaskCurve == null || skirtMaskCurve.length == 0 || IsLegacyShortSkirtMaskCurve(skirtMaskCurve))
                    skirtMaskCurve = CreateDefaultSkirtMaskCurve();
            }
        }

        public static AnimationCurve CreateDefaultSkirtMaskCurve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0f, 0.24f),
                new Keyframe(0.22f, 0.21f),
                new Keyframe(0.5f, 0.18f),
                new Keyframe(0.78f, 0.22f),
                new Keyframe(1f, 0.24f));

            for (int i = 0; i < curve.length; i++)
                AnimationUtilityHelper.SmoothTangents(curve, i, 0f);

            return curve;
        }

        private static bool IsLegacyShortSkirtMaskCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length != LegacyDefaultSkirtMaskTimes.Length)
                return false;

            const float epsilon = 0.0001f;
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve.keys[i];
                if (Mathf.Abs(key.time - LegacyDefaultSkirtMaskTimes[i]) > epsilon)
                    return false;
                if (Mathf.Abs(key.value - LegacyDefaultSkirtMaskValues[i]) > epsilon)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Generate all 4 tile meshes with the given settings.
        /// </summary>
        public static Dictionary<ProceduralTileType, Mesh> GenerateAllMeshes(ProceduralMeshSettings settings)
        {
            return new Dictionary<ProceduralTileType, Mesh>
            {
                { ProceduralTileType.Full,        GenerateFullMesh(settings) },
                { ProceduralTileType.Corner,      GenerateCornerMesh(settings) },
                { ProceduralTileType.Edge,        GenerateEdgeMesh(settings) },
                { ProceduralTileType.InnerCorner, GenerateInnerCornerMesh(settings) },
            };
        }

        // ─── Full tile (1x1 square — top cap only, NO walls, NO skirt) ──────

        public static Mesh GenerateFullMesh(ProceduralMeshSettings settings)
        {
            var outline = new List<Vector2>
            {
                new Vector2(-0.5f,  0.5f),
                new Vector2( 0.5f,  0.5f),
                new Vector2( 0.5f, -0.5f),
                new Vector2(-0.5f, -0.5f),
            };
            return ExtrudeShape(outline, settings, "ProceduralTile_Full", skipSideWalls: true);
        }

        // ─── Corner (outer corner with quadratic curve) ─────────────
        // Interior edges: 0→1 (bottom), 1→2 (right) — these connect to adjacent tiles.
        // Exterior edges: 2 onwards (curve + connecting segments on the perimeter).

        public static Mesh GenerateCornerMesh(ProceduralMeshSettings settings)
        {
            float r = settings.radius;
            int segs = settings.curveSegments;

            var outline = new List<Vector2>();
            outline.Add(new Vector2(0f,   -0.5f));  // 0
            outline.Add(new Vector2(0.5f, -0.5f));  // 1
            outline.Add(new Vector2(0.5f,  0f));     // 2
            outline.Add(new Vector2(r,     0f));     // 3

            // Quadratic bezier: P0=(r, 0), P1=(0, 0), P2=(0, -r)
            for (int i = 1; i <= segs; i++)
            {
                float t = (float)i / segs;
                float x = QuadBezier(r, 0f, 0f, t);
                float y = QuadBezier(0f, 0f, -r, t);
                outline.Add(new Vector2(x, y));
            }

            // Edge 0 (0→1) and edge 1 (1→2) are interior (shared with neighbors).
            // Edges 2+ are exterior (curve + perimeter segments).
            int n = outline.Count;
            var exterior = new HashSet<int>();
            for (int i = 2; i < n; i++) exterior.Add(i);

            return ExtrudeShape(outline, settings, "ProceduralTile_Corner", exteriorEdges: exterior);
        }

        // ─── Edge (half tile, straight) ─────────────────────────────
        // Only the straight edge at x=0 is exterior (visible wall).
        // The 3 other edges (y=0.5, x=0.5, y=-0.5) are interior (shared with neighbors).

        public static Mesh GenerateEdgeMesh(ProceduralMeshSettings settings)
        {
            // Outline: 0:(0,0.5), 1:(0.5,0.5), 2:(0.5,-0.5), 3:(0,-0.5)
            // Edge 3→0 is the exterior wall (x=0 line)
            var outline = new List<Vector2>
            {
                new Vector2(0f,    0.5f),
                new Vector2(0.5f,  0.5f),
                new Vector2(0.5f, -0.5f),
                new Vector2(0f,   -0.5f),
            };

            var exterior = new HashSet<int> { 3 }; // only edge 3→0
            return ExtrudeShape(outline, settings, "ProceduralTile_Edge", exteriorEdges: exterior);
        }

        // ─── InnerCorner (concave corner with quadratic curve) ──────
        // Interior edges: 0→1 (y=0.5), 1→2 (x=0.5), 2→3 (y=-0.5), 3→4 (x=-0.5)
        // Exterior edges: from vertex 4 onwards (the concave curve + connecting segments)

        public static Mesh GenerateInnerCornerMesh(ProceduralMeshSettings settings)
        {
            float r = settings.radius;
            int segs = settings.curveSegments;

            var outline = new List<Vector2>();
            outline.Add(new Vector2( 0f,    0.5f));   // 0
            outline.Add(new Vector2( 0.5f,  0.5f));   // 1
            outline.Add(new Vector2( 0.5f, -0.5f));   // 2
            outline.Add(new Vector2(-0.5f, -0.5f));   // 3
            outline.Add(new Vector2(-0.5f,  0f));      // 4
            outline.Add(new Vector2(-r,     0f));      // 5

            // Quadratic bezier: P0=(-r, 0), P1=(0, 0), P2=(0, r)
            for (int i = 1; i <= segs; i++)
            {
                float t = (float)i / segs;
                float x = QuadBezier(-r, 0f, 0f, t);
                float y = QuadBezier(0f, 0f, r, t);
                outline.Add(new Vector2(x, y));
            }

            // Exterior edges: from vertex 4 to end (curve + connecting segments)
            // Interior edges: 0→1, 1→2, 2→3, 3→4 (indices 0, 1, 2, 3)
            int n = outline.Count;
            var exterior = new HashSet<int>();
            for (int i = 4; i < n; i++) exterior.Add(i);

            return ExtrudeShape(outline, settings, "ProceduralTile_InnerCorner", exteriorEdges: exterior);
        }

        // ─────────────────────────────────────────────
        // Core Extrusion Engine
        // Top cap + exterior side walls + optional skirt (curved overhang)
        // Submesh 0: top cap (floor material)
        // Submesh 1: walls (wall material)
        // Submesh 2: skirt (skirt/floor material) — only if skirt enabled
        // ─────────────────────────────────────────────

        private static Mesh ExtrudeShape(List<Vector2> outline, ProceduralMeshSettings settings, string name,
            bool skipSideWalls = false, HashSet<int> exteriorEdges = null)
        {
            float depth = settings.depth;
            int n = outline.Count;

            bool hasSkirt = !skipSideWalls && settings.skirtEnabled && settings.skirtWidth > 0f && settings.skirtHeight > 0f;
            float skirtW = settings.skirtWidth;
            float skirtH = settings.skirtHeight;
            int skirtSegs = settings.skirtSegments;
            float skirtUVS = settings.skirtUVScale;
            float skirtUVO = settings.skirtUVOffsetY;

            bool hasBottom = settings.bottomMode != BottomMode.None;

            // Detect 2D winding direction for correct triangle winding after Z-mirror
            float signedArea2D = 0f;
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                signedArea2D += (outline[next].x - outline[i].x) * (outline[next].y + outline[i].y);
            }
            bool isCW = signedArea2D > 0f;

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var uv2s = new List<Vector2>();
            var capTriangles = new List<int>();    // submesh 0: top cap
            var wallTriangles = new List<int>();   // submesh 1: walls
            var skirtTriangles = new List<int>();  // submesh 2: skirt
            var bottomTriangles = new List<int>(); // submesh 3: bottom cap

            // Compute per-vertex outward normals (needed for skirt offset)
            Vector2[] vertNormals = null;
            if (hasSkirt)
                vertNormals = ComputeVertexOutwardNormals(outline, isCW, exteriorEdges);

            // ── Side walls (only exterior edges) ──
            if (!skipSideWalls)
            {
                // Two rings: bottom (Y=0) and top (Y=depth)
                for (int ring = 0; ring < 2; ring++)
                {
                    float y = ring == 0 ? 0f : depth;
                    for (int i = 0; i < n; i++)
                    {
                        Vector2 p = outline[i];
                        vertices.Add(new Vector3(p.x, y, -p.y)); // Z-mirror
                        uvs.Add(new Vector2((float)i / n, (float)ring));
                        uv2s.Add(Vector2.zero);
                    }
                }

                // Wall quads — only for exterior edges
                int ringA = 0;    // bottom ring start
                int ringB = n;    // top ring start
                for (int i = 0; i < n; i++)
                {
                    // Skip interior edges
                    if (exteriorEdges != null && !exteriorEdges.Contains(i)) continue;

                    int next = (i + 1) % n;
                    if (isCW)
                    {
                        wallTriangles.Add(ringA + i);
                        wallTriangles.Add(ringB + i);
                        wallTriangles.Add(ringA + next);

                        wallTriangles.Add(ringA + next);
                        wallTriangles.Add(ringB + i);
                        wallTriangles.Add(ringB + next);
                    }
                    else
                    {
                        wallTriangles.Add(ringA + i);
                        wallTriangles.Add(ringA + next);
                        wallTriangles.Add(ringB + i);

                        wallTriangles.Add(ringA + next);
                        wallTriangles.Add(ringB + next);
                        wallTriangles.Add(ringB + i);
                    }
                }
            }

            // ── Top cap only (at Y = depth) → submesh 0 ──
            var topCapOutline = new List<Vector2>(n);
            int topCapStart = vertices.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = outline[i];
                topCapOutline.Add(p);
                vertices.Add(new Vector3(p.x, depth, -p.y)); // Z-mirror
                uvs.Add(new Vector2(p.x + 0.5f, p.y + 0.5f));
                uv2s.Add(Vector2.zero);
            }
            TriangulateCap(topCapOutline, capTriangles, topCapStart, isCW);

            // ── Skirt (curved overhang on exterior edges) → submesh 2 ──
            if (hasSkirt)
            {
                var skirtUByVertex = ComputeSkirtExteriorUCoordinates(outline, exteriorEdges);

                // Keep the original full-ring skirt topology so corners stay connected
                // the same way as before, but drive U from the actual visible exterior
                // runs instead of the full local outline to avoid stretched textures.
                int skirtBaseIdx = vertices.Count;

                for (int s = 0; s <= skirtSegs; s++)
                {
                    float t = (float)s / skirtSegs;
                    float angle = t * Mathf.PI * 0.5f;
                    float offsetDist = Mathf.Sin(angle) * skirtW;   // 0 → skirtWidth
                    float yDrop = (1f - Mathf.Cos(angle)) * skirtH; // 0 → skirtHeight
                    float y = depth - yDrop;

                    for (int i = 0; i < n; i++)
                    {
                        Vector2 norm = vertNormals[i];
                        Vector2 p = outline[i] + norm * offsetDist;
                        vertices.Add(new Vector3(p.x, y, -p.y)); // Z-mirror

                        float u = skirtUByVertex[i] * skirtUVS;
                        float v = (1f - t) * skirtUVS + skirtUVO;
                        uvs.Add(new Vector2(u, v));
                        uv2s.Add(new Vector2(Mathf.Clamp01(skirtUByVertex[i]), 1f - t));
                    }
                }

                // Connect consecutive rings with quads, only on exterior edges
                for (int s = 0; s < skirtSegs; s++)
                {
                    int rA = skirtBaseIdx + s * n;
                    int rB = skirtBaseIdx + (s + 1) * n;

                    for (int i = 0; i < n; i++)
                    {
                        if (exteriorEdges != null && !exteriorEdges.Contains(i)) continue;
                        int next = (i + 1) % n;

                        if (isCW)
                        {
                            skirtTriangles.Add(rA + i);
                            skirtTriangles.Add(rA + next);
                            skirtTriangles.Add(rB + i);

                            skirtTriangles.Add(rA + next);
                            skirtTriangles.Add(rB + next);
                            skirtTriangles.Add(rB + i);
                        }
                        else
                        {
                            skirtTriangles.Add(rA + i);
                            skirtTriangles.Add(rB + i);
                            skirtTriangles.Add(rA + next);

                            skirtTriangles.Add(rA + next);
                            skirtTriangles.Add(rB + i);
                            skirtTriangles.Add(rB + next);
                        }
                    }
                }
            }

            // ── Bottom cap (at Y = 0, visible from below) → submesh 3 ──
            if (hasBottom)
            {
                ProceduralTileBottomCapGenerator.Generate(
                    new ProceduralTileBottomContext(
                        outline,
                        vertices,
                        uvs,
                        bottomTriangles,
                        settings,
                        isCW,
                        skipSideWalls,
                        exteriorEdges));
            }

            while (uv2s.Count < vertices.Count)
                uv2s.Add(Vector2.zero);

            // ── Assemble mesh ──
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, uv2s);

            int submeshCount = 2; // caps + walls
            if (hasSkirt) submeshCount++;
            if (hasBottom && bottomTriangles.Count > 0) submeshCount++;
            mesh.subMeshCount = submeshCount;

            int si = 0;
            mesh.SetTriangles(capTriangles, si++);    // submesh 0: top cap
            mesh.SetTriangles(wallTriangles, si++);   // submesh 1: walls
            if (hasSkirt)
                mesh.SetTriangles(skirtTriangles, si++); // submesh 2: skirt
            if (hasBottom && bottomTriangles.Count > 0)
                mesh.SetTriangles(bottomTriangles, si++); // submesh 3 (or 2 if no skirt): bottom

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            return mesh;
        }

        private static class AnimationUtilityHelper
        {
            public static void SmoothTangents(AnimationCurve curve, int index, float weight)
            {
#if UNITY_EDITOR
                UnityEditor.AnimationUtility.SetKeyLeftTangentMode(curve, index, UnityEditor.AnimationUtility.TangentMode.Auto);
                UnityEditor.AnimationUtility.SetKeyRightTangentMode(curve, index, UnityEditor.AnimationUtility.TangentMode.Auto);
#else
                curve.SmoothTangents(index, weight);
#endif
            }
        }

        // ─────────────────────────────────────────────
        // Vertex outward normals (for skirt offset direction)
        // ─────────────────────────────────────────────

        private static Vector2[] ComputeVertexOutwardNormals(List<Vector2> outline, bool isCW, HashSet<int> exteriorEdges)
        {
            int n = outline.Count;

            // Compute per-edge outward normals
            var edgeNormals = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                Vector2 d = outline[next] - outline[i];
                float len = d.magnitude;
                if (len < 0.0001f) { edgeNormals[i] = Vector2.zero; continue; }
                d /= len;

                // Outward normal: for CW winding, left perpendicular; for CCW, right
                if (isCW)
                    edgeNormals[i] = new Vector2(-d.y, d.x);
                else
                    edgeNormals[i] = new Vector2(d.y, -d.x);
            }

            // Compute per-vertex normals by averaging adjacent edge normals.
            // Only consider exterior edges for the average so the skirt direction
            // is meaningful at the boundary between exterior and interior edges.
            var vertNormals = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                int prevEdge = (i - 1 + n) % n;
                int curEdge = i;

                bool prevIsExterior = (exteriorEdges == null || exteriorEdges.Contains(prevEdge));
                bool curIsExterior = (exteriorEdges == null || exteriorEdges.Contains(curEdge));

                Vector2 sum = Vector2.zero;
                if (prevIsExterior) sum += edgeNormals[prevEdge];
                if (curIsExterior) sum += edgeNormals[curEdge];

                if (sum.sqrMagnitude > 0.0001f)
                    vertNormals[i] = sum.normalized;
                else
                    vertNormals[i] = edgeNormals[curEdge]; // fallback
            }

            return vertNormals;
        }

        private static List<List<int>> BuildExteriorVertexRuns(int vertexCount, HashSet<int> exteriorEdges)
        {
            var runs = new List<List<int>>();
            if (vertexCount < 2) return runs;

            if (exteriorEdges == null)
            {
                var fullLoop = new List<int>(vertexCount);
                for (int i = 0; i < vertexCount; i++) fullLoop.Add(i);
                runs.Add(fullLoop);
                return runs;
            }

            var isExteriorEdge = new bool[vertexCount];
            bool hasExterior = false;
            foreach (int edge in exteriorEdges)
            {
                if (edge < 0 || edge >= vertexCount) continue;
                isExteriorEdge[edge] = true;
                hasExterior = true;
            }

            if (!hasExterior) return runs;

            var starts = new List<int>();
            for (int i = 0; i < vertexCount; i++)
            {
                if (!isExteriorEdge[i]) continue;

                int prev = (i - 1 + vertexCount) % vertexCount;
                if (!isExteriorEdge[prev])
                    starts.Add(i);
            }

            // All edges exterior: make one closed run.
            if (starts.Count == 0)
                starts.Add(0);

            foreach (int start in starts)
            {
                var run = new List<int> { start };
                int edge = start;
                int safety = vertexCount + 1;

                while (safety-- > 0 && isExteriorEdge[edge])
                {
                    int nextVertex = (edge + 1) % vertexCount;
                    if (nextVertex == start)
                        break;

                    run.Add(nextVertex);
                    edge = nextVertex;
                }

                if (run.Count >= 2)
                    runs.Add(run);
            }

            return runs;
        }

        private static float[] ComputeSkirtExteriorUCoordinates(List<Vector2> outline, HashSet<int> exteriorEdges)
        {
            int n = outline.Count;
            var uByVertex = new float[n];
            if (n < 2) return uByVertex;

            bool allExterior = exteriorEdges == null;
            if (!allExterior && exteriorEdges.Count >= n)
            {
                allExterior = true;
                for (int i = 0; i < n; i++)
                {
                    if (!exteriorEdges.Contains(i))
                    {
                        allExterior = false;
                        break;
                    }
                }
            }

            if (allExterior)
            {
                float[] arcLengths = new float[n];
                arcLengths[0] = 0f;
                for (int i = 1; i < n; i++)
                    arcLengths[i] = arcLengths[i - 1] + (outline[i] - outline[i - 1]).magnitude;

                float totalArc = arcLengths[n - 1] + (outline[0] - outline[n - 1]).magnitude;
                if (totalArc <= 0.0001f) return uByVertex;

                for (int i = 0; i < n; i++)
                    uByVertex[i] = arcLengths[i] / totalArc;

                return uByVertex;
            }

            var exteriorRuns = BuildExteriorVertexRuns(n, exteriorEdges);
            foreach (var run in exteriorRuns)
            {
                int runCount = run.Count;
                if (runCount < 2) continue;

                float[] runArcLengths = new float[runCount];
                runArcLengths[0] = 0f;
                for (int i = 1; i < runCount; i++)
                {
                    Vector2 prev = outline[run[i - 1]];
                    Vector2 cur = outline[run[i]];
                    runArcLengths[i] = runArcLengths[i - 1] + (cur - prev).magnitude;
                }

                float totalRunArc = runArcLengths[runCount - 1];
                if (totalRunArc <= 0.0001f) continue;

                for (int i = 0; i < runCount; i++)
                    uByVertex[run[i]] = runArcLengths[i] / totalRunArc;
            }

            return uByVertex;
        }

        // ─────────────────────────────────────────────
        // Cap triangulation (ear clipping for simple polygons)
        // ─────────────────────────────────────────────

        internal static void TriangulateCap(List<Vector2> outline, List<int> triangles, int baseIndex, bool flipWinding)
        {
            int n = outline.Count;
            if (n < 3) return;

            float signedArea = 0f;
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                signedArea += (outline[next].x - outline[i].x) * (outline[next].y + outline[i].y);
            }
            float convexSign = signedArea > 0f ? -1f : 1f;

            var remaining = new List<int>();
            for (int i = 0; i < n; i++) remaining.Add(i);

            int safety = n * n;
            while (remaining.Count > 2 && safety-- > 0)
            {
                bool earFound = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int prev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    int curr = remaining[i];
                    int next = remaining[(i + 1) % remaining.Count];

                    Vector2 a = outline[prev];
                    Vector2 b = outline[curr];
                    Vector2 c = outline[next];

                    float cross = Cross2D(b - a, c - b);
                    if (cross * convexSign <= 0f) continue;

                    bool containsPoint = false;
                    for (int j = 0; j < remaining.Count; j++)
                    {
                        int idx = remaining[j];
                        if (idx == prev || idx == curr || idx == next) continue;
                        if (PointInTriangle(outline[idx], a, b, c))
                        {
                            containsPoint = true;
                            break;
                        }
                    }

                    if (!containsPoint)
                    {
                        if (flipWinding)
                        {
                            triangles.Add(baseIndex + prev);
                            triangles.Add(baseIndex + next);
                            triangles.Add(baseIndex + curr);
                        }
                        else
                        {
                            triangles.Add(baseIndex + prev);
                            triangles.Add(baseIndex + curr);
                            triangles.Add(baseIndex + next);
                        }
                        remaining.RemoveAt(i);
                        earFound = true;
                        break;
                    }
                }

                if (!earFound)
                {
                    if (remaining.Count >= 3)
                    {
                        int prev = remaining[0];
                        int curr = remaining[1];
                        int next = remaining[2];
                        if (flipWinding)
                        {
                            triangles.Add(baseIndex + prev);
                            triangles.Add(baseIndex + next);
                            triangles.Add(baseIndex + curr);
                        }
                        else
                        {
                            triangles.Add(baseIndex + prev);
                            triangles.Add(baseIndex + curr);
                            triangles.Add(baseIndex + next);
                        }
                        remaining.RemoveAt(1);
                    }
                    else break;
                }
            }
        }

        // ─────────────────────────────────────────────
        // Math helpers
        // ─────────────────────────────────────────────

        private static float QuadBezier(float p0, float p1, float p2, float t)
        {
            float omt = 1f - t;
            return omt * omt * p0 + 2f * omt * t * p1 + t * t * p2;
        }

        private static float Cross2D(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross2D(b - a, p - a);
            float d2 = Cross2D(c - b, p - b);
            float d3 = Cross2D(a - c, p - c);
            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }
    }
}
