using UnityEngine;

/// <summary>
/// Simple ball handler for local (non-networked) ball.
/// Handles positioning mode with thumbsticks and racket collision.
/// </summary>
public class LocalBallHandler : MonoBehaviour
{
    [Header("Positioning Settings")]
    [SerializeField] private float positionMoveSpeed = 1.5f;
    [SerializeField] private float positionHeightSpeed = 0.8f;
    
    [Header("Physics Settings")]
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private float bounciness = 0.92f; // Bounciness for table/floor (higher = bouncier)
    [SerializeField] private float drag = 0.5f; // Air resistance
    
    private Rigidbody rb;
    private bool isInPositioningMode = true;
    private bool isInPlay = false;
    private Vector3 velocity;
    private float lastHitTime = 0f; // Prevent double-hits
    private TableTennisManager tableTennisManager;
    
    public void Initialize(Rigidbody rigidbody)
    {
        rb = rigidbody;
        isInPositioningMode = true;
        tableTennisManager = FindObjectOfType<TableTennisManager>();
        lastHitTime = Time.time; // Prevent immediate hit detection after spawn
        
        // Setup rigidbody for proper physics
        rb.isKinematic = false;
        rb.useGravity = false; // We'll apply gravity manually for more control
        rb.drag = drag;
        rb.angularDrag = 0.5f;
        rb.constraints = RigidbodyConstraints.FreezeAll; // Freeze until hit
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        
        // Create and apply a bouncy physics material
        PhysicMaterial bouncyMat = new PhysicMaterial("BallMaterial");
        bouncyMat.bounciness = bounciness;
        bouncyMat.bounceCombine = PhysicMaterialCombine.Maximum;
        bouncyMat.frictionCombine = PhysicMaterialCombine.Minimum;
        bouncyMat.dynamicFriction = 0.2f;
        bouncyMat.staticFriction = 0.2f;
        
        // Apply to all colliders on the ball
        var colliders = GetComponentsInChildren<Collider>();
        foreach (var col in colliders)
        {
            col.material = bouncyMat;
        }
        
        Debug.Log("[LocalBallHandler] Initialized in positioning mode - use thumbsticks to move, hit with racket to start");
    }
    
    private void Update()
    {
        if (isInPositioningMode)
        {
            HandlePositioningMode();
            CheckProximityHit(); // Fallback hit detection
        }
    }
    
