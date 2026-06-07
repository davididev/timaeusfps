using UnityEngine;

[System.Serializable]
public class GameObjectPoolEntry 
{
    public string PoolName = "Name";
    public GameObject Prefab;
    public int InitialCount = 20;
}
