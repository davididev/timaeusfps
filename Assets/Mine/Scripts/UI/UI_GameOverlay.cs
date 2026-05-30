using UnityEngine;
using UnityEngine.UI;

public class UI_GameOverlay : MonoBehaviour
{
    [SerializeField] TMPro.TextMeshProUGUI starsText;
    [SerializeField] Image HealthBarInstant, HealthBarGradual, TransitionFadeImage;
    const float GRADUAL_HP_PER_SECOND = 0.05f;
    float _current_gradual_amount = 1.0f;
    private int last_star_count = 0;
    private Color target_fade_color;
    private float fade_per_second = 0.25f;
    public static Color DamageFlash = Color.clear;  //Set by player health and star
    public static bool TransitionFade = false;  //Set by teleporter
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        target_fade_color = Color.clear;
        fade_per_second = 0.25f;
    }

    void ProcessFadeAndFlash()
    {
        if (DamageFlash.Equals(Color.clear) == false)
        {
            target_fade_color = DamageFlash;
            fade_per_second = 20f;
            DamageFlash = Color.clear;
        }
        if(TransitionFade == true)
        {
            target_fade_color = Color.black;
            fade_per_second = 0.25f;
            TransitionFade = false;
        }

        Color c = TransitionFadeImage.color;
        if(c.Equals(target_fade_color))
        {
            if (target_fade_color.Equals(Color.clear) == false)  //Ending transition
                target_fade_color = Color.clear;
            
        }
        else
        {
            c.r = Mathf.MoveTowards(c.r, target_fade_color.r, fade_per_second * Time.deltaTime);
            c.g = Mathf.MoveTowards(c.g, target_fade_color.g, fade_per_second * Time.deltaTime);
            c.b = Mathf.MoveTowards(c.b, target_fade_color.b, fade_per_second * Time.deltaTime);
            c.a = Mathf.MoveTowards(c.a, target_fade_color.a, fade_per_second * Time.deltaTime);
            TransitionFadeImage.color = c;
        }
    }

    // Update is called once per frame
    void Update()
    {
        ProcessFadeAndFlash();
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
