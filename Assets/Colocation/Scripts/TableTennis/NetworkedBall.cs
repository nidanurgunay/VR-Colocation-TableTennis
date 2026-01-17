using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Networked ping pong ball for colocation table tennis.
/// Host has physics authority, syncs anchor-relative position to clients.
/// </summary>
public class NetworkedBall : NetworkBehaviour
{
    [Header("Physics Settings")]
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private float bounciness = 0.95f; // Increased from 0.92 for more realistic ping pong bounce
    [SerializeField] private float airResistance = 0.02f;
    [SerializeField] private float tableHeight = 0.76f; // Standard table tennis height
    [SerializeField] private float racketHitMultiplier = 2.2f; // Multiplier for racket velocity transfer
    [SerializeField] private float fallbackHitMultiplier = 1.2f; // Multiplier when racket has no rigidbody
    
    [Header("Serve Settings")]
    [SerializeField] private float serveHeight = 0.4f; // Height above table for serve
    [SerializeField] private float serveDistanceFromCenter = 0.8f; // How far from table center (toward server)
    [SerializeField] private float serveForce = 3f;
    
    [Header("Table Reference")]
    [SerializeField] private Transform tableObject;
    [SerializeField] private string tableTag = "Table";
    
    [Header("Sync Settings")]
    [SerializeField] private float syncRate = 30f; // Hz
    
    [Header("Reset Settings")]
    [SerializeField] private float resetBelowY = -1f; // Reset if ball falls below this
    [SerializeField] private float resetAfterSeconds = 5f; // Reset if no activity
    
    [Header("Positioning Mode")]
    [SerializeField] private float positionMoveSpeed = 1.5f; // Speed of thumbstick movement
    [SerializeField] private float positionHeightSpeed = 0.8f; // Speed of vertical movement
    
    // Networked state - table relative (table is aligned to same world position on both devices)
    [Networked] private Vector3 TableRelativePosition { get; set; }
    [Networked] private Vector3 TableRelativeVelocity { get; set; }
    [Networked] private NetworkBool IsInPlay { get; set; }
    [Networked] private NetworkBool IsInPositioningMode { get; set; } // Ball can be moved with thumbsticks
    
    // Legacy - kept for compatibility but unused
    [Networked] private Vector3 AnchorRelativePosition { get; set; }
    [Networked] private Vector3 AnchorRelativeVelocity { get; set; }
    
    // Local state
    private Transform tableTransform; // Use table as reference frame - it's aligned on both devices
    private Transform sharedAnchor; // Kept for fallback
    private Rigidbody rb;
    private float lastSyncTime;
    private float lastHitTime;
    private Vector3 localVelocity;
    private bool isInitialized;
    private TableTennisGameManager gameManager;
    private int currentServerSide = 1; // Which side to spawn ball (1 or 2)
    private bool localPositioningMode = true; // Local flag for positioning
    private bool ballAdjustModeActive = false; // Toggle with A button to enable ball movement
    private Vector3 initialTablePosition; // Track initial table position for debugging
    private Quaternion initialTableRotation;
    
    // For interpolation on clients
    private Vector3 targetPosition;
    private Vector3 previousPosition;
    private float interpolationTime;
    
    [Header("Visual Settings")]
    [SerializeField] private float ballRadius = 0.04f; // 8cm diameter for better visibility (real is 4cm)
    [SerializeField] private Color ballColor = new Color(1f, 0.5f, 0f); // Orange for visibility
    
