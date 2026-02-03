#if FUSION2

using Fusion;
using UnityEngine;
using System.Collections;

/// <summary>
/// Simple networked cube that spawns and stays in place.
/// Parented to the local spatial anchor to stay aligned across devices.
/// Can also function as a ping pong ball with physics when Ball Mode is enabled.
/// </summary>
public class NetworkedCube : NetworkBehaviour
{
    [Header("Visual Settings")]
    [SerializeField] private Color cubeColor = Color.cyan;
    
    [Header("Ball Mode Settings")]
    [SerializeField] private bool isBallMode = false;
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private float bounciness = 0.85f;
    [SerializeField] private float airResistance = 0.02f;
    [SerializeField] private float tableHeight = 0.76f; // Standard table tennis height
    [SerializeField] private float tableBoundsX = 0.76f; // Half table width
    [SerializeField] private float tableBoundsZ = 1.37f; // Half table length
    
    [Header("Ball Reset Settings")]
    [SerializeField] private float resetBelowY = -1f;
    [SerializeField] private float resetAfterSeconds = 10f;
    [SerializeField] private Vector3 servePosition = new Vector3(0, 1.2f, 0);
    [SerializeField] private float serveForce = 3f;
    
    [Header("Ball Sync Settings")]
    [SerializeField] private float syncRate = 30f; // Hz
    
    // SIMPLIFIED: Use world position sync (after alignment, world positions should match)
    [Networked] private Vector3 SyncedWorldPosition { get; set; }
    [Networked] private Quaternion SyncedWorldRotation { get; set; }
    [Networked] private Vector3 SyncedVelocity { get; set; }
    [Networked] private NetworkBool IsBallInPlay { get; set; }

    private Renderer cubeRenderer;
    private MaterialPropertyBlock propertyBlock;
    private bool isParentedToAnchor = false;
    private int retryCount = 0;
    private const int MAX_RETRIES = 50; // Try for ~5 seconds
    
    // Ball mode specific
    private Rigidbody rb;
    private Transform sharedAnchor;
    private Vector3 localVelocity;
    private float lastSyncTime;
    private float lastHitTime;
    private bool ballInitialized = false;
    
    // Client interpolation
    private Vector3 targetPosition;
    private Vector3 previousPosition;
    private float interpolationTime;

    public override void Spawned()
    {
        base.Spawned();

        cubeRenderer = GetComponent<Renderer>();
        if (cubeRenderer == null)
        {
            cubeRenderer = GetComponentInChildren<Renderer>();
        }

        // Ball mode: setup physics
        if (isBallMode)
        {
            SetupBallPhysics();
        }
        else
        {
            // Cube mode: Remove any Rigidbody - we want static objects
            var existingRb = GetComponent<Rigidbody>();
            if (existingRb != null)
            {
                Destroy(existingRb);
            }
        }

        propertyBlock = new MaterialPropertyBlock();
        UpdateVisualState();

        // SIMPLIFIED APPROACH: Use world position sync
        // After alignment, both devices should have matching world coordinate systems
        if (Object.HasStateAuthority)
        {
            // HOST: Store current world position for clients
            SyncedWorldPosition = transform.position;
            SyncedWorldRotation = transform.rotation;
            
        }
        else
        {
            // CLIENT: Wait for sync and apply world position
            StartCoroutine(ClientSyncRoutine());
            
        }

    }
    
    /// <summary>
    /// Simple client sync - just apply the world position from host
    /// </summary>
    private IEnumerator ClientSyncRoutine()
    {
        
        int attempts = 0;
        const int MAX_ATTEMPTS = 50;
        
        // Log camera rig position for comparison with host
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        
        
        // Wait for networked position to sync
        while (SyncedWorldPosition == Vector3.zero && attempts < MAX_ATTEMPTS)
        {
            attempts++;
            yield return new WaitForSeconds(0.1f);
        }
        
        if (SyncedWorldPosition != Vector3.zero)
        {
            
            // Apply world position directly
            transform.position = SyncedWorldPosition;
            transform.rotation = SyncedWorldRotation;
            
        }
        else
        {
            Debug.LogError($"[CLIENT SYNC DEBUG] Failed to receive world position after {MAX_ATTEMPTS} attempts!");
        }
    }
    
    private void SetupBallPhysics()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        
        // Configure as ping pong ball
        rb.mass = 0.0027f; // 2.7 grams
        rb.drag = airResistance;
        rb.angularDrag = 0.5f;
        rb.useGravity = false; // We handle gravity manually
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        
        // Only host simulates physics
        if (Object.HasStateAuthority)
        {
            rb.isKinematic = false;
            lastHitTime = Time.time;
        }
        else
        {
            rb.isKinematic = true;
        }
        
