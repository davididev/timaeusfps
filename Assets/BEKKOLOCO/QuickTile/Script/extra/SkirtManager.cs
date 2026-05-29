// SkirtManager.cs — Mono + DOTS bridge (sans NonUniformScale)
// - API inchangée : wallCount, scaleValue, ApplyVisuals(), Generate(), ...
// - Éditeur (hors Play) : rendu Mono (stretch Y-only / duplication) => feedback instantané
// - Play : si Entities présent, push d’état vers entité DOTS (ownerId) ; systèmes DOTS appliquent
// - Ce fichier n'inclut PAS les types DOTS (SkirtData, Systems) pour éviter les doublons.

using UnityEngine;
using System.Linq;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement;
#endif

using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using Unity.Collections;

namespace Bekkoloco
{
    [ExecuteAlways]
    public class SkirtManager : MonoBehaviour
    {
        [Header("Skirt Components")]
        public Transform skirt;
        public Transform wall;
        public Transform bottom;

        [Header("Wall Mode")]
        [SerializeField] private bool _useWallStretching = true;
        [Tooltip("Stretch the wall instead of duplicating it")]
        public bool useWallStretching
        {
            get => _useWallStretching;
            set
            {
                if (_useWallStretching != value)
                {
                    _useWallStretching = value;
                    MarkDirtyAndPushToDots();
                }
            }
        }

        [Header("Wall Settings")]
        [SerializeField] private float _targetWallHeight = 1f;
        public float targetWallHeight
        {
            get => _targetWallHeight;
            set
            {
                if (!Mathf.Approximately(_targetWallHeight, value))
                {
                    _targetWallHeight = value;
                    if (_useWallStretching) _scaleValue = value; // cohérence legacy
                    MarkDirtyAndPushToDots();
                }
            }
        }

        [Header("Legacy Settings (duplication mode)")]
        [SerializeField] private int _wallCount = 0;
        public int wallCount
        {
            get => _wallCount;
            set
            {
                if (_wallCount != value)
                {
                    _wallCount = value;
                    if (!_useWallStretching) MarkDirtyAndPushToDots();
                }
            }
        }

        [SerializeField] private float _wallStep = 1f;
        public float WallStep => _wallStep;

        [SerializeField] private float _scaleValue = 0f;
        public float scaleValue
        {
            get => _scaleValue;
            set
            {
                if (!Mathf.Approximately(_scaleValue, value))
                {
                    _scaleValue = value;
                    if (_useWallStretching) _targetWallHeight = value;
                    MarkDirtyAndPushToDots();
                }
            }
        }

        // ───────── Internes Mono
        bool _isDirty;
        Vector3 _originalWallScale = Vector3.one;
        Vector3 _originalWallPosition = Vector3.zero;
        bool _originalsInitialized;

        bool _lastUsedStretching = true;
        float _lastTargetHeight = 0f;
        int _lastWallCount = 0;
        bool _hasInitializedRuntime = false;

        // ───────── Pont DOTS
        EntityManager _em;
        Entity _rootEntity;
        int _ownerId;            // pour retrouver "notre" entité
        bool _worldResolved;

        void Awake()
        {
            _ownerId = GetInstanceID();
        }

        void OnEnable()
        {
            InitializeIfNeeded();

#if UNITY_EDITOR
            EditorApplication.delayCall += () =>
            {
                if (this != null && !EditorApplication.isPlaying && IsSafeToRun() && !IsInPrefabStage())
                {
                    SyncWithTileRule();
                    ApplyVisualsIfNeeded(); // feedback Editor (Mono)
                }
            };
#else
            // Play
            SyncWithTileRuleRuntime();
            ForceRuntimeUpdate();
#endif
        }

        void Start()
        {
#if !UNITY_EDITOR
            if (!_hasInitializedRuntime)
            {
                SyncWithTileRuleRuntime();
                ForceRuntimeUpdate();
            }
#endif
        }

