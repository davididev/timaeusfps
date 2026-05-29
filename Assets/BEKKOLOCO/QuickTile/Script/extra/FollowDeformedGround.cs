// FollowDeformedGround.cs — Snaps the object's Y to the deformed mesh surface below it.
// Attach automatically via GameObjectRule.followDeformationY toggle.

using UnityEngine;

namespace Bekkoloco
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class FollowDeformedGround : MonoBehaviour
    {
        [Tooltip("Maximum raycast distance downward to find the ground.")]
        public float raycastDistance = 50f;

        [Tooltip("Layer mask for ground detection. If 0 (Nothing), defaults to all layers.")]
        public LayerMask groundLayerMask = ~0;

        [Tooltip("How fast the object blends to the target Y (0 = instant). Only applies at runtime.")]
        public float smoothSpeed = 0f;

        // Cached
        private float _lastSampledY;
        private bool _hasValidGround;

        private void OnEnable()
        {
            SampleGround();
            ApplyYImmediate();
        }

        private void LateUpdate()
        {
            SampleGround();

            if (!_hasValidGround) return;

            if (smoothSpeed > 0f && Application.isPlaying)
            {
                Vector3 pos = transform.position;
                float targetY = _lastSampledY;
                pos.y = Mathf.Lerp(pos.y, targetY, Time.deltaTime * smoothSpeed);
                transform.position = pos;
            }
            else
            {
                ApplyYImmediate();
            }
        }

        /// <summary>
        /// Raycast downward from slightly above the object to find the ground mesh.
        /// </summary>
        private void SampleGround()
        {
            Vector3 origin = transform.position;
            origin.y += 50f;

            // RaycastAll to skip placed objects (QuickTileMarker) and other non-terrain hits
            var hits = Physics.RaycastAll(origin, Vector3.down, raycastDistance + 50f, groundLayerMask);
            if (hits.Length == 0)
            {
                _hasValidGround = false;
                return;
            }

            // Sort by distance (closest first)
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var hit in hits)
            {
                // Only terrain meshes — skip everything else
                if (!(hit.collider is MeshCollider) && !(hit.collider is TerrainCollider))
                    continue;

                // Skip placed objects (golems, props, etc.)
                if (hit.collider.GetComponentInParent<QuickTileMarker>() != null)
                    continue;

                // Skip self
                if (hit.collider.transform.IsChildOf(transform))
                    continue;

                _lastSampledY = hit.point.y;
                _hasValidGround = true;
                return;
            }

            _hasValidGround = false;
        }

        private void ApplyYImmediate()
        {
            if (!_hasValidGround) return;
            Vector3 pos = transform.position;
            pos.y = _lastSampledY;
            transform.position = pos;
        }

        /// <summary>
        /// Call from outside (e.g. RadialHillDeformer) to force Y update
        /// by directly sampling mesh triangles — no raycast needed.
        /// This works immediately after mesh vertex changes.
        /// </summary>
        public void ForceRefreshFromMeshes(MeshFilter[] deformedMeshes)
        {
            if (deformedMeshes == null || deformedMeshes.Length == 0) return;

            Vector3 pos = transform.position;
            float closestY = float.MinValue;
            bool found = false;

            foreach (var mf in deformedMeshes)
            {
                if (mf == null || mf.sharedMesh == null) continue;

                float y;
                if (SampleMeshHeightAt(mf, pos.x, pos.z, out y))
                {
                    if (!found || y > closestY)
                    {
                        closestY = y;
                        found = true;
                    }
                }
            }

            if (found)
            {
                _lastSampledY = closestY;
                _hasValidGround = true;
                ApplyYImmediate();
            }
        }

        /// <summary>
        /// Fallback ForceRefresh using raycast (for runtime or when meshes aren't passed).
        /// </summary>
        public void ForceRefresh()
        {
            SampleGround();
            ApplyYImmediate();
        }

        /// <summary>
        /// Sample the Y height of a mesh at a given world XZ position
        /// by checking each triangle. Works immediately after vertex changes.
        /// </summary>
        private static bool SampleMeshHeightAt(MeshFilter mf, float worldX, float worldZ, out float worldY)
        {
            worldY = 0f;
            var mesh = mf.sharedMesh;
            if (mesh == null) return false;

            var localToWorld = mf.transform.localToWorldMatrix;
            var worldToLocal = mf.transform.worldToLocalMatrix;

            // Convert world XZ to local space
            Vector3 localPoint = worldToLocal.MultiplyPoint3x4(new Vector3(worldX, 0f, worldZ));
            float lx = localPoint.x;
            float lz = localPoint.z;

            var verts = mesh.vertices;
            var tris = mesh.triangles;

            float bestY = float.MinValue;
            bool found = false;

            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 v0 = verts[tris[i]];
                Vector3 v1 = verts[tris[i + 1]];
                Vector3 v2 = verts[tris[i + 2]];

                // Barycentric test in XZ
                float y;
                if (PointInTriangleXZ(lx, lz, v0, v1, v2, out y))
                {
                    if (y > bestY)
                    {
                        bestY = y;
                        found = true;
                    }
                }
            }

            if (found)
            {
                // Convert local Y back to world
                Vector3 localHit = new Vector3(lx, bestY, lz);
                worldY = localToWorld.MultiplyPoint3x4(localHit).y;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if point (px,pz) is inside triangle (v0,v1,v2) in XZ plane.
        /// If yes, interpolate Y via barycentric coords.
        /// </summary>
        private static bool PointInTriangleXZ(float px, float pz, Vector3 v0, Vector3 v1, Vector3 v2, out float y)
        {
            y = 0f;

            float d00 = (v1.x - v0.x) * (v1.x - v0.x) + (v1.z - v0.z) * (v1.z - v0.z);
            float d01 = (v1.x - v0.x) * (v2.x - v0.x) + (v1.z - v0.z) * (v2.z - v0.z);
            float d11 = (v2.x - v0.x) * (v2.x - v0.x) + (v2.z - v0.z) * (v2.z - v0.z);
            float d20 = (px - v0.x) * (v1.x - v0.x) + (pz - v0.z) * (v1.z - v0.z);
            float d21 = (px - v0.x) * (v2.x - v0.x) + (pz - v0.z) * (v2.z - v0.z);

            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 1e-8f) return false;

            float invDenom = 1f / denom;
            float u = (d11 * d20 - d01 * d21) * invDenom;
            float v = (d00 * d21 - d01 * d20) * invDenom;

            if (u < -0.001f || v < -0.001f || u + v > 1.001f)
                return false;

            // Interpolate Y
            y = v0.y + u * (v1.y - v0.y) + v * (v2.y - v0.y);
            return true;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            // Visualize the raycast
            Gizmos.color = _hasValidGround ? Color.green : Color.red;
            Vector3 from = transform.position + Vector3.up * 10f;
            Vector3 to = _hasValidGround
                ? new Vector3(transform.position.x, _lastSampledY, transform.position.z)
                : transform.position + Vector3.down * raycastDistance;
            Gizmos.DrawLine(from, to);

            if (_hasValidGround)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(new Vector3(transform.position.x, _lastSampledY, transform.position.z), 0.15f);
            }
        }
#endif
    }
}
