using UnityEngine;

public class PlayerHealth : MonoBehaviour
{
    [HideInInspector] public int Health = MAX_HEALTH;
    public static int Stars = 0;
    public static float HealthPerc = 1.0f;
    public const int MAX_HEALTH = 100;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start() 
    {
        
    }

    public void Damage(int amount)
    {
        //TODO: Add sound FX and blood
        Health -= amount;

    }

    public void AddStars(int ct)
    {
        Stars += ct;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
