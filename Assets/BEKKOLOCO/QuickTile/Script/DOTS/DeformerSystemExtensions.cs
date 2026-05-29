using UnityEngine;
using System.Linq;
// Import explicite du namespace DOTS pour utiliser RadialHillDeformer
using Bekkoloco.DOTS;

#if UNITY_AI_NAVIGATION
using Unity.AI.Navigation;
#endif

namespace Bekkoloco
{
    /// <summary>
    /// Extensions et utilitaires pour maintenir la compatibilité avec les systèmes existants
    /// qui utilisent RadialHillDeformer
    /// </summary>
    public static class DeformerSystemExtensions
    {
        /// <summary>
        /// Trouve tous les déformeurs (anciens et nouveaux) dans la scène
        /// </summary>
        public static IRadialDeformer[] FindAllRadialDeformers()
        {
            var results = new System.Collections.Generic.List<IRadialDeformer>();

            // Chercher la classe RadialHillDeformer du namespace DOTS
            var deformers = Object.FindObjectsByType<Bekkoloco.DOTS.RadialHillDeformer>(FindObjectsSortMode.None);

            // Utiliser le wrapper approprié selon la configuration
            results.AddRange(deformers.Select(d => new RadialDeformerWrapper(d)));

            return results.ToArray();
        }

        /// <summary>
        /// Trouve tous les déformeurs avec des handles actifs
        /// </summary>
        public static IRadialDeformer[] FindActiveRadialDeformers()
        {
            return FindAllRadialDeformers()
                .Where(d => d.HasActiveHandles())
                .ToArray();
        }

        /// <summary>
        /// Compte le nombre total de handles dans la scène
        /// </summary>
        public static int CountTotalHandles()
        {
            return FindAllRadialDeformers()
                .Sum(d => d.GetHandleCount());
        }

        /// <summary>
        /// Force la mise à jour de tous les déformeurs
        /// </summary>
        public static void ForceUpdateAllDeformers()
        {
            var deformers = FindAllRadialDeformers();
            Debug.Log($"[DeformerSystemExtensions] Forcing update on {deformers.Length} deformers");

            foreach (var deformer in deformers)
            {
                deformer.ForceUpdate();
            }
        }

        /// <summary>
        /// Obtient des statistiques sur les déformeurs de la scène
        /// </summary>
        public static DeformerStats GetSceneDeformerStats()
        {
            var allDeformers = FindAllRadialDeformers();
            var activeDeformers = allDeformers.Where(d => d.HasActiveHandles()).ToArray();
            var optimizedDeformers = allDeformers.Where(d => d.IsRuntimeOptimized).ToArray();

            return new DeformerStats
            {
                totalDeformers = allDeformers.Length,
                activeDeformers = activeDeformers.Length,
                totalHandles = CountTotalHandles(),
                runtimeOptimizedCount = optimizedDeformers.Length
            };
        }

        /// <summary>
        /// Valide la configuration des déformeurs et retourne les erreurs trouvées
        /// </summary>
        public static string[] ValidateDeformerConfiguration()
        {
            var errors = new System.Collections.Generic.List<string>();
            var deformers = FindAllRadialDeformers();

            foreach (var deformer in deformers)
            {
                if (deformer.GameObject == null)
                {
                    errors.Add("Found null GameObject in deformer");
                    continue;
                }

                if (!deformer.HasActiveHandles())
                {
                    errors.Add($"Deformer '{deformer.GameObject.name}' has no active handles");
                }

                if (deformer.GetHandleCount() > 50)
                {
                    errors.Add($"Deformer '{deformer.GameObject.name}' has {deformer.GetHandleCount()} handles (performance warning)");
                }
            }

            return errors.ToArray();
        }
    }

    /// <summary>
    /// Structure pour les statistiques des déformeurs
    /// </summary>
    public struct DeformerStats
    {
        public int totalDeformers;
        public int activeDeformers;
        public int totalHandles;
        public int runtimeOptimizedCount;

        public override string ToString()
        {
            return $"Deformers: {totalDeformers} total, {activeDeformers} active, {totalHandles} handles, {runtimeOptimizedCount} optimized";
        }
    }

    /// <summary>
    /// Interface commune pour tous les types de déformeurs
    /// </summary>
    public interface IRadialDeformer
    {
        bool HasActiveHandles();
        int GetHandleCount();
        void ForceUpdate();
        bool IsRuntimeOptimized { get; }
        GameObject GameObject { get; }
    }

    /// <summary>
    /// Wrapper universel pour RadialHillDeformer (la classe DOTS)
    /// </summary>
    public class RadialDeformerWrapper : IRadialDeformer
    {
        private readonly Bekkoloco.DOTS.RadialHillDeformer _deformer;

        public RadialDeformerWrapper(Bekkoloco.DOTS.RadialHillDeformer deformer)
        {
            _deformer = deformer;
        }

        public bool HasActiveHandles()
        {
            if (_deformer == null) return false;

            bool hasHandle = _deformer.handle != null;
            bool hasAdditional = _deformer.additionalHandles?.Any(h => h != null) == true;

            return hasHandle || hasAdditional;
        }

        public int GetHandleCount()
        {
            if (_deformer == null) return 0;

            int count = 0;
            if (_deformer.handle != null) count++;
            if (_deformer.additionalHandles != null)
            {
                count += _deformer.additionalHandles.Count(h => h != null);
            }

            return count;
        }

