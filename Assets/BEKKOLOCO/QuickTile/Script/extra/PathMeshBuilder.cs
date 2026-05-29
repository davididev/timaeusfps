// PathMeshBuilder.cs — Generates procedural meshes for Slope, Stairs, and Bridge path types.
// Attached as a component to a child GameObject under the Grid parent, similar to TrackMeshBuilder.
// Uses 2 or 3 submeshes: submesh 0 = surface (top), submesh 1 = walls (sides), optional submesh 2 = slope side skirt.

using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class PathMeshBuilder : MonoBehaviour
    {
        // ── Config (set from QuickTilemapEditor_PathSystem) ──
        [Header("Path Type")]
        public QuickTilemapEditor.PathType pathType = QuickTilemapEditor.PathType.Slope;

        [Header("World Points")]
        public List<Vector3> worldPoints = new List<Vector3>();

        [Header("Slope/Stairs Settings")]
        public float width = 1f;
        public int stairSteps = 4;
        public bool stairAutoSteps = true;
        public float stairStepDepth = 1f;
        public bool smoothTransition = true;
        public float endpointPadding = 0.5f;

        [Header("Slope Side Skirt")]
        public bool slopeSideSkirtEnabled = true;
        public float slopeSideSkirtWidth = 0.155f;
        public float slopeSideSkirtHeight = 0.485f;
        public int slopeSideSkirtSegments = 2;
        public float slopeSideSkirtUVScale = 1f;
        public float slopeSideSkirtUVOffsetY = 0.389f;

        [Header("Bridge Settings")]
        public float bridgeWidth = 1f;
        public float bridgeHeight = 0.1f;
        public QuickTilemapEditor.BridgeProfile bridgeProfile = QuickTilemapEditor.BridgeProfile.Curved;
        public float bridgeCurve = 0f;
        public int bridgeSteps = 6;
        public bool bridgeRailings = true;
        public float bridgeRailThickness = 0.16f;
        public float bridgeRailSpread = 0f;
        public float bridgeRailEndExtension = 0f;
        public float bridgeRailYOffset = 0f;
        public float bridgeRailUvOffsetY = 0f;
        public float bridgeRailCurveFollow = 0f;

        [Header("Debug")]
        public bool drawSelectedGizmos = false;

        [Header("Ground Snap")]
        public LayerMask groundLayer = ~0;
        public float groundOffset = 0.02f;

        // ── Internal ──
        private static readonly Rect BridgeBodyUvRect = new Rect(0f, 0f, 1f, 0.72f);
        private static readonly Rect BridgeRailUvRect = new Rect(0f, 0.72f, 1f, 0.28f);
        private const float BridgeHighEndpointTopLift = 0.01f;
        private Mesh _mesh;
        private MeshFilter _filter;
        private MeshCollider _meshCollider;
        private bool _dirty = true;

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            EnsureMeshCollider();
        }

        private void OnEnable()
        {
            EnsureMeshCollider();
            _dirty = true;
        }

        private void LateUpdate()
        {
            if (_dirty)
            {
                RebuildMesh();
                _dirty = false;
            }
        }

        public void MarkDirty() => _dirty = true;

        public void SetPoints(List<Vector3> points)
        {
            worldPoints = points ?? new List<Vector3>();
            _dirty = true;
        }

        public void RebuildNow()
        {
            RebuildMesh();
            _dirty = false;
        }

        // ─────────────────────────────────────────────────────────
        // Main rebuild
        // ─────────────────────────────────────────────────────────

        private void RebuildMesh()
        {
            if (_filter == null) _filter = GetComponent<MeshFilter>();
            EnsureMeshCollider();

            if (worldPoints == null || worldPoints.Count < 2)
            {
                ClearMesh();
                return;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "PathMesh";
            }
            _mesh.Clear();

            switch (pathType)
            {
                case QuickTilemapEditor.PathType.Slope:
                    BuildSlopeMesh();
                    break;
                case QuickTilemapEditor.PathType.Stairs:
                    BuildStairsMesh();
                    break;
                case QuickTilemapEditor.PathType.Bridge:
                    BuildBridgeMesh();
                    break;
                default:
                    ClearMesh();
                    return;
            }

            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _filter.sharedMesh = _mesh;
            UpdateMeshCollider();
        }

        private void EnsureMeshCollider()
        {
            if (_meshCollider == null)
                _meshCollider = GetComponent<MeshCollider>() ?? gameObject.AddComponent<MeshCollider>();
        }

        private void UpdateMeshCollider()
        {
            if (_meshCollider == null)
                return;

            // Force Unity to refresh the collision shape when the procedural mesh changes.
            _meshCollider.sharedMesh = null;
            _meshCollider.sharedMesh = _mesh;
        }

        // ─────────────────────────────────────────────────────────
        // SLOPE — smooth ramp between consecutive points
        // ─────────────────────────────────────────────────────────

        private void BuildSlopeMesh()
        {
            var surfaceVerts = new List<Vector3>();
            var surfaceUVs = new List<Vector2>();
            var surfaceTris = new List<int>();
            var wallVerts = new List<Vector3>();
            var wallUVs = new List<Vector2>();
            var wallTris = new List<int>();
            var skirtVerts = new List<Vector3>();
            var skirtUVs = new List<Vector2>();
            var skirtTris = new List<int>();

            int segments = 12; // Smoothness of each segment

            for (int seg = 0; seg < worldPoints.Count - 1; seg++)
            {
                // Points are already in local space (grid-parent local)
                Vector3 start = worldPoints[seg];
                Vector3 end = worldPoints[seg + 1];
                ExpandSegmentEndpoints(seg, ref start, ref end);

                Vector3 direction = (end - start);
                float length = new Vector3(direction.x, 0, direction.z).magnitude;
                float heightDiff = end.y - start.y;

                Vector3 flatDir = new Vector3(direction.x, 0, direction.z).normalized;
                if (flatDir.sqrMagnitude < 0.001f) flatDir = Vector3.forward;

                Vector3 right = Vector3.Cross(Vector3.up, flatDir).normalized;
                float halfW = width * 0.5f;

                int baseVert = surfaceVerts.Count;

                for (int i = 0; i <= segments; i++)
                {
                    float t = (float)i / segments;

                    // Height interpolation — smoothstep for smooth transition
                    float heightT = smoothTransition ? SmoothStep(t) : t;
                    float y = start.y + heightDiff * heightT;
                    Vector3 pos = start + flatDir * (length * t);
                    pos.y = y;

                    Vector3 left = pos - right * halfW;
                    Vector3 rt = pos + right * halfW;

                    surfaceVerts.Add(left);
                    surfaceVerts.Add(rt);
                    surfaceUVs.Add(new Vector2(0f, t));
                    surfaceUVs.Add(new Vector2(1f, t));

                    if (i > 0)
                    {
                        int v = baseVert + i * 2;
                        surfaceTris.Add(v - 2); surfaceTris.Add(v);     surfaceTris.Add(v - 1);
                        surfaceTris.Add(v - 1); surfaceTris.Add(v);     surfaceTris.Add(v + 1);
                    }
                }

                // Side walls
                BuildSideWalls(surfaceVerts, baseVert, segments, start.y, end.y, halfW, flatDir, right,
                               wallVerts, wallUVs, wallTris);

                // Tilemap-style skirt on the left/right edges only.
                BuildSlopeSideSkirts(surfaceVerts, baseVert, segments, right, skirtVerts, skirtUVs, skirtTris);
            }

            ApplyMeshWithOptionalSkirt(surfaceVerts, surfaceUVs, surfaceTris,
                                       wallVerts, wallUVs, wallTris,
                                       skirtVerts, skirtUVs, skirtTris);
        }

        // ─────────────────────────────────────────────────────────
        // STAIRS — stepped connection between consecutive points
        // ─────────────────────────────────────────────────────────

        private void BuildStairsMesh()
        {
            var surfaceVerts = new List<Vector3>();
            var surfaceUVs = new List<Vector2>();
            var surfaceTris = new List<int>();
            var wallVerts = new List<Vector3>();
            var wallUVs = new List<Vector2>();
            var wallTris = new List<int>();
            var railVerts = new List<Vector3>();
            var railUVs = new List<Vector2>();
            var railTris = new List<int>();

            for (int seg = 0; seg < worldPoints.Count - 1; seg++)
            {
                // Points are already in local space (grid-parent local)
                Vector3 start = worldPoints[seg];
                Vector3 end = worldPoints[seg + 1];
                ExpandSegmentEndpoints(seg, ref start, ref end);

                Vector3 direction = end - start;
                float length = new Vector3(direction.x, 0, direction.z).magnitude;
                float heightDiff = end.y - start.y;

                Vector3 flatDir = new Vector3(direction.x, 0, direction.z).normalized;
                if (flatDir.sqrMagnitude < 0.001f) flatDir = Vector3.forward;

                Vector3 right = Vector3.Cross(Vector3.up, flatDir).normalized;
                float halfW = width * 0.5f;

                int steps = ResolveStairStepCount(length);

                float stepLength = length / steps;
                float stepHeight = heightDiff / steps;

                // Determine direction: ascending or descending
                bool ascending = heightDiff > 0;

                if (!ascending && Mathf.Abs(stepHeight) > 0.001f)
                {
                    Vector3 landingLeft = start - right * halfW;
                    Vector3 landingRight = start + right * halfW;
                    Vector3 riserBL = landingLeft; riserBL.y = start.y + stepHeight;
                    Vector3 riserBR = landingRight; riserBR.y = start.y + stepHeight;
                    Vector3 riserTL = landingLeft; riserTL.y = start.y;
                    Vector3 riserTR = landingRight; riserTR.y = start.y;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        riserBL, riserBR, riserTL, riserTR,
                        new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0f, 1f), new Vector2(1f, 1f));
                }

                for (int s = 0; s < steps; s++)
                {
                    float t0 = (float)s / steps;
                    float t1 = (float)(s + 1) / steps;

                    float y;
                    if (ascending)
                        y = start.y + stepHeight * s;
                    else
                        y = start.y + stepHeight * (s + 1);

                    Vector3 frontLeft  = start + flatDir * (length * t0) - right * halfW;
                    Vector3 frontRight = start + flatDir * (length * t0) + right * halfW;
                    Vector3 backLeft   = start + flatDir * (length * t1) - right * halfW;
                    Vector3 backRight  = start + flatDir * (length * t1) + right * halfW;

                    frontLeft.y = y; frontRight.y = y;
                    backLeft.y = y;  backRight.y = y;

                    // The lowest landing already exists in the level mesh, so we do not
                    // generate a duplicate stair tread on that exact bottom level.
                    bool isBottomTread = ascending ? s == 0 : s == steps - 1;
                    if (!isBottomTread)
                    {
                        int bv = surfaceVerts.Count;
                        surfaceVerts.Add(frontLeft);  surfaceUVs.Add(new Vector2(0, t0));
                        surfaceVerts.Add(frontRight); surfaceUVs.Add(new Vector2(1, t0));
                        surfaceVerts.Add(backLeft);   surfaceUVs.Add(new Vector2(0, t1));
                        surfaceVerts.Add(backRight);  surfaceUVs.Add(new Vector2(1, t1));

                        surfaceTris.Add(bv); surfaceTris.Add(bv + 2); surfaceTris.Add(bv + 1);
                        surfaceTris.Add(bv + 1); surfaceTris.Add(bv + 2); surfaceTris.Add(bv + 3);
                    }

                    // Riser (vertical face between steps)
                    if (s > 0)
                    {
                        float prevY;
                        if (ascending)
                            prevY = start.y + stepHeight * (s - 1);
                        else
                            prevY = start.y + stepHeight * s;

                        Vector3 riserBL = frontLeft; riserBL.y = prevY;
                        Vector3 riserBR = frontRight; riserBR.y = prevY;
                        Vector3 riserTL = frontLeft;
                        Vector3 riserTR = frontRight;

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            riserBL, riserBR, riserTL, riserTR,
                            new Vector2(0f, 0f), new Vector2(1f, 0f),
                            new Vector2(0f, 1f), new Vector2(1f, 1f));
                    }
                }

                if (ascending && Mathf.Abs(stepHeight) > 0.001f)
                {
                    Vector3 landingLeft = start + flatDir * length - right * halfW;
                    Vector3 landingRight = start + flatDir * length + right * halfW;
                    Vector3 riserBL = landingLeft; riserBL.y = start.y + stepHeight * (steps - 1);
                    Vector3 riserBR = landingRight; riserBR.y = start.y + stepHeight * (steps - 1);
                    Vector3 riserTL = landingLeft; riserTL.y = end.y;
                    Vector3 riserTR = landingRight; riserTR.y = end.y;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        riserBL, riserBR, riserTL, riserTR,
                        new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0f, 1f), new Vector2(1f, 1f));
                }

                // Side walls for stairs — simplified: two vertical quads per step
                BuildStairSideWalls(start, end, steps, stepLength, stepHeight, halfW,
                                    flatDir, right, wallVerts, wallUVs, wallTris);

                if (bridgeRailings)
                {
                    BuildStairRailings(start, end, steps, halfW, right,
                                       railVerts, railUVs, railTris);
                }
            }

            ApplyMeshWithOptionalExtraSubmesh(surfaceVerts, surfaceUVs, surfaceTris,
                                              wallVerts, wallUVs, wallTris,
                                              railVerts, railUVs, railTris);
        }

        private int ResolveStairStepCount(float length)
        {
            if (!stairAutoSteps)
                return Mathf.Max(stairSteps, 2);

            float targetDepth = Mathf.Max(stairStepDepth, 0.1f);
            return Mathf.Max(2, Mathf.RoundToInt(length / targetDepth));
        }

        private float GetStairRailStraightExtension(float flatLength, int steps)
        {
            if (flatLength < 0.0001f)
                return 0f;

            float bridgeLikeExtension = GetBridgeStraightExtension(flatLength);
            float stepLength = steps > 0 ? flatLength / steps : flatLength;
            float maxUsableExtension = flatLength * 0.24f;
            return Mathf.Min(bridgeLikeExtension, stepLength, maxUsableExtension);
        }

        // ─────────────────────────────────────────────────────────
        // BRIDGE — flat surface with optional railings
        // ─────────────────────────────────────────────────────────

        private void BuildBridgeMesh()
        {
            var surfaceVerts = new List<Vector3>();
            var surfaceUVs = new List<Vector2>();
            var surfaceTris = new List<int>();
            var wallVerts = new List<Vector3>();
            var wallUVs = new List<Vector2>();
            var wallTris = new List<int>();

            int segments = 8;

            for (int seg = 0; seg < worldPoints.Count - 1; seg++)
            {
                bool isFirstSegment = seg == 0;
                bool isLastSegment = seg == worldPoints.Count - 2;
                // Points are already in local space (grid-parent local)
                Vector3 start = worldPoints[seg];
                Vector3 end = worldPoints[seg + 1];
                Vector3 supportStart = start;
                Vector3 supportEnd = end;
                float startDeckLift = isFirstSegment ? GetBridgeEndpointDeckLift(true) : 0f;
                float endDeckLift = isLastSegment ? GetBridgeEndpointDeckLift(false) : 0f;
                float deckStartBaseY = start.y + startDeckLift;
                float deckEndBaseY = end.y + endDeckLift;

                Vector3 direction = end - start;
                float curveLength = new Vector3(direction.x, 0, direction.z).magnitude;

                Vector3 flatDir = new Vector3(direction.x, 0, direction.z).normalized;
                if (flatDir.sqrMagnitude < 0.001f) flatDir = Vector3.forward;

                Vector3 right = Vector3.Cross(Vector3.up, flatDir).normalized;
                float halfW = bridgeWidth * 0.5f;
                float startStraightExtension = GetBridgeStraightExtension(curveLength, start.y, end.y);
                float endStraightExtension = GetBridgeStraightExtension(curveLength, end.y, start.y);
                NormalizeBridgeStraightExtensions(curveLength, ref startStraightExtension, ref endStraightExtension);
                float totalLength = curveLength;
                Vector3 deckStart = start;

                int straightSegments = curveLength > 0.001f
                    ? Mathf.Max(1, Mathf.RoundToInt(segments * (Mathf.Max(startStraightExtension, endStraightExtension) / curveLength)))
                    : 1;
                int totalSegments = segments + straightSegments * 2;
                int deckSegments = Mathf.Max(totalSegments - 2, 1);
                float baseDeckInset = totalSegments > 0 ? totalLength / totalSegments : 0f;
                float startDeckInset = baseDeckInset * 0.4f;
                float endDeckInset = baseDeckInset * 0.4f;
                bool usesSmoothedPlanarBridge = worldPoints.Count > 2;
                float bridgeStartY = worldPoints.Count > 0 ? worldPoints[0].y : start.y;
                float bridgeEndY = worldPoints.Count > 0 ? worldPoints[worldPoints.Count - 1].y : end.y;

                // Smoothed diagonal bridges should keep the same planar attachment logic
                // regardless of endpoint heights: the real start/end of the bridge touch
                // the cliff edge directly, while only the internal sub-segments stay inset.
                if (usesSmoothedPlanarBridge)
                {
                    if (isFirstSegment)
                        startDeckInset = 0f;
                    if (isLastSegment)
                        endDeckInset = 0f;
                }
                // Non-smoothed bridges still need the higher side to reach the platform edge
                // directly, otherwise a visible gap appears under the upper cliff.
                else if (Mathf.Abs(bridgeStartY - bridgeEndY) > 0.01f)
                {
                    if (isFirstSegment && bridgeStartY > bridgeEndY + 0.01f)
                        startDeckInset = 0f;
                    if (isLastSegment && bridgeEndY > bridgeStartY + 0.01f)
                        endDeckInset = 0f;
                }

                float deckLength = Mathf.Max(totalLength - startDeckInset - endDeckInset, 0.001f);
                Vector3 deckBodyStart = deckStart + flatDir * startDeckInset;

                var deckSampleDistances = new List<float>();
                var deckSampleHeights = new List<float>();
                BuildBridgeDeckSamples(deckStartBaseY, deckEndBaseY, deckLength, startDeckInset, curveLength, startStraightExtension, endStraightExtension, deckSegments,
                    deckSampleDistances, deckSampleHeights);
                ApplyBridgeEntrySlopeBias(flatDir, deckSampleHeights);

                float deckThickness = 0.08f;
                int baseVert = surfaceVerts.Count;
                float startDeckY = deckSampleHeights.Count > 0
                    ? deckSampleHeights[0]
                    : EvaluateBridgeDeckY(start.y, end.y, startDeckInset, curveLength, startStraightExtension, endStraightExtension);
                float endDeckY = deckSampleHeights.Count > 0
                    ? deckSampleHeights[deckSampleHeights.Count - 1]
                    : EvaluateBridgeDeckY(start.y, end.y, startDeckInset + deckLength, curveLength, startStraightExtension, endStraightExtension);
                float startDeckDistance = deckSampleDistances.Count > 0 ? deckSampleDistances[0] : 0f;
                float endDeckDistance = deckSampleDistances.Count > 0 ? deckSampleDistances[deckSampleDistances.Count - 1] : deckLength;
                Vector3 startDeckCenter = deckBodyStart + flatDir * startDeckDistance;
                Vector3 endDeckCenter = deckBodyStart + flatDir * endDeckDistance;

                // Bridge deck (flat surface)
                for (int i = 0; i < deckSampleDistances.Count; i++)
                {
                    float localDistance = deckSampleDistances[i];
                    float t = deckLength > 0.001f ? localDistance / deckLength : 0f;
                    float deckY = deckSampleHeights[i];
                    Vector3 pos = deckBodyStart + flatDir * localDistance;
                    pos.y = deckY;

                    surfaceVerts.Add(pos - right * halfW);
                    surfaceVerts.Add(pos + right * halfW);
                    surfaceUVs.Add(MapBridgeBodyUv(t, 0f));
                    surfaceUVs.Add(MapBridgeBodyUv(t, 1f));

                    if (i > 0)
                    {
                        int v = baseVert + i * 2;
                        surfaceTris.Add(v - 2); surfaceTris.Add(v);     surfaceTris.Add(v - 1);
                        surfaceTris.Add(v - 1); surfaceTris.Add(v);     surfaceTris.Add(v + 1);
                    }
                }

                // Bottom face of the bridge deck
                Vector3 prevBottomLeft = Vector3.zero;
                Vector3 prevBottomRight = Vector3.zero;
                bool hasPrevBottomPair = false;
                for (int i = 0; i < deckSampleDistances.Count; i++)
                {
                    float localDistance = deckSampleDistances[i];
                    float t = deckLength > 0.001f ? localDistance / deckLength : 0f;
                    float deckBottomY = deckSampleHeights[i] - deckThickness;
                    Vector3 pos = deckBodyStart + flatDir * localDistance;
                    pos.y = deckBottomY;

                    Vector3 currentBottomLeft = pos - right * halfW;
                    Vector3 currentBottomRight = pos + right * halfW;

                    if (hasPrevBottomPair)
                    {
                        float prevT = deckLength > 0.001f ? deckSampleDistances[i - 1] / deckLength : 0f;
                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevBottomLeft, prevBottomRight, currentBottomLeft, currentBottomRight,
                            MapBridgeBodyUv(prevT, 0f), MapBridgeBodyUv(prevT, 1f),
                            MapBridgeBodyUv(t, 0f), MapBridgeBodyUv(t, 1f));
                    }

                    prevBottomLeft = currentBottomLeft;
                    prevBottomRight = currentBottomRight;
                    hasPrevBottomPair = true;
                }

                // Side walls connecting the bridge deck back to its underside.
                BuildBridgeSideWalls(
                    deckBodyStart, flatDir, right,
                    deckLength, deckThickness, halfW,
                    deckSampleDistances, deckSampleHeights,
                    wallVerts, wallUVs, wallTris);

                // Front and back caps of the main bridge body
                if (isFirstSegment)
                {
                    float frontY = startDeckY;
                    Vector3 frontCenter = startDeckCenter;
                    Vector3 tl = frontCenter - right * halfW; tl.y = frontY;
                    Vector3 tr = frontCenter + right * halfW; tr.y = frontY;
                    Vector3 bl = tl; bl.y -= deckThickness;
                    Vector3 br = tr; br.y -= deckThickness;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        bl, br, tl, tr,
                        MapBridgeBodyUv(0f, 0f), MapBridgeBodyUv(1f, 0f),
                        MapBridgeBodyUv(0f, 1f), MapBridgeBodyUv(1f, 1f));
                }

                if (isLastSegment)
                {
                    float backY = endDeckY;
                    Vector3 backCenter = endDeckCenter;
                    Vector3 tl = backCenter - right * halfW; tl.y = backY;
                    Vector3 tr = backCenter + right * halfW; tr.y = backY;
                    Vector3 bl = tl; bl.y -= deckThickness;
                    Vector3 br = tr; br.y -= deckThickness;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        bl, br, tl, tr,
                        MapBridgeBodyUv(0f, 0f), MapBridgeBodyUv(1f, 0f),
                        MapBridgeBodyUv(0f, 1f), MapBridgeBodyUv(1f, 1f));
                }

                // Supports at start and end
                if (isFirstSegment)
                {
                    supportStart = startDeckCenter;
                    supportStart.y = start.y;
                    BuildBridgeSupport(supportStart, right, halfW, startDeckY, deckThickness,
                                       wallVerts, wallUVs, wallTris);
                }

                if (isLastSegment)
                {
                    Vector3 endFlat = endDeckCenter;
                    endFlat.y = end.y;
                    BuildBridgeSupport(endFlat, right, halfW, endDeckY, deckThickness,
                                       wallVerts, wallUVs, wallTris);
                }

                // Railings
                if (bridgeRailings)
                {
                    BuildRailings(deckStart, flatDir, right, totalLength, deckStartBaseY, deckEndBaseY, curveLength, startStraightExtension, endStraightExtension, halfW, totalSegments,
                                  wallVerts, wallUVs, wallTris,
                                  isFirstSegment, isLastSegment);
                }
            }

            ApplyMesh2Submeshes(surfaceVerts, surfaceUVs, surfaceTris, wallVerts, wallUVs, wallTris);
        }

        private void BuildBridgeDeckSamples(float startY, float endY, float deckLength, float startDeckInset, float curveLength,
                                            float startStraightExtension, float endStraightExtension, int curvedSegments,
                                            List<float> localDistances, List<float> heights)
        {
            localDistances.Clear();
            heights.Clear();

            if (bridgeProfile == QuickTilemapEditor.BridgeProfile.Stepped)
            {
                int steps = Mathf.Max(bridgeSteps, 2);
                float stepLength = deckLength / steps;

                for (int step = 0; step < steps; step++)
                {
                    float stepStart = stepLength * step;
                    float stepEnd = stepLength * (step + 1);
                    float sampleLocal = (stepStart + stepEnd) * 0.5f;
                    float sampleHeight = EvaluateCurvedBridgeDeckY(
                        startY, endY, startDeckInset + sampleLocal, curveLength, startStraightExtension, endStraightExtension);

                    if (step == 0)
                    {
                        localDistances.Add(stepStart);
                        heights.Add(sampleHeight);
                    }

                    localDistances.Add(stepEnd);
                    heights.Add(sampleHeight);

                    if (step < steps - 1)
                    {
                        float nextSampleLocal = (stepEnd + (stepEnd + stepLength)) * 0.5f;
                        float nextSampleHeight = EvaluateCurvedBridgeDeckY(
                            startY, endY, startDeckInset + nextSampleLocal, curveLength, startStraightExtension, endStraightExtension);
                        localDistances.Add(stepEnd);
                        heights.Add(nextSampleHeight);
                    }
                }

                const float endpointStepTolerance = 0.01f;
                if (localDistances.Count >= 4 &&
                    Mathf.Abs(heights[0] - startY) <= endpointStepTolerance &&
                    localDistances[1] > localDistances[0] + 0.0001f)
                {
                    localDistances.RemoveAt(0);
                    heights.RemoveAt(0);
                }

                int lastIndex = localDistances.Count - 1;
                if (localDistances.Count >= 4 &&
                    Mathf.Abs(heights[lastIndex] - endY) <= endpointStepTolerance &&
                    localDistances[lastIndex] > localDistances[lastIndex - 1] + 0.0001f)
                {
                    localDistances.RemoveAt(lastIndex);
                    heights.RemoveAt(lastIndex);
                }

                return;
            }

            for (int i = 0; i <= curvedSegments; i++)
            {
                float localDistance = deckLength * i / curvedSegments;
                float s = startDeckInset + localDistance;
                localDistances.Add(localDistance);
                heights.Add(EvaluateCurvedBridgeDeckY(
                    startY, endY, s, curveLength, startStraightExtension, endStraightExtension));
            }
        }

        private void ApplyBridgeEntrySlopeBias(Vector3 flatDir, List<float> heights)
        {
            // Bridge endpoints must stay locked to the platform heights.
            // Biasing the first/last samples upward creates visible floating gaps.
            return;
        }

        private static bool ShouldBiasBridgeEntry(Vector3 flatDir)
        {
            if (flatDir.sqrMagnitude < 0.0001f)
                return false;

            Vector3 normalized = flatDir.normalized;
            const float axisThreshold = 0.985f;
            return Mathf.Abs(normalized.x) >= axisThreshold || Mathf.Abs(normalized.z) >= axisThreshold;
        }

        // ─────────────────────────────────────────────────────────
        // Helper: side walls for slope
        // ─────────────────────────────────────────────────────────

        private void BuildSideWalls(List<Vector3> surfVerts, int baseVert, int segments,
                                    float startY, float endY, float halfW,
                                    Vector3 flatDir, Vector3 right,
                                    List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            // The lower Y of the two endpoints — walls go from surface down to this level
            float lowerY = Mathf.Min(startY, endY);

            // Left wall and right wall — double-sided
            for (int side = 0; side < 2; side++)
            {
                for (int i = 0; i < segments; i++)
                {
                    Vector3 topA = surfVerts[baseVert + i * 2 + side];
                    Vector3 topB = surfVerts[baseVert + (i + 1) * 2 + side];
                    Vector3 botA = topA; botA.y = lowerY;
                    Vector3 botB = topB; botB.y = lowerY;
                    float t0 = (float)i / segments;
                    float t1 = (float)(i + 1) / segments;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        botA, botB, topA, topB,
                        new Vector2(t0, 0f), new Vector2(t1, 0f),
                        new Vector2(t0, 1f), new Vector2(t1, 1f));
                }
            }

            // Front and back cap walls (close the ends)
            // Front cap (at start)
            {
                Vector3 tl = surfVerts[baseVert + 0]; // left
                Vector3 tr = surfVerts[baseVert + 1]; // right
                Vector3 bl = tl; bl.y = lowerY;
                Vector3 br = tr; br.y = lowerY;

                AddDoubleSidedQuad(
                    wallVerts, wallUVs, wallTris,
                    bl, br, tl, tr,
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(0f, 1f), new Vector2(1f, 1f));
            }
            // Back cap (at end)
            {
                int endIdx = baseVert + segments * 2;
                Vector3 tl = surfVerts[endIdx + 0];
                Vector3 tr = surfVerts[endIdx + 1];
                Vector3 bl = tl; bl.y = lowerY;
                Vector3 br = tr; br.y = lowerY;

                AddDoubleSidedQuad(
                    wallVerts, wallUVs, wallTris,
                    bl, br, tl, tr,
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(0f, 1f), new Vector2(1f, 1f));
            }

            // Bottom face (connects the bottom edges)
            {
                for (int i = 0; i < segments; i++)
                {
                    Vector3 leftA = surfVerts[baseVert + i * 2 + 0];
                    Vector3 rightA = surfVerts[baseVert + i * 2 + 1];
                    Vector3 leftB = surfVerts[baseVert + (i + 1) * 2 + 0];
                    Vector3 rightB = surfVerts[baseVert + (i + 1) * 2 + 1];
                    leftA.y = lowerY;
                    rightA.y = lowerY;
                    leftB.y = lowerY;
                    rightB.y = lowerY;

                    float t0 = (float)i / segments;
                    float t1 = (float)(i + 1) / segments;
                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        leftA, rightA, leftB, rightB,
                        new Vector2(0f, t0), new Vector2(1f, t0),
                        new Vector2(0f, t1), new Vector2(1f, t1));
                }
            }
        }

        private void BuildSlopeSideSkirts(List<Vector3> surfVerts, int baseVert, int segments,
                                          Vector3 right,
                                          List<Vector3> skirtVerts, List<Vector2> skirtUVs, List<int> skirtTris)
        {
            if (!slopeSideSkirtEnabled ||
                slopeSideSkirtWidth <= 0f ||
                slopeSideSkirtHeight <= 0f ||
                slopeSideSkirtSegments <= 0)
                return;

            float uvScale = Mathf.Max(0.01f, slopeSideSkirtUVScale);
            int rings = Mathf.Max(1, slopeSideSkirtSegments);

            for (int side = 0; side < 2; side++)
            {
                Vector3 outward = (side == 0 ? -right : right).normalized;
                if (outward.sqrMagnitude < 0.0001f)
                    continue;

                var sideUCoords = new float[segments + 1];
                sideUCoords[0] = 0f;
                for (int i = 1; i <= segments; i++)
                {
                    Vector3 prev = surfVerts[baseVert + (i - 1) * 2 + side];
                    Vector3 current = surfVerts[baseVert + i * 2 + side];
                    sideUCoords[i] = sideUCoords[i - 1] + Vector3.Distance(prev, current);
                }

                for (int ring = 0; ring < rings; ring++)
                {
                    float ringTop = (float)ring / rings;
                    float ringBottom = (float)(ring + 1) / rings;

                    float angleTop = ringTop * Mathf.PI * 0.5f;
                    float angleBottom = ringBottom * Mathf.PI * 0.5f;

                    float offsetTop = Mathf.Sin(angleTop) * slopeSideSkirtWidth;
                    float offsetBottom = Mathf.Sin(angleBottom) * slopeSideSkirtWidth;
                    float dropTop = (1f - Mathf.Cos(angleTop)) * slopeSideSkirtHeight;
                    float dropBottom = (1f - Mathf.Cos(angleBottom)) * slopeSideSkirtHeight;

                    float vTop = (1f - ringTop) * uvScale + slopeSideSkirtUVOffsetY;
                    float vBottom = (1f - ringBottom) * uvScale + slopeSideSkirtUVOffsetY;

                    for (int i = 0; i < segments; i++)
                    {
                        float u0 = sideUCoords[i] * uvScale;
                        float u1 = sideUCoords[i + 1] * uvScale;

                        Vector3 topA = surfVerts[baseVert + i * 2 + side] + outward * offsetTop;
                        Vector3 topB = surfVerts[baseVert + (i + 1) * 2 + side] + outward * offsetTop;
                        Vector3 bottomA = surfVerts[baseVert + i * 2 + side] + outward * offsetBottom;
                        Vector3 bottomB = surfVerts[baseVert + (i + 1) * 2 + side] + outward * offsetBottom;

                        topA.y -= dropTop;
                        topB.y -= dropTop;
                        bottomA.y -= dropBottom;
                        bottomB.y -= dropBottom;

                        AddDoubleSidedQuad(
                            skirtVerts, skirtUVs, skirtTris,
                            bottomA, bottomB, topA, topB,
                            new Vector2(u0, vBottom), new Vector2(u1, vBottom),
                            new Vector2(u0, vTop), new Vector2(u1, vTop));
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // Helper: side walls for stairs
        // ─────────────────────────────────────────────────────────

        private void BuildStairSideWalls(Vector3 start, Vector3 end, int steps,
                                         float stepLength, float stepHeight, float halfW,
                                         Vector3 flatDir, Vector3 right,
                                         List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            float lowerY = Mathf.Min(start.y, end.y);
            bool ascending = (end.y - start.y) > 0;
            float length = new Vector3(end.x - start.x, 0, end.z - start.z).magnitude;

            for (int side = 0; side < 2; side++)
            {
                Vector3 sideOffset = (side == 0) ? -right * halfW : right * halfW;

                // Build the stair-step outline as pairs: (position, top) + (position, bottom=lowerY)
                // This creates a continuous wall that follows the step profile on top
                // and stays flat at lowerY on the bottom.
                var profileBottoms = new List<Vector3>();
                var profileTops = new List<Vector3>();
                var profileUs = new List<float>();

                if (!ascending)
                {
                    Vector3 landingBot = start + sideOffset;
                    landingBot.y = lowerY;
                    Vector3 landingTop = landingBot;
                    landingTop.y = start.y;
                    profileBottoms.Add(landingBot);
                    profileTops.Add(landingTop);
                    profileUs.Add(0f);
                }

                for (int s = 0; s < steps; s++)
                {
                    float t0 = (float)s / steps;
                    float t1 = (float)(s + 1) / steps;
                    bool isBottomTread = ascending ? s == 0 : s == steps - 1;

                    float y;
                    if (ascending)
                        y = start.y + stepHeight * s;
                    else
                        y = start.y + stepHeight * (s + 1);

                    Vector3 frontPos = start + flatDir * (length * t0) + sideOffset;
                    Vector3 backPos  = start + flatDir * (length * t1) + sideOffset;

                    // For the first visible step, add the front edge.
                    if (s == 0 && !isBottomTread)
                    {
                        Vector3 vBot = frontPos; vBot.y = lowerY;
                        Vector3 vTop = frontPos; vTop.y = y;
                        profileBottoms.Add(vBot);
                        profileTops.Add(vTop);
                        profileUs.Add(t0);
                    }

                    // Add the vertical transition at the front of each step.
                    if (s > 0)
                    {
                        Vector3 riserBot = frontPos; riserBot.y = lowerY;
                        Vector3 riserTop = frontPos; riserTop.y = y;
                        profileBottoms.Add(riserBot);
                        profileTops.Add(riserTop);
                        profileUs.Add(t0);
                    }

                    // Tread end vertex
                    if (!isBottomTread)
                    {
                        Vector3 vBot = backPos; vBot.y = lowerY;
                        Vector3 vTop = backPos; vTop.y = y;
                        profileBottoms.Add(vBot);
                        profileTops.Add(vTop);
                        profileUs.Add(t1);
                    }
                }

                if (ascending)
                {
                    Vector3 landingBot = start + sideOffset + flatDir * length;
                    landingBot.y = lowerY;
                    Vector3 landingTop = landingBot;
                    landingTop.y = end.y;
                    profileBottoms.Add(landingBot);
                    profileTops.Add(landingTop);
                    profileUs.Add(1f);
                }

                // Triangulate strips between consecutive profile vertex pairs with duplicated vertices
                float heightRange = Mathf.Max(Mathf.Abs(end.y - start.y), 0.01f);
                for (int i = 0; i < profileBottoms.Count - 1; i++)
                {
                    float u0 = profileUs[i];
                    float u1 = profileUs[i + 1];
                    float v0 = (profileTops[i].y - lowerY) / heightRange;
                    float v1 = (profileTops[i + 1].y - lowerY) / heightRange;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        profileBottoms[i], profileBottoms[i + 1],
                        profileTops[i], profileTops[i + 1],
                        new Vector2(u0, 0f), new Vector2(u1, 0f),
                        new Vector2(u0, v0), new Vector2(u1, v1));
                }
            }

            // Front cap wall (closing the front face)
            {
                float frontY = start.y;
                Vector3 tl = start - right * halfW; tl.y = frontY;
                Vector3 tr = start + right * halfW; tr.y = frontY;
                Vector3 bl = tl; bl.y = lowerY;
                Vector3 br = tr; br.y = lowerY;

                if (Mathf.Abs(frontY - lowerY) > 0.001f)
                {
                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        bl, br, tl, tr,
                        new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0f, 1f), new Vector2(1f, 1f));
                }
            }

            // Back cap wall (closing the back face)
            {
                float backY = end.y;
                Vector3 endFlat = start + flatDir * length;
                Vector3 tl = endFlat - right * halfW; tl.y = backY;
                Vector3 tr = endFlat + right * halfW; tr.y = backY;
                Vector3 bl = tl; bl.y = lowerY;
                Vector3 br = tr; br.y = lowerY;

                if (Mathf.Abs(backY - lowerY) > 0.001f)
                {
                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        bl, br, tl, tr,
                        new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0f, 1f), new Vector2(1f, 1f));
                }
            }

            // Bottom face
            {
                float bottomFaceStart = ascending ? stepLength : 0f;
                float bottomFaceEnd = ascending ? length : Mathf.Max(length - stepLength, 0f);

                if (bottomFaceEnd - bottomFaceStart > 0.0001f)
                {
                    Vector3 fl = start + flatDir * bottomFaceStart - right * halfW; fl.y = lowerY;
                    Vector3 fr = start + flatDir * bottomFaceStart + right * halfW; fr.y = lowerY;
                    Vector3 bl = start + flatDir * bottomFaceEnd - right * halfW; bl.y = lowerY;
                    Vector3 br = start + flatDir * bottomFaceEnd + right * halfW; br.y = lowerY;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        fl, fr, bl, br,
                        new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0f, 1f), new Vector2(1f, 1f));
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // Helper: bridge side walls
        // ─────────────────────────────────────────────────────────

        private void BuildBridgeSideWalls(Vector3 deckStart, Vector3 flatDir, Vector3 right,
                                          float deckLength, float thickness, float halfW,
                                          List<float> deckSampleDistances, List<float> deckSampleHeights,
                                          List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            if (deckSampleDistances == null || deckSampleHeights == null)
                return;

            if (deckSampleDistances.Count < 2 || deckSampleDistances.Count != deckSampleHeights.Count)
                return;

            for (int side = 0; side < 2; side++)
            {
                Vector3 sideOffset = (side == 0) ? -right * halfW : right * halfW;
                Vector3 prevTop = deckStart + flatDir * deckSampleDistances[0] + sideOffset;
                prevTop.y = deckSampleHeights[0];
                Vector3 prevBot = prevTop;
                prevBot.y -= thickness;

                for (int i = 1; i < deckSampleDistances.Count; i++)
                {
                    float s = deckSampleDistances[i];
                    float prevT = deckLength > 0.001f ? deckSampleDistances[i - 1] / deckLength : 0f;
                    float t = deckLength > 0.001f ? s / deckLength : 0f;
                    Vector3 top = deckStart + flatDir * s + sideOffset;
                    top.y = deckSampleHeights[i];
                    Vector3 bot = top;
                    bot.y -= thickness;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        prevBot, bot, prevTop, top,
                        MapBridgeBodyUv(prevT, 0f), MapBridgeBodyUv(t, 0f),
                        MapBridgeBodyUv(prevT, 1f), MapBridgeBodyUv(t, 1f));

                    prevBot = bot;
                    prevTop = top;
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // Helper: bridge support columns
        // ─────────────────────────────────────────────────────────

        private void BuildBridgeSupport(Vector3 groundPos, Vector3 right, float halfW,
                                        float bridgeY, float deckThickness,
                                        List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            float supportW = 0.12f;
            float topY = bridgeY - deckThickness;
            float botY = groundPos.y;

            if (topY - botY < 0.05f) return; // Too short, skip

            // Single pillar quad (front and back faces)
            Vector3 bl = groundPos - right * supportW; bl.y = botY;
            Vector3 br = groundPos + right * supportW; br.y = botY;
            Vector3 tl = groundPos - right * supportW; tl.y = topY;
            Vector3 tr = groundPos + right * supportW; tr.y = topY;

            AddDoubleSidedQuad(
                wallVerts, wallUVs, wallTris,
                bl, br, tl, tr,
                MapBridgeBodyUv(0f, 0f), MapBridgeBodyUv(1f, 0f),
                MapBridgeBodyUv(0f, 1f), MapBridgeBodyUv(1f, 1f));
        }

        // ─────────────────────────────────────────────────────────
        // Helper: bridge railings
        // ─────────────────────────────────────────────────────────

        private void BuildRailings(Vector3 deckStart, Vector3 flatDir, Vector3 right,
                                   float totalLength, float startY, float endY, float curveLength,
                                   float startStraightExtension, float endStraightExtension, float halfW, int segments,
                                   List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris,
                                   bool closeStart = true, bool closeEnd = true)
        {
            var railSampleDistances = new List<float>();
            BuildBridgeRailSamples(totalLength, curveLength, startStraightExtension, endStraightExtension, segments, railSampleDistances);
            var railSampleHeights = new List<float>(railSampleDistances.Count);
            bool useCurveFollow = bridgeProfile == QuickTilemapEditor.BridgeProfile.Curved &&
                                  bridgeRailCurveFollow > 0.0001f &&
                                  Mathf.Abs(bridgeCurve) > 0.0001f &&
                                  totalLength > 0.0001f;

            for (int i = 0; i < railSampleDistances.Count; i++)
            {
                float s = railSampleDistances[i];
                railSampleHeights.Add(EvaluateBridgeDeckY(
                    startY, endY, s, curveLength, startStraightExtension, endStraightExtension));
            }

            ApplyBridgeEntrySlopeBias(flatDir, railSampleHeights);
            BuildLinearRailings(deckStart, flatDir, right, totalLength, halfW, segments,
                                railSampleDistances, railSampleHeights, useCurveFollow,
                                wallVerts, wallUVs, wallTris,
                                closeStart, closeEnd);
        }

        private void BuildBridgeRailSamples(float totalLength, float curveLength,
                                            float startStraightExtension, float endStraightExtension, int segments,
                                            List<float> sampleDistances)
        {
            sampleDistances.Clear();

            float effectiveCurveLength = Mathf.Max(curveLength - startStraightExtension - endStraightExtension, 0f);

            if (bridgeProfile == QuickTilemapEditor.BridgeProfile.Stepped && effectiveCurveLength > 0.0001f)
            {
                int steps = Mathf.Max(bridgeSteps, 2);
                float stepLength = effectiveCurveLength / steps;

                sampleDistances.Add(0f);

                if (startStraightExtension > 0.0001f)
                    sampleDistances.Add(startStraightExtension);

                for (int step = 0; step < steps; step++)
                {
                    float boundary = startStraightExtension + stepLength * (step + 1);
                    sampleDistances.Add(boundary);

                    if (step < steps - 1)
                        sampleDistances.Add(boundary);
                }

                if (sampleDistances[sampleDistances.Count - 1] < totalLength)
                    sampleDistances.Add(totalLength);

                return;
            }

            for (int i = 0; i <= segments; i++)
                sampleDistances.Add(totalLength * i / segments);
        }

        private void BuildStairRailings(Vector3 start, Vector3 end, int steps, float halfW,
                                        Vector3 right,
                                        List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            Vector3 railVector = end - start;
            float railLength = railVector.magnitude;
            if (railLength < 0.0001f)
                return;

            Vector3 flatRailVector = railVector;
            flatRailVector.y = 0f;
            float flatLength = flatRailVector.magnitude;
            Vector3 flatDir = flatLength > 0.0001f ? flatRailVector / flatLength : railVector / railLength;
            Vector3 railRight = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.Cross(Vector3.up, flatDir).normalized;
            if (railRight.sqrMagnitude < 0.0001f)
                railRight = Vector3.right;

            float railThickness = Mathf.Max(bridgeRailThickness, 0.02f);
            float halfRailThickness = railThickness * 0.5f;
            float maxEndExtension = steps > 0 ? flatLength / Mathf.Max(steps, 1) : flatLength;
            float endExtension = Mathf.Min(Mathf.Max(bridgeRailEndExtension, 0f), maxEndExtension);
            float straightExtension = GetStairRailStraightExtension(flatLength, steps);
            bool useStraightSegments = straightExtension > 0.0001f && flatLength > straightExtension * 2f + 0.05f;

            if (!useStraightSegments)
            {
                float outwardOffset = Mathf.Max(0f, halfW + halfRailThickness + bridgeRailSpread);
                float lift = halfRailThickness + bridgeRailYOffset;

                for (int side = 0; side < 2; side++)
                {
                    float sideSign = side == 0 ? -1f : 1f;
                    Vector3 sideOffset = railRight * sideSign * outwardOffset;
                    Vector3 baseStart = start + sideOffset + Vector3.up * lift;
                    Vector3 baseEnd = end + sideOffset + Vector3.up * lift;
                    Vector3 railForward = railVector / railLength;
                    Vector3 beamStart = baseStart - railForward * endExtension;
                    Vector3 beamEnd = baseEnd + railForward * endExtension;

                    AddBridgeStyledRailBeam(
                        wallVerts, wallUVs, wallTris,
                        beamStart, beamEnd,
                        railRight, halfRailThickness,
                        side == 0);
                }

                return;
            }

            float totalLength = flatLength + endExtension * 2f;
            if (totalLength < 0.0001f)
                return;

            float upperCornerDistance = endExtension + straightExtension;
            float lowerCornerDistance = totalLength - upperCornerDistance;
            if (lowerCornerDistance <= upperCornerDistance)
                return;

            Vector3 deckStart = start - flatDir * endExtension;
            var railSampleDistances = new List<float>(4)
            {
                0f,
                upperCornerDistance,
                lowerCornerDistance,
                totalLength
            };
            var railSampleHeights = new List<float>(4)
            {
                start.y,
                start.y,
                end.y,
                end.y
            };

            BuildLinearRailings(
                deckStart, flatDir, railRight,
                totalLength, halfW, Mathf.Max(steps + 2, 4),
                railSampleDistances, railSampleHeights, false,
                wallVerts, wallUVs, wallTris);
        }

        private void BuildLinearRailings(Vector3 deckStart, Vector3 flatDir, Vector3 right,
                                         float totalLength, float halfW, int segments,
                                         List<float> railSampleDistances, List<float> railSampleHeights, bool useCurveFollow,
                                         List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris,
                                         bool closeStart = true, bool closeEnd = true)
        {
            if (railSampleDistances == null || railSampleHeights == null ||
                railSampleDistances.Count != railSampleHeights.Count ||
                railSampleDistances.Count < 2)
                return;

            float railThickness = Mathf.Max(bridgeRailThickness, 0.02f);
            float railHeight = railThickness;
            float railHalfThickness = railThickness * 0.5f;
            GetBridgeRailWrapVBounds(railThickness, railHeight, out float railOuterTopV, out float railInnerTopV, out float railInnerBottomV);
            float railLift = bridgeRailYOffset;
            float railOutset = bridgeRailSpread;
            float railEndDrop = Mathf.Max(bridgeRailEndExtension, 0f);
            float endFadeLength = Mathf.Min(totalLength * 0.22f, Mathf.Max(totalLength / Mathf.Max(segments, 1) * 1.5f, 0.2f));
            var railSampleBottomUOffsets = new List<float>(railSampleDistances.Count);

            for (int i = 0; i < railSampleDistances.Count; i++)
            {
                if (!useCurveFollow)
                {
                    railSampleBottomUOffsets.Add(0f);
                    continue;
                }

                int prevIndex = Mathf.Max(i - 1, 0);
                int nextIndex = Mathf.Min(i + 1, railSampleDistances.Count - 1);
                float deltaS = railSampleDistances[nextIndex] - railSampleDistances[prevIndex];
                float slope = deltaS > 0.0001f
                    ? (railSampleHeights[nextIndex] - railSampleHeights[prevIndex]) / deltaS
                    : 0f;
                float distanceToNearestEnd = Mathf.Min(railSampleDistances[i], totalLength - railSampleDistances[i]);
                float endBlend = endFadeLength > 0.0001f
                    ? 1f - Mathf.Clamp01(distanceToNearestEnd / endFadeLength)
                    : 0f;
                float extraBottomDrop = railEndDrop * SmoothStep(endBlend);
                float faceHeight = railHeight + extraBottomDrop;
                railSampleBottomUOffsets.Add(-faceHeight * slope * bridgeRailCurveFollow / totalLength);
            }

            for (int side = 0; side < 2; side++)
            {
                float sideSign = side == 0 ? -1f : 1f;
                Vector3 sideOffset = right * sideSign * (halfW + railOutset);
                bool aIsOuter = sideSign < 0f;
                Vector3 prevBotOuter = Vector3.zero;
                Vector3 prevBotInner = Vector3.zero;
                Vector3 prevTopOuter = Vector3.zero;
                Vector3 prevTopInner = Vector3.zero;
                bool hasPrev = false;

                for (int i = 0; i < railSampleDistances.Count; i++)
                {
                    float s = railSampleDistances[i];
                    float t = totalLength > 0.001f ? s / totalLength : 0f;
                    float deckY = railSampleHeights[i];
                    float distanceToNearestEnd = Mathf.Min(s, totalLength - s);
                    float endBlend = endFadeLength > 0.0001f
                        ? 1f - Mathf.Clamp01(distanceToNearestEnd / endFadeLength)
                        : 0f;
                    float extraBottomDrop = railEndDrop * SmoothStep(endBlend);
                    Vector3 center = deckStart + flatDir * s + sideOffset;
                    Vector3 lateral = right * railHalfThickness;

                    Vector3 botA = center - lateral;
                    Vector3 botB = center + lateral;
                    botA.y = deckY + railLift - extraBottomDrop;
                    botB.y = deckY + railLift - extraBottomDrop;

                    Vector3 topA = center - lateral;
                    Vector3 topB = center + lateral;
                    topA.y = deckY + railLift + railHeight;
                    topB.y = deckY + railLift + railHeight;

                    Vector3 botOuter = aIsOuter ? botA : botB;
                    Vector3 botInner = aIsOuter ? botB : botA;
                    Vector3 topOuter = aIsOuter ? topA : topB;
                    Vector3 topInner = aIsOuter ? topB : topA;

                    if (!hasPrev && closeStart)
                    {
                        AddBridgeRailCapQuad(
                            wallVerts, wallUVs, wallTris,
                            botOuter, botInner, topOuter, topInner,
                            side == 0 ? 0f : 1f,
                            railThickness,
                            railOuterTopV);
                    }

                    if (hasPrev)
                    {
                        float prevT = totalLength > 0.001f ? railSampleDistances[i - 1] / totalLength : 0f;
                        float sidePrevU = side == 0 ? prevT : 1f - prevT;
                        float sideU = side == 0 ? t : 1f - t;
                        float prevBottomWorldU = prevT + railSampleBottomUOffsets[i - 1];
                        float bottomWorldU = t + railSampleBottomUOffsets[i];
                        float sidePrevBottomU = side == 0 ? prevBottomWorldU : 1f - prevBottomWorldU;
                        float sideBottomU = side == 0 ? bottomWorldU : 1f - bottomWorldU;

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevBotOuter, botOuter, prevTopOuter, topOuter,
                            MapBridgeRailUv(sidePrevBottomU, 0f), MapBridgeRailUv(sideBottomU, 0f),
                            MapBridgeRailUv(sidePrevU, railOuterTopV), MapBridgeRailUv(sideU, railOuterTopV));

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevBotInner, botInner, prevTopInner, topInner,
                            MapBridgeRailUv(sidePrevBottomU, 0f), MapBridgeRailUv(sideBottomU, 0f),
                            MapBridgeRailUv(sidePrevU, railOuterTopV), MapBridgeRailUv(sideU, railOuterTopV));

                        Vector3 prevTopMid = Vector3.Lerp(prevTopOuter, prevTopInner, 0.5f);
                        Vector3 topMid = Vector3.Lerp(topOuter, topInner, 0.5f);

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevTopOuter, prevTopMid, topOuter, topMid,
                            MapBridgeRailUv(sidePrevU, railOuterTopV), MapBridgeRailUv(sidePrevU, railInnerTopV),
                            MapBridgeRailUv(sideU, railOuterTopV), MapBridgeRailUv(sideU, railInnerTopV));

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevTopMid, prevTopInner, topMid, topInner,
                            MapBridgeRailUv(sidePrevU, railInnerTopV), MapBridgeRailUv(sidePrevU, railOuterTopV),
                            MapBridgeRailUv(sideU, railInnerTopV), MapBridgeRailUv(sideU, railOuterTopV));

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevBotOuter, prevBotInner, botOuter, botInner,
                            MapBridgeRailUv(prevT, 1f), MapBridgeRailUv(prevT, railInnerBottomV),
                            MapBridgeRailUv(t, 1f), MapBridgeRailUv(t, railInnerBottomV));
                    }

                    prevBotOuter = botOuter;
                    prevBotInner = botInner;
                    prevTopOuter = topOuter;
                    prevTopInner = topInner;
                    hasPrev = true;
                }

                if (closeEnd)
                {
                    AddBridgeRailCapQuad(
                        wallVerts, wallUVs, wallTris,
                        prevBotOuter, prevBotInner, prevTopOuter, prevTopInner,
                        side == 0 ? 1f : 0f,
                        railThickness,
                        railOuterTopV);
                }
            }
        }

        private void BuildBridgeLowerRails(Vector3 deckStart, Vector3 flatDir, Vector3 right,
                                           float totalLength, float startY, float endY, float curveLength,
                                           float startStraightExtension, float endStraightExtension, float halfW, int segments, float deckThickness,
                                           List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            float beamHeight = Mathf.Max(bridgeRailThickness * 0.55f, 0.03f);

            for (int side = 0; side < 2; side++)
            {
                float sideSign = side == 0 ? -1f : 1f;
                Vector3 sideOffset = right * sideSign * (halfW + 0.012f);
                Vector3 prevBottom = Vector3.zero;
                Vector3 prevTop = Vector3.zero;
                bool hasPrev = false;

                for (int i = 0; i <= segments; i++)
                {
                    float s = totalLength * i / segments;
                    float t = totalLength > 0.001f ? s / totalLength : 0f;
                    Vector3 center = deckStart + flatDir * s + sideOffset;

                    float topY = EvaluateBridgeLowerBeamTopY(
                        startY, endY, s, totalLength, curveLength, startStraightExtension, endStraightExtension, deckThickness);
                    float bottomY = topY - beamHeight;

                    Vector3 bottom = center;
                    bottom.y = bottomY;
                    Vector3 top = center;
                    top.y = topY;

                    if (hasPrev)
                    {
                        float prevT = (float)(i - 1) / segments;

                        AddDoubleSidedQuad(
                            wallVerts, wallUVs, wallTris,
                            prevBottom, bottom, prevTop, top,
                            new Vector2(prevT, 0f), new Vector2(t, 0f),
                            new Vector2(prevT, 1f), new Vector2(t, 1f));
                    }

                    prevBottom = bottom;
                    prevTop = top;
                    hasPrev = true;
                }
            }
        }

        private void BuildBridgePosts(Vector3 deckStart, Vector3 flatDir, Vector3 right,
                                      float totalLength, float startY, float endY, float curveLength,
                                      float startStraightExtension, float endStraightExtension, float halfW, int segments, float deckThickness,
                                      List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            float railThickness = Mathf.Max(bridgeRailThickness, 0.02f);
            float lowerBeamHeight = Mathf.Max(bridgeRailThickness * 0.55f, 0.03f);
            float postHalfForward = Mathf.Max(railThickness * 0.18f, 0.03f);
            int postCount = Mathf.Max(4, Mathf.RoundToInt(totalLength / 0.65f) + 1);

            for (int side = 0; side < 2; side++)
            {
                float sideSign = side == 0 ? -1f : 1f;
                Vector3 sideOffset = right * sideSign * (halfW + 0.012f);
                Vector3 forwardOffset = flatDir * postHalfForward;

                for (int i = 0; i < postCount; i++)
                {
                    float alpha = postCount > 1 ? (float)i / (postCount - 1) : 0f;
                    float s = totalLength * alpha;
                    float deckY = EvaluateBridgeDeckY(
                        startY, endY, s, curveLength, startStraightExtension, endStraightExtension);
                    float beamTopY = EvaluateBridgeLowerBeamTopY(
                        startY, endY, s, totalLength, curveLength, startStraightExtension, endStraightExtension, deckThickness);

                    float topY = deckY + railThickness * 0.9f;
                    float bottomY = beamTopY + lowerBeamHeight * 0.05f;
                    if (topY - bottomY < 0.03f)
                        continue;

                    Vector3 center = deckStart + flatDir * s + sideOffset;
                    Vector3 bl = center - forwardOffset;
                    Vector3 br = center + forwardOffset;
                    Vector3 tl = bl;
                    Vector3 tr = br;
                    bl.y = bottomY;
                    br.y = bottomY;
                    tl.y = topY;
                    tr.y = topY;

                    AddDoubleSidedQuad(
                        wallVerts, wallUVs, wallTris,
                        bl, br, tl, tr,
                        new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0f, 1f), new Vector2(1f, 1f));
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // Utility
        // ─────────────────────────────────────────────────────────

        private void ApplyMesh2Submeshes(List<Vector3> surfVerts, List<Vector2> surfUVs, List<int> surfTris,
                                         List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris)
        {
            int surfCount = surfVerts.Count;
            int wallCount = wallVerts.Count;

            var allVerts = new Vector3[surfCount + wallCount];
            var allUVs = new Vector2[surfCount + wallCount];

            surfVerts.CopyTo(allVerts, 0);
            wallVerts.CopyTo(allVerts, surfCount);
            surfUVs.CopyTo(allUVs, 0);
            wallUVs.CopyTo(allUVs, surfCount);

            // Offset wall triangle indices
            var wallTrisOffset = new int[wallTris.Count];
            for (int i = 0; i < wallTris.Count; i++)
                wallTrisOffset[i] = wallTris[i] + surfCount;

            _mesh.vertices = allVerts;
            _mesh.uv = allUVs;
            _mesh.subMeshCount = 2;
            _mesh.SetTriangles(surfTris.ToArray(), 0);
            _mesh.SetTriangles(wallTrisOffset, 1);
        }

        private void ApplyMeshWithOptionalExtraSubmesh(List<Vector3> surfVerts, List<Vector2> surfUVs, List<int> surfTris,
                                                       List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris,
                                                       List<Vector3> extraVerts, List<Vector2> extraUVs, List<int> extraTris)
        {
            if (extraVerts == null || extraVerts.Count == 0 || extraTris == null || extraTris.Count == 0)
            {
                ApplyMesh2Submeshes(surfVerts, surfUVs, surfTris, wallVerts, wallUVs, wallTris);
                return;
            }

            int surfCount = surfVerts.Count;
            int wallCount = wallVerts.Count;
            int extraCount = extraVerts.Count;

            var allVerts = new Vector3[surfCount + wallCount + extraCount];
            var allUVs = new Vector2[surfCount + wallCount + extraCount];

            surfVerts.CopyTo(allVerts, 0);
            wallVerts.CopyTo(allVerts, surfCount);
            extraVerts.CopyTo(allVerts, surfCount + wallCount);
            surfUVs.CopyTo(allUVs, 0);
            wallUVs.CopyTo(allUVs, surfCount);
            extraUVs.CopyTo(allUVs, surfCount + wallCount);

            var wallTrisOffset = new int[wallTris.Count];
            for (int i = 0; i < wallTris.Count; i++)
                wallTrisOffset[i] = wallTris[i] + surfCount;

            var extraTrisOffset = new int[extraTris.Count];
            for (int i = 0; i < extraTris.Count; i++)
                extraTrisOffset[i] = extraTris[i] + surfCount + wallCount;

            _mesh.vertices = allVerts;
            _mesh.uv = allUVs;
            _mesh.subMeshCount = 3;
            _mesh.SetTriangles(surfTris.ToArray(), 0);
            _mesh.SetTriangles(wallTrisOffset, 1);
            _mesh.SetTriangles(extraTrisOffset, 2);
        }

        private void ApplyMeshWithOptionalSkirt(List<Vector3> surfVerts, List<Vector2> surfUVs, List<int> surfTris,
                                                List<Vector3> wallVerts, List<Vector2> wallUVs, List<int> wallTris,
                                                List<Vector3> skirtVerts, List<Vector2> skirtUVs, List<int> skirtTris)
        {
            if (skirtVerts == null || skirtVerts.Count == 0 || skirtTris == null || skirtTris.Count == 0)
            {
                ApplyMesh2Submeshes(surfVerts, surfUVs, surfTris, wallVerts, wallUVs, wallTris);
                return;
            }

            int surfCount = surfVerts.Count;
            int wallCount = wallVerts.Count;
            int skirtCount = skirtVerts.Count;

            var allVerts = new Vector3[surfCount + wallCount + skirtCount];
            var allUVs = new Vector2[surfCount + wallCount + skirtCount];

            surfVerts.CopyTo(allVerts, 0);
            wallVerts.CopyTo(allVerts, surfCount);
            skirtVerts.CopyTo(allVerts, surfCount + wallCount);
            surfUVs.CopyTo(allUVs, 0);
            wallUVs.CopyTo(allUVs, surfCount);
            skirtUVs.CopyTo(allUVs, surfCount + wallCount);

            var wallTrisOffset = new int[wallTris.Count];
            for (int i = 0; i < wallTris.Count; i++)
                wallTrisOffset[i] = wallTris[i] + surfCount;

            var skirtTrisOffset = new int[skirtTris.Count];
            for (int i = 0; i < skirtTris.Count; i++)
                skirtTrisOffset[i] = skirtTris[i] + surfCount + wallCount;

            _mesh.vertices = allVerts;
            _mesh.uv = allUVs;
            _mesh.subMeshCount = 3;
            _mesh.SetTriangles(surfTris.ToArray(), 0);
            _mesh.SetTriangles(wallTrisOffset, 1);
            _mesh.SetTriangles(skirtTrisOffset, 2);
        }

        private static void AddDoubleSidedQuad(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                               Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr,
                                               Vector2 uvBL, Vector2 uvBR, Vector2 uvTL, Vector2 uvTR)
        {
            int front = verts.Count;
            verts.Add(bl); uvs.Add(uvBL);
            verts.Add(br); uvs.Add(uvBR);
            verts.Add(tl); uvs.Add(uvTL);
            verts.Add(tr); uvs.Add(uvTR);

            tris.Add(front);     tris.Add(front + 2); tris.Add(front + 1);
            tris.Add(front + 1); tris.Add(front + 2); tris.Add(front + 3);

            int back = verts.Count;
            verts.Add(bl); uvs.Add(uvBL);
            verts.Add(br); uvs.Add(uvBR);
            verts.Add(tl); uvs.Add(uvTL);
            verts.Add(tr); uvs.Add(uvTR);

            tris.Add(back);     tris.Add(back + 1); tris.Add(back + 2);
            tris.Add(back + 1); tris.Add(back + 3); tris.Add(back + 2);
        }

        private static void AddOrientedBox(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                           Vector3 center,
                                           Vector3 forwardAxis, float halfForward,
                                           Vector3 upAxis, float halfUp,
                                           Vector3 rightAxis, float halfRight)
        {
            Vector3 forward = forwardAxis.normalized * halfForward;
            Vector3 up = upAxis.normalized * halfUp;
            Vector3 right = rightAxis.normalized * halfRight;

            Vector3 lbb = center - right - up - forward;
            Vector3 rbb = center + right - up - forward;
            Vector3 lbf = center - right - up + forward;
            Vector3 rbf = center + right - up + forward;
            Vector3 ltb = center - right + up - forward;
            Vector3 rtb = center + right + up - forward;
            Vector3 ltf = center - right + up + forward;
            Vector3 rtf = center + right + up + forward;

            AddDoubleSidedQuad(verts, uvs, tris, lbf, rbf, ltf, rtf,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            AddDoubleSidedQuad(verts, uvs, tris, rbb, lbb, rtb, ltb,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            AddDoubleSidedQuad(verts, uvs, tris, rbf, rbb, rtf, rtb,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            AddDoubleSidedQuad(verts, uvs, tris, lbb, lbf, ltb, ltf,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            AddDoubleSidedQuad(verts, uvs, tris, ltb, rtb, ltf, rtf,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
            AddDoubleSidedQuad(verts, uvs, tris, lbf, rbf, lbb, rbb,
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f));
        }

        private void AddBridgeStyledRailBeam(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                             Vector3 beamStart, Vector3 beamEnd,
                                             Vector3 rightAxis, float halfThickness,
                                             bool isLeftRail,
                                             float startU = 0f, float endU = 1f,
                                             bool addBackCap = true, bool addFrontCap = true)
        {
            Vector3 beamVector = beamEnd - beamStart;
            float beamLength = beamVector.magnitude;
            if (beamLength < 0.0001f)
                return;

            Vector3 forwardAxis = beamVector / beamLength;
            Vector3 safeRightAxis = rightAxis.sqrMagnitude > 0.0001f
                ? rightAxis.normalized
                : Vector3.Cross(Vector3.up, forwardAxis).normalized;
            if (safeRightAxis.sqrMagnitude < 0.0001f)
                safeRightAxis = Vector3.right;

            Vector3 upAxis = Vector3.Cross(forwardAxis, safeRightAxis).normalized;
            if (upAxis.sqrMagnitude < 0.0001f)
                upAxis = Vector3.up;

            Vector3 center = (beamStart + beamEnd) * 0.5f;
            float halfForward = beamLength * 0.5f;
            Vector3 forward = forwardAxis * halfForward;
            Vector3 up = upAxis * halfThickness;
            Vector3 right = safeRightAxis * halfThickness;

            Vector3 lbb = center - right - up - forward;
            Vector3 rbb = center + right - up - forward;
            Vector3 lbf = center - right - up + forward;
            Vector3 rbf = center + right - up + forward;
            Vector3 ltb = center - right + up - forward;
            Vector3 rtb = center + right + up - forward;
            Vector3 ltf = center - right + up + forward;
            Vector3 rtf = center + right + up + forward;

            float railThickness = halfThickness * 2f;
            float railHeight = railThickness;
            GetBridgeRailWrapVBounds(railThickness, railHeight, out float railOuterTopV, out float railInnerTopV, out float railInnerBottomV);

            Vector3 backBottomOuter = isLeftRail ? lbb : rbb;
            Vector3 backBottomInner = isLeftRail ? rbb : lbb;
            Vector3 frontBottomOuter = isLeftRail ? lbf : rbf;
            Vector3 frontBottomInner = isLeftRail ? rbf : lbf;
            Vector3 backTopOuter = isLeftRail ? ltb : rtb;
            Vector3 backTopInner = isLeftRail ? rtb : ltb;
            Vector3 frontTopOuter = isLeftRail ? ltf : rtf;
            Vector3 frontTopInner = isLeftRail ? rtf : ltf;

            Vector3 backTopMid = Vector3.Lerp(backTopOuter, backTopInner, 0.5f);
            Vector3 frontTopMid = Vector3.Lerp(frontTopOuter, frontTopInner, 0.5f);

            float sideStartU = isLeftRail ? startU : 1f - startU;
            float sideEndU = isLeftRail ? endU : 1f - endU;

            if (addBackCap)
            {
                AddBridgeRailCapQuad(
                    verts, uvs, tris,
                    backBottomOuter, backBottomInner, backTopOuter, backTopInner,
                    sideStartU,
                    railThickness,
                    railOuterTopV);
            }

            AddDoubleSidedQuad(
                verts, uvs, tris,
                backBottomOuter, frontBottomOuter, backTopOuter, frontTopOuter,
                MapBridgeRailUv(sideStartU, 0f), MapBridgeRailUv(sideEndU, 0f),
                MapBridgeRailUv(sideStartU, railOuterTopV), MapBridgeRailUv(sideEndU, railOuterTopV));

            AddDoubleSidedQuad(
                verts, uvs, tris,
                backBottomInner, frontBottomInner, backTopInner, frontTopInner,
                MapBridgeRailUv(sideStartU, 0f), MapBridgeRailUv(sideEndU, 0f),
                MapBridgeRailUv(sideStartU, railOuterTopV), MapBridgeRailUv(sideEndU, railOuterTopV));

            AddDoubleSidedQuad(
                verts, uvs, tris,
                backTopOuter, backTopMid, frontTopOuter, frontTopMid,
                MapBridgeRailUv(sideStartU, railOuterTopV), MapBridgeRailUv(sideStartU, railInnerTopV),
                MapBridgeRailUv(sideEndU, railOuterTopV), MapBridgeRailUv(sideEndU, railInnerTopV));

            AddDoubleSidedQuad(
                verts, uvs, tris,
                backTopMid, backTopInner, frontTopMid, frontTopInner,
                MapBridgeRailUv(sideStartU, railInnerTopV), MapBridgeRailUv(sideStartU, railOuterTopV),
                MapBridgeRailUv(sideEndU, railInnerTopV), MapBridgeRailUv(sideEndU, railOuterTopV));

            AddDoubleSidedQuad(
                verts, uvs, tris,
                backBottomOuter, backBottomInner, frontBottomOuter, frontBottomInner,
                MapBridgeRailUv(startU, 1f), MapBridgeRailUv(startU, railInnerBottomV),
                MapBridgeRailUv(endU, 1f), MapBridgeRailUv(endU, railInnerBottomV));

            if (addFrontCap)
            {
                AddBridgeRailCapQuad(
                    verts, uvs, tris,
                    frontBottomOuter, frontBottomInner, frontTopOuter, frontTopInner,
                    sideEndU,
                    railThickness,
                    railOuterTopV);
            }
        }

        private void AddBridgeRailCapQuad(List<Vector3> verts, List<Vector2> uvs, List<int> tris,
                                          Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr,
                                          float longitudinalU, float railThickness, float railOuterTopV)
        {
            GetBridgeRailCapURange(longitudinalU, railThickness, out float uMin, out float uMax);

            AddDoubleSidedQuad(
                verts, uvs, tris,
                bl, br, tl, tr,
                MapBridgeRailUv(uMin, 0f), MapBridgeRailUv(uMax, 0f),
                MapBridgeRailUv(uMin, railOuterTopV), MapBridgeRailUv(uMax, railOuterTopV));
        }

        private void GetBridgeRailCapURange(float longitudinalU, float railThickness, out float uMin, out float uMax)
        {
            float referenceWidth = Mathf.Max(bridgeWidth, railThickness * 2f, 0.0001f);
            float sliceWidth = Mathf.Clamp((railThickness / referenceWidth) * 0.75f, 0.05f, 0.16f);
            float halfSlice = sliceWidth * 0.5f;

            uMin = longitudinalU - halfSlice;
            uMax = longitudinalU + halfSlice;

            if (uMin < 0f)
            {
                uMax = Mathf.Min(1f, uMax - uMin);
                uMin = 0f;
            }

            if (uMax > 1f)
            {
                uMin = Mathf.Max(0f, uMin - (uMax - 1f));
                uMax = 1f;
            }
        }

        private static Vector2 MapBridgeBodyUv(float u, float v)
        {
            return RemapUv(BridgeBodyUvRect, u, v);
        }

        private Vector2 MapBridgeRailUv(float u, float v)
        {
            Vector2 uv = RemapUv(BridgeRailUvRect, u, v);
            uv.y += bridgeRailUvOffsetY;
            return uv;
        }

        private void GetBridgeBodyWrapVBounds(float thickness, out float topLeftV, out float topRightV)
        {
            float wrappedSpan = Mathf.Max(bridgeWidth + thickness * 2f, 0.0001f);
            topLeftV = thickness / wrappedSpan;
            topRightV = (thickness + bridgeWidth) / wrappedSpan;
        }

        private void GetBridgeRailWrapVBounds(float railThickness, float railHeight, out float outerTopV, out float innerTopV, out float innerBottomV)
        {
            float wrappedSpan = Mathf.Max(railHeight * 2f + railThickness * 2f, 0.0001f);
            outerTopV = railHeight / wrappedSpan;
            innerTopV = (railHeight + railThickness) / wrappedSpan;
            innerBottomV = (railHeight * 2f + railThickness) / wrappedSpan;
        }

        private static Vector2 RemapUv(Rect rect, float u, float v)
        {
            return new Vector2(
                Mathf.LerpUnclamped(rect.xMin, rect.xMax, u),
                Mathf.LerpUnclamped(rect.yMin, rect.yMax, v));
        }

        private void ExpandSegmentEndpoints(int segmentIndex, ref Vector3 start, ref Vector3 end)
        {
            if (endpointPadding <= 0f)
                return;

            Vector3 flat = end - start;
            flat.y = 0f;
            float flatLen = flat.magnitude;
            if (flatLen < 0.0001f)
                return;

            Vector3 flatDir = flat / flatLen;

            if (segmentIndex == 0)
                start += flatDir * endpointPadding;

            if (segmentIndex == worldPoints.Count - 2)
                end -= flatDir * endpointPadding;
        }

        private float EvaluateBridgeDeckY(float startY, float endY, float distanceAlongDeck, float curveLength,
                                          float startStraightExtension, float endStraightExtension)
        {
            float effectiveCurveLength = Mathf.Max(curveLength - startStraightExtension - endStraightExtension, 0f);

            if (bridgeProfile == QuickTilemapEditor.BridgeProfile.Stepped)
            {
                if (effectiveCurveLength < 0.0001f)
                    return EvaluateCurvedBridgeDeckY(startY, endY, distanceAlongDeck, curveLength, startStraightExtension, endStraightExtension);

                int steps = Mathf.Max(bridgeSteps, 2);
                float curveDistance = Mathf.Clamp(distanceAlongDeck - startStraightExtension, 0f, effectiveCurveLength);
                float stepLength = effectiveCurveLength / steps;
                int stepIndex = Mathf.Min(Mathf.FloorToInt(curveDistance / stepLength), steps - 1);
                float stepStart = stepLength * stepIndex;
                float stepEnd = stepLength * (stepIndex + 1);
                float sampleDistance = (stepStart + stepEnd) * 0.5f;
                return EvaluateCurvedBridgeDeckY(
                    startY, endY, startStraightExtension + sampleDistance, curveLength, startStraightExtension, endStraightExtension);
            }

            return EvaluateCurvedBridgeDeckY(startY, endY, distanceAlongDeck, curveLength, startStraightExtension, endStraightExtension);
        }

        private float EvaluateCurvedBridgeDeckY(float startY, float endY, float distanceAlongDeck, float curveLength,
                                                float startStraightExtension, float endStraightExtension)
        {
            float totalLength = curveLength;
            float effectiveCurveLength = Mathf.Max(curveLength - startStraightExtension - endStraightExtension, 0f);

            if (totalLength <= 0.0001f)
                return startY;

            float clampedDistance = Mathf.Clamp(distanceAlongDeck, 0f, totalLength);

            if (effectiveCurveLength <= 0.0001f)
            {
                float totalU = clampedDistance / totalLength;
                return Mathf.Lerp(startY, endY, totalU);
            }

            if (clampedDistance <= startStraightExtension)
                return startY;

            if (clampedDistance >= totalLength - endStraightExtension)
                return endY;

            float curveDistance = Mathf.Clamp(clampedDistance - startStraightExtension, 0f, effectiveCurveLength);
            float u = curveDistance / effectiveCurveLength;
            float baseY = Mathf.Lerp(startY, endY, SmoothStep(u));

            if (Mathf.Abs(bridgeCurve) < 0.0001f)
                return baseY;

            float curveOffset = 4f * u * (1f - u) * bridgeCurve;
            return baseY + curveOffset;
        }

        private float EvaluateBridgeLowerBeamTopY(float startY, float endY, float distanceAlongDeck, float totalLength, float curveLength,
                                                  float startStraightExtension, float endStraightExtension, float deckThickness)
        {
            float deckY = EvaluateBridgeDeckY(startY, endY, distanceAlongDeck, curveLength, startStraightExtension, endStraightExtension);
            float railThickness = Mathf.Max(bridgeRailThickness, 0.02f);

            if (totalLength < 0.0001f)
                return deckY - deckThickness - railThickness;

            float u = Mathf.Clamp01(distanceAlongDeck / totalLength);
            float archT = 4f * u * (1f - u);
            float endBlend = 1f - archT;
            float endDrop = deckThickness * 1.2f + railThickness * 1.8f + Mathf.Max(bridgeHeight * 0.45f, 0.06f);
            float centerDrop = deckThickness * 0.8f + railThickness * 1.1f + Mathf.Max(bridgeHeight * 0.16f, 0.03f);
            float extraEndDrop = Mathf.Max(bridgeRailEndExtension, 0f) * SmoothStep(endBlend);
            float drop = Mathf.Lerp(endDrop, centerDrop, archT) + extraEndDrop;

            return deckY - drop;
        }

        private float GetBridgeStraightExtension(float curveLength)
        {
            float requestedExtension = endpointPadding > 0f ? endpointPadding : 0.5f;
            return Mathf.Min(requestedExtension, curveLength * 0.24f);
        }

        private float GetBridgeEndpointDeckLift(bool isStartEndpoint)
        {
            if (worldPoints == null || worldPoints.Count < 2)
                return 0f;

            int endpointIndex = isStartEndpoint ? 0 : worldPoints.Count - 1;
            int neighborIndex = isStartEndpoint ? 1 : worldPoints.Count - 2;
            if (endpointIndex < 0 || endpointIndex >= worldPoints.Count ||
                neighborIndex < 0 || neighborIndex >= worldPoints.Count)
                return 0f;

            return worldPoints[endpointIndex].y > worldPoints[neighborIndex].y + 0.01f
                ? BridgeHighEndpointTopLift
                : 0f;
        }

        private float GetBridgeStraightExtension(float curveLength, float endpointY, float oppositeY)
        {
            if (curveLength < 0.0001f)
                return 0f;

            float baseExtension = GetBridgeStraightExtension(curveLength);
            float heightDelta = Mathf.Max(endpointY - oppositeY, 0f);
            if (heightDelta <= 0.01f)
                return baseExtension;

            float relativeSlope = heightDelta / Mathf.Max(curveLength, 0.0001f);
            float bonus = curveLength * Mathf.Clamp(relativeSlope * 0.35f, 0f, 0.18f);
            return Mathf.Min(baseExtension + bonus, curveLength * 0.42f);
        }

        private static void NormalizeBridgeStraightExtensions(float curveLength, ref float startStraightExtension, ref float endStraightExtension)
        {
            if (curveLength < 0.0001f)
            {
                startStraightExtension = 0f;
                endStraightExtension = 0f;
                return;
            }

            startStraightExtension = Mathf.Clamp(startStraightExtension, 0f, curveLength);
            endStraightExtension = Mathf.Clamp(endStraightExtension, 0f, curveLength);

            float maxTotalStraight = curveLength * 0.72f;
            float totalStraight = startStraightExtension + endStraightExtension;
            if (totalStraight <= maxTotalStraight || totalStraight <= 0.0001f)
                return;

            float scale = maxTotalStraight / totalStraight;
            startStraightExtension *= scale;
            endStraightExtension *= scale;
        }

        private float GetBridgeFrameInset(float totalLength, int segments)
        {
            if (totalLength < 0.0001f)
                return 0f;

            float bySegment = totalLength / Mathf.Max(segments, 1) * 0.95f;
            float byLength = totalLength * 0.16f;
            return Mathf.Min(bySegment, byLength);
        }

        private static float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }

        private void ClearMesh()
        {
            if (_mesh != null)
            {
                _mesh.Clear();
                if (_filter != null) _filter.sharedMesh = _mesh;
            }

            if (_meshCollider != null)
                _meshCollider.sharedMesh = null;
        }

        private void OnDestroy()
        {
            if (_mesh != null)
            {
#if UNITY_EDITOR
                DestroyImmediate(_mesh);
#else
                Destroy(_mesh);
#endif
                _mesh = null;
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawSelectedGizmos)
                return;

            if (worldPoints == null || worldPoints.Count < 2) return;

            Gizmos.color = pathType == QuickTilemapEditor.PathType.Bridge ? Color.cyan :
                           pathType == QuickTilemapEditor.PathType.Stairs ? Color.magenta : Color.yellow;

            // Points are in local space — convert to world for Gizmos
            for (int i = 0; i < worldPoints.Count - 1; i++)
                Gizmos.DrawLine(transform.TransformPoint(worldPoints[i]),
                                transform.TransformPoint(worldPoints[i + 1]));

            foreach (var pt in worldPoints)
                Gizmos.DrawWireSphere(transform.TransformPoint(pt), 0.12f);
        }
#endif
    }
}
