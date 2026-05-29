// ──────────────────────────────────────────────────────────────────────────────
//  BEKKOLOCO / QuickTile – RadialHillDeformer (DOTS/Jobs + Burst, Hybrid)
//  - Fidèle au legacy : handles multiples, falloffs, yMin relatif, clamp monde,
//    radius lié à l’échelle (modes), compensation localScale.y, ratio Y, etc.
//  - Editor: feedback temps réel (ExecuteAlways).
//  - Runtime: mode statique (une seule appli après délai) ou dynamique.
//  - Multi-mesh: parcourt tous les MeshFilters enfants, clone + restaure proprement.
//  - API de compat: ForceUpdate(), Apply(bool), IsRuntimeOptimized, SyncWithTileRuleRuntime()
// ──────────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
#define QT_EDITOR
#endif

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bekkoloco;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

#if UNITY_BURST
using Unity.Burst;
#endif

namespace Bekkoloco.DOTS
{
    public enum DOTSFalloff : byte { Linear, Parabolic, SmoothStep, Gaussian }
    public enum DOTSRadiusLinkMode : byte { WorldConstant, MultiplyByScale }
    public enum DOTSScaleMetric : byte { AverageXZ, MaxXZ, MinXZ, GeometricMeanXZ }
    public enum DOTSDeformShape : byte { Round, Square }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class RadialHillDeformer : MonoBehaviour
    {
        // ───────────────────────── Parameters (parité legacy) ─────────────────────────
        [Header("🎮 Runtime Performance")]
        [Tooltip("En runtime, appliquer la déformation seulement une fois au début")]
        public bool runtimeStaticMode = true;

        [Tooltip("Délai (s) avant d'appliquer la déformation en runtime statique")]
        public float runtimeInitDelay = 0.1f;

        [Header("Radius → Handle Scale")]
        [Tooltip("Si ON: adapte 'radius' à l'échelle (XZ) du HANDLE (ou de chaque handle si multiple).")]
        public bool linkRadiusToScale = false;

        [Tooltip("WorldConstant: rayon constant en monde (radius/k). MultiplyByScale: radius*k.")]
        public DOTSRadiusLinkMode radiusLinkMode = DOTSRadiusLinkMode.WorldConstant;

        [Tooltip("Comment résumer l'échelle XZ quand elle est non uniforme.")]
        public DOTSScaleMetric scaleMetric = DOTSScaleMetric.AverageXZ;

        [Header("Handles")]
        [Tooltip("Handle principal (compatibilité).")]
        public Transform handle;

        // ⬇️ Back-compat : migre l'ancien champ 'additionalHandlesList' vers 'additionalHandles'
        [FormerlySerializedAs("additionalHandlesList")]
        [Tooltip("Handles supplémentaires (leurs influences s'additionnent).")]
        public List<Transform> additionalHandles = new List<Transform>();

        // ⬇️ Alias pour le code existant qui référence encore 'additionalHandlesList'
        public List<Transform> additionalHandlesList
        {
            get
            {
                if (additionalHandles == null) additionalHandles = new List<Transform>();
                return additionalHandles;
            }
            set { additionalHandles = value ?? new List<Transform>(); }
        }

        // (optionnel) utilitaire anti-doublon
        public void AddHandleUnique(Transform t)
        {
            if (t == null) return;
            if (additionalHandles == null) additionalHandles = new List<Transform>();
            if (!additionalHandles.Contains(t)) additionalHandles.Add(t);
        }

        [Header("Control")]
        [Tooltip("Base amplitude factor. Deformation behaves as if localScale.y == 1.")]
        public float heightPerUnitY = 1f;

        [Header("Influence (XZ)")]
        [Tooltip("Round = circular falloff (radial distance). Square = box falloff (Chebyshev distance).")]
        public DOTSDeformShape shape = DOTSDeformShape.Round;
        public float radius = 5f;
        public DOTSFalloff falloff = DOTSFalloff.SmoothStep;
        [Range(0.1f, 4f)] public float gaussianSharpness = 1.5f;

        [Header("Direction")]
        [Tooltip("If true, moving the handle up pushes vertices DOWN (flip sign).")]
        public bool invertDirection = false;

        [Header("Handle Zero (offset along local Up, in WORLD units)")]
        [Tooltip("If true, subtract a zero offset (along transform.up) from the handle height (WORLD units).")]
        public bool useHandleZero = false;
        [Tooltip("World-space offset along transform.up used as neutral (appliqué à tous les handles).")]
        public float handleZeroAlongUp = 0f;

        [Header("Y Threshold (local)")]
        public bool useYMin = false;
        public float yMin = 0f;
        public bool yMinRelativeToHandle = false;
        public float yFeather = 0f;
        public DOTSFalloff yMinFalloff = DOTSFalloff.SmoothStep;
        [Range(0.1f, 4f)] public float yMinGaussianSharpness = 1.5f;