        public void ForceUpdate()
        {
            if (_deformer != null)
            {
                // Utiliser la méthode de la classe RadialHillDeformer DOTS
                try
                {
                    // Appeler directement la méthode ForceUpdate
                    _deformer.ForceUpdate();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[RadialDeformerWrapper] Could not force update: {e.Message}");
                }
            }
        }

        public bool IsRuntimeOptimized
        {
            get
            {
                if (_deformer != null)
                {
                    // Utiliser la propriété IsRuntimeOptimized de la classe DOTS
                    return _deformer.IsRuntimeOptimized;
                }
                return false;
            }
        }

        public GameObject GameObject => _deformer?.gameObject;
    }
}

/// <summary>
/// Utilitaires spécifiques pour QuickTilemapEditor
/// </summary>
namespace Bekkoloco.QuickTile
{
    public static class QuickTileDeformerUtils
    {
        /// <summary>
        /// Méthode de compatibilité pour QuickTilemapEditor_LevelSystem
        /// </summary>
        public static void WaitForDeformersAndBuildNavMesh(MonoBehaviour context, object navSurface, float delay = 0.5f)
        {
            context.StartCoroutine(WaitForDeformersCoroutine(navSurface, delay));
        }

        /// <summary>
        /// Version asynchrone pour attendre les déformeurs et construire le NavMesh
        /// </summary>
        public static void WaitForDeformersAndBuildNavMeshAsync(MonoBehaviour context, object navSurface, System.Action onComplete = null, float delay = 0.5f)
        {
            context.StartCoroutine(WaitForDeformersCoroutineAsync(navSurface, onComplete, delay));
        }

        private static System.Collections.IEnumerator WaitForDeformersCoroutine(object navSurface, float delay)
        {
            if (navSurface == null)
            {
                Debug.LogWarning("[QuickTilemapEditor] NavMeshSurface is null, cannot build NavMesh");
                yield break;
            }

            Debug.Log($"[QuickTilemapEditor] Waiting {delay}s for terrain deformation before building NavMesh...");

            // Attendre que les déformeurs appliquent leurs déformations
            yield return new UnityEngine.WaitForSeconds(delay);

            // Forcer la mise à jour de tous les déformeurs
            DeformerSystemExtensions.ForceUpdateAllDeformers();

            // Attendre un frame pour que les mises à jour soient appliquées
            yield return null;

            // Vérifier qu'il y a des handles actifs
            var deformers = DeformerSystemExtensions.FindActiveRadialDeformers();
            int totalHandles = DeformerSystemExtensions.CountTotalHandles();

            if (totalHandles > 0)
            {
                Debug.Log($"[QuickTilemapEditor] Found {totalHandles} deformer handles across {deformers.Length} deformers, building NavMesh...");
            }
            else
            {
                Debug.Log("[QuickTilemapEditor] No active deformer handles found, building NavMesh anyway...");
            }

            // Construire le NavMesh avec le terrain déformé
#if UNITY_AI_NAVIGATION
            if (surface != null)
            {
                surface.BuildNavMesh();
                Debug.Log("[QuickTilemapEditor] NavMesh rebuilt after terrain deformation");
            }
#else
            Debug.LogWarning("[QuickTilemapEditor] AI Navigation package not installed. Cannot build NavMesh automatically.");
#endif
        }

        private static System.Collections.IEnumerator WaitForDeformersCoroutineAsync(object navSurface, System.Action onComplete, float delay)
        {
            yield return WaitForDeformersCoroutine(navSurface, delay);
            onComplete?.Invoke();
        }

        /// <summary>
        /// Méthode de compatibilité pour trouver tous les déformeurs dans QuickTilemapEditor
        /// </summary>
        public static IRadialDeformer[] FindObjectsOfType_RadialHillDeformer()
        {
            return DeformerSystemExtensions.FindAllRadialDeformers();
        }

        /// <summary>
        /// Valide que tous les déformeurs sont correctement configurés
        /// </summary>
        public static bool ValidateDeformersForTileSystem()
        {
            var errors = DeformerSystemExtensions.ValidateDeformerConfiguration();

            if (errors.Length > 0)
            {
                Debug.LogWarning($"[QuickTilemapEditor] Found {errors.Length} deformer configuration issues:");
                foreach (var error in errors)
                {
                    Debug.LogWarning($"[QuickTilemapEditor] - {error}");
                }
                return false;
            }

            var stats = DeformerSystemExtensions.GetSceneDeformerStats();
            Debug.Log($"[QuickTilemapEditor] Deformer validation passed: {stats}");
            return true;
        }

        /// <summary>
        /// Force la synchronisation de tous les déformeurs avec les TileRules
        /// </summary>
        public static void SyncAllDeformersWithTileRules()
        {
            var deformers = Object.FindObjectsByType<Bekkoloco.DOTS.RadialHillDeformer>(FindObjectsSortMode.None);
            int syncCount = 0;

            foreach (var deformer in deformers)
            {
                if (deformer != null)
                {
                    // Appeler directement SyncWithTileRuleRuntime
                    try
                    {
                        deformer.SyncWithTileRuleRuntime();
                        syncCount++;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"[QuickTilemapEditor] Could not sync {deformer.name}: {e.Message}");
                    }
                }
            }

            Debug.Log($"[QuickTilemapEditor] Synchronized {syncCount} deformers with TileRules");
        }
    }
}
