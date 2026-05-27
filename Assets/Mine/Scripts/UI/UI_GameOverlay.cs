using UnityEngine;
using UnityEngine.UI;

public class UI_GameOverlay : MonoBehaviour
{
    [SerializeField] TMPro.TextMeshProUGUI starsText;
    [SerializeField] Image HealthBarInstant, HealthBarGradual;
    const float GRADUAL_HP_PER_SECOND = 0.05f;
    float _current_gradual_amount = 1.0f;
    private int last_star_count = 0;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if(last_star_count != PlayerHealth.Stars)
        {
            last_star_count = PlayerHealth.Stars;
            starsText.text = last_star_count.ToString("D3");
            starsText.GetComponent<Animator>().SetTrigger("Burst");

        }
        if(!Mathf.Approximately(_current_gradual_amount, PlayerHealth.HealthPerc))
        {
            _current_gradual_amount = Mathf.MoveTowards(_current_gradual_amount, PlayerHealth.HealthPerc, GRADUAL_HP_PER_SECOND * Time.deltaTime);
            HealthBarInstant.fillAmount = PlayerHealth.HealthPerc;
            HealthBarGradual.fillAmount = _current_gradual_amount;
        }

    }
}