    /// <summary>
    /// Proximity-based hit detection as fallback for collision issues
    /// </summary>
    private void CheckProximityHit()
    {
        // Don't check if we just spawned or just hit
        if (Time.time - lastHitTime < 0.5f) return;
        
        // Find all rackets and check distance
        GameObject[] rackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var racket in rackets)
        {
            if (racket == null || !racket.activeInHierarchy) continue;
            
            float distance = Vector3.Distance(transform.position, racket.transform.position);
            if (distance < 0.12f) // 12cm proximity (tighter)
            {
                // Get racket velocity - needs to be moving fast enough (actual swing)
                Rigidbody racketRb = racket.GetComponent<Rigidbody>();
                if (racketRb != null && racketRb.velocity.magnitude > 1.0f) // Higher threshold
                {
                    Debug.Log($"[LocalBallHandler] PROXIMITY HIT! Distance: {distance}, Velocity: {racketRb.velocity.magnitude}");
                    HandleProximityHit(racket, racketRb.velocity);
                    return;
                }
            }
        }
    }
    
    private void HandleProximityHit(GameObject racket, Vector3 racketVelocity)
    {
        // Exit positioning mode
        isInPositioningMode = false;
        rb.constraints = RigidbodyConstraints.None; // Unfreeze
        rb.useGravity = false; // We handle gravity manually
        
        // Calculate hit direction: combination of racket velocity and direction from racket to ball
        Vector3 racketToBall = (transform.position - racket.transform.position).normalized;
        
        // Blend racket velocity direction with racket-to-ball direction
        // This makes the ball go where you swing it, but also away from the racket
        Vector3 swingDir = racketVelocity.normalized;
        Vector3 hitDir = (swingDir * 0.7f + racketToBall * 0.3f).normalized;
        
        // Use racket speed to determine ball speed
        float hitSpeed = Mathf.Clamp(racketVelocity.magnitude * 2.5f, 2f, 12f);
        Vector3 hitVelocity = hitDir * hitSpeed;
        
        // Add slight upward arc for better gameplay (like real table tennis)
        if (hitVelocity.y < 0.5f && hitVelocity.y > -3f)
        {
            hitVelocity.y += 1f;
        }
        
        velocity = hitVelocity;
        rb.velocity = hitVelocity;
        isInPlay = true;
        lastHitTime = Time.time;
        
        // Notify manager that ball was hit (transitions to Playing phase)
        if (tableTennisManager != null)
        {
            tableTennisManager.OnBallHit();
        }
        
        Debug.Log($"[LocalBallHandler] Ball hit! RacketVel={racketVelocity}, BallVel={hitVelocity}");
    }
    
    private void FixedUpdate()
    {
        if (!isInPositioningMode && isInPlay)
        {
            // Apply gravity manually (gives us more control than Unity's gravity)
            rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
            
            // Track velocity for hit detection
            velocity = rb.velocity;
            
            // Reset if ball falls too low or goes too far
            if (transform.position.y < -2f || transform.position.magnitude > 20f)
            {
                ResetBall();
            }
        }
    }
    
    private void HandlePositioningMode()
    {
        // Get thumbstick input
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        // Dead zone
        if (leftStick.magnitude < 0.1f) leftStick = Vector2.zero;
        if (rightStick.magnitude < 0.1f) rightStick = Vector2.zero;
        
        if (leftStick.magnitude > 0.1f || Mathf.Abs(rightStick.y) > 0.1f)
        {
            // Get camera for movement direction
            Camera cam = Camera.main;
            if (cam == null) return;
            
            // Calculate movement in camera-relative space (horizontal only)
            Vector3 camForward = cam.transform.forward;
            camForward.y = 0;
            camForward.Normalize();
            Vector3 camRight = cam.transform.right;
            camRight.y = 0;
            camRight.Normalize();
            
            // Left stick: horizontal movement (X/Z)
            Vector3 moveDir = (camRight * leftStick.x + camForward * leftStick.y) * positionMoveSpeed * Time.deltaTime;
            
            // Right stick Y: vertical movement
            float verticalMove = rightStick.y * positionHeightSpeed * Time.deltaTime;
            
            // Apply movement
            Vector3 newPos = transform.position + moveDir;
            newPos.y += verticalMove;
            
            // Clamp height to reasonable range
            newPos.y = Mathf.Clamp(newPos.y, 0.5f, 2.5f);
            
            transform.position = newPos;
        }
    }
    
    private void OnCollisionEnter(Collision collision)
    {
        Debug.Log($"[BALL_COLLISION] Ball collided with: {collision.gameObject.name}, Tag: {collision.gameObject.tag}, Layer: {collision.gameObject.layer}");
        // Check if hit floor - respawn ball
        if (collision.gameObject.CompareTag("Floor") || 
            collision.gameObject.name.ToLower().Contains("floor") ||
            collision.gameObject.name.ToLower().Contains("ground") ||
            collision.gameObject.layer == LayerMask.NameToLayer("Floor"))
        {
            Debug.Log("[LocalBallHandler] Ball hit floor - respawning!");
            ResetBall();
            return;
        }
        
        // Check if hit by racket (with cooldown to prevent double hits)
        if (Time.time - lastHitTime < 0.3f) return;
        
        if (collision.gameObject.CompareTag("Racket") || 
            collision.gameObject.name.ToLower().Contains("racket"))
        {
            HandleRacketHit(collision);
        }
    }
    
    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[BALL_TRIGGER] Ball triggered by: {other.gameObject.name}, Tag: {other.gameObject.tag}, Layer: {other.gameObject.layer}");
        // Check if hit floor trigger
        if (other.CompareTag("Floor") || 
            other.gameObject.name.ToLower().Contains("floor") ||
            other.gameObject.name.ToLower().Contains("ground"))
        {
            Debug.Log("[LocalBallHandler] Ball entered floor trigger - respawning!");
            ResetBall();
            return;
        }
        
        // Also check triggers for racket detection (with cooldown)
        if (Time.time - lastHitTime < 0.3f) return;
        
        if (other.CompareTag("Racket") || 
            other.gameObject.name.ToLower().Contains("racket"))
        {
            HandleRacketHitTrigger(other);
        }
    }
    
    private void HandleRacketHit(Collision collision)
    {
        Debug.Log($"[LocalBallHandler] HIT BY RACKET: {collision.gameObject.name}");
        
        // Exit positioning mode
        if (isInPositioningMode)
        {
            isInPositioningMode = false;
            rb.constraints = RigidbodyConstraints.None; // Unfreeze
            rb.useGravity = false; // We handle gravity manually
            Debug.Log("[LocalBallHandler] Exited positioning mode - game started!");
        }
        
        // Get racket velocity for direction
        Rigidbody racketRb = collision.gameObject.GetComponent<Rigidbody>();
        Vector3 racketVelocity = racketRb != null ? racketRb.velocity : collision.relativeVelocity;
        
        // Calculate hit direction
        Vector3 racketToBall = (transform.position - collision.gameObject.transform.position).normalized;
        Vector3 swingDir = racketVelocity.magnitude > 0.1f ? racketVelocity.normalized : racketToBall;
        
        // Blend swing direction with away-from-racket direction
        Vector3 hitDir = (swingDir * 0.7f + racketToBall * 0.3f).normalized;
        
        // Calculate speed based on impact
        float hitSpeed = Mathf.Clamp(racketVelocity.magnitude * 2.5f, 2f, 12f);
        Vector3 hitVelocity = hitDir * hitSpeed;
        
        // Add slight upward arc for better gameplay
        if (hitVelocity.y < 0.5f && hitVelocity.y > -3f)
        {
            hitVelocity.y += 1f;
        }
        
        velocity = hitVelocity;
        rb.velocity = hitVelocity;
        isInPlay = true;
        lastHitTime = Time.time;
        
        // Notify manager that ball was hit
        if (tableTennisManager != null)
        {
            tableTennisManager.OnBallHit();
        }
        
        Debug.Log($"[LocalBallHandler] Ball velocity: {hitVelocity}");
    }

    private void HandleRacketHitTrigger(Collider other)
    {
        Debug.Log($"[LocalBallHandler] TRIGGER HIT BY RACKET: {other.gameObject.name}");
        
        // Exit positioning mode
        if (isInPositioningMode)
        {
            isInPositioningMode = false;
            rb.constraints = RigidbodyConstraints.None; // Unfreeze
            rb.useGravity = false;
            Debug.Log("[LocalBallHandler] Exited positioning mode - game started!");
        }
        
        // Get racket velocity
        Vector3 racketVel = Vector3.zero;
        ControllerRacket racketController = other.GetComponent<ControllerRacket>();
        if (racketController == null)
        {
            racketController = other.GetComponentInParent<ControllerRacket>();
        }
        
        if (racketController != null)
        {
            racketVel = racketController.GetRacketVelocity(other.gameObject);
        }
        else
        {
            // Try to get velocity directly from rigidbody
            var otherRb = other.GetComponent<Rigidbody>();
            if (otherRb != null)
            {
                racketVel = otherRb.velocity;
            }
        }
        
        // Calculate hit direction
        Vector3 racketToBall = (transform.position - other.transform.position).normalized;
        Vector3 swingDir = racketVel.magnitude > 0.1f ? racketVel.normalized : racketToBall;
        
        // Blend swing direction with away-from-racket direction
        Vector3 hitDir = (swingDir * 0.7f + racketToBall * 0.3f).normalized;
        
        // Calculate speed
        float hitSpeed = Mathf.Clamp(racketVel.magnitude * 2.5f, 2f, 12f);
        Vector3 hitVelocity = hitDir * hitSpeed;
        
        // Add slight upward arc for better gameplay
        if (hitVelocity.y < 0.5f && hitVelocity.y > -3f)
        {
            hitVelocity.y += 1f;
        }
        
        velocity = hitVelocity;
        rb.velocity = hitVelocity;
        isInPlay = true;
        lastHitTime = Time.time;
        
        // Notify manager that ball was hit
        if (tableTennisManager != null)
        {
            tableTennisManager.OnBallHit();
        }
        
        Debug.Log($"[LocalBallHandler] Ball velocity: {hitVelocity}");
    }
    
    private void ResetBall()
    {
        Debug.Log("[LocalBallHandler] Ball reset - back to positioning mode");
        
        // Reset to center, enter positioning mode
        Camera cam = Camera.main;
        if (cam != null)
        {
            transform.position = cam.transform.position + cam.transform.forward * 0.5f;
            transform.position = new Vector3(transform.position.x, cam.transform.position.y, transform.position.z);
        }
        else
        {
            transform.position = new Vector3(0, 1.2f, 0);
        }
        
        rb.velocity = Vector3.zero;
        rb.constraints = RigidbodyConstraints.FreezeAll; // Freeze for positioning
        velocity = Vector3.zero;
        isInPositioningMode = true;
        isInPlay = false;
    }
    
    /// <summary>
    /// Check if ball is in positioning mode (for UI)
    /// </summary>
    public bool InPositioningMode => isInPositioningMode;
}