        [Header("World Y Clamp (absolute)")]
        public bool clampWorldMinY = false;
        public float worldMinY = 0f;
        public bool clampOnlyAffected = true;

        [Header("Y-Scale Matching")]
        [Tooltip("Use world-up projection then divide by THIS object's localScale.y (sign preserved).")]
        public bool compensateLocalScaleY = true;
        [Tooltip("Extra manual multiplier after compensation. Keep it POSITIVE; use the toggle to invert.")]
        public float yDeformRatio = 1f;

        [Header("Mesh Output")]
        public bool recalcNormals = true;
        public bool updateMeshCollider = true;

        // ───────────────────────── Internals ─────────────────────────
        const string kSuffix = "_QT_EDIT";

        MeshFilter[] _affectedMeshFilters;
        readonly Dictionary<MeshFilter, Mesh> _originalMeshByMF = new();
        readonly Dictionary<MeshFilter, Vector3[]> _baseVertsByMF = new();
        readonly Dictionary<MeshFilter, Mesh> _deformedMeshByMF = new();
        readonly Dictionary<MeshFilter, Mesh> _colliderMeshByMF = new();

        bool _runtimeInitialized;
        bool _runtimeAppliedOnce;

#if QT_EDITOR
        bool _editorDirty;
        Vector3 _lastHandlePos;
        Quaternion _lastHandleRot;
        readonly List<Vector3> _lastExtraPos = new();
        readonly List<Quaternion> _lastExtraRot = new();

        double _nextEditorUpdate;
        const double kMaxHz = 15.0;        // throttle éditeur (~15 fps)
#endif


        // ─── Snapshot persistant des sources (anti None(Mesh)) ───
        [System.Serializable]
        private struct SavedSource
        {
            public string path;  // chemin relatif depuis ce deformer jusqu'au MeshFilter
            public Mesh source;  // mesh asset d'origine (ou clone si c'est tout ce qu'on a)
        }
        [SerializeField] private List<SavedSource> _savedSources = new List<SavedSource>();

        static string GetRelativePath(Transform root, Transform t)
        {
            if (root == null || t == null) return "";
            var stack = new Stack<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            return string.Join("/", stack);
        }

        Mesh GetSavedSourceFor(MeshFilter mf)
        {
            var p = GetRelativePath(transform, mf.transform);
            for (int i = 0; i < _savedSources.Count; i++)
                if (_savedSources[i].path == p)
                    return _savedSources[i].source;
            return null;
        }

        void SaveOrUpdateSource(MeshFilter mf, Mesh src)
        {
            if (mf == null || src == null) return;
            var p = GetRelativePath(transform, mf.transform);
            for (int i = 0; i < _savedSources.Count; i++)
            {
                if (_savedSources[i].path == p)
                {
                    _savedSources[i] = new SavedSource { path = p, source = src };
                    return;
                }
            }
            _savedSources.Add(new SavedSource { path = p, source = src });
        }

        // ───────────────────────── Lifecycle ─────────────────────────
        void OnEnable()
        {
            CacheMeshes();
#if QT_EDITOR
            if (!Application.isPlaying)
            {
                // Éditeur : feedback live
                Apply(true);
            }
#endif
            if (Application.isPlaying && !runtimeStaticMode)
            {
                // Runtime dynamique : prêt à appliquer chaque Update
                _runtimeInitialized = true;
            }
        }

        void OnDisable()
        {
            // Restaure les Meshes originaux (propre en Editor)
            RestoreOriginalMeshes();
            _runtimeInitialized = false;
            _runtimeAppliedOnce = false;
        }

        void OnDestroy()
        {
            // Libère les meshes collider temporaires quand le composant disparaît.
            CleanupColliderMeshes();
        }

#if QT_EDITOR
        void OnValidate()
        {
            // Auto-heal en Editor si un MF a perdu son mesh
            if (_affectedMeshFilters == null || _affectedMeshFilters.Length == 0)
                _affectedMeshFilters = GetComponentsInChildren<MeshFilter>(true);

            if (_affectedMeshFilters != null)
            {
                foreach (var mf in _affectedMeshFilters)
                {
                    if (mf == null) continue;
                    if (mf.sharedMesh == null)
                    {
                        var saved = GetSavedSourceFor(mf);
                        if (saved != null) mf.sharedMesh = saved;
                    }
                }
            }
        }
#endif

        void Start()
        {
            if (Application.isPlaying && runtimeStaticMode && !_runtimeInitialized)
            {
                StartCoroutine(DeformOnceAfterDelay());
            }
        }

