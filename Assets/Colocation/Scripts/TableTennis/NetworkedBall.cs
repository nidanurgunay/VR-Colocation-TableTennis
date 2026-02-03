using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Networked ping pong ball for colocation table tennis.
/// Host has physics authority, syncs world position directly to clients.
/// Relies on colocation alignment to ensure world coordinates match on both devices.
/// </summary>
public class NetworkedBall : NetworkBehaviour
{
    [Header("Physics Settings")]
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private float bounciness = 0.95f; // Increased from 0.92 for more realistic ping pong bounce
    [SerializeField] private float airResistance = 0.02f;
    [SerializeField] private float tableHeight = 0.76f; // Standard table tennis height

    [Header("Serve Settings")]
    [SerializeField] private float serveForce = 3f;

    [Header("Table Reference")]
    [SerializeField] private Transform tableObject;
    [SerializeField] private string tableTag = "Table";

    [Header("Sync Settings")]
    [SerializeField] private float syncRate = 30f; // Hz

    [Header("Reset Settings")]
    [SerializeField] private float resetBelowY = -1f; // Reset if ball falls below this
    [SerializeField] private float resetAfterSeconds = 5f; // Reset if no activity

    // Networked state - direct world position sync (relies on colocation alignment)
    [Networked] private Vector3 SyncedWorldPosition { get; set; }
    [Networked] private Quaternion SyncedWorldRotation { get; set; }
    [Networked] private Vector3 SyncedVelocity { get; set; }
    [Networked] private NetworkBool IsInPlay { get; set; }
    [Networked] private NetworkBool IsInPositioningMode { get; set; } // Ball can be moved with thumbsticks
    [Networked] public int CurrentAuthority { get; set; } // 1 or 2 - which player has ball authority (service)
    [Networked] public int ScorePlayer1 { get; set; }
    [Networked] public int ScorePlayer2 { get; set; }

