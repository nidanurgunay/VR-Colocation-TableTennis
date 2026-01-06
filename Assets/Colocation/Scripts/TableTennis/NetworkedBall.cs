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
    [SerializeField] private float bounciness = 0.85f;
    [SerializeField] private float airResistance = 0.02f;
    [SerializeField] private float tableHeight = 0.76f; // Standard table tennis height
    
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
    
    // Networked state - anchor relative
    [Networked] private Vector3 AnchorRelativePosition { get; set; }
    [Networked] private Vector3 AnchorRelativeVelocity { get; set; }
    [Networked] private NetworkBool IsInPlay { get; set; }
    [Networked] private NetworkBool IsInPositioningMode { get; set; } // Ball can be moved with thumbsticks
    
    // Local state
    private Transform sharedAnchor;
    private Rigidbody rb;
    private float lastSyncTime;
    private float lastHitTime;
    private Vector3 localVelocity;
    private bool isInitialized;
    private TableTennisGameManager gameManager;
    private int currentServerSide = 1; // Which side to spawn ball (1 or 2)
    private bool localPositioningMode = true; // Local flag for positioning
    
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
        int attempts = 0;
        while (sharedAnchor == null && attempts < 50)
        {
            // Try to find the shared anchor
            var anchors = FindObjectsOfType<OVRSpatialAnchor>();
            foreach (var anchor in anchors)
            {
                if (anchor.gameObject.name.Contains("Shared") || 
                    anchor.gameObject.name.Contains("Anchor"))
                {
                    sharedAnchor = anchor.transform;
                    Debug.Log($"[NetworkedBall] Found anchor: {anchor.gameObject.name}");
                    break;
                }
            }
            
            if (sharedAnchor == null)
            {
                // Also try finding by tag or AlignmentManager reference
                var alignmentManager = FindObjectOfType<AlignmentManager>();
                if (alignmentManager != null)
                {
                    var anchorObj = GameObject.FindGameObjectWithTag("SharedAnchor");
                    if (anchorObj != null)
                    {
                        sharedAnchor = anchorObj.transform;
                    }
                }
            }
            
            attempts++;
            yield return new WaitForSeconds(0.2f);
        }
        
        if (sharedAnchor != null)
        {
            isInitialized = true;
            
            // Find game manager
            gameManager = FindObjectOfType<TableTennisGameManager>();
            
            if (Object.HasStateAuthority)
            {
                ResetToServePosition();
            }
            else
            {
                UpdateLocalPositionFromNetwork();
            }
        }
        else
        {
            Debug.LogWarning("[NetworkedBall] Could not find shared anchor after 50 attempts");
            sharedAnchor = new GameObject("FallbackAnchor").transform;
            isInitialized = true;
        }
    }
    
    public override void FixedUpdateNetwork()
    {
        if (!isInitialized || sharedAnchor == null) return;
        
        if (Object.HasStateAuthority)
        {
            // Skip physics simulation if in positioning mode
            if (!IsInPositioningMode)
            {
                SimulatePhysics();
                CheckForReset();
            }
            SyncToNetwork();
        }
    }
    
    private void Update()
    {
        if (!isInitialized || sharedAnchor == null) return;
        
        // Handle positioning mode with thumbsticks
        if (IsInPositioningMode || localPositioningMode)
        {
            HandlePositioningMode();
        }
        
        if (!Object.HasStateAuthority)
        {
            InterpolatePosition();
        }
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
            
            // Update anchor-relative position for sync
            if (sharedAnchor != null)
            {
                AnchorRelativePosition = sharedAnchor.InverseTransformPoint(newPos);
            }
        }
    }
    
    /// <summary>
    /// Enter positioning mode - ball floats and can be moved
    /// </summary>
    public void EnterPositioningMode()
    {
        localPositioningMode = true;
        
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
        
        Debug.Log("[NetworkedBall] Entered positioning mode - use thumbsticks to adjust, hit ball to start");
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
        
        AnchorRelativePosition = sharedAnchor.InverseTransformPoint(transform.position);
        AnchorRelativeVelocity = sharedAnchor.InverseTransformDirection(rb.velocity);
    }
    
    private void UpdateLocalPositionFromNetwork()
    {
        if (sharedAnchor == null) return;
        
        previousPosition = targetPosition;
        targetPosition = sharedAnchor.TransformPoint(AnchorRelativePosition);
        interpolationTime = 0f;
    }
    
    private void InterpolatePosition()
    {
        interpolationTime += Time.deltaTime * syncRate;
        
        if (interpolationTime <= 1f)
        {
            transform.position = Vector3.Lerp(previousPosition, targetPosition, interpolationTime);
        }
        else
        {
            Vector3 worldVelocity = sharedAnchor.TransformDirection(AnchorRelativeVelocity);
            transform.position = targetPosition + worldVelocity * (interpolationTime - 1f) / syncRate;
        }
    }
    
    public override void Render()
    {
        if (!Object.HasStateAuthority && isInitialized)
        {
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
        
        Vector3 worldServePos;
        
        if (tableObject != null)
        {
            // Position relative to table
            // Player 1 is on -Z side, Player 2 is on +Z side
            float zOffset = serverPlayerNumber == 1 ? -serveDistanceFromCenter : serveDistanceFromCenter;
            Vector3 localServePos = new Vector3(0, serveHeight, zOffset);
            worldServePos = tableObject.TransformPoint(localServePos);
        }
        else if (sharedAnchor != null)
        {
            // Fallback to anchor-relative
            float zOffset = serverPlayerNumber == 1 ? -serveDistanceFromCenter : serveDistanceFromCenter;
            Vector3 servePosition = new Vector3(0, tableHeight + serveHeight, zOffset);
            worldServePos = sharedAnchor.TransformPoint(servePosition);
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
        
        // Update anchor-relative position for sync
        if (sharedAnchor != null)
        {
            AnchorRelativePosition = sharedAnchor.InverseTransformPoint(worldServePos);
        }
        AnchorRelativeVelocity = Vector3.zero;
        
        Debug.Log($"[NetworkedBall] Reset to serve position for Player {serverPlayerNumber}: {worldServePos} - IN POSITIONING MODE");
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
        
        if (collision.gameObject.CompareTag("Racket") || 
            collision.gameObject.layer == LayerMask.NameToLayer("Racket"))
        {
            Rigidbody racketRb = collision.gameObject.GetComponent<Rigidbody>();
            Vector3 hitVelocity;
            
            if (racketRb != null)
            {
                hitVelocity = racketRb.velocity * 1.5f;
            }
            else
            {
                hitVelocity = collision.relativeVelocity * 0.8f;
            }
            
            hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
            
            OnRacketHit(hitVelocity, collision.contacts[0].point);
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
                Vector3 hitVelocity = racketRb.velocity * 1.5f;
                hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
                OnRacketHit(hitVelocity, other.ClosestPoint(transform.position));
            }
        }
    }
}
