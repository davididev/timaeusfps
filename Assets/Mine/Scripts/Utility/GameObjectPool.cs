using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Assemblies;

/// <summary>
/// Utility MonoBehavior attached to Boilerplate to deal with objects that are used for short time use
/// </summary>
public class GameObjectPool : MonoBehaviour
{
    public static GameObjectPool Current;
    [SerializeField] private GameObjectPoolEntry[] startingList;

    protected static Dictionary<string, GameObject[]> currentList = new Dictionary<string, GameObject[]>();

    private void Awake()  //Make sure we reset the static stuff BEFORE the first update
    {
        Current = this;
        currentList.Clear();
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
        for(int i = 0; i < startingList.Length; i++)
        {
            InitPoolItem(startingList[i].PoolName, startingList[i].Prefab, startingList[i].InitialCount);
        }
    }

    /// <summary>
    /// Utiltiy function to create an object in the pool, as needed
    /// </summary>
    /// <param name="s">The identifier in the pool.  Cannot be the same name as another entry</param>
    /// <param name="prefab">The GameObject we are making copies of</param>
    /// <param name="initialCount">How many in the pool.  This cannot be changed</param>
    public static void InitPoolItem(string s, GameObject prefab, int initialCount)
    {
        if(currentList.ContainsKey(s) == true)
        {
            Debug.Log("Entry already exists- ignoring InitPoolItem");
            return;
        }

        //Key does not exist, we are now going to create a pool of items and make them inactive.
        GameObject[] tempPool = new GameObject[initialCount];
        for(int i = 0; i < initialCount; i++)
        {
            GameObject inst = GameObject.Instantiate(prefab);
            tempPool[i] = inst;
        }
        currentList.Add(s, tempPool);
    }

    /// <summary>
    /// Obtains an instance from the pool to be used until it is inactive again
    /// </summary>
    /// <param name="key">Entry from InitPoolItem.</param>
    /// <param name="pos">Global Position to spawn at</param>
    /// <param name="rot">Euler angles to set to</param>
    /// <returns>An instance from the pool.  Also sends message "OnGetInstance"</returns>
    public static GameObject GetInstance(string key, Vector3 pos, Vector3 rot)
    {
        GameObject[] tempPool;
        if(currentList.TryGetValue(key, out tempPool))
        {
            for(int i = 0; i < tempPool.Length; i++)
            {
                if (tempPool[i].activeInHierarchy == false)
                {
                    tempPool[i].transform.position = pos;
                    tempPool[i].transform.eulerAngles = rot;
                    tempPool[i].SetActive(true);
                    tempPool[i].SendMessage("OnGetInstance", SendMessageOptions.DontRequireReceiver);
                    return tempPool[i];
                }

            }
        }
        else
        {
            Debug.LogError("Key not found in pool.");
            return null;
        }

        Debug.Log("All the entries in the pool were taken.");
        return null;
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