        IEnumerator DeformOnceAfterDelay()
        {
            _runtimeInitialized = true;
            if (runtimeInitDelay > 0f)
                yield return new WaitForSeconds(runtimeInitDelay);

            Apply(true);
            _runtimeAppliedOnce = true;
        }

        void Update()
        {
#if QT_EDITOR
            if (!Application.isPlaying)
                return; // on gère l’éditeur dans LateUpdate() pour mieux contrôler la fréquence
#endif
            // RUNTIME
            if (!runtimeStaticMode)
            {
                Apply();
            }
            else
            {
                // Mode statique : rien après l’application unique (gérée par Start()).
            }
        }

#if QT_EDITOR
        void LateUpdate()
        {
            if (Application.isPlaying) return;

            // ── 1) Throttle éditeur (évite de spammer à 100+ fps)
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now < _nextEditorUpdate) return;
            _nextEditorUpdate = now + 1.0 / kMaxHz;

            // ── 2) Détection de vrai changement de handles
            bool changed = false;

            if (handle != null)
            {
                if (handle.position != _lastHandlePos || handle.rotation != _lastHandleRot)
                {
                    changed = true;
                    _lastHandlePos = handle.position;
                    _lastHandleRot = handle.rotation;
                }
            }

            // sync des listes si taille a changé
            if (_lastExtraPos.Count != additionalHandles.Count)
            {
                _lastExtraPos.Clear(); _lastExtraRot.Clear();
                for (int i = 0; i < additionalHandles.Count; i++)
                {
                    var h = additionalHandles[i];
                    _lastExtraPos.Add(h ? h.position : Vector3.zero);
                    _lastExtraRot.Add(h ? h.rotation : Quaternion.identity);
                }
                changed = true;
            }
            else
            {
                for (int i = 0; i < additionalHandles.Count; i++)
                {
                    var h = additionalHandles[i];
                    var p = h ? h.position : Vector3.zero;
                    var r = h ? h.rotation : Quaternion.identity;
                    if (p != _lastExtraPos[i] || r != _lastExtraRot[i])
                    {
                        changed = true;
                        _lastExtraPos[i] = p;
                        _lastExtraRot[i] = r;
                    }
                }
            }

            // ── 3) Seulement si changement → Apply(true)
            if (changed)
                Apply(true);
        }
#endif


        // ───────────────────────── Public API (compat) ─────────────────────────
        /// <summary>Fired after Apply() finishes deforming meshes.</summary>
        public event System.Action OnPostApply;

        public bool IsRuntimeOptimized => runtimeStaticMode && _runtimeAppliedOnce && _runtimeInitialized;

        public void ForceUpdate() => Apply(true);

        /// <summary>
        /// Re-cache all MeshFilters and their vertices, then re-apply deformation.
        /// Call this when a child mesh has been replaced (e.g. procedural rebuild).
        /// </summary>
        public void RecacheAndApply()
        {
            CacheMeshes();
            Apply(true);
        }