        void Update()
        {
#if UNITY_EDITOR
            if (!EditorApplication.isPlaying)
            {
                if (_isDirty)
                {
                    ApplyWallTransform_Mono();
                    _isDirty = false;
                }
                return;
            }
#endif
            // Runtime
            if (_isDirty)
            {
                if (UseDotsNow())
                {
                    PushStateToDots(); // les systèmes DOTS feront l’update
                    _isDirty = false;
                }
                else
                {
                    ApplyWallTransform_Mono();
                    _isDirty = false;
                }
            }
        }

        void OnValidate()
        {
#if UNITY_EDITOR
            if (!IsSafeToRun()) return;
            _isDirty = true;

            EditorApplication.delayCall += () =>
            {
                if (this != null && IsSafeToRun() && !IsInPrefabStage())
                {
                    SyncWithTileRule();
                    ApplyWallTransform_Mono();
                }
            };
#endif
        }

        void InitializeIfNeeded()
        {
            if (!_originalsInitialized && wall != null)
            {
                _originalWallScale = wall.localScale;
                _originalWallPosition = wall.localPosition;
                _originalsInitialized = true;
            }
        }

        void MarkDirtyAndPushToDots()
        {
            _isDirty = true;
            if (Application.isPlaying && UseDotsNow())
                PushStateToDots();
        }

        // ───────── Helpers DOTS
        bool UseDotsNow()
        {
            if (!Application.isPlaying) return false;
            return ResolveDotsEntity();
        }

        bool ResolveDotsEntity()
        {
            if (_worldResolved && _em != default && _rootEntity != Entity.Null && _em.Exists(_rootEntity))
                return true;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null) return false;

            _em = world.EntityManager;

            using var q = _em.CreateEntityQuery(ComponentType.ReadOnly<Bekkoloco.DOTS.SkirtData>());
            using var es = q.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < es.Length; i++)
            {
                var e = es[i];
                var d = _em.GetComponentData<Bekkoloco.DOTS.SkirtData>(e);
                if (d.ownerId == _ownerId) // ← nécessite ownerId dans SkirtManagerDOTS.cs
                {
                    _rootEntity = e;
                    _worldResolved = true;
                    return true;
                }
            }
            return false;
        }

        void PushStateToDots()
        {
            if (!ResolveDotsEntity()) return;

            var d = _em.GetComponentData<Bekkoloco.DOTS.SkirtData>(_rootEntity);
            d.useWallStretching = _useWallStretching;
            d.targetWallHeight = _targetWallHeight;
            d.wallCount = _wallCount;
            d.wallStep = math.max(0.0001f, _wallStep);
            d.scaleValue = _scaleValue;
            d.isDirty = true;
            _em.SetComponentData(_rootEntity, d);

            if (!_em.HasComponent<Bekkoloco.DOTS.SkirtUpdateRequired>(_rootEntity))
                _em.AddComponent<Bekkoloco.DOTS.SkirtUpdateRequired>(_rootEntity);
        }

#if UNITY_EDITOR
        bool IsSafeToRun()
        {
            return gameObject.scene.IsValid() && !PrefabUtility.IsPartOfPrefabAsset(gameObject);
        }

        bool IsInPrefabStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            return stage != null && stage.IsPartOfPrefabContents(gameObject);
        }

        public void SyncWithTileRule()
        {
            if (!IsSafeToRun() || IsInPrefabStage()) return;

            var tilemap = transform.parent?.GetComponent<UnityEngine.Tilemaps.Tilemap>();
            if (tilemap == null) return;

            QuickTilemapEditor tilemapEditor = FindFirstObjectByType<QuickTilemapEditor>();
            if (tilemapEditor == null) return;

            var rule = tilemapEditor.tileRules.FirstOrDefault(r => r.customTargetTilemap == tilemap);
            if (rule != null)
            {
                if (_useWallStretching)
                {
                    float newHeight = rule.fixBase ? rule.yOffset : rule.sizeY;
                    if (!Mathf.Approximately(_targetWallHeight, newHeight))
                    {
                        _targetWallHeight = newHeight;
                        _scaleValue = newHeight;
                        _isDirty = true;
                    }
                }
                else
                {
                    int newWallCount = rule.fixBase ? Mathf.FloorToInt(rule.yOffset / _wallStep) : Mathf.RoundToInt(rule.sizeY);
                    float newScaleValue = rule.fixBase ? rule.yOffset * 10f : rule.sizeY;

                    if (_wallCount != newWallCount || !Mathf.Approximately(_scaleValue, newScaleValue))
                    {
                        _wallCount = newWallCount;
                        _scaleValue = newScaleValue;
                        _isDirty = true;
                    }
                }

                if (_isDirty) EditorUtility.SetDirty(this);
            }
        }