    // Local state
    private Transform tableTransform; // Used for serve positioning and side detection
    private Rigidbody rb;
    private float lastSyncTime;
    private float lastHitTime;
    private Vector3 localVelocity;
    private bool isInitialized;
    private TableTennisGameManager gameManager;
    private int currentServerSide = 1; // Which side to spawn ball (1 or 2)
    private bool localPositioningMode = true;
    private GameObject floorPlane; // Floor collider for detecting ball out of play
    private float lastResetTime = 0f; // Cooldown to prevent reset loops

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
            // Initialize authority to Player 1 for first serve
            if (CurrentAuthority == 0)
            {
                CurrentAuthority = 1;
            }
            // Initialize synced world position
            SyncedWorldPosition = transform.position;
            SyncedWorldRotation = transform.rotation;
            SyncedVelocity = Vector3.zero;
            StartCoroutine(TryFindAnchorAndInitialize());
        }
        else
        {
            rb.isKinematic = true;
            StartCoroutine(TryFindAnchorAndInitialize());
        }


        // Create floor collider for detecting ball out of play (host only)
        if (Object.HasStateAuthority)
        {
            CreateFloorCollider();
        }
    }

    /// <summary>
    /// Create an invisible floor plane below the play area to detect when ball falls
    /// </summary>
    private void CreateFloorCollider()
    {
        // Don't create if already exists
        if (floorPlane != null) return;

        floorPlane = new GameObject("BallFloorCollider");
        floorPlane.transform.position = new Vector3(0, -0.5f, 0); // 0.5m below origin (typical floor level)
        floorPlane.transform.rotation = Quaternion.identity;

        // Create a large box collider to catch the ball
        BoxCollider boxCollider = floorPlane.AddComponent<BoxCollider>();
        boxCollider.size = new Vector3(20f, 0.1f, 20f); // 20m x 20m floor area
        boxCollider.isTrigger = true; // Use trigger to avoid physics interference

        // Tag as Ground for collision detection
        floorPlane.tag = "Ground";

        // Make floor invisible (no renderer)
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
    
        PhysicMaterial ballMaterial = Resources.Load<PhysicMaterial>("PingPongBall");
        if (ballMaterial != null)
        {
            collider.material = ballMaterial;
        }
        else
        {
            // Fallback: Create bouncy material dynamically
            PhysicMaterial bouncyMat = new PhysicMaterial("BallMaterial");
            bouncyMat.bounciness = 0.95f;
            bouncyMat.dynamicFriction = 0.1f;
            bouncyMat.staticFriction = 0.1f;
            bouncyMat.frictionCombine = PhysicMaterialCombine.Minimum;
            bouncyMat.bounceCombine = PhysicMaterialCombine.Maximum;
            collider.material = bouncyMat;
        }

    }

    /// <summary>
    /// Called when app is paused (headset removed) or resumed (headset put back on)
    /// </summary>
    private void OnApplicationPause(bool pauseStatus)
    {
        if (!pauseStatus) // Resuming (headset put back on)
        {

            // Refresh table reference in case tracking re-localized
            RefreshTableReference();

            // If we're the host, check if ball is in a valid position
            if (Object != null && Object.HasStateAuthority)
            {
                // Give a short delay for tracking to stabilize
                StartCoroutine(CheckBallPositionAfterResume());
            }
        }
    }

    private IEnumerator CheckBallPositionAfterResume()
    {
        yield return new WaitForSeconds(0.5f); // Wait for tracking to stabilize

        // Check if ball is in a reasonable position
        if (tableTransform != null)
        {
            float distanceFromTable = Vector3.Distance(transform.position, tableTransform.position);
            if (distanceFromTable > 5f || transform.position.y < -1f || transform.position.y > 5f)
            {
                ResetToServePosition();
            }
        }
    }

    private IEnumerator TryFindAnchorAndInitialize()
    {
        // On client, wait for alignment to complete before initializing
        if (!Object.HasStateAuthority)
        {
            float waitTime = 0f;
            while (!AnchorGUIManager_AutoAlignment.AlignmentCompletedStatic && waitTime < 15f)
            {
                yield return new WaitForSeconds(0.5f);
                waitTime += 0.5f;
            }

        }

        int attempts = 0;

        // Find the table - used for serve positioning and side detection
        while (tableTransform == null && attempts < 50)
        {
            var table = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable")
                        ?? GameObject.Find("pingpong_table") ?? GameObject.Find("pingpong")
                        ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis")
                        ?? GameObject.Find("Table");
            if (table != null)
            {
                tableTransform = table.transform;
            }

            attempts++;
            yield return new WaitForSeconds(0.2f);
        }

        if (tableTransform != null)
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
                // Client: Wait for initial sync
                StartCoroutine(ClientSyncRoutine());
            }
        }
        else
        {

            // Fallback: Use static anchor position from AnchorGUIManager if available
            Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
            if (firstAnchor.sqrMagnitude > 0.01f)
            {
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

    private IEnumerator ClientSyncRoutine()
    {

        int attempts = 0;
        while (SyncedWorldPosition == Vector3.zero && attempts < 50)
        {
            attempts++;
            yield return new WaitForSeconds(0.1f);
        }

        if (SyncedWorldPosition != Vector3.zero)
        {
            transform.position = SyncedWorldPosition;
            transform.rotation = SyncedWorldRotation;
            targetPosition = transform.position;
            previousPosition = transform.position;
            isInitialized = true;

        }

    }

    public override void FixedUpdateNetwork()
    {
        if (!isInitialized) return;

        if (Object.HasStateAuthority)
        {
            // SANITY CHECK: If ball is more than 5m from table, something is wrong - reset immediately
            if (tableTransform != null)
            {
                float distFromTable = Vector3.Distance(transform.position, tableTransform.position);
                if (distFromTable > 5f)
                {
                    ResetToServePosition();
                    return;
                }
            }

            // Skip physics simulation if in positioning mode
            if (!IsInPositioningMode && !localPositioningMode)
            {
                SimulatePhysics();
                CheckForReset();
            }
            // Always sync position to network (including during positioning)
            SyncToNetwork();
        }
    }

    private void Update()
    {
        // A BUTTON: Reset ball to serve position - works ALWAYS, even if not fully initialized!
        // This is the main way to recover a lost ball
        if (Object != null && Object.HasStateAuthority)
        {
            bool aButtonPressed = OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three);
            if (aButtonPressed)
            {

                // Try to find table if we don't have it
                if (tableTransform == null)
                {
                    RefreshTableReference();
                }

                ResetToServePosition();
                return; // Don't process other input this frame
            }
        }
  
        // Guard for other operations that need table
        if (!isInitialized || tableTransform == null) return;

        // Ball stick positioning disabled - users reposition by grabbing
        // Keep positioning mode logic for kinematic state only
        if (IsInPositioningMode || localPositioningMode)
        {
            HandlePositioningMode();
        }

        // Proximity-based racket hit detection (as fallback for collision issues)
        // Runs on both host and client - client sends RPC to host
        CheckProximityRacketHit();

        // Note: Client interpolation is handled in Render() for smoother updates
    }

    /// <summary>
    /// Handle thumbstick input to move ball position before starting game
    /// </summary>
    private void HandlePositioningMode()
    {
        // Ball repositioning via controller sticks is disabled.
        // Users now reposition the ball by grabbing it directly.

        // Only keep ball kinematic while in positioning mode
        if (rb != null && !rb.isKinematic)
        {
            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
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
        if (tableTransform != null && transform.position.y <= tableHeight + 0.02f && localVelocity.y < 0)
        {
            Vector3 relPos = tableTransform.InverseTransformPoint(transform.position);
            // Standard table tennis table: 1.525m x 2.74m (half = 0.76m x 1.37m)
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

        // Sync world position directly - relies on colocation alignment
        SyncedWorldPosition = transform.position;
        SyncedWorldRotation = transform.rotation;
        SyncedVelocity = rb != null ? rb.velocity : Vector3.zero;
    }

    private void UpdateLocalPositionFromNetwork()
    {
        // Use direct world position sync
        previousPosition = targetPosition;
        Vector3 newTargetPosition = SyncedWorldPosition;

        // Only update if position changed significantly (avoid jitter)
        if (Vector3.Distance(newTargetPosition, targetPosition) > 0.001f)
        {
            targetPosition = newTargetPosition;
            interpolationTime = 0f;
        }

        // Move the ball to the target position
        transform.position = targetPosition;
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
            // Predict based on velocity
            transform.position = targetPosition + SyncedVelocity * (interpolationTime - 1f) / syncRate;
        }
    }

    public override void Render()
    {
        if (!Object.HasStateAuthority && isInitialized)
        {
            // Always use direct world position sync
            UpdateLocalPositionFromNetwork();
        }
    }

    private void CheckForReset()
    {
        // Reset if ball falls below threshold
        if (transform.position.y < resetBelowY)
        {

            // Notify game manager ball went out
            if (gameManager != null && IsInPlay)
            {
                gameManager.OnBallOut();
            }

            ResetToServePosition();
            return;
        }

        // Reset if ball goes too high (above 10m - tracking issue)
        if (transform.position.y > 10f)
        {
            ResetToServePosition();
            return;
        }

        // Reset if ball is too far from table (more than 10m - tracking re-localization issue)
        if (tableTransform != null)
        {
            float distanceFromTable = Vector3.Distance(transform.position, tableTransform.position);
            if (distanceFromTable > 10f)
            {
                ResetToServePosition();
                return;
            }
        }

        // Reset if inactive too long
        if (Time.time - lastHitTime > resetAfterSeconds && IsInPlay)
        {

            // Notify game manager ball went out
            if (gameManager != null)
            {
                gameManager.OnBallOut();
            }

            ResetToServePosition();
        }
    }

    public void ResetToServePosition(int serverPlayerNumber = 1)
    {
        // COOLDOWN: Prevent reset loops - minimum 1 second between resets
        if (Time.time - lastResetTime < 1.0f)
        {
            return;
        }
        lastResetTime = Time.time;

        // Use current authority if available, otherwise use provided parameter
        if (CurrentAuthority != 0)
        {
            serverPlayerNumber = CurrentAuthority;
        }

        // Spawn ball between the two anchors, 0.5m up
        Vector3 worldServePos = GetSpawnPositionBetweenAnchors();


        // Find table for side detection (still needed for game logic)
        if (tableObject == null)
        {
            FindTableObject();
        }
        if (tableTransform == null && tableObject != null)
        {
            tableTransform = tableObject;
        }

        transform.position = worldServePos;
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        localVelocity = Vector3.zero;

        // Enter positioning mode - player adjusts with thumbsticks, hit to start
        EnterPositioningMode();

        lastHitTime = Time.time;

        // Sync world position directly
        SyncedWorldPosition = worldServePos;
        SyncedWorldRotation = transform.rotation;
        SyncedVelocity = Vector3.zero;

    }

    /// <summary>
    /// Called to refresh the table reference after alignment changes.
    /// </summary>
    public void RefreshTableReference()
    {
        var table = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable")
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        if (table != null)
        {
            tableTransform = table.transform;
        }

    }

    /// <summary>
    /// Calculate spawn position as the midpoint between the two anchors, 0.5m above.
    /// Uses world coordinates since both devices have aligned world space through colocation.
    /// </summary>
    private Vector3 GetSpawnPositionBetweenAnchors()
    {
        Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
        Vector3 secondAnchor = AnchorGUIManager_AutoAlignment.SecondAnchorPosition;

        // Check if we have valid anchor positions
        if (firstAnchor.sqrMagnitude > 0.01f && secondAnchor.sqrMagnitude > 0.01f)
        {
            // Calculate midpoint between anchors, always 0.5m above world midpoint
            Vector3 midpoint = (firstAnchor + secondAnchor) / 2f;
            midpoint.y += 0.5f;

            return midpoint;
        }
        else if (firstAnchor.sqrMagnitude > 0.01f)
        {
            // Only first anchor available
            Vector3 pos = firstAnchor;
            pos.y += 0.5f;
            return pos;
        }
        else
        {
            // Fallback: use camera position
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 pos = cam.transform.position + cam.transform.forward * 0.5f;
                pos.y = cam.transform.position.y; // Same height as camera
                return pos;
            }

            // Last resort
            return new Vector3(0, 0.5f, 0);
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
            return;
        }

        // Try to find by name
        var allObjects = FindObjectsOfType<Transform>();
        foreach (var obj in allObjects)
        {
            if (obj.name.ToLower().Contains("table") && obj.GetComponent<Collider>() != null)
            {
                tableObject = obj;
                return;
            }
        }
    }

    /// <summary>
    /// Proximity-based racket hit detection (fallback for collision issues with kinematic rigidbodies)
    /// Works on both host and client - client sends RPC to host
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

                    // Use racket face normal for hit direction
                    Vector3 racketNormal = racket.transform.up;
                    Vector3 hitVelocity = CalculateRacketHitVelocity(racket, Vector3.zero, racketNormal);

                    if (Object.HasStateAuthority)
                    {
                        // Host processes hit directly
                        OnRacketHit(hitVelocity, transform.position, 0);
                    }
                    else
                    {
                        // Client sends RPC to host
                        RPC_RequestHit(hitVelocity, transform.position);
                    }
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
        }

        // CLAMP velocity to prevent ball from flying off to infinity
        float maxSpeed = 8f; // Max 8 m/s - reasonable for table tennis in VR
        if (hitVelocity.magnitude > maxSpeed)
        {
            hitVelocity = hitVelocity.normalized * maxSpeed;
        }

        // Ensure some upward component so ball arcs nicely
        if (hitVelocity.y < 0.5f)
        {
            hitVelocity.y = 0.5f;
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

        // Notify game managers
        if (gameManager != null)
        {
            gameManager.OnBallHit(playerNumber);
        }

        var passthroughManager = FindObjectOfType<PassthroughGameManager>();
        if (passthroughManager != null)
        {
            passthroughManager.OnBallHit(playerNumber);
        }

    }

    /// <summary>
    /// Set spawn position explicitly (called by PassthroughGameManager after spawn)
    /// </summary>
    public void SetSpawnPosition(Vector3 worldPosition, Transform table = null)
    {

        // Set transform position
        transform.position = worldPosition;

        // Update rigidbody if exists
        if (rb != null)
        {
            rb.position = worldPosition;
        }

        // Update target position for interpolation
        targetPosition = worldPosition;
        previousPosition = worldPosition;

        // Set table reference if provided, or find it synchronously
        if (table != null)
        {
            tableTransform = table;
        }
        else if (tableTransform == null)
        {
            var tableObj = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable")
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
            if (tableObj != null)
            {
                tableTransform = tableObj.transform;
            }
        }

        // Sync world position directly
        SyncedWorldPosition = worldPosition;
        SyncedWorldRotation = transform.rotation;
        SyncedVelocity = Vector3.zero;

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

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_RequestHit(Vector3 hitVelocity, Vector3 hitPoint)
    {
        OnRacketHit(hitVelocity, hitPoint);
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
        else if (tableTransform != null)
        {
            serveDir = serverPlayerNumber == 1 ? tableTransform.forward : -tableTransform.forward;
        }

        Vector3 velocity = serveDir * serveForce;
        rb.velocity = velocity;
        localVelocity = velocity;
        IsInPlay = true;
        lastHitTime = Time.time;
    }

    private void OnCollisionEnter(Collision collision)
    {
        // Check for racket hit BEFORE authority guard - clients need to detect this too
        if (collision.gameObject.CompareTag("Racket") ||
            collision.gameObject.layer == LayerMask.NameToLayer("Racket"))
        {
            Vector3 hitPoint = collision.contacts.Length > 0 ? collision.contacts[0].point : transform.position;
            Vector3 contactNormal = collision.contacts.Length > 0 ? collision.contacts[0].normal : collision.gameObject.transform.up;
            Vector3 hitVelocity = CalculateRacketHitVelocity(collision.gameObject, collision.relativeVelocity, contactNormal);

            if (Object.HasStateAuthority)
            {
                // Host processes hit directly
                OnRacketHit(hitVelocity, hitPoint);
            }
            else
            {
                // Client sends RPC to host
                RPC_RequestHit(hitVelocity, hitPoint);
            }
            return;
        }

        // Remaining collision handling only on host
        if (!Object.HasStateAuthority) return;

        // Check for table collision (bounce)
        if (collision.gameObject.CompareTag("Table") ||
            collision.gameObject.CompareTag(tableTag) ||
            (tableObject != null && collision.gameObject == tableObject.gameObject))
        {
            if (gameManager != null)
            {
                gameManager.OnBallBounce(transform.position);
            }

            return;
        }

        // Check for ground hit (respawn needed)
        if (collision.gameObject.CompareTag("Ground") ||
            collision.gameObject.layer == LayerMask.NameToLayer("Default") ||
            transform.position.y < resetBelowY)
        {
            OnGroundHit();
        }
    }

    /// <summary>
    /// Calculate hit velocity from racket collision using WORLD SPACE physics.
    /// Since both VR devices have aligned world coordinates through colocation,
    /// all calculations use world space for accurate cross-device hit detection.
    /// </summary>
    private Vector3 CalculateRacketHitVelocity(GameObject racket, Vector3 relativeVelocity, Vector3 contactNormal)
    {
        // Get racket swing velocity in WORLD SPACE
        Vector3 worldRacketVelocity = GetWorldSpaceRacketVelocity(racket);

        // Racket face normal is already in world space (transform.up returns world direction)
        Vector3 worldHitNormal = contactNormal;
        if (worldHitNormal == Vector3.zero || worldHitNormal.magnitude < 0.1f)
        {
            worldHitNormal = racket.transform.up;
        }

        // Calculate hit direction based on swing
        float swingSpeed = worldRacketVelocity.magnitude;
        Vector3 swingDirection = swingSpeed > 0.1f ? worldRacketVelocity.normalized : worldHitNormal;

        // Blend swing direction with racket face normal
        Vector3 hitDirection;
        if (swingSpeed > 0.5f)
        {
            // Strong swing - primarily use swing direction
            hitDirection = (swingDirection * 0.8f + worldHitNormal * 0.2f).normalized;
        }
        else
        {
            // Weak swing - use racket face normal
            hitDirection = worldHitNormal;
        }

        // Calculate final speed
        float hitSpeed = Mathf.Clamp(swingSpeed * 1.5f, 2f, 8f);
        Vector3 hitVelocity = hitDirection * hitSpeed;

        // Ensure upward arc for table tennis (world Y axis)
        if (hitVelocity.y < 0.5f)
        {
            hitVelocity.y = 0.5f + swingSpeed * 0.2f;
        }


        return hitVelocity;
    }

    /// <summary>
    /// Get racket velocity in world space coordinates.
    /// ControllerRacket now provides world-space velocity directly.
    /// </summary>
    private Vector3 GetWorldSpaceRacketVelocity(GameObject racket)
    {
        // ControllerRacket.GetRacketVelocity now returns world-space velocity
        var controllerRacket = racket.GetComponentInParent<ControllerRacket>();
        if (controllerRacket != null)
        {
            return controllerRacket.GetRacketVelocity(racket);
        }

        // Rigidbody velocity is already in world space
        Rigidbody racketRb = racket.GetComponent<Rigidbody>();
        if (racketRb != null)
        {
            return racketRb.velocity;
        }

        // Fallback: Convert local controller velocity to world space
        Vector3 localVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch);
        if (localVelocity.magnitude < 0.3f)
        {
            localVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.LTouch);
        }

        Transform cameraRig = Camera.main?.transform.parent;
        if (cameraRig != null)
        {
            return cameraRig.TransformDirection(localVelocity);
        }

        return localVelocity;
    }

    /// <summary>
    /// Called when ball hits the ground - triggers round end and authority switch
    /// </summary>
    private void OnGroundHit()
    {
        if (!Object.HasStateAuthority) return;

        // Stop ball physics
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        IsInPlay = false;

        // Alternate authority: swap to the other player each round
        int nextAuthority = CurrentAuthority == 1 ? 2 : 1;
        CurrentAuthority = nextAuthority;

        // Notify game managers
        var passthroughManager = FindObjectOfType<PassthroughGameManager>();
        if (passthroughManager != null)
        {
            passthroughManager.OnBallGroundHit();
        }

        var tableTennisManager = FindObjectOfType<TableTennisManager>();
        if (tableTennisManager != null)
        {
            tableTennisManager.OnBallGroundHit();
        }

        // Notify via RPC for UI updates
        RPC_NotifyRoundEnd(nextAuthority);

        // Reset ball to serve position for next player
        ResetToServePosition(nextAuthority);
    }

    /// <summary>
    /// Determine which side (1 or 2) the ball hit on
    /// Side 1 is -Z, Side 2 is +Z (relative to table)
    /// </summary>
    private int DetermineHitSide()
    {
        if (tableTransform == null) return 1; // Default to side 1

        // Get ball position relative to table
        Vector3 localPos = tableTransform.InverseTransformPoint(transform.position);

        // Check which side based on Z coordinate
        return localPos.z < 0 ? 1 : 2;
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyRoundEnd(int nextAuthority)
    {
    }

    private void OnTriggerEnter(Collider other)
    {
        // Check for racket hit BEFORE authority guard - clients need to detect this too
        if (other.CompareTag("Racket"))
        {
            Vector3 hitPoint = other.ClosestPoint(transform.position);
            Vector3 contactNormal = other.transform.up; // Use racket face normal
            Vector3 hitVelocity = CalculateRacketHitVelocity(other.gameObject, Vector3.zero, contactNormal);

            if (Object.HasStateAuthority)
            {
                // Host processes hit directly
                OnRacketHit(hitVelocity, hitPoint);
            }
            else
            {
                // Client sends RPC to host
                RPC_RequestHit(hitVelocity, hitPoint);
            }
            return;
        }

        // Remaining trigger handling only on host
        if (!Object.HasStateAuthority) return;

        // Check for GROUND trigger (floor collider for ball out of play)
        if (other.CompareTag("Ground") || other.gameObject.name == "BallFloorCollider")
        {
            OnGroundHit();
            return;
        }
    }

    private void OnDestroy()
    {
        // Clean up floor collider when ball is destroyed
        if (floorPlane != null)
        {
            Destroy(floorPlane);
            floorPlane = null;
        }
    }
}
