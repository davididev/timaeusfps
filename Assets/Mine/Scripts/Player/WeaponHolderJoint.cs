using UnityEngine;

public class WeaponHolderJoint : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    [SerializeField] private WeaponRigidBody[] possible_weapons;
    [SerializeField] private GameObject[] bullet_prefabs;
    private InputSystem_Actions action;
    private Animator anim;
    private int _current_weapon_id = 0;

    private void Awake()  //Correct rotation for FPS
    {
        action = new InputSystem_Actions();
        action.Player.Attack.performed += Attack_performed;
        action.Player.Melee.performed += Melee_performed;
        action.Player.Enable();
        Vector3 targetpos = transform.parent.position + (transform.parent.forward * 100.0f);
        transform.GetChild(0).LookAt(targetpos);

    }

    private void Melee_performed(UnityEngine.InputSystem.InputAction.CallbackContext obj)
    {
        if (anim == null)
            anim = GetComponent<Animator>();
        anim.SetTrigger("Attack");
    }

    private void Attack_performed(UnityEngine.InputSystem.InputAction.CallbackContext obj)
    {
        Debug.Log("Pew");
    }

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
        if (anim == null)
            anim = GetComponent<Animator>();

        if (action.Player.Move.ReadValue<Vector2>() == Vector2.zero)
            anim.SetFloat("BobMulti", 4.0f);
        else
            anim.SetFloat("BobMulti", 1.0f);

    }
}
