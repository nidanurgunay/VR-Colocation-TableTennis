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
    [SerializeField] private float bounciness = 0.85f;
    [SerializeField] private float tableHeight = 0.76f;
    
    private Rigidbody rb;
    private bool isInPositioningMode = true;
    private bool isInPlay = false;
    private Vector3 velocity;
    
    public void Initialize(Rigidbody rigidbody)
    {
        rb = rigidbody;
        isInPositioningMode = true;
        
        // Make ball non-kinematic but freeze position for positioning mode
        // This allows collision detection with kinematic rackets
        rb.isKinematic = false;
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeAll; // Freeze until hit
        
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
        // Find all rackets and check distance
        GameObject[] rackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var racket in rackets)
        {
            if (racket == null || !racket.activeInHierarchy) continue;
            
            float distance = Vector3.Distance(transform.position, racket.transform.position);
            if (distance < 0.15f) // 15cm proximity
            {
                // Get racket velocity
                Rigidbody racketRb = racket.GetComponent<Rigidbody>();
                if (racketRb != null && racketRb.velocity.magnitude > 0.5f)
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
        
        // Calculate hit velocity
        Vector3 hitVelocity = racketVelocity * 1.5f;
        hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
        
        if (hitVelocity.magnitude < 2f)
        {
            hitVelocity = hitVelocity.normalized * 2f;
        }
        
        velocity = hitVelocity;
        rb.velocity = hitVelocity;
        isInPlay = true;
        
        Debug.Log($"[LocalBallHandler] Ball hit via proximity! Velocity: {hitVelocity}");
    }
    
    private void FixedUpdate()
    {
        if (!isInPositioningMode && isInPlay)
        {
            // Apply gravity
            velocity.y -= gravity * Time.fixedDeltaTime;
            rb.velocity = velocity;
            
            // Simple floor/table bounce
            if (transform.position.y < tableHeight + 0.02f && velocity.y < 0)
            {
                velocity.y = -velocity.y * bounciness;
                rb.velocity = velocity;
                Vector3 pos = transform.position;
                pos.y = tableHeight + 0.02f;
                transform.position = pos;
            }
            
            // Reset if ball falls too low
            if (transform.position.y < -1f)
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
        // Check if hit by racket
        if (collision.gameObject.CompareTag("Racket") || 
            collision.gameObject.name.ToLower().Contains("racket"))
        {
            HandleRacketHit(collision);
        }
    }
    
    private void OnTriggerEnter(Collider other)
    {
        // Also check triggers for racket detection
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
        
        // Calculate hit velocity from collision
        Vector3 hitVelocity = collision.relativeVelocity * 0.8f;
        
        // Ensure some upward velocity
        hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
        
        // Ensure minimum forward velocity
        if (hitVelocity.magnitude < 2f)
        {
            hitVelocity = hitVelocity.normalized * 2f;
        }
        
        velocity = hitVelocity;
        rb.velocity = hitVelocity;
        isInPlay = true;
        
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
        
        // For trigger, use a default velocity in the direction away from racket
        Vector3 hitDir = (transform.position - other.transform.position).normalized;
        hitDir.y = Mathf.Max(hitDir.y, 0.3f);
        
        Vector3 hitVelocity = hitDir * 3f;
        
        velocity = hitVelocity;
        rb.velocity = hitVelocity;
        isInPlay = true;
        
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
