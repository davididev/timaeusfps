using UnityEngine;
using UnityEngine.AI;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.Tilemaps;
using System.Collections;

namespace Bekkoloco
{
    public class PathFollower : MonoBehaviour
    {
        [Header("Startup Settings")]
        public float startupDelay = 0f; // Délai avant de commencer le mouvement
        private bool startupDelayComplete = false;

        [Header("Movement Settings")]
        public float moveSpeed = 2f;
        [SerializeField] private int pathIndex = -1;
        public List<Vector3> path;
        private int currentWaypointIndex = 0;
        private bool isMoving = false;
        private Tilemap tilemap;
        private Vector3 originalLocalPosition;
        private Vector3 initialOffset;

        [Header("Trigger Names (Inspector)")]
        public string idleTrigger = "Idle";
        public string walkTrigger = "Walk";
        public string turnLeftTrigger = "TurnLeft";
        public string turnRightTrigger = "TurnRight";

        // NavMeshAgent support
        public enum MovementMode
        {
            Auto,      // Use NavMeshAgent if a NavMeshAgent component is found, otherwise Manual
            Manual,    // Force manual movement — ignore any NavMeshAgent component
            NavMesh    // Force NavMeshAgent movement — falls back to Manual if agent is missing or NavMesh is unreachable
        }

        [Header("NavMesh Settings")]
        [Tooltip("Auto: use NavMeshAgent if one is attached. Manual: always manual. NavMesh: require a NavMeshAgent.")]
        public MovementMode movementMode = MovementMode.Auto;

        private NavMeshAgent navMeshAgent;
        private bool useNavMeshAgent = false;
        private bool navMeshInitialized = false;

        // Pause system
        [Header("Pause Settings")]
        public bool pauseAtWaypoints = false;
        public bool pauseOnlyAtExtremities = false;
        public float pauseDuration = 1f;
        private bool isPaused = false;
        private float pauseTimer = 0f;

        // Animation system
        [Header("Animation Settings")]
        public bool useAnimations = true;
        public Animator animator;
        [Tooltip("Speed threshold below which the character is considered idle")]
        public float idleSpeedThreshold = 0.1f;
        [Tooltip("Turn angle threshold in degrees to trigger turn animation")]
        public float turnAngleThreshold = 30f;
        [Tooltip("Time to blend between animations")]
        public float animationBlendTime = 0.2f;

        // Animation state tracking
        private Vector3 lastPosition;
        private Vector3 lastForward;
        private float currentSpeed;
        private AnimationState currentAnimationState;
        private float lastDirectionChangeTime;

        // Animation parameter names (these should match your Animator Controller)
        private const string ANIM_SPEED = "Speed";
        private const string ANIM_IS_MOVING = "IsMoving";
        private const string ANIM_IS_TURNING = "IsTurning";
        private const string ANIM_TURN_DIRECTION = "TurnDirection"; // -1 for left, 1 for right

        public enum AnimationState
        {
            Idle,
            Walking,
            TurningLeft,
            TurningRight
        }

        private void Awake()
        {
            tilemap = FindFirstObjectByType<Tilemap>();
            if (tilemap == null)
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Tilemap not found in the scene. Using raw coordinates for path conversion.");
            }

            // Check for NavMeshAgent component but don't fully initialize yet
            navMeshAgent = GetComponent<NavMeshAgent>();
            switch (movementMode)
            {
                case MovementMode.Manual:
                    useNavMeshAgent = false;
                    if (navMeshAgent != null) navMeshAgent.enabled = false;
                    break;

                case MovementMode.NavMesh:
                    if (navMeshAgent != null)
                    {
                        useNavMeshAgent = true;
                        navMeshAgent.enabled = false;
                    }
                    else
                    {
                        Debug.LogWarning($"[PathFollower] {gameObject.name}: MovementMode is NavMesh but no NavMeshAgent component found — falling back to Manual.");
                        useNavMeshAgent = false;
                    }
                    break;

                case MovementMode.Auto:
                default:
                    if (navMeshAgent != null)
                    {
                        useNavMeshAgent = true;
                        navMeshAgent.enabled = false;
                    }
                    else
                    {
                        useNavMeshAgent = false;
                    }
                    break;
            }

