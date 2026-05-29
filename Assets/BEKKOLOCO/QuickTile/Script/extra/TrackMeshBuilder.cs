// TrackMeshBuilder.cs — Generates a 3D ribbon mesh along path points.
// Points can snap to the deformed ground, and per-point rotation twists the mesh cross-section.

using System.Collections.Generic;
using UnityEngine;

namespace Bekkoloco
{
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class TrackMeshBuilder : MonoBehaviour
    {
        // ── Public config (set from QuickTilemapEditor) ──
        [Header("Path Points (world space)")]
        public List<TrackPointWorld> worldPoints = new List<TrackPointWorld>();

        [Header("Mesh Settings")]
        [Tooltip("Number of interpolated segments between each pair of control points.")]
        [Range(1, 20)] public int subdivisions = 4;

        [Tooltip("Default track width (can be overridden per-point).")]
        public float defaultWidth = 1f;

        [Tooltip("Small Y offset above the ground to prevent z-fighting.")]
        public float groundOffset = 0.02f;

        [Tooltip("UV tiling along the track length.")]
        public float uvTilingY = 1f;

        [Header("Ground Snap")]
        [Tooltip("Layer mask for ground raycast.")]
        public LayerMask groundLayer = ~0;

        [Tooltip("Max raycast distance.")]
        public float raycastDistance = 50f;

        // ── Internal ──
        private Mesh _mesh;
        private MeshFilter _filter;
        private bool _dirty = true;

        [System.Serializable]
        public class TrackPointWorld
        {
            public Vector3 position;
            public bool snapToGround = true;
            public float rotation = 0f; // degrees around forward axis
            public float width = 1f;
        }

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
        }