        public void Apply(bool force = false)
        {
            if (_affectedMeshFilters == null || _affectedMeshFilters.Length == 0)
                CacheMeshes();

            if (_affectedMeshFilters == null || _affectedMeshFilters.Length == 0)
                return;

            var handles = CollectHandles();
            if (handles.Count == 0)
            {
                // Pas de handles : reset to base
                foreach (var mf in _affectedMeshFilters)
                    ResetMesh(mf);
                return;
            }

            // Prépare les paramètres par handle (en LOCAL du déformeur)
            var hp = BuildHandleParams(handles);

            // Matrices du déformeur
            float4x4 deformLocalToWorld = transform.localToWorldMatrix;
            float4x4 deformWorldToLocal = transform.worldToLocalMatrix;

            // Prépare yMin feather
            float feather = math.max(0f, math.abs(yFeather));

            // Pour chaque MeshFilter enfant
            foreach (var mf in _affectedMeshFilters)
            {
                if (mf == null || !_baseVertsByMF.TryGetValue(mf, out var baseVerts)) continue;

                // Assure le mesh cloné
                var outMesh = EnsureDeformedMesh(mf);
                if (outMesh == null) continue;

                // NativeArrays
                var baseNA = new NativeArray<float3>(baseVerts.Length, Allocator.TempJob);
                var outNA = new NativeArray<float3>(baseVerts.Length, Allocator.TempJob);

                for (int i = 0; i < baseVerts.Length; i++)
                    baseNA[i] = baseVerts[i];

                // Handle params natifs
                var hpNA = new NativeArray<HandleParam>(hp.Length, Allocator.TempJob);
                for (int i = 0; i < hp.Length; i++) hpNA[i] = hp[i];

                // Matrices du MF
                float4x4 mfLocalToWorld = mf.transform.localToWorldMatrix;
                float4x4 mfWorldToLocal = mf.transform.worldToLocalMatrix;

                var job = new DeformJob
                {
                    baseVertices = baseNA,
                    outVertices = outNA,
                    mfLocalToWorld = mfLocalToWorld,
                    mfWorldToLocal = mfWorldToLocal,
                    deformLocalToWorld = deformLocalToWorld,
                    deformWorldToLocal = deformWorldToLocal,
                    handles = hpNA,

                    shape = shape,
                    falloff = falloff,
                    gaussianSharpness = math.max(0.0001f, gaussianSharpness),
                    useYMin = useYMin ? (byte)1 : (byte)0,
                    yFeather = feather,
                    yMinFalloff = yMinFalloff,
                    yMinGaussianSharp = math.max(0.0001f, yMinGaussianSharpness),

                    clampWorldMinY = clampWorldMinY ? (byte)1 : (byte)0,
                    worldMinY = worldMinY,
                    clampOnlyAffected = clampOnlyAffected ? (byte)1 : (byte)0,
                };

                var handle = job.Schedule(baseVerts.Length, 128);
                handle.Complete();

                // Récupère dans Vector3[]
                var outManaged = new Vector3[outNA.Length];
                for (int i = 0; i < outNA.Length; i++) outManaged[i] = outNA[i];

                outMesh.SetVertices(outManaged);
                if (recalcNormals) outMesh.RecalculateNormals();
                outMesh.RecalculateBounds();

                if (updateMeshCollider)
                {
                    UpdateMeshColliderSafe(mf, outMesh);
                }

                baseNA.Dispose();
                outNA.Dispose();
                hpNA.Dispose();
            }

            OnPostApply?.Invoke();

#if UNITY_EDITOR
            // Notify all FollowDeformedGround objects to refresh their Y position
            if (!Application.isPlaying)
            {
                NotifyGroundFollowers();
            }
#endif
        }

#if UNITY_EDITOR
        /// <summary>
        /// After deformation, tell every FollowDeformedGround to re-sample
        /// so placed objects move live when dragging handles.
        /// </summary>
        private void NotifyGroundFollowers()
        {
            var meshes = _affectedMeshFilters;

            // Update placed GameObjects (FollowDeformedGround)
            var followers = FindObjectsByType<FollowDeformedGround>(FindObjectsSortMode.None);
            if (followers != null)
            {
                foreach (var f in followers)
                {
                    if (f == null || !f.enabled) continue;
                    f.ForceRefreshFromMeshes(meshes);
                }
            }

            // Update vegetation instance Y positions on the deformed terrain
            UpdateVegetationPositionsOnDeformedMesh(meshes);
        }

        /// <summary>
        /// Re-sample vegetation Y positions after terrain deformation
        /// so grass/cards/prefabs follow the new ground height.
        /// </summary>
        private void UpdateVegetationPositionsOnDeformedMesh(MeshFilter[] meshes)
        {
            if (meshes == null || meshes.Length == 0) return;

            var editor = FindFirstObjectByType<QuickTilemapEditor>();
            if (editor == null || editor.texturePaintRules == null) return;

            bool anyChanged = false;
            foreach (var rule in editor.texturePaintRules)
            {
                if (rule?.vegetationEntries == null) continue;
                foreach (var entry in rule.vegetationEntries)
                {
                    if (entry?.instances == null) continue;
                    for (int i = 0; i < entry.instances.Count; i++)
                    {
                        var inst = entry.instances[i];
                        float newY;
                        if (SampleMeshHeightAtXZ(meshes, inst.position.x, inst.position.z, out newY))
                        {
                            if (!Mathf.Approximately(inst.position.y, newY + entry.yOffset))
                            {
                                inst.position.y = newY + entry.yOffset;
                                entry.instances[i] = inst;
                                anyChanged = true;
                            }
                        }
                    }
                }
            }

            if (anyChanged)
            {
                var gpuRenderer = editor.GetComponent<VegetationGPURenderer>();
                if (gpuRenderer != null)
                    gpuRenderer.RebuildFromRules(editor.texturePaintRules);
            }
        }

