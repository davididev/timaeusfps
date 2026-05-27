using UnityEngine;

public class HideForTimer : MonoBehaviour
{
    [SerializeField] private float Timer = 60f * 3f;
    [SerializeField] private GameObject object_to_activate;
    private float time_passed = 0f;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if(object_to_activate.activeInHierarchy == false)
        {
            time_passed += Time.deltaTime;
            if(time_passed > Timer)
            {
                time_passed = 0f;
                object_to_activate.SetActive(true);
            }
        }
    }
}