    public override void Spawned()
    {
        rb = GetComponent<Rigidbody>();
        
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        
        // Initialize target position to current position to avoid jumps
        targetPosition = transform.position;
        previousPosition = transform.position;
        
        // Ensure ball has a visual mesh
        EnsureBallVisual();
        
        // Configure rigidbody
        rb.mass = 0.0027f; // Ping pong ball: 2.7 grams
        rb.drag = airResistance;
        rb.angularDrag = 0.5f;
        rb.useGravity = false; // We handle gravity manually for better control
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        
        // Only host simulates physics
        if (Object.HasStateAuthority)
        {
            rb.isKinematic = false;
            StartCoroutine(TryFindAnchorAndInitialize());
        }
        else
        {
            rb.isKinematic = true;
            StartCoroutine(TryFindAnchorAndInitialize());
        }
        
        Debug.Log($"[NetworkedBall] Spawned. HasStateAuthority: {Object.HasStateAuthority}, Position: {transform.position}");
    }
    
    /// <summary>
    /// Ensure the ball has a visible mesh and collider
    /// </summary>
    private void EnsureBallVisual()
    {
        // Check if already has a mesh renderer
        MeshRenderer existingRenderer = GetComponent<MeshRenderer>();
        MeshFilter existingFilter = GetComponent<MeshFilter>();
        
        if (existingRenderer == null || existingFilter == null)
        {
            // Create a sphere visual
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            
            // Copy mesh filter
            MeshFilter sphereFilter = sphere.GetComponent<MeshFilter>();
            if (existingFilter == null)
            {
                existingFilter = gameObject.AddComponent<MeshFilter>();
            }
            existingFilter.mesh = sphereFilter.sharedMesh;
            
            // Copy mesh renderer
            if (existingRenderer == null)
            {
                existingRenderer = gameObject.AddComponent<MeshRenderer>();
            }
            
            // Create material - use URP compatible shader
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Standard");
            
            Material mat;
            if (shader != null)
            {
                mat = new Material(shader);
                mat.color = ballColor;
                // Set base color for URP shaders
                if (mat.HasProperty("_BaseColor"))
                {
                    mat.SetColor("_BaseColor", ballColor);
                }
            }
            else
            {
                // Fallback: use primitive's default material
                mat = sphere.GetComponent<Renderer>().material;
                mat.color = ballColor;
            }
            existingRenderer.material = mat;
            
            // Clean up temporary sphere
            Destroy(sphere);
            
            Debug.Log("[NetworkedBall] Created ball visual with URP shader");
        }
        
        // Set correct scale for ping pong ball size
        transform.localScale = Vector3.one * (ballRadius * 2f);
        
        // Ensure collider exists
        SphereCollider collider = GetComponent<SphereCollider>();
        if (collider == null)
        {
            collider = gameObject.AddComponent<SphereCollider>();
            collider.radius = 0.5f; // Normalized to scale
        }
        
        Debug.Log($"[NetworkedBall] Ball visual ensured. Scale: {transform.localScale}, Position: {transform.position}");
    }
    