        /// <summary>
        /// Sample Y height from mesh triangles at world XZ (same approach as FollowDeformedGround).
        /// </summary>
        private static bool SampleMeshHeightAtXZ(MeshFilter[] meshes, float worldX, float worldZ, out float worldY)
        {
            worldY = 0f;
            float bestY = float.MinValue;
            bool found = false;

            foreach (var mf in meshes)
            {
                if (mf == null || mf.sharedMesh == null) continue;

                var localToWorld = mf.transform.localToWorldMatrix;
                var worldToLocal = mf.transform.worldToLocalMatrix;
                Vector3 localPoint = worldToLocal.MultiplyPoint3x4(new Vector3(worldX, 0f, worldZ));
                float lx = localPoint.x, lz = localPoint.z;

                var verts = mf.sharedMesh.vertices;
                var tris = mf.sharedMesh.triangles;

                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 v0 = verts[tris[i]];
                    Vector3 v1 = verts[tris[i + 1]];
                    Vector3 v2 = verts[tris[i + 2]];

                    float d00 = (v1.x - v0.x) * (v1.x - v0.x) + (v1.z - v0.z) * (v1.z - v0.z);
                    float d01 = (v1.x - v0.x) * (v2.x - v0.x) + (v1.z - v0.z) * (v2.z - v0.z);
                    float d11 = (v2.x - v0.x) * (v2.x - v0.x) + (v2.z - v0.z) * (v2.z - v0.z);
                    float d20 = (lx - v0.x) * (v1.x - v0.x) + (lz - v0.z) * (v1.z - v0.z);
                    float d21 = (lx - v0.x) * (v2.x - v0.x) + (lz - v0.z) * (v2.z - v0.z);

                    float denom = d00 * d11 - d01 * d01;
                    if (Mathf.Abs(denom) < 1e-8f) continue;

                    float invDenom = 1f / denom;
                    float u = (d11 * d20 - d01 * d21) * invDenom;
                    float v = (d00 * d21 - d01 * d20) * invDenom;

                    if (u < -0.001f || v < -0.001f || u + v > 1.001f) continue;

                    float localY = v0.y + u * (v1.y - v0.y) + v * (v2.y - v0.y);
                    float wy = localToWorld.MultiplyPoint3x4(new Vector3(lx, localY, lz)).y;
                    if (wy > bestY) { bestY = wy; found = true; }
                }
            }

            worldY = bestY;
            return found;
        }