            // Initialize animation system
            InitializeAnimationSystem();

            originalLocalPosition = transform.localPosition;
            lastPosition = transform.position;
            lastForward = transform.forward;
            currentAnimationState = AnimationState.Idle;
            ////Debug.Log($"[PathFollower] {gameObject.name}: Initialized with pathIndex {pathIndex}");
        }

        // NOUVEAU: Délai de démarrage
        private void Start()
        {
            StartCoroutine(StartupDelayCoroutine());
        }

        private System.Collections.IEnumerator StartupDelayCoroutine()
        {
            yield return new WaitForSeconds(startupDelay);
            startupDelayComplete = true;
        }

        // NEW: Properly initialize NavMeshAgent after path is set
        private void InitializeNavMeshAgent()
        {
            if (!useNavMeshAgent || navMeshAgent == null || navMeshInitialized)
                return;

            // Enable the agent
            navMeshAgent.enabled = true;

            // Wait a frame for the agent to be properly enabled
            StartCoroutine(DelayedNavMeshSetup());
        }

        // NEW: Coroutine to setup NavMesh after a small delay
        private System.Collections.IEnumerator DelayedNavMeshSetup()
        {
            yield return null; // Wait one frame

            // Configure NavMeshAgent
            navMeshAgent.speed = moveSpeed;
            navMeshAgent.autoBraking = false;
            navMeshAgent.updateRotation = false; // We handle rotation manually for animations

            // Ensure agent is on NavMesh
            if (!navMeshAgent.isOnNavMesh)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(transform.position, out hit, 10f, NavMesh.AllAreas))
                {
                    navMeshAgent.Warp(hit.position);
                    ////Debug.Log($"[PathFollower] {gameObject.name}: NavMeshAgent warped to valid position: {hit.position}");
                }
                else
                {
                    ////Debug.LogError($"[PathFollower] {gameObject.name}: Cannot find valid NavMesh position. Switching to manual movement.");
                    useNavMeshAgent = false;
                    navMeshAgent.enabled = false;
                    yield break; // Use yield break instead of return in coroutines
                }
            }

            navMeshInitialized = true;
            ////Debug.Log($"[PathFollower] {gameObject.name}: NavMeshAgent fully initialized.");
        }

        private void InitializeAnimationSystem()
        {
            if (!useAnimations) return;

            // Try to find Animator if not assigned
            if (animator == null)
            {
                animator = GetComponent<Animator>();
            }

            if (animator == null)
            {
                // Try to find in children
                animator = GetComponentInChildren<Animator>();
            }

            if (animator != null)
            {
                //Debug.Log($"[PathFollower] {gameObject.name}: Animator found and initialized.");
                // Initialize animation parameters
                SetAnimationState(AnimationState.Idle);
            }
            else if (useAnimations)
            {
                //Debug.LogWarning($"[PathFollower] {gameObject.name}: useAnimations is true but no Animator component found!");
                useAnimations = false;
            }
        }

        public void SetPathIndex(int index)
        {
            pathIndex = index;
            ////Debug.Log($"[PathFollower] {gameObject.name}: PathIndex set to {index}");
        }

        public int GetPathIndex()
        {
            return pathIndex;
        }

        // GARDE L'ANCIEN SYSTÈME - pas de démarrage automatique ici
        public void SetPath(List<Vector2> newPath)
        {
            if (newPath == null)
            {
                ////Debug.LogError($"[PathFollower] {gameObject.name}: Null path provided; skipping.");
                return;
            }

            if (newPath.Count == 0)
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Empty path provided; skipping.");
                return;
            }

            // Store original position
            if (originalLocalPosition == Vector3.zero)
            {
                originalLocalPosition = transform.localPosition;
            }

            ////Debug.Log($"[PathFollower] {gameObject.name}: Setting path with {newPath.Count} points");

            List<Vector3> worldPositions = new List<Vector3>();

            if (tilemap != null)
            {
                foreach (Vector2 p in newPath)
                {
                    Vector3Int cellPos = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), 0);
                    Vector3 worldPos = tilemap.GetCellCenterWorld(cellPos);

                    // Apply offsets
                    worldPos.x -= 1.0f;
                    worldPos.z -= 0.0f;

                    // For NavMeshAgent, use world position Y, otherwise keep transform Y
                    if (!useNavMeshAgent)
                    {
                        worldPos.y = transform.position.y;
                    }

                    worldPositions.Add(worldPos);
                    ////Debug.Log($"[PathFollower] Added path point {worldPos} from cell {cellPos}");
                }

                if (worldPositions.Count > 0)
                {
                    if (useNavMeshAgent)
                    {
                        // For NavMeshAgent, store world positions directly
                        path = new List<Vector3>(worldPositions);
                    }
                    else
                    {
                        // For manual movement, convert to local positions
                        path = new List<Vector3>();
                        foreach (Vector3 worldPos in worldPositions)
                        {
                            if (transform.parent != null)
                            {
                                path.Add(transform.parent.InverseTransformPoint(worldPos));
                            }
                            else
                            {
                                path.Add(worldPos);
                            }
                        }
                        initialOffset = transform.localPosition - path[0];
                    }

                    ////Debug.Log($"[PathFollower] {gameObject.name}: Path set successfully. Path length: {path.Count}");
                }
            }
            else
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Tilemap not found. Converting raw grid coordinates.");

                // Fallback to raw conversion if no tilemap
                if (useNavMeshAgent)
                {
                    path = newPath.Select(p => new Vector3(p.x - 1.0f, transform.position.y, p.y - 1.0f)).ToList();
                }
                else
                {
                    path = newPath.Select(p =>
                    {
                        Vector3 rawPos = new Vector3(p.x - 1.0f, transform.localPosition.y, p.y - 1.0f);
                        return rawPos;
                    }).ToList();
                }
            }

            // Reset waypoint index
            currentWaypointIndex = 0;

            // Initialize NavMeshAgent now that we have a path
            if (useNavMeshAgent)
            {
                InitializeNavMeshAgent();
            }

            // PAS de démarrage automatique - garde l'ancien comportement
        }

        // GARDE L'ANCIEN SYSTÈME - StartMoving reste identique
        public void StartMoving()
        {
            // Enhanced validation
            if (path == null)
            {
                ////Debug.LogError($"[PathFollower] {gameObject.name}: Path is null. Cannot start moving.");
                return;
            }

            if (path.Count == 0)
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Path is empty (no points). Cannot start moving.");
                return;
            }

            if (path.Count == 1)
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Path has only one point. Will not provide actual movement.");
            }

            // Wait for NavMesh to be properly initialized
            if (useNavMeshAgent && !navMeshInitialized)
            {
                StartCoroutine(WaitForNavMeshAndStart());
                return;
            }

            isMoving = true;

            if (useNavMeshAgent && navMeshAgent != null && navMeshInitialized)
            {
                StartNavMeshMovement();
            }

            ////Debug.Log($"[PathFollower] {gameObject.name}: Started moving on path with {path.Count} points, current waypoint: {currentWaypointIndex}");
        }

        // NEW: Wait for NavMesh initialization before starting movement
        private System.Collections.IEnumerator WaitForNavMeshAndStart()
        {
            float waitTime = 0f;
            const float maxWaitTime = 3f; // Maximum wait time

            while (!navMeshInitialized && waitTime < maxWaitTime)
            {
                yield return new WaitForSeconds(0.1f);
                waitTime += 0.1f;
            }

            if (navMeshInitialized)
            {
                isMoving = true;
                StartNavMeshMovement();
                ////Debug.Log($"[PathFollower] {gameObject.name}: NavMesh ready, starting movement.");
            }
            else
            {
                ////Debug.LogError($"[PathFollower] {gameObject.name}: NavMesh initialization timeout. Switching to manual movement.");
                useNavMeshAgent = false;
                if (navMeshAgent != null)
                    navMeshAgent.enabled = false;
                isMoving = true;
            }
        }

        // NEW: Separate method for starting NavMesh movement
        private void StartNavMeshMovement()
        {
            if (!useNavMeshAgent || navMeshAgent == null || !navMeshInitialized)
                return;

            // Double-check agent is on NavMesh
            if (!navMeshAgent.isOnNavMesh)
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Agent not on NavMesh at start. Attempting to fix.");
                if (!ValidateNavMeshPosition())
                {
                    ////Debug.LogError($"[PathFollower] {gameObject.name}: Cannot place agent on NavMesh. Switching to manual movement.");
                    useNavMeshAgent = false;
                    navMeshAgent.enabled = false;
                    return;
                }
            }

            // Set the first destination
            if (path.Count > 0)
            {
                SetNavMeshDestination();
            }
        }

        public void StopMoving()
        {
            isMoving = false;
            isPaused = false;
            pauseTimer = 0f;

            if (useNavMeshAgent && navMeshAgent != null && navMeshAgent.enabled)
            {
                navMeshAgent.ResetPath();
            }

            // Set animation to idle when stopping
            SetAnimationState(AnimationState.Idle);
        }

        // MODIFIÉ : Update avec vérification du délai
        private void Update()
        {
            // NOUVEAU: Attendre que le délai soit terminé
            if (!startupDelayComplete)
                return;

            // Update animation system
            UpdateAnimations();

            // Skip if not moving or path is invalid
            if (!isMoving || path == null || path.Count == 0)
                return;

            // Handle pause system
            if (isPaused)
            {
                pauseTimer -= Time.deltaTime;
                if (pauseTimer <= 0f)
                {
                    isPaused = false;
                    //Debug.Log($"[PathFollower] {gameObject.name}: Pause ended, resuming movement.");

                    // Move to next waypoint after pause
                    MoveToNextWaypoint();
                }
                return; // Stay paused until timer expires
            }

            // Safety check for current index
            if (currentWaypointIndex >= path.Count)
            {
                //Debug.LogWarning($"[PathFollower] {gameObject.name}: Waypoint index {currentWaypointIndex} exceeds path length {path.Count}. Resetting to 0.");
                currentWaypointIndex = 0;
            }

            if (useNavMeshAgent && navMeshAgent != null && navMeshInitialized)
            {
                // NavMeshAgent movement logic
                HandleNavMeshMovement();
            }
            else
            {
                // Manual movement logic (original code)
                HandleManualMovement();
            }
        }

        // RESTE DE L'ANCIEN CODE - inchangé
        private void UpdateAnimations()
        {
            if (!useAnimations || animator == null) return;

            // Calculate current speed
            Vector3 velocity = (transform.position - lastPosition) / Time.deltaTime;
            currentSpeed = velocity.magnitude;

            // Determine animation state based on movement
            AnimationState newState = DetermineAnimationState(velocity);

            // Update animation state if changed
            if (newState != currentAnimationState)
            {
                SetAnimationState(newState);
            }

            // Update animator parameters
            UpdateAnimatorParameters();

            // Store current values for next frame
            lastPosition = transform.position;
            lastForward = transform.forward;
        }

        private AnimationState DetermineAnimationState(Vector3 velocity)
        {
            // If paused or not moving, return idle
            if (isPaused || !isMoving || currentSpeed < idleSpeedThreshold)
            {
                return AnimationState.Idle;
            }

            // Check for turning
            if (currentSpeed > idleSpeedThreshold && path != null && path.Count > 0)
            {
                Vector3 targetDirection = GetDirectionToCurrentWaypoint();
                float angleDifference = Vector3.SignedAngle(transform.forward, targetDirection, Vector3.up);

                // Check if we're turning significantly
                if (Mathf.Abs(angleDifference) > turnAngleThreshold)
                {
                    // Determine turn direction
                    if (angleDifference > 0)
                        return AnimationState.TurningRight;
                    else
                        return AnimationState.TurningLeft;
                }
            }

            // Default to walking if moving
            return AnimationState.Walking;
        }

        private Vector3 GetDirectionToCurrentWaypoint()
        {
            if (path == null || path.Count == 0 || currentWaypointIndex >= path.Count)
                return transform.forward;

            Vector3 targetPosition;
            if (useNavMeshAgent)
            {
                targetPosition = path[currentWaypointIndex];
            }
            else
            {
                Vector3 localTarget = path[currentWaypointIndex] + initialOffset;
                targetPosition = transform.parent != null ?
                    transform.parent.TransformPoint(localTarget) : localTarget;
            }

            Vector3 direction = (targetPosition - transform.position).normalized;
            direction.y = 0; // Keep direction in XZ plane
            return direction.magnitude > 0.01f ? direction : transform.forward;
        }

        private void SetAnimationState(AnimationState newState)
        {
            if (!useAnimations || animator == null) return;
            currentAnimationState = newState;

            // reset all triggers (optional)
            animator.ResetTrigger(idleTrigger);
            animator.ResetTrigger(walkTrigger);
            animator.ResetTrigger(turnLeftTrigger);
            animator.ResetTrigger(turnRightTrigger);

            // fire the chosen trigger
            switch (newState)
            {
                case AnimationState.Idle:
                    animator.SetTrigger(idleTrigger);
                    break;
                case AnimationState.Walking:
                    animator.SetTrigger(walkTrigger);
                    break;
                case AnimationState.TurningLeft:
                    animator.SetTrigger(turnLeftTrigger);
                    break;
                case AnimationState.TurningRight:
                    animator.SetTrigger(turnRightTrigger);
                    break;
            }

            //   //Debug.Log($"[PathFollower] {gameObject.name}: Triggered {newState}");
        }

        private void UpdateAnimatorParameters()
        {
            if (!useAnimations || animator == null) return;

            // Update speed parameter (normalized to moveSpeed)
            float normalizedSpeed = moveSpeed > 0 ? currentSpeed / moveSpeed : 0f;
            animator.SetFloat(ANIM_SPEED, normalizedSpeed);
        }

        private void HandleNavMeshMovement()
        {
            // Check if agent is on NavMesh
            if (!navMeshAgent.isOnNavMesh)
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: NavMeshAgent is not on NavMesh! Trying to warp to nearest valid position.");

                // Try to find the nearest point on NavMesh
                NavMeshHit hit;
                if (NavMesh.SamplePosition(transform.position, out hit, 10f, NavMesh.AllAreas))
                {
                    navMeshAgent.Warp(hit.position);
                    ////Debug.Log($"[PathFollower] {gameObject.name}: Warped NavMeshAgent to valid position: {hit.position}");
                }
                else
                {
                    ////Debug.LogError($"[PathFollower] {gameObject.name}: Could not find valid NavMesh position within 10 units. Switching to manual movement.");
                    useNavMeshAgent = false;
                    navMeshAgent.enabled = false;
                    return;
                }
            }

            // Handle rotation for animations
            if (useAnimations && navMeshAgent.velocity.magnitude > 0.1f)
            {
                Vector3 lookDirection = navMeshAgent.velocity.normalized;
                lookDirection.y = 0;
                if (lookDirection.magnitude > 0.1f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(lookDirection);
                    transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 5f);
                }
            }

            // Check if we've reached the current destination
            if (!navMeshAgent.pathPending && navMeshAgent.remainingDistance < 0.5f)
            {
                // Reached waypoint - check if we should pause
                if (pauseAtWaypoints && pauseDuration > 0f && ShouldPauseAtCurrentWaypoint())
                {
                    StartPause();
                }
                else
                {
                    MoveToNextWaypoint();
                }
            }
        }

        private void HandleManualMovement()
        {
            // Get target position from path
            Vector3 targetLocalPosition = path[currentWaypointIndex] + initialOffset;

            // Preserve current Y position when moving
            float currentY = transform.localPosition.y;

            // Calculate direction for rotation
            Vector3 targetWorldPosition = transform.parent != null ?
                transform.parent.TransformPoint(targetLocalPosition) : targetLocalPosition;
            Vector3 direction = (targetWorldPosition - transform.position).normalized;
            direction.y = 0; // Keep in XZ plane

            // Handle rotation for animations
            if (useAnimations && direction.magnitude > 0.1f)
            {
                Quaternion targetRotation = Quaternion.LookRotation(direction);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 5f);
            }

            // Move toward the target
            Vector3 nextLocalPosition = Vector3.MoveTowards(
                transform.localPosition,
                targetLocalPosition,
                moveSpeed * Time.deltaTime
            );

            // Ensure we keep the original Y position
            nextLocalPosition.y = currentY;

            // Apply the movement
            transform.localPosition = nextLocalPosition;

            // Check if we've reached the current waypoint (in 2D XZ plane)
            Vector2 pos2D = new Vector2(transform.localPosition.x, transform.localPosition.z);
            Vector2 target2D = new Vector2(targetLocalPosition.x, targetLocalPosition.z);

            if (Vector2.Distance(pos2D, target2D) < 0.01f)
            {
                // Reached waypoint - check if we should pause
                if (pauseAtWaypoints && pauseDuration > 0f && ShouldPauseAtCurrentWaypoint())
                {
                    StartPause();
                }
                else
                {
                    MoveToNextWaypoint();
                }
            }
        }

        private bool ShouldPauseAtCurrentWaypoint()
        {
            if (!pauseOnlyAtExtremities)
            {
                // Pause at every waypoint (original behavior)
                return true;
            }

            // Only pause at extremities (first and last waypoint)
            bool isFirstWaypoint = (currentWaypointIndex == 0);
            bool isLastWaypoint = (currentWaypointIndex == path.Count - 1);

            return isFirstWaypoint || isLastWaypoint;
        }

        private void StartPause()
        {
            isPaused = true;
            pauseTimer = pauseDuration;

            //Debug.Log($"[PathFollower] {gameObject.name}: Pausing at waypoint {currentWaypointIndex} for {pauseDuration} seconds.");

            // Stop NavMeshAgent if using one
            if (useNavMeshAgent && navMeshAgent != null && navMeshAgent.enabled)
            {
                navMeshAgent.ResetPath();
            }

            // Set animation to idle when paused
            SetAnimationState(AnimationState.Idle);
        }

        private void MoveToNextWaypoint()
        {
            // Move to next waypoint
            currentWaypointIndex++;

            // If we reached the end of the path, loop back to the beginning
            if (currentWaypointIndex >= path.Count)
            {
                ////Debug.Log($"[PathFollower] {gameObject.name}: Completed path. Restarting from beginning.");
                currentWaypointIndex = 0;
            }

            // Set next destination if using NavMeshAgent
            if (useNavMeshAgent && navMeshAgent != null && navMeshInitialized)
            {
                SetNavMeshDestination();
            }
        }

        private void SetNavMeshDestination()
        {
            if (!useNavMeshAgent || navMeshAgent == null || !navMeshAgent.enabled || currentWaypointIndex >= path.Count)
                return;

            // Validate next destination
            Vector3 nextDestination = path[currentWaypointIndex];
            NavMeshHit nextHit;
            if (NavMesh.SamplePosition(nextDestination, out nextHit, 2f, NavMesh.AllAreas))
            {
                navMeshAgent.SetDestination(nextHit.position);
                ////Debug.Log($"[PathFollower] {gameObject.name}: NavMeshAgent moving to next waypoint {currentWaypointIndex}: {nextHit.position}");
            }
            else
            {
                ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Next waypoint not reachable, trying to find valid position.");
                if (NavMesh.SamplePosition(nextDestination, out nextHit, 10f, NavMesh.AllAreas))
                {
                    navMeshAgent.SetDestination(nextHit.position);
                }
            }
        }

        private void ResumeNavMeshMovement()
        {
            if (useNavMeshAgent && navMeshAgent != null && navMeshInitialized && path != null && path.Count > 0)
            {
                //Debug.Log($"[PathFollower] {gameObject.name}: Resuming NavMesh movement to waypoint {currentWaypointIndex}");
                SetNavMeshDestination();
            }
        }

        public bool HasValidPath()
        {
            return path != null && path.Count > 1;
        }

        public bool IsUsingNavMeshAgent()
        {
            return useNavMeshAgent && navMeshInitialized;
        }

        public bool IsPaused()
        {
            return isPaused;
        }

        public float GetRemainingPauseTime()
        {
            return isPaused ? pauseTimer : 0f;
        }

        public void SkipCurrentPause()
        {
            if (isPaused)
            {
                isPaused = false;
                pauseTimer = 0f;
                ////Debug.Log($"[PathFollower] {gameObject.name}: Pause skipped manually.");

                // Resume movement
                if (useNavMeshAgent && navMeshAgent != null && navMeshInitialized)
                {
                    ResumeNavMeshMovement();
                }
            }
        }

        // Animation-related public methods
        public AnimationState GetCurrentAnimationState()
        {
            return currentAnimationState;
        }

        public float GetCurrentSpeed()
        {
            return currentSpeed;
        }

        public void SetUseAnimations(bool useAnims)
        {
            useAnimations = useAnims;
            if (!useAnimations && animator != null)
            {
                // Reset animator to idle state
                SetAnimationState(AnimationState.Idle);
            }
        }

        // Method to manually validate and fix NavMesh positioning
        public bool ValidateNavMeshPosition()
        {
            if (!useNavMeshAgent || navMeshAgent == null)
                return true; // Not using NavMesh, so always valid

            if (!navMeshAgent.isOnNavMesh)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(transform.position, out hit, 10f, NavMesh.AllAreas))
                {
                    navMeshAgent.Warp(hit.position);
                    ////Debug.Log($"[PathFollower] {gameObject.name}: Fixed NavMesh position to: {hit.position}");
                    return true;
                }
                else
                {
                    ////Debug.LogError($"[PathFollower] {gameObject.name}: Cannot find valid NavMesh position!");
                    return false;
                }
            }
            return true;
        }

        // Method to check if all waypoints are reachable via NavMesh
        public void ValidatePathOnNavMesh()
        {
            if (!useNavMeshAgent || path == null || path.Count == 0)
                return;

            ////Debug.Log($"[PathFollower] {gameObject.name}: Validating {path.Count} waypoints on NavMesh...");

            for (int i = 0; i < path.Count; i++)
            {
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(path[i], out hit, 2f, NavMesh.AllAreas))
                {
                    ////Debug.LogWarning($"[PathFollower] {gameObject.name}: Waypoint {i} at {path[i]} is not on NavMesh!");

                    // Try to find a nearby valid position
                    if (NavMesh.SamplePosition(path[i], out hit, 10f, NavMesh.AllAreas))
                    {
                        Vector3 oldPos = path[i];
                        path[i] = hit.position;
                        ////Debug.Log($"[PathFollower] {gameObject.name}: Adjusted waypoint {i} from {oldPos} to {hit.position}");
                    }
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (path == null || path.Count < 2) return;

            const float xOffset = 1f;   // ajoute 1 sur X
            const float zOffset = 1f;   // ajoute 1 sur Z

            // Trace la ligne du chemin
            Gizmos.color = Color.yellow;
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 a, b;

                if (useNavMeshAgent)
                {
                    a = path[i];
                    b = path[i + 1];
                }
                else
                {
                    // on ajoute initialOffset avant de transformer en world space
                    Vector3 localA = path[i] + initialOffset;
                    Vector3 localB = path[i + 1] + initialOffset;
                    a = transform.parent != null
                        ? transform.parent.TransformPoint(localA)
                        : localA;
                    b = transform.parent != null
                        ? transform.parent.TransformPoint(localB)
                        : localB;
                }

                // puis on applique tes décalages X/Z si besoin
                a.x += xOffset; a.z += zOffset;
                b.x += xOffset; b.z += zOffset;

                Gizmos.DrawLine(a, b);
            }

            // Sphères aux waypoints
            Gizmos.color = Color.red;
            foreach (var p in path)
            {
                Vector3 wp = useNavMeshAgent
                    ? p
                    : (transform.parent != null
                        ? transform.parent.TransformPoint(p)
                        : p);

                wp.x += xOffset; wp.z += zOffset;
                Gizmos.DrawSphere(wp, 0.1f);
            }

            // Highlight waypoint courant
            if (currentWaypointIndex >= 0 && currentWaypointIndex < path.Count)
            {
                Vector3 cur = useNavMeshAgent
                    ? path[currentWaypointIndex]
                    : (transform.parent != null
                        ? transform.parent.TransformPoint(path[currentWaypointIndex])
                        : path[currentWaypointIndex]);

                cur.x += xOffset; cur.z += zOffset;

                Gizmos.color = isPaused ? Color.magenta : Color.green;
                Gizmos.DrawSphere(cur, 0.15f);
                if (isPaused)
                {
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawWireSphere(cur, 0.3f);
                }
            }

            // Chemin NavMeshAgent
            if (useNavMeshAgent && navMeshAgent != null && navMeshAgent.enabled && navMeshAgent.hasPath)
            {
                Gizmos.color = Color.blue;
                var corners = navMeshAgent.path.corners;
                for (int i = 0; i < corners.Length - 1; i++)
                {
                    Vector3 cA = corners[i];
                    Vector3 cB = corners[i + 1];

                    cA.x += xOffset; cA.z += zOffset;
                    cB.x += xOffset; cB.z += zOffset;

                    Gizmos.DrawLine(cA, cB);
                }
            }

            // Indicateur d'animation
            if (useAnimations && Application.isPlaying)
            {
                Vector3 headPos = transform.position + Vector3.up * 2f;
                headPos.x += xOffset; headPos.z += zOffset;

                switch (currentAnimationState)
                {
                    case AnimationState.Idle:
                        Gizmos.color = Color.white;
                        Gizmos.DrawWireCube(headPos, Vector3.one * 0.2f);
                        break;
                    case AnimationState.Walking:
                        Gizmos.color = Color.green;
                        Gizmos.DrawSphere(headPos, 0.1f);
                        break;
                    case AnimationState.TurningLeft:
                        Gizmos.color = Color.red;
                        Gizmos.DrawWireSphere(headPos, 0.15f);
                        Gizmos.DrawLine(headPos, headPos + Vector3.left * 0.3f);
                        break;
                    case AnimationState.TurningRight:
                        Gizmos.color = Color.blue;
                        Gizmos.DrawWireSphere(headPos, 0.15f);
                        Gizmos.DrawLine(headPos, headPos + Vector3.right * 0.3f);
                        break;
                }
            }
        }
    }
}
