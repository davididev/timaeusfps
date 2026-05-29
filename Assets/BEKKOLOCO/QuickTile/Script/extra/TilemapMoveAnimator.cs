using System.Collections;
using UnityEngine;

namespace Bekkoloco
{
    /// <summary>
    /// Version améliorée qui démarre toujours l'animation quand configurée
    /// </summary>
    [DisallowMultipleComponent]
    public class TilemapMoveAnimator : MonoBehaviour
    {
        private const float k_DefaultAnimDuration = 0.75f;

        [SerializeField] private Vector3 baseLocalPosition;
        [SerializeField] private Vector3 moveOffset;
        [SerializeField] private float animationDuration = k_DefaultAnimDuration;
        [SerializeField] private float pauseDuration = 0f;
        [SerializeField] private bool isConfigured = false;

        private Coroutine _animationRoutine;

        /// <summary>
        /// Configure the animator. Calling this will restart the loop if we are playing.
        /// </summary>
        public void Configure(Vector3 basePosition, Vector3 offset, float pauseSeconds, float duration = k_DefaultAnimDuration)
        {
            baseLocalPosition = basePosition;
            moveOffset = offset;
            pauseDuration = Mathf.Max(0f, pauseSeconds);
            animationDuration = Mathf.Max(0.001f, duration);
            isConfigured = true;

            if (!Application.isPlaying)
            {
                transform.localPosition = baseLocalPosition;
                return;
            }

            TryStartAnimation();
        }

        private void TryStartAnimation()
        {
            if (!Application.isPlaying || !isConfigured) return;

            if (moveOffset.sqrMagnitude < 1e-6f)
                return;

            if (isActiveAndEnabled && gameObject.activeInHierarchy)
            {
                RestartAnimation();
            }
            else
            {
                StartCoroutine(WaitForActivationAndStart());
            }
        }

        private IEnumerator WaitForActivationAndStart()
        {
            float timeout = 5f;
            float elapsed = 0f;

            while (elapsed < timeout && (!isActiveAndEnabled || !gameObject.activeInHierarchy))
            {
                yield return null;
                elapsed += Time.deltaTime;
            }

            if (isActiveAndEnabled && gameObject.activeInHierarchy && isConfigured)
            {
                RestartAnimation();
            }
            else
            {
                Debug.LogWarning($"[TilemapMoveAnimator] {gameObject.name}: Timeout ou échec d'activation après {elapsed:F2}s");
            }
        }

        /// <summary>
        /// Force le démarrage de l'animation (nouvelle méthode publique)
        /// </summary>
        public void ForceStartAnimation()
        {
            if (!Application.isPlaying || !isConfigured) return;

            if (moveOffset.sqrMagnitude > 1e-6f)
            {
                RestartAnimation();
            }
            else
            {
                Debug.LogWarning($"[TilemapMoveAnimator] {gameObject.name}: moveOffset est zero, impossible d'animer");
            }
        }

        /// <summary>
        /// Stops any animation and snaps the transform back to the stored base position.
        /// </summary>
        public void ResetToBase(Vector3 basePosition)
        {
            baseLocalPosition = basePosition;
            isConfigured = false;

            if (_animationRoutine != null)
            {
                StopCoroutine(_animationRoutine);
                _animationRoutine = null;
            }
            transform.localPosition = baseLocalPosition;
        }

        private void OnEnable()
        {
            if (Application.isPlaying && gameObject.activeInHierarchy && isConfigured)
            {
                TryStartAnimation();
            }
            else if (!Application.isPlaying)
            {
                transform.localPosition = baseLocalPosition;
            }
        }

        private void OnDisable()
        {
            if (_animationRoutine != null)
            {
                StopCoroutine(_animationRoutine);
                _animationRoutine = null;
            }

            if (transform != null)
            {
                transform.localPosition = baseLocalPosition;
            }
        }

        private void RestartAnimation()
        {
            if (_animationRoutine != null)
            {
                StopCoroutine(_animationRoutine);
            }

            _animationRoutine = StartCoroutine(AnimateLoop());
        }

        private IEnumerator AnimateLoop()
        {
            while (true)
            {
                // Aller vers la position offset
                yield return AnimateTowards(baseLocalPosition + moveOffset);

                if (pauseDuration > 0f)
                    yield return new WaitForSeconds(pauseDuration);

                // Revenir à la position de base
                yield return AnimateTowards(baseLocalPosition);

                if (pauseDuration > 0f)
                    yield return new WaitForSeconds(pauseDuration);
            }
        }

        private IEnumerator AnimateTowards(Vector3 target)
        {
            Vector3 start = transform.localPosition;

            if (animationDuration <= 0.001f)
            {
                transform.localPosition = target;
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < animationDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / animationDuration);
                float easedT = Mathf.SmoothStep(0f, 1f, t);
                transform.localPosition = Vector3.LerpUnclamped(start, target, easedT);
                yield return null;
            }

            transform.localPosition = target;
        }

        /// <summary>
        /// Méthode de debug publique — appeler manuellement pour inspecter l'état
        /// </summary>
        public void DebugState()
        {
            Debug.Log($"[TilemapMoveAnimator] État de {gameObject.name}:\n" +
                      $"  isConfigured={isConfigured}, active={isActiveAndEnabled}, hierarchy={gameObject.activeInHierarchy}\n" +
                      $"  base={baseLocalPosition}, offset={moveOffset}, duration={animationDuration}, pause={pauseDuration}\n" +
                      $"  running={_animationRoutine != null}, pos={transform.localPosition}");
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!Application.isPlaying)
            {
                transform.localPosition = baseLocalPosition;
            }
        }
#endif
    }
}