        private void OnEnable()
        {
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

        /// <summary>
        /// Call this whenever path data changes.
        /// </summary>
        public void MarkDirty()
        {
            _dirty = true;
        }

        /// <summary>
        /// Set up all points from the path data and rebuild.
        /// </summary>
        public void SetPoints(List<TrackPointWorld> points)
        {
            worldPoints = points ?? new List<TrackPointWorld>();
            _dirty = true;
        }

        // ──────────────────────────────────────────────────────────────
        // Mesh generation
        // ──────────────────────────────────────────────────────────────

        private void RebuildMesh()
        {
            if (_filter == null) _filter = GetComponent<MeshFilter>();

            if (worldPoints == null || worldPoints.Count < 2)
            {
                ClearMesh();
                return;
            }

            // 1. Snap points to ground if needed
            List<Vector3> snappedPositions = new List<Vector3>(worldPoints.Count);
            List<float> rotations = new List<float>(worldPoints.Count);
            List<float> widths = new List<float>(worldPoints.Count);

            for (int i = 0; i < worldPoints.Count; i++)
            {
                var pt = worldPoints[i];
                Vector3 pos = pt.position;

                if (pt.snapToGround)
                {
                    pos = SnapToGround(pos);
                }

                pos.y += groundOffset;
                snappedPositions.Add(pos);
                rotations.Add(pt.rotation);
                widths.Add(pt.width > 0.001f ? pt.width : defaultWidth);
            }

            // 2. Generate interpolated spline points
            List<Vector3> splinePos = new List<Vector3>();
            List<float> splineRot = new List<float>();
            List<float> splineWidth = new List<float>();

            for (int i = 0; i < snappedPositions.Count - 1; i++)
            {
                Vector3 p0 = (i > 0) ? snappedPositions[i - 1] : snappedPositions[i];
                Vector3 p1 = snappedPositions[i];
                Vector3 p2 = snappedPositions[i + 1];
                Vector3 p3 = (i + 2 < snappedPositions.Count) ? snappedPositions[i + 2] : snappedPositions[i + 1];

                float r1 = rotations[i];
                float r2 = rotations[i + 1];
                float w1 = widths[i];
                float w2 = widths[i + 1];

                for (int s = 0; s < subdivisions; s++)
                {
                    float t = (float)s / subdivisions;
                    splinePos.Add(CatmullRom(p0, p1, p2, p3, t));
                    splineRot.Add(Mathf.Lerp(r1, r2, t));
                    splineWidth.Add(Mathf.Lerp(w1, w2, t));

                    // Ground-snap interpolated points too
                    if (s > 0) // first point already snapped via control point
                    {
                        int last = splinePos.Count - 1;
                        Vector3 interp = splinePos[last];
                        Vector3 snapped = SnapToGround(interp);
                        snapped.y += groundOffset;
                        splinePos[last] = snapped;
                    }
                }
            }

            // Add last control point
            splinePos.Add(snappedPositions[snappedPositions.Count - 1]);
            splineRot.Add(rotations[rotations.Count - 1]);
            splineWidth.Add(widths[widths.Count - 1]);

            // 3. Build mesh vertices and triangles
            int vertCount = splinePos.Count * 2;
            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] triangles = new int[(splinePos.Count - 1) * 6];

            float accumulatedDist = 0f;

            for (int i = 0; i < splinePos.Count; i++)
            {
                Vector3 pos = transform.InverseTransformPoint(splinePos[i]);
                Vector3 forward;

                if (i < splinePos.Count - 1)
                    forward = (splinePos[i + 1] - splinePos[i]).normalized;
                else if (i > 0)
                    forward = (splinePos[i] - splinePos[i - 1]).normalized;
                else
                    forward = Vector3.forward;

                // Prevent zero-length forward
                if (forward.sqrMagnitude < 0.0001f)
                    forward = Vector3.forward;

                // Cross with up to get right vector
                Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                if (right.sqrMagnitude < 0.0001f)
                    right = Vector3.Cross(Vector3.forward, forward).normalized;

                // Apply per-point rotation around the forward axis
                float rotRad = splineRot[i] * Mathf.Deg2Rad;
                Vector3 up = Vector3.Cross(forward, right);
                Vector3 rotatedRight = right * Mathf.Cos(rotRad) + up * Mathf.Sin(rotRad);

                // Transform right to local space
                rotatedRight = transform.InverseTransformDirection(
                    transform.TransformDirection(Vector3.zero) == Vector3.zero
                        ? rotatedRight
                        : rotatedRight);

                // Actually compute in world space then convert
                Vector3 worldRight = Vector3.Cross(Vector3.up, transform.TransformDirection(forward)).normalized;
                if (worldRight.sqrMagnitude < 0.0001f)
                    worldRight = Vector3.right;

                float rotAngle = splineRot[i];
                Quaternion twist = Quaternion.AngleAxis(rotAngle, forward);
                worldRight = twist * worldRight;
                Vector3 localRight = transform.InverseTransformDirection(worldRight);

                float halfW = splineWidth[i] * 0.5f;

                vertices[i * 2] = pos - localRight * halfW;
                vertices[i * 2 + 1] = pos + localRight * halfW;

                // UV: X = 0..1 across width, Y = accumulated distance
                if (i > 0)
                    accumulatedDist += Vector3.Distance(splinePos[i], splinePos[i - 1]);

                float v = accumulatedDist * uvTilingY;
                uvs[i * 2] = new Vector2(0f, v);
                uvs[i * 2 + 1] = new Vector2(1f, v);
            }

            // Triangles
            int triIdx = 0;
            for (int i = 0; i < splinePos.Count - 1; i++)
            {
                int bl = i * 2;
                int br = i * 2 + 1;
                int tl = (i + 1) * 2;
                int tr = (i + 1) * 2 + 1;

                // Triangle 1
                triangles[triIdx++] = bl;
                triangles[triIdx++] = tl;
                triangles[triIdx++] = br;

                // Triangle 2
                triangles[triIdx++] = br;
                triangles[triIdx++] = tl;
                triangles[triIdx++] = tr;
            }

            // 4. Apply to mesh
            if (_mesh == null)
            {
                _mesh = new Mesh();
                _mesh.name = "TrackMesh";
            }

            _mesh.Clear();
            _mesh.vertices = vertices;
            _mesh.uv = uvs;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            _filter.sharedMesh = _mesh;
        }

        private void ClearMesh()
        {
            if (_mesh != null)
            {
                _mesh.Clear();
                if (_filter != null) _filter.sharedMesh = _mesh;
            }
        }

        private Vector3 SnapToGround(Vector3 pos)
        {
            Vector3 origin = pos;
            origin.y += 20f;

            if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, raycastDistance + 20f, groundLayer))
            {
                if (hit.collider is MeshCollider || hit.collider is TerrainCollider)
                {
                    pos.y = hit.point.y;
                }
            }

            return pos;
        }

        /// <summary>
        /// Catmull-Rom spline interpolation between p1 and p2.
        /// </summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
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
            if (worldPoints == null || worldPoints.Count < 2) return;

            Gizmos.color = Color.cyan;
            for (int i = 0; i < worldPoints.Count - 1; i++)
            {
                Gizmos.DrawLine(worldPoints[i].position, worldPoints[i + 1].position);
            }

            foreach (var pt in worldPoints)
            {
                Gizmos.color = pt.snapToGround ? Color.green : Color.red;
                Gizmos.DrawWireSphere(pt.position, 0.15f);

                // Draw rotation indicator
                if (Mathf.Abs(pt.rotation) > 0.1f)
                {
                    Gizmos.color = Color.yellow;
                    Vector3 right = Quaternion.Euler(0, 0, pt.rotation) * Vector3.right;
                    Gizmos.DrawLine(pt.position - right * 0.3f, pt.position + right * 0.3f);
                }
            }
        }
#endif
    }
}
