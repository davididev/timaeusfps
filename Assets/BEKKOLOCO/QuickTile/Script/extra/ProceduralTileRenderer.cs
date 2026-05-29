// ProceduralTileRenderer.cs
// Runtime/Editor component that reads a Unity Tilemap and generates
// procedural 3D meshes using the dual-grid system.
// Replaces the prefab-based pipeline when MeshMode == Procedural.
//
// Performance: all tiles are combined into a single mesh (up to 3 submeshes: caps + walls + skirt)
// instead of one GameObject per tile. This drastically reduces draw calls.

using UnityEngine;
using UnityEngine.Tilemaps;
using System.Collections.Generic;
using Bekkoloco.DOTS;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Bekkoloco
{
    [ExecuteAlways]
    public class ProceduralTileRenderer : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("The tilemap to read cell data from.")]
        public Tilemap sourceTilemap;

        [Header("Dig")]
        [Tooltip("When enabled this tilemap acts only as subtraction volume and does not generate its own mesh.")]
        public bool actsAsDigLayer = false;
        [Tooltip("Tilemaps whose occupied cells are subtracted from this mesh before generation.")]
        public List<Tilemap> digTilemaps = new List<Tilemap>();
        [Tooltip("Scene-space dig volumes that carve this mesh when they overlap it.")]
        public List<QuickTileDigVolume> digVolumes = new List<QuickTileDigVolume>();

        private static readonly Color DigPreviewColor = new Color(0.22f, 0.62f, 1f, 0.42f);

        [Header("Mesh Settings")]
        public ProceduralTileMeshGenerator.ProceduralMeshSettings settings
            = new ProceduralTileMeshGenerator.ProceduralMeshSettings();

        [Header("Materials (per surface — matches HTML version)")]
        [Tooltip("Material for top cap (floor). Submesh 0.")]
        public Material floorMaterial;
        [Tooltip("Material for side walls. Submesh 1.")]
        public Material wallMaterial;
        [Tooltip("(Optional) Override material for skirt. If null, uses floorMaterial.")]
        public Material ceilingMaterial;
        [Tooltip("(Optional) Override material for bottom cap. If null, uses wallMaterial.")]
        public Material bottomMaterial;
        [Tooltip("Preview material used when this renderer acts as a Dig Layer.")]
        public Material digPreviewMaterial;

        [Header("Positioning (mirrors TileRule settings)")]
        [Tooltip("Y offset from base — same as TileRule.yOffset")]
        public float yOffset = 0f;

        [Tooltip("When true, depth is anchored from yOffset down to Y=0 (fixBase mode). When false, depth = sizeY from tilemap position.")]
        public bool fixBase = false;

        [Tooltip("Height of the tile rule (used when fixBase is false)")]
        public float sizeY = 1f;

        // ── Internal state ──
        private Dictionary<ProceduralTileType, Mesh> _cachedMeshes;
        private float _lastRadius = -1f;
        private float _lastDepth = -1f;
        private int _lastCurveSegments = -1;
        private bool _lastSkirtEnabled = false;
        private float _lastSkirtWidth = -1f;
        private float _lastSkirtHeight = -1f;
        private int _lastSkirtSegments = -1;
        private float _lastSkirtUVScale = -1f;
        private float _lastSkirtUVOffsetY = -999f;
        private BottomMode _lastBottomMode = (BottomMode)(-1);
        private int _lastBottomHash = -1;
        private bool _isBuilt;

        // Combined mesh output (single GO instead of one per tile)
        private GameObject _combinedGO;
        private Mesh _combinedMesh;
        private bool _rebuildScheduled;
        private MeshCollider _meshCollider;
        private Mesh _colliderMesh;

        // Track whether the current mesh has a skirt/bottom submesh
        private bool _meshHasSkirt;
        private bool _meshHasBottom;
        private Material _generatedSkirtMaterial;
        private Texture2D _generatedSkirtMaskTexture;
        private int _generatedSkirtMaskHash = int.MinValue;
        private Material _generatedSkirtSourceMaterial;
        [SerializeField, HideInInspector] private bool _hasDigPreviewRestoreColor;
        [SerializeField, HideInInspector] private Color _digPreviewRestoreColor = Color.white;
        [SerializeField, HideInInspector] private Tilemap _digPreviewTilemap;
        private const float SliceEpsilon = 0.0001f;

        private sealed class DigVolumeInfo
        {
            public Tilemap tilemap;
            public QuickTileDigVolume volume;
            public Bounds worldBounds;
            public float bottomWorld;
            public float topWorld;
        }

        private struct VerticalSliceDefinition
        {
            public float bottomWorld;
            public float topWorld;
            public List<DigVolumeInfo> activeDigVolumes;
        }

        private struct VerticalInterval
        {
            public float bottomWorld;
            public float topWorld;
        }

        // Skirt post-deform data: for each skirt vertex (ring>0), store its combined index,
        // the ring0 vertex index, and the local offset from ring0.
        private struct SkirtVertexInfo
        {
            public int combinedIndex;   // index of THIS skirt vertex in the combined mesh
            public int ring0Index;      // index of the corresponding ring 0 vertex in the combined mesh
            public Vector3 localOffset; // offset from ring0 position (XZ outward + Y drop)
        }
        private List<SkirtVertexInfo> _skirtVertexInfos;
        private RadialHillDeformer _subscribedDeformer;

        // Bottom cap post-deform data:
        // - Flat mode: restore interior vertices to base positions, but allow seam vertices
        //   to follow the wall so the underside stays stitched after deformation.
        // - Bevel mode: follow deformed ring0 (wall bottom) + maintain relative offset,
        //   same pattern as skirt, so bevel stays attached to walls after deformation.
        private struct BottomVertexInfo
        {
            public int combinedIndex;    // index of THIS bottom vertex in the combined mesh
            public int ring0Index;       // seam/wall source index, or -1 for flat interior vertices
            public Vector3 baseOffset;   // offset from ring0 for seam-following, or absolute base position for flat interior
        }
        private List<BottomVertexInfo> _bottomVertexInfos;
        private bool _bottomIsFlat; // true=Flat mode (restore to base), false=Bevel mode (follow ring0)

        private struct BoundarySegment
        {
            public Vector2 a;
            public Vector2 b;

            public BoundarySegment(Vector2 a, Vector2 b)
            {
                this.a = a;
                this.b = b;
            }
        }

        private struct QuantizedVertexKey : System.IEquatable<QuantizedVertexKey>
        {
            private const float QuantizeScale = 10000f;

            public int x;
            public int y;
            public int z;

            public QuantizedVertexKey(Vector3 v)
            {
                x = Mathf.RoundToInt(v.x * QuantizeScale);
                y = Mathf.RoundToInt(v.y * QuantizeScale);
                z = Mathf.RoundToInt(v.z * QuantizeScale);
            }

            public bool Equals(QuantizedVertexKey other)
            {
                return x == other.x && y == other.y && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is QuantizedVertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + x;
                    hash = hash * 31 + y;
                    hash = hash * 31 + z;
                    return hash;
                }
            }
        }

        private sealed class SeamNormalGroup
        {
            public readonly List<int> bottomIndices = new List<int>();
            public readonly List<int> wallIndices = new List<int>();
        }

        private static Dictionary<QuantizedVertexKey, List<int>> BuildVertexLookup(IReadOnlyCollection<int> indices, IReadOnlyList<Vector3> verts)
        {
            var lookup = new Dictionary<QuantizedVertexKey, List<int>>();
            if (indices == null || verts == null)
                return lookup;

            foreach (int index in indices)
            {
                if (index < 0 || index >= verts.Count) continue;

                var key = new QuantizedVertexKey(verts[index]);
                if (!lookup.TryGetValue(key, out var list))
                {
                    list = new List<int>(2);
                    lookup[key] = list;
                }

                list.Add(index);
            }

            return lookup;
        }

        private static int FindNearestVertexIndex(
            IReadOnlyDictionary<QuantizedVertexKey, List<int>> lookup,
            IReadOnlyList<Vector3> verts,
            int sourceIndex)
        {
            if (lookup == null || verts == null || sourceIndex < 0 || sourceIndex >= verts.Count)
                return -1;

            if (!lookup.TryGetValue(new QuantizedVertexKey(verts[sourceIndex]), out var candidates))
                return -1;

            int bestIndex = -1;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                int candidateIndex = candidates[i];
                if (candidateIndex < 0 || candidateIndex >= verts.Count) continue;

                float distance = (verts[candidateIndex] - verts[sourceIndex]).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = candidateIndex;
                }
            }

            return bestIndex;
        }

        private struct QuantizedPoint2DKey : System.IEquatable<QuantizedPoint2DKey>
        {
            private const float QuantizeScale = 10000f;

            public int x;
            public int y;

            public QuantizedPoint2DKey(Vector2 p)
            {
                x = Mathf.RoundToInt(p.x * QuantizeScale);
                y = Mathf.RoundToInt(p.y * QuantizeScale);
            }

            public bool Equals(QuantizedPoint2DKey other)
            {
                return x == other.x && y == other.y;
            }

            public override bool Equals(object obj)
            {
                return obj is QuantizedPoint2DKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (x * 397) ^ y;
                }
            }
        }

        private struct UndirectedEdge2DKey : System.IEquatable<UndirectedEdge2DKey>
        {
            public QuantizedPoint2DKey a;
            public QuantizedPoint2DKey b;

            public UndirectedEdge2DKey(QuantizedPoint2DKey p0, QuantizedPoint2DKey p1)
            {
                if (Compare(p0, p1) <= 0)
                {
                    a = p0;
                    b = p1;
                }
                else
                {
                    a = p1;
                    b = p0;
                }
            }

            public bool Equals(UndirectedEdge2DKey other)
            {
                return a.Equals(other.a) && b.Equals(other.b);
            }

            public override bool Equals(object obj)
            {
                return obj is UndirectedEdge2DKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (a.GetHashCode() * 397) ^ b.GetHashCode();
                }
            }

            private static int Compare(QuantizedPoint2DKey lhs, QuantizedPoint2DKey rhs)
            {
                if (lhs.x != rhs.x) return lhs.x.CompareTo(rhs.x);
                return lhs.y.CompareTo(rhs.y);
            }
        }

        private struct TriangleKey : System.IEquatable<TriangleKey>
        {
            public int a;
            public int b;
            public int c;

            public TriangleKey(int i0, int i1, int i2)
            {
                if (i0 > i1) Swap(ref i0, ref i1);
                if (i1 > i2) Swap(ref i1, ref i2);
                if (i0 > i1) Swap(ref i0, ref i1);

                a = i0;
                b = i1;
                c = i2;
            }

            public bool Equals(TriangleKey other)
            {
                return a == other.a && b == other.b && c == other.c;
            }

            public override bool Equals(object obj)
            {
                return obj is TriangleKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + a;
                    hash = hash * 31 + b;
                    hash = hash * 31 + c;
                    return hash;
                }
            }

            private static void Swap(ref int lhs, ref int rhs)
            {
                int tmp = lhs;
                lhs = rhs;
                rhs = tmp;
            }
        }

        // ─────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Rebuild all procedural meshes from the current tilemap state.
        /// Combines everything into a single mesh with 2-3 submeshes (caps + walls + optional skirt).
        /// </summary>
        public void Rebuild()
        {
            if (actsAsDigLayer)
            {
                ApplyDigPreviewVisuals();
            }
            else
            {
                RestoreDigPreviewVisuals();
            }

            if (sourceTilemap == null)
            {
                ClearCombinedMesh();
                return;
            }

            var baseFilledCells = CollectFilledCells(sourceTilemap);
            if (baseFilledCells.Count == 0)
            {
                ClearCombinedMesh();
                return;
            }

            // 2.5D side-scroller path: when the owning editor is in Mode2_5D,
            // use PlatformerMeshGenerator instead of the 3D dual-grid pipeline.
            if (IsOwningEditorIn2_5D())
            {
                Build2_5DPlatformerMesh(baseFilledCells);
                return;
            }

            float targetTopWorld = sourceTilemap.transform.position.y;
            float targetBottomWorld = GetBottomWorldY(targetTopWorld);
            var digVolumes = actsAsDigLayer ? new List<DigVolumeInfo>() : CollectActiveDigVolumes(targetBottomWorld, targetTopWorld);
            var slices = BuildVerticalSlices(targetBottomWorld, targetTopWorld, digVolumes);

            if (!BuildCombinedMeshFromSlices(baseFilledCells, slices, digVolumes, targetBottomWorld, targetTopWorld, actsAsDigLayer))
            {
                ClearCombinedMesh();
                return;
            }

            NotifyDeformers();
        }

        bool IsOwningEditorIn2_5D()
        {
            var editor = GetComponentInParent<QuickTilemapEditor>();
            return editor != null && editor.gridStyle == QuickTilemapEditor.GridStyle.Mode2_5D;
        }

        void Build2_5DPlatformerMesh(HashSet<Vector2Int> cells)
        {
            if (actsAsDigLayer)
            {
                // Dig layers in 2.5D: no visible mesh for MVP.
                ClearCombinedMesh();
                return;
            }

            int platformerCurveSegments = settings != null
                ? Mathf.Clamp(settings.curveSegments, 1, 16)
                : 5;

            if ((platformerCurveSegments & 1) == 0)
                platformerCurveSegments += 1;

            var platformerSettings = new PlatformerMeshGenerator.PlatformerSettings
            {
                blockDepth = PlatformerMeshGenerator.DefaultBlockDepth,
                topThickness = PlatformerMeshGenerator.DefaultTopThickness,
                radius = Mathf.Clamp(settings != null ? settings.radius : PlatformerMeshGenerator.DefaultRadius, 0f, 0.5f),
                meshSegments = platformerCurveSegments,
                yPosition = yOffset,

                // Reuse the 3D skirt parameters so the same texture/material setup
                // the user configures for 3D also drives the 2.5D crown.
                skirtEnabled = settings != null && settings.skirtEnabled,
                skirtWidth = settings != null ? settings.skirtWidth : 0.155f,
                skirtHeight = settings != null ? settings.skirtHeight : 0.485f,
                skirtSegments = settings != null ? Mathf.Clamp(settings.skirtSegments, 1, 8) : 2,
                skirtUVScale = settings != null ? settings.skirtUVScale : 1f,
                skirtUVOffsetY = settings != null ? settings.skirtUVOffsetY : 0.389f,
                floorOverhang = 0.08f,
                sideUnderlap = settings != null ? Mathf.Max(0.5f, settings.skirtWidth) : 0.5f,
                bottomMode = settings != null ? settings.bottomMode : BottomMode.None,
                bottomBevelInset = settings != null ? settings.bottomBevelInset : 0.1f,
                bottomBevelDepth = settings != null ? settings.bottomBevelDepth : 0.15f,
                bottomBevelSegments = settings != null ? settings.bottomBevelSegments : 4,
                bottomBevelProfile = settings != null ? settings.bottomBevelProfile : BevelProfile.Convex,
                bottomNoiseScale = settings != null ? settings.bottomNoiseScale : 0.96f,
                bottomNoiseAmplitude = settings != null ? settings.bottomNoiseAmplitude : 5.98f,
                bottomIslandSharpness = settings != null ? settings.bottomIslandSharpness : 1.99f,
                bottomIslandSmooth = settings != null ? settings.bottomIslandSmooth : 0f,
                bottomNoiseResolution = settings != null ? settings.bottomNoiseResolution : 1,
                bottomNoiseSeed = settings != null ? settings.bottomNoiseSeed : 0f
            };

            var meshes = PlatformerMeshGenerator.Build(cells, platformerSettings);
            if (meshes.Wall == null && meshes.Floor == null && meshes.Crown == null && meshes.Bottom == null)
            {
                ClearCombinedMesh();
                return;
            }

            if (_combinedMesh == null)
                _combinedMesh = new Mesh { name = "ProceduralTiles_Combined_2_5D" };
            else
                _combinedMesh.Clear();

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var wallTris = new List<int>();
            var floorTris = new List<int>();
            var crownTris = new List<int>();
            var bottomTris = new List<int>();

            AppendSubMesh(meshes.Wall,  verts, uvs, wallTris);
            AppendSubMesh(meshes.Floor, verts, uvs, floorTris);
            AppendSubMesh(meshes.Crown, verts, uvs, crownTris);
            AppendSubMesh(meshes.Bottom, verts, uvs, bottomTris);

            if (verts.Count > 65535)
                _combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            _combinedMesh.SetVertices(verts);
            _combinedMesh.SetUVs(0, uvs);
            int subMeshCount = 3 + (meshes.Bottom != null ? 1 : 0);
            _combinedMesh.subMeshCount = subMeshCount;
            _combinedMesh.SetTriangles(wallTris,  0);
            _combinedMesh.SetTriangles(floorTris, 1);
            _combinedMesh.SetTriangles(crownTris, 2);
            if (meshes.Bottom != null)
                _combinedMesh.SetTriangles(bottomTris, 3);
            _combinedMesh.RecalculateNormals();
            _combinedMesh.RecalculateBounds();

            EnsureCombinedGO();
            var mf = _combinedGO.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = _combinedMesh;

            // Flag skirt presence so ResolveSkirtDisplayMaterial can build the masked variant if enabled.
            _meshHasSkirt = meshes.Crown != null;
            _meshHasBottom = meshes.Bottom != null;

            var mr = _combinedGO.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                Material wall = wallMaterial != null ? wallMaterial : floorMaterial;
                Material floor = floorMaterial != null ? floorMaterial : wallMaterial;
                // Reuse the 3D skirt material pipeline (mask support included) for the 2.5D crown.
                Material crown = meshes.Crown != null ? ResolveSkirtDisplayMaterial() : floor;
                if (crown == null) crown = floor;
                Material bottom = bottomMaterial != null ? bottomMaterial : wall;

                mr.sharedMaterials = meshes.Bottom != null
                    ? new[] { wall, floor, crown, bottom }
                    : new[] { wall, floor, crown };
                mr.enabled = true;
            }

            ClearCollider();
        }

        static void AppendSubMesh(Mesh source, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            if (source == null) return;
            int baseIndex = verts.Count;
            var srcVerts = source.vertices;
            var srcTris = source.triangles;
            var srcUVs = source.uv; // may be empty
            verts.AddRange(srcVerts);
            if (srcUVs != null && srcUVs.Length == srcVerts.Length)
                uvs.AddRange(srcUVs);
            else
                for (int i = 0; i < srcVerts.Length; i++)
                    uvs.Add(Vector2.zero);
            for (int i = 0; i < srcTris.Length; i++)
                tris.Add(srcTris[i] + baseIndex);
        }

        private HashSet<Vector2Int> CollectFilledCells(Tilemap tilemap)
        {
            var filledCells = new HashSet<Vector2Int>();
            if (tilemap == null)
                return filledCells;

            BoundsInt bounds = tilemap.cellBounds;
            foreach (var pos in bounds.allPositionsWithin)
            {
                if (tilemap.HasTile(pos))
                    filledCells.Add(new Vector2Int(pos.x, pos.y));
            }

            return filledCells;
        }

        private float GetBottomWorldY(float topWorldY)
        {
            return fixBase
                ? 0f
                : topWorldY - Mathf.Max(SliceEpsilon, sizeY);
        }

        private List<DigVolumeInfo> CollectActiveDigVolumes(float targetBottomWorld, float targetTopWorld)
        {
            var activeVolumes = new List<DigVolumeInfo>();
            if (digTilemaps != null)
            {
                foreach (var digTilemap in digTilemaps)
                {
                    if (digTilemap == null || digTilemap == sourceTilemap)
                        continue;

                    float digTopWorld = digTilemap.transform.position.y;
                    float digBottomWorld = digTopWorld - 1f;

                    var digRenderer = digTilemap.GetComponentInChildren<ProceduralTileRenderer>(true);
                    if (digRenderer != null)
                    {
                        digTopWorld = digTilemap.transform.position.y;
                        digBottomWorld = digRenderer.fixBase
                            ? 0f
                            : digTopWorld - Mathf.Max(SliceEpsilon, digRenderer.sizeY);
                    }

                    float minWorld = Mathf.Min(digBottomWorld, digTopWorld);
                    float maxWorld = Mathf.Max(digBottomWorld, digTopWorld);
                    if (maxWorld <= targetBottomWorld + SliceEpsilon || minWorld >= targetTopWorld - SliceEpsilon)
                        continue;

                    activeVolumes.Add(new DigVolumeInfo
                    {
                        tilemap = digTilemap,
                        bottomWorld = minWorld,
                        topWorld = maxWorld
                    });
                }
            }

            if (digVolumes != null)
            {
                foreach (var digVolume in digVolumes)
                {
                    if (digVolume == null || !digVolume.isActiveAndEnabled)
                        continue;

                    if (!digVolume.TryGetWorldBounds(out Bounds digBounds))
                        continue;

                    float minWorld = Mathf.Min(digBounds.min.y, digBounds.max.y);
                    float maxWorld = Mathf.Max(digBounds.min.y, digBounds.max.y);
                    if (maxWorld <= targetBottomWorld + SliceEpsilon || minWorld >= targetTopWorld - SliceEpsilon)
                        continue;

                    activeVolumes.Add(new DigVolumeInfo
                    {
                        volume = digVolume,
                        worldBounds = digBounds,
                        bottomWorld = minWorld,
                        topWorld = maxWorld
                    });
                }
            }

            return activeVolumes;
        }

        private List<VerticalSliceDefinition> BuildVerticalSlices(float targetBottomWorld, float targetTopWorld, List<DigVolumeInfo> digVolumes)
        {
            var boundaries = new List<float> { targetBottomWorld, targetTopWorld };
            var mergedIntervals = MergeDigIntervals(targetBottomWorld, targetTopWorld, digVolumes);

            foreach (var interval in mergedIntervals)
            {
                AddUniqueBoundary(boundaries, interval.bottomWorld);
                AddUniqueBoundary(boundaries, interval.topWorld);
            }

            AddDigBevelBoundaries(boundaries, targetBottomWorld, targetTopWorld, digVolumes);

            boundaries.Sort();

            var slices = new List<VerticalSliceDefinition>();
            for (int i = 0; i < boundaries.Count - 1; i++)
            {
                float bottomWorld = boundaries[i];
                float topWorld = boundaries[i + 1];
                if (topWorld <= bottomWorld + SliceEpsilon)
                    continue;

                var activeSliceVolumes = new List<DigVolumeInfo>();
                if (digVolumes != null)
                {
                    foreach (var digVolume in digVolumes)
                    {
                        if (digVolume == null) continue;
                        if (digVolume.topWorld <= bottomWorld + SliceEpsilon) continue;
                        if (digVolume.bottomWorld >= topWorld - SliceEpsilon) continue;
                        activeSliceVolumes.Add(digVolume);
                    }
                }

                slices.Add(new VerticalSliceDefinition
                {
                    bottomWorld = bottomWorld,
                    topWorld = topWorld,
                    activeDigVolumes = activeSliceVolumes
                });
            }

            return slices;
        }

        private void AddDigBevelBoundaries(List<float> boundaries, float targetBottomWorld, float targetTopWorld, List<DigVolumeInfo> digVolumes)
        {
            if (boundaries == null || digVolumes == null || digVolumes.Count == 0)
                return;

            float cellSizeX;
            float cellSizeZ;
            GetSourceCellSizeXZ(out cellSizeX, out cellSizeZ);
            float baseCellSize = Mathf.Max(SliceEpsilon, Mathf.Min(cellSizeX, cellSizeZ));

            foreach (var digVolume in digVolumes)
            {
                if (digVolume == null || digVolume.volume == null || !digVolume.volume.bevelEdges || digVolume.volume.edgeSmooth <= 0.0001f)
                    continue;

                float overlapBottom = Mathf.Max(targetBottomWorld, digVolume.bottomWorld);
                float overlapTop = Mathf.Min(targetTopWorld, digVolume.topWorld);
                float overlapHeight = overlapTop - overlapBottom;
                if (overlapHeight <= SliceEpsilon)
                    continue;

                float bevelRadius = digVolume.volume.GetRoundedEdgeRadiusWorld();
                if (bevelRadius <= SliceEpsilon)
                    continue;

                // Build the lip from the slice silhouettes themselves so the
                // dual-grid tiles generate the rounded transition.
                float bevelHeight = Mathf.Max(bevelRadius, digVolume.volume.GetBevelHeightWorld());
                bevelHeight = Mathf.Min(bevelHeight, overlapHeight * 0.45f);
                bevelHeight = Mathf.Min(bevelHeight, overlapHeight * 0.45f);
                if (bevelHeight <= SliceEpsilon)
                    continue;

                float suggestedStep = digVolume.volume.GetSuggestedSliceStep(baseCellSize);
                float targetStep = Mathf.Clamp(
                    Mathf.Min(bevelHeight, suggestedStep * 0.5f),
                    baseCellSize * 0.18f,
                    Mathf.Max(baseCellSize * 0.45f, bevelHeight));
                int densityCuts = Mathf.Clamp(
                    Mathf.CeilToInt(bevelHeight / Mathf.Max(SliceEpsilon, targetStep)),
                    2,
                    Mathf.Max(3, settings.curveSegments));
                int internalBevelCuts = Mathf.Clamp(
                    Mathf.Max(densityCuts, digVolume.volume.bevelSegments),
                    2,
                    8);
                for (int i = 1; i <= internalBevelCuts; i++)
                {
                    float t = i / (internalBevelCuts + 1f);
                    float curveT = 1f - Mathf.Cos(t * Mathf.PI * 0.5f);
                    AddUniqueBoundary(boundaries, overlapBottom + bevelHeight * curveT);
                    AddUniqueBoundary(boundaries, overlapTop - bevelHeight * curveT);
                }
            }
        }

        private List<VerticalInterval> MergeDigIntervals(float targetBottomWorld, float targetTopWorld, List<DigVolumeInfo> digVolumes)
        {
            var intervals = new List<VerticalInterval>();
            if (digVolumes == null || digVolumes.Count == 0)
                return intervals;

            foreach (var digVolume in digVolumes)
            {
                if (digVolume == null)
                    continue;

                float overlapBottom = Mathf.Max(targetBottomWorld, digVolume.bottomWorld);
                float overlapTop = Mathf.Min(targetTopWorld, digVolume.topWorld);
                if (overlapTop <= overlapBottom + SliceEpsilon)
                    continue;

                intervals.Add(new VerticalInterval
                {
                    bottomWorld = overlapBottom,
                    topWorld = overlapTop
                });
            }

            if (intervals.Count <= 1)
                return intervals;

            intervals.Sort((a, b) => a.bottomWorld.CompareTo(b.bottomWorld));
            var merged = new List<VerticalInterval> { intervals[0] };
            for (int i = 1; i < intervals.Count; i++)
            {
                var current = intervals[i];
                int lastIndex = merged.Count - 1;
                var previous = merged[lastIndex];

                if (current.bottomWorld <= previous.topWorld + SliceEpsilon)
                {
                    previous.topWorld = Mathf.Max(previous.topWorld, current.topWorld);
                    merged[lastIndex] = previous;
                    continue;
                }

                merged.Add(current);
            }

            return merged;
        }

        private void AddUniqueBoundary(List<float> boundaries, float value)
        {
            for (int i = 0; i < boundaries.Count; i++)
            {
                if (Mathf.Abs(boundaries[i] - value) <= SliceEpsilon)
                    return;
            }

            boundaries.Add(value);
        }

        private bool BuildCombinedMeshFromSlices(
            HashSet<Vector2Int> baseFilledCells,
            List<VerticalSliceDefinition> slices,
            List<DigVolumeInfo> digVolumes,
            float targetBottomWorld,
            float targetTopWorld,
            bool previewAsDigVolume)
        {
            if (baseFilledCells == null || baseFilledCells.Count == 0 || slices == null || slices.Count == 0)
                return false;

            var capVerts = new List<Vector3>();
            var capUVs = new List<Vector2>();
            var capMaskUVs = new List<Vector2>();
            var capTris = new List<int>();
            var wallTris = new List<int>();
            var cutTris = new List<int>();
            var skirtTris = new List<int>();
            var bottomTris = new List<int>();

            _skirtVertexInfos = null;
            _bottomVertexInfos = null;
            _bottomIsFlat = false;

            var sliceFilledCellsList = new List<HashSet<Vector2Int>>(slices.Count);
            foreach (var slice in slices)
            {
                var sliceFilledCells = new HashSet<Vector2Int>(baseFilledCells);
                if (!previewAsDigVolume)
                    ApplyDigSubtraction(sliceFilledCells, slice.activeDigVolumes, slice.bottomWorld, slice.topWorld);
                sliceFilledCellsList.Add(sliceFilledCells);
            }

            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                var slice = slices[sliceIndex];
                if (slice.topWorld <= slice.bottomWorld + SliceEpsilon)
                    continue;

                var sliceFilledCells = sliceFilledCellsList[sliceIndex];
                if (sliceFilledCells.Count == 0)
                    continue;

                bool isTopmostSlice = Mathf.Abs(slice.topWorld - targetTopWorld) <= SliceEpsilon;
                bool isBottommostSlice = Mathf.Abs(slice.bottomWorld - targetBottomWorld) <= SliceEpsilon;
                float sliceHeight = slice.topWorld - slice.bottomWorld;
                float sliceBottomLocalY = slice.bottomWorld - targetTopWorld;
                HashSet<Vector2Int> upperSliceCells = sliceIndex < slices.Count - 1 ? sliceFilledCellsList[sliceIndex + 1] : null;
                HashSet<Vector2Int> lowerSliceCells = sliceIndex > 0 ? sliceFilledCellsList[sliceIndex - 1] : null;
                HashSet<Vector2Int> exposedTopCells = isTopmostSlice
                    ? new HashSet<Vector2Int>(sliceFilledCells)
                    : BuildExposedCellSet(sliceFilledCells, upperSliceCells);
                HashSet<Vector2Int> exposedBottomCells = (!isBottommostSlice && !previewAsDigVolume)
                    ? BuildExposedCellSet(sliceFilledCells, lowerSliceCells)
                    : null;

                var sliceSettings = CreateSliceSettings(sliceHeight, isTopmostSlice, isBottommostSlice, previewAsDigVolume);
                var sliceMeshCache = ProceduralTileMeshGenerator.GenerateAllMeshes(GetPrototypeMeshSettings(sliceSettings));

                try
                {
                    AppendSliceGeometry(
                        sliceFilledCells,
                        exposedTopCells,
                        exposedBottomCells,
                        sliceSettings,
                        sliceMeshCache,
                        sliceBottomLocalY,
                        capVerts,
                        capUVs,
                        capMaskUVs,
                        capTris,
                        wallTris,
                        cutTris,
                        skirtTris,
                        bottomTris,
                        isTopmostSlice,
                        isBottommostSlice);
                }
                finally
                {
                    DestroyGeneratedMeshCache(sliceMeshCache);
                }
            }

            if (!previewAsDigVolume && wallTris.Count > 0)
            {
                ApplyDigWallNoise(capVerts, wallTris, digVolumes);
            }

            if (!previewAsDigVolume && skirtTris.Count > 0)
            {
                CullContactingSkirtTriangles(capVerts, skirtTris, digVolumes, targetTopWorld);
            }

            if (capVerts.Count == 0)
                return false;

            if (_combinedMesh == null)
                _combinedMesh = new Mesh { name = "ProceduralTiles_Combined" };
            else
                _combinedMesh.Clear();

            if (capVerts.Count > 65535)
                _combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            _combinedMesh.SetVertices(capVerts);
            _combinedMesh.SetUVs(0, capUVs);
            _combinedMesh.SetUVs(1, capMaskUVs);

            _meshHasSkirt = skirtTris.Count > 0;
            _meshHasBottom = bottomTris.Count > 0;
            List<int> renderWallTris = wallTris;
            if (cutTris.Count > 0)
            {
                renderWallTris = new List<int>(wallTris.Count + cutTris.Count);
                renderWallTris.AddRange(wallTris);
                renderWallTris.AddRange(cutTris);
            }

            int subMeshCount = 2;
            if (_meshHasSkirt) subMeshCount++;
            if (_meshHasBottom) subMeshCount++;
            _combinedMesh.subMeshCount = subMeshCount;

            int subMeshIndex = 0;
            _combinedMesh.SetTriangles(capTris, subMeshIndex++);
            _combinedMesh.SetTriangles(renderWallTris, subMeshIndex++);
            if (_meshHasSkirt)
                _combinedMesh.SetTriangles(skirtTris, subMeshIndex++);
            if (_meshHasBottom)
                _combinedMesh.SetTriangles(bottomTris, subMeshIndex++);

            _combinedMesh.RecalculateNormals();
            if (!previewAsDigVolume &&
                _meshHasBottom &&
                (settings.bottomMode == BottomMode.IslandNoise || settings.bottomMode == BottomMode.Bevel))
            {
                SmoothBottomWallSeamNormals(_combinedMesh, bottomTris, wallTris);
            }
            _combinedMesh.RecalculateBounds();
            _combinedMesh.RecalculateTangents();

            EnsureCombinedGO();
            var meshFilter = _combinedGO.GetComponent<MeshFilter>();
            if (meshFilter != null)
                meshFilter.sharedMesh = _combinedMesh;

            var meshRenderer = _combinedGO.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.sharedMaterials = BuildMaterialArray(previewAsDigVolume);
                meshRenderer.enabled = true;
            }

            UpdateCollider(previewAsDigVolume, capVerts, capTris, renderWallTris, bottomTris);

            return true;
        }

        private void UpdateCollider(
            bool previewAsDigVolume,
            List<Vector3> vertices,
            List<int> capTris,
            List<int> wallTris,
            List<int> bottomTris)
        {
            if (previewAsDigVolume || actsAsDigLayer)
            {
                ClearCollider();
                return;
            }

            EnsureCombinedGO();
            RemoveLegacyColliderChildren();

            if (_meshCollider == null)
                _meshCollider = _combinedGO.AddComponent<MeshCollider>();

            // Build a collider mesh from cap + wall + bottom (no skirt)
            if (_colliderMesh == null)
                _colliderMesh = new Mesh { name = "ProceduralTiles_Collider" };
            else
                _colliderMesh.Clear();

            if (vertices.Count > 65535)
                _colliderMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            _colliderMesh.SetVertices(vertices);

            // Merge all non-skirt triangles into a single submesh
            var allTris = new List<int>(capTris.Count + wallTris.Count + bottomTris.Count);
            allTris.AddRange(capTris);
            allTris.AddRange(wallTris);
            allTris.AddRange(bottomTris);

            _colliderMesh.SetTriangles(allTris, 0);
            _colliderMesh.RecalculateBounds();

            _meshCollider.sharedMesh = null;
            _meshCollider.sharedMesh = _colliderMesh;
            _meshCollider.enabled = true;
        }

        private void ClearCollider()
        {
            if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                if (Application.isPlaying) Destroy(_meshCollider);
                else DestroyImmediate(_meshCollider);
                _meshCollider = null;
            }

            if (_colliderMesh != null)
            {
                if (Application.isPlaying) Destroy(_colliderMesh);
                else DestroyImmediate(_colliderMesh);
                _colliderMesh = null;
            }

            RemoveLegacyColliderChildren();
        }

        private void RemoveLegacyColliderChildren()
        {
            if (_combinedGO == null)
                return;

            RemoveLegacyColliderChild("Collider_Ground");
            RemoveLegacyColliderChild("Collider_Wall");
            RemoveLegacyColliderChild("Collider_Bottom");
        }

        private void RemoveLegacyColliderChild(string childName)
        {
            var child = _combinedGO != null ? _combinedGO.transform.Find(childName) : null;
            if (child == null)
                return;

            if (Application.isPlaying) Destroy(child.gameObject);
            else DestroyImmediate(child.gameObject);
        }

        private void ApplyDigBevelSurfaceProjection(
            List<Vector3> combinedVertices,
            List<int> capTris,
            List<int> wallTris,
            List<int> bottomTris,
            List<DigVolumeInfo> digVolumes)
        {
            if (combinedVertices == null || combinedVertices.Count == 0 || digVolumes == null || digVolumes.Count == 0)
                return;

            float cellSizeX;
            float cellSizeZ;
            GetSourceCellSizeXZ(out cellSizeX, out cellSizeZ);
            float baseCellSize = Mathf.Max(SliceEpsilon, Mathf.Min(cellSizeX, cellSizeZ));

            var candidateIndices = new HashSet<int>();
            AddTriangleIndices(candidateIndices, capTris);
            AddTriangleIndices(candidateIndices, wallTris);
            AddTriangleIndices(candidateIndices, bottomTris);

            foreach (int vertexIndex in candidateIndices)
            {
                if (vertexIndex < 0 || vertexIndex >= combinedVertices.Count)
                    continue;

                Vector3 worldPoint = transform.TransformPoint(combinedVertices[vertexIndex]);
                if (!TryGetBestDigSurfaceProjection(worldPoint, digVolumes, baseCellSize, out Vector3 projectedPoint, out float blendWeight))
                    continue;

                Vector3 projectedLocal = transform.InverseTransformPoint(projectedPoint);
                combinedVertices[vertexIndex] = Vector3.Lerp(combinedVertices[vertexIndex], projectedLocal, blendWeight);
            }
        }

        private bool TryGetBestDigSurfaceProjection(
            Vector3 worldPoint,
            List<DigVolumeInfo> digVolumes,
            float baseCellSize,
            out Vector3 projectedPoint,
            out float blendWeight)
        {
            projectedPoint = worldPoint;
            blendWeight = 0f;

            bool found = false;
            float bestDistance = float.MaxValue;

            foreach (var digVolume in digVolumes)
            {
                if (digVolume == null || digVolume.volume == null || !digVolume.volume.bevelEdges || digVolume.volume.edgeSmooth <= 0.0001f)
                    continue;

                float bevelRadius = digVolume.volume.GetRoundedEdgeRadiusWorld();
                if (bevelRadius <= SliceEpsilon)
                    continue;

                float influenceDistance = Mathf.Max(baseCellSize * 1.1f, bevelRadius * 1.15f);
                if (!digVolume.volume.TryProjectWorldPointToSurface(
                    worldPoint,
                    influenceDistance,
                    out Vector3 candidateProjectedPoint,
                    out _,
                    out float candidateBlendWeight))
                {
                    continue;
                }

                float candidateDistance = (candidateProjectedPoint - worldPoint).sqrMagnitude;
                if (candidateDistance >= bestDistance)
                    continue;

                bestDistance = candidateDistance;
                projectedPoint = candidateProjectedPoint;
                blendWeight = candidateBlendWeight;
                found = true;
            }

            return found;
        }

        private static void AddTriangleIndices(HashSet<int> indices, List<int> triangles)
        {
            if (indices == null || triangles == null)
                return;

            for (int i = 0; i < triangles.Count; i++)
                indices.Add(triangles[i]);
        }

        private void ApplyDigWallNoise(List<Vector3> combinedVertices, List<int> wallTris, List<DigVolumeInfo> digVolumes)
        {
            if (combinedVertices == null || wallTris == null || wallTris.Count == 0 || digVolumes == null || digVolumes.Count == 0)
                return;

            float cellSizeX;
            float cellSizeZ;
            GetSourceCellSizeXZ(out cellSizeX, out cellSizeZ);
            float cellSize = Mathf.Max(SliceEpsilon, Mathf.Min(cellSizeX, cellSizeZ));
            Vector3[] wallNormals = BuildWallVertexNormals(combinedVertices, wallTris);
            if (wallNormals == null || wallNormals.Length != combinedVertices.Count)
                return;

            var wallVertIndices = new HashSet<int>(wallTris);
            foreach (int vertexIndex in wallVertIndices)
            {
                if (vertexIndex < 0 || vertexIndex >= combinedVertices.Count)
                    continue;

                Vector3 normal = wallNormals[vertexIndex];
                if (normal.sqrMagnitude < 0.000001f)
                    continue;

                Vector3 worldPoint = transform.TransformPoint(combinedVertices[vertexIndex]);
                Vector3 worldNormal = transform.TransformDirection(normal).normalized;
                Vector3 displacement = EvaluateDigWallDisplacement(worldPoint, worldNormal, digVolumes, cellSize);
                if (displacement.sqrMagnitude <= 0.0000001f)
                    continue;

                combinedVertices[vertexIndex] += transform.InverseTransformVector(displacement);
            }
        }

        private Vector3 EvaluateDigWallDisplacement(
            Vector3 worldPoint,
            Vector3 wallNormalWorld,
            List<DigVolumeInfo> digVolumes,
            float cellSize)
        {
            Vector3 strongestDisplacement = Vector3.zero;
            float strongestWeight = 0f;

            foreach (var digVolume in digVolumes)
            {
                if (digVolume == null || digVolume.volume == null || digVolume.volume.edgeNoiseAmount <= 0.0001f)
                    continue;

                float amplitude = digVolume.volume.GetNoiseAmplitudeWorld();
                if (amplitude <= 0.0001f)
                    continue;

                float band = Mathf.Max(cellSize * 1.35f, amplitude * 2f);
                float distance = digVolume.volume.EvaluateCarveDistanceWorld(worldPoint, 0f);
                float absDistance = Mathf.Abs(distance);
                if (absDistance >= band)
                    continue;

                float boundaryWeight = 1f - Mathf.Clamp01(absDistance / band);
                float verticalWeight = 0.55f + 0.45f * (1f - Mathf.Abs(Vector3.Dot(wallNormalWorld, Vector3.up)));
                float noise = digVolume.volume.SampleNoiseAtWorldPoint(worldPoint);
                float displacementAmount = noise * amplitude * boundaryWeight * verticalWeight;
                if (Mathf.Abs(displacementAmount) <= 0.0001f)
                    continue;

                Vector3 candidate = wallNormalWorld * displacementAmount;
                float candidateWeight = Mathf.Abs(displacementAmount);
                if (candidateWeight > strongestWeight)
                {
                    strongestWeight = candidateWeight;
                    strongestDisplacement = candidate;
                }
            }

            return strongestDisplacement;
        }

        private Vector3[] BuildWallVertexNormals(List<Vector3> combinedVertices, List<int> wallTris)
        {
            if (combinedVertices == null || wallTris == null || wallTris.Count < 3)
                return null;

            var normals = new Vector3[combinedVertices.Count];
            for (int i = 0; i <= wallTris.Count - 3; i += 3)
            {
                int ia = wallTris[i];
                int ib = wallTris[i + 1];
                int ic = wallTris[i + 2];
                if (ia < 0 || ib < 0 || ic < 0 ||
                    ia >= combinedVertices.Count || ib >= combinedVertices.Count || ic >= combinedVertices.Count)
                {
                    continue;
                }

                Vector3 a = combinedVertices[ia];
                Vector3 b = combinedVertices[ib];
                Vector3 c = combinedVertices[ic];
                Vector3 faceNormal = Vector3.Cross(b - a, c - a);
                if (faceNormal.sqrMagnitude < 0.000001f)
                    continue;

                normals[ia] += faceNormal;
                normals[ib] += faceNormal;
                normals[ic] += faceNormal;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                if (normals[i].sqrMagnitude > 0.000001f)
                    normals[i].Normalize();
            }

            return normals;
        }

        private void GetSourceCellSizeXZ(out float cellSizeX, out float cellSizeZ)
        {
            Vector3 cellSize = sourceTilemap != null && sourceTilemap.layoutGrid != null
                ? sourceTilemap.layoutGrid.cellSize
                : Vector3.one;

            cellSizeX = Mathf.Abs(cellSize.x) > SliceEpsilon ? Mathf.Abs(cellSize.x) : 1f;
            cellSizeZ = Mathf.Abs(cellSize.y) > SliceEpsilon ? Mathf.Abs(cellSize.y) : 1f;
        }

        private void CullContactingSkirtTriangles(
            List<Vector3> combinedVertices,
            List<int> skirtTris,
            List<DigVolumeInfo> digVolumes,
            float targetTopWorld)
        {
            if (combinedVertices == null || skirtTris == null || skirtTris.Count == 0 || digVolumes == null || digVolumes.Count == 0)
                return;

            var skirtVolumes = CollectSkirtContactVolumes(digVolumes, targetTopWorld);
            if (skirtVolumes.Count == 0)
                return;

            float cellSizeX;
            float cellSizeZ;
            GetSourceCellSizeXZ(out cellSizeX, out cellSizeZ);
            float cellPadding = Mathf.Max(cellSizeX, cellSizeZ) * 0.7f;
            float trianglePadding = Mathf.Max(0.08f, cellPadding, settings.skirtWidth * 0.95f, settings.skirtHeight * 0.55f);
            var keptTriangles = new List<int>(skirtTris.Count);

            for (int i = 0; i < skirtTris.Count; i += 3)
            {
                int indexA = skirtTris[i];
                int indexB = skirtTris[i + 1];
                int indexC = skirtTris[i + 2];

                if (TriangleTouchesDig(
                    combinedVertices[indexA],
                    combinedVertices[indexB],
                    combinedVertices[indexC],
                    skirtVolumes,
                    trianglePadding))
                {
                    continue;
                }

                keptTriangles.Add(indexA);
                keptTriangles.Add(indexB);
                keptTriangles.Add(indexC);
            }

            skirtTris.Clear();
            skirtTris.AddRange(keptTriangles);
        }

        private List<DigVolumeInfo> CollectSkirtContactVolumes(List<DigVolumeInfo> digVolumes, float targetTopWorld)
        {
            var contactVolumes = new List<DigVolumeInfo>();
            if (digVolumes == null || digVolumes.Count == 0 || settings.skirtHeight <= SliceEpsilon)
                return contactVolumes;

            float skirtBottomWorld = targetTopWorld - Mathf.Max(SliceEpsilon, settings.skirtHeight) - SliceEpsilon;
            foreach (var digVolume in digVolumes)
            {
                if (digVolume == null)
                    continue;

                if (digVolume.volume != null && !digVolume.volume.removeContactingSkirt)
                    continue;

                if (digVolume.topWorld <= skirtBottomWorld || digVolume.bottomWorld >= targetTopWorld + SliceEpsilon)
                    continue;

                contactVolumes.Add(digVolume);
            }

            return contactVolumes;
        }

        private bool TriangleTouchesDig(
            Vector3 localA,
            Vector3 localB,
            Vector3 localC,
            List<DigVolumeInfo> digVolumes,
            float pointPaddingWorld)
        {
            Vector3 worldA = transform.TransformPoint(localA);
            Vector3 worldB = transform.TransformPoint(localB);
            Vector3 worldC = transform.TransformPoint(localC);
            Vector3 centroid = (worldA + worldB + worldC) / 3f;
            Vector3 abMid = (worldA + worldB) * 0.5f;
            Vector3 bcMid = (worldB + worldC) * 0.5f;
            Vector3 caMid = (worldC + worldA) * 0.5f;
            Vector3 aToCentroid = (worldA + centroid) * 0.5f;
            Vector3 bToCentroid = (worldB + centroid) * 0.5f;
            Vector3 cToCentroid = (worldC + centroid) * 0.5f;
            Vector3 triMin = Vector3.Min(worldA, Vector3.Min(worldB, worldC));
            Vector3 triMax = Vector3.Max(worldA, Vector3.Max(worldB, worldC));

            foreach (var digVolume in digVolumes)
            {
                if (digVolume == null)
                    continue;

                if (!TriangleBoundsTouchesDig(digVolume, triMin, triMax, pointPaddingWorld))
                    continue;

                if (PointTouchesDig(digVolume, worldA, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, worldB, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, worldC, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, centroid, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, abMid, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, bcMid, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, caMid, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, aToCentroid, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, bToCentroid, pointPaddingWorld) ||
                    PointTouchesDig(digVolume, cToCentroid, pointPaddingWorld))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TriangleBoundsTouchesDig(DigVolumeInfo digVolume, Vector3 triMinWorld, Vector3 triMaxWorld, float pointPaddingWorld)
        {
            if (digVolume == null)
                return false;

            Bounds triangleBounds = new Bounds((triMinWorld + triMaxWorld) * 0.5f, triMaxWorld - triMinWorld);
            triangleBounds.Expand(pointPaddingWorld * 2f);

            if (digVolume.volume != null || digVolume.worldBounds.size.sqrMagnitude > 0.000001f)
            {
                Bounds digBounds = digVolume.worldBounds;
                digBounds.Expand(pointPaddingWorld * 2f);
                return triangleBounds.Intersects(digBounds);
            }

            if (digVolume.tilemap != null)
                return DigTilemapTouchesWorldBounds(digVolume.tilemap, triangleBounds);

            return false;
        }

        private bool PointTouchesDig(DigVolumeInfo digVolume, Vector3 worldPoint, float pointPaddingWorld)
        {
            if (digVolume == null)
                return false;

            if (digVolume.volume != null)
                return digVolume.volume.ShouldCarveWorldPoint(worldPoint, pointPaddingWorld, 0f, false);

            if (digVolume.tilemap != null)
                return DigTilemapTouchesWorldPoint(digVolume.tilemap, worldPoint, pointPaddingWorld);

            if (digVolume.worldBounds.size.sqrMagnitude > 0.000001f)
            {
                Bounds expandedBounds = digVolume.worldBounds;
                expandedBounds.Expand(pointPaddingWorld * 2f);
                return expandedBounds.Contains(worldPoint);
            }

            return false;
        }

        private bool DigTilemapTouchesWorldBounds(Tilemap digTilemap, Bounds worldBounds)
        {
            if (digTilemap == null)
                return false;

            Vector3 cellSize3 = digTilemap.layoutGrid != null ? digTilemap.layoutGrid.cellSize : Vector3.one;
            float cellSizeX = Mathf.Abs(cellSize3.x) > SliceEpsilon ? Mathf.Abs(cellSize3.x) : 1f;
            float cellSizeZ = Mathf.Abs(cellSize3.y) > SliceEpsilon ? Mathf.Abs(cellSize3.y) : 1f;

            int minCellX = Mathf.FloorToInt((worldBounds.min.x - digTilemap.transform.position.x) / cellSizeX);
            int maxCellX = Mathf.CeilToInt((worldBounds.max.x - digTilemap.transform.position.x) / cellSizeX) - 1;
            int minCellY = Mathf.FloorToInt((worldBounds.min.z - digTilemap.transform.position.z) / cellSizeZ);
            int maxCellY = Mathf.CeilToInt((worldBounds.max.z - digTilemap.transform.position.z) / cellSizeZ) - 1;

            for (int y = minCellY; y <= maxCellY; y++)
            {
                for (int x = minCellX; x <= maxCellX; x++)
                {
                    if (digTilemap.HasTile(new Vector3Int(x, y, 0)))
                        return true;
                }
            }

            return false;
        }

        private bool DigTilemapTouchesWorldPoint(Tilemap digTilemap, Vector3 worldPoint, float pointPaddingWorld)
        {
            if (digTilemap == null)
                return false;

            Vector3 cellSize3 = digTilemap.layoutGrid != null ? digTilemap.layoutGrid.cellSize : Vector3.one;
            float cellSizeX = Mathf.Abs(cellSize3.x) > SliceEpsilon ? Mathf.Abs(cellSize3.x) : 1f;
            float cellSizeZ = Mathf.Abs(cellSize3.y) > SliceEpsilon ? Mathf.Abs(cellSize3.y) : 1f;
            int searchRadiusX = Mathf.Max(0, Mathf.CeilToInt(pointPaddingWorld / cellSizeX));
            int searchRadiusY = Mathf.Max(0, Mathf.CeilToInt(pointPaddingWorld / cellSizeZ));
            Vector3Int centerCell = digTilemap.WorldToCell(worldPoint);

            for (int offsetY = -searchRadiusY; offsetY <= searchRadiusY; offsetY++)
            {
                for (int offsetX = -searchRadiusX; offsetX <= searchRadiusX; offsetX++)
                {
                    Vector3Int candidateCell = new Vector3Int(centerCell.x + offsetX, centerCell.y + offsetY, centerCell.z);
                    if (!digTilemap.HasTile(candidateCell))
                        continue;

                    Vector3 cellCenter = digTilemap.GetCellCenterWorld(candidateCell);
                    float halfX = cellSizeX * 0.5f + pointPaddingWorld;
                    float halfZ = cellSizeZ * 0.5f + pointPaddingWorld;
                    if (Mathf.Abs(worldPoint.x - cellCenter.x) <= halfX &&
                        Mathf.Abs(worldPoint.z - cellCenter.z) <= halfZ)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private ProceduralTileMeshGenerator.ProceduralMeshSettings CreateSliceSettings(
            float sliceHeight,
            bool isTopmostSlice,
            bool isBottommostSlice,
            bool previewAsDigVolume)
        {
            return new ProceduralTileMeshGenerator.ProceduralMeshSettings
            {
                radius = settings.radius,
                depth = Mathf.Max(SliceEpsilon, sliceHeight),
                curveSegments = settings.curveSegments,
                skirtEnabled = previewAsDigVolume ? false : (settings.skirtEnabled && isTopmostSlice),
                skirtWidth = settings.skirtWidth,
                skirtHeight = settings.skirtHeight,
                skirtSegments = settings.skirtSegments,
                skirtUVScale = settings.skirtUVScale,
                skirtUVOffsetY = settings.skirtUVOffsetY,
                bottomMode = previewAsDigVolume
                    ? BottomMode.Flat
                    : (isBottommostSlice ? settings.bottomMode : BottomMode.None),
                bottomBevelInset = settings.bottomBevelInset,
                bottomBevelDepth = settings.bottomBevelDepth,
                bottomBevelSegments = settings.bottomBevelSegments,
                bottomBevelProfile = settings.bottomBevelProfile,
                bottomNoiseScale = settings.bottomNoiseScale,
                bottomNoiseAmplitude = settings.bottomNoiseAmplitude,
                bottomIslandSharpness = settings.bottomIslandSharpness,
                bottomIslandSmooth = settings.bottomIslandSmooth,
                bottomNoiseResolution = settings.bottomNoiseResolution,
                bottomNoiseSeed = settings.bottomNoiseSeed,
            };
        }

        private Material[] BuildMaterialArray(bool previewAsDigVolume)
        {
            if (previewAsDigVolume || !_meshHasSkirt)
                DestroyGeneratedSkirtDisplayResources();

            if (previewAsDigVolume)
            {
                Material digMat = digPreviewMaterial != null ? digPreviewMaterial : floorMaterial;
                if (digMat == null)
                    digMat = wallMaterial != null ? wallMaterial : digPreviewMaterial;

                var digMaterials = new List<Material> { digMat, digMat };
                if (_meshHasSkirt) digMaterials.Add(digMat);
                if (_meshHasBottom) digMaterials.Add(digMat);
                return digMaterials.ToArray();
            }

            Material capMat = floorMaterial;
            Material sideMat = wallMaterial != null ? wallMaterial : floorMaterial;

            var mats = new List<Material> { capMat, sideMat };
            if (_meshHasSkirt)
                mats.Add(ResolveSkirtDisplayMaterial());
            if (_meshHasBottom)
                mats.Add(bottomMaterial != null ? bottomMaterial : (wallMaterial != null ? wallMaterial : floorMaterial));
            return mats.ToArray();
        }

        private Material ResolveSkirtDisplayMaterial()
        {
            Material fallback = ceilingMaterial != null ? ceilingMaterial : floorMaterial;
            if (settings == null || settings.skirtMaterialMode != SkirtMaterialMode.UseFloorMaterialWithMask)
            {
                DestroyGeneratedSkirtDisplayResources();
                return fallback;
            }

            Material source = floorMaterial != null ? floorMaterial : fallback;
            if (source == null)
            {
                DestroyGeneratedSkirtDisplayResources();
                return fallback;
            }

            EnsureGeneratedSkirtDisplayResources(source);
            return _generatedSkirtMaterial != null ? _generatedSkirtMaterial : source;
        }

        private void EnsureGeneratedSkirtDisplayResources(Material sourceMaterial)
        {
            if (sourceMaterial == null || settings == null)
                return;

            settings.EnsureSkirtMaskCurve();
            int nextHash = ComputeSkirtMaskHash(sourceMaterial);
            if (_generatedSkirtMaterial != null &&
                _generatedSkirtMaskTexture != null &&
                _generatedSkirtSourceMaterial == sourceMaterial &&
                _generatedSkirtMaskHash == nextHash)
                return;

            DestroyGeneratedSkirtDisplayResources();

            _generatedSkirtMaskTexture = GenerateSkirtMaskTexture(settings.skirtMaskCurve);
            _generatedSkirtSourceMaterial = sourceMaterial;
            _generatedSkirtMaskHash = nextHash;

            _generatedSkirtMaterial = new Material(sourceMaterial)
            {
                name = sourceMaterial.name + " (Skirt Mask)",
                hideFlags = HideFlags.HideAndDontSave
            };

            if (_generatedSkirtMaterial.HasProperty("_LocalMaskTex"))
                _generatedSkirtMaterial.SetTexture("_LocalMaskTex", _generatedSkirtMaskTexture);
            if (_generatedSkirtMaterial.HasProperty("_UseLocalMask"))
                _generatedSkirtMaterial.SetFloat("_UseLocalMask", 1f);
            if (_generatedSkirtMaterial.HasProperty("_LocalMaskCutoff"))
                _generatedSkirtMaterial.SetFloat("_LocalMaskCutoff", 0.5f);
            if (_generatedSkirtMaterial.HasProperty("_AlphaClip"))
                _generatedSkirtMaterial.SetFloat("_AlphaClip", 1f);
            if (_generatedSkirtMaterial.HasProperty("_Cutoff"))
                _generatedSkirtMaterial.SetFloat("_Cutoff", Mathf.Max(0.45f, _generatedSkirtMaterial.GetFloat("_Cutoff")));

            _generatedSkirtMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private int ComputeSkirtMaskHash(Material sourceMaterial)
        {
            unchecked
            {
                int hash = sourceMaterial != null ? sourceMaterial.GetInstanceID() : 0;
                hash = hash * 31 + (int)settings.skirtMaterialMode;
                if (settings.skirtMaskCurve != null)
                {
                    hash = hash * 31 + settings.skirtMaskCurve.length;
                    for (int i = 0; i < settings.skirtMaskCurve.length; i++)
                    {
                        Keyframe key = settings.skirtMaskCurve.keys[i];
                        hash = hash * 31 + key.time.GetHashCode();
                        hash = hash * 31 + key.value.GetHashCode();
                        hash = hash * 31 + key.inTangent.GetHashCode();
                        hash = hash * 31 + key.outTangent.GetHashCode();
                    }
                }
                return hash;
            }
        }

        private static Texture2D GenerateSkirtMaskTexture(AnimationCurve maskCurve, int resolution = 256)
        {
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false, true)
            {
                name = "GeneratedSkirtMask",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            var curve = maskCurve != null && maskCurve.length > 0
                ? maskCurve
                : ProceduralTileMeshGenerator.CreateDefaultSkirtMaskCurve();

            var pixels = new Color32[resolution * resolution];
            for (int y = 0; y < resolution; y++)
            {
                float v = y / (float)(resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = x / (float)(resolution - 1);
                    float threshold = Mathf.Clamp01(curve.Evaluate(u));
                    byte value = v >= threshold ? (byte)255 : (byte)0;
                    pixels[y * resolution + x] = new Color32(value, value, value, value);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private void DestroyGeneratedSkirtDisplayResources()
        {
            _generatedSkirtMaskHash = int.MinValue;
            _generatedSkirtSourceMaterial = null;

            if (_generatedSkirtMaterial != null)
            {
                if (Application.isPlaying) Destroy(_generatedSkirtMaterial);
                else DestroyImmediate(_generatedSkirtMaterial);
                _generatedSkirtMaterial = null;
            }

            if (_generatedSkirtMaskTexture != null)
            {
                if (Application.isPlaying) Destroy(_generatedSkirtMaskTexture);
                else DestroyImmediate(_generatedSkirtMaskTexture);
                _generatedSkirtMaskTexture = null;
            }
        }

        private void AppendSliceGeometry(
            HashSet<Vector2Int> filledCells,
            HashSet<Vector2Int> exposedTopCells,
            HashSet<Vector2Int> exposedBottomCells,
            ProceduralTileMeshGenerator.ProceduralMeshSettings activeSettings,
            Dictionary<ProceduralTileType, Mesh> meshCache,
            float tileY,
            List<Vector3> capVerts,
            List<Vector2> capUVs,
            List<Vector2> capMaskUVs,
            List<int> capTris,
            List<int> wallTris,
            List<int> cutTris,
            List<int> skirtTris,
            List<int> bottomTris,
            bool isTopmostSlice,
            bool trackBottomPostDeform)
        {
            if (filledCells == null || filledCells.Count == 0 || meshCache == null)
                return;

            int minX = int.MaxValue;
            int maxX = int.MinValue;
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (var cell in filledCells)
            {
                if (cell.x < minX) minX = cell.x;
                if (cell.x > maxX) maxX = cell.x;
                if (cell.y < minY) minY = cell.y;
                if (cell.y > maxY) maxY = cell.y;
            }

            System.Func<int, int, bool> isFilled = (x, y) => filledCells.Contains(new Vector2Int(x, y));

            bool hasSkirt = activeSettings.skirtEnabled && activeSettings.skirtWidth > 0f && activeSettings.skirtHeight > 0f;
            bool hasBottom = activeSettings.bottomMode != BottomMode.None;
            bool buildGlobalFlatBottom = hasBottom && activeSettings.bottomMode == BottomMode.Flat;
            bool buildGlobalBevelBottom = hasBottom && activeSettings.bottomMode == BottomMode.Bevel;
            bool buildProjectedBottom = buildGlobalFlatBottom || buildGlobalBevelBottom;
            var ignoredCapTris = new List<int>();

            if (hasSkirt && _skirtVertexInfos == null)
                _skirtVertexInfos = new List<SkirtVertexInfo>();

            List<int> bottomList = (hasBottom && !buildProjectedBottom) ? bottomTris : null;

            for (int dy = minY; dy <= maxY + 1; dy++)
            {
                for (int dx = minX; dx <= maxX + 1; dx++)
                {
                    int mask = ProceduralTileMeshGenerator.ComputeDualGridMask(isFilled, dx, dy);
                    var result = ProceduralTileMeshGenerator.GetTileForMask(mask);
                    if (result == null) continue;

                    Vector3 pos = new Vector3(dx - 0.5f, tileY, dy - 0.5f);
                    if (result.isDiagonal)
                    {
                        AppendTileMesh(meshCache, activeSettings, result.type, pos, result.rotationDeg, capVerts, capUVs, capMaskUVs, ignoredCapTris, wallTris, skirtTris, bottomList);
                        AppendTileMesh(meshCache, activeSettings, result.type, pos, result.rotationDeg + 180f, capVerts, capUVs, capMaskUVs, ignoredCapTris, wallTris, skirtTris, bottomList);
                    }
                    else
                    {
                        AppendTileMesh(meshCache, activeSettings, result.type, pos, result.rotationDeg, capVerts, capUVs, capMaskUVs, ignoredCapTris, wallTris, skirtTris, bottomList);
                    }
                }
            }

            List<int> topCapTarget = isTopmostSlice ? capTris : cutTris;
            AppendCapGeometry(exposedTopCells, meshCache, activeSettings, tileY, capVerts, capUVs, capMaskUVs, topCapTarget, false, activeSettings.depth);
            if (exposedBottomCells != null && exposedBottomCells.Count > 0)
                AppendCapGeometry(exposedBottomCells, meshCache, activeSettings, tileY, capVerts, capUVs, capMaskUVs, cutTris, true, 0f);

            int bottomStartIndex = bottomTris.Count;

            List<BevelRingData> bevelRingData = null;

            if (hasBottom && activeSettings.bottomMode == BottomMode.IslandNoise)
            {
                ApplyIslandNoiseDeformer(capVerts, bottomTris, wallTris, filledCells, activeSettings, tileY);
            }
            else if (buildGlobalFlatBottom)
            {
                BuildGlobalFlatBottom(capVerts, capUVs, capMaskUVs, capTris, wallTris, bottomTris, tileY);
            }
            else if (buildGlobalBevelBottom)
            {
                bevelRingData = trackBottomPostDeform ? new List<BevelRingData>() : null;
                BuildGlobalBevelBottom(capVerts, capUVs, capMaskUVs, capTris, wallTris, bottomTris, activeSettings, tileY, bevelRingData);
            }

            if (!trackBottomPostDeform)
                return;

            if (buildGlobalFlatBottom && bottomTris.Count > bottomStartIndex)
            {
                _bottomIsFlat = true;
                var bottomVertSet = new HashSet<int>();
                for (int i = bottomStartIndex; i < bottomTris.Count; i++)
                    bottomVertSet.Add(bottomTris[i]);

                var wallVertIndicesSet = new HashSet<int>(wallTris);
                var wallByKey = BuildVertexLookup(wallVertIndicesSet, capVerts);

                _bottomVertexInfos = new List<BottomVertexInfo>(bottomVertSet.Count);
                foreach (int idx in bottomVertSet)
                {
                    if (idx < 0 || idx >= capVerts.Count) continue;
                    int wallIdx = FindNearestVertexIndex(wallByKey, capVerts, idx);
                    _bottomVertexInfos.Add(new BottomVertexInfo
                    {
                        combinedIndex = idx,
                        ring0Index = wallIdx,
                        baseOffset = wallIdx >= 0 ? (capVerts[idx] - capVerts[wallIdx]) : capVerts[idx]
                    });
                }
            }
            else if (buildGlobalBevelBottom && bottomTris.Count > bottomStartIndex
                     && bevelRingData != null && bevelRingData.Count > 0)
            {
                _bottomIsFlat = false;

                // Build a map from ring 0 bottom vertex → nearest wall vertex at same position.
                // Index wall vertices by QuantizedVertexKey for O(1) lookup.
                var wallVertIndicesSet = new HashSet<int>(wallTris);
                var wallByKey = BuildVertexLookup(wallVertIndicesSet, capVerts);

                var ring0ToWall = new Dictionary<int, int>();
                foreach (var rd in bevelRingData)
                {
                    if (rd.ring0Indices == null) continue;
                    foreach (int r0 in rd.ring0Indices)
                    {
                        if (r0 < 0 || r0 >= capVerts.Count) continue;
                        int bestWall = FindNearestVertexIndex(wallByKey, capVerts, r0);
                        if (bestWall >= 0)
                            ring0ToWall[r0] = bestWall;
                    }
                }

                // Build _bottomVertexInfos: ring 0 first (follow wall), then rings 1-N (follow ring 0).
                _bottomVertexInfos = new List<BottomVertexInfo>();

                foreach (var rd in bevelRingData)
                {
                    if (rd.allRingIndices == null || rd.allRingIndices.Count == 0) continue;
                    var ring0 = rd.allRingIndices[0];
                    int n = ring0.Count;

                    // Ring 0: follow wall vertex, offset = zero (they are seam-snapped).
                    for (int i = 0; i < n; i++)
                    {
                        int r0idx = ring0[i];
                        if (r0idx < 0 || r0idx >= capVerts.Count) continue;
                        if (!ring0ToWall.TryGetValue(r0idx, out int wallIdx)) continue;
                        _bottomVertexInfos.Add(new BottomVertexInfo
                        {
                            combinedIndex = r0idx,
                            ring0Index = wallIdx,
                            baseOffset = capVerts[r0idx] - capVerts[wallIdx]
                        });
                    }

                    // Rings 1..N: follow corresponding ring 0 vertex + stored offset.
                    for (int s = 1; s < rd.allRingIndices.Count; s++)
                    {
                        var ring = rd.allRingIndices[s];
                        for (int i = 0; i < Mathf.Min(ring.Count, n); i++)
                        {
                            int rIdx = ring[i];
                            int r0idx = ring0[i];
                            if (rIdx < 0 || rIdx >= capVerts.Count) continue;
                            if (r0idx < 0 || r0idx >= capVerts.Count) continue;
                            _bottomVertexInfos.Add(new BottomVertexInfo
                            {
                                combinedIndex = rIdx,
                                ring0Index = r0idx,
                                baseOffset = capVerts[rIdx] - capVerts[r0idx]
                            });
                        }
                    }
                }

                if (_bottomVertexInfos.Count == 0)
                    _bottomVertexInfos = null;
            }
        }

        private static HashSet<Vector2Int> BuildExposedCellSet(HashSet<Vector2Int> filledCells, HashSet<Vector2Int> neighborCells)
        {
            var exposed = new HashSet<Vector2Int>();
            if (filledCells == null || filledCells.Count == 0)
                return exposed;

            if (neighborCells == null || neighborCells.Count == 0)
                return new HashSet<Vector2Int>(filledCells);

            foreach (var cell in filledCells)
            {
                if (!neighborCells.Contains(cell))
                    exposed.Add(cell);
            }

            return exposed;
        }

        private void AppendCapGeometry(
            HashSet<Vector2Int> filledCells,
            Dictionary<ProceduralTileType, Mesh> meshCache,
            ProceduralTileMeshGenerator.ProceduralMeshSettings activeSettings,
            float tileY,
            List<Vector3> verts,
            List<Vector2> uvs,
            List<Vector2> maskUVs,
            List<int> tris,
            bool flipWinding,
            float localY)
        {
            if (filledCells == null || filledCells.Count == 0 || meshCache == null)
                return;

            int minX = int.MaxValue;
            int maxX = int.MinValue;
            int minY = int.MaxValue;
            int maxY = int.MinValue;
            foreach (var cell in filledCells)
            {
                if (cell.x < minX) minX = cell.x;
                if (cell.x > maxX) maxX = cell.x;
                if (cell.y < minY) minY = cell.y;
                if (cell.y > maxY) maxY = cell.y;
            }

            System.Func<int, int, bool> isFilled = (x, y) => filledCells.Contains(new Vector2Int(x, y));

            for (int dy = minY; dy <= maxY + 1; dy++)
            {
                for (int dx = minX; dx <= maxX + 1; dx++)
                {
                    int mask = ProceduralTileMeshGenerator.ComputeDualGridMask(isFilled, dx, dy);
                    var result = ProceduralTileMeshGenerator.GetTileForMask(mask);
                    if (result == null) continue;

                    Vector3 pos = new Vector3(dx - 0.5f, tileY, dy - 0.5f);
                    if (result.isDiagonal)
                    {
                        AppendTileCapMesh(meshCache, result.type, pos, result.rotationDeg, verts, uvs, maskUVs, tris, flipWinding, localY);
                        AppendTileCapMesh(meshCache, result.type, pos, result.rotationDeg + 180f, verts, uvs, maskUVs, tris, flipWinding, localY);
                    }
                    else
                    {
                        AppendTileCapMesh(meshCache, result.type, pos, result.rotationDeg, verts, uvs, maskUVs, tris, flipWinding, localY);
                    }
                }
            }
        }

        private void DestroyGeneratedMeshCache(Dictionary<ProceduralTileType, Mesh> meshCache)
        {
            if (meshCache == null)
                return;

            foreach (var mesh in meshCache.Values)
            {
                if (mesh == null) continue;
                if (Application.isPlaying) Destroy(mesh);
                else DestroyImmediate(mesh);
            }
        }

        private void ApplyDigSubtraction(HashSet<Vector2Int> filledCells, List<DigVolumeInfo> activeDigVolumes, float sliceBottomWorld, float sliceTopWorld)
        {
            if (filledCells == null || filledCells.Count == 0 || activeDigVolumes == null || activeDigVolumes.Count == 0)
                return;

            foreach (var digVolume in activeDigVolumes)
            {
                if (digVolume == null)
                    continue;

                if (digVolume.tilemap != null)
                {
                    ApplyDigSubtraction(filledCells, digVolume.tilemap);
                }
                else if (digVolume.volume != null)
                {
                    ApplyDigSubtraction(filledCells, digVolume.volume, digVolume.worldBounds, sliceBottomWorld, sliceTopWorld);
                }
            }
        }

        private void ApplyDigSubtraction(HashSet<Vector2Int> filledCells, Tilemap digTilemap)
        {
            if (filledCells == null || filledCells.Count == 0 || digTilemap == null || sourceTilemap == null || digTilemap == sourceTilemap)
                return;

            BoundsInt digBounds = digTilemap.cellBounds;
            foreach (var pos in digBounds.allPositionsWithin)
            {
                if (!digTilemap.HasTile(pos))
                    continue;

                Vector3 digWorldCenter = digTilemap.GetCellCenterWorld(pos);
                Vector3Int sourceCell = sourceTilemap.WorldToCell(digWorldCenter);
                filledCells.Remove(new Vector2Int(sourceCell.x, sourceCell.y));
            }
        }

        private void ApplyDigSubtraction(HashSet<Vector2Int> filledCells, QuickTileDigVolume digVolume, Bounds digWorldBounds, float sliceBottomWorld, float sliceTopWorld)
        {
            if (filledCells == null || filledCells.Count == 0 || sourceTilemap == null || digVolume == null)
                return;

            Vector3 tilemapWorld = sourceTilemap.transform.position;
            Vector3 cellSize3 = sourceTilemap.layoutGrid != null ? sourceTilemap.layoutGrid.cellSize : Vector3.one;
            float cellSizeX = Mathf.Abs(cellSize3.x) > SliceEpsilon ? Mathf.Abs(cellSize3.x) : 1f;
            float cellSizeZ = Mathf.Abs(cellSize3.y) > SliceEpsilon ? Mathf.Abs(cellSize3.y) : 1f;
            float sliceHeight = Mathf.Max(SliceEpsilon, sliceTopWorld - sliceBottomWorld);
            float sliceCenterY = (sliceBottomWorld + sliceTopWorld) * 0.5f;
            bool useDetailedSampling = digVolume.bevelEdges || digVolume.HasDetailedEdges();
            Vector3 paddingExtents = useDetailedSampling
                ? new Vector3(cellSizeX * 0.12f, sliceHeight * 0.12f, cellSizeZ * 0.12f)
                : new Vector3(cellSizeX * 0.5f, sliceHeight * 0.5f, cellSizeZ * 0.5f);
            float coveragePadding = paddingExtents.magnitude;
            float horizontalInsetWorld = 0f;
            float bevelInsetWorld = 0f;
            float baseInsetWorld = 0f;
            float lipRange = 0f;

            if (useDetailedSampling)
            {
                if (digVolume.bevelEdges && digVolume.edgeSmooth > 0.0001f)
                {
                    float bevelLipHeight = Mathf.Max(
                        Mathf.Max(cellSizeX, cellSizeZ) * 0.35f,
                        digVolume.GetBevelHeightWorld());
                    lipRange = Mathf.Max(SliceEpsilon, Mathf.Min(bevelLipHeight, digWorldBounds.size.y));
                    bevelInsetWorld = Mathf.Max(
                        digVolume.GetRoundedEdgeRadiusWorld(),
                        digVolume.GetBevelHeightWorld() * 0.85f);
                }

                baseInsetWorld = digVolume.GetBaseInsetWorld();

                if (lipRange > SliceEpsilon)
                {
                    float topT = 1f - Mathf.Clamp01((digWorldBounds.max.y - sliceCenterY) / lipRange);
                    if (topT > 0f && bevelInsetWorld > SliceEpsilon)
                    {
                        float curveT = Mathf.Sin(topT * Mathf.PI * 0.5f);
                        horizontalInsetWorld -= bevelInsetWorld * curveT;
                    }

                    float bottomT = 1f - Mathf.Clamp01((sliceCenterY - digWorldBounds.min.y) / lipRange);
                    if (bottomT > 0f)
                    {
                        float curveT = Mathf.Sin(bottomT * Mathf.PI * 0.5f);
                        float bottomInsetWorld = bevelInsetWorld + baseInsetWorld;
                        if (bottomInsetWorld > SliceEpsilon)
                            horizontalInsetWorld += bottomInsetWorld * curveT;
                    }
                }
            }

            float worldPadding = coveragePadding + digVolume.GetWorldBoundaryPadding() + Mathf.Max(0f, bevelInsetWorld);

            Bounds expandedBounds = digWorldBounds;
            expandedBounds.Expand(worldPadding * 2f);

            int minCellX = Mathf.FloorToInt((expandedBounds.min.x - tilemapWorld.x) / cellSizeX);
            int maxCellX = Mathf.CeilToInt((expandedBounds.max.x - tilemapWorld.x) / cellSizeX) - 1;
            int minCellY = Mathf.FloorToInt((expandedBounds.min.z - tilemapWorld.z) / cellSizeZ);
            int maxCellY = Mathf.CeilToInt((expandedBounds.max.z - tilemapWorld.z) / cellSizeZ) - 1;

            if (maxCellX < minCellX || maxCellY < minCellY)
                return;

            for (int y = minCellY; y <= maxCellY; y++)
            {
                for (int x = minCellX; x <= maxCellX; x++)
                {
                    var sourceCell = new Vector2Int(x, y);
                    if (!filledCells.Contains(sourceCell))
                        continue;

                    Vector3 worldCellCenter = new Vector3(
                        tilemapWorld.x + (x + 0.5f) * cellSizeX,
                        sliceCenterY,
                        tilemapWorld.z + (y + 0.5f) * cellSizeZ);

                    if (!digVolume.ShouldCarveWorldPoint(worldCellCenter, coveragePadding, horizontalInsetWorld, false))
                        continue;

                    filledCells.Remove(sourceCell);
                }
            }
        }

        private void ApplyDigPreviewVisuals()
        {
            if (sourceTilemap == null)
                return;

            if (_digPreviewTilemap != null && _digPreviewTilemap != sourceTilemap)
                RestoreDigPreviewVisuals();

            if (!_hasDigPreviewRestoreColor || _digPreviewTilemap != sourceTilemap)
            {
                _digPreviewRestoreColor = sourceTilemap.color;
                _hasDigPreviewRestoreColor = true;
            }

            _digPreviewTilemap = sourceTilemap;
            sourceTilemap.color = DigPreviewColor;

#if UNITY_EDITOR
            EditorUtility.SetDirty(sourceTilemap);
#endif
        }

        private void RestoreDigPreviewVisuals()
        {
            Tilemap previewTilemap = _digPreviewTilemap != null ? _digPreviewTilemap : sourceTilemap;
            if (previewTilemap != null && _hasDigPreviewRestoreColor)
            {
                previewTilemap.color = _digPreviewRestoreColor;

#if UNITY_EDITOR
                EditorUtility.SetDirty(previewTilemap);
#endif
            }

            _hasDigPreviewRestoreColor = false;
            _digPreviewTilemap = null;
        }

        /// <summary>
        /// Global island noise deformer — detects separate connected islands,
        /// computes a per-island centroid, and pulls bottom cap vertices downward
        /// to create a mountain/stalactite shape underneath each island.
        /// Also blends the lower portion of wall vertices for smooth transition.
        /// </summary>
        private void ApplyIslandNoiseDeformer(
            List<Vector3> verts, List<int> bottomTris, List<int> wallTris,
            HashSet<Vector2Int> filledCells,
            ProceduralTileMeshGenerator.ProceduralMeshSettings settings,
            float tileY)
        {
            if (bottomTris == null || bottomTris.Count == 0) return;

            float amplitude = settings.bottomNoiseAmplitude;
            float noiseScale = settings.bottomNoiseScale;
            float seed = settings.bottomNoiseSeed;
            float sharpness = settings.bottomIslandSharpness;
            bool useNoise = noiseScale > 0.0001f;

            // ── Step 1: Collect unique bottom vertex indices ──
            var bottomVertIndices = new HashSet<int>();
            for (int i = 0; i < bottomTris.Count; i++)
                bottomVertIndices.Add(bottomTris[i]);

            if (bottomVertIndices.Count == 0) return;

            HashSet<int> wallVertIndices = null;
            List<SeamNormalGroup> seamGroups = null;
            if (wallTris != null && wallTris.Count > 0)
            {
                wallVertIndices = new HashSet<int>(wallTris);
                seamGroups = BuildBottomWallSeamGroups(verts, bottomVertIndices, wallVertIndices);
            }

            // ── Step 2: Find connected components (separate islands) via flood-fill ──
            var cellToIsland = new Dictionary<Vector2Int, int>();
            var islands = new List<HashSet<Vector2Int>>();
            foreach (var cell in filledCells)
            {
                if (cellToIsland.ContainsKey(cell)) continue;

                // BFS flood fill
                int islandIdx = islands.Count;
                var island = new HashSet<Vector2Int>();
                var queue = new Queue<Vector2Int>();
                queue.Enqueue(cell);
                cellToIsland[cell] = islandIdx;
                island.Add(cell);

                while (queue.Count > 0)
                {
                    var c = queue.Dequeue();
                    // 4-connected neighbors
                    Vector2Int[] neighbors = {
                        new Vector2Int(c.x + 1, c.y),
                        new Vector2Int(c.x - 1, c.y),
                        new Vector2Int(c.x, c.y + 1),
                        new Vector2Int(c.x, c.y - 1)
                    };
                    foreach (var nb in neighbors)
                    {
                        if (filledCells.Contains(nb) && !cellToIsland.ContainsKey(nb))
                        {
                            cellToIsland[nb] = islandIdx;
                            island.Add(nb);
                            queue.Enqueue(nb);
                        }
                    }
                }
                islands.Add(island);
            }

            // ── Step 3: For each island, compute per-cell distance from edge ──
            // BFS from perimeter cells inward. Distance 0 = perimeter, higher = deeper interior.
            // This gives a smooth falloff for the deformer.
            var cellEdgeDist = new List<Dictionary<Vector2Int, int>>();
            for (int ii = 0; ii < islands.Count; ii++)
            {
                var distMap = new Dictionary<Vector2Int, int>();
                var bfsQueue = new Queue<Vector2Int>();

                // Seed: perimeter cells (any neighbor is empty) start at distance 0
                foreach (var c in islands[ii])
                {
                    if (!filledCells.Contains(new Vector2Int(c.x + 1, c.y)) ||
                        !filledCells.Contains(new Vector2Int(c.x - 1, c.y)) ||
                        !filledCells.Contains(new Vector2Int(c.x, c.y + 1)) ||
                        !filledCells.Contains(new Vector2Int(c.x, c.y - 1)))
                    {
                        distMap[c] = 0;
                        bfsQueue.Enqueue(c);
                    }
                }

                // BFS inward
                while (bfsQueue.Count > 0)
                {
                    var c = bfsQueue.Dequeue();
                    int d = distMap[c];
                    Vector2Int[] nbs = {
                        new Vector2Int(c.x + 1, c.y),
                        new Vector2Int(c.x - 1, c.y),
                        new Vector2Int(c.x, c.y + 1),
                        new Vector2Int(c.x, c.y - 1)
                    };
                    foreach (var nb in nbs)
                    {
                        if (islands[ii].Contains(nb) && !distMap.ContainsKey(nb))
                        {
                            distMap[nb] = d + 1;
                            bfsQueue.Enqueue(nb);
                        }
                    }
                }

                // Any island cells not reached (shouldn't happen) get distance 0
                foreach (var c in islands[ii])
                {
                    if (!distMap.ContainsKey(c))
                        distMap[c] = 0;
                }

                cellEdgeDist.Add(distMap);
            }

            // ── Step 4: Assign each bottom vertex to an island ──
            // Bottom vertices are in world-local space at dual-grid positions.
            // Map vertex XZ → nearest island by checking which island's cell region
            // contains the vertex. Dual-grid tiles are offset by -0.5 so a vertex
            // at (x, z) corresponds to cell region around (x+0.5, -z+0.5).
            // We check the 4 surrounding cells and pick the island that owns them.
            var islandVerts = new List<List<int>>();
            for (int ii = 0; ii < islands.Count; ii++)
                islandVerts.Add(new List<int>());

            foreach (int vi in bottomVertIndices)
            {
                Vector3 v = verts[vi];
                // Convert vertex XZ to cell space (undo the dual-grid offset and Z-mirror)
                // In Rebuild: pos = (dx - 0.5, tileY, dy - 0.5)
                // In GenerateBottomFlat: vertex = (p.x, 0, -p.y) then transformed by pos + rot
                // So world vertex X ≈ cellX - 0.5 + localX, Z ≈ cellY - 0.5 + localZ
                // The vertex XZ roughly falls within the cells that generated it.
                float fx = v.x;
                float fz = v.z;
                // Check 4 surrounding integer cells
                int cx0 = Mathf.FloorToInt(fx);
                int cz0 = Mathf.FloorToInt(fz);

                int bestIsland = -1;
                float bestDist = float.MaxValue;
                for (int dxx = 0; dxx <= 1; dxx++)
                {
                    for (int dzz = 0; dzz <= 1; dzz++)
                    {
                        var testCell = new Vector2Int(cx0 + dxx, cz0 + dzz);
                        int isIdx;
                        if (cellToIsland.TryGetValue(testCell, out isIdx))
                        {
                            // Distance from vertex to cell center
                            float ddx = fx - (testCell.x + 0.5f);
                            float ddz = fz - (testCell.y + 0.5f);
                            float d2 = ddx * ddx + ddz * ddz;
                            if (d2 < bestDist)
                            {
                                bestDist = d2;
                                bestIsland = isIdx;
                            }
                        }
                    }
                }
                if (bestIsland >= 0)
                    islandVerts[bestIsland].Add(vi);
            }

            // ── Step 5a: Precompute per-island centroid and max radius ──
            var islandCX = new float[islands.Count];
            var islandCZ = new float[islands.Count];
            var islandMaxDist = new float[islands.Count];
            var islandMaxEdgeDist = new int[islands.Count];
            var islandBoundarySegments = new List<List<BoundarySegment>>(islands.Count);
            var islandMaxBoundaryDist = new float[islands.Count];

            for (int ii = 0; ii < islands.Count; ii++)
            {
                var boundarySegments = BuildIslandBoundarySegments(islands[ii]);
                islandBoundarySegments.Add(boundarySegments);

                var iVerts = islandVerts[ii];
                if (iVerts.Count == 0)
                {
                    islandMaxDist[ii] = 1f;
                    islandMaxEdgeDist[ii] = 1;
                    islandMaxBoundaryDist[ii] = 1f;
                    continue;
                }

                float cx2 = 0f, cz2 = 0f;
                foreach (int vi in iVerts) { cx2 += verts[vi].x; cz2 += verts[vi].z; }
                cx2 /= iVerts.Count; cz2 /= iVerts.Count;
                islandCX[ii] = cx2; islandCZ[ii] = cz2;

                float mdsq = 0f;
                foreach (int vi in iVerts)
                {
                    float dx2 = verts[vi].x - cx2; float dz2 = verts[vi].z - cz2;
                    float d2 = dx2 * dx2 + dz2 * dz2;
                    if (d2 > mdsq) mdsq = d2;
                }
                islandMaxDist[ii] = Mathf.Sqrt(mdsq);
                if (islandMaxDist[ii] < 0.001f) islandMaxDist[ii] = 1f;

                int med = 0;
                foreach (var kv in cellEdgeDist[ii])
                    if (kv.Value > med) med = kv.Value;
                islandMaxEdgeDist[ii] = Mathf.Max(med, 1);

                float maxBoundaryDist = 0f;
                foreach (int vi in iVerts)
                {
                    float boundaryDist = DistanceToBoundary(new Vector2(verts[vi].x, verts[vi].z), boundarySegments);
                    if (boundaryDist > maxBoundaryDist)
                        maxBoundaryDist = boundaryDist;
                }
                islandMaxBoundaryDist[ii] = Mathf.Max(maxBoundaryDist, 0.001f);
            }

            // ── Step 5b: Deform each island independently ──
            // smooth controls fillet radius: 0 = sharp 90° corner, 1 = wide gentle curve
            float smooth = settings.bottomIslandSmooth;
            for (int ii = 0; ii < islands.Count; ii++)
            {
                var iVerts = islandVerts[ii];
                if (iVerts.Count == 0) continue;

                var distMap = cellEdgeDist[ii];
                int maxEdgeDist = islandMaxEdgeDist[ii];
                float icx = islandCX[ii];
                float icz = islandCZ[ii];
                float imaxDist = islandMaxDist[ii];
                var boundarySegments = islandBoundarySegments[ii];
                float maxBoundaryDist = islandMaxBoundaryDist[ii];
                float edgeFilletDepth = amplitude * smooth * 0.5f;

                foreach (int vi in iVerts)
                {
                    Vector3 v = verts[vi];

                    // ── Edge distance blend ──
                    // Bilinear sample of BFS cell edge distance at vertex position.
                    int vcx = Mathf.FloorToInt(v.x);
                    int vcz = Mathf.FloorToInt(v.z);
                    float fx = v.x - vcx;
                    float fz = v.z - vcz;

                    float d00 = 0f, d10 = 0f, d01 = 0f, d11 = 0f;
                    int tmp;
                    if (distMap.TryGetValue(new Vector2Int(vcx, vcz), out tmp)) d00 = tmp;
                    if (distMap.TryGetValue(new Vector2Int(vcx + 1, vcz), out tmp)) d10 = tmp;
                    if (distMap.TryGetValue(new Vector2Int(vcx, vcz + 1), out tmp)) d01 = tmp;
                    if (distMap.TryGetValue(new Vector2Int(vcx + 1, vcz + 1), out tmp)) d11 = tmp;

                    float edgeDistInterp = Mathf.Lerp(
                        Mathf.Lerp(d00, d10, fx),
                        Mathf.Lerp(d01, d11, fx),
                        fz);

                    // Blend the coarse cell-depth field with a continuous perimeter distance.
                    // The continuous term removes the 1-cell "flat shelf" that could appear
                    // near the wall/bottom junction when the BFS distance stayed at 0.
                    float cellInteriorT = Mathf.Clamp01(edgeDistInterp / maxEdgeDist);
                    float boundaryDist = DistanceToBoundary(new Vector2(v.x, v.z), boundarySegments);
                    float boundaryInteriorT = Mathf.Clamp01(boundaryDist / maxBoundaryDist);
                    float interiorT = Mathf.Max(cellInteriorT, boundaryInteriorT);
                    float edgeBlend = Mathf.SmoothStep(0f, 1f, interiorT);

                    // Distance from island centroid, normalized 0-1
                    float ddxc = v.x - icx;
                    float ddzc = v.z - icz;
                    float dist = Mathf.Sqrt(ddxc * ddxc + ddzc * ddzc);
                    float edgeT = Mathf.Clamp01(dist / imaxDist);
                    float centerT = 1f - edgeT;

                    // Depth curve: power-based — sharpness controls pointiness
                    float depthT = Mathf.Pow(centerT, sharpness);
                    float islandDepth = depthT * amplitude;

                    // ── Fillet: additive depth near edge ──
                    // Problem: islandDepth ≈ 0 at the edge (centerT small, pow makes it tiny).
                    // This creates a flat shelf the deformer can't curve.
                    // Fix: add a fillet bonus that provides its own depth at the edge,
                    // independent of centroid distance. The bonus fades to 0 at interior
                    // so only the edge zone is affected.
                    //
                    // smooth=0 → no fillet (sharp corner)
                    // smooth=1 → large fillet spanning full edgeBlend range
                    float filletBonus = 0f;
                    float filletT = 1f; // progress through fillet zone (for noise fading)
                    if (smooth > 0.01f)
                    {
                        filletT = Mathf.Clamp01(edgeBlend / smooth);
                        // sin(ft*PI/2): 0→1, so (1 - sin): 1→0 (max at edge, zero at end)
                        filletBonus = edgeFilletDepth
                            * (1f - Mathf.Sin(filletT * Mathf.PI * 0.5f));
                    }

                    float depth = islandDepth + filletBonus;

                    float noise = 0f;
                    if (useNoise)
                    {
                        // Multi-octave Perlin noise for rocky texture
                        float nx = (v.x + seed) * noiseScale;
                        float nz = (v.z + seed * 1.7f) * noiseScale;
                        noise = Mathf.PerlinNoise(nx, nz) - 0.5f;
                        noise += 0.5f * (Mathf.PerlinNoise(nx * 2.3f + 31.7f, nz * 2.3f + 17.3f) - 0.5f);
                        noise += 0.25f * (Mathf.PerlinNoise(nx * 4.7f + 67.1f, nz * 4.7f + 43.9f) - 0.5f);
                    }

                    // Noise weight: strongest in middle belt, fades at edges and in fillet
                    float noiseWeight = 4f * centerT * (1f - centerT) * filletT;

                    // Apply Y deformation
                    v.y -= depth;
                    v.y += noise * amplitude * 0.4f * noiseWeight;

                    // XZ noise displacement for rocky look (fades near edge and in fillet)
                    if (useNoise && filletT > 0.3f && edgeT < 0.85f)
                    {
                        float xzScale = amplitude * 0.1f * noiseWeight;
                        float nx2 = (v.x + seed * 3.1f) * noiseScale * 1.5f;
                        float nz2 = (v.z + seed * 2.7f) * noiseScale * 1.5f;
                        v.x += (Mathf.PerlinNoise(nx2, nz2) - 0.5f) * xzScale;
                        v.z += (Mathf.PerlinNoise(nz2 + 50f, nx2 + 50f) - 0.5f) * xzScale;
                    }

                    // XZ pinch toward centroid — fades in fillet zone
                    float pinchT = Mathf.Lerp(0.08f, 0.02f, filletT) * centerT;
                    v.x += (icx - v.x) * pinchT;
                    v.z += (icz - v.z) * pinchT;

                    verts[vi] = v;
                }
            }

            // ── Step 6: Wall fillet — curve wall bottom with quarter-circle profile ──
            // smooth controls fillet radius: 0 = no curve, 1 = wide gentle arc.
            if (smooth > 0.01f && wallTris != null && wallTris.Count > 0)
            {
                float wallHeight = -tileY;
                float filletH = wallHeight * smooth * 0.5f;
                float filletTopY = tileY + filletH;

                foreach (int vi in wallVertIndices)
                {
                    Vector3 v = verts[vi];
                    if (v.y >= filletTopY) continue;

                    float t = (filletH > 0.001f) ? Mathf.Clamp01((v.y - tileY) / filletH) : 0f;
                    float curveT = 1f - Mathf.Sin(t * Mathf.PI * 0.5f);

                    int wcx = Mathf.FloorToInt(v.x);
                    int wcz = Mathf.FloorToInt(v.z);
                    int bestIsland = -1;
                    float bestD = float.MaxValue;
                    for (int dxx = 0; dxx <= 1; dxx++)
                    {
                        for (int dzz = 0; dzz <= 1; dzz++)
                        {
                            var tc = new Vector2Int(wcx + dxx, wcz + dzz);
                            int isIdx;
                            if (cellToIsland.TryGetValue(tc, out isIdx))
                            {
                                float dx2 = v.x - (tc.x + 0.5f);
                                float dz2 = v.z - (tc.y + 0.5f);
                                float d2 = dx2 * dx2 + dz2 * dz2;
                                if (d2 < bestD) { bestD = d2; bestIsland = isIdx; }
                            }
                        }
                    }
                    if (bestIsland < 0) continue;

                    float wicx = islandCX[bestIsland];
                    float wicz = islandCZ[bestIsland];
                    float wimaxDist = islandMaxDist[bestIsland];

                    float ddxw = v.x - wicx;
                    float ddzw = v.z - wicz;
                    float distW = Mathf.Sqrt(ddxw * ddxw + ddzw * ddzw);
                    float centerTW = 1f - Mathf.Clamp01(distW / wimaxDist);

                    float depthTW = Mathf.Pow(centerTW, sharpness);
                    float islandDepth = depthTW * amplitude;
                    float edgeFilletDepth = amplitude * smooth * 0.5f;
                    float wallPull = (islandDepth + edgeFilletDepth) * curveT;
                    v.y -= wallPull;

                    float inwardFrac = 0.08f * centerTW * curveT;
                    v.x += (wicx - v.x) * inwardFrac;
                    v.z += (wicz - v.z) * inwardFrac;

                    verts[vi] = v;
                }
            }

            if (seamGroups != null && seamGroups.Count > 0)
                SnapBottomWallSeamVertices(verts, seamGroups);
        }

        private void BuildGlobalFlatBottom(
            List<Vector3> verts, List<Vector2> uvs, List<Vector2> maskUVs, List<int> capTris, List<int> wallTris, List<int> bottomTris, float tileY)
        {
            if (verts == null || uvs == null || maskUVs == null || bottomTris == null)
                return;

            if (capTris != null && capTris.Count > 0)
            {
                BuildProjectedBottomFromCap(verts, uvs, maskUVs, capTris, bottomTris, tileY);
                return;
            }

            if (wallTris == null || wallTris.Count == 0)
                return;

            var boundaryLoops = ExtractWallBottomLoops(verts, wallTris, tileY);
            for (int loopIndex = 0; loopIndex < boundaryLoops.Count; loopIndex++)
            {
                var loop = boundaryLoops[loopIndex];
                if (loop == null || loop.Count < 3)
                    continue;

                float signedArea = ComputeSignedArea(loop);
                bool isClockwise = signedArea < 0f;
                int baseIndex = verts.Count;

                for (int i = 0; i < loop.Count; i++)
                {
                    Vector2 point = loop[i];
                    verts.Add(new Vector3(point.x, tileY, -point.y));
                    uvs.Add(new Vector2(point.x + 0.5f, point.y + 0.5f));
                    maskUVs.Add(Vector2.zero);
                }

                ProceduralTileMeshGenerator.TriangulateCap(
                    loop,
                    bottomTris,
                    baseIndex,
                    !isClockwise);
            }
        }

        /// <summary>
        /// Per-loop ring structure emitted by BuildGlobalBevelBottom so the caller can
        /// build post-deformation tracking (BottomVertexInfo list).
        /// </summary>
        private struct BevelRingData
        {
            /// <summary>Ring 0 vertex indices (outer ring at wall bottom Y). Length = n.</summary>
            public List<int> ring0Indices;
            /// <summary>All ring indices: ringIndices[s][i], s=0..segments, i=0..n-1.</summary>
            public List<List<int>> allRingIndices;
            /// <summary>Set of ring 0 vertex indices that were seam-matched to wall vertices.</summary>
            public HashSet<int> seamBottomIndices;
        }

        private void BuildGlobalBevelBottom(
            List<Vector3> verts, List<Vector2> uvs, List<Vector2> maskUVs, List<int> capTris, List<int> wallTris, List<int> bottomTris,
            ProceduralTileMeshGenerator.ProceduralMeshSettings settings,
            float tileY,
            List<BevelRingData> outRingData = null)
        {
            if (verts == null || uvs == null || maskUVs == null || wallTris == null || bottomTris == null)
                return;

            float bevelInset = Mathf.Max(0.0001f, settings.bottomBevelInset);
            float bevelDepth = Mathf.Max(0f, settings.bottomBevelDepth);
            bool concave = settings.bottomBevelProfile == BevelProfile.Concave;
            int bevelSegments = Mathf.Max(2, settings.bottomBevelSegments * 2);

            var boundaryLoops = ExtractWallBottomLoops(verts, wallTris, tileY);
            if (boundaryLoops.Count == 0)
            {
                if (capTris != null && capTris.Count > 0)
                    BuildProjectedBottomFromCap(verts, uvs, maskUVs, capTris, bottomTris, tileY);
                return;
            }

            var bottomSeamIndices = new HashSet<int>();
            var wallVertIndices = new HashSet<int>(wallTris);

            for (int li = 0; li < boundaryLoops.Count; li++)
            {
                var loop = boundaryLoops[li];
                if (loop == null || loop.Count < 3)
                    continue;

                float signedArea = ComputeSignedArea(loop);
                bool isClockwise = signedArea < 0f;
                int n = loop.Count;
                var innerLoop = new List<Vector2>(n);
                var outerRingIndices = new List<int>(n);

                for (int i = 0; i < n; i++)
                {
                    Vector2 curr = loop[i];
                    outerRingIndices.Add(verts.Count);
                    bottomSeamIndices.Add(verts.Count);
                    verts.Add(new Vector3(curr.x, tileY, -curr.y));
                    uvs.Add(Vector2.zero);
                    maskUVs.Add(Vector2.zero);

                    Vector2 prev = loop[(i - 1 + n) % n];
                    Vector2 next = loop[(i + 1) % n];

                    Vector2 prevDir = (curr - prev).normalized;
                    Vector2 nextDir = (next - curr).normalized;

                    Vector2 inwardPrev = isClockwise
                        ? new Vector2(prevDir.y, -prevDir.x)
                        : new Vector2(-prevDir.y, prevDir.x);
                    Vector2 inwardNext = isClockwise
                        ? new Vector2(nextDir.y, -nextDir.x)
                        : new Vector2(-nextDir.y, nextDir.x);

                    Vector2 inward = inwardPrev + inwardNext;
                    if (inward.sqrMagnitude < 0.000001f)
                        inward = inwardPrev.sqrMagnitude > 0.000001f ? inwardPrev : inwardNext;
                    if (inward.sqrMagnitude < 0.000001f)
                    {
                        innerLoop.Add(curr);
                        continue;
                    }

                    inward.Normalize();

                    float prevLen = Vector2.Distance(prev, curr);
                    float nextLen = Vector2.Distance(curr, next);
                    float miterDenom = Mathf.Max(0.35f, Mathf.Min(Vector2.Dot(inward, inwardPrev), Vector2.Dot(inward, inwardNext)));
                    // Keep the roundover readable on long straight runs, but still cap it
                    // below the full edge length so tight corners do not fold over themselves.
                    float localInset = Mathf.Min(bevelInset / miterDenom, Mathf.Min(prevLen, nextLen) * 0.85f);
                    innerLoop.Add(curr + inward * localInset);
                }

                if (outerRingIndices.Count < 3 || innerLoop.Count < 3)
                    continue;

                // Light smoothing keeps corners bevelled instead of collapsing into pointy fans.
                int smoothPasses = Mathf.Clamp(bevelSegments - 1, 0, 2);
                for (int pass = 0; pass < smoothPasses; pass++)
                {
                    var smoothed = new List<Vector2>(innerLoop);
                    for (int i = 0; i < n; i++)
                    {
                        Vector2 prev = innerLoop[(i - 1 + n) % n];
                        Vector2 curr = innerLoop[i];
                        Vector2 next = innerLoop[(i + 1) % n];
                        Vector2 avg = (prev + curr + next) / 3f;
                        Vector2 outer = loop[i];
                        Vector2 delta = avg - outer;
                        delta = Vector2.ClampMagnitude(delta, bevelInset);
                        smoothed[i] = outer + delta;
                    }
                    innerLoop = smoothed;
                }

                var ringIndices = new List<List<int>>(bevelSegments + 1) { outerRingIndices };

                for (int s = 1; s <= bevelSegments; s++)
                {
                    float t = (float)s / bevelSegments;
                    float angle = t * Mathf.PI * 0.5f;
                    float horizT = concave ? Mathf.Sin(angle) : (1f - Mathf.Cos(angle));
                    float yT = concave ? (1f - Mathf.Cos(angle)) : Mathf.Sin(angle);

                    var ring = new List<int>(n);

                    for (int i = 0; i < n; i++)
                    {
                        Vector2 outer = loop[i];
                        Vector2 inner = innerLoop[i];
                        Vector2 p = Vector2.Lerp(outer, inner, horizT);
                        ring.Add(verts.Count);
                        verts.Add(new Vector3(p.x, tileY - yT * bevelDepth, -p.y));
                        uvs.Add(Vector2.zero);
                        maskUVs.Add(Vector2.zero);
                    }

                    ringIndices.Add(ring);
                }

                for (int s = 0; s < bevelSegments; s++)
                {
                    var ringA = ringIndices[s];
                    var ringB = ringIndices[s + 1];

                    for (int i = 0; i < n; i++)
                    {
                        int next = (i + 1) % n;
                        int a0 = ringA[i];
                        int a1 = ringA[next];
                        int b0 = ringB[i];
                        int b1 = ringB[next];

                        if (!isClockwise)
                        {
                            bottomTris.Add(a0);
                            bottomTris.Add(b0);
                            bottomTris.Add(a1);
                            bottomTris.Add(a1);
                            bottomTris.Add(b0);
                            bottomTris.Add(b1);
                        }
                        else
                        {
                            bottomTris.Add(a0);
                            bottomTris.Add(a1);
                            bottomTris.Add(b0);
                            bottomTris.Add(a1);
                            bottomTris.Add(b1);
                            bottomTris.Add(b0);
                        }
                    }
                }

                ProceduralTileMeshGenerator.TriangulateCap(
                    innerLoop,
                    bottomTris,
                    ringIndices[bevelSegments][0],
                    !isClockwise);

                if (outRingData != null)
                {
                    outRingData.Add(new BevelRingData
                    {
                        ring0Indices = outerRingIndices,
                        allRingIndices = ringIndices,
                        seamBottomIndices = bottomSeamIndices
                    });
                }
            }

            if (bottomSeamIndices.Count > 0 && wallVertIndices.Count > 0)
            {
                var seamGroups = BuildBottomWallSeamGroups(verts, bottomSeamIndices, wallVertIndices);
                if (seamGroups.Count > 0)
                    SnapBottomWallSeamVertices(verts, seamGroups);
            }
        }

        private static void BuildProjectedBottomFromCap(
            List<Vector3> verts, List<Vector2> uvs, List<Vector2> maskUVs, List<int> capTris, List<int> bottomTris, float tileY)
        {
            if (verts == null || uvs == null || maskUVs == null || capTris == null || bottomTris == null || capTris.Count == 0)
                return;

            var bottomVertMap = new Dictionary<int, int>();

            for (int i = 0; i < capTris.Count; i++)
            {
                int srcIndex = capTris[i];
                if (srcIndex < 0 || srcIndex >= verts.Count) continue;
                if (bottomVertMap.ContainsKey(srcIndex)) continue;

                Vector3 top = verts[srcIndex];
                bottomVertMap[srcIndex] = verts.Count;
                verts.Add(new Vector3(top.x, tileY, top.z));
                uvs.Add(srcIndex < uvs.Count ? uvs[srcIndex] : Vector2.zero);
                maskUVs.Add(srcIndex < maskUVs.Count ? maskUVs[srcIndex] : Vector2.zero);
            }

            for (int i = 0; i <= capTris.Count - 3; i += 3)
            {
                int ia = capTris[i];
                int ib = capTris[i + 1];
                int ic = capTris[i + 2];

                if (!bottomVertMap.TryGetValue(ia, out int a) ||
                    !bottomVertMap.TryGetValue(ib, out int b) ||
                    !bottomVertMap.TryGetValue(ic, out int c))
                    continue;

                bottomTris.Add(a);
                bottomTris.Add(c);
                bottomTris.Add(b);
            }
        }

        private static List<List<Vector2>> ExtractBoundaryLoopsFromTriangles(
            IReadOnlyList<Vector3> verts, IReadOnlyList<int> tris, float planeY)
        {
            const float planeEpsilon = 0.0001f;

            var points = new Dictionary<QuantizedPoint2DKey, Vector2>();
            var edgeUseCount = new Dictionary<UndirectedEdge2DKey, int>();
            var edgePoints = new Dictionary<UndirectedEdge2DKey, (QuantizedPoint2DKey a, QuantizedPoint2DKey b)>();

            void AddEdge(int ia, int ib)
            {
                if (ia < 0 || ib < 0 || ia >= verts.Count || ib >= verts.Count) return;

                Vector3 va = verts[ia];
                Vector3 vb = verts[ib];
                if (Mathf.Abs(va.y - planeY) > planeEpsilon || Mathf.Abs(vb.y - planeY) > planeEpsilon)
                    return;

                Vector2 pa = new Vector2(va.x, -va.z);
                Vector2 pb = new Vector2(vb.x, -vb.z);
                var ka = new QuantizedPoint2DKey(pa);
                var kb = new QuantizedPoint2DKey(pb);
                if (ka.Equals(kb)) return;

                points[ka] = pa;
                points[kb] = pb;

                var edgeKey = new UndirectedEdge2DKey(ka, kb);
                if (edgeUseCount.TryGetValue(edgeKey, out int count))
                    edgeUseCount[edgeKey] = count + 1;
                else
                    edgeUseCount[edgeKey] = 1;

                if (!edgePoints.ContainsKey(edgeKey))
                    edgePoints[edgeKey] = (ka, kb);
            }

            for (int i = 0; i <= tris.Count - 3; i += 3)
            {
                int ia = tris[i];
                int ib = tris[i + 1];
                int ic = tris[i + 2];
                AddEdge(ia, ib);
                AddEdge(ib, ic);
                AddEdge(ic, ia);
            }

            var boundaryEdges = new Dictionary<UndirectedEdge2DKey, (QuantizedPoint2DKey a, QuantizedPoint2DKey b)>();
            foreach (var pair in edgeUseCount)
            {
                if (pair.Value != 1) continue;
                boundaryEdges[pair.Key] = edgePoints[pair.Key];
            }

            var adjacency = new Dictionary<QuantizedPoint2DKey, List<QuantizedPoint2DKey>>();
            foreach (var edge in boundaryEdges.Values)
            {
                if (!adjacency.TryGetValue(edge.a, out var na))
                {
                    na = new List<QuantizedPoint2DKey>();
                    adjacency[edge.a] = na;
                }
                if (!adjacency.TryGetValue(edge.b, out var nb))
                {
                    nb = new List<QuantizedPoint2DKey>();
                    adjacency[edge.b] = nb;
                }

                if (!na.Contains(edge.b)) na.Add(edge.b);
                if (!nb.Contains(edge.a)) nb.Add(edge.a);
            }

            var unusedEdges = new HashSet<UndirectedEdge2DKey>(boundaryEdges.Keys);
            var loops = new List<List<Vector2>>();

            foreach (var edge in boundaryEdges.Values)
            {
                var startEdge = new UndirectedEdge2DKey(edge.a, edge.b);
                if (!unusedEdges.Contains(startEdge)) continue;

                var loopKeys = new List<QuantizedPoint2DKey>();
                QuantizedPoint2DKey start = edge.a;
                QuantizedPoint2DKey previous = default;
                QuantizedPoint2DKey current = edge.a;
                QuantizedPoint2DKey next = edge.b;
                bool hasPrevious = false;

                loopKeys.Add(start);

                while (true)
                {
                    var currentEdge = new UndirectedEdge2DKey(current, next);
                    if (!unusedEdges.Remove(currentEdge))
                    {
                        loopKeys.Clear();
                        break;
                    }

                    previous = current;
                    current = next;
                    hasPrevious = true;

                    if (current.Equals(start))
                        break;

                    loopKeys.Add(current);

                    if (!adjacency.TryGetValue(current, out var neighbors) || neighbors.Count == 0)
                    {
                        loopKeys.Clear();
                        break;
                    }

                    bool found = false;
                    for (int ni = 0; ni < neighbors.Count; ni++)
                    {
                        var candidate = neighbors[ni];
                        if (hasPrevious && candidate.Equals(previous)) continue;
                        if (unusedEdges.Contains(new UndirectedEdge2DKey(current, candidate)))
                        {
                            next = candidate;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        for (int ni = 0; ni < neighbors.Count; ni++)
                        {
                            var candidate = neighbors[ni];
                            if (unusedEdges.Contains(new UndirectedEdge2DKey(current, candidate)))
                            {
                                next = candidate;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        loopKeys.Clear();
                        break;
                    }
                }

                if (loopKeys.Count >= 3 && current.Equals(start))
                {
                    var loop = new List<Vector2>(loopKeys.Count);
                    for (int i = 0; i < loopKeys.Count; i++)
                    {
                        if (points.TryGetValue(loopKeys[i], out Vector2 p))
                            loop.Add(p);
                    }

                    if (loop.Count >= 3)
                        loops.Add(loop);
                }
            }

            return loops;
        }

        private static List<BoundarySegment> ExtractBoundarySegmentsFromTriangles(
            IReadOnlyList<Vector3> verts, IReadOnlyList<int> tris, float planeY)
        {
            const float planeEpsilon = 0.0001f;

            var points = new Dictionary<QuantizedPoint2DKey, Vector2>();
            var edgeUseCount = new Dictionary<UndirectedEdge2DKey, int>();
            var edgePoints = new Dictionary<UndirectedEdge2DKey, (QuantizedPoint2DKey a, QuantizedPoint2DKey b)>();

            void AddEdge(int ia, int ib)
            {
                if (ia < 0 || ib < 0 || ia >= verts.Count || ib >= verts.Count) return;

                Vector3 va = verts[ia];
                Vector3 vb = verts[ib];
                if (Mathf.Abs(va.y - planeY) > planeEpsilon || Mathf.Abs(vb.y - planeY) > planeEpsilon)
                    return;

                Vector2 pa = new Vector2(va.x, -va.z);
                Vector2 pb = new Vector2(vb.x, -vb.z);
                var ka = new QuantizedPoint2DKey(pa);
                var kb = new QuantizedPoint2DKey(pb);
                if (ka.Equals(kb)) return;

                points[ka] = pa;
                points[kb] = pb;

                var edgeKey = new UndirectedEdge2DKey(ka, kb);
                if (edgeUseCount.TryGetValue(edgeKey, out int count))
                    edgeUseCount[edgeKey] = count + 1;
                else
                    edgeUseCount[edgeKey] = 1;

                if (!edgePoints.ContainsKey(edgeKey))
                    edgePoints[edgeKey] = (ka, kb);
            }

            for (int i = 0; i <= tris.Count - 3; i += 3)
            {
                int ia = tris[i];
                int ib = tris[i + 1];
                int ic = tris[i + 2];
                AddEdge(ia, ib);
                AddEdge(ib, ic);
                AddEdge(ic, ia);
            }

            var segments = new List<BoundarySegment>();
            foreach (var pair in edgeUseCount)
            {
                if (pair.Value != 1) continue;
                var edge = edgePoints[pair.Key];
                if (!points.TryGetValue(edge.a, out Vector2 a) || !points.TryGetValue(edge.b, out Vector2 b))
                    continue;
                segments.Add(new BoundarySegment(a, b));
            }

            return segments;
        }

        private static bool BuildWeldedCapFromTriangles(
            IReadOnlyList<Vector3> verts, IReadOnlyList<int> tris, float planeY,
            out List<Vector2> weldedPoints, out List<int> weldedTris)
        {
            var localWeldedPoints = new List<Vector2>();
            var localWeldedTris = new List<int>();

            if (verts == null || tris == null || tris.Count < 3)
            {
                weldedPoints = localWeldedPoints;
                weldedTris = localWeldedTris;
                return false;
            }

            const float planeEpsilon = 0.0001f;

            var pointToIndex = new Dictionary<QuantizedPoint2DKey, int>();
            var triangleKeys = new HashSet<TriangleKey>();

            int GetOrCreateIndex(int srcIndex)
            {
                if (srcIndex < 0 || srcIndex >= verts.Count)
                    return -1;

                Vector3 v = verts[srcIndex];
                if (Mathf.Abs(v.y - planeY) > planeEpsilon)
                    return -1;

                Vector2 p = new Vector2(v.x, -v.z);
                var key = new QuantizedPoint2DKey(p);
                if (!pointToIndex.TryGetValue(key, out int idx))
                {
                    idx = localWeldedPoints.Count;
                    localWeldedPoints.Add(p);
                    pointToIndex[key] = idx;
                }

                return idx;
            }

            for (int i = 0; i <= tris.Count - 3; i += 3)
            {
                int a = GetOrCreateIndex(tris[i]);
                int b = GetOrCreateIndex(tris[i + 1]);
                int c = GetOrCreateIndex(tris[i + 2]);
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || c == a)
                    continue;

                var triKey = new TriangleKey(a, b, c);
                if (!triangleKeys.Add(triKey))
                    continue;

                localWeldedTris.Add(a);
                localWeldedTris.Add(b);
                localWeldedTris.Add(c);
            }

            weldedPoints = localWeldedPoints;
            weldedTris = localWeldedTris;
            return weldedPoints.Count >= 3 && weldedTris.Count >= 3;
        }

        private static Dictionary<int, List<int>> BuildTriangleAdjacency(IReadOnlyList<int> tris)
        {
            var adjacencySets = new Dictionary<int, HashSet<int>>();

            void Link(int a, int b)
            {
                if (a == b) return;

                if (!adjacencySets.TryGetValue(a, out var setA))
                {
                    setA = new HashSet<int>();
                    adjacencySets[a] = setA;
                }
                if (!adjacencySets.TryGetValue(b, out var setB))
                {
                    setB = new HashSet<int>();
                    adjacencySets[b] = setB;
                }

                setA.Add(b);
                setB.Add(a);
            }

            for (int i = 0; i <= tris.Count - 3; i += 3)
            {
                int ia = tris[i];
                int ib = tris[i + 1];
                int ic = tris[i + 2];
                Link(ia, ib);
                Link(ib, ic);
                Link(ic, ia);
            }

            var adjacency = new Dictionary<int, List<int>>(adjacencySets.Count);
            foreach (var pair in adjacencySets)
                adjacency[pair.Key] = new List<int>(pair.Value);

            return adjacency;
        }

        private static float ComputeSignedArea(IReadOnlyList<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return 0f;

            float area = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Count];
                area += a.x * b.y - b.x * a.y;
            }

            return area * 0.5f;
        }

        private static List<List<Vector2>> ExtractWallBottomLoops(List<Vector3> verts, List<int> wallTris, float tileY)
        {
            const float planeEpsilon = 0.0001f;

            var points = new Dictionary<QuantizedPoint2DKey, Vector2>();
            var edges = new Dictionary<UndirectedEdge2DKey, (QuantizedPoint2DKey a, QuantizedPoint2DKey b)>();

            void AddBottomEdge(int ia, int ib)
            {
                if (ia < 0 || ib < 0 || ia >= verts.Count || ib >= verts.Count) return;

                Vector3 va = verts[ia];
                Vector3 vb = verts[ib];
                if (Mathf.Abs(va.y - tileY) > planeEpsilon || Mathf.Abs(vb.y - tileY) > planeEpsilon)
                    return;

                Vector2 pa = new Vector2(va.x, -va.z);
                Vector2 pb = new Vector2(vb.x, -vb.z);
                var ka = new QuantizedPoint2DKey(pa);
                var kb = new QuantizedPoint2DKey(pb);
                if (ka.Equals(kb)) return;

                points[ka] = pa;
                points[kb] = pb;

                var edgeKey = new UndirectedEdge2DKey(ka, kb);
                if (!edges.ContainsKey(edgeKey))
                    edges[edgeKey] = (ka, kb);
            }

            for (int i = 0; i <= wallTris.Count - 3; i += 3)
            {
                int ia = wallTris[i];
                int ib = wallTris[i + 1];
                int ic = wallTris[i + 2];
                AddBottomEdge(ia, ib);
                AddBottomEdge(ib, ic);
                AddBottomEdge(ic, ia);
            }

            var adjacency = new Dictionary<QuantizedPoint2DKey, List<QuantizedPoint2DKey>>();
            foreach (var edge in edges.Values)
            {
                if (!adjacency.TryGetValue(edge.a, out var na))
                {
                    na = new List<QuantizedPoint2DKey>();
                    adjacency[edge.a] = na;
                }
                if (!adjacency.TryGetValue(edge.b, out var nb))
                {
                    nb = new List<QuantizedPoint2DKey>();
                    adjacency[edge.b] = nb;
                }

                if (!na.Contains(edge.b)) na.Add(edge.b);
                if (!nb.Contains(edge.a)) nb.Add(edge.a);
            }

            var unusedEdges = new HashSet<UndirectedEdge2DKey>(edges.Keys);
            var loops = new List<List<Vector2>>();

            foreach (var edge in edges.Values)
            {
                var startEdge = new UndirectedEdge2DKey(edge.a, edge.b);
                if (!unusedEdges.Contains(startEdge)) continue;

                var loopKeys = new List<QuantizedPoint2DKey>();
                QuantizedPoint2DKey start = edge.a;
                QuantizedPoint2DKey previous = default;
                QuantizedPoint2DKey current = edge.a;
                QuantizedPoint2DKey next = edge.b;
                bool hasPrevious = false;

                loopKeys.Add(start);

                while (true)
                {
                    var currentEdge = new UndirectedEdge2DKey(current, next);
                    if (!unusedEdges.Remove(currentEdge))
                    {
                        loopKeys.Clear();
                        break;
                    }

                    previous = current;
                    current = next;
                    hasPrevious = true;

                    if (current.Equals(start))
                        break;

                    loopKeys.Add(current);

                    if (!adjacency.TryGetValue(current, out var neighbors) || neighbors.Count == 0)
                    {
                        loopKeys.Clear();
                        break;
                    }

                    bool found = false;
                    for (int ni = 0; ni < neighbors.Count; ni++)
                    {
                        var candidate = neighbors[ni];
                        if (hasPrevious && candidate.Equals(previous)) continue;
                        if (unusedEdges.Contains(new UndirectedEdge2DKey(current, candidate)))
                        {
                            next = candidate;
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        for (int ni = 0; ni < neighbors.Count; ni++)
                        {
                            var candidate = neighbors[ni];
                            if (unusedEdges.Contains(new UndirectedEdge2DKey(current, candidate)))
                            {
                                next = candidate;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (!found)
                    {
                        loopKeys.Clear();
                        break;
                    }
                }

                if (loopKeys.Count >= 3 && current.Equals(start))
                {
                    var loop = new List<Vector2>(loopKeys.Count);
                    for (int i = 0; i < loopKeys.Count; i++)
                        loop.Add(points[loopKeys[i]]);
                    loops.Add(loop);
                }
            }

            return loops;
        }

        private static List<BoundarySegment> BuildIslandBoundarySegments(HashSet<Vector2Int> islandCells)
        {
            var segments = new List<BoundarySegment>();
            if (islandCells == null || islandCells.Count == 0) return segments;

            foreach (var cell in islandCells)
            {
                float minX = cell.x;
                float maxX = cell.x + 1f;
                float minZ = cell.y;
                float maxZ = cell.y + 1f;

                if (!islandCells.Contains(new Vector2Int(cell.x - 1, cell.y)))
                    segments.Add(new BoundarySegment(new Vector2(minX, minZ), new Vector2(minX, maxZ)));
                if (!islandCells.Contains(new Vector2Int(cell.x + 1, cell.y)))
                    segments.Add(new BoundarySegment(new Vector2(maxX, maxZ), new Vector2(maxX, minZ)));
                if (!islandCells.Contains(new Vector2Int(cell.x, cell.y - 1)))
                    segments.Add(new BoundarySegment(new Vector2(maxX, minZ), new Vector2(minX, minZ)));
                if (!islandCells.Contains(new Vector2Int(cell.x, cell.y + 1)))
                    segments.Add(new BoundarySegment(new Vector2(minX, maxZ), new Vector2(maxX, maxZ)));
            }

            return segments;
        }

        private static float DistanceToBoundary(Vector2 point, List<BoundarySegment> segments)
        {
            if (segments == null || segments.Count == 0) return 0f;

            float bestDistSq = float.MaxValue;
            for (int i = 0; i < segments.Count; i++)
            {
                float distSq = DistancePointToSegmentSq(point, segments[i].a, segments[i].b);
                if (distSq < bestDistSq)
                    bestDistSq = distSq;
            }

            return bestDistSq < float.MaxValue ? Mathf.Sqrt(bestDistSq) : 0f;
        }

        private static void EvaluateBoundaryResponse(
            Vector2 point, List<BoundarySegment> segments,
            out float distance, out Vector2 inwardGuide)
        {
            distance = 0f;
            inwardGuide = Vector2.zero;
            if (segments == null || segments.Count == 0)
                return;

            const float distanceSlackSq = 0.0001f;
            float bestDistSq = float.MaxValue;
            Vector2 accumulatedGuide = Vector2.zero;
            Vector2 fallbackGuide = Vector2.zero;

            for (int i = 0; i < segments.Count; i++)
            {
                // When a point is equally close to multiple boundary segments
                // (typical around corners), averaging the guides gives a stable inward bisector.
                Vector2 projection = ClosestPointOnSegment(point, segments[i].a, segments[i].b);
                Vector2 guide = point - projection;
                float distSq = guide.sqrMagnitude;

                if (distSq + distanceSlackSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    accumulatedGuide = guide;
                    fallbackGuide = guide;
                }
                else if (Mathf.Abs(distSq - bestDistSq) <= distanceSlackSq)
                {
                    accumulatedGuide += guide;
                }
            }

            if (bestDistSq < float.MaxValue)
                distance = Mathf.Sqrt(bestDistSq);

            Vector2 finalGuide = accumulatedGuide.sqrMagnitude > 0.000001f ? accumulatedGuide : fallbackGuide;
            inwardGuide = finalGuide.sqrMagnitude > 0.000001f ? finalGuide.normalized : Vector2.zero;
        }

        private static float DistancePointToSegmentSq(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float denom = ab.sqrMagnitude;
            if (denom < 0.000001f)
                return (point - a).sqrMagnitude;

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / denom);
            Vector2 projection = a + ab * t;
            return (point - projection).sqrMagnitude;
        }

        private static Vector2 ClosestPointOnSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float denom = ab.sqrMagnitude;
            if (denom < 0.000001f)
                return a;

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / denom);
            return a + ab * t;
        }

        private static List<SeamNormalGroup> BuildBottomWallSeamGroups(
            IReadOnlyList<Vector3> verts, HashSet<int> bottomVerts, HashSet<int> wallVerts)
        {
            var seamMap = new Dictionary<QuantizedVertexKey, SeamNormalGroup>();
            if (verts == null || bottomVerts == null || wallVerts == null) return new List<SeamNormalGroup>();

            foreach (int vi in bottomVerts)
            {
                if (vi < 0 || vi >= verts.Count) continue;
                var key = new QuantizedVertexKey(verts[vi]);
                if (!seamMap.TryGetValue(key, out var group))
                {
                    group = new SeamNormalGroup();
                    seamMap[key] = group;
                }
                group.bottomIndices.Add(vi);
            }

            foreach (int vi in wallVerts)
            {
                if (vi < 0 || vi >= verts.Count) continue;
                var key = new QuantizedVertexKey(verts[vi]);
                if (!seamMap.TryGetValue(key, out var group))
                {
                    group = new SeamNormalGroup();
                    seamMap[key] = group;
                }
                group.wallIndices.Add(vi);
            }

            var seamGroups = new List<SeamNormalGroup>();
            foreach (var pair in seamMap)
            {
                if (pair.Value.bottomIndices.Count > 0 && pair.Value.wallIndices.Count > 0)
                    seamGroups.Add(pair.Value);
            }

            return seamGroups;
        }

        private static void SnapBottomWallSeamVertices(List<Vector3> verts, List<SeamNormalGroup> seamGroups)
        {
            if (verts == null || seamGroups == null || seamGroups.Count == 0) return;

            for (int gi = 0; gi < seamGroups.Count; gi++)
            {
                var group = seamGroups[gi];
                if (group.bottomIndices.Count == 0 || group.wallIndices.Count == 0)
                    continue;

                Vector3 avg = Vector3.zero;
                int count = 0;

                for (int i = 0; i < group.bottomIndices.Count; i++)
                {
                    int vi = group.bottomIndices[i];
                    if (vi < 0 || vi >= verts.Count) continue;
                    avg += verts[vi];
                    count++;
                }

                for (int i = 0; i < group.wallIndices.Count; i++)
                {
                    int vi = group.wallIndices[i];
                    if (vi < 0 || vi >= verts.Count) continue;
                    avg += verts[vi];
                    count++;
                }

                if (count == 0) continue;
                avg /= count;

                for (int i = 0; i < group.bottomIndices.Count; i++)
                {
                    int vi = group.bottomIndices[i];
                    if (vi >= 0 && vi < verts.Count)
                        verts[vi] = avg;
                }

                for (int i = 0; i < group.wallIndices.Count; i++)
                {
                    int vi = group.wallIndices[i];
                    if (vi >= 0 && vi < verts.Count)
                        verts[vi] = avg;
                }
            }
        }

        private static void SmoothBottomWallSeamNormals(Mesh mesh, List<int> bottomTris, List<int> wallTris)
        {
            if (mesh == null || bottomTris == null || wallTris == null || bottomTris.Count == 0 || wallTris.Count == 0)
                return;

            var verts = mesh.vertices;
            var normals = mesh.normals;
            if (verts == null || normals == null || verts.Length != normals.Length)
                return;

            var bottomVerts = new HashSet<int>(bottomTris);
            var wallVerts = new HashSet<int>(wallTris);
            var seamGroups = new Dictionary<QuantizedVertexKey, SeamNormalGroup>();

            foreach (int vi in bottomVerts)
            {
                if (vi < 0 || vi >= verts.Length) continue;
                var key = new QuantizedVertexKey(verts[vi]);
                if (!seamGroups.TryGetValue(key, out var group))
                {
                    group = new SeamNormalGroup();
                    seamGroups[key] = group;
                }
                group.bottomIndices.Add(vi);
            }

            foreach (int vi in wallVerts)
            {
                if (vi < 0 || vi >= verts.Length) continue;
                var key = new QuantizedVertexKey(verts[vi]);
                if (!seamGroups.TryGetValue(key, out var group))
                {
                    group = new SeamNormalGroup();
                    seamGroups[key] = group;
                }
                group.wallIndices.Add(vi);
            }

            foreach (var pair in seamGroups)
            {
                var group = pair.Value;
                if (group.bottomIndices.Count == 0 || group.wallIndices.Count == 0)
                    continue;

                Vector3 avg = Vector3.zero;
                for (int i = 0; i < group.bottomIndices.Count; i++)
                    avg += normals[group.bottomIndices[i]];
                for (int i = 0; i < group.wallIndices.Count; i++)
                    avg += normals[group.wallIndices[i]];

                if (avg.sqrMagnitude < 0.000001f)
                    continue;

                avg.Normalize();
                for (int i = 0; i < group.bottomIndices.Count; i++)
                    normals[group.bottomIndices[i]] = avg;
                for (int i = 0; i < group.wallIndices.Count; i++)
                    normals[group.wallIndices[i]] = avg;
            }

            mesh.normals = normals;
        }

        private void NotifyDeformers()
        {
            // Unsubscribe from previous deformer
            if (_subscribedDeformer != null)
            {
                _subscribedDeformer.OnPostApply -= OnPostDeform;
                _subscribedDeformer = null;
            }

            // Find the deformer
            var deformer = GetComponentInParent<RadialHillDeformer>();
            if (deformer == null && transform.parent != null)
            {
                deformer = transform.parent.GetComponentInChildren<RadialHillDeformer>(true);
            }

            if (deformer != null)
            {
                // Subscribe for post-deform skirt rebuild and/or bottom cap restoration
                bool needsSkirtPostDeform = _meshHasSkirt && _skirtVertexInfos != null && _skirtVertexInfos.Count > 0;
                bool needsBottomPostDeform = _bottomVertexInfos != null && _bottomVertexInfos.Count > 0;

                if (needsSkirtPostDeform || needsBottomPostDeform)
                {
                    _subscribedDeformer = deformer;
                    deformer.OnPostApply += OnPostDeform;
                }
                deformer.RecacheAndApply();
            }
        }

        /// <summary>
        /// Called after RadialHillDeformer finishes deforming.
        /// Recalculates skirt vertex positions from the deformed ring0 (cap edge) positions
        /// so the skirt follows the deformed surface naturally.
        /// For Flat bottom: restores interior bottom vertices while keeping the wall seam stitched.
        /// For Bevel bottom: ring>0 vertices follow deformed ring0 + stored offset.
        /// </summary>
        private void OnPostDeform()
        {
            if (_combinedGO == null) return;

            bool hasSkirtWork = _skirtVertexInfos != null && _skirtVertexInfos.Count > 0;
            bool hasBottomWork = _bottomVertexInfos != null && _bottomVertexInfos.Count > 0;
            if (!hasSkirtWork && !hasBottomWork) return;

            var mf = _combinedGO.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;

            var mesh = mf.sharedMesh;
            var verts = mesh.vertices;

            // For each tracked skirt vertex (ring>0), recompute its position:
            // newPos = deformedRing0Pos + localOffset
            // This makes the skirt curve follow the deformed surface.
            if (hasSkirtWork)
            {
                for (int i = 0; i < _skirtVertexInfos.Count; i++)
                {
                    var info = _skirtVertexInfos[i];
                    if (info.combinedIndex < 0 || info.combinedIndex >= verts.Length) continue;
                    if (info.ring0Index < 0 || info.ring0Index >= verts.Length) continue;

                    Vector3 deformedRing0 = verts[info.ring0Index];
                    verts[info.combinedIndex] = deformedRing0 + info.localOffset;
                }
            }

            // Restore bottom cap vertices after deformation.
            // Flat: restore interior vertices to the undeformed plane, but keep seam vertices
            // glued to the wall so the underside does not crack open after deformation.
            // Bevel: follow deformed ring 0 + stored offset (bevel stays stitched to walls).
            if (hasBottomWork)
            {
                if (_bottomIsFlat)
                {
                    for (int i = 0; i < _bottomVertexInfos.Count; i++)
                    {
                        var info = _bottomVertexInfos[i];
                        if (info.combinedIndex < 0 || info.combinedIndex >= verts.Length) continue;
                        if (info.ring0Index >= 0 && info.ring0Index < verts.Length)
                            verts[info.combinedIndex] = verts[info.ring0Index] + info.baseOffset;
                        else
                            verts[info.combinedIndex] = info.baseOffset;
                    }
                }
                else
                {
                    // Bevel: ring>0 vertices follow their ring 0 counterpart
                    for (int i = 0; i < _bottomVertexInfos.Count; i++)
                    {
                        var info = _bottomVertexInfos[i];
                        if (info.combinedIndex < 0 || info.combinedIndex >= verts.Length) continue;
                        if (info.ring0Index < 0 || info.ring0Index >= verts.Length) continue;
                        verts[info.combinedIndex] = verts[info.ring0Index] + info.baseOffset;
                    }
                }
            }

            mesh.vertices = verts;
            mesh.RecalculateNormals();

            if (_meshHasBottom && mesh.subMeshCount > 2)
            {
                int bottomSubmeshIndex = mesh.subMeshCount - 1;
                int[] wallTriangles = mesh.GetTriangles(1);
                int[] bottomTriangles = mesh.GetTriangles(bottomSubmeshIndex);
                if (wallTriangles != null && wallTriangles.Length > 0 &&
                    bottomTriangles != null && bottomTriangles.Length > 0)
                {
                    SmoothBottomWallSeamNormals(
                        mesh,
                        new List<int>(bottomTriangles),
                        new List<int>(wallTriangles));
                }
            }

            mesh.RecalculateBounds();
        }

        /// <summary>
        /// Force regeneration of cached tile prototypes (call when settings change).
        /// </summary>
        public void InvalidateMeshCache()
        {
            if (_cachedMeshes != null)
            {
                foreach (var mesh in _cachedMeshes.Values)
                {
                    if (Application.isPlaying) Destroy(mesh);
                    else DestroyImmediate(mesh);
                }
                _cachedMeshes = null;
            }
            _lastRadius = -1f;
        }

        /// <summary>
        /// Access the combined mesh (for deformer systems at runtime).
        /// </summary>
        public Mesh GetCombinedMesh() => _combinedMesh;

        // ─────────────────────────────────────────────
        // Internal — mesh combining
        // ─────────────────────────────────────────────

        private void AppendTileCapMesh(
            Dictionary<ProceduralTileType, Mesh> meshCache,
            ProceduralTileType type,
            Vector3 position,
            float rotationDeg,
            List<Vector3> verts,
            List<Vector2> uvs,
            List<Vector2> maskUVs,
            List<int> tris,
            bool flipWinding,
            float localY)
        {
            if (meshCache == null || !meshCache.ContainsKey(type)) return;

            Mesh tileMesh = meshCache[type];
            if (tileMesh == null || tileMesh.subMeshCount == 0)
                return;

            int[] capTriangles = tileMesh.GetTriangles(0);
            if (capTriangles == null || capTriangles.Length == 0)
                return;

            Vector3[] srcVerts = tileMesh.vertices;
            Vector2[] srcUVs = tileMesh.uv;
            var srcMaskUVs = new List<Vector2>();
            tileMesh.GetUVs(1, srcMaskUVs);
            Quaternion rot = Quaternion.Euler(0f, rotationDeg, 0f);
            var vertexMap = new Dictionary<int, int>();

            for (int i = 0; i < capTriangles.Length; i++)
            {
                int srcIndex = capTriangles[i];
                if (vertexMap.ContainsKey(srcIndex))
                    continue;

                Vector3 src = srcVerts[srcIndex];
                src.y = localY;
                vertexMap[srcIndex] = verts.Count;
                verts.Add(position + rot * src);
                uvs.Add(srcIndex < srcUVs.Length ? srcUVs[srcIndex] : Vector2.zero);
                maskUVs.Add(srcIndex < srcMaskUVs.Count ? srcMaskUVs[srcIndex] : Vector2.zero);
            }

            for (int i = 0; i <= capTriangles.Length - 3; i += 3)
            {
                int a = vertexMap[capTriangles[i]];
                int b = vertexMap[capTriangles[i + 1]];
                int c = vertexMap[capTriangles[i + 2]];

                if (flipWinding)
                {
                    tris.Add(a);
                    tris.Add(c);
                    tris.Add(b);
                }
                else
                {
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);
                }
            }
        }

        private void AppendTileMesh(
            Dictionary<ProceduralTileType, Mesh> meshCache,
            ProceduralTileMeshGenerator.ProceduralMeshSettings activeSettings,
            ProceduralTileType type,
            Vector3 position,
            float rotationDeg,
            List<Vector3> verts,
            List<Vector2> uvs,
            List<Vector2> maskUVs,
            List<int> capTris,
            List<int> wallTris,
            List<int> skirtTris,
            List<int> bottomTris)
        {
            if (meshCache == null || !meshCache.ContainsKey(type)) return;

            Mesh tileMesh = meshCache[type];
            Vector3[] srcVerts = tileMesh.vertices;
            Vector2[] srcUVs = tileMesh.uv;
            var srcMaskUVs = new List<Vector2>();
            tileMesh.GetUVs(1, srcMaskUVs);

            Quaternion rot = Quaternion.Euler(0f, rotationDeg, 0f);
            int baseIdx = verts.Count;

            // Transform and append vertices
            for (int i = 0; i < srcVerts.Length; i++)
            {
                verts.Add(position + rot * srcVerts[i]);
                uvs.Add(i < srcUVs.Length ? srcUVs[i] : Vector2.zero);
                maskUVs.Add(i < srcMaskUVs.Count ? srcMaskUVs[i] : Vector2.zero);
            }

            // Append cap triangles (submesh 0) with offset
            if (tileMesh.subMeshCount > 0)
            {
                int[] tris0 = tileMesh.GetTriangles(0);
                for (int i = 0; i < tris0.Length; i++)
                    capTris.Add(tris0[i] + baseIdx);
            }

            // Append wall triangles (submesh 1) with offset
            if (tileMesh.subMeshCount > 1)
            {
                int[] tris1 = tileMesh.GetTriangles(1);
                for (int i = 0; i < tris1.Length; i++)
                    wallTris.Add(tris1[i] + baseIdx);
            }

            bool hasLocalBottom = bottomTris != null && activeSettings.bottomMode != BottomMode.None;
            bool hasLocalSkirt = activeSettings.skirtEnabled && tileMesh.subMeshCount > (hasLocalBottom ? 3 : 2);
            int skirtSubmeshIndex = hasLocalSkirt ? 2 : -1;
            int bottomSubmeshIndex = hasLocalBottom && tileMesh.subMeshCount >= (hasLocalSkirt ? 4 : 3)
                ? tileMesh.subMeshCount - 1
                : -1;

            // Append skirt triangles with offset + build skirt vertex info
            if (skirtSubmeshIndex >= 0 && _skirtVertexInfos != null)
            {
                int[] tris2 = tileMesh.GetTriangles(skirtSubmeshIndex);
                for (int i = 0; i < tris2.Length; i++)
                    skirtTris.Add(tris2[i] + baseIdx);

                // Build skirt vertex mapping:
                // Prototype layout: wallVerts(2*n) + capVerts(n) + skirtVerts((segs+1)*n) [+ bottomVerts...]
                // n = outline vertex count. Walls use 2 rings of n verts each.
                // Skirt starts at index 3*n with (skirtSegs+1) rings of n verts.
                // Bottom vertices (if any) come after skirt and must NOT be included.
                // We derive n from the expected pre-bottom layout: n*(3 + skirtSegs+1) = n*(4+skirtSegs)
                int skirtSegs = activeSettings.skirtSegments;
                int divisor = 4 + skirtSegs;
                // Expected vertex count without bottom = n * divisor
                // With bottom, totalVerts >= n * divisor. Derive n:
                int preBottomCount = srcVerts.Length;
                // If bottom submesh exists, subtract its vertex contribution
                // by finding the min vertex index used by the bottom submesh
                if (bottomSubmeshIndex >= 0)
                {
                    int[] bTris = tileMesh.GetTriangles(bottomSubmeshIndex);
                    if (bTris.Length > 0)
                    {
                        int minBottomVert = int.MaxValue;
                        for (int bi = 0; bi < bTris.Length; bi++)
                            if (bTris[bi] < minBottomVert) minBottomVert = bTris[bi];
                        preBottomCount = minBottomVert;
                    }
                }
                int n = preBottomCount / divisor;
                if (n > 0 && n * divisor == preBottomCount)
                {
                    int skirtStart = 3 * n; // first skirt vertex in prototype

                    // For each ring > 0, each vertex maps to ring 0 vertex
                    for (int s = 1; s <= skirtSegs; s++)
                    {
                        for (int vi = 0; vi < n; vi++)
                        {
                            int ring0ProtoIdx = skirtStart + vi;
                            int thisProtoIdx = skirtStart + s * n + vi;

                            Vector3 ring0World = position + rot * srcVerts[ring0ProtoIdx];
                            Vector3 thisWorld = position + rot * srcVerts[thisProtoIdx];

                            _skirtVertexInfos.Add(new SkirtVertexInfo
                            {
                                combinedIndex = baseIdx + thisProtoIdx,
                                ring0Index = baseIdx + ring0ProtoIdx,
                                localOffset = thisWorld - ring0World
                            });
                        }
                    }
                }
            }
            else if (skirtSubmeshIndex >= 0)
            {
                // No _skirtVertexInfos tracking, just append triangles
                int[] tris2 = tileMesh.GetTriangles(skirtSubmeshIndex);
                for (int i = 0; i < tris2.Length; i++)
                    skirtTris.Add(tris2[i] + baseIdx);
            }

            // Append bottom triangles (last submesh if bottom mode is active)
            // The bottom submesh is always the LAST submesh in the tile mesh.
            // Submesh layout: 0=cap, 1=walls, [2=skirt], [last=bottom]
            // We know bottom exists if bottomMode != None and subMeshCount >= 3
            if (bottomSubmeshIndex >= 0)
            {
                int[] btris = tileMesh.GetTriangles(bottomSubmeshIndex);
                for (int i = 0; i < btris.Length; i++)
                    bottomTris.Add(btris[i] + baseIdx);
            }
        }

        private void EnsureCombinedGO()
        {
            if (_combinedGO == null)
            {
                var existing = transform.Find("ProceduralTiles");
                if (existing != null)
                {
                    _combinedGO = existing.gameObject;
                    // Ensure it has the right components
                    if (_combinedGO.GetComponent<MeshFilter>() == null)
                        _combinedGO.AddComponent<MeshFilter>();
                    if (_combinedGO.GetComponent<MeshRenderer>() == null)
                        _combinedGO.AddComponent<MeshRenderer>();
                }
                else
                {
                    _combinedGO = new GameObject("ProceduralTiles");
                    _combinedGO.transform.SetParent(transform, false);
                    _combinedGO.AddComponent<MeshFilter>();
                    _combinedGO.AddComponent<MeshRenderer>();
                }
            }
        }

        private void ClearCombinedMesh()
        {
            _meshHasSkirt = false;
            _meshHasBottom = false;
            _skirtVertexInfos = null;
            _bottomVertexInfos = null;

            if (_subscribedDeformer != null)
            {
                _subscribedDeformer.OnPostApply -= OnPostDeform;
                _subscribedDeformer = null;
            }

            if (_combinedMesh != null)
            {
                _combinedMesh.Clear();
            }
            if (_combinedGO != null)
            {
                var mf = _combinedGO.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = null;
            }

            ClearCollider();
        }

        private void EnsureMeshCache()
        {
            bool needsRegen = _cachedMeshes == null
                || !Mathf.Approximately(_lastRadius, settings.radius)
                || !Mathf.Approximately(_lastDepth, settings.depth)
                || _lastCurveSegments != settings.curveSegments
                || _lastSkirtEnabled != settings.skirtEnabled
                || !Mathf.Approximately(_lastSkirtWidth, settings.skirtWidth)
                || !Mathf.Approximately(_lastSkirtHeight, settings.skirtHeight)
                || _lastSkirtSegments != settings.skirtSegments
                || !Mathf.Approximately(_lastSkirtUVScale, settings.skirtUVScale)
                || !Mathf.Approximately(_lastSkirtUVOffsetY, settings.skirtUVOffsetY)
                || _lastBottomMode != settings.bottomMode
                || _lastBottomHash != ComputeBottomHash();

            if (!needsRegen) return;

            InvalidateMeshCache();
            _cachedMeshes = ProceduralTileMeshGenerator.GenerateAllMeshes(GetPrototypeMeshSettings());
            _lastRadius = settings.radius;
            _lastDepth = settings.depth;
            _lastCurveSegments = settings.curveSegments;
            _lastSkirtEnabled = settings.skirtEnabled;
            _lastSkirtWidth = settings.skirtWidth;
            _lastSkirtHeight = settings.skirtHeight;
            _lastSkirtSegments = settings.skirtSegments;
            _lastSkirtUVScale = settings.skirtUVScale;
            _lastSkirtUVOffsetY = settings.skirtUVOffsetY;
            _lastBottomMode = settings.bottomMode;
            _lastBottomHash = ComputeBottomHash();
        }

        private ProceduralTileMeshGenerator.ProceduralMeshSettings GetPrototypeMeshSettings()
        {
            var prototypeSettings = new ProceduralTileMeshGenerator.ProceduralMeshSettings
            {
                radius = settings.radius,
                depth = settings.depth,
                curveSegments = settings.curveSegments,
                skirtEnabled = settings.skirtEnabled,
                skirtWidth = settings.skirtWidth,
                skirtHeight = settings.skirtHeight,
                skirtSegments = settings.skirtSegments,
                skirtUVScale = settings.skirtUVScale,
                skirtUVOffsetY = settings.skirtUVOffsetY,
                bottomMode = settings.bottomMode,
                bottomBevelInset = settings.bottomBevelInset,
                bottomBevelDepth = settings.bottomBevelDepth,
                bottomBevelSegments = settings.bottomBevelSegments,
                bottomBevelProfile = settings.bottomBevelProfile,
                bottomNoiseScale = settings.bottomNoiseScale,
                bottomNoiseAmplitude = settings.bottomNoiseAmplitude,
                bottomIslandSharpness = settings.bottomIslandSharpness,
                bottomIslandSmooth = settings.bottomIslandSmooth,
                bottomNoiseResolution = settings.bottomNoiseResolution,
                bottomNoiseSeed = settings.bottomNoiseSeed,
            };

            // Flat and Biseau are generated globally from the merged wall outline.
            if (prototypeSettings.bottomMode == BottomMode.Flat)
                prototypeSettings.bottomMode = BottomMode.None;
            else if (prototypeSettings.bottomMode == BottomMode.Bevel)
                prototypeSettings.bottomMode = BottomMode.None;

            return prototypeSettings;
        }

        private static ProceduralTileMeshGenerator.ProceduralMeshSettings GetPrototypeMeshSettings(
            ProceduralTileMeshGenerator.ProceduralMeshSettings activeSettings)
        {
            var prototypeSettings = new ProceduralTileMeshGenerator.ProceduralMeshSettings
            {
                radius = activeSettings.radius,
                depth = activeSettings.depth,
                curveSegments = activeSettings.curveSegments,
                skirtEnabled = activeSettings.skirtEnabled,
                skirtWidth = activeSettings.skirtWidth,
                skirtHeight = activeSettings.skirtHeight,
                skirtSegments = activeSettings.skirtSegments,
                skirtUVScale = activeSettings.skirtUVScale,
                skirtUVOffsetY = activeSettings.skirtUVOffsetY,
                bottomMode = activeSettings.bottomMode,
                bottomBevelInset = activeSettings.bottomBevelInset,
                bottomBevelDepth = activeSettings.bottomBevelDepth,
                bottomBevelSegments = activeSettings.bottomBevelSegments,
                bottomBevelProfile = activeSettings.bottomBevelProfile,
                bottomNoiseScale = activeSettings.bottomNoiseScale,
                bottomNoiseAmplitude = activeSettings.bottomNoiseAmplitude,
                bottomIslandSharpness = activeSettings.bottomIslandSharpness,
                bottomIslandSmooth = activeSettings.bottomIslandSmooth,
                bottomNoiseResolution = activeSettings.bottomNoiseResolution,
                bottomNoiseSeed = activeSettings.bottomNoiseSeed,
            };

            if (prototypeSettings.bottomMode == BottomMode.Flat)
                prototypeSettings.bottomMode = BottomMode.None;
            else if (prototypeSettings.bottomMode == BottomMode.Bevel)
                prototypeSettings.bottomMode = BottomMode.None;

            return prototypeSettings;
        }

        /// <summary>
        /// Quick hash of all bottom-cap parameters so any change triggers mesh regen.
        /// </summary>
        private int ComputeBottomHash()
        {
            if (settings == null) return 0;
            unchecked
            {
                int h = (int)settings.bottomMode * 397;
                h = h * 31 + settings.bottomBevelInset.GetHashCode();
                h = h * 31 + settings.bottomBevelDepth.GetHashCode();
                h = h * 31 + settings.bottomBevelSegments;
                h = h * 31 + (int)settings.bottomBevelProfile * 17;
                h = h * 31 + settings.bottomNoiseScale.GetHashCode();
                h = h * 31 + settings.bottomNoiseAmplitude.GetHashCode();
                h = h * 31 + settings.bottomIslandSharpness.GetHashCode();
                h = h * 31 + settings.bottomIslandSmooth.GetHashCode();
                h = h * 31 + settings.bottomNoiseResolution;
                h = h * 31 + settings.bottomNoiseSeed.GetHashCode();
                return h;
            }
        }

        // ─────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────

        private void OnDisable()
        {
            RestoreDigPreviewVisuals();
            DestroyGeneratedSkirtDisplayResources();
        }

        private void OnDestroy()
        {
            RestoreDigPreviewVisuals();

            // Unsubscribe from deformer to avoid null ref after destroy
            if (_subscribedDeformer != null)
            {
                _subscribedDeformer.OnPostApply -= OnPostDeform;
                _subscribedDeformer = null;
            }

            InvalidateMeshCache();
            ClearCollider();
            DestroyGeneratedSkirtDisplayResources();
            if (_combinedMesh != null)
            {
                if (Application.isPlaying) Destroy(_combinedMesh);
                else DestroyImmediate(_combinedMesh);
            }
            if (_combinedGO != null)
            {
                if (Application.isPlaying) Destroy(_combinedGO);
                else DestroyImmediate(_combinedGO);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Debounce: only schedule one rebuild per frame
            if (_rebuildScheduled) return;
            _rebuildScheduled = true;
            EditorApplication.delayCall += () =>
            {
                _rebuildScheduled = false;
                if (this != null) Rebuild();
            };
        }
#endif
    }
}
