using UnityEngine;

public class PlayParticlesOnce : MonoBehaviour
{
    [SerializeField] private ParticleSystem[] systemsToPlay;
    [SerializeField] private float lifeSpan = 5.0f;  //How long to last before we return it to the pool
    float remainingTime = 5.0f;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    /// <summary>
    /// Message called from GameObjectPool when we get an instance
    /// </summary>
    void OnGetInstance()
    {
        remainingTime = lifeSpan;
        for(int i = 0; i < systemsToPlay.Length; i++)
        {
            systemsToPlay[i].Play();
        }
    }

    // Update is called once per frame
    void Update()
    {
        remainingTime -= Time.deltaTime;
        if(remainingTime <= 0.0f)
        {
            gameObject.SetActive(false);  //Return it to the pool
        }
    }
}
