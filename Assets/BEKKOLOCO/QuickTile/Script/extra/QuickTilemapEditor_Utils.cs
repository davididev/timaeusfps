using UnityEngine;
using UnityEngine.Tilemaps;
using UnityEngine.AI; // ← pour NavMeshAgent

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
        // ─────────────────────────────────────
        // Grid lookup helpers (layoutGrid can be null if the Tilemap
        // is not parented under a Grid component)
        // ─────────────────────────────────────

        /// <summary>
        /// Finds the Grid component by checking multiple sources:
        /// 1. targetTilemap.layoutGrid
        /// 2. QuickTilemapEditor's own parent hierarchy
        /// 3. GameObject named "Grid" in the scene
        /// </summary>
        public Grid FindGrid()
        {
            if (targetTilemap != null)
            {
                var lg = targetTilemap.layoutGrid;
                if (lg != null && lg is Grid g) return g;

                var pg = targetTilemap.GetComponentInParent<Grid>();
                if (pg != null) return pg;
            }

            var selfGrid = GetComponentInParent<Grid>();
            if (selfGrid != null) return selfGrid;

            var gridGO = GameObject.Find("Grid");
            if (gridGO != null)
            {
                var fg = gridGO.GetComponent<Grid>();
                if (fg != null) return fg;
            }

            return null;
        }

        /// <summary>
        /// Like Tilemap.GetCellCenterWorld but works even when layoutGrid is null.
        /// </summary>
        public Vector3 SafeGetCellCenterWorld(Vector3Int cellPos)
        {
            if (targetTilemap != null && targetTilemap.layoutGrid != null)
                return targetTilemap.GetCellCenterWorld(cellPos);

            var grid = FindGrid();
            if (grid != null)
                return grid.GetCellCenterWorld(cellPos);

            // Ultimate fallback: manual XZ plane, cell size 1
            return new Vector3(cellPos.x + 0.5f, 0f, cellPos.y + 0.5f);
        }

        /// <summary>
        /// Like Tilemap.CellToWorld but works even when layoutGrid is null.
        /// </summary>
        public Vector3 SafeCellToWorld(Vector3Int cellPos)
        {
            if (targetTilemap != null && targetTilemap.layoutGrid != null)
                return targetTilemap.CellToWorld(cellPos);

            var grid = FindGrid();
            if (grid != null)
                return grid.CellToWorld(cellPos);

            // Ultimate fallback: manual XZ plane, cell size 1
            return new Vector3(cellPos.x, 0f, cellPos.y);
        }

        /// <summary>
        /// Like Tilemap.WorldToCell but works even when layoutGrid is null.
        /// Can also accept an explicit Tilemap to convert from (e.g. a parent tilemap).
        /// </summary>
        public Vector3Int SafeWorldToCell(Vector3 worldPos, Tilemap tm = null)
        {
            if (IsDualGrid())
            {
                // Dual-grid: inverse of GetPlacementWorldPos
                // World XZ → cell XY via Grid's inverse transform
                var grid = FindGrid();
                Vector3 local = grid != null
                    ? grid.transform.InverseTransformPoint(worldPos)
                    : worldPos;
                return new Vector3Int(
                    Mathf.RoundToInt(local.x),
                    Mathf.RoundToInt(local.z),
                    0);
            }

            // Try the provided tilemap first
            if (tm != null && tm.layoutGrid != null)
                return tm.WorldToCell(worldPos);

            // Try targetTilemap
            if (targetTilemap != null && targetTilemap.layoutGrid != null)
                return targetTilemap.WorldToCell(worldPos);

            // Use Grid directly
            var grid2 = FindGrid();
            if (grid2 != null)
                return grid2.WorldToCell(worldPos);

            // Fallback: manual floor
            return new Vector3Int(Mathf.FloorToInt(worldPos.x), Mathf.FloorToInt(worldPos.z), 0);
        }

        /// <summary>
        /// Returns true if ANY tilemap in the project uses a
        /// ProceduralTileRenderer (dual-grid mesh mode).
        /// Checks targetTilemap AND all custom/rule tilemaps.
        /// </summary>
        public bool IsDualGrid()
        {
            if (targetTilemap != null && targetTilemap.GetComponentInChildren<ProceduralTileRenderer>(true) != null)
                return true;
            foreach (var ctm in GetAllCustomTilemaps())
                if (ctm != null && ctm.GetComponentInChildren<ProceduralTileRenderer>(true) != null)
                    return true;
            return false;
        }

        /// <summary>
        /// Returns the world position where a GameObject should be placed
        /// for the given cell.
        /// In dual-grid mode: the mesh center for cell (cx,cy) is at
        /// local (cx, 0, cy) in the Grid's space.  We use TransformPoint
        /// to convert to world, bypassing Grid.CellToWorld (which may use
        /// XY swizzle instead of XZ).
        /// In standard mode: uses GetCellCenterWorld as before.
        /// </summary>
        public Vector3 GetPlacementWorldPos(Vector3Int cellPos)
        {
            if (IsDualGrid())
            {
                // Dual-grid: mesh center = (cellX, 0, cellY) in Grid local space
                Vector3 localXZ = new Vector3(cellPos.x, 0f, cellPos.y);
                var grid = FindGrid();
                if (grid != null)
                    return grid.transform.TransformPoint(localXZ);
                return localXZ; // fallback: Grid at origin
            }
            return SafeGetCellCenterWorld(cellPos);
        }

        /// <summary>
        /// Returns the dual-grid intersection position for path meshes that should live
        /// on the dual-grid lattice rather than on source-cell centers.
        /// In dual-grid, procedural tiles are centered on half-offset coordinates
        /// like (x - 0.5, z - 0.5), so we mirror that here for slopes.
        /// Falls back to the regular placement position outside dual-grid mode.
        /// </summary>
        public Vector3 GetDualGridIntersectionWorldPos(Vector3Int cellPos)
        {
            if (!IsDualGrid())
                return GetPlacementWorldPos(cellPos);

            Vector3 localXZ = new Vector3(cellPos.x - 0.5f, 0f, cellPos.y - 0.5f);
            var grid = FindGrid();
            if (grid != null)
                return grid.transform.TransformPoint(localXZ);

            return localXZ;
        }

        /// <summary>
        /// World position used by runtime/path meshes.
        /// Slopes and stairs can live on dual-grid intersections so their mesh,
        /// followers, and inspector overlay all agree on the same anchor.
        /// Other path types keep the regular placement position.
        /// </summary>
        public Vector3 GetPathWorldPos(Path path, Vector3Int cellPos)
        {
            bool usesDualGridIntersection = path != null &&
                                            (path.pathType == PathType.Slope ||
                                             path.pathType == PathType.Stairs);

            if (usesDualGridIntersection && IsDualGrid())
                return GetDualGridIntersectionWorldPos(cellPos);

            return GetPlacementWorldPos(cellPos);
        }

        private static void SafeDestroy(Object obj)
        {
#if UNITY_EDITOR
            Object.DestroyImmediate(obj);
#else
            Object.Destroy(obj);
#endif
        }

        // Désactive/assainit les composants "vivants" sur l'INSTANCE DE PREVIEW uniquement
        private static void SanitizePreviewComponents(GameObject go)
        {
            if (!go) return;

            // 1) CharacterController : clamp + disable (évite "Step Offset must be positive")
            var ccs = go.GetComponentsInChildren<CharacterController>(true);
            foreach (var cc in ccs)
            {
                if (!cc) continue;

                // Si le stepOffset est <= 0, mets une valeur sûre (Unity par défaut ≈ 0.3)
                if (cc.stepOffset <= 0f) cc.stepOffset = 0.3f;

                // Assure-toi aussi qu'il n'est pas absurde par rapport à la hauteur
                if (cc.height > 0.1f && cc.stepOffset >= cc.height)
                    cc.stepOffset = Mathf.Max(0.05f, cc.height * 0.5f);

                cc.enabled = false; // on désactive en preview
            }

            // 2) NavMeshAgent : off en preview
            var agents = go.GetComponentsInChildren<NavMeshAgent>(true);
            foreach (var agent in agents) if (agent) agent.enabled = false;

            // 3) Physics : neutraliser
            var rbs = go.GetComponentsInChildren<Rigidbody>(true);
            foreach (var rb in rbs)
            {
                if (!rb) continue;
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            var cols = go.GetComponentsInChildren<Collider>(true);
            foreach (var col in cols) if (col) col.enabled = false;

            // 4) Animator : off pour éviter des OnEnable/Update d’anim en editor
            var anims = go.GetComponentsInChildren<Animator>(true);
            foreach (var anim in anims) if (anim) anim.enabled = false;
        }

        public Transform GetPrefabContainer()
        {
            Transform prefabContainer = transform.Find("PrefabContainer");
            if (prefabContainer == null)
            {
                // (réservé / futur)
            }
            return prefabContainer;
        }

        public void ShowPreviewObject(GameObject prefab, Vector3 worldPos, Quaternion rotation, Color color)
        {
            if (prefab == null) return;

#if UNITY_EDITOR
            if (previewObject != null && previewObject.name != prefab.name + "(Clone)")
            {
                ClearPreviewObject();
            }

            if (previewObject == null)
            {
                // Temporarily suppress "Step Offset must be positive" warning during instantiation
                // The CharacterController triggers this during Awake() before we can fix it
                void SuppressStepOffsetWarning(string logString, string stackTrace, LogType type)
                {
                    // This callback just exists to mark that we're in a special mode
                    // Unity still logs it, but we can filter in console
                }
                
                Application.logMessageReceived += SuppressStepOffsetWarning;
                
                try
                {
                    Object instantiated = null;

                    try
                    {
                        instantiated = PrefabUtility.InstantiatePrefab(prefab);
                    }
                    catch
                    {
                        // Fall back to plain instantiation for assets Unity refuses to instantiate via PrefabUtility.
                    }

                    if (instantiated == null)
                    {
                        try
                        {
                            instantiated = Object.Instantiate((Object)prefab);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[QuickTile] Preview instantiation failed for '{prefab.name}': {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    previewObject = instantiated as GameObject ?? (instantiated as Component)?.gameObject;
                }
                finally
                {
                    Application.logMessageReceived -= SuppressStepOffsetWarning;
                }
                
                if (previewObject != null)
                {
                    // IMPORTANT : Sanitize AVANT d'appliquer les hideFlags
                    SanitizePreviewComponents(previewObject);
                    previewObject.hideFlags = HideFlags.HideAndDontSave;
                }
            }

            if (previewObject != null)
            {
                previewObject.transform.position = worldPos;
                previewObject.transform.rotation = rotation;

                Renderer[] renderers = previewObject.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    foreach (Renderer r in renderers)
                    {
                        if (r != null && r.sharedMaterial != null)
                        {
                            // Use a MaterialPropertyBlock to tint without instantiating materials
                            var mpb = new MaterialPropertyBlock();
                            r.GetPropertyBlock(mpb);
                            mpb.SetColor("_Color", color);
                            r.SetPropertyBlock(mpb);
                        }
                    }
                }
            }
#endif
        }

        public void ClearPreviewObject()
        {
            if (previewObject != null)
            {
                SafeDestroy(previewObject);
                previewObject = null;
            }
        }

        public void ClearGrassTilesInGrid()
        {
            GameObject grassGO = GameObject.Find("Tilemap_Rule_Grass");
            if (grassGO != null)
            {
                Tilemap grassTilemap = grassGO.GetComponent<Tilemap>();
                if (grassTilemap != null)
                {
                    int gridWidth = gridSize.x;
                    int gridHeight = gridSize.y;
                    Vector3Int gridOffset = new Vector3Int(-gridWidth / 2, -gridHeight / 2, 0);
                    for (int x = 0; x < gridWidth; x++)
                    {
                        for (int y = 0; y < gridHeight; y++)
                        {
                            Vector3Int pos = new Vector3Int(x, y, 0) + gridOffset;
                            grassTilemap.SetTile(pos, null);
                        }
                    }
                }
            }
        }

        public string GetLevelJson(LevelData level)
        {
            return level.jsonFile != null ? level.jsonFile.text : "";
        }
    }
}