    private IEnumerator TryFindAnchorAndInitialize()
    {
        // On client, wait for alignment to complete before initializing
        if (!Object.HasStateAuthority)
        {
            Debug.Log("[NetworkedBall][INIT] Client waiting for alignment to complete...");
            float waitTime = 0f;
            while (!AnchorGUIManager_AutoAlignment.AlignmentCompletedStatic && waitTime < 15f)
            {
                yield return new WaitForSeconds(0.5f);
                waitTime += 0.5f;
            }
            
            if (AnchorGUIManager_AutoAlignment.AlignmentCompletedStatic)
            {
                Debug.Log($"[NetworkedBall][INIT] Client alignment completed, proceeding with initialization after {waitTime}s");
            }
            else
            {
                Debug.LogWarning("[NetworkedBall][INIT] Client timed out waiting for alignment, proceeding anyway");
            }
        }
        
        int attempts = 0;
        
        // First, try to find the table - this is our main reference frame
        while (tableTransform == null && attempts < 50)
        {
            var table = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
            if (table != null)
            {
                tableTransform = table.transform;
                Debug.Log($"[NetworkedBall][FIND] Found table: {table.name} at position {tableTransform.position}, rotation={tableTransform.rotation.eulerAngles}, parent={tableTransform.parent?.name ?? "none"}");
            }
            
            // Also try to find anchor as fallback
            if (sharedAnchor == null)
            {
                var anchors = FindObjectsOfType<OVRSpatialAnchor>();
                foreach (var anchor in anchors)
                {
                    if (anchor.gameObject.name.Contains("Shared") || 
                        anchor.gameObject.name.Contains("Anchor"))
                    {
                        sharedAnchor = anchor.transform;
                        Debug.Log($"[NetworkedBall] Found anchor: {anchor.gameObject.name} at position {anchor.transform.position}");
                        break;
                    }
                }
            }
            
            attempts++;
            yield return new WaitForSeconds(0.2f);
        }
        
        if (tableTransform != null)
        {
            isInitialized = true;
            initialTablePosition = tableTransform.position;
            initialTableRotation = tableTransform.rotation;
            Debug.Log($"[NetworkedBall] Initialized with table at {tableTransform.position}, rotation={tableTransform.rotation.eulerAngles}");
            
            // Find game manager
            gameManager = FindObjectOfType<TableTennisGameManager>();
            
            if (Object.HasStateAuthority)
            {
                ResetToServePosition();
            }
            else
            {
                // Client: Initial position update
                Debug.Log($"[NetworkedBall][INIT] Client: TableRelativePosition={TableRelativePosition}");
                Debug.Log($"[NetworkedBall][INIT] Client: Table at {tableTransform.position}, rot={tableTransform.rotation.eulerAngles}");
                UpdateLocalPositionFromNetwork();
                Debug.Log($"[NetworkedBall][INIT] Client: Ball world position after update: {transform.position}");
            }
        }
        else
        {
            Debug.LogWarning("[NetworkedBall] Could not find table after 50 attempts, using fallback");
            
            // Fallback: Use static anchor position from AnchorGUIManager if available
            Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
            if (firstAnchor.sqrMagnitude > 0.01f)
            {
                Debug.Log($"[NetworkedBall] Using static anchor position as fallback: {firstAnchor}");
                GameObject fallbackObj = new GameObject("FallbackTable_FromStatic");
                fallbackObj.transform.position = firstAnchor;
                tableTransform = fallbackObj.transform;
            }
            else
            {
                // Last resort: create at origin
                tableTransform = new GameObject("FallbackTable_Origin").transform;
            }
            isInitialized = true;
        }
    }
    
    public override void FixedUpdateNetwork()
    {
        if (!isInitialized || tableTransform == null) return;
        
        if (Object.HasStateAuthority)
        {
            // Skip physics simulation if in positioning mode
            if (!IsInPositioningMode && !localPositioningMode)
            {
                SimulatePhysics();
                CheckForReset();
            }
            // Always sync position to network (including during positioning)
            SyncToNetwork();
        }
        else
        {
            // CLIENT: Force immediate position sync during positioning mode for smooth updates
            if (IsInPositioningMode && tableTransform != null)
            {
                Vector3 targetPosition = tableTransform.TransformPoint(TableRelativePosition);
                transform.position = targetPosition; // Force immediate sync
                if (rb != null)
                {
                    rb.position = targetPosition; // Also update rigidbody position
                }
            }
        }
    }
    
