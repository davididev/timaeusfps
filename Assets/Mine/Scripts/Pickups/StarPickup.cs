using UnityEditorInternal;
using UnityEngine;

public class StarPickup : MonoBehaviour
{
    [SerializeField] int Amount = 1;
    [SerializeField] float MaxScale = 12f;
    [SerializeField] private AudioClip soundFX;

    static float SoundPitch = 1.0f;
    static float LastCollectedTime = 0.0f;
    const float TIME_PITCH_DIF = 0.2f;  //Amount of time that should pass before pitch resest

    const float MIN_SCALE = 0.1f;
    private float cur_scale = MIN_SCALE;
    const float SCALE_PER_SECOND = 12f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void OnEnable()
    {
        cur_scale = MIN_SCALE;
        transform.localScale = cur_scale * Vector3.one;
    }

    // Update is called once per frame
    void Update()
    {
        if (!Mathf.Approximately(cur_scale, MaxScale))
        {
            cur_scale = Mathf.MoveTowards(cur_scale, MaxScale, SCALE_PER_SECOND * Time.deltaTime);
            transform.localScale = cur_scale * Vector3.one;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.tag == "Player")
        {
            float dif = Mathf.Abs(Time.time - LastCollectedTime);
            LastCollectedTime = Time.time;
            if ((dif > TIME_PITCH_DIF))
                SoundPitch = 1.0f;
            else
                SoundPitch += 0.1f;

            SoundFXPlayer.PlaySound(soundFX, transform.position, 1.0f, 1.0f, 1.0f, SoundPitch);
            UI_GameOverlay.DamageFlash = new Color(1f, 1f, 0f, 0.1f);
            //TODO: Add sound FX and animation
            other.SendMessage("AddStars", Amount);
            GameObject inst = GameObjectPool.GetInstance("StarSparkles", transform.position, Vector3.zero);
            gameObject.SetActive(false);
            transform.localScale = MIN_SCALE * Vector3.one;
        }

    }
}