#endif

        // ───────── Runtime sync
        public void SyncWithTileRuleRuntime()
        {
            var tilemap = transform.parent?.GetComponent<UnityEngine.Tilemaps.Tilemap>();
            if (tilemap == null) return;

            var editor = FindFirstObjectByType<QuickTilemapEditor>();
            if (editor == null) return;

            var rule = editor.tileRules.FirstOrDefault(r => r.customTargetTilemap == tilemap);
            if (rule != null)
            {
                if (_useWallStretching)
                {
                    float newHeight = rule.fixBase ? rule.yOffset : rule.sizeY;
                    if (!Mathf.Approximately(_targetWallHeight, newHeight))
                    {
                        _targetWallHeight = newHeight;
                        _scaleValue = newHeight;
                        _isDirty = true;
                    }
                }
                else
                {
                    int newWallCount = rule.fixBase ? Mathf.FloorToInt(rule.yOffset / WallStep) : Mathf.RoundToInt(rule.sizeY);
                    float newScaleValue = rule.fixBase ? rule.yOffset * 10f : rule.sizeY;

                    if (_wallCount != newWallCount || !Mathf.Approximately(_scaleValue, newScaleValue))
                    {
                        _wallCount = newWallCount;
                        _scaleValue = newScaleValue;
                        _isDirty = true;
                    }
                }
            }
        }

        // ───────── Runtime init
        void ForceRuntimeUpdate()
        {
            if (UseDotsNow())
            {
                PushStateToDots(); // systèmes DOTS appliqueront
            }
            else
            {
                ApplyWallTransform_Mono();
            }

            _lastUsedStretching = _useWallStretching;
            _lastTargetHeight = _targetWallHeight;
            _lastWallCount = _wallCount;
            _hasInitializedRuntime = true;
        }

        void ApplyVisualsIfNeeded()
        {
            bool needsUpdate = !_hasInitializedRuntime
                               || (_useWallStretching != _lastUsedStretching)
                               || (_useWallStretching && !Mathf.Approximately(_targetWallHeight, _lastTargetHeight))
                               || (!_useWallStretching && _wallCount != _lastWallCount);

            if (needsUpdate)
            {
                ApplyWallTransform_Mono();

                _lastUsedStretching = _useWallStretching;
                _lastTargetHeight = _targetWallHeight;
                _lastWallCount = _wallCount;
                _hasInitializedRuntime = true;
            }
        }

        // ───────── Impl Mono (Éditeur + fallback)
        void ApplyWallTransform_Mono()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying && (!IsSafeToRun() || IsInPrefabStage()))
                return;
#endif
            InitializeIfNeeded();

            if (_useWallStretching) StretchWall_Mono();
            else GenerateWithDuplication_Mono();

            PositionBottom_Mono();

#if UNITY_EDITOR
            EditorUtility.SetDirty(gameObject);
