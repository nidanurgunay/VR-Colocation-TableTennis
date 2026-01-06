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
    
    // Networked state - anchor relative
    [Networked] private Vector3 AnchorRelativePosition { get; set; }
    [Networked] private Vector3 AnchorRelativeVelocity { get; set; }
    [Networked] private NetworkBool IsInPlay { get; set; }
    
    // Local state
    private Transform sharedAnchor;
    private Rigidbody rb;
    private float lastSyncTime;
    private float lastHitTime;
    private Vector3 localVelocity;
    private bool isInitialized;
    private TableTennisGameManager gameManager;
    private int currentServerSide = 1; // Which side to spawn ball (1 or 2)
    
    // For interpolation on clients
    private Vector3 targetPosition;
    private Vector3 previousPosition;
    private float interpolationTime;
    
    [Header("Visual Settings")]
    [SerializeField] private float ballRadius = 0.02f; // 40mm diameter standard ping pong ball
    [SerializeField] private Color ballColor = Color.white;
    
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
            
            // Create material
            Material mat = new Material(Shader.Find("Standard"));
            if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
            {
                mat = new Material(Shader.Find("Unlit/Color"));
            }
            mat.color = ballColor;
            existingRenderer.material = mat;
            
            // Clean up temporary sphere
            Destroy(sphere);
            
            Debug.Log("[NetworkedBall] Created ball visual");
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
            SimulatePhysics();
            SyncToNetwork();
            CheckForReset();
        }
    }
    
    private void Update()
    {
        if (!isInitialized || sharedAnchor == null) return;
        
        if (!Object.HasStateAuthority)
        {
            InterpolatePosition();
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
        
        IsInPlay = false;
        lastHitTime = Time.time;
        
        // Update anchor-relative position for sync
        if (sharedAnchor != null)
        {
            AnchorRelativePosition = sharedAnchor.InverseTransformPoint(worldServePos);
        }
        AnchorRelativeVelocity = Vector3.zero;
        
        Debug.Log($"[NetworkedBall] Reset to serve position for Player {serverPlayerNumber}: {worldServePos}");
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
