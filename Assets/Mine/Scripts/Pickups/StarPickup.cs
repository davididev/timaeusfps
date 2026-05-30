using UnityEngine;

public class StarPickup : MonoBehaviour
{
    [SerializeField] int Amount = 1;
    [SerializeField] float MaxScale = 12f;
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
            UI_GameOverlay.DamageFlash = new Color(1f, 1f, 0f, 0.1f);
            //TODO: Add sound FX and animation
            other.SendMessage("AddStars", Amount);
            gameObject.SetActive(false);
            transform.localScale = MIN_SCALE * Vector3.one;
        }

    }
}