#endif
        }

        void StretchWall_Mono()
        {
            if (wall == null) return;

            if (!_lastUsedStretching) CleanupDuplicatedWalls_Mono();

            if (_targetWallHeight > 0f)
            {
                wall.gameObject.SetActive(true);

                float stretchFactor = _targetWallHeight / Mathf.Max(0.0001f, _wallStep);

                Vector3 newScale = _originalWallScale;
                newScale.y *= stretchFactor;                 // Y-only en Mono
                wall.localScale = newScale;

                Vector3 newPosition = _originalWallPosition;
                newPosition.y = _originalWallPosition.y - ((_targetWallHeight - _wallStep) * 0.5f);
                wall.localPosition = newPosition;
            }
            else
            {
                wall.gameObject.SetActive(false);
            }
        }

        void GenerateWithDuplication_Mono()
        {
            if (wall == null) return;

            CleanupDuplicatedWalls_Mono();

            int wallsToGenerate = Mathf.Max(0, _wallCount);

            for (int i = 0; i < wallsToGenerate; i++)
            {
                GameObject newWall = Instantiate(wall.gameObject);
                newWall.name = $"Wall_{i}";
                newWall.SetActive(true);
                newWall.transform.SetParent(transform, false);
                newWall.transform.localPosition = wall.localPosition + new Vector3(0, -i * _wallStep, 0);
                newWall.transform.localRotation = wall.localRotation;
                newWall.transform.localScale = _originalWallScale;
            }

            wall.gameObject.SetActive(wallsToGenerate > 0);
            wall.localScale = _originalWallScale;
            wall.localPosition = _originalWallPosition;
        }

        void PositionBottom_Mono()
        {
            if (bottom == null || wall == null) return;

            Vector3 bottomPos = bottom.localPosition;

            if (_useWallStretching && _targetWallHeight > 0f)
            {
                bottomPos.y = wall.localPosition.y - (_targetWallHeight * 0.5f);
                bottom.gameObject.SetActive(true);
            }
            else if (!_useWallStretching)
            {
                bottomPos.y = _originalWallPosition.y - (_wallCount * _wallStep);
                bottom.gameObject.SetActive(_wallCount > 0);
            }
            else
            {
                bottom.gameObject.SetActive(false);
                return;
            }

            bottom.localPosition = bottomPos;
        }

        void CleanupDuplicatedWalls_Mono()
        {
            var childrenToDestroy = new System.Collections.Generic.List<Transform>();
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child.name.StartsWith("Wall_")) childrenToDestroy.Add(child);
            }

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.delayCall += () =>
                {
                    foreach (var child in childrenToDestroy)
                        if (child != null) DestroyImmediate(child.gameObject);
                };
            }
            else
