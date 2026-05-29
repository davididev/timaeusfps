// VegetationGPURenderer.cs
// GPU-instanced vegetation renderer using DrawMeshInstancedIndirect + Compute Shader culling.
// Attach to the same GameObject as QuickTilemapEditor.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bekkoloco
{
    [ExecuteAlways]
    public class VegetationGPURenderer : MonoBehaviour
    {
        private struct PrefabRenderSource
        {
            public Mesh mesh;
            public Material material;
            public int subMeshIndex;
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Per-entry GPU data
         *──────────────────────────────────────────────────────────────────*/

        /// <summary>One draw group = one mesh + material + N instances.</summary>
        public class DrawGroup
        {
            public Mesh mesh;
            public Material material;         // instancing-ready material clone
            public int subMeshIndex;
            public Bounds bounds;
            public bool useCpuInstancing;
            public bool billboardToCamera;
            public Matrix4x4[][] matrixBatches;

            // CPU-side instance list (authoritative)
            public QuickTilemapEditor.VegetationInstanceData[] instanceArray;
            public int instanceCount;

            // GPU buffers
            public ComputeBuffer allInstancesBuffer;
            public ComputeBuffer visibleInstancesBuffer;
            public ComputeBuffer argsBuffer;

            // Material property block (per-group buffer binding)
            public MaterialPropertyBlock mpb;

            public bool isDirty = true;

            public void ReleaseBuffers()
            {
                allInstancesBuffer?.Release();  allInstancesBuffer = null;
                visibleInstancesBuffer?.Release(); visibleInstancesBuffer = null;
                argsBuffer?.Release();          argsBuffer = null;
            }

            public void Release()
            {
                ReleaseBuffers();
                if (material != null)
                {
#if UNITY_EDITOR
                    // Don't destroy materials that are assets on disk (e.g. user-assigned cardMaterial)
                    if (!UnityEditor.AssetDatabase.Contains(material))
                    {
                        if (Application.isPlaying) Object.Destroy(material);
                        else Object.DestroyImmediate(material);
                    }
#else
                    if (Application.isPlaying) Object.Destroy(material);
#endif
                    material = null;
                }
            }
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Inspector fields
         *──────────────────────────────────────────────────────────────────*/

        [Tooltip("Compute shader for GPU frustum culling (VegetationCulling).")]
        public ComputeShader cullingShader;

        [Tooltip("Shader for Card mode (CPU instancing, no procedural:setup in shadow passes).")]
        public Shader instancedShader;

        [Tooltip("Shader for Grass mode (GPU indirect, procedural:setup in ALL passes).")]
        public Shader grassShader;

        [Tooltip("Enable GPU frustum + distance culling for Grass mode entries.")]
        public bool enableFrustumCulling = true;

        [Tooltip("Max draw distance (metres). Instances beyond this are culled.")]
        public float maxDrawDistance = 200f;

        [Tooltip("Shadow casting mode for vegetation.")]
        public ShadowCastingMode shadowMode = ShadowCastingMode.On;

        [Tooltip("Rendering layer mask.")]
        public int renderLayer = 0;

        /*──────────────────────────────────────────────────────────────────*
         *  Runtime state
         *──────────────────────────────────────────────────────────────────*/

        private readonly List<DrawGroup> drawGroups = new List<DrawGroup>();
        private readonly Dictionary<string, Mesh> bakedPrefabMeshCache = new Dictionary<string, Mesh>();
        private readonly Vector4[] frustumPlanesCache = new Vector4[6];
        private int cullingKernel = -1;
        private bool cullingKernelResolved;

        // Shader property IDs (cached)
        private static readonly int _AllInstances     = Shader.PropertyToID("_AllInstances");
        private static readonly int _VisibleInstances  = Shader.PropertyToID("_VisibleInstances");
        private static readonly int _ArgsBuffer        = Shader.PropertyToID("_ArgsBuffer");
        private static readonly int _FrustumPlanes     = Shader.PropertyToID("_FrustumPlanes");
        private static readonly int _InstanceCount     = Shader.PropertyToID("_InstanceCount");
        private static readonly int _MaxDistance       = Shader.PropertyToID("_MaxDistance");
        private static readonly int _CameraPosition    = Shader.PropertyToID("_CameraPosition");

        /*──────────────────────────────────────────────────────────────────*
         *  Public API
         *──────────────────────────────────────────────────────────────────*/

        /// <summary>
        /// Rebuild all draw groups from the current texture paint rules.
        /// Call after Populate or Clear in the editor.
        /// </summary>
        public void RebuildFromRules(List<QuickTilemapEditor.TexturePaintRule> rules)
        {
            ClearAllGroups();
            ClearGeneratedMeshCaches();

            if (rules == null) return;

            // Re-resolve culling kernel (shader may have been assigned after OnEnable)
            ResolveCullingKernel();

            foreach (var rule in rules)
            {
                if (rule?.vegetationEntries == null) continue;
                foreach (var entry in rule.vegetationEntries)
                {
                    if (entry.instances == null || entry.instances.Count == 0)
                        continue;

                    var instanceArray = entry.instances.ToArray();
                    DrawGroup group = null;

                    if (entry.mode == QuickTilemapEditor.VegetationMode.Card)
                    {
                        // ── Card mode: CPU instancing (DrawMeshInstanced) ──
                        if (entry.cardTexture == null && entry.cardMaterial == null) continue;

                        group = new DrawGroup
                        {
                            mesh = entry.placementSurface == QuickTilemapEditor.VegetationPlacementSurface.Skirt
                                ? GetOrCreateSingleQuadMesh(entry.cardWidth, entry.cardHeight)
                                : GetOrCreateCrossQuadMesh(entry.cardWidth, entry.cardHeight),
                            subMeshIndex = 0,
                            instanceArray = instanceArray,
                            instanceCount = entry.instances.Count,
                        };

                        if (entry.cardMaterial != null)
                        {
                            group.material = entry.cardMaterial;
                        }
                        else
                        {
                            group.material = CreateCardMaterialSimple(entry.cardTexture, entry.cardTint);
                        }

                        group.useCpuInstancing = true;
                        group.matrixBatches = BuildMatrixBatches(instanceArray);
                        group.isDirty = false;
                    }
                    else if (entry.mode == QuickTilemapEditor.VegetationMode.Grass)
                    {
                        // ── Grass mode: GPU indirect (DrawMeshInstancedIndirect + compute culling) ──
                        // No texture required — Zelda-style coloured blades

                        group = new DrawGroup
                        {
                            mesh = GetOrCreateGrassBladeMesh(entry.cardWidth, entry.cardHeight, entry.grassShape),
                            subMeshIndex = 0,
                            instanceArray = instanceArray,
                            instanceCount = entry.instances.Count,
                        };

                        if (entry.cardMaterial != null)
                        {
                            group.material = entry.cardMaterial;
                        }
                        else
                        {
                            group.material = CreateGrassMaterial(entry.cardTint);
                        }

                        // GPU indirect path — buffers uploaded by UploadAllDirty()
                        group.useCpuInstancing = false;
                        group.isDirty = true;
                    }
                    else if (entry.mode == QuickTilemapEditor.VegetationMode.Prefab)
                    {
                        // ── Prefab mode: extract all renderer/submesh pairs from the prefab ──
                        if (entry.prefab == null) continue;

                        foreach (var source in GetPrefabRenderSources(entry.prefab))
                        {
                            group = new DrawGroup
                            {
                                mesh = source.mesh,
                                material = CreatePrefabMaterial(source.material),
                                subMeshIndex = source.subMeshIndex,
                                instanceArray = instanceArray,
                                instanceCount = entry.instances.Count,
                                useCpuInstancing = true,
                                matrixBatches = BuildMatrixBatches(instanceArray),
                                isDirty = false,
                            };

                            group.mpb = new MaterialPropertyBlock();
                            group.bounds = ComputeGroupBounds(group.instanceArray);
                            drawGroups.Add(group);
                        }

                        continue;
                    }
                    else
                    {
                        continue;
                    }

                    group.mpb = new MaterialPropertyBlock();
                    group.bounds = ComputeGroupBounds(group.instanceArray);
                    drawGroups.Add(group);
                }
            }

            UploadAllDirty();

            // Force scene view repaint so instances appear immediately
#if UNITY_EDITOR
            UnityEditor.SceneView.RepaintAll();
#endif
        }

        /// <summary>Remove all draw groups and release GPU resources.</summary>
        public void ClearAllGroups()
        {
            foreach (var g in drawGroups) g.Release();
            drawGroups.Clear();
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Lifecycle
         *──────────────────────────────────────────────────────────────────*/

        void OnEnable()
        {
            cullingKernelResolved = false;
            ResolveCullingKernel();
#if UNITY_EDITOR
            UnityEditor.SceneView.duringSceneGui += OnSceneGUI;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.SceneView.duringSceneGui -= OnSceneGUI;
#endif
            ClearAllGroups();
            ClearGeneratedMeshCaches();
            _cachedWindZone = null;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Hook into scene view rendering to ensure vegetation draws every repaint.
        /// </summary>
        private void OnSceneGUI(UnityEditor.SceneView sceneView)
        {
            if (drawGroups.Count == 0) return;
            if (Event.current.type != EventType.Repaint) return;

            Camera cam = sceneView.camera;
            if (cam == null) return;

            RenderGroups(cam);
        }
#endif

        void Update()
        {
            if (drawGroups.Count == 0) return;

            // In play mode, use main camera
            if (Application.isPlaying)
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                RenderGroups(cam);
            }
            // In editor, rendering happens in OnSceneGUI
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Rendering
         *──────────────────────────────────────────────────────────────────*/

        // ── WindZone integration ──
        private static readonly int _WindZoneDirection = Shader.PropertyToID("_WindZoneDirection");
        private static readonly int _WindZoneStrength  = Shader.PropertyToID("_WindZoneStrength");
        private static readonly int _WindZoneSpeed     = Shader.PropertyToID("_WindZoneSpeed");
        private static readonly int _WindZoneTurbulence = Shader.PropertyToID("_WindZoneTurbulence");

        private void PushWindZoneToShader()
        {
            var windZone = FindFirstWindZone();
            if (windZone == null)
            {
                // No WindZone in scene → zero out globals so material params take over
                Shader.SetGlobalFloat(_WindZoneStrength, 0f);
                return;
            }

            // WindZone.transform.forward is the wind direction
            Vector3 dir = windZone.transform.forward;
            Shader.SetGlobalVector(_WindZoneDirection, new Vector4(dir.x, dir.y, dir.z, 0f));
            Shader.SetGlobalFloat(_WindZoneStrength, windZone.windMain);
            Shader.SetGlobalFloat(_WindZoneSpeed, windZone.windPulseMagnitude > 0.001f
                ? windZone.windPulseFrequency : 1f);
            Shader.SetGlobalFloat(_WindZoneTurbulence, windZone.windTurbulence);
        }

        private static WindZone _cachedWindZone;
        private static float _windZoneCacheTime;

        private static WindZone FindFirstWindZone()
        {
            // Cache for 0.5s to avoid FindObjectsByType every frame
            if (_cachedWindZone != null && Time.unscaledTime - _windZoneCacheTime < 0.5f)
                return _cachedWindZone;

            var zones = Object.FindObjectsByType<WindZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            _cachedWindZone = zones.Length > 0 ? zones[0] : null;
            _windZoneCacheTime = Time.unscaledTime;
            return _cachedWindZone;
        }

        private void RenderGroups(Camera cam)
        {
            if (cam == null) return;

            PushWindZoneToShader();
            UploadAllDirty();

            // Lazy resolve kernel (in case shader was assigned after OnEnable)
            if (!cullingKernelResolved)
                ResolveCullingKernel();

            // GPU frustum culling: available in both editor and play mode when enabled
            bool useGpuCulling = enableFrustumCulling && cullingShader != null && cullingKernel >= 0;

            if (useGpuCulling)
            {
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
                for (int i = 0; i < 6; i++)
                    frustumPlanesCache[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            }

            foreach (var group in drawGroups)
            {
                if (group.useCpuInstancing)
                {
                    DrawCpuInstancedGroup(group, cam);
                    continue;
                }

                if (group.instanceCount == 0 || group.allInstancesBuffer == null) continue;
                if (group.argsBuffer == null) continue;
                if (group.material == null || group.mesh == null) continue;

                // ── GPU Culling ──
                if (useGpuCulling)
                {
                    SetIndirectArgs(group, 0u);

                    cullingShader.SetBuffer(cullingKernel, _AllInstances, group.allInstancesBuffer);
                    cullingShader.SetBuffer(cullingKernel, _VisibleInstances, group.visibleInstancesBuffer);
                    cullingShader.SetBuffer(cullingKernel, _ArgsBuffer, group.argsBuffer);
                    cullingShader.SetVectorArray(_FrustumPlanes, frustumPlanesCache);
                    cullingShader.SetInt(_InstanceCount, group.instanceCount);
                    cullingShader.SetFloat(_MaxDistance, maxDrawDistance);
                    cullingShader.SetVector(_CameraPosition, cam.transform.position);

                    int threadGroups = Mathf.CeilToInt(group.instanceCount / 64f);
                    cullingShader.Dispatch(cullingKernel, Mathf.Max(1, threadGroups), 1, 1);

                    BindInstanceBuffer(group, group.visibleInstancesBuffer);
                }
                else
                {
                    // No culling shader → draw all (fallback)
                    SetIndirectArgs(group, (uint)group.instanceCount);

                    BindInstanceBuffer(group, group.allInstancesBuffer);
                }

                Graphics.DrawMeshInstancedIndirect(
                    group.mesh,
                    group.subMeshIndex,
                    group.material,
                    group.bounds,
                    group.argsBuffer,
                    0,
                    group.mpb,
                    shadowMode,
                    true,
                    renderLayer,
                    cam
                );
            }
        }

        private void DrawCpuInstancedGroup(DrawGroup group, Camera cam)
        {
            if (group.material == null || group.mesh == null) return;
            if (group.matrixBatches == null || group.matrixBatches.Length == 0) return;
            Matrix4x4[][] batches = group.matrixBatches;

            // Auto-enable GPU Instancing on the material if the shader supports it
            if (!group.material.enableInstancing && group.material.shader != null)
            {
                group.material.enableInstancing = true;
            }

            foreach (var batch in batches)
            {
                if (batch == null || batch.Length == 0) continue;

                Graphics.DrawMeshInstanced(
                    group.mesh,
                    group.subMeshIndex,
                    group.material,
                    batch,
                    batch.Length,
                    group.mpb,
                    shadowMode,
                    true,
                    renderLayer,
                    cam
                );
            }
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Internal helpers
         *──────────────────────────────────────────────────────────────────*/

        private void ResolveCullingKernel()
        {
            cullingKernelResolved = false;

            if (cullingShader != null)
            {
                try
                {
                    cullingKernel = cullingShader.FindKernel("CSMain");
                    cullingKernelResolved = true;
                    return;
                }
                catch
                {
                    Debug.LogWarning("[VegetationGPURenderer] Failed to resolve CSMain kernel on the assigned culling shader.");
                }
            }

            cullingKernel = -1;
        }

        private void UploadAllDirty()
        {
            foreach (var g in drawGroups)
            {
                if (g.useCpuInstancing) continue;
                if (!g.isDirty || g.instanceCount == 0) continue;
                UploadGroup(g);
                g.isDirty = false;
            }
        }

        private void UploadGroup(DrawGroup group)
        {
            group.ReleaseBuffers();

            int count = group.instanceCount;
            int stride = QuickTilemapEditor.VegetationInstanceData.Size; // 20 bytes

            group.allInstancesBuffer = new ComputeBuffer(count, stride, ComputeBufferType.Structured);
            group.allInstancesBuffer.SetData(group.instanceArray);

            group.visibleInstancesBuffer = new ComputeBuffer(count, stride, ComputeBufferType.Structured);

            // IndirectArguments: 5 uints (indexCount, instanceCount, startIndex, baseVertex, startInstance)
            group.argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            SetIndirectArgs(group, (uint)count);
            BindInstanceBuffer(group, group.allInstancesBuffer);
        }

        private static void SetIndirectArgs(DrawGroup group, uint instanceCount)
        {
            uint[] args =
            {
                group.mesh.GetIndexCount(group.subMeshIndex),
                instanceCount,
                group.mesh.GetIndexStart(group.subMeshIndex),
                (uint)group.mesh.GetBaseVertex(group.subMeshIndex),
                0
            };
            group.argsBuffer.SetData(args);
        }

        private static void BindInstanceBuffer(DrawGroup group, ComputeBuffer instanceBuffer)
        {
            if (group.mpb == null)
                group.mpb = new MaterialPropertyBlock();

            group.mpb.Clear();
            group.mpb.SetBuffer(_VisibleInstances, instanceBuffer);

            // Some Unity versions are more reliable when the StructuredBuffer is also bound on the material.
            group.material.SetBuffer(_VisibleInstances, instanceBuffer);
        }

        private IEnumerable<PrefabRenderSource> GetPrefabRenderSources(GameObject prefab)
        {
            if (prefab == null)
                yield break;

            Transform root = prefab.transform;
            MeshRenderer[] renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);

            foreach (var meshRenderer in renderers)
            {
                if (meshRenderer == null || !meshRenderer.enabled)
                    continue;

                MeshFilter meshFilter = meshRenderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;

                Mesh bakedMesh = GetOrCreateBakedPrefabMesh(root, meshFilter);
                Material[] sharedMaterials = meshRenderer.sharedMaterials;
                int subMeshCount = Mathf.Min(bakedMesh.subMeshCount, sharedMaterials.Length);

                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                {
                    Material sourceMaterial = sharedMaterials[subMeshIndex];
                    if (sourceMaterial == null)
                        continue;

                    yield return new PrefabRenderSource
                    {
                        mesh = bakedMesh,
                        material = sourceMaterial,
                        subMeshIndex = subMeshIndex
                    };
                }
            }
        }

        private Mesh GetOrCreateBakedPrefabMesh(Transform root, MeshFilter meshFilter)
        {
            Mesh sourceMesh = meshFilter.sharedMesh;
            Matrix4x4 localMatrix = root.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
            string cacheKey = GetPrefabMeshCacheKey(sourceMesh, localMatrix);

            if (bakedPrefabMeshCache.TryGetValue(cacheKey, out Mesh cachedMesh) && cachedMesh != null)
                return cachedMesh;

            Mesh bakedMesh = Object.Instantiate(sourceMesh);
            bakedMesh.name = $"{sourceMesh.name}_PrefabBaked";
            bakedMesh.hideFlags = HideFlags.HideAndDontSave;

            Vector3[] vertices = sourceMesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = localMatrix.MultiplyPoint3x4(vertices[i]);
            bakedMesh.vertices = vertices;

            Vector3[] normals = sourceMesh.normals;
            if (normals != null && normals.Length == vertices.Length)
            {
                Matrix4x4 normalMatrix = localMatrix.inverse.transpose;
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
                bakedMesh.normals = normals;
            }

            Vector4[] tangents = sourceMesh.tangents;
            if (tangents != null && tangents.Length == vertices.Length)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    Vector3 tangentDir = new Vector3(tangents[i].x, tangents[i].y, tangents[i].z);
                    tangentDir = localMatrix.MultiplyVector(tangentDir).normalized;
                    tangents[i] = new Vector4(tangentDir.x, tangentDir.y, tangentDir.z, tangents[i].w);
                }
                bakedMesh.tangents = tangents;
            }

            bakedMesh.RecalculateBounds();
            bakedPrefabMeshCache[cacheKey] = bakedMesh;
            return bakedMesh;
        }

        private static string GetPrefabMeshCacheKey(Mesh mesh, Matrix4x4 localMatrix)
        {
            return string.Format(
                "{0}|{1:F4},{2:F4},{3:F4},{4:F4}|{5:F4},{6:F4},{7:F4},{8:F4}|{9:F4},{10:F4},{11:F4},{12:F4}",
                mesh.GetInstanceID(),
                localMatrix.m00, localMatrix.m01, localMatrix.m02, localMatrix.m03,
                localMatrix.m10, localMatrix.m11, localMatrix.m12, localMatrix.m13,
                localMatrix.m20, localMatrix.m21, localMatrix.m22, localMatrix.m23
            );
        }

        private void ClearGeneratedMeshCaches()
        {
            foreach (var kvp in bakedPrefabMeshCache)
            {
                if (kvp.Value == null) continue;
                if (Application.isPlaying) Object.Destroy(kvp.Value);
                else Object.DestroyImmediate(kvp.Value);
            }
            bakedPrefabMeshCache.Clear();

            // Also clear cross-quad, single-quad, and grass blade caches
            foreach (var kvp in crossQuadCache)
            {
                if (kvp.Value != null)
                {
                    if (Application.isPlaying) Object.Destroy(kvp.Value);
                    else Object.DestroyImmediate(kvp.Value);
                }
            }
            crossQuadCache.Clear();

            foreach (var kvp in grassBladeCache)
            {
                if (kvp.Value != null)
                {
                    if (Application.isPlaying) Object.Destroy(kvp.Value);
                    else Object.DestroyImmediate(kvp.Value);
                }
            }
            grassBladeCache.Clear();

            foreach (var kvp in grassBladeCache3)
            {
                if (kvp.Value != null)
                {
                    if (Application.isPlaying) Object.Destroy(kvp.Value);
                    else Object.DestroyImmediate(kvp.Value);
                }
            }
            grassBladeCache3.Clear();

            foreach (var kvp in singleQuadCache)
            {
                if (kvp.Value != null)
                {
                    if (Application.isPlaying) Object.Destroy(kvp.Value);
                    else Object.DestroyImmediate(kvp.Value);
                }
            }
            singleQuadCache.Clear();
        }

        private static Matrix4x4[][] BuildMatrixBatches(QuickTilemapEditor.VegetationInstanceData[] instances)
        {
            if (instances == null || instances.Length == 0)
                return System.Array.Empty<Matrix4x4[]>();

            const int maxBatchSize = 1023;
            int batchCount = Mathf.CeilToInt(instances.Length / (float)maxBatchSize);
            Matrix4x4[][] batches = new Matrix4x4[batchCount][];

            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                int start = batchIndex * maxBatchSize;
                int count = Mathf.Min(maxBatchSize, instances.Length - start);
                Matrix4x4[] batch = new Matrix4x4[count];

                for (int i = 0; i < count; i++)
                {
                    var inst = instances[start + i];
                    batch[i] = Matrix4x4.TRS(
                        inst.position,
                        Quaternion.Euler(0f, inst.rotation * Mathf.Rad2Deg, 0f),
                        Vector3.one * inst.scale
                    );
                }

                batches[batchIndex] = batch;
            }

            return batches;
        }

        private static Matrix4x4[][] BuildBillboardMatrixBatches(
            QuickTilemapEditor.VegetationInstanceData[] instances,
            Camera cam)
        {
            if (instances == null || instances.Length == 0 || cam == null)
                return System.Array.Empty<Matrix4x4[]>();

            const int maxBatchSize = 1023;
            int batchCount = Mathf.CeilToInt(instances.Length / (float)maxBatchSize);
            Matrix4x4[][] batches = new Matrix4x4[batchCount][];

            Vector3 cameraUp = cam.transform.up;
            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
            {
                int start = batchIndex * maxBatchSize;
                int count = Mathf.Min(maxBatchSize, instances.Length - start);
                Matrix4x4[] batch = new Matrix4x4[count];

                for (int i = 0; i < count; i++)
                {
                    var inst = instances[start + i];
                    Vector3 toCamera = cam.transform.position - inst.position;
                    if (toCamera.sqrMagnitude < 0.0001f)
                        toCamera = -cam.transform.forward;

                    Quaternion faceCamera = Quaternion.LookRotation(toCamera.normalized, cameraUp);
                    Quaternion spin = Quaternion.AngleAxis(inst.rotation * Mathf.Rad2Deg, Vector3.forward);
                    batch[i] = Matrix4x4.TRS(
                        inst.position,
                        faceCamera * spin,
                        Vector3.one * inst.scale
                    );
                }

                batches[batchIndex] = batch;
            }

            return batches;
        }

        private Material CreatePrefabMaterial(Material source)
        {
            Material mat;
            if (source != null)
            {
                mat = new Material(source);
            }
            else
            {
                mat = new Material(Shader.Find("Standard"));
            }

            mat.enableInstancing = true;
            mat.hideFlags = HideFlags.HideAndDontSave;
            return mat;
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Cross-quad mesh for Card mode
         *──────────────────────────────────────────────────────────────────*/

        // Cache generated meshes by (width, height) to avoid duplicates
        private readonly Dictionary<Vector2, Mesh> crossQuadCache = new Dictionary<Vector2, Mesh>();
        private readonly Dictionary<Vector2, Mesh> singleQuadCache = new Dictionary<Vector2, Mesh>();
        private readonly Dictionary<Vector2, Mesh> grassBladeCache = new Dictionary<Vector2, Mesh>();
        private readonly Dictionary<Vector3, Mesh> grassBladeCache3 = new Dictionary<Vector3, Mesh>();

        /// <summary>
        /// Get or create a cross-shaped mesh (2 quads intersecting at 90°).
        /// Classic vegetation billboard geometry — looks good from any angle.
        /// </summary>
        private Mesh GetOrCreateCrossQuadMesh(float width, float height)
        {
            width = Mathf.Max(0.01f, width);
            height = Mathf.Max(0.01f, height);

            Vector2 key = new Vector2(width, height);
            if (crossQuadCache.TryGetValue(key, out Mesh cached) && cached != null)
                return cached;

            float hw = width * 0.5f;  // half width

            // 2 intersecting quads — classic billboard cross
            // Each quad has its own 4 vertices for correct UVs
            Vector3[] vertices = new Vector3[8]
            {
                // Quad A: along X axis
                new Vector3(-hw, 0,      0),  // 0 bottom-left
                new Vector3( hw, 0,      0),  // 1 bottom-right
                new Vector3( hw, height, 0),  // 2 top-right
                new Vector3(-hw, height, 0),  // 3 top-left

                // Quad B: along Z axis
                new Vector3(0, 0,      -hw),  // 4 bottom-left
                new Vector3(0, 0,       hw),  // 5 bottom-right
                new Vector3(0, height,  hw),  // 6 top-right
                new Vector3(0, height, -hw),  // 7 top-left
            };

            Vector2[] uvs = new Vector2[8]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1),
            };

            // Slightly upward-biased normals keep billboard cards readable under scene lighting.
            Vector3 quadANormal = new Vector3(0f, 0.35f, 1f).normalized;
            Vector3 quadBNormal = new Vector3(1f, 0.35f, 0f).normalized;
            Vector3[] normals = new Vector3[8]
            {
                quadANormal, quadANormal, quadANormal, quadANormal,
                quadBNormal, quadBNormal, quadBNormal, quadBNormal,
            };

            // Single-sided geometry is enough because the shader already uses Cull Off.
            int[] triangles = new int[12]
            {
                // Quad A
                0, 3, 1,   1, 3, 2,
                // Quad B
                4, 7, 5,   5, 7, 6,
            };

            Mesh mesh = new Mesh
            {
                name = $"CrossQuad_{width:F1}x{height:F1}",
                vertices = vertices,
                uv = uvs,
                normals = normals,
                triangles = triangles,
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.bounds = new Bounds(
                new Vector3(0f, height * 0.5f, 0f),
                new Vector3(width, height, width)
            );
            mesh.RecalculateTangents();

            crossQuadCache[key] = mesh;
            return mesh;
        }

        /// <summary>
        /// Zelda-style grass blade mesh: two intersecting triangular blades.
        /// Each blade is a triangle: wide at the base, pointed at the tip.
        /// Y=0 at base, Y=height at tip (used for wind weight + color gradient).
        /// </summary>
        private Mesh GetOrCreateGrassBladeMesh(float width, float height,
            QuickTilemapEditor.GrassShape shape = QuickTilemapEditor.GrassShape.Triangle)
        {
            width = Mathf.Max(0.01f, width);
            height = Mathf.Max(0.01f, height);

            // Cache key includes shape
            Vector3 key = new Vector3(width, height, (float)shape);
            if (grassBladeCache3.TryGetValue(key, out Mesh cached) && cached != null)
                return cached;

            float hw = width * 0.5f;
            Vector3 normal = new Vector3(0f, 0.7f, 0.7f).normalized;

            Vector3[] vertices;
            Vector2[] uvs;
            Vector3[] normals;
            int[] triangles;

            switch (shape)
            {
                default:
                case QuickTilemapEditor.GrassShape.Triangle:
                    // Single triangle — pointed tip (original)
                    vertices = new Vector3[3]
                    {
                        new Vector3(-hw, 0f, 0f),
                        new Vector3( hw, 0f, 0f),
                        new Vector3( 0f, height, 0f),
                    };
                    uvs = new Vector2[3]
                    {
                        new Vector2(0f, 0f),
                        new Vector2(1f, 0f),
                        new Vector2(0.5f, 1f),
                    };
                    normals = new Vector3[3] { normal, normal, normal };
                    triangles = new int[3] { 0, 2, 1 };
                    break;

                case QuickTilemapEditor.GrassShape.Blade:
                    // Thin rectangle — straight blade
                    float bw = hw * 0.4f; // narrower than triangle
                    vertices = new Vector3[4]
                    {
                        new Vector3(-bw, 0f, 0f),       // 0 base-left
                        new Vector3( bw, 0f, 0f),       // 1 base-right
                        new Vector3( bw, height, 0f),   // 2 top-right
                        new Vector3(-bw, height, 0f),   // 3 top-left
                    };
                    uvs = new Vector2[4]
                    {
                        new Vector2(0f, 0f),
                        new Vector2(1f, 0f),
                        new Vector2(1f, 1f),
                        new Vector2(0f, 1f),
                    };
                    normals = new Vector3[4] { normal, normal, normal, normal };
                    triangles = new int[6] { 0, 3, 1, 1, 3, 2 };
                    break;

                case QuickTilemapEditor.GrassShape.Fan:
                    // Trapezoid — narrow base, wider top (fan shape)
                    float topW = hw * 1.5f; // top is wider than base
                    vertices = new Vector3[4]
                    {
                        new Vector3(-hw * 0.4f, 0f, 0f),   // 0 base-left (narrow)
                        new Vector3( hw * 0.4f, 0f, 0f),   // 1 base-right (narrow)
                        new Vector3( topW, height, 0f),     // 2 top-right (wide)
                        new Vector3(-topW, height, 0f),     // 3 top-left (wide)
                    };
                    uvs = new Vector2[4]
                    {
                        new Vector2(0.3f, 0f),
                        new Vector2(0.7f, 0f),
                        new Vector2(1f, 1f),
                        new Vector2(0f, 1f),
                    };
                    normals = new Vector3[4] { normal, normal, normal, normal };
                    triangles = new int[6] { 0, 3, 1, 1, 3, 2 };
                    break;
            }

            Mesh mesh = new Mesh
            {
                name = $"GrassBlade_{shape}_{width:F2}x{height:F2}",
                vertices = vertices,
                uv = uvs,
                normals = normals,
                triangles = triangles,
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.bounds = new Bounds(
                new Vector3(0f, height * 0.5f, 0f),
                new Vector3(width * 2f, height, width * 2f)
            );
            mesh.RecalculateTangents();

            grassBladeCache3[key] = mesh;
            return mesh;
        }

        private Mesh GetOrCreateSingleQuadMesh(float width, float height)
        {
            width = Mathf.Max(0.01f, width);
            height = Mathf.Max(0.01f, height);

            Vector2 key = new Vector2(width, height);
            if (singleQuadCache.TryGetValue(key, out Mesh cached) && cached != null)
                return cached;

            float hw = width * 0.5f;

            Vector3[] vertices = new Vector3[4]
            {
                new Vector3(-hw, 0f, 0f),
                new Vector3( hw, 0f, 0f),
                new Vector3( hw, height, 0f),
                new Vector3(-hw, height, 0f),
            };

            Vector2[] uvs = new Vector2[4]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };

            Vector3[] normals = new Vector3[4]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
            };

            int[] triangles = new int[6]
            {
                0, 3, 1,
                1, 3, 2,
            };

            Mesh mesh = new Mesh
            {
                name = $"CardQuad_{width:F1}x{height:F1}",
                vertices = vertices,
                uv = uvs,
                normals = normals,
                triangles = triangles,
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.bounds = new Bounds(
                new Vector3(0f, height * 0.5f, 0f),
                new Vector3(width, height, 0.01f)
            );
            mesh.RecalculateTangents();

            singleQuadCache[key] = mesh;
            return mesh;
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Card material creation
         *──────────────────────────────────────────────────────────────────*/

        /// <summary>
        /// Creates an instancing-ready material for Card mode vegetation.
        /// Uses the VegetationInstanced shader with alpha cutout.
        /// </summary>
        /// <summary>
        /// Creates a simple instancing-ready material for Card mode vegetation.
        /// Uses VegetationInstanced shader (SubShader 2 = standard instancing + wind).
        /// Falls back to Standard if VegetationInstanced is not found.
        /// </summary>
        private Material CreateCardMaterialSimple(Texture2D texture, Color tint)
        {
            // Use VegetationInstanced (SubShader 2 handles CPU instancing + wind)
            Shader shader = instancedShader != null
                ? instancedShader
                : Shader.Find("BEKKOLOCO/VegetationInstanced");
            if (shader == null) shader = Shader.Find("Standard");

            if (shader == null)
            {
                Debug.LogWarning("[VegetationGPURenderer] No suitable shader found for card material.");
                return null;
            }

            Material mat = new Material(shader);
            mat.enableInstancing = true;
            mat.hideFlags = HideFlags.HideAndDontSave;

            // Set texture and properties (defaults match shader)
            mat.SetTexture("_MainTex", texture);
            mat.SetColor("_Color", tint);
            mat.SetFloat("_Cutoff", 0.516f);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            return mat;
        }

        /// <summary>
        /// Creates a material for Grass mode (GPU indirect, Zelda-style coloured blades).
        /// Uses VegetationGrass shader (procedural:setup in ALL passes, no texture needed).
        /// </summary>
        private Material CreateGrassMaterial(Color topColor)
        {
            Shader shader = grassShader != null
                ? grassShader
                : Shader.Find("BEKKOLOCO/VegetationGrass");
            if (shader == null)
            {
                Debug.LogWarning("[VegetationGPURenderer] VegetationGrass shader not found.");
                shader = Shader.Find("Standard");
            }

            Material mat = new Material(shader);
            mat.enableInstancing = true;
            mat.hideFlags = HideFlags.HideAndDontSave;

            mat.SetColor("_Color", topColor);
            // Bottom color = darker version of top color
            Color bottom = new Color(topColor.r * 0.45f, topColor.g * 0.55f, topColor.b * 0.4f, 1f);
            mat.SetColor("_BottomColor", bottom);

            return mat;
        }

        private Material CreateCardMaterialFromSource(Material source, Texture2D texture, Color tint)
        {
            if (source == null)
                return CreateCardMaterialSimple(texture, tint);

            Material mat = new Material(source);
            mat.enableInstancing = true;
            mat.hideFlags = HideFlags.HideAndDontSave;
            ApplyCardVisualProperties(mat, texture, tint, preserveSurfaceSettings: true);
            return mat;
        }

        private static void ApplyCardVisualProperties(Material mat, Texture2D texture, Color tint, bool preserveSurfaceSettings)
        {
            if (mat == null)
                return;

            if (texture != null)
            {
                if (mat.HasProperty("_MainTex"))
                    mat.SetTexture("_MainTex", texture);
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", texture);
            }

            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", tint);
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", tint);

            if (!preserveSurfaceSettings)
            {
                if (mat.HasProperty("_Cutoff"))
                    mat.SetFloat("_Cutoff", 0.516f);
            }

            mat.enableInstancing = true;
            if (!preserveSurfaceSettings)
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        /// <summary>
        /// Legacy: Creates material with VegetationInstanced shader (for GPU indirect path).
        /// Kept for backward compatibility but no longer used by default for cards.
        /// </summary>
        private Material CreateCardMaterial(Texture2D texture, Color tint)
        {
            Shader shader = instancedShader != null
                ? instancedShader
                : Shader.Find("BEKKOLOCO/VegetationInstanced");

            if (shader == null)
            {
                Debug.LogWarning("[VegetationGPURenderer] VegetationInstanced shader not found, falling back to Standard.");
                shader = Shader.Find("Standard");
            }

            Material mat = new Material(shader);
            mat.SetTexture("_MainTex", texture);
            mat.SetColor("_Color", tint);
            mat.SetFloat("_Cutoff", 0.516f);
            mat.enableInstancing = true;
            mat.hideFlags = HideFlags.HideAndDontSave;
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;

            return mat;
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Vegetation refresh after mesh changes (dig volumes, deform…)
         *──────────────────────────────────────────────────────────────────*/

        /// <summary>
        /// Re-raycast every vegetation instance onto the current mesh surface
        /// and remove any that fall inside a QuickTileDigVolume.
        /// Call this after the procedural mesh has been rebuilt.
        /// </summary>
        public void RefreshVegetationPositions(List<QuickTilemapEditor.TexturePaintRule> rules)
        {
            if (rules == null) return;

            // Ensure physics colliders are up to date after mesh rebuild
            Physics.SyncTransforms();

            // Collect all active dig volumes in the scene
            var digVolumes = FindObjectsByType<QuickTileDigVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            bool anyChanged = false;

            foreach (var rule in rules)
            {
                if (rule?.vegetationEntries == null) continue;
                foreach (var entry in rule.vegetationEntries)
                {
                    if (entry.instances == null || entry.instances.Count == 0)
                        continue;

                    int removed = 0;
                    int removedWithoutSurface = 0;
                    for (int i = entry.instances.Count - 1; i >= 0; i--)
                    {
                        var inst = entry.instances[i];
                        Vector3 pos = inst.position;

                        // 1) Check if inside any dig volume → remove
                        bool insideDig = false;
                        if (digVolumes != null)
                        {
                            foreach (var dv in digVolumes)
                            {
                                if (dv == null || !dv.gameObject.activeInHierarchy) continue;
                                float dist = dv.EvaluateCarveDistanceWorld(pos);
                                if (dist <= 0f)
                                {
                                    insideDig = true;
                                    break;
                                }
                            }
                        }

                        if (insideDig)
                        {
                            entry.instances.RemoveAt(i);
                            removed++;
                            anyChanged = true;
                            continue;
                        }

                        // 2) Re-raycast Y position onto current mesh
                        Vector3 probe = new Vector3(pos.x, 100f, pos.z);
                        if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit, 250f))
                        {
                            float newY = hit.point.y + entry.yOffset;
                            if (Mathf.Abs(newY - pos.y) > 0.001f)
                            {
                                inst.position = new Vector3(pos.x, newY, pos.z);
                                entry.instances[i] = inst;
                                anyChanged = true;
                            }
                        }
                        else
                        {
                            entry.instances.RemoveAt(i);
                            removed++;
                            removedWithoutSurface++;
                            anyChanged = true;
                        }
                    }

                    if (removed > 0)
                        Debug.Log($"[VegetationGPURenderer] Removed {removed} stale vegetation instances for entry {entry.mode} ({removedWithoutSurface} without ground surface)");
                }
            }

            if (anyChanged)
            {
                RebuildFromRules(rules);
#if UNITY_EDITOR
                // Mark dirty so changes are saved
                var editor = GetComponent<QuickTilemapEditor>();
                if (editor != null)
                    UnityEditor.EditorUtility.SetDirty(editor);
#endif
            }
        }

        /// <summary>
        /// Check if a world position is inside any active dig volume.
        /// </summary>
        public static bool IsInsideAnyDigVolume(Vector3 worldPos, QuickTileDigVolume[] digVolumes)
        {
            if (digVolumes == null) return false;
            foreach (var dv in digVolumes)
            {
                if (dv == null || !dv.gameObject.activeInHierarchy) continue;
                if (dv.EvaluateCarveDistanceWorld(worldPos) <= 0f)
                    return true;
            }
            return false;
        }

        /*──────────────────────────────────────────────────────────────────*
         *  Bounds
         *──────────────────────────────────────────────────────────────────*/

        private static Bounds ComputeGroupBounds(QuickTilemapEditor.VegetationInstanceData[] instances)
        {
            if (instances == null || instances.Length == 0)
                return new Bounds(Vector3.zero, Vector3.one * 1000f);

            Vector3 min = instances[0].position;
            Vector3 max = instances[0].position;
            for (int i = 1; i < instances.Length; i++)
            {
                Vector3 p = instances[i].position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            // Expand bounds generously
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = (max - min) + Vector3.one * 100f;
            return new Bounds(center, size);
        }
    }
}
