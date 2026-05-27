using UnityEngine;

public class RotatePerSecond : MonoBehaviour
{
    public Vector3 RotateAmount = new Vector3(0.0f, 180f, 0.0f);
    public float Multiplier = 1.0f;  //This one mostly exists for the machine gun animator
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        transform.Rotate(RotateAmount * Multiplier * Time.deltaTime, Space.Self);
    }
}
