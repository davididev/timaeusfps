// Assets/BEKKOLOCO/QuickTile/Script/extra/PlatformerMeshGenerator.cs
// 2.5D side-scroller mesh generation — MVP port of QuickTexture web's PlatformerLayer.
// Takes a set of filled cells on the XY plane, groups them into horizontal runs,
// and produces wall + floor (top cap) meshes. Crown is a simple perimeter ring for now.
//
// Reference: QuickTexture/components/QuickTilePage.tsx PlatformerLayer

using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    public static class PlatformerMeshGenerator
    {
        public const float DefaultBlockDepth = 4f;        // PLATFORMER_DEPTH — extrusion along Z
        public const float DefaultTopThickness = 0.08f;   // thin floor cap above each top run
        public const float DefaultRadius = 0.34f;         // top-view rounded corner radius (0..0.5)

        [System.Serializable]
        public class PlatformerSettings
        {
            [Tooltip("Depth of the wall block along Z (away from camera).")]
            public float blockDepth = DefaultBlockDepth;

            [Tooltip("Thickness of the thin floor cap on top of top-runs.")]
            public float topThickness = DefaultTopThickness;

            [Tooltip("Top-view rounded-rect corner radius (0..0.5). Applied to wall footprint and floor shape.")]
            [Range(0f, 0.5f)] public float radius = DefaultRadius;

            [Tooltip("Perimeter tessellation. 1 = sharp, higher = smoother corners.")]
            [Range(1, 16)] public int meshSegments = 4;

            [Tooltip("Vertical layer offset (layer.yPosition equivalent).")]
            public float yPosition = 0f;

            // ── Skirt (drives the 'crown' mesh using the same fields as the 3D path) ──
            [Tooltip("Emit the skirt (curved grass overhang) around top-runs.")]
            public bool skirtEnabled = true;

            [Tooltip("How far outward the skirt extends from the run's top edge.")]
            [Range(0f, 0.3f)] public float skirtWidth = 0.155f;

            [Tooltip("How far down the skirt drops from the top cap.")]
            [Range(0f, 0.5f)] public float skirtHeight = 0.485f;

            [Tooltip("Number of segments for the skirt curve.")]
            [Range(1, 8)] public int skirtSegments = 2;

            [Tooltip("UV scale for the skirt texture.")]
            [Range(0.1f, 10f)] public float skirtUVScale = 1f;

            [Tooltip("UV Y offset for the skirt texture.")]
            [Range(-1f, 1f)] public float skirtUVOffsetY = 0.389f;

            [Tooltip("Outward overhang of the floor past the wall edge. The skirt hangs off this lip, so the grass appears to bleed over the wall rather than meeting it at a hard corner.")]
            [Range(0f, 0.3f)] public float floorOverhang = 0.08f;

            [Tooltip("Small horizontal underlap used by the 2.5D top silhouette so bridges tuck slightly under neighbouring walls.")]
            [Range(0f, 0.5f)] public float sideUnderlap = 0.5f;

            [Tooltip("Bottom cap style reused by the 2.5D platformer path.")]
            public BottomMode bottomMode = BottomMode.None;

            [Tooltip("Bevel inset reused by the 2.5D bottom cap path.")]
            public float bottomBevelInset = 0.1f;

            [Tooltip("Bevel depth reused by the 2.5D bottom cap path.")]
            public float bottomBevelDepth = 0.15f;

            [Tooltip("Bevel segments reused by the 2.5D bottom cap path.")]
            public int bottomBevelSegments = 4;

            [Tooltip("Bevel profile reused by the 2.5D bottom cap path.")]
            public BevelProfile bottomBevelProfile = BevelProfile.Convex;

            [Tooltip("Noise scale reused by the 2.5D rocky island bottom cap.")]
            public float bottomNoiseScale = 0.96f;

            [Tooltip("Noise amplitude reused by the 2.5D rocky island bottom cap.")]
            public float bottomNoiseAmplitude = 5.98f;

            [Tooltip("Island sharpness reused by the 2.5D rocky island bottom cap.")]
            public float bottomIslandSharpness = 1.99f;

            [Tooltip("Island smoothness reused by the 2.5D rocky island bottom cap.")]
            public float bottomIslandSmooth = 0f;

            [Tooltip("Noise resolution reused by the 2.5D rocky island bottom cap.")]
            public int bottomNoiseResolution = 1;

            [Tooltip("Noise seed reused by the 2.5D rocky island bottom cap.")]
            public float bottomNoiseSeed = 0f;
        }

        public readonly struct Run
        {
            public readonly int Y;
            public readonly int StartX;
            public readonly int EndX;
            public Run(int y, int startX, int endX) { Y = y; StartX = startX; EndX = endX; }
            public int Width => EndX - StartX + 1;
            public float CenterX => (StartX + EndX) * 0.5f;
        }

        public readonly struct PlatformerMeshes
        {
            public readonly Mesh Wall;
            public readonly Mesh Floor;
            public readonly Mesh Crown;
            public readonly Mesh Bottom;
            public PlatformerMeshes(Mesh wall, Mesh floor, Mesh crown, Mesh bottom)
            {
                Wall = wall; Floor = floor; Crown = crown; Bottom = bottom;
            }
        }

        public static PlatformerMeshes Build(HashSet<Vector2Int> cells, PlatformerSettings settings = null)
        {
            settings ??= new PlatformerSettings();
            if (cells == null || cells.Count == 0)
                return new PlatformerMeshes(null, null, null, null);

            ExtractRuns(cells, out var allRuns, out var topRuns, out var bottomRuns);

            var wall  = BuildWallsMesh(allRuns, settings, cells);
            var floor = BuildFloorsMesh(topRuns, settings, cells);
            var crown = settings.skirtEnabled ? BuildCrownMesh(topRuns, settings, cells) : null;
            var bottom = settings.bottomMode != BottomMode.None ? BuildBottomMesh(bottomRuns, settings, cells) : null;

            if (wall  != null) wall.name  = "QT_Platformer_Wall";
            if (floor != null) floor.name = "QT_Platformer_Floor";
            if (crown != null) crown.name = "QT_Platformer_Crown";
            if (bottom != null) bottom.name = "QT_Platformer_Bottom";

            return new PlatformerMeshes(wall, floor, crown, bottom);
        }

        // ─────────────────────────────────────────────
        // Run extraction
        // ─────────────────────────────────────────────

        static void ExtractRuns(HashSet<Vector2Int> cells, out List<Run> allRuns, out List<Run> topRuns, out List<Run> bottomRuns)
        {
            var byYAll = new Dictionary<int, List<int>>();
            var byYTop = new Dictionary<int, List<int>>();
            var byYBottom = new Dictionary<int, List<int>>();

            foreach (var c in cells)
            {
                if (!byYAll.TryGetValue(c.y, out var listAll))
                {
                    listAll = new List<int>();
                    byYAll[c.y] = listAll;
                }
                listAll.Add(c.x);

                // Top run only if the cell directly above is empty.
                if (!cells.Contains(new Vector2Int(c.x, c.y + 1)))
                {
                    if (!byYTop.TryGetValue(c.y, out var listTop))
                    {
                        listTop = new List<int>();
                        byYTop[c.y] = listTop;
                    }
                    listTop.Add(c.x);
                }

                // Bottom run only if the cell directly below is empty.
                if (!cells.Contains(new Vector2Int(c.x, c.y - 1)))
                {
                    if (!byYBottom.TryGetValue(c.y, out var listBottom))
                    {
                        listBottom = new List<int>();
                        byYBottom[c.y] = listBottom;
                    }
                    listBottom.Add(c.x);
                }
            }

            allRuns = SplitRuns(byYAll);
            topRuns = SplitRuns(byYTop);
            bottomRuns = SplitRuns(byYBottom);
        }

        static List<Run> SplitRuns(Dictionary<int, List<int>> byY)
        {
            var result = new List<Run>();
            foreach (var kv in byY)
            {
                int y = kv.Key;
                var xs = kv.Value;
                xs.Sort();
                if (xs.Count == 0) continue;

                int start = xs[0];
                int prev = xs[0];
                for (int i = 1; i < xs.Count; i++)
                {
                    int x = xs[i];
                    if (x == prev + 1) { prev = x; continue; }
                    result.Add(new Run(y, start, prev));
                    start = x;
                    prev = x;
                }
                result.Add(new Run(y, start, prev));
            }
            return result;
        }

        // ─────────────────────────────────────────────
        // Walls — runs extruded along Y, rounded footprint on XZ.
        // ─────────────────────────────────────────────

        static Mesh BuildWallsMesh(List<Run> runs, PlatformerSettings s, HashSet<Vector2Int> cells)
        {
            if (runs == null || runs.Count == 0) return null;

            var builder = new MeshBuilder();
            float sideUnderlap = Mathf.Max(0f, s.sideUnderlap);
            foreach (var run in runs)
            {
                bool leftContact = cells.Contains(new Vector2Int(run.StartX - 1, run.Y));
                bool rightContact = cells.Contains(new Vector2Int(run.EndX + 1, run.Y));
                float leftUnderlap = leftContact ? sideUnderlap : 0f;
                float rightUnderlap = rightContact ? sideUnderlap : 0f;
                var shape = BuildRoundedRectShape(run.Width, s.blockDepth, s.radius, s.meshSegments);
                ApplySideUnderlapToShape(shape, leftUnderlap, rightUnderlap);
                float centerX = run.CenterX;
                float centerY = run.Y + s.yPosition;
                // Walls never emit top/bottom caps — they're internal (between stacked cells)
                // or covered by the floor cap for top-runs.
                ExtrudeShape(builder, shape, depthY: 1f,
                    translation: new Vector3(centerX, centerY - 0.5f, 0f),
                    emitTopCap: false, emitBottomCap: false);
            }
            return builder.ToMesh();
        }

        // ─────────────────────────────────────────────
        // Floors — thin cap on top of each top-run.
        // ─────────────────────────────────────────────

        static Mesh BuildFloorsMesh(List<Run> topRuns, PlatformerSettings s, HashSet<Vector2Int> cells)
        {
            if (topRuns == null || topRuns.Count == 0) return null;

            var builder = new MeshBuilder();
            float overhang = Mathf.Max(0f, s.floorOverhang);
            float sideUnderlap = Mathf.Max(0f, s.sideUnderlap);
            foreach (var run in topRuns)
            {
                var shape = BuildRoundedRectShape(run.Width, s.blockDepth, s.radius, s.meshSegments);
                bool leftContact = cells.Contains(new Vector2Int(run.StartX - 1, run.Y));
                bool rightContact = cells.Contains(new Vector2Int(run.EndX + 1, run.Y));
                float leftUnderlap = leftContact ? sideUnderlap : 0f;
                float rightUnderlap = rightContact ? sideUnderlap : 0f;
                ApplySideUnderlapToShape(shape, leftUnderlap, rightUnderlap);
                int n = shape.Count;
                if (n < 3) continue;

                float centerX = run.CenterX;
                float baseY = run.Y + s.yPosition + 0.5f;

                // Push each vertex outward by floorOverhang along its outward normal,
                // so the floor extends slightly past the wall edge. The skirt will start
                // at the same offset, creating a visible grass lip that bleeds over the wall.
                var vNorm = ComputeVertexOutwardNormals(shape, n);
                int topStart = builder.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    Vector2 p = shape[i] + vNorm[i] * overhang;
                    builder.AddVertex(new Vector3(p.x + centerX, baseY, p.y));
                }
                // Winding: fan reversed so the normal faces +Y (visible from above).
                for (int i = 1; i < n - 1; i++)
                    builder.AddTriangle(topStart, topStart + i + 1, topStart + i);
            }
            return builder.ToMesh();
        }

        static Mesh BuildBottomMesh(List<Run> bottomRuns, PlatformerSettings s, HashSet<Vector2Int> cells)
        {
            if (s.bottomMode == BottomMode.IslandNoise)
                return BuildConnectedIslandBottomMesh(cells, s);

            if (bottomRuns == null || bottomRuns.Count == 0)
                return null;

            var builder = new MeshBuilder();
            float sideUnderlap = Mathf.Max(0f, s.sideUnderlap);
            foreach (var run in bottomRuns)
            {
                var shape = BuildRoundedRectShape(run.Width, s.blockDepth, s.radius, s.meshSegments);
                bool leftContact = cells.Contains(new Vector2Int(run.StartX - 1, run.Y));
                bool rightContact = cells.Contains(new Vector2Int(run.EndX + 1, run.Y));
                float leftUnderlap = leftContact ? sideUnderlap : 0f;
                float rightUnderlap = rightContact ? sideUnderlap : 0f;
                ApplySideUnderlapToShape(shape, leftUnderlap, rightUnderlap);
                int n = shape.Count;
                if (n < 3) continue;

                float centerX = run.CenterX;
                float baseY = run.Y + s.yPosition - 0.5f;

                switch (s.bottomMode)
                {
                    case BottomMode.Flat:
                        AppendFlatBottom(builder, shape, centerX, baseY);
                        break;
                    case BottomMode.IslandNoise:
                        AppendRockyIslandBottom(builder, shape, centerX, baseY, s);
                        break;
                    case BottomMode.Bevel:
                        AppendFlatBottom(builder, shape, centerX, baseY);
                        break;
                }
            }

            return builder.ToMesh();
        }

        static Mesh BuildConnectedIslandBottomMesh(HashSet<Vector2Int> cells, PlatformerSettings s)
        {
            if (cells == null || cells.Count == 0)
                return null;

            var builder = new MeshBuilder();
            var remaining = new HashSet<Vector2Int>(cells);
            var queue = new Queue<Vector2Int>();

            while (remaining.Count > 0)
            {
                Vector2Int seed = default;
                foreach (var cell in remaining)
                {
                    seed = cell;
                    break;
                }

                remaining.Remove(seed);
                queue.Enqueue(seed);

                var componentCells = new List<Vector2Int> { seed };

                while (queue.Count > 0)
                {
                    Vector2Int cell = queue.Dequeue();

                    EnqueueIfPresent(cell + Vector2Int.left);
                    EnqueueIfPresent(cell + Vector2Int.right);
                    EnqueueIfPresent(cell + Vector2Int.up);
                    EnqueueIfPresent(cell + Vector2Int.down);
                }

                void EnqueueIfPresent(Vector2Int candidate)
                {
                    if (!remaining.Remove(candidate))
                        return;

                    componentCells.Add(candidate);
                    queue.Enqueue(candidate);
                }

                AppendConnectedIslandProfileBottom(builder, componentCells, cells, s);
            }

            return builder.ToMesh();
        }

        static void AppendConnectedIslandProfileBottom(MeshBuilder builder, List<Vector2Int> componentCells, HashSet<Vector2Int> allCells, PlatformerSettings s)
        {
            if (componentCells == null || componentCells.Count == 0)
                return;

            var minYByX = new Dictionary<int, int>();
            var maxYByX = new Dictionary<int, int>();
            int minX = int.MaxValue;
            int maxX = int.MinValue;

            for (int i = 0; i < componentCells.Count; i++)
            {
                Vector2Int cell = componentCells[i];
                if (cell.x < minX) minX = cell.x;
                if (cell.x > maxX) maxX = cell.x;

                if (!minYByX.TryGetValue(cell.x, out int currentMin) || cell.y < currentMin)
                    minYByX[cell.x] = cell.y;
                if (!maxYByX.TryGetValue(cell.x, out int currentMax) || cell.y > currentMax)
                    maxYByX[cell.x] = cell.y;
            }

            int maxColumnHeight = 1;
            foreach (var kv in minYByX)
            {
                int h = maxYByX[kv.Key] - kv.Value + 1;
                if (h > maxColumnHeight) maxColumnHeight = h;
            }

            float sideUnderlap = Mathf.Max(0f, s.sideUnderlap);
            bool leftContact = HasComponentSideContact(componentCells, allCells, minX, -1);
            bool rightContact = HasComponentSideContact(componentCells, allCells, maxX, 1);
            float leftExt = leftContact ? sideUnderlap : 0f;
            float rightExt = rightContact ? sideUnderlap : 0f;

            float firstBottomY = minYByX[minX] + s.yPosition - 0.5f;
            float lastBottomY = minYByX[maxX] + s.yPosition - 0.5f;
            float worldMinX = minX - 0.5f - leftExt;
            float worldMaxX = maxX + 0.5f + rightExt;

            // Stair profile along the true exposed bottom silhouette in worldX → worldY.
            // Each column contributes its own bottom Y (from the lowest cell in that column),
            // so the resulting polyline faithfully follows the wall's per-run bottom edge.
            var topProfile = new List<Vector2>();
            topProfile.Add(new Vector2(worldMinX, firstBottomY));
            for (int x = minX; x <= maxX; x++)
            {
                float currentBottomY = minYByX[x] + s.yPosition - 0.5f;
                topProfile.Add(new Vector2(x + 0.5f, currentBottomY));

                if (x < maxX)
                {
                    float nextBottomY = minYByX[x + 1] + s.yPosition - 0.5f;
                    if (!Mathf.Approximately(currentBottomY, nextBottomY))
                        topProfile.Add(new Vector2(x + 0.5f, nextBottomY));
                }
            }
            if (topProfile[topProfile.Count - 1].x < worldMaxX)
                topProfile.Add(new Vector2(worldMaxX, lastBottomY));

            if (topProfile.Count < 2)
                return;

            // Column-height profile in worldX → heightRatio (0..1 relative to the block's
            // tallest column). Used by the apex pass to sag deeper under heavy stacks.
            var heightProfile = new List<Vector2>();
            heightProfile.Add(new Vector2(worldMinX, ColumnHeightRatio(minYByX, maxYByX, minX, maxColumnHeight)));
            for (int x = minX; x <= maxX; x++)
            {
                float ratio = ColumnHeightRatio(minYByX, maxYByX, x, maxColumnHeight);
                heightProfile.Add(new Vector2(x + 0.5f, ratio));
            }
            if (heightProfile[heightProfile.Count - 1].x < worldMaxX)
                heightProfile.Add(new Vector2(worldMaxX, ColumnHeightRatio(minYByX, maxYByX, maxX, maxColumnHeight)));

            float depth = s.blockDepth;
            float zFront = -depth * 0.5f;
            float zBack = depth * 0.5f;
            float halfWidth = (worldMaxX - worldMinX) * 0.5f;
            float halfDepth = depth * 0.5f;
            float r = Mathf.Clamp(s.radius, 0f, Mathf.Max(0f, Mathf.Min(halfWidth, halfDepth) - 1e-4f));
            int arcSegs = Mathf.Max(1, s.meshSegments);

            // Walk the ring so the island starts on exactly the wall's bottom silhouette:
            //   front-left arc (in XZ, at firstBottomY) →
            //   front edge walking every stair step of topProfile →
            //   front-right arc (at lastBottomY) →
            //   back-right arc →
            //   back edge walking topProfile reversed →
            //   back-left arc → close.
            var topRing = new List<Vector3>();

            AppendArc3DXZ(topRing, worldMinX + r, zFront + r, r, Mathf.PI, Mathf.PI * 1.5f, arcSegs, firstBottomY);
            AppendProfileEdge3D(topRing, topProfile, worldMinX + r, worldMaxX - r, zFront, reverse: false);
            AppendArc3DXZ(topRing, worldMaxX - r, zFront + r, r, Mathf.PI * 1.5f, Mathf.PI * 2f, arcSegs, lastBottomY);
            AppendArc3DXZ(topRing, worldMaxX - r, zBack - r, r, 0f, Mathf.PI * 0.5f, arcSegs, lastBottomY);
            AppendProfileEdge3D(topRing, topProfile, worldMinX + r, worldMaxX - r, zBack, reverse: true);
            AppendArc3DXZ(topRing, worldMinX + r, zBack - r, r, Mathf.PI * 0.5f, Mathf.PI, arcSegs, firstBottomY);

            if (topRing.Count < 3)
                return;

            AppendRockyIslandFromTopRing(builder, topRing, s, topProfile, heightProfile);
        }

        static float ColumnHeightRatio(Dictionary<int, int> minYByX, Dictionary<int, int> maxYByX, int x, int maxHeight)
        {
            if (maxHeight <= 0) return 0f;
            if (!minYByX.TryGetValue(x, out int lo) || !maxYByX.TryGetValue(x, out int hi)) return 0f;
            int h = hi - lo + 1;
            return Mathf.Clamp01((float)h / maxHeight);
        }

        static void AppendArc3DXZ(List<Vector3> ring, float cx, float cz, float r, float a0, float a1, int segs, float y)
        {
            for (int i = 0; i < segs; i++)
            {
                float t = (float)i / segs;
                float a = Mathf.Lerp(a0, a1, t);
                ring.Add(new Vector3(cx + Mathf.Cos(a) * r, y, cz + Mathf.Sin(a) * r));
            }
        }

        static void AppendProfileEdge3D(List<Vector3> ring, List<Vector2> topProfile, float xMin, float xMax, float z, bool reverse)
        {
            if (xMax < xMin + 1e-5f)
                return;

            var segPoints = new List<Vector2>();
            segPoints.Add(new Vector2(xMin, SampleProfileY(topProfile, xMin)));
            for (int i = 0; i < topProfile.Count; i++)
            {
                float px = topProfile[i].x;
                if (px > xMin + 1e-5f && px < xMax - 1e-5f)
                    segPoints.Add(topProfile[i]);
            }
            segPoints.Add(new Vector2(xMax, SampleProfileY(topProfile, xMax)));

            if (reverse)
            {
                for (int i = segPoints.Count - 1; i >= 0; i--)
                    ring.Add(new Vector3(segPoints[i].x, segPoints[i].y, z));
            }
            else
            {
                for (int i = 0; i < segPoints.Count; i++)
                    ring.Add(new Vector3(segPoints[i].x, segPoints[i].y, z));
            }
        }

        static bool HasComponentSideContact(List<Vector2Int> componentCells, HashSet<Vector2Int> allCells, int edgeX, int direction)
        {
            if (componentCells == null || allCells == null)
                return false;

            for (int i = 0; i < componentCells.Count; i++)
            {
                Vector2Int cell = componentCells[i];
                if (cell.x != edgeX)
                    continue;

                if (allCells.Contains(new Vector2Int(cell.x + direction, cell.y)))
                    return true;
            }

            return false;
        }

        static float SampleProfileY(List<Vector2> profile, float x)
        {
            if (profile == null || profile.Count == 0)
                return 0f;

            if (x <= profile[0].x)
                return profile[0].y;

            int last = profile.Count - 1;
            if (x >= profile[last].x)
                return profile[last].y;

            for (int i = 0; i < last; i++)
            {
                Vector2 a = profile[i];
                Vector2 b = profile[i + 1];
                float minX = Mathf.Min(a.x, b.x);
                float maxX = Mathf.Max(a.x, b.x);
                if (x < minX || x > maxX)
                    continue;

                float span = b.x - a.x;
                if (Mathf.Abs(span) <= 1e-5f)
                    return b.y;

                float t = Mathf.InverseLerp(a.x, b.x, x);
                return Mathf.Lerp(a.y, b.y, t);
            }

            return profile[last].y;
        }

        static void AppendRockyIslandFromTopRing(MeshBuilder builder, List<Vector3> topRing, PlatformerSettings s, List<Vector2> topProfile = null, List<Vector2> heightProfile = null)
        {
            var denseRing = DensifyRing3D(topRing, Mathf.Max(2, s.bottomNoiseResolution + Mathf.CeilToInt(s.meshSegments * 0.5f)));
            int n = denseRing.Count;
            if (n < 3)
                return;

            int rings = Mathf.Max(3, s.bottomNoiseResolution * 2 + 1);
            Vector2 centroidXZ = Vector2.zero;
            float minTopY = float.MaxValue;
            float minRingX = float.MaxValue;
            float maxRingX = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = denseRing[i];
                centroidXZ += new Vector2(p.x, p.z);
                if (p.y < minTopY) minTopY = p.y;
                if (p.x < minRingX) minRingX = p.x;
                if (p.x > maxRingX) maxRingX = p.x;
            }
            centroidXZ /= n;

            // Precompute the target apex Y for every ring column.
            //   Base  = smoothed silhouette Y at this X (curve follows the wall contour).
            //   Drop  = sag below the silhouette, scaled by columnHeight.
            //   Noise = rocky wobble along the whole curve.
            bool hasProfileForApex = topProfile != null && topProfile.Count >= 2;
            bool hasHeightProfile = heightProfile != null && heightProfile.Count >= 2;
            float centerDrop = Mathf.Max(0f, s.bottomNoiseAmplitude);
            float apexZ = centroidXZ.y;
            float noiseScale = Mathf.Max(0.0001f, s.bottomNoiseScale);
            float waveAmp = centerDrop * 0.2f;
            const float baselineDropRatio = 0.3f;           // minimum sag under short runs
            const float maxDropRatio = 1.0f;                // max sag under the tallest runs
            const float edgeAnchor = 0.2f;                  // fade near component ends
            const float silhouetteSmoothRadius = 1.25f;
            const float heightSmoothRadius = 0.75f;

            var apexYByI = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 source = denseRing[i];

                float silhouetteY = hasProfileForApex
                    ? SmoothProfileY(topProfile, source.x, silhouetteSmoothRadius)
                    : source.y;

                float heightRatio = hasHeightProfile
                    ? Mathf.Clamp01(SmoothProfileY(heightProfile, source.x, heightSmoothRadius))
                    : 1f;

                float rt = Mathf.InverseLerp(minRingX, maxRingX, source.x);
                float edgeFade = Mathf.SmoothStep(0f, 1f, Mathf.Min(rt, 1f - rt) / edgeAnchor);

                float dropRatio = Mathf.Lerp(baselineDropRatio, maxDropRatio, heightRatio) * edgeFade;
                float drop = centerDrop * dropRatio;

                float nx = (source.x + s.bottomNoiseSeed + 17.3f) / noiseScale;
                float nz = (apexZ + s.bottomNoiseSeed + 42.7f) / noiseScale;
                float centered = (Mathf.PerlinNoise(nx * 2.1f, nz * 2.1f) - 0.5f) * 2f;
                float noiseOffset = centered * waveAmp * edgeFade;

                // Clamp apex strictly below source.y so the Lerp sweep stays monotonic.
                apexYByI[i] = Mathf.Min(source.y - 1e-4f, silhouetteY - drop + noiseOffset);
            }

            // Rings descend from the outer stair (t=0, Y = source.y) to the precomputed
            // apex (t=1, Y = apexYByI[i]). Z fully collapses toward the centroid so the
            // front/back halves meet cleanly at the keel.
            int baseIndex = builder.VertexCount;
            for (int ring = 0; ring <= rings; ring++)
            {
                float t = (float)ring / rings;
                // Use a circular easing curve instead of SmoothStep.
                // This ensures that at t=0, the lateral/depth inward pull is near zero (1-cos),
                // while the vertical drop is steep (sin). This tangency matches the vertical walls
                // giving a perfectly smooth, rounded 'shoulder' rather than a sharp 90-degree seam.
                float angle = t * Mathf.PI * 0.5f;
                float tXZ = 1f - Mathf.Cos(angle);
                float tY = Mathf.Sin(angle);

                for (int i = 0; i < n; i++)
                {
                    Vector3 source = denseRing[i];
                    Vector2 sourceXZ = new Vector2(source.x, source.z);
                    Vector2 pointXZ = new Vector2(sourceXZ.x, Mathf.Lerp(sourceXZ.y, centroidXZ.y, tXZ));
                    Vector2 radialDir = (pointXZ - centroidXZ).sqrMagnitude > 1e-6f
                        ? (pointXZ - centroidXZ).normalized
                        : Vector2.zero;

                    float lateral = ComputeRockyIslandLateralNoise(sourceXZ, t, s);
                    pointXZ += radialDir * lateral;

                    float y = Mathf.Lerp(source.y, apexYByI[i], tY);
                    builder.AddVertex(new Vector3(pointXZ.x, y, pointXZ.y));
                }
            }

            for (int ring = 0; ring < rings; ring++)
            {
                int ringA = baseIndex + ring * n;
                int ringB = baseIndex + (ring + 1) * n;
                for (int i = 0; i < n; i++)
                {
                    int next = (i + 1) % n;
                    builder.AddTriangle(ringA + i, ringA + next, ringB + i);
                    builder.AddTriangle(ringA + next, ringB + next, ringB + i);
                }
            }
        }

        // Box-filter smoothing of the stair profile Y around x, over ±radius. Turns the
        // stair silhouette into a smooth bezier-like curve that the apex ridge can follow.
        static float SmoothProfileY(List<Vector2> profile, float x, float radius)
        {
            if (profile == null || profile.Count < 2)
                return 0f;
            const int samples = 9;
            float total = 0f;
            for (int i = 0; i < samples; i++)
            {
                float t = (samples > 1) ? (float)i / (samples - 1) : 0.5f;
                float sx = Mathf.Lerp(x - radius, x + radius, t);
                total += SampleProfileY(profile, sx);
            }
            return total / samples;
        }

        static List<Vector3> DensifyRing3D(List<Vector3> ring, int subdivisionsPerEdge)
        {
            var dense = new List<Vector3>();
            if (ring == null || ring.Count < 3)
                return dense;

            int subdivisions = Mathf.Max(1, subdivisionsPerEdge);
            for (int i = 0; i < ring.Count; i++)
            {
                Vector3 a = ring[i];
                Vector3 b = ring[(i + 1) % ring.Count];
                for (int step = 0; step < subdivisions; step++)
                {
                    float t = (float)step / subdivisions;
                    dense.Add(Vector3.Lerp(a, b, t));
                }
            }

            return dense;
        }

        static List<Vector2> DensifyProfile(List<Vector2> profile, int subdivisionsPerSegment)
        {
            var dense = new List<Vector2>();
            if (profile == null || profile.Count == 0)
                return dense;

            int subdivisions = Mathf.Max(1, subdivisionsPerSegment);
            for (int i = 0; i < profile.Count - 1; i++)
            {
                Vector2 a = profile[i];
                Vector2 b = profile[i + 1];
                for (int step = 0; step < subdivisions; step++)
                {
                    float t = (float)step / subdivisions;
                    dense.Add(Vector2.Lerp(a, b, t));
                }
            }

            dense.Add(profile[profile.Count - 1]);
            return dense;
        }

        static List<Vector2> BuildRockyIslandBottomProfile(List<Vector2> topProfile, PlatformerSettings s)
        {
            var bottom = new List<Vector2>(topProfile.Count);
            if (topProfile == null || topProfile.Count == 0)
                return bottom;

            float minX = topProfile[0].x;
            float maxX = topProfile[0].x;
            float maxTopY = topProfile[0].y;
            for (int i = 1; i < topProfile.Count; i++)
            {
                Vector2 p = topProfile[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y > maxTopY) maxTopY = p.y;
            }

            float centerX = (minX + maxX) * 0.5f;
            float halfWidth = Mathf.Max(0.0001f, (maxX - minX) * 0.5f);
            float amplitude = Mathf.Max(0f, s.bottomNoiseAmplitude);
            float sharpness = Mathf.Max(0.0001f, s.bottomIslandSharpness);
            float smooth = Mathf.Clamp01(s.bottomIslandSmooth);
            float noiseScale = Mathf.Max(0.0001f, s.bottomNoiseScale);

            for (int i = 0; i < topProfile.Count; i++)
            {
                Vector2 top = topProfile[i];
                float normalized = 1f - Mathf.Clamp01(Mathf.Abs(top.x - centerX) / halfWidth);
                float radial = Mathf.Pow(normalized, sharpness);
                float smoothed = Mathf.Lerp(radial, Mathf.SmoothStep(0f, 1f, radial), smooth);

                float noiseA = Mathf.PerlinNoise((top.x + s.bottomNoiseSeed) / noiseScale, 0.13f);
                float noiseB = Mathf.PerlinNoise((top.x + s.bottomNoiseSeed + 17.1f) / (noiseScale * 0.6f), 0.71f);
                float noise = Mathf.Lerp(noiseA, noiseB, 0.45f);
                float noisyProfile = Mathf.Lerp(0.8f, 1.35f, noise);
                float drop = amplitude * smoothed * noisyProfile;

                // Ensure the global bottom profile hangs from the highest underside band.
                float y = maxTopY - drop;
                bottom.Add(new Vector2(top.x, Mathf.Min(top.y, y)));
            }

            return bottom;
        }

        static void AppendFlatBottom(MeshBuilder builder, List<Vector2> shape, float centerX, float baseY)
        {
            int n = shape.Count;
            int bottomStart = builder.VertexCount;
            for (int i = 0; i < n; i++)
            {
                Vector2 p = shape[i];
                builder.AddVertex(new Vector3(p.x + centerX, baseY, p.y));
            }

            // Reverse winding so the cap is visible from below.
            for (int i = 1; i < n - 1; i++)
                builder.AddTriangle(bottomStart, bottomStart + i, bottomStart + i + 1);
        }

        static void AppendRockyIslandBottom(
            MeshBuilder builder,
            List<Vector2> shape,
            float centerX,
            float baseY,
            PlatformerSettings s)
        {
            var denseShape = BuildDenseBottomOutline(shape, s);
            int n = denseShape.Count;
            if (n < 3)
                return;

            int rings = Mathf.Max(3, s.bottomNoiseResolution * 2 + 1);
            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < n; i++)
                centroid += denseShape[i];
            centroid /= n;

            int baseIndex = builder.VertexCount;
            for (int ring = 0; ring <= rings; ring++)
            {
                float t = (float)ring / rings;
                for (int i = 0; i < n; i++)
                {
                    Vector2 source = denseShape[i];
                    Vector2 point = Vector2.Lerp(source, centroid, t);
                    Vector2 radialDir = (point - centroid).sqrMagnitude > 1e-6f
                        ? (point - centroid).normalized
                        : Vector2.zero;

                    float lateral = ComputeRockyIslandLateralNoise(source, t, s);
                    point += radialDir * lateral;

                    float drop = ComputeRockyIslandDrop(source, centroid, t, s);
                    builder.AddVertex(new Vector3(point.x + centerX, baseY - drop, point.y));
                }
            }

            int centroidIndex = builder.VertexCount;
            float centerDrop = Mathf.Max(0f, s.bottomNoiseAmplitude);
            builder.AddVertex(new Vector3(centroid.x + centerX, baseY - centerDrop, centroid.y));

            for (int ring = 0; ring < rings; ring++)
            {
                int ringA = baseIndex + ring * n;
                int ringB = baseIndex + (ring + 1) * n;
                for (int i = 0; i < n; i++)
                {
                    int next = (i + 1) % n;
                    builder.AddTriangle(ringA + i, ringA + next, ringB + i);
                    builder.AddTriangle(ringA + next, ringB + next, ringB + i);
                }
            }

            int lastRing = baseIndex + rings * n;
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                builder.AddTriangle(lastRing + i, lastRing + next, centroidIndex);
            }
        }

        static List<Vector2> BuildDenseBottomOutline(List<Vector2> shape, PlatformerSettings s)
        {
            var dense = new List<Vector2>();
            if (shape == null || shape.Count < 2)
                return dense;

            int edgeSubdivisions = Mathf.Max(2, s.bottomNoiseResolution + Mathf.CeilToInt(s.meshSegments * 0.5f));
            for (int i = 0; i < shape.Count; i++)
            {
                int next = (i + 1) % shape.Count;
                Vector2 a = shape[i];
                Vector2 b = shape[next];

                for (int step = 0; step < edgeSubdivisions; step++)
                {
                    float t = (float)step / edgeSubdivisions;
                    dense.Add(Vector2.Lerp(a, b, t));
                }
            }

            return dense;
        }

        static float ComputeRockyIslandDrop(Vector2 point, Vector2 centroid, float t, PlatformerSettings s)
        {
            float amplitude = Mathf.Max(0f, s.bottomNoiseAmplitude);
            float sharpness = Mathf.Max(0.0001f, s.bottomIslandSharpness);
            float smooth = Mathf.Clamp01(s.bottomIslandSmooth);
            float noiseScale = Mathf.Max(0.0001f, s.bottomNoiseScale);

            Vector2 noiseCoord = (point + Vector2.one * s.bottomNoiseSeed) / noiseScale;
            float noiseA = Mathf.PerlinNoise(noiseCoord.x, noiseCoord.y);
            float noiseB = Mathf.PerlinNoise(noiseCoord.x * 2.13f + 17.37f, noiseCoord.y * 2.13f + 4.91f);
            float noise = Mathf.Lerp(noiseA, noiseB, 0.4f);
            float radial = Mathf.Pow(Mathf.Clamp01(t), sharpness);
            float smoothed = Mathf.Lerp(radial, Mathf.SmoothStep(0f, 1f, radial), smooth);
            float noisyProfile = Mathf.Lerp(0.85f, 1.35f, noise);

            float centerBias = 1f - Mathf.Clamp01((point - centroid).magnitude);
            float bias = Mathf.Lerp(1f, 0.85f + centerBias * 0.15f, smooth);
            return amplitude * smoothed * noisyProfile * bias;
        }

        static float ComputeRockyIslandLateralNoise(Vector2 point, float t, PlatformerSettings s)
        {
            float noiseScale = Mathf.Max(0.0001f, s.bottomNoiseScale);
            Vector2 noiseCoord = (point + Vector2.one * (s.bottomNoiseSeed + 31.7f)) / noiseScale;
            float noise = Mathf.PerlinNoise(noiseCoord.x * 1.7f, noiseCoord.y * 1.7f);
            float centered = (noise - 0.5f) * 2f;
            float strength = Mathf.Max(0f, s.bottomNoiseAmplitude) * 0.06f;
            return centered * strength * Mathf.Clamp01(t);
        }

        static Vector2[] ComputeVertexOutwardNormals(List<Vector2> shape, int n)
        {
            var vn = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = shape[(i - 1 + n) % n];
                Vector2 cur  = shape[i];
                Vector2 next = shape[(i + 1) % n];
                Vector2 e1 = cur - prev;
                Vector2 e2 = next - cur;
                float m1 = e1.magnitude, m2 = e2.magnitude;
                Vector2 n1 = m1 > 1e-6f ? new Vector2(e1.y, -e1.x) / m1 : Vector2.zero;
                Vector2 n2 = m2 > 1e-6f ? new Vector2(e2.y, -e2.x) / m2 : Vector2.zero;
                Vector2 avg = n1 + n2;
                float mg = avg.magnitude;
                vn[i] = mg > 1e-6f ? avg / mg : n2;
            }
            return vn;
        }

        // ─────────────────────────────────────────────
        // Crown (skirt) — curved grass overhang around each top run,
        // built with the SAME cosine profile + UV scheme as the 3D skirt
        // (see ProceduralTileMeshGenerator) so it honours the tile rule's
        // skirtWidth / skirtHeight / skirtSegments / skirtUVScale /
        // skirtUVOffsetY settings and can reuse its skirt material.
        // ─────────────────────────────────────────────

        static Mesh BuildCrownMesh(List<Run> topRuns, PlatformerSettings s, HashSet<Vector2Int> cells)
        {
            if (topRuns == null || topRuns.Count == 0) return null;
            if (s.skirtWidth <= 0f || s.skirtHeight <= 0f) return null;

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            int skirtSegs = Mathf.Max(1, s.skirtSegments);
            float skirtW = s.skirtWidth;
            float skirtH = s.skirtHeight;
            float uvScale = s.skirtUVScale;
            float uvOffsetY = s.skirtUVOffsetY;
            float sideUnderlap = Mathf.Max(0f, s.sideUnderlap);

            foreach (var run in topRuns)
            {
                var shape = BuildRoundedRectShape(run.Width, s.blockDepth, s.radius, s.meshSegments);
                bool leftContact = cells.Contains(new Vector2Int(run.StartX - 1, run.Y));
                bool rightContact = cells.Contains(new Vector2Int(run.EndX + 1, run.Y));
                float leftUnderlap = leftContact ? sideUnderlap : 0f;
                float rightUnderlap = rightContact ? sideUnderlap : 0f;
                ApplySideUnderlapToShape(shape, leftUnderlap, rightUnderlap);
                int n = shape.Count;
                if (n < 2) continue;

                float centerX = run.CenterX;
                float baseY = run.Y + s.yPosition + 0.5f;
                float topY = baseY; // start the skirt exactly at the wall top (flush with the flat floor cap)

                // 1) Treat the full crown outline as exterior so the skirt wraps all the way
                //    around each top run, regardless of neighbouring walls.
                bool[] exterior = new bool[n];
                for (int i = 0; i < n; i++)
                    exterior[i] = true;

                // 2) Per-vertex outward normals (average of adjacent edge perpendiculars,
                //    weighted only by exterior edges when possible).
                Vector2[] vNorm = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    Vector2 prev = shape[(i - 1 + n) % n];
                    Vector2 cur  = shape[i];
                    Vector2 next = shape[(i + 1) % n];
                    Vector2 e1 = (cur - prev);
                    Vector2 e2 = (next - cur);
                    float m1 = e1.magnitude, m2 = e2.magnitude;
                    bool prevExt = exterior[(i - 1 + n) % n];
                    bool curExt  = exterior[i];
                    Vector2 n1 = m1 > 1e-6f ? new Vector2(e1.y, -e1.x) / m1 : Vector2.zero;
                    Vector2 n2 = m2 > 1e-6f ? new Vector2(e2.y, -e2.x) / m2 : Vector2.zero;
                    Vector2 avg = Vector2.zero;
                    if (prevExt) avg += n1;
                    if (curExt)  avg += n2;
                    if (avg.sqrMagnitude < 1e-8f) avg = n1 + n2; // fallback if both neighbours interior
                    float mg = avg.magnitude;
                    vNorm[i] = mg > 1e-6f ? avg / mg : n2;
                }

                // 3) Per-vertex U, accumulating only along exterior edges (= visible perimeter).
                //    Interior edges still carry a U value (= end of the previous exterior run)
                //    so the two corner rings that share them stay connected without stretching.
                float[] uPerVert = new float[n];
                float uAcc = 0f;
                for (int i = 0; i < n; i++)
                {
                    uPerVert[i] = uAcc;
                    if (exterior[i])
                    {
                        float dx = shape[(i + 1) % n].x - shape[i].x;
                        float dz = shape[(i + 1) % n].y - shape[i].y;
                        uAcc += Mathf.Sqrt(dx * dx + dz * dz);
                    }
                }

                // 4) Emit skirtSegs + 1 rings of n vertices each. Rings are shared between
                //    segments — the strip stays continuous, no duplicated seams.
                //    Ring 0 sits flush with the floor's outer edge (shape + floorOverhang),
                //    so the skirt starts exactly where the lip ends, then curves outward
                //    and down to the final skirtWidth beyond that.
                float overhang = Mathf.Max(0f, s.floorOverhang);
                int baseIdx = verts.Count;
                for (int r = 0; r <= skirtSegs; r++)
                {
                    float t = (float)r / skirtSegs;
                    float angle = t * Mathf.PI * 0.5f;
                    float off  = overhang + Mathf.Sin(angle) * skirtW;  // sin profile (matches 3D) + lip
                    float drop = (1f - Mathf.Cos(angle)) * skirtH;
                    float y = topY - drop;
                    for (int i = 0; i < n; i++)
                    {
                        Vector2 p = shape[i] + vNorm[i] * off;
                        verts.Add(new Vector3(p.x + centerX, y, p.y));
                        uvs.Add(new Vector2(uPerVert[i] * uvScale, (1f - t) * uvScale + uvOffsetY));
                    }
                }

                // 5) Connect consecutive rings with quads, only on exterior edges.
                //    Shape is CCW in world XZ (no Z-mirror), so winding (i, next, i+n) on the lower
                //    strip produces outward normals.
                for (int r = 0; r < skirtSegs; r++)
                {
                    int rA = baseIdx + r * n;
                    int rB = baseIdx + (r + 1) * n;
                    for (int i = 0; i < n; i++)
                    {
                        if (!exterior[i]) continue;
                        int next = (i + 1) % n;
                        tris.Add(rA + i);
                        tris.Add(rA + next);
                        tris.Add(rB + i);

                        tris.Add(rA + next);
                        tris.Add(rB + next);
                        tris.Add(rB + i);
                    }
                }

                // 6) Cap the open ends of each exposed skirt run so adjacent height steps
                // don't reveal slits between the crown and the wall.
                foreach (var runInfo in BuildExteriorEdgeRuns(exterior))
                {
                    int startVertex = runInfo.startEdge;
                    int endVertex = (runInfo.endEdge + 1) % n;

                    Vector2 startTangent2D = (shape[(runInfo.startEdge + 1) % n] - shape[runInfo.startEdge]).normalized;
                    Vector2 endTangent2D = (shape[(runInfo.endEdge + 1) % n] - shape[runInfo.endEdge]).normalized;
                    Vector2 startReturnDir2D = (shape[(startVertex - 1 + n) % n] - shape[startVertex]).normalized;
                    Vector2 endReturnDir2D = (shape[(endVertex + 1) % n] - shape[endVertex]).normalized;
                    float returnLength = Mathf.Min(skirtW * 0.75f, 0.12f);

                    Vector3 startDesiredNormal = new Vector3(-startTangent2D.x, 0f, -startTangent2D.y);
                    Vector3 endDesiredNormal = new Vector3(endTangent2D.x, 0f, endTangent2D.y);

                    AddCrownBoundaryCap(
                        verts, uvs, tris,
                        shape, vNorm,
                        centerX, topY,
                        overhang, skirtW, skirtH, skirtSegs,
                        uvScale, uvOffsetY,
                        startVertex, startDesiredNormal, startReturnDir2D, returnLength);

                    AddCrownBoundaryCap(
                        verts, uvs, tris,
                        shape, vNorm,
                        centerX, topY,
                        overhang, skirtW, skirtH, skirtSegs,
                        uvScale, uvOffsetY,
                        endVertex, endDesiredNormal, endReturnDir2D, returnLength);
                }
            }

            if (verts.Count == 0) return null;

            var mesh = new Mesh { name = "QT_Platformer_Crown" };
            if (verts.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void ApplySideUnderlapToShape(List<Vector2> shape, float leftUnderlap, float rightUnderlap)
        {
            if (shape == null || shape.Count == 0)
                return;

            float left = Mathf.Max(0f, leftUnderlap);
            float right = Mathf.Max(0f, rightUnderlap);
            if (left <= 0f && right <= 0f)
                return;

            for (int i = 0; i < shape.Count; i++)
            {
                Vector2 p = shape[i];
                if (p.x < -1e-5f)
                    p.x -= left;
                else if (p.x > 1e-5f)
                    p.x += right;
                else
                    p.x += right - left;

                shape[i] = p;
            }
        }

        static List<(int startEdge, int endEdge)> BuildExteriorEdgeRuns(bool[] exterior)
        {
            var runs = new List<(int startEdge, int endEdge)>();
            if (exterior == null || exterior.Length == 0)
                return runs;

            int n = exterior.Length;
            bool hasExterior = false;
            for (int i = 0; i < n; i++)
            {
                if (exterior[i])
                {
                    hasExterior = true;
                    break;
                }
            }

            if (!hasExterior)
                return runs;

            var starts = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!exterior[i]) continue;
                int prev = (i - 1 + n) % n;
                if (!exterior[prev])
                    starts.Add(i);
            }

            // A fully exposed loop has no open ends to cap.
            if (starts.Count == 0)
                return runs;

            foreach (int start in starts)
            {
                int edge = start;
                int safety = n + 1;
                while (safety-- > 0 && exterior[edge])
                {
                    int nextEdge = (edge + 1) % n;
                    if (!exterior[nextEdge])
                        break;
                    edge = nextEdge;
                }

                runs.Add((start, edge));
            }

            return runs;
        }

        static void AddCrownBoundaryCap(
            List<Vector3> verts,
            List<Vector2> uvs,
            List<int> tris,
            List<Vector2> shape,
            Vector2[] vNorm,
            float centerX,
            float topY,
            float overhang,
            float skirtWidth,
            float skirtHeight,
            int skirtSegments,
            float uvScale,
            float uvOffsetY,
            int vertexIndex,
            Vector3 desiredNormal,
            Vector2 returnDirection2D,
            float returnLength)
        {
            if (shape == null || vNorm == null || shape.Count == 0 || skirtSegments < 1)
                return;

            int baseIndex = verts.Count;
            Vector2 basePoint = shape[vertexIndex];
            Vector2 outward = vNorm[vertexIndex];
            Vector2 returnDir = returnDirection2D.sqrMagnitude > 1e-6f
                ? returnDirection2D.normalized
                : Vector2.zero;

            for (int r = 0; r <= skirtSegments; r++)
            {
                float t = (float)r / skirtSegments;
                float angle = t * Mathf.PI * 0.5f;
                float off = overhang + Mathf.Sin(angle) * skirtWidth;
                float drop = (1f - Mathf.Cos(angle)) * skirtHeight;
                float y = topY - drop;
                float returnOffset = Mathf.Lerp(returnLength * 0.4f, returnLength, Mathf.Sin(angle));
                Vector2 anchorPoint = basePoint + outward * off;
                Vector2 wrappedPoint = anchorPoint + returnDir * returnOffset;

                verts.Add(new Vector3(anchorPoint.x + centerX, y, anchorPoint.y));
                uvs.Add(new Vector2(0f, (1f - t) * uvScale + uvOffsetY));

                verts.Add(new Vector3(wrappedPoint.x + centerX, y, wrappedPoint.y));
                uvs.Add(new Vector2(uvScale, (1f - t) * uvScale + uvOffsetY));
            }

            for (int r = 0; r < skirtSegments; r++)
            {
                int innerA = baseIndex + r * 2;
                int outerA = innerA + 1;
                int innerB = innerA + 2;
                int outerB = innerA + 3;
                AddOrientedQuad(verts, tris, innerA, outerA, innerB, outerB, desiredNormal);
            }
        }

        static void AddOrientedQuad(
            List<Vector3> verts,
            List<int> tris,
            int a,
            int b,
            int c,
            int d,
            Vector3 desiredNormal)
        {
            Vector3 ab = verts[b] - verts[a];
            Vector3 ac = verts[c] - verts[a];
            Vector3 normal = Vector3.Cross(ab, ac);

            if (Vector3.Dot(normal, desiredNormal) >= 0f)
            {
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);

                tris.Add(c);
                tris.Add(b);
                tris.Add(d);
            }
            else
            {
                tris.Add(a);
                tris.Add(c);
                tris.Add(b);

                tris.Add(c);
                tris.Add(d);
                tris.Add(b);
            }
        }

        // ─────────────────────────────────────────────
        // Rounded-rect shape on XZ (returns 2D points, .x = X, .y = Z).
        // ─────────────────────────────────────────────

        /// <summary>
        /// Rounded-rect shape where only the left/right X-edges can be rounded
        /// depending on <paramref name="roundLeft"/> / <paramref name="roundRight"/>.
        /// When a side is not rounded, its 2 corners become sharp 90° angles so
        /// adjacent runs fuse perfectly. Z-edges are always fully curved (kept at
        /// the run's depth extents — always exposed since every run is a single slab).
        /// </summary>
        static List<Vector2> BuildAdaptiveRoundedShape(float width, float depth, float radius, int curveSegs, bool roundLeft, bool roundRight)
        {
            float w = width * 0.5f;
            float d = depth * 0.5f;
            float maxR = Mathf.Max(0f, Mathf.Min(w, d) - 1e-4f);
            float r = Mathf.Clamp(radius, 0f, maxR);
            var pts = new List<Vector2>();

            if (r <= 1e-5f || (!roundLeft && !roundRight))
            {
                // Fully sharp rectangle.
                pts.Add(new Vector2(-w, -d));
                pts.Add(new Vector2( w, -d));
                pts.Add(new Vector2( w,  d));
                pts.Add(new Vector2(-w,  d));
                return pts;
            }

            int arcSegs = Mathf.Max(1, curveSegs);

            // CCW from bottom-left.
            if (roundLeft)
                ArcAppend(pts, new Vector2(-w + r, -d + r), r, Mathf.PI, Mathf.PI * 1.5f, arcSegs);
            else
                pts.Add(new Vector2(-w, -d));

            if (roundRight)
                ArcAppend(pts, new Vector2( w - r, -d + r), r, Mathf.PI * 1.5f, Mathf.PI * 2f, arcSegs);
            else
                pts.Add(new Vector2( w, -d));

            if (roundRight)
                ArcAppend(pts, new Vector2( w - r,  d - r), r, 0f, Mathf.PI * 0.5f, arcSegs);
            else
                pts.Add(new Vector2( w,  d));

            if (roundLeft)
                ArcAppend(pts, new Vector2(-w + r,  d - r), r, Mathf.PI * 0.5f, Mathf.PI, arcSegs);
            else
                pts.Add(new Vector2(-w,  d));

            return pts;
        }

        static List<Vector2> BuildRoundedRectShape(float width, float depth, float radius, int curveSegs)
        {
            float w = width * 0.5f;
            float d = depth * 0.5f;
            float maxR = Mathf.Max(0f, Mathf.Min(w, d) - 1e-4f);
            float r = Mathf.Clamp(radius, 0f, maxR);
            var pts = new List<Vector2>();

            if (r <= 1e-5f)
            {
                pts.Add(new Vector2(-w, -d));
                pts.Add(new Vector2( w, -d));
                pts.Add(new Vector2( w,  d));
                pts.Add(new Vector2(-w,  d));
                return pts;
            }

            int arcSegs = Mathf.Max(1, curveSegs);
            // Start at bottom-left corner start, go CCW.
            ArcAppend(pts, new Vector2(-w + r, -d + r), r, Mathf.PI,       Mathf.PI * 1.5f, arcSegs);
            ArcAppend(pts, new Vector2( w - r, -d + r), r, Mathf.PI * 1.5f, Mathf.PI * 2f,   arcSegs);
            ArcAppend(pts, new Vector2( w - r,  d - r), r, 0f,              Mathf.PI * 0.5f, arcSegs);
            ArcAppend(pts, new Vector2(-w + r,  d - r), r, Mathf.PI * 0.5f, Mathf.PI,        arcSegs);
            return pts;
        }

        static void ArcAppend(List<Vector2> pts, Vector2 center, float radius, float a0, float a1, int segs)
        {
            for (int i = 0; i < segs; i++)
            {
                float t = (float)i / segs;
                float a = Mathf.Lerp(a0, a1, t);
                pts.Add(new Vector2(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius));
            }
        }

        // ─────────────────────────────────────────────
        // Shape extrusion along Y (shape is XZ plane, depth is Y axis).
        // Produces top cap (at Y = translation.y + depth), bottom cap (at Y = translation.y),
        // and side quads between them.
        // ─────────────────────────────────────────────

        static void ExtrudeShape(MeshBuilder builder, List<Vector2> shape, float depthY,
            Vector3 translation, bool emitTopCap = true, bool emitBottomCap = true)
        {
            if (shape == null || shape.Count < 3) return;

            float yBottom = translation.y;
            float yTop = translation.y + depthY;

            if (emitBottomCap)
            {
                // Bottom cap — triangulate via fan (shape is convex-ish rounded rect).
                int bottomStart = builder.VertexCount;
                for (int i = 0; i < shape.Count; i++)
                    builder.AddVertex(new Vector3(shape[i].x + translation.x, yBottom, shape[i].y + translation.z));
                // CCW from bottom = normal -Y.
                for (int i = 1; i < shape.Count - 1; i++)
                    builder.AddTriangle(bottomStart, bottomStart + i + 1, bottomStart + i);
            }

            if (emitTopCap)
            {
                // Top cap — CCW from top, normal +Y.
                int topStart = builder.VertexCount;
                for (int i = 0; i < shape.Count; i++)
                    builder.AddVertex(new Vector3(shape[i].x + translation.x, yTop, shape[i].y + translation.z));
                for (int i = 1; i < shape.Count - 1; i++)
                    builder.AddTriangle(topStart, topStart + i, topStart + i + 1);
            }

            // Sides — outward-facing quads. Shape is CCW in XZ; the correct winding
            // for an outward normal on edge i→j (going around the shape) is
            // (bottom_j, bottom_i, top_i, top_j).
            for (int i = 0; i < shape.Count; i++)
            {
                int j = (i + 1) % shape.Count;
                Vector3 bi = new Vector3(shape[i].x + translation.x, yBottom, shape[i].y + translation.z);
                Vector3 bj = new Vector3(shape[j].x + translation.x, yBottom, shape[j].y + translation.z);
                Vector3 ti = new Vector3(shape[i].x + translation.x, yTop,    shape[i].y + translation.z);
                Vector3 tj = new Vector3(shape[j].x + translation.x, yTop,    shape[j].y + translation.z);
                builder.AddQuad(bj, bi, ti, tj);
            }
        }

        // ─────────────────────────────────────────────
        // Lightweight mesh builder — position-only for MVP (UVs/normals auto-recomputed).
        // ─────────────────────────────────────────────

        sealed class MeshBuilder
        {
            readonly List<Vector3> _verts = new();
            readonly List<int> _tris = new();

            public int VertexCount => _verts.Count;

            public int AddVertex(Vector3 v) { _verts.Add(v); return _verts.Count - 1; }

            public void AddTriangle(int a, int b, int c)
            {
                _tris.Add(a); _tris.Add(b); _tris.Add(c);
            }

            public void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int b = _verts.Count;
                _verts.Add(p0); _verts.Add(p1); _verts.Add(p2); _verts.Add(p3);
                _tris.Add(b); _tris.Add(b + 1); _tris.Add(b + 2);
                _tris.Add(b); _tris.Add(b + 2); _tris.Add(b + 3);
            }

            public Mesh ToMesh()
            {
                if (_verts.Count == 0) return null;
                var mesh = new Mesh();
                if (_verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(_verts);
                mesh.SetTriangles(_tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