#endif

        /// <summary>
        /// Ajuste worldMinY depuis la TileRule parent (runtime-safe)
        /// </summary>
        public void SyncWithTileRuleRuntime()
        {
            var tilemap = transform.parent ? transform.parent.GetComponent<UnityEngine.Tilemaps.Tilemap>() : null;
            if (tilemap == null) return;

            // QuickTilemapEditor se trouve dans le projet (legacy)
            var editor = Object.FindFirstObjectByType<Bekkoloco.QuickTilemapEditor>();
            if (editor == null || editor.tileRules == null) return;

            var rule = editor.tileRules.FirstOrDefault(r => r.customTargetTilemap == tilemap);
            if (rule != null)
            {
                float newWorldMinY = rule.yOffset - rule.sizeY;
                if (!Mathf.Approximately(worldMinY, newWorldMinY))
                {
                    worldMinY = newWorldMinY;
                    Apply(true);
                }
                if (!clampWorldMinY) clampWorldMinY = true;
            }
        }

        // ───────────────────────── Mesh caching / cloning ─────────────────────────
        /// <summary>
        /// Returns true if the given Transform is one of the deformer handles
        /// (or a child of a handle). These should NOT be deformed.
        /// </summary>
        bool IsHandleOrChildOfHandle(Transform t)
        {
            // Collect all handle transforms
            var handleSet = new HashSet<Transform>();
            if (handle != null) handleSet.Add(handle);
            if (additionalHandles != null)
            {
                foreach (var h in additionalHandles)
                    if (h != null) handleSet.Add(h);
            }
            if (handleSet.Count == 0) return false;

            // Walk up the parent chain to see if any ancestor is a handle
            var cur = t;
            while (cur != null && cur != transform)
            {
                if (handleSet.Contains(cur)) return true;
                cur = cur.parent;
            }
            return false;
        }

        void CacheMeshes()
        {
            // Get all MeshFilters, then exclude handle objects (they should not be deformed)
            var allMFs = GetComponentsInChildren<MeshFilter>(true);
            var filtered = new List<MeshFilter>(allMFs.Length);
            foreach (var mf in allMFs)
            {
                if (mf == null) continue;
                if (IsHandleOrChildOfHandle(mf.transform)) continue;
                // Skip placed GameObjects — their Y position follows the ground instead
                if (mf.GetComponentInParent<QuickTileMarker>() != null) continue;
                filtered.Add(mf);
            }
            _affectedMeshFilters = filtered.ToArray();

            CleanupColliderMeshes();
            _originalMeshByMF.Clear();
            _baseVertsByMF.Clear();
            _deformedMeshByMF.Clear();

            foreach (var mf in _affectedMeshFilters)
            {
                if (mf == null) continue;

                // 1) Récupère la source de façon robuste
                var src = mf.sharedMesh;
                if (src == null)
                {
                    // 🛟 Unity a "perdu" le clone: recharger depuis le snapshot persistant
                    var saved = GetSavedSourceFor(mf);
                    if (saved != null)
                    {
                        src = saved;
                        mf.sharedMesh = src; // ré-assigne la source au MF
                    }
                }

                if (src == null) continue; // toujours rien → on skip

                // 2) Mémorise/MAJ la source persistée
                SaveOrUpdateSource(mf, src);

                // 3) Enregistre original + base verts
                _originalMeshByMF[mf] = src;

#if QT_EDITOR
                _baseVertsByMF[mf] = (Vector3[])src.vertices.Clone();
#else
                if (!src.isReadable)
                {
                    Debug.LogError($"[RadialHillDeformer] Mesh '{src.name}' is not readable. Enable Read/Write.", mf);
                    continue;
                }
                _baseVertsByMF[mf] = (Vector3[])src.vertices.Clone();
#endif
                // 4) Crée le clone dynamique et l'assigne
                var clone = Instantiate(src);
                clone.name = src.name.EndsWith(kSuffix) ? src.name : (src.name + kSuffix);
                clone.MarkDynamic();
                _deformedMeshByMF[mf] = clone;
                mf.sharedMesh = clone;
            }
        }

        Mesh EnsureDeformedMesh(MeshFilter mf)
        {
            if (_deformedMeshByMF.TryGetValue(mf, out var m) && m != null) return m;

            // Recrée si détruit à partir de l'original connu...
            if (_originalMeshByMF.TryGetValue(mf, out var src) && src != null)
            {
                var clone = Instantiate(src);
                clone.name = src.name.EndsWith(kSuffix) ? src.name : (src.name + kSuffix);
                clone.MarkDynamic();
                _deformedMeshByMF[mf] = clone;
                mf.sharedMesh = clone;
                return clone;
            }

            // ... ou depuis le snapshot persistant
            var saved = GetSavedSourceFor(mf);
            if (saved != null)
            {
                var clone = Instantiate(saved);
                clone.name = saved.name.EndsWith(kSuffix) ? saved.name : (saved.name + kSuffix);
                clone.MarkDynamic();
                _deformedMeshByMF[mf] = clone;
                mf.sharedMesh = clone;
                return clone;
            }

            return null;
        }

        void ResetMesh(MeshFilter mf)
        {
            if (!_baseVertsByMF.TryGetValue(mf, out var baseVerts)) return;
            var outMesh = EnsureDeformedMesh(mf);
            if (outMesh == null) return;

            outMesh.SetVertices(baseVerts);
            if (recalcNormals) outMesh.RecalculateNormals();
            outMesh.RecalculateBounds();

            if (updateMeshCollider)
            {
                UpdateMeshColliderSafe(mf, outMesh);
            }
        }

        void RestoreOriginalMeshes()
        {
            if (_affectedMeshFilters == null) return;

            foreach (var mf in _affectedMeshFilters)
            {
                if (mf == null) continue;

                Mesh src = null;
                if (_originalMeshByMF.TryGetValue(mf, out var srcFound) && srcFound != null)
                    src = srcFound;
                else
                    src = GetSavedSourceFor(mf); // 🛟 fallback snapshot

                if (src != null)
                {
                    mf.sharedMesh = src;

                    if (updateMeshCollider)
                    {
                        UpdateMeshColliderSafe(mf, src);
                    }
                }

#if QT_EDITOR
                // Nettoie le clone
                if (_deformedMeshByMF.TryGetValue(mf, out var clone) && clone != null)
                {
                    if (!UnityEditor.AssetDatabase.Contains(clone))
                        DestroyImmediate(clone);
                }
#endif
            }

            _deformedMeshByMF.Clear();
            _originalMeshByMF.Clear();
            _baseVertsByMF.Clear();
        }

        void CleanupColliderMeshes()
        {
            foreach (var pair in _colliderMeshByMF)
            {
                var mesh = pair.Value;
                if (mesh == null) continue;

#if QT_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(mesh);
                else
                    Destroy(mesh);
#else
                Destroy(mesh);
#endif
            }

            _colliderMeshByMF.Clear();
        }

        void UpdateMeshColliderSafe(MeshFilter mf, Mesh sourceMesh)
        {
            if (!updateMeshCollider || mf == null || sourceMesh == null) return;

            var col = mf.GetComponent<MeshCollider>();
            if (!col) return;

            if (!_colliderMeshByMF.TryGetValue(mf, out var colliderMesh) || colliderMesh == null)
            {
                colliderMesh = new Mesh
                {
                    name = sourceMesh.name.EndsWith("_QT_COLLIDER") ? sourceMesh.name : sourceMesh.name + "_QT_COLLIDER"
                };
                colliderMesh.MarkDynamic();
                _colliderMeshByMF[mf] = colliderMesh;
            }

            if (!TryBuildCleanColliderMesh(sourceMesh, colliderMesh))
            {
                col.sharedMesh = null;
                return;
            }

            col.sharedMesh = null;
            col.sharedMesh = colliderMesh;
        }

        static bool TryBuildCleanColliderMesh(Mesh sourceMesh, Mesh colliderMesh)
        {
            if (sourceMesh == null || colliderMesh == null) return false;

            var verts = sourceMesh.vertices;
            if (verts == null || verts.Length < 3) return false;

            colliderMesh.Clear();
            if (verts.Length > 65535)
                colliderMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            var cleanTriangles = new List<int>();
            const float edgeEpsilon = 1e-10f;
            const float areaEpsilon = 1e-12f;

            for (int sm = 0; sm < sourceMesh.subMeshCount; sm++)
            {
                var tris = sourceMesh.GetTriangles(sm);
                for (int i = 0; i <= tris.Length - 3; i += 3)
                {
                    int ia = tris[i];
                    int ib = tris[i + 1];
                    int ic = tris[i + 2];

                    if (ia < 0 || ib < 0 || ic < 0 ||
                        ia >= verts.Length || ib >= verts.Length || ic >= verts.Length)
                        continue;
                    if (ia == ib || ib == ic || ic == ia)
                        continue;

                    Vector3 a = verts[ia];
                    Vector3 b = verts[ib];
                    Vector3 c = verts[ic];

                    if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                        continue;

                    Vector3 ab = b - a;
                    Vector3 ac = c - a;
                    Vector3 bc = c - b;
                    if (ab.sqrMagnitude <= edgeEpsilon || ac.sqrMagnitude <= edgeEpsilon || bc.sqrMagnitude <= edgeEpsilon)
                        continue;
                    if (Vector3.Cross(ab, ac).sqrMagnitude <= areaEpsilon)
                        continue;

                    cleanTriangles.Add(ia);
                    cleanTriangles.Add(ib);
                    cleanTriangles.Add(ic);
                }
            }

            if (cleanTriangles.Count < 3)
                return false;

            colliderMesh.subMeshCount = 1;
            colliderMesh.SetVertices(verts);
            colliderMesh.SetTriangles(cleanTriangles, 0);
            colliderMesh.RecalculateBounds();
            return true;
        }

        static bool IsFinite(Vector3 v)
        {
            return IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // ───────────────────────── Handle Params ─────────────────────────
        struct HandleParam
        {
            public float3 hLocal;      // position du handle en LOCAL du déformeur
            public float heightLocal; // amplitude locale (compensée)
            public float radiusLocal; // rayon local
            public float yRef;        // seuil local
            public byte valid;       // 1/0
        }

        List<Transform> CollectHandles()
        {
            var list = new List<Transform>(4);
            if (handle) list.Add(handle);
            if (additionalHandles != null)
            {
                foreach (var h in additionalHandles)
                    if (h && !list.Contains(h)) list.Add(h);
            }
            return list;
        }

        float GetScaleFactorXZ(Transform src)
        {
            var s = src.lossyScale;
            float ax = Mathf.Abs(s.x);
            float az = Mathf.Abs(s.z);
            return scaleMetric switch
            {
                DOTSScaleMetric.MaxXZ => Mathf.Max(ax, az),
                DOTSScaleMetric.MinXZ => Mathf.Min(ax, az),
                DOTSScaleMetric.GeometricMeanXZ => Mathf.Sqrt(Mathf.Max(1e-8f, ax * az)),
                _ => (ax + az) * 0.5f, // AverageXZ
            };
        }

        HandleParam[] BuildHandleParams(List<Transform> hs)
        {
            var arr = new HandleParam[hs.Count];

            float safeRatio = Mathf.Max(1e-6f, Mathf.Abs(yDeformRatio));
            float syLocal = transform.localScale.y;
            if (Mathf.Abs(syLocal) < 1e-6f) syLocal = Mathf.Sign(syLocal == 0f ? 1f : syLocal) * 1e-6f;

            for (int h = 0; h < hs.Count; h++)
            {
                var th = hs[h];
                if (!th) { arr[h].valid = 0; continue; }

                // Position handle en LOCAL du déformeur
                Vector3 hLocal = transform.InverseTransformPoint(th.position);

                // Amplitude locale
                float heightLocal;
                if (compensateLocalScaleY)
                {
                    float worldDeltaAlongUp = Vector3.Dot(th.position - transform.position, transform.up);
                    if (useHandleZero) worldDeltaAlongUp -= handleZeroAlongUp;
                    heightLocal = (worldDeltaAlongUp * heightPerUnitY) / syLocal;
                }
                else
                {
                    float localDelta = hLocal.y;
                    if (useHandleZero)
                    {
                        float zeroLocal = transform.InverseTransformVector(transform.up * handleZeroAlongUp).y;
                        localDelta -= zeroLocal;
                    }
                    heightLocal = localDelta * heightPerUnitY;
                }
                if (invertDirection) heightLocal = -heightLocal;
                heightLocal *= safeRatio;

                // Rayon local
                float rLocal = radius;
                if (linkRadiusToScale)
                {
                    float k = Mathf.Max(1e-6f, GetScaleFactorXZ(th));
                    rLocal = (radiusLinkMode == DOTSRadiusLinkMode.MultiplyByScale) ? (radius * k) : (radius / k);
                }
                rLocal = Mathf.Max(0.0001f, rLocal);

                // yMin réf locale
                float yRef = yMinRelativeToHandle ? (hLocal.y + yMin) : yMin;

                arr[h] = new HandleParam
                {
                    hLocal = hLocal,
                    heightLocal = heightLocal,
                    radiusLocal = rLocal,
                    yRef = yRef,
                    valid = 1
                };
            }

            return arr;
        }

        // ───────────────────────── Job ─────────────────────────
#if UNITY_BURST
        [BurstCompile]
#endif
        struct DeformJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> baseVertices;
            [WriteOnly] public NativeArray<float3> outVertices;

            // Transforms
            [ReadOnly] public float4x4 mfLocalToWorld;
            [ReadOnly] public float4x4 mfWorldToLocal;
            [ReadOnly] public float4x4 deformLocalToWorld;
            [ReadOnly] public float4x4 deformWorldToLocal;

            // Handles
            [ReadOnly] public NativeArray<HandleParam> handles;

            // Shape & Falloffs
            [ReadOnly] public DOTSDeformShape shape;
            [ReadOnly] public DOTSFalloff falloff;
            [ReadOnly] public float gaussianSharpness;

            [ReadOnly] public byte useYMin;
            [ReadOnly] public float yFeather;
            [ReadOnly] public DOTSFalloff yMinFalloff;
            [ReadOnly] public float yMinGaussianSharp;

            // Clamp
            [ReadOnly] public byte clampWorldMinY;
            [ReadOnly] public float worldMinY;
            [ReadOnly] public byte clampOnlyAffected;

            public void Execute(int i)
            {
                float3 pL = TransformToDeformerLocal(baseVertices[i]);

                // Somme des contributions
                float deltaY = 0f;
                for (int h = 0; h < handles.Length; h++)
                {
                    var HP = handles[h];
                    if (HP.valid == 0) continue;

                    // XZ distance weight — Round (Euclidean) or Square (Chebyshev)
                    float dx = pL.x - HP.hLocal.x;
                    float dz = pL.z - HP.hLocal.z;
                    float dist = (shape == DOTSDeformShape.Square)
                        ? math.max(math.abs(dx), math.abs(dz))
                        : math.sqrt(dx * dx + dz * dz);
                    float t = math.clamp(1f - dist / HP.radiusLocal, 0f, 1f);

                    float w = EvalFalloff(falloff, t, gaussianSharpness);

                    // yMin (local)
                    if (useYMin != 0)
                    {
                        float wy;
                        if (yFeather <= 0f)
                        {
                            wy = (pL.y >= HP.yRef) ? 1f : 0f;
                        }
                        else
                        {
                            float ty = math.saturate((pL.y - HP.yRef) / yFeather);
                            wy = EvalFalloff(yMinFalloff, ty, yMinGaussianSharp);
                        }
                        w *= wy;
                        if (w <= 0f) continue;
                    }

                    deltaY += HP.heightLocal * w;
                }

                float3 newLocal = new float3(pL.x, pL.y + deltaY, pL.z);

                // Clamp monde
                if (clampWorldMinY != 0 && (clampOnlyAffected == 0 || math.abs(deltaY) > 0f))
                {
                    float3 worldNew = math.transform(deformLocalToWorld, newLocal);
                    if (worldNew.y < worldMinY)
                    {
                        worldNew.y = worldMinY;
                        newLocal = math.transform(deformWorldToLocal, worldNew);
                    }
                }

                // Retour dans l'espace du mesh
                float3 worldP = math.transform(deformLocalToWorld, newLocal);
                outVertices[i] = math.transform(mfWorldToLocal, worldP);
            }

            float3 TransformToDeformerLocal(float3 vLocalMF)
            {
                float3 world = math.transform(mfLocalToWorld, vLocalMF);
                return math.transform(deformWorldToLocal, world);
            }

            static float EvalFalloff(DOTSFalloff f, float t, float gauss)
            {
                t = math.saturate(t);
                switch (f)
                {
                    case DOTSFalloff.Parabolic: return t * t;
                    case DOTSFalloff.SmoothStep: return t * t * (3f - 2f * t);
                    case DOTSFalloff.Gaussian:
                        {
                            float x = (1f - t) * gauss;
                            return math.exp(-(x * x));
                        }
                    default: return t; // Linear
                }
            }
        }
    }
}
