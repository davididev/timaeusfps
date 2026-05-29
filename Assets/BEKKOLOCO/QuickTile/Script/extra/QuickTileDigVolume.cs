using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Bekkoloco
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class QuickTileDigVolume : MonoBehaviour
    {
        [Tooltip("Fallback local center used when no BoxCollider is present.")]
        public Vector3 center = Vector3.zero;

        [Tooltip("Fallback local size used when no BoxCollider is present.")]
        public Vector3 size = Vector3.one;

        [Header("Edge Controls")]
        [Range(0f, 1f)]
        [Tooltip("Rounds the dig volume edges. 0 = sharp cube, 1 = softer carved edges.")]
        public float edgeSmooth = 0f;

        [Tooltip("Adds curved bevel support around the dig opening so the tunnel can round into walls, floor and ceiling.")]
        public bool bevelEdges = false;

        [Range(0f, 1f)]
        [Tooltip("How deep the bevel pushes in from the top and bottom lips. 0 = almost flat cut, 1 = deeper chamfer.")]
        public float bevelDepth = 0.35f;

        [Range(1, 8)]
        [Tooltip("How many vertical steps are used to shape the bevel. Higher = smoother curve, more geometry.")]
        public int bevelSegments = 4;

        [Range(0f, 1f)]
        [Tooltip("Pulls the lower walls inward so the flat base inside the dig becomes smaller.")]
        public float baseInset = 0f;

        [Range(0f, 1f)]
        [Tooltip("Adds rocky breakup near the dig borders.")]
        public float edgeNoiseAmount = 0f;

        [Min(0.05f)]
        [Tooltip("Frequency of the rocky breakup noise.")]
        public float edgeNoiseScale = 1f;

        [Tooltip("When enabled, this dig volume also removes any skirt geometry it touches.")]
        public bool removeContactingSkirt = true;

        [Tooltip("Show the digging volume as a red gizmo in the Scene view.")]
        public bool drawGizmo = true;

        [Tooltip("Tint used for the Scene-view dig gizmo.")]
        public Color gizmoColor = new Color(1f, 0.2f, 0.1f, 0.18f);

#if UNITY_EDITOR
        private static bool _refreshQueued;
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastLossyScale;
#endif

        public bool TryGetWorldBounds(out Bounds bounds)
        {
            var boxCollider = GetComponent<BoxCollider>();
            if (boxCollider != null)
            {
                bounds = TransformLocalBounds(boxCollider.center, boxCollider.size);
                return bounds.size.sqrMagnitude > 0.000001f;
            }

            var genericCollider = GetComponent<Collider>();
            if (genericCollider != null)
            {
                bounds = genericCollider.bounds;
                return bounds.size.sqrMagnitude > 0.000001f;
            }

            var volumeRenderer = GetComponent<Renderer>();
            if (volumeRenderer != null)
            {
                bounds = volumeRenderer.bounds;
                if (bounds.size.sqrMagnitude > 0.000001f)
                    return true;
            }

            Vector3 safeSize = new Vector3(
                Mathf.Max(0.01f, Mathf.Abs(size.x)),
                Mathf.Max(0.01f, Mathf.Abs(size.y)),
                Mathf.Max(0.01f, Mathf.Abs(size.z)));

            bounds = TransformLocalBounds(center, safeSize);
            return true;
        }

        public bool HasDetailedEdges()
        {
            return edgeSmooth > 0.0001f || edgeNoiseAmount > 0.0001f;
        }

        public float EvaluateCarveDistanceWorld(
            Vector3 worldPoint,
            float coveragePaddingWorld = 0f,
            float horizontalInsetWorld = 0f,
            bool includeNoise = true)
        {
            if (!TryGetLocalVolumeBox(out Vector3 localCenter, out Vector3 localSize))
                return float.PositiveInfinity;

            Vector3 safeLocalSize = SanitizeLocalSize(localSize);
            Vector3 localPoint = transform.InverseTransformPoint(worldPoint) - localCenter;
            float localHorizontalInset = horizontalInsetWorld / GetMinHorizontalLossyScale();
            Vector3 adjustedLocalSize = safeLocalSize;
            if (Mathf.Abs(localHorizontalInset) > 0.0001f)
            {
                adjustedLocalSize.x = Mathf.Max(0.01f, adjustedLocalSize.x - localHorizontalInset * 2f);
                adjustedLocalSize.z = Mathf.Max(0.01f, adjustedLocalSize.z - localHorizontalInset * 2f);
            }

            Vector3 halfSize = adjustedLocalSize * 0.5f;

            float roundedRadius = GetRoundedEdgeRadiusLocal(adjustedLocalSize);
            float signedDistance = SignedDistanceToRoundedBox(localPoint, halfSize, roundedRadius);
            float localPadding = Mathf.Max(0f, coveragePaddingWorld) / GetMinLossyScale();

            float noiseAmplitude = includeNoise ? GetNoiseAmplitudeLocal(safeLocalSize) : 0f;
            if (noiseAmplitude > 0.0001f)
            {
                float influenceRange = Mathf.Max(0.0001f, localPadding + roundedRadius + noiseAmplitude);
                float edgeWeight = 1f - Mathf.Clamp01(Mathf.Abs(signedDistance) / influenceRange);
                if (edgeWeight > 0.0001f)
                {
                    float noise = SampleRockNoise(localPoint);
                    localPadding += noise * noiseAmplitude * edgeWeight;
                }
            }

            return signedDistance - localPadding;
        }

        public float GetNoiseAmplitudeWorld()
        {
            if (!TryGetLocalVolumeBox(out _, out Vector3 localSize))
                return 0f;

            return GetNoiseAmplitudeLocal(localSize) * GetMaxLossyScale();
        }

        public float GetRoundedEdgeRadiusWorld()
        {
            if (!TryGetLocalVolumeBox(out _, out Vector3 localSize))
                return 0f;

            return GetRoundedEdgeRadiusLocal(localSize) * GetMaxLossyScale();
        }

        public float GetBevelHeightWorld()
        {
            if (!TryGetLocalVolumeBox(out _, out Vector3 localSize))
                return 0f;

            float minAxis = Mathf.Min(localSize.x, Mathf.Min(localSize.y, localSize.z));
            float localDepth = Mathf.Clamp01(bevelDepth) * minAxis * 0.5f;
            return localDepth * GetMaxLossyScale();
        }

        public float GetBaseInsetWorld()
        {
            if (!TryGetLocalVolumeBox(out _, out Vector3 localSize))
                return 0f;

            float minAxis = Mathf.Min(localSize.x, Mathf.Min(localSize.y, localSize.z));
            float localInset = Mathf.Clamp01(baseInset) * minAxis * 0.35f;
            return localInset * GetMaxLossyScale();
        }

        public float SampleNoiseAtWorldPoint(Vector3 worldPoint)
        {
            if (!TryGetLocalVolumeBox(out Vector3 localCenter, out _))
                return 0f;

            Vector3 localPoint = transform.InverseTransformPoint(worldPoint) - localCenter;
            return SampleRockNoise(localPoint);
        }

        public float GetSuggestedSliceStep(float fallbackStep)
        {
            float safeFallback = Mathf.Max(0.1f, fallbackStep);
            if (!HasDetailedEdges())
                return safeFallback;

            float refinedStep = safeFallback * Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(edgeSmooth));
            if (TryGetLocalVolumeBox(out _, out Vector3 localSize))
            {
                float localNoiseAmplitude = GetNoiseAmplitudeLocal(localSize);
                if (localNoiseAmplitude > 0.0001f)
                {
                    float minScale = GetMinLossyScale();
                    float worldNoiseAmplitude = localNoiseAmplitude * minScale;
                    refinedStep = Mathf.Min(refinedStep, Mathf.Max(0.15f, worldNoiseAmplitude * 0.9f));
                }
            }

            return Mathf.Clamp(refinedStep, 0.15f, 1f);
        }

        public float GetWorldBoundaryPadding()
        {
            if (!TryGetLocalVolumeBox(out _, out Vector3 localSize))
                return 0f;

            float localPadding = GetRoundedEdgeRadiusLocal(localSize) + GetNoiseAmplitudeLocal(localSize);
            return localPadding * GetMaxLossyScale();
        }

        public bool ShouldCarveWorldPoint(
            Vector3 worldPoint,
            float coveragePaddingWorld,
            float horizontalInsetWorld = 0f,
            bool includeNoise = true)
        {
            return EvaluateCarveDistanceWorld(worldPoint, coveragePaddingWorld, horizontalInsetWorld, includeNoise) <= 0f;
        }

        public bool TryProjectWorldPointToSurface(
            Vector3 worldPoint,
            float maxDistanceWorld,
            out Vector3 projectedPoint,
            out Vector3 surfaceNormal,
            out float blendWeight)
        {
            projectedPoint = worldPoint;
            surfaceNormal = Vector3.up;
            blendWeight = 0f;

            if (!bevelEdges || edgeSmooth <= 0.0001f)
                return false;

            float safeDistance = Mathf.Max(0.01f, maxDistanceWorld);
            float signedDistance = EvaluateCarveDistanceWorld(worldPoint, 0f);
            if (float.IsNaN(signedDistance) || float.IsInfinity(signedDistance))
                return false;

            float absDistance = Mathf.Abs(signedDistance);
            if (absDistance > safeDistance)
                return false;

            float sampleStep = Mathf.Clamp(safeDistance * 0.15f, 0.01f, 0.1f);
            Vector3 gradient = new Vector3(
                EvaluateCarveDistanceWorld(worldPoint + new Vector3(sampleStep, 0f, 0f), 0f) -
                EvaluateCarveDistanceWorld(worldPoint - new Vector3(sampleStep, 0f, 0f), 0f),
                EvaluateCarveDistanceWorld(worldPoint + new Vector3(0f, sampleStep, 0f), 0f) -
                EvaluateCarveDistanceWorld(worldPoint - new Vector3(0f, sampleStep, 0f), 0f),
                EvaluateCarveDistanceWorld(worldPoint + new Vector3(0f, 0f, sampleStep), 0f) -
                EvaluateCarveDistanceWorld(worldPoint - new Vector3(0f, 0f, sampleStep), 0f));

            if (gradient.sqrMagnitude <= 0.0000001f)
                return false;

            surfaceNormal = gradient.normalized;
            projectedPoint = worldPoint - surfaceNormal * signedDistance;
            blendWeight = Mathf.SmoothStep(0f, 1f, 1f - absDistance / safeDistance);
            return true;
        }

        public Bounds GetWorldBounds()
        {
            return TryGetWorldBounds(out Bounds bounds)
                ? bounds
                : new Bounds(transform.position, Vector3.zero);
        }

        private Bounds TransformLocalBounds(Vector3 localCenter, Vector3 localSize)
        {
            Vector3 worldCenter = transform.TransformPoint(localCenter);
            Vector3 scaledSize = Vector3.Scale(localSize, AbsVector(transform.lossyScale));
            Vector3 extents = scaledSize * 0.5f;

            Vector3 axisX = transform.right * extents.x;
            Vector3 axisY = transform.up * extents.y;
            Vector3 axisZ = transform.forward * extents.z;

            Vector3 worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

            return new Bounds(worldCenter, worldExtents * 2f);
        }

        private static Vector3 AbsVector(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        private bool TryGetLocalVolumeBox(out Vector3 localCenter, out Vector3 localSize)
        {
            var boxCollider = GetComponent<BoxCollider>();
            if (boxCollider != null)
            {
                localCenter = boxCollider.center;
                localSize = SanitizeLocalSize(boxCollider.size);
                return true;
            }

            localCenter = center;
            localSize = SanitizeLocalSize(size);
            return true;
        }

        private static Vector3 SanitizeLocalSize(Vector3 rawSize)
        {
            return new Vector3(
                Mathf.Max(0.01f, Mathf.Abs(rawSize.x)),
                Mathf.Max(0.01f, Mathf.Abs(rawSize.y)),
                Mathf.Max(0.01f, Mathf.Abs(rawSize.z)));
        }

        private float GetRoundedEdgeRadiusLocal(Vector3 localSize)
        {
            float minAxis = Mathf.Min(localSize.x, Mathf.Min(localSize.y, localSize.z));
            return Mathf.Clamp01(edgeSmooth) * minAxis * 0.225f;
        }

        private float GetNoiseAmplitudeLocal(Vector3 localSize)
        {
            float minAxis = Mathf.Min(localSize.x, Mathf.Min(localSize.y, localSize.z));
            return Mathf.Clamp01(edgeNoiseAmount) * Mathf.Max(0.05f, minAxis * 0.2f);
        }

        private float SampleRockNoise(Vector3 localPoint)
        {
            float scale = Mathf.Max(0.05f, edgeNoiseScale);
            Vector3 p = localPoint * scale;

            float xy = Mathf.PerlinNoise(p.x + 17.3f, p.y + 29.1f) * 2f - 1f;
            float yz = Mathf.PerlinNoise(p.y + 53.7f, p.z + 11.9f) * 2f - 1f;
            float xz = Mathf.PerlinNoise(p.x + 71.4f, p.z + 97.2f) * 2f - 1f;
            float fine = Mathf.PerlinNoise(p.x * 2.1f + 131.5f, p.z * 2.1f + 41.8f) * 2f - 1f;

            return Mathf.Clamp((xy + yz + xz + fine * 0.5f) / 3.5f, -1f, 1f);
        }

        private static float SignedDistanceToRoundedBox(Vector3 point, Vector3 halfExtents, float radius)
        {
            Vector3 shrunkenHalfExtents = new Vector3(
                Mathf.Max(0.0001f, halfExtents.x - radius),
                Mathf.Max(0.0001f, halfExtents.y - radius),
                Mathf.Max(0.0001f, halfExtents.z - radius));

            Vector3 q = AbsVector(point) - shrunkenHalfExtents;
            Vector3 outside = new Vector3(
                Mathf.Max(q.x, 0f),
                Mathf.Max(q.y, 0f),
                Mathf.Max(q.z, 0f));

            float outsideDistance = outside.magnitude;
            float insideDistance = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
            return outsideDistance + insideDistance - radius;
        }

        private float GetMinLossyScale()
        {
            Vector3 absScale = AbsVector(transform.lossyScale);
            return Mathf.Max(0.0001f, Mathf.Min(absScale.x, Mathf.Min(absScale.y, absScale.z)));
        }

        private float GetMinHorizontalLossyScale()
        {
            Vector3 absScale = AbsVector(transform.lossyScale);
            return Mathf.Max(0.0001f, Mathf.Min(absScale.x, absScale.z));
        }

        private float GetMaxLossyScale()
        {
            Vector3 absScale = AbsVector(transform.lossyScale);
            return Mathf.Max(0.0001f, Mathf.Max(absScale.x, Mathf.Max(absScale.y, absScale.z)));
        }

#if UNITY_EDITOR
        private void OnEnable()
        {
            CaptureTransformState();
            QueueQuickTileRefresh();
        }

        private void OnDisable()
        {
            QueueQuickTileRefresh();
        }

        private void OnValidate()
        {
            size = new Vector3(
                Mathf.Max(0.01f, Mathf.Abs(size.x)),
                Mathf.Max(0.01f, Mathf.Abs(size.y)),
                Mathf.Max(0.01f, Mathf.Abs(size.z)));
            edgeSmooth = Mathf.Clamp01(edgeSmooth);
            bevelDepth = Mathf.Clamp01(bevelDepth);
            bevelSegments = Mathf.Clamp(bevelSegments, 1, 8);
            baseInset = Mathf.Clamp01(baseInset);
            edgeNoiseAmount = Mathf.Clamp01(edgeNoiseAmount);
            edgeNoiseScale = Mathf.Max(0.05f, edgeNoiseScale);

            CaptureTransformState();
            QueueQuickTileRefresh();
        }

        private void Update()
        {
            if (Application.isPlaying)
                return;

            if (!TransformStateChanged())
                return;

            CaptureTransformState();
            QueueQuickTileRefresh();
        }

        private void CaptureTransformState()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastLossyScale = transform.lossyScale;
        }

        private bool TransformStateChanged()
        {
            return _lastPosition != transform.position ||
                   _lastRotation != transform.rotation ||
                   _lastLossyScale != transform.lossyScale;
        }

        private static void QueueQuickTileRefresh()
        {
            if (_refreshQueued)
                return;

            _refreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                _refreshQueued = false;
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return;

                var editor = UnityEngine.Object.FindFirstObjectByType<QuickTilemapEditor>();
                if (editor == null)
                {
                    SceneView.RepaintAll();
                    return;
                }

                editor.SyncAllProceduralRenderers();
                EditorUtility.SetDirty(editor);
                if (editor.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(editor.gameObject.scene);

                SceneView.RepaintAll();
            };
        }
#endif

        private void OnDrawGizmos()
        {
            if (!drawGizmo || !TryGetWorldBounds(out Bounds bounds))
                return;

            Color previousColor = Gizmos.color;
            Gizmos.color = gizmoColor;
            Gizmos.DrawCube(bounds.center, bounds.size);

            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, Mathf.Clamp01(gizmoColor.a + 0.45f));
            Gizmos.DrawWireCube(bounds.center, bounds.size);
            Gizmos.color = previousColor;
        }
    }
}
