using System.Collections.Generic;
using System.Linq;
using Bekkoloco.DOTS;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Bekkoloco
{
    public partial class QuickTilemapEditor
    {
#if UNITY_EDITOR
        private const string DefaultSlopeSkirtMaterialPath = "Assets/BEKKOLOCO/QuickTile/Material/Grass_skirt.mat";
        private const string DefaultBridgeRailMaterialPath = "Assets/BEKKOLOCO/QuickTile/Material/bridge.mat";
        private static Material _defaultSlopeSkirtMaterial;
        private static Material _defaultBridgeRailMaterial;

        private static Material GetDefaultSlopeSkirtMaterial()
        {
            if (_defaultSlopeSkirtMaterial == null)
                _defaultSlopeSkirtMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultSlopeSkirtMaterialPath);

            return _defaultSlopeSkirtMaterial;
        }

        private static Material GetDefaultBridgeRailMaterial()
        {
            if (_defaultBridgeRailMaterial == null)
                _defaultBridgeRailMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultBridgeRailMaterialPath);

            return _defaultBridgeRailMaterial;
        }
#endif

        public void AssignPathToObject(Vector3Int objectPosition, int pathIndex)
        {
            for (int i = 0; i < placedObjects.Count; i++)
            {
                if (placedObjects[i].position == objectPosition)
                {
                    int oldPathIndex = placedObjects[i].pathIndex;
                    placedObjects[i].pathIndex = pathIndex + 1;

                    foreach (GameObject go in instantiatedGameObjects)
                    {
                        if (go == null) continue;

                        Tilemap parentMap = go.transform.parent?.GetComponent<Tilemap>();
                        if (parentMap == null) continue;

                        Vector3Int cellPos = SafeWorldToCell(go.transform.position, parentMap);

                        if (cellPos == objectPosition)
                        {
                            PathFollower follower = go.GetComponent<PathFollower>();
                            if (follower == null && pathIndex >= 0)
                            {
                                follower = go.AddComponent<PathFollower>();
                            }

                            if (follower != null)
                            {
                                if (pathIndex < 0)
                                {
                                    follower.SetPathIndex(-1);
                                }
                                else if (pathIndex < paths.Count)
                                {
                                    follower.SetPathIndex(pathIndex);

                                    List<Vector2> worldPath = paths[pathIndex].points
                                        .Select(p => {
                                            Vector3 worldPos = GetPathWorldPos(paths[pathIndex], p);
                                            return new Vector2(worldPos.x, worldPos.z);
                                        })
                                        .ToList();

                                    follower.SetPath(worldPath);

                                    if (Application.isPlaying)
                                        follower.StartMoving();
                                }
                            }
                            break;
                        }
                    }
                    break;
                }
            }
        }

        public void InitializePathFollowersAfterLoad()
        {
            Vector3 levelOffset = Vector3.zero;
            if (centerOriginToSurfaceMass && targetTilemap != null && targetTilemap.transform.parent != null)
            {
                levelOffset = targetTilemap.transform.parent.position;
            }

            foreach (var po in placedObjects)
            {
                if (po.pathIndex <= 0 || po.pathIndex > paths.Count)
                    continue;

                int pathIdx = po.pathIndex - 1;
                var path = paths[pathIdx];

                var go = instantiatedGameObjects.FirstOrDefault(g =>
                {
                    var tm = g.transform.parent?.GetComponent<Tilemap>();
                    return tm != null && SafeWorldToCell(g.transform.position, tm) == po.position;
                });
                if (go == null)
                    continue;

                var map = go.transform.parent.GetComponent<Tilemap>();

                var worldPath = path.points
                    .Select(pt =>
                    {
                        Vector3 worldPos = GetPathWorldPos(path, pt);
                        worldPos.y += po.instanceYOffset;

                        if (centerOriginToSurfaceMass)
                        {
                            worldPos.x -= levelOffset.x;
                            worldPos.z -= levelOffset.z;
                        }

                        return new Vector2(worldPos.x, worldPos.z);
                    })
                    .ToList();

                var pf = go.GetComponent<PathFollower>() ?? go.AddComponent<PathFollower>();
                pf.SetPathIndex(pathIdx);
                pf.SetPath(worldPath);
                if (Application.isPlaying)
                    pf.StartMoving();
            }

            // Build track meshes for paths that have enableTrackMesh
            RebuildAllTrackMeshes();
        }

        public void RefreshAllPathFollowers()
        {
            foreach (var placedObj in placedObjects)
            {
                if (placedObj.pathIndex > 0 && placedObj.pathIndex <= paths.Count)
                {
                    foreach (GameObject go in instantiatedGameObjects)
                    {
                        if (go == null) continue;

                        Tilemap parentMap = go.transform.parent?.GetComponent<Tilemap>();
                        if (parentMap == null) continue;

                        Vector3Int goCell = SafeWorldToCell(go.transform.position, parentMap);
                        if (goCell == placedObj.position)
                        {
                            PathFollower pf = go.GetComponent<PathFollower>();
                            if (pf != null)
                            {
                                int pathIndex = placedObj.pathIndex - 1;
                                pf.SetPathIndex(pathIndex);

                                List<Vector2> worldPath = paths[pathIndex].points
                                    .Select(p => {
                                        Vector3 worldPos = GetPathWorldPos(paths[pathIndex], p);
                                        return new Vector2(worldPos.x, worldPos.z);
                                    })
                                    .ToList();

                                pf.SetPath(worldPath);
                            }
                        }
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────
        // Track Mesh Builder management
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// For each path that is explicitly a Track and has enableTrackMesh = true,
        /// create or update a TrackMeshBuilder child object under the Grid parent.
        /// Called after level load and when track-mesh settings change.
        /// </summary>
        public void RebuildAllTrackMeshes()
        {
            if (paths == null || targetTilemap == null) return;

            CleanupLegacyPathVisuals();
            bool navMeshRelevantPathMeshesChanged = false;

            Transform gridParent = targetTilemap.transform.parent ?? transform;

            // Determine level offset for surface-mass centering
            Vector3 levelOffset = Vector3.zero;
            if (centerOriginToSurfaceMass && targetTilemap.transform.parent != null)
                levelOffset = targetTilemap.transform.parent.position;

            for (int i = 0; i < paths.Count; i++)
            {
                string goName = $"TrackMesh_Path_{i}";
                Transform existing = gridParent.Find(goName);

                var path = paths[i];

                bool needsTrackMesh = path.pathType == PathType.Track &&
                                      path.enableTrackMesh &&
                                      path.points != null &&
                                      path.points.Count >= 2;

                if (!needsTrackMesh)
                {
                    // Destroy track mesh object if it exists but is no longer needed
                    if (existing != null)
                        SafeDestroy(existing.gameObject);
                    continue;
                }

                // Create or get the TrackMeshBuilder object
                TrackMeshBuilder builder;
                if (existing != null)
                {
                    builder = existing.GetComponent<TrackMeshBuilder>();
                    if (builder == null)
                        builder = existing.gameObject.AddComponent<TrackMeshBuilder>();
                }
                else
                {
                    var go = new GameObject(goName);
                    go.transform.SetParent(gridParent, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    builder = go.AddComponent<TrackMeshBuilder>();
                }

                // Assign material
                if (path.trackMaterial != null)
                {
                    var renderer = builder.GetComponent<MeshRenderer>();
                    if (renderer != null)
                        renderer.sharedMaterial = path.trackMaterial;
                }

                // Set mesh settings
                builder.subdivisions = path.trackSubdivisions;
                builder.defaultWidth = path.trackWidth;
                builder.uvTilingY = path.trackUVTilingY;

                // Sync trackPoints count with points count
                SyncTrackPointsList(path);

                // Convert grid positions to world positions
                var worldPoints = new List<TrackMeshBuilder.TrackPointWorld>();
                for (int j = 0; j < path.points.Count; j++)
                {
                    Vector3 worldPos = GetPlacementWorldPos(path.points[j]);

                    // Apply level offset if centering
                    if (centerOriginToSurfaceMass)
                    {
                        worldPos.x -= levelOffset.x;
                        worldPos.z -= levelOffset.z;
                    }

                    var tp = (j < path.trackPoints.Count) ? path.trackPoints[j] : new TrackPoint();

                    worldPoints.Add(new TrackMeshBuilder.TrackPointWorld
                    {
                        position = worldPos,
                        snapToGround = tp.snapToGround,
                        rotation = tp.rotation,
                        width = tp.width > 0.001f ? tp.width : path.trackWidth
                    });
                }

                builder.SetPoints(worldPoints);
                ApplyPathGeneratedObjectVisibility(i, gridParent);
            }

            // ── Build PathMeshBuilder for Slope / Stairs / Bridge paths ──
            for (int i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                string pmGoName = $"PathMesh_{i}";
                Transform pmExisting = gridParent.Find(pmGoName);

                bool needsPathMesh = (path.pathType == PathType.Slope ||
                                      path.pathType == PathType.Stairs ||
                                      path.pathType == PathType.Bridge) &&
                                     path.points != null && path.points.Count >= 2;

                if (!needsPathMesh)
                {
                    if (pmExisting != null)
                    {
                        SafeDestroy(pmExisting.gameObject);
                        navMeshRelevantPathMeshesChanged = true;
                    }
                    continue;
                }

                PathMeshBuilder pmBuilder;
                if (pmExisting != null)
                {
                    pmBuilder = pmExisting.GetComponent<PathMeshBuilder>();
                    if (pmBuilder == null)
                        pmBuilder = pmExisting.gameObject.AddComponent<PathMeshBuilder>();
                }
                else
                {
                    var go = new GameObject(pmGoName);
                    go.transform.SetParent(gridParent, false);
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                    pmBuilder = go.AddComponent<PathMeshBuilder>();
                }

                // Paths must sit flush on the sampled tile surface.
                // Mirroring the terrain deformer onto generated path meshes introduced
                // a visible vertical offset at the cliff/platform contact points, so we
                // keep generated path meshes in the grid parent's local space.
                RadialHillDeformer sourcePathDeformer = null;
                AlignGeneratedPathTransform(pmBuilder.transform, sourcePathDeformer, gridParent);

                // Configure
                pmBuilder.pathType = path.pathType;
                pmBuilder.width = path.slopeWidth;
                pmBuilder.stairSteps = path.stairSteps;
                pmBuilder.stairAutoSteps = path.stairAutoSteps;
                pmBuilder.stairStepDepth = path.stairStepDepth;
                pmBuilder.smoothTransition = path.smoothTransition;
                pmBuilder.slopeSideSkirtEnabled = path.slopeSideSkirtEnabled;
                pmBuilder.slopeSideSkirtWidth = path.slopeSideSkirtWidth;
                pmBuilder.slopeSideSkirtHeight = path.slopeSideSkirtHeight;
                pmBuilder.slopeSideSkirtSegments = path.slopeSideSkirtSegments;
                pmBuilder.slopeSideSkirtUVScale = path.slopeSideSkirtUVScale;
                pmBuilder.slopeSideSkirtUVOffsetY = path.slopeSideSkirtUVOffsetY;
                // Keep ramps/stairs flush against touching tiles; endpoint inset creates visible gaps.
                pmBuilder.endpointPadding = path.pathType == PathType.Bridge ? 0.5f : 0f;
                pmBuilder.bridgeWidth = path.bridgeWidth;
                pmBuilder.bridgeHeight = path.bridgeHeight;
                pmBuilder.bridgeProfile = path.bridgeProfile;
                pmBuilder.bridgeCurve = path.bridgeCurve;
                pmBuilder.bridgeSteps = path.bridgeSteps;
                pmBuilder.bridgeRailings = path.bridgeRailings;
                pmBuilder.bridgeRailThickness = path.bridgeRailThickness;
                pmBuilder.bridgeRailSpread = path.bridgeRailSpread;
                pmBuilder.bridgeRailEndExtension = path.bridgeRailEndExtension;
                pmBuilder.bridgeRailYOffset = path.bridgeRailYOffset;
                pmBuilder.bridgeRailUvOffsetY = path.bridgeRailUvOffsetY;
                pmBuilder.bridgeRailCurveFollow = path.bridgeRailCurveFollow;

                // Materials: 2 submeshes (surface + walls)
                var pmRenderer = pmBuilder.GetComponent<MeshRenderer>();
                if (pmRenderer != null)
                {
                    Material surfMat = null;
                    Material wallMat = null;
                    Material skirtMat = null;
                    Material railMat = null;

                    if (path.pathType == PathType.Bridge)
                    {
                        surfMat = path.bridgeMaterial;
                        wallMat = path.bridgeMaterial;
                    }
                    else
                    {
                        surfMat = path.slopeSurfaceMaterial;
                        wallMat = path.slopeWallMaterial ?? path.slopeSurfaceMaterial;

                        if (path.pathType == PathType.Slope)
                        {
#if UNITY_EDITOR
                            skirtMat = GetDefaultSlopeSkirtMaterial();
#endif
                            if (skirtMat == null)
                                skirtMat = surfMat != null ? surfMat : wallMat;
                        }
                        else if (path.pathType == PathType.Stairs)
                        {
                            railMat = path.stairRailMaterial;
#if UNITY_EDITOR
                            if (railMat == null)
                                railMat = GetDefaultBridgeRailMaterial();
#endif
                            if (railMat == null)
                                railMat = wallMat != null ? wallMat : surfMat;
                        }
                    }

                    bool slopeUsesSkirt = path.pathType == PathType.Slope &&
                                          path.slopeSideSkirtEnabled &&
                                          path.slopeSideSkirtWidth > 0f &&
                                          path.slopeSideSkirtHeight > 0f &&
                                          path.slopeSideSkirtSegments > 0;
                    bool stairsUseRails = path.pathType == PathType.Stairs && path.bridgeRailings;

                    if (slopeUsesSkirt && (surfMat != null || wallMat != null || skirtMat != null))
                    {
                        Material resolvedSurface = surfMat != null ? surfMat : (wallMat != null ? wallMat : skirtMat);
                        Material resolvedWall = wallMat != null ? wallMat : (resolvedSurface != null ? resolvedSurface : skirtMat);
                        Material resolvedSkirt = skirtMat != null ? skirtMat : resolvedSurface;

                        pmRenderer.sharedMaterials = new Material[]
                        {
                            resolvedSurface,
                            resolvedWall,
                            resolvedSkirt
                        };
                    }
                    else if (stairsUseRails && (surfMat != null || wallMat != null || railMat != null))
                    {
                        Material resolvedSurface = surfMat != null ? surfMat : (wallMat != null ? wallMat : railMat);
                        Material resolvedWall = wallMat != null ? wallMat : (resolvedSurface != null ? resolvedSurface : railMat);
                        Material resolvedRail = railMat != null ? railMat : resolvedWall;

                        pmRenderer.sharedMaterials = new Material[]
                        {
                            resolvedSurface,
                            resolvedWall,
                            resolvedRail
                        };
                    }
                    else if (surfMat != null || wallMat != null)
                    {
                        pmRenderer.sharedMaterials = new Material[]
                        {
                            surfMat,
                            wallMat ?? surfMat
                        };
                    }
                }

                // Convert grid points to LOCAL space of gridParent with correct tile top height
                var pmLocalPoints = new List<Vector3>();
                if (path.pathType == PathType.Bridge && TryBuildSmoothedBridgeLocalPoints(path, pmBuilder, pmLocalPoints))
                {
                    // Diagonal bridges can use a smoothed planar bezier so they
                    // meet the cliff edges more naturally in top view, even when
                    // their endpoints sit at different heights.
                }
                else
                {
                    for (int j = 0; j < path.points.Count; j++)
                    {
                        Vector3 worldPos = path.pathType == PathType.Bridge
                            ? GetBridgeMeshWorldPos(path, j)
                            : GetPathWorldPos(path, path.points[j]);
                        worldPos.y = path.pathType == PathType.Bridge
                            ? GetSurfaceYAtBridgePoint(path, j)
                            : GetSurfaceYAtPathPoint(path, path.points[j]);
                        Vector3 localPos = pmBuilder.transform.InverseTransformPoint(worldPos);

                        pmLocalPoints.Add(localPos);
                    }
                }

                // Points are now in local space — PathMeshBuilder uses them directly
                pmBuilder.SetPoints(pmLocalPoints);
                pmBuilder.RebuildNow();
                // Remove any previously mirrored deformer so rebuilt paths snap back
                // exactly onto the tile/platform level.
                SyncGeneratedPathDeformer(pmBuilder.gameObject, sourcePathDeformer, path);
                ApplyPathGeneratedObjectVisibility(i, gridParent);
                navMeshRelevantPathMeshesChanged = true;
            }

            // Clean up orphaned track mesh objects (e.g. if paths were removed)
            for (int i = paths.Count; i < 50; i++) // reasonable upper bound
            {
                string goName = $"TrackMesh_Path_{i}";
                Transform orphan = gridParent.Find(goName);
                if (orphan == null) break;
                SafeDestroy(orphan.gameObject);
            }

            // Clean up orphaned path mesh objects
            for (int i = paths.Count; i < 50; i++)
            {
                string pmGoName = $"PathMesh_{i}";
                Transform orphan = gridParent.Find(pmGoName);
                if (orphan == null) break;
                SafeDestroy(orphan.gameObject);
                navMeshRelevantPathMeshesChanged = true;
            }

            if (navMeshRelevantPathMeshesChanged)
            {
                RequestNavMeshRebuild();
            }
        }

        public void ApplyPathGeneratedObjectVisibility(int pathIndex)
        {
            if (targetTilemap == null)
                return;

            Transform gridParent = targetTilemap.transform.parent ?? transform;
            ApplyPathGeneratedObjectVisibility(pathIndex, gridParent);
        }

        private void ApplyPathGeneratedObjectVisibility(int pathIndex, Transform gridParent)
        {
            if (paths == null || pathIndex < 0 || pathIndex >= paths.Count || gridParent == null)
                return;

            var path = paths[pathIndex];
            bool isVisible = path == null || path.isVisible;

            Transform trackMesh = gridParent.Find($"TrackMesh_Path_{pathIndex}");
            if (trackMesh != null)
                trackMesh.gameObject.SetActive(isVisible);

            Transform pathMesh = gridParent.Find($"PathMesh_{pathIndex}");
            if (pathMesh != null)
                pathMesh.gameObject.SetActive(isVisible);

            if (path != null &&
                (path.pathType == PathType.Slope ||
                 path.pathType == PathType.Stairs ||
                 path.pathType == PathType.Bridge))
            {
                RequestNavMeshRebuild();
            }
        }

        /// <summary>
        /// Ensure trackPoints list is in sync with points list.
        /// </summary>
        private void SyncTrackPointsList(Path path)
        {
            if (path.trackPoints == null)
                path.trackPoints = new List<TrackPoint>();

            // Add missing entries
            while (path.trackPoints.Count < path.points.Count)
            {
                int idx = path.trackPoints.Count;
                path.trackPoints.Add(new TrackPoint
                {
                    gridPosition = (idx < path.points.Count) ? path.points[idx] : Vector3Int.zero,
                    snapToGround = true,
                    rotation = 0f,
                    width = path.trackWidth
                });
            }

            // Trim excess
            while (path.trackPoints.Count > path.points.Count)
                path.trackPoints.RemoveAt(path.trackPoints.Count - 1);

            // Sync positions
            for (int i = 0; i < path.trackPoints.Count; i++)
                path.trackPoints[i].gridPosition = path.points[i];
        }

        /// <summary>
        /// Returns the top surface Y for a given cell position by scanning all TileRules.
        /// Checks each rule's tilemap for a tile at this cell.
        /// In the procedural renderer, the top surface is at the tilemap's WORLD Y,
        /// and the volume extends downward by sizeY (or to 0 in fixBase mode).
        /// Returns 0 if no tile is found at this position.
        /// </summary>
        private bool TryGetSurfaceYAtCell(Vector3Int cellPos, out float surfaceY)
        {
            surfaceY = 0f;
            if (tileRules == null) return false;

            float bestY = float.NegativeInfinity;
            bool found = false;

            for (int r = 0; r < tileRules.Count; r++)
            {
                var rule = tileRules[r];
                if (rule == null || rule.tile == null) continue;

                Tilemap tm = GetTilemapForPathRule(rule);
                if (tm == null) continue;
                SyncRuleYOffsetFromTilemapTransform(rule, tm);

                // Check if there's a tile at this cell
                if (tm.HasTile(new Vector3Int(cellPos.x, cellPos.y, 0)))
                {
                    // Path meshes are built from world-space anchors, so the sampled
                    // terrain height must also be in world space. Using localPosition.y
                    // introduces a constant vertical offset as soon as the Grid parent
                    // itself is moved vertically.
                    float ruleSurfaceY = tm.transform.position.y;
                    if (ruleSurfaceY > bestY)
                    {
                        bestY = ruleSurfaceY;
                        found = true;
                    }
                }
            }

            if (found)
                surfaceY = bestY;

            return found;
        }

        public float GetSurfaceYAtCell(Vector3Int cellPos)
        {
            return TryGetSurfaceYAtCell(cellPos, out float surfaceY) ? surfaceY : 0f;
        }

        private Tilemap GetTilemapForPathRule(TileRule rule)
        {
            if (rule == null)
                return null;

            if (rule.useCustomTilemap && rule.customTargetTilemap != null)
                return rule.customTargetTilemap;

            float key = Mathf.Round(rule.yOffset * 100f) / 100f;
            if (heightTilemaps != null && heightTilemaps.TryGetValue(key, out Tilemap htmTm))
                return htmTm;

            return targetTilemap;
        }

        private RadialHillDeformer FindPrimaryDeformerForPath(Path path, Transform gridParent)
        {
            if (path == null || tileRules == null || gridParent == null)
                return null;

            RadialHillDeformer bestDeformer = null;
            int bestScore = 0;
            float bestSurfaceY = float.NegativeInfinity;

            for (int i = 0; i < tileRules.Count; i++)
            {
                var rule = tileRules[i];
                Tilemap tm = GetTilemapForPathRule(rule);
                if (tm == null || tm.transform.parent != gridParent)
                    continue;

                var deformer = tm.GetComponentInChildren<RadialHillDeformer>(true);
                if (deformer == null)
                    continue;

                int matchScore = CountPathCellsOnTilemap(path, tm);
                if (matchScore <= 0)
                    continue;

                float surfaceY = tm.transform.position.y;
                if (matchScore > bestScore || (matchScore == bestScore && surfaceY > bestSurfaceY))
                {
                    bestScore = matchScore;
                    bestSurfaceY = surfaceY;
                    bestDeformer = deformer;
                }
            }

            return bestDeformer;
        }

        private int CountPathCellsOnTilemap(Path path, Tilemap tilemap)
        {
            if (path == null || path.points == null || tilemap == null)
                return 0;

            int score = 0;
            for (int i = 0; i < path.points.Count; i++)
            {
                if (PathTouchesTilemapCell(path, tilemap, path.points[i]))
                    score++;
            }

            return score;
        }

        private bool PathTouchesTilemapCell(Path path, Tilemap tilemap, Vector3Int point)
        {
            if (path == null || tilemap == null)
                return false;

            if (path.pathType == PathType.Slope || path.pathType == PathType.Stairs)
            {
                Vector3Int[] touchingCells =
                {
                    new Vector3Int(point.x, point.y, 0),
                    new Vector3Int(point.x - 1, point.y, 0),
                    new Vector3Int(point.x, point.y - 1, 0),
                    new Vector3Int(point.x - 1, point.y - 1, 0)
                };

                for (int i = 0; i < touchingCells.Length; i++)
                {
                    if (tilemap.HasTile(touchingCells[i]))
                        return true;
                }

                return false;
            }

            if (path.pathType == PathType.Bridge)
            {
                if (tilemap.HasTile(point))
                    return true;

                for (int i = 0; i < path.points.Count; i++)
                {
                    if (path.points[i] != point)
                        continue;

                    if (GetBridgeInteriorCell(path, i, out Vector3Int interiorCell) && tilemap.HasTile(interiorCell))
                        return true;
                    if (GetBridgeExteriorCell(path, i, out Vector3Int exteriorCell) && tilemap.HasTile(exteriorCell))
                        return true;
                }
            }

            return tilemap.HasTile(point);
        }

        private void AlignGeneratedPathTransform(Transform pathTransform, RadialHillDeformer sourceDeformer, Transform gridParent)
        {
            if (pathTransform == null)
                return;

            pathTransform.SetParent(gridParent, false);

            if (sourceDeformer != null && sourceDeformer.transform.parent == gridParent)
            {
                pathTransform.localPosition = sourceDeformer.transform.localPosition;
                pathTransform.localRotation = sourceDeformer.transform.localRotation;
                pathTransform.localScale = sourceDeformer.transform.localScale;
            }
            else
            {
                pathTransform.localPosition = Vector3.zero;
                pathTransform.localRotation = Quaternion.identity;
                pathTransform.localScale = Vector3.one;
            }
        }

        private void SyncGeneratedPathDeformer(GameObject pathObject, RadialHillDeformer sourceDeformer, Path path)
        {
            if (pathObject == null)
                return;

            var mirroredDeformer = pathObject.GetComponent<RadialHillDeformer>();

            if (sourceDeformer == null || path == null || path.pathType == PathType.Move || path.pathType == PathType.Track)
            {
                if (mirroredDeformer != null)
                    SafeDestroy(mirroredDeformer);
                return;
            }

            if (mirroredDeformer == null)
                mirroredDeformer = pathObject.AddComponent<RadialHillDeformer>();

            mirroredDeformer.runtimeStaticMode = sourceDeformer.runtimeStaticMode;
            mirroredDeformer.runtimeInitDelay = sourceDeformer.runtimeInitDelay;
            mirroredDeformer.linkRadiusToScale = sourceDeformer.linkRadiusToScale;
            mirroredDeformer.radiusLinkMode = sourceDeformer.radiusLinkMode;
            mirroredDeformer.scaleMetric = sourceDeformer.scaleMetric;
            mirroredDeformer.handle = sourceDeformer.handle;
            mirroredDeformer.additionalHandles = sourceDeformer.additionalHandles != null
                ? new List<Transform>(sourceDeformer.additionalHandles.Where(h => h != null))
                : new List<Transform>();
            mirroredDeformer.heightPerUnitY = sourceDeformer.heightPerUnitY;
            mirroredDeformer.shape = sourceDeformer.shape;
            mirroredDeformer.radius = sourceDeformer.radius;
            mirroredDeformer.falloff = sourceDeformer.falloff;
            mirroredDeformer.gaussianSharpness = sourceDeformer.gaussianSharpness;
            mirroredDeformer.invertDirection = sourceDeformer.invertDirection;
            mirroredDeformer.useHandleZero = sourceDeformer.useHandleZero;
            mirroredDeformer.handleZeroAlongUp = sourceDeformer.handleZeroAlongUp;
            mirroredDeformer.useYMin = sourceDeformer.useYMin;
            mirroredDeformer.yMin = sourceDeformer.yMin;
            mirroredDeformer.yMinRelativeToHandle = sourceDeformer.yMinRelativeToHandle;
            mirroredDeformer.yFeather = sourceDeformer.yFeather;
            mirroredDeformer.yMinFalloff = sourceDeformer.yMinFalloff;
            mirroredDeformer.yMinGaussianSharpness = sourceDeformer.yMinGaussianSharpness;
            mirroredDeformer.clampWorldMinY = sourceDeformer.clampWorldMinY;
            mirroredDeformer.worldMinY = sourceDeformer.worldMinY;
            mirroredDeformer.clampOnlyAffected = sourceDeformer.clampOnlyAffected;
            mirroredDeformer.compensateLocalScaleY = sourceDeformer.compensateLocalScaleY;
            mirroredDeformer.yDeformRatio = sourceDeformer.yDeformRatio;
            mirroredDeformer.recalcNormals = sourceDeformer.recalcNormals;
            mirroredDeformer.updateMeshCollider = sourceDeformer.updateMeshCollider;

            mirroredDeformer.RecacheAndApply();
        }

        private bool TryGetBridgeSpanStep(Path path, int pointIndex, out int stepX, out int stepY)
        {
            stepX = 0;
            stepY = 0;

            if (path == null || path.points == null || path.points.Count < 2 ||
                pointIndex < 0 || pointIndex >= path.points.Count)
            {
                return false;
            }

            Vector3Int point = path.points[pointIndex];
            Vector3Int tangent;

            if (pointIndex <= 0)
                tangent = path.points[1] - point;
            else if (pointIndex >= path.points.Count - 1)
                tangent = point - path.points[path.points.Count - 2];
            else
                tangent = path.points[pointIndex + 1] - path.points[pointIndex - 1];

            stepX = tangent.x == 0 ? 0 : (tangent.x > 0 ? 1 : -1);
            stepY = tangent.y == 0 ? 0 : (tangent.y > 0 ? 1 : -1);

            return stepX != 0 || stepY != 0;
        }

        private bool GetBridgeInteriorCell(Path path, int pointIndex, out Vector3Int interiorCell)
        {
            interiorCell = Vector3Int.zero;
            if (path == null || path.points == null || path.points.Count < 2)
                return false;

            Vector3Int point = path.points[pointIndex];
            if (!TryGetBridgeSpanStep(path, pointIndex, out int stepX, out int stepY))
                return false;

            if (pointIndex <= 0)
                interiorCell = new Vector3Int(point.x - stepX, point.y - stepY, 0);
            else if (pointIndex >= path.points.Count - 1)
                interiorCell = new Vector3Int(point.x + stepX, point.y + stepY, 0);
            else
                interiorCell = point;

            return true;
        }

        private float GetSurfaceYAtBridgePoint(Path path, int pointIndex)
        {
            if (path == null || path.points == null || pointIndex < 0 || pointIndex >= path.points.Count)
                return 0f;

            Vector3Int cellPos = path.points[pointIndex];

            if (GetBridgeInteriorCell(path, pointIndex, out Vector3Int interiorCell) &&
                TryGetSurfaceYAtCell(interiorCell, out float interiorSurfaceY))
            {
                return interiorSurfaceY;
            }

            return GetSurfaceYAtCell(cellPos);
        }

        private bool GetBridgeExteriorCell(Path path, int pointIndex, out Vector3Int exteriorCell)
        {
            exteriorCell = Vector3Int.zero;
            if (path == null || path.points == null || path.points.Count < 2)
                return false;

            if (pointIndex > 0 && pointIndex < path.points.Count - 1)
                return false;

            Vector3Int point = path.points[pointIndex];
            if (!TryGetBridgeSpanStep(path, pointIndex, out int stepX, out int stepY))
                return false;

            if (pointIndex <= 0)
                exteriorCell = new Vector3Int(point.x + stepX, point.y + stepY, 0);
            else
                exteriorCell = new Vector3Int(point.x - stepX, point.y - stepY, 0);

            return true;
        }

        private bool CellMatchesBridgePlatformHeight(Vector3Int cellPos, float platformSurfaceY, float tolerance = 0.01f)
        {
            return TryGetSurfaceYAtCell(cellPos, out float cellSurfaceY) &&
                   Mathf.Abs(cellSurfaceY - platformSurfaceY) <= tolerance;
        }

        private bool TryGetBridgeEdgeAnchor(Path path, int pointIndex, out Vector3 anchorWorldPos, out Vector3 outwardDir)
        {
            anchorWorldPos = Vector3.zero;
            outwardDir = Vector3.zero;

            if (path == null || path.points == null || pointIndex < 0 || pointIndex >= path.points.Count)
                return false;

            Vector3Int cellPos = path.points[pointIndex];
            Vector3 worldPos = GetPlacementWorldPos(cellPos);
            float platformSurfaceY = GetSurfaceYAtBridgePoint(path, pointIndex);
            bool pointCellTouchesPlatform = CellMatchesBridgePlatformHeight(cellPos, platformSurfaceY);

            if (pointCellTouchesPlatform &&
                GetBridgeExteriorCell(path, pointIndex, out Vector3Int exteriorCell) &&
                !CellMatchesBridgePlatformHeight(exteriorCell, platformSurfaceY))
            {
                Vector3 exteriorWorldPos = GetPlacementWorldPos(exteriorCell);
                outwardDir = exteriorWorldPos - worldPos;
                outwardDir.y = 0f;
                if (outwardDir.sqrMagnitude > 0.0001f)
                    outwardDir.Normalize();

                anchorWorldPos = Vector3.Lerp(worldPos, exteriorWorldPos, 0.5f);
                anchorWorldPos.y = worldPos.y;
                return true;
            }

            if (GetBridgeInteriorCell(path, pointIndex, out Vector3Int interiorCell))
            {
                Vector3 interiorWorldPos = GetPlacementWorldPos(interiorCell);
                outwardDir = worldPos - interiorWorldPos;
                outwardDir.y = 0f;
                if (outwardDir.sqrMagnitude > 0.0001f)
                    outwardDir.Normalize();

                anchorWorldPos = Vector3.Lerp(worldPos, interiorWorldPos, 0.5f);
                anchorWorldPos.y = worldPos.y;
                return true;
            }

            anchorWorldPos = worldPos;
            if (TryGetBridgeSpanStep(path, pointIndex, out int stepX, out int stepY))
            {
                outwardDir = new Vector3(stepX, 0f, stepY).normalized;
                return true;
            }

            return false;
        }

        private float GetBridgeEndpointEmbedDistance(Path path, int pointIndex)
        {
            if (path == null || path.points == null || path.points.Count < 2)
                return 0f;

            if (pointIndex > 0 && pointIndex < path.points.Count - 1)
                return 0f;

            float embedDistance = 0.22f;
            Vector3Int cellPos = path.points[pointIndex];
            Vector3 cellWorldPos = GetPlacementWorldPos(cellPos);

            if (GetBridgeInteriorCell(path, pointIndex, out Vector3Int interiorCell))
            {
                Vector3 interiorWorldPos = GetPlacementWorldPos(interiorCell);
                Vector3 flatDelta = interiorWorldPos - cellWorldPos;
                flatDelta.y = 0f;
                if (flatDelta.sqrMagnitude > 0.0001f)
                    embedDistance = Mathf.Min(flatDelta.magnitude * 0.24f, 0.34f);
            }

            int neighborIndex = pointIndex <= 0 ? 1 : path.points.Count - 2;
            if (neighborIndex >= 0 && neighborIndex < path.points.Count)
            {
                float endpointSurfaceY = GetSurfaceYAtBridgePoint(path, pointIndex);
                float neighborSurfaceY = GetSurfaceYAtBridgePoint(path, neighborIndex);
                float higherSideDelta = endpointSurfaceY - neighborSurfaceY;
                if (higherSideDelta > 0.01f)
                    embedDistance += Mathf.Min(higherSideDelta * 0.1f, 0.1f);
            }

            return Mathf.Min(embedDistance, 0.42f);
        }

        private Vector3 GetBridgeMeshWorldPos(Path path, int pointIndex)
        {
            if (path == null || path.points == null || pointIndex < 0 || pointIndex >= path.points.Count)
                return Vector3.zero;

            Vector3Int cellPos = path.points[pointIndex];
            Vector3 worldPos = GetPlacementWorldPos(cellPos);

            if (TryGetBridgeEdgeAnchor(path, pointIndex, out _, out Vector3 outwardDir) &&
                outwardDir.sqrMagnitude > 0.0001f)
            {
                float embedDistance = GetBridgeEndpointEmbedDistance(path, pointIndex);
                worldPos -= outwardDir.normalized * embedDistance;
            }

            return worldPos;
        }

        private bool ShouldUseSmoothedBridgePlanarCurve(Path path)
        {
            if (path == null || path.points == null || path.points.Count != 2)
                return false;

            if (path.bridgeProfile == BridgeProfile.Stepped)
                return false;

            if (Mathf.Abs(path.bridgeCurve) > 0.0001f)
                return false;

            Vector3Int delta = path.points[1] - path.points[0];
            return delta.x != 0 && delta.y != 0;
        }

        private bool TryBuildSmoothedBridgeLocalPoints(Path path, PathMeshBuilder pmBuilder, List<Vector3> pmLocalPoints)
        {
            if (pmBuilder == null || pmLocalPoints == null || !ShouldUseSmoothedBridgePlanarCurve(path))
                return false;

            Vector3 startWorldPos = GetBridgeMeshWorldPos(path, 0);
            Vector3 endWorldPos = GetBridgeMeshWorldPos(path, 1);
            bool hasStartAnchor = TryGetBridgeEdgeAnchor(path, 0, out _, out Vector3 startOutwardDir);
            bool hasEndAnchor = TryGetBridgeEdgeAnchor(path, 1, out _, out Vector3 endOutwardDir);

            Vector3 startFlat = startWorldPos;
            startFlat.y = 0f;
            Vector3 endFlat = endWorldPos;
            endFlat.y = 0f;
            float flatDistance = Vector3.Distance(startFlat, endFlat);
            if (flatDistance < 0.001f)
                return false;

            if (!hasStartAnchor || startOutwardDir.sqrMagnitude < 0.0001f)
                startOutwardDir = (endWorldPos - startWorldPos).normalized;
            if (!hasEndAnchor || endOutwardDir.sqrMagnitude < 0.0001f)
                endOutwardDir = (startWorldPos - endWorldPos).normalized;

            float handleLength = Mathf.Min(flatDistance * 0.35f, 1.5f);
            Vector3 controlA = startWorldPos + startOutwardDir.normalized * handleLength;
            Vector3 controlB = endWorldPos + endOutwardDir.normalized * handleLength;

            float startY = GetSurfaceYAtBridgePoint(path, 0);
            float endY = GetSurfaceYAtBridgePoint(path, 1);
            float flatFraction = flatDistance > 0.0001f
                ? Mathf.Min(0.5f / flatDistance, 0.24f)
                : 0f;
            const int bezierSegments = 6;

            pmLocalPoints.Clear();
            for (int i = 0; i <= bezierSegments; i++)
            {
                float t = (float)i / bezierSegments;
                Vector3 worldPos = CubicBezier(startWorldPos, controlA, controlB, endWorldPos, t);
                worldPos.y = EvaluateBridgeHeightWithFlatEnds(startY, endY, t, flatFraction);
                Vector3 localPos = pmBuilder.transform.InverseTransformPoint(worldPos);
                pmLocalPoints.Add(localPos);
            }

            return pmLocalPoints.Count >= 2;
        }

        private static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;
            return uuu * p0 +
                   3f * uu * t * p1 +
                   3f * u * tt * p2 +
                   ttt * p3;
        }

        private static float EvaluateBridgeHeightWithFlatEnds(float startY, float endY, float t, float flatFraction)
        {
            t = Mathf.Clamp01(t);
            flatFraction = Mathf.Clamp(flatFraction, 0f, 0.49f);

            if (t <= flatFraction)
                return startY;

            if (t >= 1f - flatFraction)
                return endY;

            float middleT = (t - flatFraction) / Mathf.Max(1f - flatFraction * 2f, 0.0001f);
            middleT = middleT * middleT * (3f - 2f * middleT);
            return Mathf.Lerp(startY, endY, middleT);
        }

        public float GetSurfaceYAtPathPoint(Path path, Vector3Int cellPos)
        {
            bool usesDualGridIntersection = path != null &&
                                            (path.pathType == PathType.Slope ||
                                             path.pathType == PathType.Stairs);

            if (!usesDualGridIntersection || !IsDualGrid())
                return GetSurfaceYAtCell(cellPos);

            float bestY = float.NegativeInfinity;
            bool found = false;

            Vector3Int[] touchingCells =
            {
                new Vector3Int(cellPos.x, cellPos.y, 0),
                new Vector3Int(cellPos.x - 1, cellPos.y, 0),
                new Vector3Int(cellPos.x, cellPos.y - 1, 0),
                new Vector3Int(cellPos.x - 1, cellPos.y - 1, 0)
            };

            for (int i = 0; i < touchingCells.Length; i++)
            {
                if (!TryGetSurfaceYAtCell(touchingCells[i], out float candidateY))
                    continue;

                if (!found || candidateY > bestY)
                {
                    bestY = candidateY;
                    found = true;
                }
            }

            return found ? bestY : 0f;
        }

        /// <summary>
        /// Removes the old Path_* LineRenderer helpers.
        /// Path rendering is now handled exclusively by TrackMesh_Path_* and PathMesh_* objects.
        /// </summary>
        public void CleanupLegacyPathVisuals()
        {
            Transform prefabContainer = GetPrefabContainer();
            if (prefabContainer == null)
                return;

            var staleVisuals = new List<GameObject>();

            for (int i = 0; i < prefabContainer.childCount; i++)
            {
                Transform child = prefabContainer.GetChild(i);
                if (child == null || !child.name.StartsWith("Path_"))
                    continue;

                if (child.GetComponent<LineRenderer>() == null)
                    continue;

                staleVisuals.Add(child.gameObject);
            }

            foreach (GameObject staleVisual in staleVisuals)
                SafeDestroy(staleVisual);
        }

        private void FixPathIndicesBeforeSaving()
        {
            if (paths == null || paths.Count == 0 || placedObjects == null || placedObjects.Count == 0)
                return;

            const float eps = 0.001f;

            for (int i = 0; i < placedObjects.Count; i++)
            {
                var obj = placedObjects[i];
                obj.pathIndex = 0;

                for (int p = 0; p < paths.Count; p++)
                {
                    var path = paths[p];
                    if (path.points == null || path.points.Count == 0) continue;

                    if (path.points.Any(pt =>
                        Mathf.Abs(pt.x - obj.position.x) < eps &&
                        Mathf.Abs(pt.y - obj.position.y) < eps))
                    {
                        obj.pathIndex = p + 1;
                        break;
                    }
                }
            }
        }
    }
}
