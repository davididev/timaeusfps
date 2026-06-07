using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    private InputSystem_Actions action;
    [SerializeField] private Transform neckBone, camPoint;
    [SerializeField] WeaponHolderJoint weaponholder;
    private Animator anim;
    private Camera cam;
    const float BREAK_GROUNDED_TIMER = 0.1f;  //How much time should pass before we say it's not grounded
    float jump_delay_timer = 0.0f;
    float neck_offset_angle = 0.0f;
    private const float JUMP_FORCE = 5.0f;
    private const float ROTATE_X = 45.0f;
    private const float ROTATE_Y = 45.0f;
    private const float MIN_WALK_SPEED = 0.5f;
    private const float MAX_WALK_SPEED = 2.75f;
    private const float WALK_ACCEL = 7.0f;
    float current_gravity = 0.0f;

    const float DEFAULT_ROTATE_Z = 7.5f;
    protected Vector3 currentAccel = Vector3.zero;
    protected Vector2 MoveVec = Vector2.zero;
    float rotate_lr = 0.0f;


    private CharacterController cc;

    private void Awake()
    {
        action = new InputSystem_Actions();
        action.Player.Jump.performed += Jump_performed;
        
        action.Player.Enable();
    }

    

    

    private void Look_performed()
    {
        Vector2 axis = action.Player.Look.ReadValue<Vector2>();
        axis.y *= -1.0f;
        if (axis.y > 0.0f)
            neck_offset_angle = Mathf.MoveTowardsAngle(neck_offset_angle, 45.0f, axis.y * ROTATE_Y * Time.deltaTime);
        if (axis.y < 0.0f)
            neck_offset_angle = Mathf.MoveTowardsAngle(neck_offset_angle, -45.0f, Mathf.Abs(axis.y) * ROTATE_Y * Time.deltaTime);

        rotate_lr = axis.x;
        transform.Rotate(Vector3.up * rotate_lr * ROTATE_X * Time.deltaTime, Space.Self);

    }


    private void Jump_performed(InputAction.CallbackContext obj)
    {
        if(cc.isGrounded)
        {
            if (jump_delay_timer <= 0.0f)
            {
                jump_delay_timer = BREAK_GROUNDED_TIMER;
                current_gravity = JUMP_FORCE;
            }
        }
        
            
    }

    

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        GetComponent<CharacterController>().Move(Vector3.down * 5.0f);  //Move the player directly on to the floor
    }
    

    
    private void LateUpdate()
    {
        if (!Mathf.Approximately(neck_offset_angle, 0.0f))
        {
            Vector3 localAngles = neckBone.localEulerAngles;

            localAngles.z = DEFAULT_ROTATE_Z + neck_offset_angle;

            neckBone.localEulerAngles = localAngles;
        }
    }
    

    // Update is called once per frame
    void Update()
    {
        if (cc == null)  //In case the reference gets lost
            cc = GetComponent<CharacterController>();
        if (anim == null)
            anim = GetComponent<Animator>();
        if (cam == null)
            cam = Camera.main;

        Look_performed();
        ProcessMovement();
        UpdateCamera();
        ProcessAnimation();
    }

    void UpdateCamera()
    {
        cam.transform.position = camPoint.transform.position;
        Vector3 rotation = transform.eulerAngles;
        rotation.x = neck_offset_angle;
        cam.transform.eulerAngles = rotation;
        
    }

    void ProcessAnimation()
    {
        anim.SetBool("IsGrounded", (jump_delay_timer > 0.0f));
        anim.SetFloat("MoveAngle", Mathf.Atan2(currentAccel.x, currentAccel.z) * Mathf.Rad2Deg / 360.0f);
        anim.SetBool("Moving", currentAccel.magnitude > 0.2f);
    }

    void ProcessMovement()
    {
        MoveVec = action.Player.Move.ReadValue<Vector2>();

        if (cc != null)
        {
            current_gravity -= 9.8f * Time.deltaTime;
            if (cc.isGrounded == true && jump_delay_timer <= 0.0f)
            {
                current_gravity = -1.0f;
            }

            if (jump_delay_timer >= 0.0f)
                jump_delay_timer -= Time.deltaTime;

            
        }
        if (MoveVec != Vector2.zero)
        {

            currentAccel.x += Mathf.Abs(MoveVec.x) * WALK_ACCEL * Time.deltaTime;
            currentAccel.z += Mathf.Abs(MoveVec.y) * WALK_ACCEL * Time.deltaTime;
            currentAccel.x = Mathf.Clamp(currentAccel.x, MIN_WALK_SPEED, MAX_WALK_SPEED);
            currentAccel.z = Mathf.Clamp(currentAccel.z, MIN_WALK_SPEED, MAX_WALK_SPEED);

        }
        if (Mathf.Approximately(MoveVec.x, 0.0f))
            currentAccel.x = Mathf.MoveTowards(currentAccel.x, 0.0f, WALK_ACCEL * 6.0f * Time.deltaTime);
        if (Mathf.Approximately(MoveVec.y, 0.0f))
            currentAccel.z = Mathf.MoveTowards(currentAccel.z, 0.0f, WALK_ACCEL * 6.0f * Time.deltaTime);

        Vector3 combined_movement = Vector3.zero;

        if (MoveVec.y > 0.0f)
            combined_movement += transform.forward * currentAccel.z;
        else
            combined_movement -= transform.forward * currentAccel.z;
        if (MoveVec.x > 0.0f)
            combined_movement += transform.right * currentAccel.x;
        else
            combined_movement -= transform.right * currentAccel.x;

        combined_movement += Vector3.up * current_gravity;
        cc.Move(combined_movement * Time.deltaTime);


    }

    void OnAnimatorIK(int layerIndex)
    {
        if (anim == null)
            anim = GetComponent<Animator>();

        WeaponRigidBody cur = weaponholder.CurrentWeapon();
        if (cur == null)
        {
            Debug.Log(Time.time + ": There is no weapon atm.");
            return;
        }
        Debug.DrawLine(transform.position, cur.primaryGrabPoint.position, Color.red, 0.1f);

        anim.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
        anim.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
        anim.SetIKPosition(AvatarIKGoal.RightHand, cur.primaryGrabPoint.position);
        anim.SetIKRotation(AvatarIKGoal.RightHand, cur.primaryGrabPoint.rotation);
        if (cur.secondaryGrabPoint != null)
        {
            anim.SetIKPositionWeight(AvatarIKGoal.LeftHand, 1f);
            anim.SetIKRotationWeight(AvatarIKGoal.LeftHand, 1f);
            anim.SetIKPosition(AvatarIKGoal.LeftHand, cur.secondaryGrabPoint.position);
            //anim.SetIKRotation(AvatarIKGoal.LeftHand, cur.secondaryGrabPoint.rotation);
        }
        anim.feetPivotActive = 1f;
    }
}
