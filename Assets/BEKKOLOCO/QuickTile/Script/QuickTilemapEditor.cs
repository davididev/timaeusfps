// QuickTilemapEditor.cs (BASE) — data-only partial
// NO behavior inside: only fields, properties, and serializable data types.

using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.Serialization;
using System.Collections.Generic;
using System.IO;
using IOPath = System.IO.Path;
using System.Linq;
using Unity.AI.Navigation;
using System.Collections;
using UnityEngine.AddressableAssets;
using System;
using UnityEngine.ResourceManagement.AsyncOperations;
using Bekkoloco.DOTS;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor : MonoBehaviour
    {
        #region Public Fields & Core State

        [SerializeField] public bool isVirtual = true;

        public Tilemap targetTilemap;
        public TileBase activeTile;
        public List<TileBase> recentTiles = new List<TileBase>();

        public Vector3Int gridSize = new Vector3Int(32, 32, 1);
        public Vector3Int cellSize = new Vector3Int(1, 1, 1);

        public bool editorEnabled = true;
        public bool paintOnlyOnTiles = true;
        public int brushSize = 1;
        public bool useCustomSize = false;
        public enum BrushShape { Square, Circle }
        public BrushShape brushShape = BrushShape.Square;
        [SerializeField] public bool useHexGrid = false;

        public enum GridStyle { Mode3D, Mode2_5D }
        [Tooltip("Drawing grid style. 3D = tiles on the XZ plane (horizontal ground). 2.5D = tiles on the XY plane (vertical side-scroller).")]
        public GridStyle gridStyle = GridStyle.Mode3D;

        public void ApplyGridStyleToGrid()
        {
            var grid = GetComponent<UnityEngine.Grid>();
            if (grid == null) grid = FindGrid();
            if (grid == null) return;

            var desired = gridStyle == GridStyle.Mode2_5D
                ? UnityEngine.GridLayout.CellSwizzle.XYZ
                : UnityEngine.GridLayout.CellSwizzle.XZY;

            if (grid.cellSwizzle != desired)
                grid.cellSwizzle = desired;
        }

        public List<GameObject> instantiatedGameObjects = new List<GameObject>();

        public List<string> levelNames = new List<string>();
        public List<LevelData> levels = new List<LevelData>();
        [NonSerialized] public Dictionary<string, List<TexturePaintRuleData>> sessionTexturePaintRulesByLevel =
            new Dictionary<string, List<TexturePaintRuleData>>();

        public List<TexturePaintRule> texturePaintRules = new List<TexturePaintRule>();
        public TexturePaintRule selectedTextureRule;

        public bool needsRefreshPreview = false;

        // GPU paint mask RT + preview material
        public RenderTexture paintMaskTexture;
        [SerializeField] private Material blendPreviewMaterial;
        private Material paintMaskMaterial;

        // Paint-mask math
        private Vector2Int maskOrigin = Vector2Int.zero;

        // Navigation surface (runtime)
        private NavMeshSurface _navSurface;
        [NonSerialized] private Coroutine _navMeshRebuildCoroutine;
#if UNITY_EDITOR
        [NonSerialized] private bool _navMeshRebuildQueuedInEditor;
#endif

        // Misc editor state
        private QuickTilemapEditor tilemapEditor;
        private Vector2 scrollPosition;
        private bool previousCenterOriginToSurfaceMass;

        [NonSerialized] public float runtimeLoadProgress = 0f;

        [HideInInspector] public string loadedJsonContent = "";

        [Range(1, 10)] public int gridScale = 1;

        [Header("Level Options")]
        public bool autoReloadOnLevelChange = false;
        public bool centerOriginToSurfaceMass = true;
        public bool CenterOriginToSurfaceMass => centerOriginToSurfaceMass;

        [HideInInspector] public int selectedGameObjectRuleIndex = -1;
        [HideInInspector] public int selectedTileRuleIndex = -1;
        [HideInInspector] public int selectedTextureRuleIndex = -1;
        [HideInInspector] public GameObject previewObject;

        [HideInInspector] public Dictionary<float, Tilemap> heightTilemaps = new Dictionary<float, Tilemap>();

        [HideInInspector] public int selectedPathIndex = -1;

        public int currentLevelIndex = 0;

        // Shader IDs (kept for parity)
        private static readonly int _qtPaintMaskID = Shader.PropertyToID("_QT_PaintMask");
        private static readonly int _qtWorldToMaskScaleID = Shader.PropertyToID("_QT_WorldToMaskScale");
        private static readonly int _qtWorldToMaskOffsetID = Shader.PropertyToID("_QT_WorldToMaskOffset");

#if UNITY_EDITOR
        [HideInInspector] public bool skirtsNeedRefresh = false;
#endif

        #endregion

        #region Rule Collections (Tiles / Objects / Paths)

        public List<TileRule> tileRules = new List<TileRule>();
        public List<GameObjectRule> gameObjectRules = new List<GameObjectRule>();
        public List<Path> paths = new List<Path>();
        public List<PlacedObject> placedObjects = new List<PlacedObject>();

       

        // Per-cell painted texture index
        public Dictionary<Vector3Int, int> texturePaintMask = new();

        #endregion

        #region Serializable Types

        [System.Serializable]
        public class PaintedTextureData
        {
            public Vector3Int position;   // The position of the tile
            public int textureIndex;      // Index of the texture used
        }

        /// <summary>GPU-friendly instance transform (matches compute shader struct, 24 bytes).</summary>
        [System.Serializable]
        public struct VegetationInstanceData
        {
            public Vector3 position;
            public float rotation;   // Y-axis radians
            public float scale;
            public uint packedGroundColor; // RGBA packed ground color (for Grass mode bottom gradient)

            public static int Size => 4 * 5 + 4; // 24 bytes (5 floats + 1 uint)

            /// <summary>Pack a Color into a uint (RGBA, 8 bits per channel).</summary>
            public static uint PackColor(Color c)
            {
                Color32 c32 = c;
                return (uint)(c32.r | (c32.g << 8) | (c32.b << 16) | (c32.a << 24));
            }
        }

        /// <summary>Rendering mode for a vegetation entry.</summary>
        public enum VegetationMode
        {
            Prefab = 0,   // Use prefab mesh + material
            Card   = 1,   // Procedural cross-quad with texture (CPU instancing)
            Grass  = 2,   // GPU indirect + compute culling — hidden from UI for now, réactiver plus tard
        }

        /// <summary>Shape of procedural grass blade mesh.</summary>
        public enum GrassShape
        {
            Triangle = 0, // Single triangle (pointed tip)
            Blade    = 1, // Thin rectangle (straight blade)
            Fan      = 2, // Trapezoid (wider at top, narrow at base)
        }

        /// <summary>Controls which cells a vegetation entry should populate.</summary>
        public enum VegetationPopulateSource
        {
            Auto = 0,            // Painted cells by default, unpainted ground when used as base texture
            PaintedCells = 1,    // Only cells explicitly painted with this texture rule
            UnpaintedGround = 2, // Ground cells not claimed by any painted texture rule
            WholeGround = 3,     // Every detected ground cell in the level domain
        }

        public enum VegetationPlacementSurface
        {
            Ground = 0,
            Skirt = 1,
        }

        /// <summary>A single vegetation/object entry to spawn on painted areas (GPU instanced).</summary>
        [System.Serializable]
        public class VegetationEntry
        {
            public VegetationMode mode = VegetationMode.Card;
            public VegetationPopulateSource populateSource = VegetationPopulateSource.Auto;
            public VegetationPlacementSurface placementSurface = VegetationPlacementSurface.Ground;
            public GameObject prefab;            // used when mode == Prefab
            public Texture2D cardTexture;        // used when mode == Card
            public Material cardMaterial;        // optional override material for cards (uses VegetationInstanced if null)
            public Color cardTint = Color.white; // tint color for cards
            public float cardWidth  = 1f;        // card quad width (world units)
            public float cardHeight = 1f;        // card quad height (world units)
            public GrassShape grassShape = GrassShape.Triangle; // shape of grass blade (Grass mode only)
            public float density = 1f;           // objects per tile cell
            public float minScale = 0.8f;
            public float maxScale = 1.2f;
            public bool randomRotationY = true;
            public float rotationYDegrees = 0f;
            public float skirtOffset = 0f;
            public float yOffset = 0f;

            /// <summary>Generated instance transforms (populated by editor, rendered by VegetationGPURenderer).</summary>
            public List<VegetationInstanceData> instances = new List<VegetationInstanceData>();
        }

        /// <summary>Serializable snapshot of a VegetationEntry (prefab stored as asset path).</summary>
        [System.Serializable]
        public class VegetationEntryData
        {
            public int mode;  // VegetationMode cast to int
            public int populateSource; // VegetationPopulateSource cast to int
            public int placementSurface; // VegetationPlacementSurface cast to int
            public string prefabPath;
            public string cardTexturePath;
            public string cardMaterialPath;
            public Color cardTint = Color.white;
            public float cardWidth  = 1f;
            public float cardHeight = 1f;
            public float density = 1f;
            public float minScale = 0.8f;
            public float maxScale = 1.2f;
            public bool randomRotationY = true;
            public float rotationYDegrees = 0f;
            public float skirtOffset = 0f;
            public float yOffset = 0f;
            public List<VegetationInstanceData> instances = new List<VegetationInstanceData>();
        }

        /// <summary>Serializable snapshot of a TexturePaintRule (asset refs stored as paths).</summary>
        [System.Serializable]
        public class TexturePaintRuleData
        {
            public string ruleName;
            public int textureIndex;
            public string materialPath;
            public string albedoPath;
            public string normalPath;
            public string heightPath;
            public string emissionPath;
            public Color emissionColor = Color.black;
            public float textureScale = 1f;
            public float blendSharpness = 1f;
            public float noiseScale = 1f;
            public int noiseType = 0;
            public bool removeVegetation = false;
            public List<VegetationEntryData> vegetationEntries = new List<VegetationEntryData>();
        }

        [System.Serializable]
        public class TexturePaintRule
        {
            public string ruleName;
            public int textureIndex;
            public Material material;
            public Texture2D albedo;
            public Texture2D normal;
            public Texture2D height;
            public Texture2D emission;
            public Color emissionColor = Color.black;
            public float textureScale = 1f;
            public float blendSharpness = 1f;
            public float noiseScale = 1f;
            public int noiseType = 0;
            public bool removeVegetation = false;
            public List<VegetationEntry> vegetationEntries = new List<VegetationEntry>();
        }

        [System.Serializable]
        public class InstanceOffset
        {
            public GameObject instanceObject;
            public float yOffset;
        }

        public enum GameObjectPlacementSurface
        {
            Top = 0,
            Skirt = 1
        }

        [System.Serializable]
        public class GameObjectRule
        {
            public string id;                  // Unique GUID for the rule
            public GameObject prefab;          // Runtime reference (Editor)
            public string prefabResourcePath;  // Asset path or Resources key
            public Color color = Color.white;
            public bool randomizeRotationY;
            public bool placeOnGround = true;
            [Tooltip("Where newly painted objects from this rule should be anchored.")]
            public GameObjectPlacementSurface placementSurface = GameObjectPlacementSurface.Top;
            public float yOffset = 0f;
            public bool isVisible = true;
            [Tooltip("When enabled, the placed object will follow the deformed mesh height (Y) at runtime.")]
            public bool followDeformationY = false;

            [Tooltip("Radius around placed objects where vegetation will not spawn. 0 = no exclusion.")]
            public float vegetationExclusionRadius = 1f;

            public List<InstanceOffset> instanceOffsets = new List<InstanceOffset>();

            [SerializeField, HideInInspector, FormerlySerializedAs("yPosition")]
            private float legacyYPosition = 0f;

            public bool MergeLegacyHeightIntoOffset()
            {
                if (Mathf.Approximately(legacyYPosition, 0f))
                    return false;

                yOffset += legacyYPosition;
                legacyYPosition = 0f;
                return true;
            }
        }

        public enum PropertyType { String, Int, Float, Bool }

        [System.Serializable]
        public class LevelProperty
        {
            public string key;
            public PropertyType type = PropertyType.String;
            public string value;
            public Vector3Int position;
            public int textureIndex;
        }

        // Helper to get typed value (kept as data utility)
        public object GetTypedValue(LevelProperty prop)
        {
            switch (prop.type)
            {
                case PropertyType.Int:
                    if (int.TryParse(prop.value, out int intVal))
                        return intVal;
                    break;
                case PropertyType.Float:
                    if (float.TryParse(prop.value, out float floatVal))
                        return floatVal;
                    break;
                case PropertyType.Bool:
                    if (bool.TryParse(prop.value, out bool boolVal))
                        return boolVal;
                    break;
            }
            return prop.value;
        }

        [System.Serializable]
        public class LevelData
        {
            public string levelName;
            public TextAsset jsonFile;
            public List<LevelProperty> properties = new List<LevelProperty>();
            public List<PaintedTextureData> paintedTextures = new List<PaintedTextureData>();
            public bool centerOriginToSurfaceMass = true;
        }

        [System.Serializable]
        public class TrackPoint
        {
            public Vector3Int gridPosition;
            [Tooltip("Snap this point's Y to the ground mesh below it.")]
            public bool snapToGround = true;
            [Tooltip("Rotation angle (degrees) applied to the track mesh cross-section at this point.")]
            public float rotation = 0f;
            [Tooltip("Width of the track mesh at this point.")]
            public float width = 1f;
        }

        /// <summary>Defines what a path generates when it has 2+ points.</summary>
        public enum PathType
        {
            Move,    // Movement path only (no mesh, just PathFollower)
            Slope,   // Smooth ramp connecting two heights
            Stairs,  // Stepped stairs connecting two heights
            Bridge,  // Flat bridge between two points (with supports)
            Track    // Custom track mesh (existing system)
        }

        public enum BridgeProfile
        {
            Curved,
            Stepped
        }

        [System.Serializable]
        public class Path
        {
            public List<Vector3Int> points = new List<Vector3Int>();
            public Color color = Color.yellow;
            public bool isVisible = true;

            [Tooltip("What this path generates: Move (no mesh), Slope, Stairs, Bridge, or Track.")]
            public PathType pathType = PathType.Move;

            // ── Slope / Stairs settings ──
            [Tooltip("Number of steps when pathType = Stairs.")]
            [Range(2, 16)] public int stairSteps = 4;
            [Tooltip("Automatically derive the number of stairs from the path length.")]
            public bool stairAutoSteps = true;
            [Tooltip("Target tread length used when Auto Steps is enabled.")]
            [Range(0.1f, 4f)] public float stairStepDepth = 1f;
            [Tooltip("Width of the slope/stairs mesh.")]
            [Range(0.25f, 4f)] public float slopeWidth = 1f;
            [Tooltip("Add smooth S-curve transition at slope ends.")]
            public bool smoothTransition = true;
            [Tooltip("Add a tilemap-style grass skirt on the left/right sides of the slope.")]
            public bool slopeSideSkirtEnabled = true;
            [Tooltip("How far the side skirt extends outward from the slope edge.")]
            [Range(0f, 0.3f)] public float slopeSideSkirtWidth = 0.155f;
            [Tooltip("How far the side skirt drops downward from the slope edge.")]
            [Range(0f, 0.5f)] public float slopeSideSkirtHeight = 0.485f;
            [Tooltip("Number of curved segments used for the side skirt.")]
            [Range(1, 8)] public int slopeSideSkirtSegments = 2;
            [Tooltip("UV tiling scale applied on the side skirt.")]
            [Range(0.1f, 10f)] public float slopeSideSkirtUVScale = 1f;
            [Tooltip("Vertical UV offset applied on the side skirt.")]
            [Range(-1f, 1f)] public float slopeSideSkirtUVOffsetY = 0.389f;
            [Tooltip("Material for the slope/stairs surface.")]
            public Material slopeSurfaceMaterial;
            [Tooltip("Material for the slope/stairs walls/sides.")]
            public Material slopeWallMaterial;
            [Tooltip("Material used by the stairs rail meshes.")]
            public Material stairRailMaterial;

            // ── Bridge settings ──
            [Tooltip("Height of bridge supports.")]
            [Range(0f, 10f)] public float bridgeHeight = 0.1f;
            [Tooltip("Width of the bridge.")]
            [Range(0.5f, 4f)] public float bridgeWidth = 1f;
            [Tooltip("Overall bridge profile.")]
            public BridgeProfile bridgeProfile = BridgeProfile.Curved;
            [Tooltip("Additional vertical offset at the middle of the bridge. Positive arches upward, negative sags downward.")]
            [Range(-5f, 5f)] public float bridgeCurve = 0.32f;
            [Tooltip("Number of steps when bridge profile is Stepped.")]
            [Range(2, 16)] public int bridgeSteps = 6;
            [Tooltip("Add railings to the bridge.")]
            public bool bridgeRailings = true;
            [Tooltip("Thickness of the bridge rails.")]
            [Range(0.02f, 0.5f)] public float bridgeRailThickness = 0.316f;
            [Tooltip("Push the rails outward or pull them inward.")]
            [Range(-1f, 1f)] public float bridgeRailSpread = 0f;
            [Tooltip("How much the bridge rails stretch downward near each end.")]
            [Range(0f, 2f)] public float bridgeRailEndExtension = 0f;
            [Tooltip("Vertical position offset applied to the bridge rails.")]
            [Range(-1f, 1f)] public float bridgeRailYOffset = 0f;
            [Tooltip("Vertical UV offset applied to the bridge rails.")]
            [Range(-1f, 1f)] public float bridgeRailUvOffsetY = -0.858f;
            [Tooltip("How much the rail side texture follows the bridge curvature. 0 keeps joints vertical, 1 bends them with the arch.")]
            [Range(0f, 2f)] public float bridgeRailCurveFollow = 0f;
            [Tooltip("Material for the bridge surface.")]
            public Material bridgeMaterial;

            // ── Track Mesh settings ──
            [Tooltip("Enable 3D track mesh generation along this path.")]
            public bool enableTrackMesh = false;
            [Tooltip("Per-point track data (snap, rotation, width). Auto-synced with points list.")]
            public List<TrackPoint> trackPoints = new List<TrackPoint>();
            [Tooltip("Default width of the generated track mesh.")]
            public float trackWidth = 1f;
            [Tooltip("Subdivisions between each pair of path points (smoothness).")]
            [Range(1, 20)] public int trackSubdivisions = 4;
            [Tooltip("Material applied to the generated track mesh.")]
            public Material trackMaterial;
            [Tooltip("UV tiling along the track length.")]
            public float trackUVTilingY = 1f;

            // ── Editor UI state ──
            [System.NonSerialized]
            public bool trackPointsFoldout = false;
        }

        [System.Serializable]
        public class PlacedObject
        {
            [Header("Identity")]
            [SerializeField] private string _uniqueId;

            public const int CurrentInstanceYOffsetVersion = 1;
            [SerializeField, HideInInspector] private int _instanceYOffsetVersion = CurrentInstanceYOffsetVersion;

            public Vector3Int position;
            public string ruleId;              // Unique GUID of the rule (replaces ruleIndex for persistence)
            public int ruleIndex = -1;
            public Color color;
            public int pathIndex = -1;
            public float rotation;
            public string parentTilemapName;
            public float instanceYOffset;
            public string prefabResourcePath;
            public GameObjectPlacementSurface placementSurface = GameObjectPlacementSurface.Top;
            public Vector3Int skirtAnchorCell;

            [Header("State")]
            public bool isValid = true;

            public string UniqueId
            {
                get
                {
                    if (string.IsNullOrEmpty(_uniqueId))
                        _uniqueId = System.Guid.NewGuid().ToString();
                    return _uniqueId;
                }
            }

            public bool NeedsInstanceYOffsetUpgrade => _instanceYOffsetVersion < CurrentInstanceYOffsetVersion;

            public void MarkInstanceYOffsetUpgraded()
            {
                _instanceYOffsetVersion = CurrentInstanceYOffsetVersion;
            }

            public void MarkInstanceYOffsetLegacy()
            {
                _instanceYOffsetVersion = 0;
            }
        }

        [System.Serializable]
        public class DeformerHandleData
        {
            public Vector3 localPosition;
            public Vector3 localEuler;
            public Vector3 localScale = Vector3.one;
        }

        /// <summary>Mesh source: Custom (prefab/FBX) or Procedural (generated at runtime).</summary>
        public enum MeshMode { Custom, Procedural }

        [System.Serializable]
        public class TileRule
        {
            public string ruleName;
            public TileBase tile;
            public Color color = Color.white;
            public float yOffset = 0f;
            public float sizeY = 1f;
            public bool useCustomTilemap;
            [Tooltip("When enabled on a dedicated tilemap layer, this layer subtracts volume from overlapping procedural layers instead of rendering solid ground.")]
            public bool isDigLayer = false;
            [Tooltip("When enabled, this procedural layer receives carving from overlapping Dig Layers.")]
            public bool isDiggable = true;
            [Tooltip("When enabled, this procedural layer ignores all Dig Layers and keeps its full mesh.")]
            public bool isUndiggable = false;
            public Tilemap customTargetTilemap;
            public int renderOrder = 0;
            public bool fixBase = false;
            [Range(0f, 1f)] public float roundedCorner = 0f;
            public string customTargetTilemapName;
            public bool isVisible = true;
            public List<GameObject> deformerObjects = new List<GameObject>();
            public List<DeformerHandleData> savedDeformerHandles = new List<DeformerHandleData>();
            public bool enableMove = false;
            [FormerlySerializedAs("moveTo")]
            [Tooltip("Relative offset added on top of the tile's base position when the move animation plays.")]
            public Vector3 moveOffset = Vector3.zero;
            public float movePause = 0f;

            public BottomSettings bottom = new BottomSettings();

            // ── Mesh Mode: Custom (existing prefab) or Procedural (generated) ──
            [Header("Mesh Mode")]
            [Tooltip("Custom = use existing tile/prefab meshes. Procedural = generate meshes from parameters (same system as QuickTexture HTML).")]
            public MeshMode meshMode = MeshMode.Procedural;

            [Tooltip("Procedural mesh settings (radius, depth, biseau). Only used when meshMode = Procedural.")]
            public ProceduralTileMeshGenerator.ProceduralMeshSettings proceduralSettings
                = new ProceduralTileMeshGenerator.ProceduralMeshSettings();

            [Header("Procedural Materials (per surface)")]
            [Tooltip("Material for top/bottom caps (floor & ceiling). Submesh 0.")]
            public Material proceduralFloorMaterial;
            [Tooltip("Material for side walls. Submesh 1.")]
            public Material proceduralWallMaterial;
            [Tooltip("(Optional) Override material for bottom cap only. If null, uses floor material.")]
            public Material proceduralCeilingMaterial;
            [Tooltip("(Optional) Material for the procedural bottom cap. If null, uses wall material or floor material.")]
            public Material proceduralBottomMaterial;
            [Tooltip("Material used by Dig Layers for their preview mesh.")]
            public Material proceduralDigMaterial;
        }

        [System.Serializable]
        private class TileRuleData
        {
            public bool fixBase;
            public string ruleName;
            public string tileName;
            public Color color = Color.white;
            public bool isVisible = true;
            public float roundedCorner = 0f;
            public float yOffset = 0f;
            public float sizeY = 1f;
            public bool useCustomTilemap;
            public bool isDigLayer = false;
            public bool isDiggable = true;
            public bool isUndiggable = false;
            public string customTargetTilemapName;
            public int renderOrder = 0;
            public List<DeformerHandleData> savedDeformerHandles = new List<DeformerHandleData>();
            public bool enableMove = false;
            [FormerlySerializedAs("moveTo")]
            public Vector3 moveOffset = Vector3.zero;
            public float movePause = 0f;

            // ── Legacy bottom/skirt settings persistence ──
            public bool bottomEnabled = true;
            public int bottomShape = 1; // BottomShape.Rounded
            public int bottomSize = 8;
            public AnimationCurve bottomProfile = AnimationCurve.EaseInOut(0, 1, 1, 0);
            public string bottomMaterialPath;

            // ── Procedural mesh mode persistence ──
            public MeshMode meshMode = MeshMode.Procedural;
            public float proceduralRadius = 0.3f;
            public float proceduralDepth = 0.4f;
            public int proceduralCurveSegments = 8;
            public bool proceduralSkirtEnabled = true;
            public float proceduralSkirtWidth = 0.155f;
            public float proceduralSkirtHeight = 0.485f;
            public int proceduralSkirtSegments = 2;
            public float proceduralSkirtUVScale = 1f;
            public float proceduralSkirtUVOffsetY = 0.389f;
            public int proceduralSkirtMaterialMode = 0;
            public AnimationCurve proceduralSkirtMaskCurve = ProceduralTileMeshGenerator.CreateDefaultSkirtMaskCurve();
            public string proceduralFloorMaterialPath;
            public string proceduralWallMaterialPath;
            public string proceduralCeilingMaterialPath;
            public string proceduralBottomMaterialPath;
            public string proceduralDigMaterialPath;
            // ── Deformer settings persistence ──
            public int deformerShape = 0;           // DOTSDeformShape cast to int
            public float deformerRadius = 5f;
            public int deformerFalloff = 2;         // DOTSFalloff cast to int (default SmoothStep)
            public float deformerGaussianSharpness = 1.5f;
            public float deformerHeightPerUnitY = 1f;
            public float deformerYDeformRatio = 1f;
            public bool deformerInvertDirection = false;
            public bool deformerLinkRadiusToScale = false;
            public int deformerRadiusLinkMode = 0;
            public bool deformerUseYMin = false;
            public float deformerYMin = 0f;
            public bool deformerYMinRelativeToHandle = false;
            public float deformerYFeather = 0f;
            public bool deformerClampWorldMinY = false;
            public float deformerWorldMinY = 0f;
            public int deformerYMinFalloff = 2;        // DOTSFalloff cast to int
            public float deformerYMinGaussianSharpness = 1.5f;

            // ── Bottom cap settings persistence ──
            public int proceduralBottomMode = 0;            // BottomMode cast to int
            public float proceduralBottomBevelInset = 0.1f;
            public float proceduralBottomBevelDepth = 0.08f;
            public int proceduralBottomBevelSegments = 4;
            public int proceduralBottomBevelProfile = 0;  // BevelProfile cast to int
            public float proceduralBottomNoiseScale = 2f;
            public float proceduralBottomNoiseAmplitude = 0.3f;
            public float proceduralBottomIslandSharpness = 2f;
            public float proceduralBottomIslandSmooth = 0.3f;
            public int proceduralBottomNoiseResolution = 8;
            public float proceduralBottomNoiseSeed = 0f;
        }

        [System.Serializable]
        public class TilemapData
        {
            public string tilemapName;
            public float height;
            public List<TileInfo> tiles = new List<TileInfo>();
        }

        [System.Serializable]
        public class TileInfo
        {
            public int x, y, z;
            public string tileName;
            public Color color = Color.white;
            public int ruleIndex = -1; // selection system
            public TileBase tile;      // selection system

            public TileInfo(int x, int y, int z, string tileName)
            { this.x = x; this.y = y; this.z = z; this.tileName = tileName; }

            public TileInfo(int x, int y, int z, TileBase tile, Color color, int ruleIndex)
            {
                this.x = x; this.y = y; this.z = z;
                this.tile = tile;
                this.tileName = tile != null ? tile.name : "";
                this.color = color;
                this.ruleIndex = ruleIndex;
            }

            public Vector3Int Position => new Vector3Int(x, y, z);
        }

        [System.Serializable]
        public class PathData
        {
            public List<Vector3Int> points = new List<Vector3Int>();
            public Color color = Color.yellow;
            public bool isVisible = true;
            public PathType pathType = PathType.Move;
            // Slope/Stairs serialization
            public int stairSteps = 4;
            public bool stairAutoSteps = true;
            public float stairStepDepth = 1f;
            public float slopeWidth = 1f;
            public bool smoothTransition = true;
            public bool slopeSideSkirtEnabled = true;
            public float slopeSideSkirtWidth = 0.155f;
            public float slopeSideSkirtHeight = 0.485f;
            public int slopeSideSkirtSegments = 2;
            public float slopeSideSkirtUVScale = 1f;
            public float slopeSideSkirtUVOffsetY = 0.389f;
            public string slopeSurfaceMaterialPath;
            public string slopeWallMaterialPath;
            public string stairRailMaterialPath;
            // Bridge serialization
            public float bridgeHeight = 0.1f;
            public float bridgeWidth = 1f;
            public BridgeProfile bridgeProfile = BridgeProfile.Curved;
            public float bridgeCurve = 0.32f;
            public int bridgeSteps = 6;
            public bool bridgeRailings = true;
            public float bridgeRailThickness = 0.316f;
            public float bridgeRailSpread = 0f;
            public float bridgeRailEndExtension = 0f;
            public float bridgeRailYOffset = 0f;
            public float bridgeRailUvOffsetY = -0.858f;
            public float bridgeRailCurveFollow = 0f;
            public string bridgeMaterialPath;
            // Track mesh serialization
            public bool enableTrackMesh = false;
            public List<TrackPoint> trackPoints = new List<TrackPoint>();
            public float trackWidth = 1f;
            public int trackSubdivisions = 4;
            public float trackUVTilingY = 1f;
            public string trackMaterialPath;
        }

        [System.Serializable]
        public class GameObjectData
        {
            public Vector3Int position;
            public int ruleIndex;
            public Color color;
            public int pathIndex;
            public float rotation;
            public string parentTilemapName;
            public float instanceYOffset;
            public string prefabPath;
            public int instanceYOffsetVersion = PlacedObject.CurrentInstanceYOffsetVersion;
            public GameObjectPlacementSurface placementSurface = GameObjectPlacementSurface.Top;
            public Vector3Int skirtAnchorCell;
        }


        /// <summary>
        /// Ensures a RadialHillDeformer component exists on each rule's tilemap
        /// that has saved deformer handles. Must be called BEFORE RestoreDeformerHandlesAfterLoad().
        /// Without this, freshly-created tilemaps (from JSON load) have no deformer to bind to.
        /// </summary>
        public void EnsureDeformerComponentsForLoad()
        {
            if (tileRules == null) return;

            foreach (var rule in tileRules)
            {
                if (rule == null) continue;
                if (rule.savedDeformerHandles == null || rule.savedDeformerHandles.Count == 0) continue;

                // Find the tilemap root for this rule
                Transform root = null;
                if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                    root = rule.customTargetTilemap.transform;
                else if (targetTilemap != null)
                    root = targetTilemap.transform;

                if (root == null) continue;

                // Check if there's already a RadialHillDeformer
                var existing = root.GetComponentInChildren<Bekkoloco.DOTS.RadialHillDeformer>(true);
                if (existing != null) continue;

                // Create one with sensible defaults (will be overwritten by ApplyDeformerSettingsAfterLoad)
                var deformer = root.gameObject.AddComponent<Bekkoloco.DOTS.RadialHillDeformer>();
                deformer.runtimeStaticMode = true;
                deformer.runtimeInitDelay = 0.1f;
                deformer.linkRadiusToScale = true;
                deformer.radiusLinkMode = DOTSRadiusLinkMode.MultiplyByScale;
                deformer.radius = 5f;
                deformer.falloff = DOTSFalloff.SmoothStep;
                deformer.heightPerUnitY = 0.7f;
                deformer.useHandleZero = true;
                deformer.useYMin = true;
                deformer.yMin = -0.3f;
                deformer.yFeather = -0.16f;
                deformer.compensateLocalScaleY = true;
                deformer.yDeformRatio = 1.11f;
                deformer.clampWorldMinY = true;
                deformer.worldMinY = rule.yOffset - rule.sizeY;
                deformer.recalcNormals = true;
                deformer.updateMeshCollider = true;

#if UNITY_EDITOR
                EditorUtility.SetDirty(deformer);
#endif
            }
        }

        public void CaptureDeformerHandlesForSave()
        {
            if (tileRules == null) return;

            foreach (var rule in tileRules)
            {
                if (rule == null) continue;
                if (rule.savedDeformerHandles == null) rule.savedDeformerHandles = new List<DeformerHandleData>();
                rule.savedDeformerHandles.Clear();

                if (rule.deformerObjects == null) continue;

                foreach (var go in rule.deformerObjects)
                {
                    if (go == null) continue;
                    rule.savedDeformerHandles.Add(new DeformerHandleData
                    {
                        localPosition = go.transform.localPosition,
                        localEuler = go.transform.localEulerAngles,
                        localScale = go.transform.localScale
                    });
                }
            }
        }

        public void RestoreDeformerHandlesAfterLoad()
        {
            if (tileRules == null) return;

            foreach (var rule in tileRules)
            {
                if (rule == null || rule.savedDeformerHandles == null) continue;

                // Clean up old scene cubes if any
                if (rule.deformerObjects != null)
                {
                    for (int i = rule.deformerObjects.Count - 1; i >= 0; i--)
                    {
                        var go = rule.deformerObjects[i];
                        if (go)
                        {
#if UNITY_EDITOR
                            DestroyImmediate(go);
#else
                    Destroy(go);
#endif
                        }
                    }
                    rule.deformerObjects.Clear();
                }
                else rule.deformerObjects = new List<GameObject>();

                // Parent and recreate…
                Transform parent =
                    (rule.useCustomTilemap && rule.customTargetTilemap != null)
                        ? rule.customTargetTilemap.transform
                        : (targetTilemap != null ? targetTilemap.transform : transform);

                foreach (var data in rule.savedDeformerHandles)
                {
                    GameObject handle = null;

                    // ✅ DISTINCTION CLAIRE : Éditeur vs Runtime
                    if (Application.isPlaying)
                    {
                        // 🎮 EN JEU : Objet vide invisible
                        handle = new GameObject("EmptyDeformerHandle");
                    }
                    else
                    {
                        // 🛠️ EN ÉDITEUR : Utiliser UI_Arrow ou cube fallback
#if UNITY_EDITOR
                        GameObject arrowPrefab = Resources.Load<GameObject>("UI_Arrow");
                        if (arrowPrefab != null)
                        {
                            handle = PrefabUtility.InstantiatePrefab(arrowPrefab) as GameObject;
                            handle.name = "ArrowHandle";
                        }
                        else
                        {
                            handle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            handle.name = "CubeHandle";
                            Debug.LogWarning("UI_Arrow prefab not found. Using cube fallback.");
                        }
#else
        // Fallback pour build (ne devrait pas arriver)
        handle = new GameObject("EmptyDeformerHandle");
#endif
                    }

                    handle.transform.SetParent(parent, false);
                    handle.transform.localPosition = data.localPosition;
                    handle.transform.localEulerAngles = data.localEuler;
                    handle.transform.localScale = data.localScale;

                    // Nettoyer les colliders
                    var col = handle.GetComponent<Collider>();
#if UNITY_EDITOR
                    if (col) DestroyImmediate(col);
#else
                    if (col) Destroy(col);
#endif

                    SetDeformerHandleVisualState(handle, !Application.isPlaying);

                    rule.deformerObjects.Add(handle);
                    LinkHandleToRadialHillDeformers_Runtime(rule, handle.transform);
                }
            }
        }

        private static void SetDeformerHandleVisualState(GameObject handle, bool visible)
        {
            if (handle == null) return;

            foreach (var renderer in handle.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null)
                    renderer.enabled = visible;
            }
        }


        /// <summary>
        /// Runtime-safe version (lives on QuickTilemapEditor) so we can call it from load.
        /// RadialHillDeformer.additionalHandles is List&lt;Transform&gt;.
        /// </summary>
        private void LinkHandleToRadialHillDeformers_Runtime(TileRule rule, Transform handle)
        {
            Transform root =
                (rule.useCustomTilemap && rule.customTargetTilemap != null)
                    ? rule.customTargetTilemap.transform
                    : (targetTilemap != null ? targetTilemap.transform : transform);

            if (root == null || handle == null) return;

            var deformers = root.GetComponentsInChildren<Bekkoloco.DOTS.RadialHillDeformer>(true);
            foreach (var d in deformers)
            {
                /*
                // Use the List property instead of the array
                if (d.additionalHandlesList == null)
                    d.additionalHandlesList = new List<Transform>();

                if (!d.additionalHandlesList.Contains(handle))
                    d.additionalHandlesList.Add(handle);
                */
                d.AddHandleUnique(handle); // évite les doublons et reste clair

            }
        }

        private void RefreshAllRadialHillDeformerBindings()
        {
            if (tileRules == null) return;

            foreach (var rule in tileRules)
            {
                if (rule?.deformerObjects == null) continue;

                // 🎯 NOUVEAU : Synchroniser worldMinY pour tous les RadialHillDeformers de cette rule
                if (rule.customTargetTilemap != null)
                {
                    var deformers = rule.customTargetTilemap.GetComponentsInChildren<RadialHillDeformer>(true);
                    foreach (var deformer in deformers)
                    {
                        // Applique la formule : worldMinY = yOffset - sizeY
                        float newWorldMinY = rule.yOffset - rule.sizeY;
                        deformer.worldMinY = newWorldMinY;
                        deformer.clampWorldMinY = true;

#if UNITY_EDITOR
                        if (!Application.isPlaying)
                            EditorUtility.SetDirty(deformer);
#endif
                    }
                }

                // Code existant pour les handles
                foreach (var handleObject in rule.deformerObjects)
                {
                    if (handleObject == null) continue;
                    LinkHandleToRadialHillDeformers_Runtime(rule, handleObject.transform);
                }
            }
        }



        /// <summary>
        /// Rebuild procedural meshes for all tile rules that use Procedural mode.
        /// Called after level load to restore procedural geometry.
        /// </summary>
        public void RebuildAllProceduralMeshes()
        {
            if (tileRules == null) return;

            foreach (var rule in tileRules)
            {
                if (rule == null) continue;
                if (rule.meshMode != MeshMode.Procedural) continue;
                SyncProceduralRendererForRule(rule);
            }
        }

        #endregion

        #region Private Save Container (kept here for now)

        [System.Serializable]
        private class SaveData
        {
            public List<TilemapData> tilemaps = new List<TilemapData>();
            public List<GameObjectData> placedObjects = new List<GameObjectData>();
            public List<TileRuleData> tileRules = new List<TileRuleData>();
            public List<PathData> paths = new List<PathData>();
            public List<PaintedTextureData> paintedTextures = new List<PaintedTextureData>();
            public List<TexturePaintRuleData> texturePaintRulesList = new List<TexturePaintRuleData>();
            public List<QuickTilemapEditor.LevelProperty> levelProperties = new List<QuickTilemapEditor.LevelProperty>();
            public List<QuickTilemapEditor.GameObjectRule> gameObjectRules = new List<QuickTilemapEditor.GameObjectRule>();

            [Tooltip("If true, saving can recenter the whole level content in grid space.")]
            public bool centerOriginToSurfaceMass = true;
            public bool positionsCenteredInCells = false;
        }

        #endregion

        #region Private Vars (non-behavior)

        private Tilemap virtualTilemap;

        #endregion
    }
}
