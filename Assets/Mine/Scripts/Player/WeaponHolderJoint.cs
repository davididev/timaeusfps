using UnityEngine;

public class WeaponHolderJoint : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private WeaponRigidBody[] possible_weapons;
    [SerializeField] private GameObject[] bullet_prefabs;
    private int _current_weapon_id = 0;
    void Start()
    {
        SetWeaponID(0);
    }

    public WeaponRigidBody CurrentWeapon()
    {
        return possible_weapons[_current_weapon_id];
    }

    void SetWeaponID(int id)
    {
        _current_weapon_id = 0;
        

        for (int i = 0; i < possible_weapons.Length; i++)
        {
            possible_weapons[i].gameObject.SetActive(id == i);
            
        }
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