        // Add sphere collider if not present
        var sphereCol = GetComponent<SphereCollider>();
        if (sphereCol == null)
        {
            sphereCol = gameObject.AddComponent<SphereCollider>();
            sphereCol.radius = 0.02f; // Standard ping pong ball radius
        }
    }

    /// <summary>
    /// Retry parenting to anchor until successful or max retries reached
    /// </summary>
    private IEnumerator TryParentToAnchorRoutine()
    {
        while (!isParentedToAnchor && retryCount < MAX_RETRIES)
        {
            if (TryParentToLocalAnchor())
            {
                yield break;
            }
            
            retryCount++;
            yield return new WaitForSeconds(0.1f); // Retry every 100ms
        }

        if (!isParentedToAnchor)
        {
            Debug.LogError($"[NetworkedCube TryParentToAnchorRoutine (anchor)] Failed to parent to anchor after {MAX_RETRIES} attempts!");
        }
    }

    private bool TryParentToLocalAnchor()
    {
        // If already parented to an anchor, we're done
        if (transform.parent != null && transform.parent.GetComponent<OVRSpatialAnchor>() != null)
        {
            isParentedToAnchor = true;
            return true;
        }

        // CRITICAL: Wait for ColocationManager alignment to complete
        // This ensures the camera rig has been adjusted before we position objects
        if (!ColocationManager.AlignmentCompletedStatic)
        {
            if (retryCount % 10 == 0) // Log every 1 second (10 x 100ms)
            {
            }
            return false;
        }


        // Method 1: Use ColocationManager.GetPrimaryAnchor() - PREFERRED
        // This ensures we use the SAME anchor that AlignmentManager used
        var colocationManager = FindObjectOfType<ColocationManager>();
        if (colocationManager != null)
        {
            var primaryAnchor = colocationManager.GetPrimaryAnchor();
            if (primaryAnchor != null && primaryAnchor.Localized)
            {
                ParentToAnchor(primaryAnchor);
                return true;
            }
        }

        // Method 2 (Fallback): Find via AnchorGUIManager_AutoAlignment
        var guiManager = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
        if (guiManager != null)
        {
            var anchor = guiManager.GetLocalizedAnchor();
            if (anchor != null && anchor.Localized)
            {
                ParentToAnchor(anchor);
                return true;
            }
        }

        // Method 3 (Last Resort): Find any localized OVRSpatialAnchor in scene
        var allAnchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in allAnchors)
        {
            if (anchor != null && anchor.Localized)
            {
                ParentToAnchor(anchor);
                return true;
            }
        }

        return false;
    }

    private void ParentToAnchor(OVRSpatialAnchor anchor)
    {
        
        // Store anchor reference for ball mode
        sharedAnchor = anchor.transform;
        
        Vector3 localScale = transform.localScale;
        
        // HOST: Set anchor-relative position NOW (after alignment completed)
        if (Object.HasStateAuthority)
        {
            // For host, we store the current position relative to the anchor
            // This happens AFTER alignment, so positions are correct
            
            if (!isBallMode)
            {
                // Cube mode: Store world position
                SyncedWorldPosition = transform.position;
                SyncedWorldRotation = transform.rotation;
                
            }
            else
            {
                // Ball mode: Keep current world position, just store relative
                SyncedWorldPosition = transform.position;
                SyncedWorldRotation = transform.rotation;
                ballInitialized = true;
                
            }
        }
        else
        {
            // CLIENT: Use the networked world position from host
            Vector3 targetWorldPos = SyncedWorldPosition;
            Quaternion targetWorldRot = SyncedWorldRotation;
            
            // If networked values aren't set yet (all zeros), wait
            if (targetWorldPos == Vector3.zero)
            {
                return;
            }
            
            
            // Apply world position directly (no anchor parenting needed)
            transform.position = targetWorldPos;
            transform.rotation = targetWorldRot;
            
            if (isBallMode)
            {
                ballInitialized = true;
                targetPosition = transform.position;
                previousPosition = transform.position;
            }
            
        }
        
        isParentedToAnchor = true;
    }

    private void UpdateVisualState()
    {
        if (cubeRenderer == null || propertyBlock == null) return;

        cubeRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor("_Color", cubeColor);
        propertyBlock.SetColor("_BaseColor", cubeColor);
        cubeRenderer.SetPropertyBlock(propertyBlock);
    }
    
    // ==================== BALL MODE METHODS ====================
    
    public override void FixedUpdateNetwork()
    {
        if (!isBallMode || !ballInitialized || sharedAnchor == null) return;
        
        if (Object.HasStateAuthority)
        {
            SimulateBallPhysics();
            SyncBallToNetwork();
            CheckBallReset();
        }
    }
    
    private void Update()
    {
        if (!isBallMode || !ballInitialized || sharedAnchor == null) return;
        
        // Client: interpolate position
        if (!Object.HasStateAuthority)
        {
            InterpolateBallPosition();
        }
    }
    
    private void SimulateBallPhysics()
    {
        if (rb == null) return;
        
        // Apply gravity
        localVelocity = rb.velocity;
        localVelocity.y -= gravity * Runner.DeltaTime;
        rb.velocity = localVelocity;
        
        // Table bounce check
        if (transform.position.y <= tableHeight + 0.02f && localVelocity.y < 0)
        {
            Vector3 relPos = sharedAnchor.InverseTransformPoint(transform.position);
            if (Mathf.Abs(relPos.x) < tableBoundsX && Mathf.Abs(relPos.z) < tableBoundsZ)
            {
                // Bounce!
                localVelocity.y = -localVelocity.y * bounciness;
                rb.velocity = localVelocity;
                transform.position = new Vector3(transform.position.x, tableHeight + 0.02f, transform.position.z);
            }
        }
    }
    
    private void SyncBallToNetwork()
    {
        if (Time.time - lastSyncTime < 1f / syncRate) return;
        
        lastSyncTime = Time.time;
        // Use world position sync
        SyncedWorldPosition = transform.position;
        SyncedWorldRotation = transform.rotation;
        SyncedVelocity = rb != null ? rb.velocity : Vector3.zero;
    }
    
    private void InterpolateBallPosition()
    {
        interpolationTime += Time.deltaTime * syncRate;
        
        if (interpolationTime <= 1f)
        {
            transform.position = Vector3.Lerp(previousPosition, targetPosition, interpolationTime);
        }
        else
        {
            // Predict based on velocity (using world velocity directly)
            transform.position = targetPosition + SyncedVelocity * (interpolationTime - 1f) / syncRate;
        }
    }
    
    public override void Render()
    {
        // Client: update target position when networked data changes
        if (isBallMode && !Object.HasStateAuthority && ballInitialized)
        {
            previousPosition = targetPosition;
            targetPosition = SyncedWorldPosition; // Use world position directly
            interpolationTime = 0f;
        }
    }
    
    private void CheckBallReset()
    {
        // Reset if ball falls below threshold
        if (transform.position.y < resetBelowY)
        {
            ResetBallToServe();
        }
        
        // Reset if inactive too long
        if (Time.time - lastHitTime > resetAfterSeconds && IsBallInPlay)
        {
            ResetBallToServe();
        }
    }
    
    private void ResetBallToServe()
    {
        // Use serve position relative to start position
        transform.position = servePosition;
        
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        
        localVelocity = Vector3.zero;
        IsBallInPlay = false;
        lastHitTime = Time.time;
        
        SyncedWorldPosition = transform.position;
        SyncedVelocity = Vector3.zero;
        
    }
    
    /// <summary>
    /// Called when racket hits the ball. Only processed on host.
    /// </summary>
    public void OnRacketHit(Vector3 hitVelocity)
    {
        if (!isBallMode || !Object.HasStateAuthority || rb == null) return;
        
        rb.velocity = hitVelocity;
        localVelocity = hitVelocity;
        IsBallInPlay = true;
        lastHitTime = Time.time;
        
    }
    
    /// <summary>
    /// Request a serve (can be called by any player)
    /// </summary>
    public void RequestServe(Vector3 direction)
    {
        if (!isBallMode) return;
        
        if (Object.HasStateAuthority)
        {
            Serve(direction);
        }
        else
        {
            RPC_RequestServe(direction);
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestServe(Vector3 direction)
    {
        Serve(direction);
    }
    
    private void Serve(Vector3 direction)
    {
        ResetBallToServe();
        StartCoroutine(ApplyServeVelocity(direction.normalized * serveForce));
    }
    
    private IEnumerator ApplyServeVelocity(Vector3 velocity)
    {
        yield return new WaitForSeconds(0.3f);
        
        if (rb != null)
        {
            rb.velocity = velocity;
            localVelocity = velocity;
        }
        
        IsBallInPlay = true;
        lastHitTime = Time.time;
    }
    
    private void OnCollisionEnter(Collision collision)
    {
        if (!isBallMode || !Object.HasStateAuthority) return;
        
        // Check if hit by racket
        if (collision.gameObject.CompareTag("Racket"))
        {
            Rigidbody racketRb = collision.gameObject.GetComponent<Rigidbody>();
            Vector3 hitVelocity;
            
            if (racketRb != null && racketRb.velocity.magnitude > 0.1f)
            {
                hitVelocity = racketRb.velocity * 1.5f;
            }
            else
            {
                hitVelocity = collision.relativeVelocity * 0.8f;
            }
            
            // Ensure some upward component to keep ball in play
            hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
            
            OnRacketHit(hitVelocity);
            
            // Haptic feedback to the player
            OVRInput.SetControllerVibration(0.5f, 0.5f, OVRInput.Controller.All);
            Invoke(nameof(StopHaptics), 0.05f);
        }
    }
    
    private void StopHaptics()
    {
        OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.All);
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (!isBallMode || !Object.HasStateAuthority) return;
        
        // Alternative racket detection via trigger
        if (other.CompareTag("Racket"))
        {
            Rigidbody racketRb = other.GetComponent<Rigidbody>();
            if (racketRb != null && racketRb.velocity.magnitude > 0.1f)
            {
                Vector3 hitVelocity = racketRb.velocity * 1.5f;
                hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
                OnRacketHit(hitVelocity);
            }
        }
    }

    private void OnDestroy()
    {
    }
}

#endif
