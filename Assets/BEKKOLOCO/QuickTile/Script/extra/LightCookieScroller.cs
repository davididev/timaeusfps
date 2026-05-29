using UnityEngine;
using UnityEngine.Rendering.Universal;


namespace Bekkoloco
{


    [RequireComponent(typeof(Light))]
    [RequireComponent(typeof(UniversalAdditionalLightData))]
    public class LightCookieScroller : MonoBehaviour
    {
        [Tooltip("Units per second the cookie moves in +X (right) and +Y (up).")]
        public Vector2 scrollSpeed = new Vector2(1f, 0f);

        [Tooltip("Keep the offset wrapped inside [-size/2 , size/2] so it never overflows.")]
        public bool wrapOffset = true;

        // cached references
        private Light dirLight;
        private UniversalAdditionalLightData urpLight;

        void Awake()
        {
            dirLight = GetComponent<Light>();
            if (dirLight.type != LightType.Directional)
            {
                Debug.LogWarning($"{name}: LightCookieScroller only works on Directional lights.");
                enabled = false;
                return;
            }

            urpLight = GetComponent<UniversalAdditionalLightData>();  
        }

        void Update()
        {
            Vector2 newOffset = urpLight.lightCookieOffset + scrollSpeed * Time.deltaTime;

            if (wrapOffset)
            {
                float size = urpLight.lightCookieSize.x;
                if (size <= 0f) size = 10f;             

                float half = size * 0.5f;
                newOffset.x = Mathf.Repeat(newOffset.x + half, size) - half;
                newOffset.y = Mathf.Repeat(newOffset.y + half, size) - half;
            }

            urpLight.lightCookieOffset = newOffset;
        }
    }
}
