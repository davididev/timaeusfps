using Unity.VisualScripting;
using UnityEngine;

public class PlayerAvatarIKWeapon : MonoBehaviour
{
    private float _current_left_hand_weight = 0.0f;
    private float _current_right_hand_weight = 1.0f;
    public Transform LeftHandPoint;
    public Transform RightHandPoint;


    [HideInInspector] public float LeftHandTargetWeight = 0.0f;
    [HideInInspector] public float RightHandTargetWeight = 0.0f;
    const float LEFT_HAND_TARGET_PER_SECOND = 5.0f;
    const float RIGHT_HAND_TARGET_PER_SECOND = 5.0f;
    [SerializeField] WeaponHolderJoint weaponHolder;
    Animator anim;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        UpdateWeapon();
    }

    public void UpdateWeapon()
    {
        WeaponRigidBody cur = weaponHolder.CurrentWeapon();

        LeftHandPoint = cur.secondaryGrabPoint;
        RightHandPoint = cur.primaryGrabPoint;
        
        if (cur.secondaryGrabPoint == null)
            LeftHandTargetWeight = 0.0f;
        else
            LeftHandTargetWeight = 1.0f;

        RightHandTargetWeight = 1.0f;
    }

    // Update is called once per frame
    void Update()
    {
        _current_left_hand_weight = Mathf.MoveTowards(_current_left_hand_weight, LeftHandTargetWeight, LEFT_HAND_TARGET_PER_SECOND * Time.deltaTime);
        _current_right_hand_weight = Mathf.MoveTowards(_current_right_hand_weight, RightHandTargetWeight, RIGHT_HAND_TARGET_PER_SECOND * Time.deltaTime);
    }

    void OnAnimatorIK(int layerIndex)
    {
        if (anim == null)
            anim = GetComponent<Animator>();
        
        
        anim.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
        anim.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
        anim.SetIKPosition(AvatarIKGoal.RightHand, RightHandPoint.position);
        anim.SetIKRotation(AvatarIKGoal.RightHand, RightHandPoint.rotation);
        if (LeftHandPoint != null)
        {
            anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, _current_left_hand_weight);
            anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, _current_left_hand_weight);
            anim.SetIKPosition(AvatarIKGoal.LeftHand, LeftHandPoint.position);
            //anim.SetIKRotation(AvatarIKGoal.LeftHand, cur.secondaryGrabPoint.rotation);
        }
        anim.feetPivotActive = 1f;
    }
}