    private void Update()
    {
        if (!isInitialized || tableTransform == null) return;
        
        // Toggle ball adjust mode with A button (only in positioning mode)
        if ((IsInPositioningMode || localPositioningMode) && 
            (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
             OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch) ||
             OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch)))
        {
            ballAdjustModeActive = !ballAdjustModeActive;
            Debug.Log($"[NetworkedBall] Ball adjust mode: {(ballAdjustModeActive ? "ON - Use thumbsticks to move ball" : "OFF")}");
        }
        
        // Handle positioning mode with thumbsticks (only if adjust mode is active)
        if ((IsInPositioningMode || localPositioningMode) && ballAdjustModeActive)
        {
            HandlePositioningMode();
        }
        
        // Proximity-based racket hit detection (as fallback for collision issues)
        if (Object.HasStateAuthority)
        {
            CheckProximityRacketHit();
        }
        
        // Note: Client interpolation is handled in Render() for smoother updates
    }
    
    /// <summary>
    /// Handle thumbstick input to move ball position before starting game
    /// </summary>
    private void HandlePositioningMode()
    {
        // Only allow positioning if we have authority or it's local-only ball
        if (!Object.HasStateAuthority && Object != null && Object.IsValid) return;
        
        // Keep ball kinematic while positioning
        if (rb != null && !rb.isKinematic)
        {
            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
        }
        
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
            
            // Clamp height to reasonable range (0.5m to 2.5m)
            newPos.y = Mathf.Clamp(newPos.y, 0.5f, 2.5f);
            
            transform.position = newPos;
            
            // Update table-relative position for sync
            if (tableTransform != null)
            {
                TableRelativePosition = tableTransform.InverseTransformPoint(newPos);
            }
        }
    }
    
    /// <summary>
    /// Enter positioning mode - ball floats and can be moved
    /// </summary>
    public void EnterPositioningMode()
    {
        localPositioningMode = true;
        ballAdjustModeActive = true; // Auto-activate ball adjustment when entering positioning mode
        
        // Only access networked properties if spawned
        if (Object != null && Object.IsValid)
        {
            IsInPositioningMode = true;
            IsInPlay = false;
        }
        
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        
        Debug.Log("[NetworkedBall] Entered positioning mode - ball adjust ON, use thumbsticks to move");
    }
    
    /// <summary>
    /// Exit positioning mode and start physics (called when racket hits ball)
    /// </summary>
    public void ExitPositioningMode()
    {
        localPositioningMode = false;
        
        // Only access networked property if spawned
        if (Object != null && Object.IsValid)
        {
            IsInPositioningMode = false;
        }
        
        if (rb != null && (Object == null || !Object.IsValid || Object.HasStateAuthority))
        {
            rb.isKinematic = false;
        }
        
        Debug.Log("[NetworkedBall] Exited positioning mode - game started!");
    }
    
    /// <summary>
    /// Check if ball is in positioning mode (safe to call before Spawned)
    /// </summary>
    public bool InPositioningMode
    {
        get
        {
            // Use local flag if not spawned yet, otherwise check networked state
            if (Object == null || !Object.IsValid)
            {
                return localPositioningMode;
            }
            return IsInPositioningMode || localPositioningMode;
        }
    }
    
    private void SimulatePhysics()
    {
        localVelocity = rb.velocity;
        localVelocity.y -= gravity * Runner.DeltaTime;
        rb.velocity = localVelocity;
        
        // Simple table bounce
        if (transform.position.y <= tableHeight + 0.02f && localVelocity.y < 0)
        {
            Vector3 relPos = sharedAnchor.InverseTransformPoint(transform.position);
            if (Mathf.Abs(relPos.x) < 0.76f && Mathf.Abs(relPos.z) < 1.37f)
            {
                localVelocity.y = -localVelocity.y * bounciness;
                rb.velocity = localVelocity;
                transform.position = new Vector3(transform.position.x, tableHeight + 0.02f, transform.position.z);
                
                // Notify game manager of bounce
                if (gameManager != null)
                {
                    gameManager.OnBallBounce(transform.position);
                }
            }
        }
    }
    
    private void SyncToNetwork()
    {
        if (Time.time - lastSyncTime < 1f / syncRate) return;
        lastSyncTime = Time.time;
        
        // Use table as reference frame - table is aligned to same world position on both devices
        if (tableTransform != null)
        {
            Vector3 oldRelPos = TableRelativePosition;
            TableRelativePosition = tableTransform.InverseTransformPoint(transform.position);
            TableRelativeVelocity = tableTransform.InverseTransformDirection(rb.velocity);
            
            // Log when position changes significantly (every sync)
            if (Vector3.Distance(oldRelPos, TableRelativePosition) > 0.01f)
            {
                Debug.Log($"[NetworkedBall][HOST] SyncToNetwork: WorldPos={transform.position}, TablePos={tableTransform.position}, TableRelPos={TableRelativePosition}");
            }
        }
    }
    
    private void UpdateLocalPositionFromNetwork()
    {
        if (tableTransform == null)
        {
            Debug.LogWarning("[NetworkedBall] Client: tableTransform is null, cannot update position");
            return;
        }
        
        previousPosition = targetPosition;
        Vector3 newTargetPosition = tableTransform.TransformPoint(TableRelativePosition);
        
        // Only update if position changed significantly (avoid jitter)
        if (Vector3.Distance(newTargetPosition, targetPosition) > 0.001f)
        {
            targetPosition = newTargetPosition;
            interpolationTime = 0f;
            
            // Debug log when position changes
            Debug.Log($"[NetworkedBall][CLIENT] Position updated: TableRelPos={TableRelativePosition}, WorldPos={targetPosition}, Table at pos={tableTransform.position} rot={tableTransform.rotation.eulerAngles}");
        }
        
        // Actually move the ball to the target position
        transform.position = targetPosition;
    }
    
    private void InterpolatePosition()
    {
        if (tableTransform == null) return;
        
        interpolationTime += Time.deltaTime * syncRate;
        
        if (interpolationTime <= 1f)
        {
            transform.position = Vector3.Lerp(previousPosition, targetPosition, interpolationTime);
        }
        else
        {
            Vector3 worldVelocity = tableTransform.TransformDirection(TableRelativeVelocity);
            transform.position = targetPosition + worldVelocity * (interpolationTime - 1f) / syncRate;
        }
    }
    
    public override void Render()
    {
        if (!Object.HasStateAuthority && isInitialized)
        {
            // Client always follows the networked position
            // During positioning mode, the host updates TableRelativePosition as they move the ball
            UpdateLocalPositionFromNetwork();
        }
    }
    
    private void CheckForReset()
    {
        if (transform.position.y < resetBelowY)
        {
            Debug.Log("[NetworkedBall] Ball fell below threshold, resetting");
            
            // Notify game manager ball went out
            if (gameManager != null && IsInPlay)
            {
                gameManager.OnBallOut();
            }
            
            ResetToServePosition();
        }
        
        if (Time.time - lastHitTime > resetAfterSeconds && IsInPlay)
        {
            Debug.Log("[NetworkedBall] Ball inactive too long, resetting");
            
            // Notify game manager ball went out
            if (gameManager != null)
            {
                gameManager.OnBallOut();
            }
            
            ResetToServePosition();
        }
    }
    
    private void ResetToServePosition(int serverPlayerNumber = 1)
    {
        // Find table if not assigned
        if (tableObject == null)
        {
            FindTableObject();
        }
        
        // Also ensure tableTransform is set (for network sync)
        if (tableTransform == null && tableObject != null)
        {
            tableTransform = tableObject;
        }
        
        Vector3 worldServePos;
        
        if (tableObject != null)
        {
            // Position relative to table
            // Player 1 is on -Z side, Player 2 is on +Z side
            float zOffset = serverPlayerNumber == 1 ? -serveDistanceFromCenter : serveDistanceFromCenter;
            Vector3 localServePos = new Vector3(0, serveHeight, zOffset);
            worldServePos = tableObject.TransformPoint(localServePos);
        }
        else if (tableTransform != null)
        {
            // Fallback to tableTransform
            float zOffset = serverPlayerNumber == 1 ? -serveDistanceFromCenter : serveDistanceFromCenter;
            Vector3 servePosition = new Vector3(0, tableHeight + serveHeight, zOffset);
            worldServePos = tableTransform.TransformPoint(servePosition);
        }
        else
        {
            worldServePos = new Vector3(0, 1.2f, serverPlayerNumber == 1 ? -1f : 1f);
        }
        
        transform.position = worldServePos;
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        localVelocity = Vector3.zero;
        
        // Enter positioning mode - player adjusts with thumbsticks, hit to start
        EnterPositioningMode();
        
        lastHitTime = Time.time;
        
        // Update table-relative position for sync
        if (tableTransform != null)
        {
            TableRelativePosition = tableTransform.InverseTransformPoint(worldServePos);
        }
        TableRelativeVelocity = Vector3.zero;
        
        Debug.Log($"[NetworkedBall] Reset to serve position for Player {serverPlayerNumber}: {worldServePos} - IN POSITIONING MODE");
    }
    
    /// <summary>
    /// Called to refresh the table reference after alignment changes.
    /// Useful for clients when the table position changes due to re-alignment.
    /// </summary>
    public void RefreshTableReference()
    {
        // Re-find the table
        var table = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        if (table != null)
        {
            Transform oldTable = tableTransform;
            tableTransform = table.transform;
            
            bool positionChanged = oldTable == null || Vector3.Distance(oldTable.position, tableTransform.position) > 0.01f;
            bool rotationChanged = oldTable == null || Quaternion.Angle(oldTable.rotation, tableTransform.rotation) > 1f;
            
            Debug.Log($"[NetworkedBall][REFRESH] Table reference refreshed: {table.name} at pos={tableTransform.position}, rot={tableTransform.rotation.eulerAngles}");
            if (positionChanged || rotationChanged)
            {
                Debug.Log($"[NetworkedBall][REFRESH] Table MOVED! Position changed: {positionChanged}, Rotation changed: {rotationChanged}");
                
                // Immediately update ball position based on new table transform
                if (!Object.HasStateAuthority)
                {
                    Vector3 newWorldPos = tableTransform.TransformPoint(TableRelativePosition);
                    transform.position = newWorldPos;
                    targetPosition = newWorldPos;
                    previousPosition = newWorldPos;
                    Debug.Log($"[NetworkedBall][REFRESH] Client ball position updated to {newWorldPos}");
                }
            }
            
            // Track initial if not set
            if (initialTablePosition == Vector3.zero)
            {
                initialTablePosition = tableTransform.position;
                initialTableRotation = tableTransform.rotation;
            }
        }
        else
        {
            Debug.LogWarning("[NetworkedBall][REFRESH] Could not find table to refresh reference");
        }
    }
    
    private void FindTableObject()
    {
        if (tableObject != null) return;
        
        // Try to find by tag
        GameObject tableByTag = GameObject.FindGameObjectWithTag(tableTag);
        if (tableByTag != null)
        {
            tableObject = tableByTag.transform;
            Debug.Log($"[NetworkedBall] Found table: {tableObject.name}");
            return;
        }
        
        // Try to find by name
        var allObjects = FindObjectsOfType<Transform>();
        foreach (var obj in allObjects)
        {
            if (obj.name.ToLower().Contains("table") && obj.GetComponent<Collider>() != null)
            {
                tableObject = obj;
                Debug.Log($"[NetworkedBall] Found table by name: {tableObject.name}");
                return;
            }
        }
    }
    
    /// <summary>
    /// Proximity-based racket hit detection (fallback for collision issues with kinematic rigidbodies)
    /// </summary>
    private void CheckProximityRacketHit()
    {
        // Don't check if we just hit or haven't been spawned long enough
        if (Time.time - lastHitTime < 0.3f) return;
        
        float hitDistance = 0.12f; // 12cm proximity threshold
        float minRacketSpeed = 0.5f; // Minimum racket speed to register hit
        
        // Find all rackets by tag
        GameObject[] rackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var racket in rackets)
        {
            if (racket == null || !racket.activeInHierarchy) continue;
            
            float distance = Vector3.Distance(transform.position, racket.transform.position);
            if (distance < hitDistance)
            {
                // Get racket velocity
                Vector3 racketVelocity = Vector3.zero;
                
                // Try ControllerRacket first
                var controllerRacket = racket.GetComponentInParent<ControllerRacket>();
                if (controllerRacket != null)
                {
                    racketVelocity = controllerRacket.GetRacketVelocity(racket);
                }
                else
                {
                    // Try Rigidbody
                    var racketRb = racket.GetComponent<Rigidbody>();
                    if (racketRb != null)
                    {
                        racketVelocity = racketRb.velocity;
                    }
                    else
                    {
                        // Fallback: use controller velocity
                        racketVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch);
                        if (racketVelocity.magnitude < minRacketSpeed)
                        {
                            racketVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.LTouch);
                        }
                    }
                }
                
                // Check if racket is moving fast enough
                if (racketVelocity.magnitude > minRacketSpeed)
                {
                    Debug.Log($"[NetworkedBall] PROXIMITY HIT! Distance: {distance:F3}, RacketVel: {racketVelocity.magnitude:F2}");
                    
                    // Calculate hit velocity
                    Vector3 racketToBall = (transform.position - racket.transform.position).normalized;
                    Vector3 swingDir = racketVelocity.normalized;
                    Vector3 hitDir = (swingDir * 0.7f + racketToBall * 0.3f).normalized;
                    
                    float hitSpeed = Mathf.Clamp(racketVelocity.magnitude * 2f, 2f, 12f);
                    Vector3 hitVelocity = hitDir * hitSpeed;
                    
                    // Add slight upward arc
                    if (hitVelocity.y < 0.5f && hitVelocity.y > -3f)
                    {
                        hitVelocity.y += 1f;
                    }
                    
                    OnRacketHit(hitVelocity, transform.position, 0);
                    return;
                }
            }
        }
    }
    
    /// <summary>
    /// Called when racket hits the ball. Only processed on host.
    /// </summary>
    public void OnRacketHit(Vector3 hitVelocity, Vector3 hitPoint, int playerNumber = 0)
    {
        if (!Object.HasStateAuthority) return;
        
        // Exit positioning mode when hit
        if (IsInPositioningMode || localPositioningMode)
        {
            ExitPositioningMode();
            Debug.Log("[NetworkedBall] Ball hit in positioning mode - starting game!");
        }
        
        // Enable physics
        if (rb != null && rb.isKinematic)
        {
            rb.isKinematic = false;
        }
        
        rb.velocity = hitVelocity;
        localVelocity = hitVelocity;
        IsInPlay = true;
        lastHitTime = Time.time;
        
        // Notify game manager
        if (gameManager != null)
        {
            gameManager.OnBallHit(playerNumber);
        }
        
        Debug.Log($"[NetworkedBall] Hit by player {playerNumber} with velocity: {hitVelocity}");
    }
    
    /// <summary>
    /// Request a serve (can be called by any player)
    /// </summary>
    /// <param name="serverPlayerNumber">1 for player 1 side, 2 for player 2 side</param>
    public void RequestServe(int serverPlayerNumber)
    {
        if (Object.HasStateAuthority)
        {
            Serve(serverPlayerNumber);
        }
        else
        {
            RPC_RequestServe(serverPlayerNumber);
        }
    }
    
    // Legacy overload for compatibility
    public void RequestServe(Vector3 direction)
    {
        RequestServe(1); // Default to player 1
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestServe(int serverPlayerNumber)
    {
        Serve(serverPlayerNumber);
    }
    
    private void Serve(int serverPlayerNumber)
    {
        currentServerSide = serverPlayerNumber;
        ResetToServePosition(serverPlayerNumber);
        StartCoroutine(ApplyServeVelocity(serverPlayerNumber));
    }
    
    private IEnumerator ApplyServeVelocity(int serverPlayerNumber)
    {
        yield return new WaitForSeconds(0.5f);
        
        // Serve direction: toward the opponent's side
        Vector3 serveDir = serverPlayerNumber == 1 ? Vector3.forward : Vector3.back;
        
        // If we have a table, use its forward direction
        if (tableObject != null)
        {
            serveDir = serverPlayerNumber == 1 ? tableObject.forward : -tableObject.forward;
        }
        else if (sharedAnchor != null)
        {
            serveDir = serverPlayerNumber == 1 ? sharedAnchor.forward : -sharedAnchor.forward;
        }
        
        Vector3 velocity = serveDir * serveForce;
        rb.velocity = velocity;
        localVelocity = velocity;
        IsInPlay = true;
        lastHitTime = Time.time;
    }
    
    private void OnCollisionEnter(Collision collision)
    {
        if (!Object.HasStateAuthority) return;

        // Check for racket hit
        if (collision.gameObject.CompareTag("Racket") ||
            collision.gameObject.layer == LayerMask.NameToLayer("Racket"))
        {
            Rigidbody racketRb = collision.gameObject.GetComponent<Rigidbody>();
            Vector3 hitVelocity;

            if (racketRb != null)
            {
                // Use racket velocity for more responsive hits (increased from 1.5x to 2.2x)
                hitVelocity = racketRb.velocity * racketHitMultiplier;
            }
            else
            {
                // Fallback to relative velocity (increased from 0.8x to 1.2x)
                hitVelocity = collision.relativeVelocity * fallbackHitMultiplier;
            }

            // Ensure minimum upward velocity for playability
            hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);

            OnRacketHit(hitVelocity, collision.contacts[0].point);
            return;
        }

        // Check for table collision (bounce)
        if (collision.gameObject.CompareTag("Table") ||
            collision.gameObject.CompareTag(tableTag) ||
            (tableObject != null && collision.gameObject == tableObject.gameObject))
        {
            // Bounce off table with bounciness factor
            Vector3 normal = collision.contacts[0].normal;
            Vector3 velocity = rb.velocity;

            // Reflect velocity with bounciness
            Vector3 reflectedVelocity = Vector3.Reflect(velocity, normal) * bounciness;

            rb.velocity = reflectedVelocity;
            localVelocity = reflectedVelocity;

            Debug.Log($"[NetworkedBall] Ball bounced on table - New velocity: {reflectedVelocity}");
            return;
        }

        // Check for ground hit (respawn needed)
        if (collision.gameObject.CompareTag("Ground") ||
            collision.gameObject.layer == LayerMask.NameToLayer("Default") ||
            transform.position.y < resetBelowY)
        {
            Debug.Log($"[NetworkedBall] Ball hit ground - Respawn needed");
            OnGroundHit();
        }
    }

    /// <summary>
    /// Called when ball hits the ground - shows message and waits for GRIP to respawn
    /// </summary>
    private void OnGroundHit()
    {
        if (!Object.HasStateAuthority) return;

        // Stop ball physics
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        IsInPlay = false;

        Debug.Log($"[NetworkedBall] Ground hit - Ball stopped. Press GRIP to respawn.");

        // Notify PassthroughGameManager to show respawn message
        var passthroughManager = FindObjectOfType<PassthroughGameManager>();
        if (passthroughManager != null)
        {
            passthroughManager.OnBallGroundHit();
        }
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (!Object.HasStateAuthority) return;

        if (other.CompareTag("Racket"))
        {
            Rigidbody racketRb = other.GetComponent<Rigidbody>();
            if (racketRb != null)
            {
                // Use same multiplier as OnCollisionEnter for consistency
                Vector3 hitVelocity = racketRb.velocity * racketHitMultiplier;
                hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
                OnRacketHit(hitVelocity, other.ClosestPoint(transform.position));
            }
        }
    }
}