#endif
            {
                foreach (var child in childrenToDestroy)
                    if (child != null) Destroy(child.gameObject);
            }
        }

        // ───────── API publique (inchangée)
        public void Generate()
        {
            if (UseDotsNow()) { PushStateToDots(); return; }
            ApplyWallTransform_Mono();
        }

        public void ApplyVisuals()
        {
#if UNITY_EDITOR
            if (!IsSafeToRun() || IsInPrefabStage()) return;
            SyncWithTileRule();
#else
            SyncWithTileRuleRuntime();
#endif
            if (UseDotsNow()) { PushStateToDots(); return; }
            ApplyWallTransform_Mono();

#if UNITY_EDITOR
            EditorUtility.SetDirty(gameObject);
#endif
        }

        public void SwitchToWallStretching()
        {
            useWallStretching = true;
            _isDirty = true;
        }

        public void SwitchToDuplicationMode()
        {
            useWallStretching = false;
            if (wall != null && _originalsInitialized)
            {
                wall.localScale = _originalWallScale;
                wall.localPosition = _originalWallPosition;
            }
            _isDirty = true;
        }

        public float GetCurrentWallHeight() =>
            _useWallStretching ? _targetWallHeight : (_wallCount * WallStep);

        public bool IsUsingStretching() => _useWallStretching;

        public void ForceUpdate()
        {
            _isDirty = true;
            if (UseDotsNow()) { PushStateToDots(); _isDirty = false; return; }
            ApplyWallTransform_Mono();
        }

        // ───────── Texture / Material Management

        public void UpdateGrassTexture(Texture2D newTexture) => ApplyTextureToRenderer(skirt, newTexture);
        
        public void UpdateWallTexture(Texture2D newTexture)
        {
            ApplyTextureToRenderer(wall, newTexture);
            // Duplicated walls
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.name.StartsWith("Wall_"))
                    ApplyTextureToRenderer(child, newTexture);
            }
        }

        public void UpdateCliffTexture(Texture2D newTexture) => ApplyTextureToRenderer(bottom, newTexture);

        public Texture2D GetGrassTexture() => GetTextureFromRenderer(skirt);
        public Texture2D GetWallTexture() => GetTextureFromRenderer(wall);
        public Texture2D GetCliffTexture() => GetTextureFromRenderer(bottom);

        // Legacy/Generic update (updates all)
        public void UpdateMaterialTexture(Texture2D newTexture)
        {
            if (newTexture == null) return;
            UpdateGrassTexture(newTexture);
            UpdateWallTexture(newTexture);
            UpdateCliffTexture(newTexture);

#if UNITY_EDITOR
            if (!Application.isPlaying) EditorUtility.SetDirty(gameObject);
#endif
        }

        private void ApplyTextureToRenderer(Transform target, Texture2D texture)
        {
            if (target == null) return;

            var r = target.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) return;

            // Use sharedMaterial for HasProperty check to avoid creating a material instance,
            // then use r.material (single instance) only once to set the texture.
            if (r.sharedMaterial.HasProperty("_MainTex"))
                r.material.SetTexture("_MainTex", texture);
            else if (r.sharedMaterial.HasProperty("_BaseMap"))
                r.material.SetTexture("_BaseMap", texture);
            else if (r.sharedMaterial.HasProperty("_BaseColorMap"))
                r.material.SetTexture("_BaseColorMap", texture);
        }

        private Texture2D GetTextureFromRenderer(Transform target)
        {
            if (target == null) return null;
            var r = target.GetComponent<Renderer>();
            if (r == null || r.sharedMaterial == null) return null;
            
            // Check properties
            if (r.sharedMaterial.HasProperty("_MainTex")) return r.sharedMaterial.GetTexture("_MainTex") as Texture2D;
            if (r.sharedMaterial.HasProperty("_BaseMap")) return r.sharedMaterial.GetTexture("_BaseMap") as Texture2D;
            if (r.sharedMaterial.HasProperty("_BaseColorMap")) return r.sharedMaterial.GetTexture("_BaseColorMap") as Texture2D;
            
            return null;
        }

        // ───────── Baker : ce Mono est l’authoring DOTS
        public class Baker : Unity.Entities.Baker<SkirtManager>
        {
            public override void Bake(SkirtManager a)
            {
                var root = GetEntity(TransformUsageFlags.Dynamic);

                var skirtE = a.skirt ? GetEntity(a.skirt, TransformUsageFlags.Dynamic) : Entity.Null;
                var wallE = a.wall ? GetEntity(a.wall, TransformUsageFlags.Dynamic) : Entity.Null;
                var bottomE = a.bottom ? GetEntity(a.bottom, TransformUsageFlags.Dynamic) : Entity.Null;

                AddComponent(root, new Bekkoloco.DOTS.SkirtData
                {
                    skirtEntity = skirtE,
                    wallEntity = wallE,
                    bottomEntity = bottomE,
                    useWallStretching = a._useWallStretching,
                    targetWallHeight = a._targetWallHeight,
                    wallCount = a._wallCount,
                    wallStep = math.max(0.0001f, a._wallStep),
                    scaleValue = a._scaleValue,
                    isDirty = true,
                    ownerId = a.GetInstanceID(), // ← requis
                });

                // NOTE: on ne rajoute pas NonUniformScale (pas dispo). Le système DOTS utilisera LocalTransform.Scale (uniform).
                AddComponent<Bekkoloco.DOTS.SkirtUpdateRequired>(root);
            }
        }
    }
}
