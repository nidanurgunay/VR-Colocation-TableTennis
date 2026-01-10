using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Manages the table tennis game setup and spawns the networked ball.
/// Attach to a GameObject in the TableTennis scene.
/// </summary>
public class TableTennisManager : NetworkBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private NetworkPrefabRef ballPrefab;
    [SerializeField] private GameObject racketPrefab; // Local prefab, not networked
    
    [Header("Table Placement (relative to anchor)")]
    [SerializeField] private Vector3 tablePositionOffset = Vector3.zero; // Position offset from anchor
    [SerializeField] private float defaultTableHeight = 0.76f; // Standard ping pong table height
    [SerializeField] private float tableXRotationOffset = 180f; // X rotation offset in degrees
    [SerializeField] private float tableYRotationOffset = 0f; // Y rotation offset in degrees
    
    [Header("Runtime Adjustment Controls")]
    [SerializeField] private float moveSpeed = 2.0f; // Meters per second
    [SerializeField] private float rotateSpeed = 90f; // Degrees per second
    [SerializeField] private bool showAdjustmentInstructions = true;
    
    // Networked table position/rotation for syncing across players (LOCAL to anchor)
    [Networked] private Vector3 NetworkedTableLocalPosition { get; set; } // Position relative to primary anchor
    [Networked] private float NetworkedTableYRotation { get; set; }
    [Networked] private float NetworkedFloorOffset { get; set; } // Shared floor level adjustment
    [Networked] public NetworkBool GameStarted { get; set; } // True when game has started (ball spawned)
    
    private bool _tableParented = false; // Track if table is parented to anchor
    
    // Game phase tracking (for UI compatibility)
    public enum GamePhase { TableSetup, BallPositioning, Playing }
    public GamePhase CurrentPhase { get; private set; } = GamePhase.TableSetup;
    
    /// <summary>
    /// Called when ball is hit by a racket - transitions to Playing phase
    /// </summary>
    public void OnBallHit()
    {
        if (CurrentPhase != GamePhase.Playing)
        {
            CurrentPhase = GamePhase.Playing;
            Debug.Log("[TableTennisManager] Ball hit! Transitioning to Playing phase.");
        }
    }
    
    // Runtime adjustment state
    private GameObject tableRoot;
    private bool isInAdjustMode = false;
    private OVRCameraRig cameraRig;
    
    [Header("Table Setup")]
    [SerializeField] private Transform tableTransform;
    [SerializeField] private Vector3 racket1Position = new Vector3(-0.3f, 0.1f, 0f); // On table surface, player 1 side
    [SerializeField] private Vector3 racket2Position = new Vector3(0.3f, 0.1f, 0f);  // On table surface, player 2 side
    [SerializeField] private Vector3 racketRotation = new Vector3(0f, 0f, 0f); // Handle up
    
    [Header("Ball Spawn")]
    [SerializeField] private Vector3 ballSpawnOffset = new Vector3(0f, 0.5f, 0f); // Above table center
    
    // References
    private NetworkedBall spawnedBall;
    private Transform sharedAnchor;
    private Transform secondaryAnchor; // For 2-point alignment after scene transition
    private AlignmentManager alignmentManager;
    private GameObject[] localRackets = new GameObject[2];
    
    public override void Spawned()
    {
        Debug.Log($"[TableTennisManager] Spawned. HasStateAuthority: {Object.HasStateAuthority}");
        
        StartCoroutine(InitializeGame());
        
        if (showAdjustmentInstructions)
        {
            Debug.Log("[TableTennisManager] TABLE ADJUSTMENT CONTROLS:");
            Debug.Log("  - Press A button to TOGGLE adjust mode ON/OFF");
            Debug.Log("  - LEFT THUMBSTICK: Move table (X/Z)");
            Debug.Log("  - RIGHT THUMBSTICK X: Rotate table");
            Debug.Log("  - RIGHT THUMBSTICK Y: Move table up/down");
            Debug.Log("  - GRIP button: Spawn ball and start game");
        }
    }
    
    private void Update()
    {
        HandleTableAdjustment();
        HandleGameStartInput();
    }
    
    // Fusion calls this every network tick - apply networked table state
    public override void FixedUpdateNetwork()
    {
        // Apply networked table state to local table object
        ApplyNetworkedTableState();
    }
    
    /// <summary>
    /// Apply the networked table position/rotation to the local table object
    /// Uses LOCAL coordinates relative to anchor parent
    /// </summary>
    private void ApplyNetworkedTableState()
    {
        if (tableRoot == null || sharedAnchor == null) return;
        
        // Ensure table is parented to anchor
        if (!_tableParented)
        {
            tableRoot.transform.SetParent(sharedAnchor, worldPositionStays: false);
            _tableParented = true;
            Debug.Log($"[TableTennisManager] Table parented to anchor");
        }
        
        // Apply LOCAL position relative to anchor (Y includes floor offset)
        tableRoot.transform.localPosition = new Vector3(
            NetworkedTableLocalPosition.x,
            NetworkedTableLocalPosition.y + NetworkedFloorOffset,
            NetworkedTableLocalPosition.z
        );
        
        // Apply LOCAL rotation relative to anchor
        Quaternion localRotation = Quaternion.Euler(tableXRotationOffset, NetworkedTableYRotation, 0);
        tableRoot.transform.localRotation = localRotation;
    }
    
    /// <summary>
    /// Handle runtime table position/rotation adjustment via controller
    /// Only the state authority (host) can adjust
    /// </summary>
    private void HandleTableAdjustment()
    {
        // Try to find tableRoot if not set yet
        if (tableRoot == null)
        {
            tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable")
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
            
            if (tableRoot == null) return;
        }
        
        // Find camera rig for height adjustment
        if (cameraRig == null)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
        }
        
        // Toggle adjust mode with A button (Button.One) - check both controllers
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch) ||
            OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch))
        {
            isInAdjustMode = !isInAdjustMode;
            Debug.Log($"[TableTennisManager] Adjust mode: {(isInAdjustMode ? "ON" : "OFF")}");
        }
        
        if (!isInAdjustMode) return;
        
        // Only state authority can adjust table
        if (!Object.HasStateAuthority)
        {
            // Request adjustment from host via RPC
            HandleClientAdjustmentInput();
            return;
        }
        
        // Host: directly adjust networked values
        HandleHostAdjustmentInput();
    }
    
    /// <summary>
    /// Host directly modifies networked table state (LOCAL coordinates)
    /// </summary>
    private void HandleHostAdjustmentInput()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
        // Move table with left thumbstick (X/Z movement in local space)
        if (leftStick.magnitude > 0.1f)
        {
            Vector3 movement = new Vector3(leftStick.x, 0, leftStick.y) * moveSpeed * Time.deltaTime;
            movement = Quaternion.Euler(0, NetworkedTableYRotation, 0) * movement;
            NetworkedTableLocalPosition += movement;
        }
        
        // Rotate table with right thumbstick X axis
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            NetworkedTableYRotation += rotation;
        }
        
        // Adjust floor level with right thumbstick Y axis
        if (Mathf.Abs(rightStick.y) > 0.1f)
        {
            float verticalMove = rightStick.y * moveSpeed * Time.deltaTime;
            NetworkedFloorOffset += verticalMove;
        }
    }
    
    /// <summary>
    /// Client sends adjustment requests to host via RPC
    /// </summary>
    private void HandleClientAdjustmentInput()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
        // Send adjustment deltas to host
        if (leftStick.magnitude > 0.1f)
        {
            Vector3 movement = new Vector3(leftStick.x, 0, leftStick.y) * moveSpeed * Time.deltaTime;
            RPC_RequestTableMove(movement);
        }
        
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            RPC_RequestTableRotate(rotation);
        }
        
        if (Mathf.Abs(rightStick.y) > 0.1f)
        {
            float verticalMove = rightStick.y * moveSpeed * Time.deltaTime;
            RPC_RequestFloorAdjust(verticalMove);
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableMove(Vector3 movement)
    {
        movement = Quaternion.Euler(0, NetworkedTableYRotation, 0) * movement;
        NetworkedTableLocalPosition += movement;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableRotate(float rotation)
    {
        NetworkedTableYRotation += rotation;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestFloorAdjust(float verticalMove)
    {
        NetworkedFloorOffset += verticalMove;
    }
    
    private IEnumerator InitializeGame()
    {
        // Wait for anchor to be available
        yield return StartCoroutine(WaitForAnchor());
        
        // Place the table at the anchor position
        PlaceTableAtAnchor();
        
        // Setup controller-based rackets (replaces old grab system)
        SetupControllerRackets();
        
        // Host spawns the ball
        if (Object.HasStateAuthority)
        {
            yield return new WaitForSeconds(0.5f);
            SpawnBall();
        }
    }
    
    /// <summary>
    /// Place the table/pingpong BETWEEN the two anchors, parented to primary anchor
    /// This ensures consistent positioning for both host and client
    /// </summary>
    private void PlaceTableAtAnchor()
    {
        if (sharedAnchor == null)
        {
            Debug.LogWarning("[TableTennisManager] No anchor to place table at!");
            return;
        }
        
        // Find the PingPongTable (now a separate root object) or fallback to old names
        tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        
        if (tableRoot == null && tableTransform != null)
        {
            tableRoot = tableTransform.gameObject;
        }
        
        if (tableRoot != null)
        {
            // Parent table to primary anchor - this is critical for colocation!
            // When parented, the table will automatically stay in correct position
            // even if the camera rig is adjusted
            tableRoot.transform.SetParent(sharedAnchor, worldPositionStays: false);
            _tableParented = true;
            
            // Calculate LOCAL position (relative to primary anchor)
            Vector3 localTablePos;
            float tableYRotation;
            
            if (secondaryAnchor != null)
            {
                // PLACE TABLE BETWEEN TWO ANCHORS
                // Get secondary anchor position in primary anchor's local space
                Vector3 secondaryLocalPos = sharedAnchor.InverseTransformPoint(secondaryAnchor.position);
                
                // Midpoint between primary (0,0,0 in local) and secondary
                Vector3 midpoint = secondaryLocalPos / 2f;
                
                // Calculate rotation to face from primary to secondary (table long axis)
                Vector3 directionToSecondary = secondaryLocalPos;
                directionToSecondary.y = 0; // Keep horizontal
                
                if (directionToSecondary.sqrMagnitude > 0.01f)
                {
                    // Table Y rotation: face perpendicular to the anchor line (so players stand at each anchor)
                    tableYRotation = Mathf.Atan2(directionToSecondary.x, directionToSecondary.z) * Mathf.Rad2Deg;
                }
                else
                {
                    tableYRotation = tableYRotationOffset;
                }
                
                // Position at midpoint, at table height
                localTablePos = new Vector3(midpoint.x, defaultTableHeight, midpoint.z) + tablePositionOffset;
                
                Debug.Log($"[TableTennisManager] 2-ANCHOR: Secondary local pos: {secondaryLocalPos}, Midpoint: {midpoint}, TableYRot: {tableYRotation}");
            }
            else
            {
                // SINGLE ANCHOR: Use offset from primary anchor
                localTablePos = new Vector3(tablePositionOffset.x, defaultTableHeight, tablePositionOffset.z);
                tableYRotation = tableYRotationOffset;
                
                Debug.Log($"[TableTennisManager] SINGLE-ANCHOR: Using offset position");
            }
            
            // Host initializes networked values (LOCAL coordinates)
            if (Object.HasStateAuthority)
            {
                NetworkedTableLocalPosition = localTablePos;
                NetworkedTableYRotation = tableYRotation;
                NetworkedFloorOffset = 0f;
                Debug.Log($"[TableTennisManager] HOST: Set networked local pos: {localTablePos}, rot: {tableYRotation}");
            }
            
            // Apply LOCAL position and rotation relative to anchor
            tableRoot.transform.localPosition = localTablePos;
            tableRoot.transform.localRotation = Quaternion.Euler(tableXRotationOffset, tableYRotation, 0);
            
            Debug.Log($"[TableTennisManager] Table placed - LocalPos: {tableRoot.transform.localPosition}, LocalRot: {tableRoot.transform.localEulerAngles}, WorldPos: {tableRoot.transform.position}");
        }
        else
        {
            Debug.LogWarning("[TableTennisManager] Could not find table object to place at anchor");
        }
    }
    
    /// <summary>
    /// Setup ControllerRacket component to show racket on controllers
    /// </summary>
    private void SetupControllerRackets()
    {
        // ControllerRacket will auto-find rackets in the scene
        // Just create the manager if it doesn't exist
        var existingManager = GameObject.Find("ControllerRacketManager");
        if (existingManager == null)
        {
            var manager = new GameObject("ControllerRacketManager");
            // Use string-based AddComponent to avoid compile order issues
            var component = manager.AddComponent(System.Type.GetType("ControllerRacket"));
            if (component != null)
            {
                Debug.Log("[TableTennisManager] Created ControllerRacketManager - press grip to show racket on controller");
            }
            else
            {
                // Fallback: try direct add
                manager.AddComponent<ControllerRacket>();
                Debug.Log("[TableTennisManager] Created ControllerRacketManager (direct)");
            }
        }
    }
    
    /// <summary>
    /// Parent the pingpong/table objects to the anchor so they stay fixed in anchor space
    /// This is critical for colocation - objects must be relative to the shared anchor
    /// </summary>
    private void ParentSceneToAnchor()
    {
        if (sharedAnchor == null)
        {
            Debug.LogWarning("[TableTennisManager] No anchor to parent scene to!");
            return;
        }
        
        // Find the main game parent object
        GameObject gameRoot = null;
        
        // Try to find pingpong parent
        gameRoot = GameObject.Find("pingpong");
        if (gameRoot == null) gameRoot = GameObject.Find("PingPong");
        if (gameRoot == null) gameRoot = GameObject.Find("TableTennis");
        if (gameRoot == null && tableTransform != null) gameRoot = tableTransform.gameObject;
        
        if (gameRoot != null)
        {
            // Store current world position/rotation
            Vector3 worldPos = gameRoot.transform.position;
            Quaternion worldRot = gameRoot.transform.rotation;
            
            // Parent to anchor
            gameRoot.transform.SetParent(sharedAnchor, worldPositionStays: true);
            
            Debug.Log($"[TableTennisManager] Parented '{gameRoot.name}' to anchor. Local pos: {gameRoot.transform.localPosition}");
        }
        else
        {
            Debug.LogWarning("[TableTennisManager] Could not find game root object to parent to anchor");
        }
    }
    
    private IEnumerator WaitForAnchor()
    {
        int attempts = 0;
        OVRSpatialAnchor primaryOVRAnchor = null;
        OVRSpatialAnchor secondaryOVRAnchor = null;
        
        // Find AlignmentManager in scene (or create one)
        alignmentManager = FindObjectOfType<AlignmentManager>();
        if (alignmentManager == null)
        {
            var alignObj = new GameObject("AlignmentManager");
            alignmentManager = alignObj.AddComponent<AlignmentManager>();
            Debug.Log("[TableTennisManager] Created AlignmentManager for scene transition alignment");
        }
        
        // Try getting explicitly from AnchorGUIManager first (most reliable)
        var anchorGUI = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
        if (anchorGUI != null)
        {
            var localized = anchorGUI.GetLocalizedAnchor();
            if (localized != null)
            {
                sharedAnchor = localized.transform;
                primaryOVRAnchor = localized;
                Debug.Log($"[TableTennisManager] Found localized anchor from GUI: {localized.Uuid}");
            }
        }

        // Search for all preserved anchors
        while ((sharedAnchor == null || secondaryAnchor == null) && attempts < 50)
        {
            // Look for any OVRSpatialAnchor that was preserved from the previous scene
            var anchors = FindObjectsOfType<OVRSpatialAnchor>(true); // Include inactive
            
            foreach (var anchor in anchors)
            {
                if (anchor != null && anchor.Localized)
                {
                    if (sharedAnchor == null)
                    {
                        sharedAnchor = anchor.transform;
                        primaryOVRAnchor = anchor;
                        Debug.Log($"[TableTennisManager] Found PRIMARY anchor: {anchor.gameObject.name}, UUID: {anchor.Uuid}");
                    }
                    else if (anchor.transform != sharedAnchor && secondaryAnchor == null)
                    {
                        secondaryAnchor = anchor.transform;
                        secondaryOVRAnchor = anchor;
                        Debug.Log($"[TableTennisManager] Found SECONDARY anchor: {anchor.gameObject.name}, UUID: {anchor.Uuid}");
                    }
                }
            }
            
            attempts++;
            yield return new WaitForSeconds(0.2f);
        }
        
        if (sharedAnchor == null)
        {
            Debug.LogWarning("[TableTennisManager] Could not find shared anchor after 50 attempts");
            
            // Use table as fallback reference
            if (tableTransform != null)
            {
                sharedAnchor = tableTransform;
                Debug.Log("[TableTennisManager] Using table as fallback anchor reference");
            }
        }
        else
        {
            // CRITICAL: Re-align the camera rig to the preserved anchors!
            // Without this, each headset's OVRCameraRig starts at default (0,0,0) and objects appear misaligned.
            Debug.Log("[TableTennisManager] Re-aligning camera rig to preserved anchors after scene transition...");
            
            if (primaryOVRAnchor != null && secondaryOVRAnchor != null)
            {
                alignmentManager.AlignUserToTwoAnchors(primaryOVRAnchor, secondaryOVRAnchor);
                Debug.Log("[TableTennisManager] Applied 2-point alignment");
            }
            else if (primaryOVRAnchor != null)
            {
                alignmentManager.AlignUserToAnchor(primaryOVRAnchor);
                Debug.Log("[TableTennisManager] Applied single-point alignment");
            }
            
            // Wait for alignment to complete before placing table
            yield return new WaitForSeconds(1.0f);
        }
    }
    
    /// <summary>
    /// Find existing rackets in the scene and ensure they have GrabbableRacket component
    /// </summary>
    private void FindExistingRackets()
    {
        // Find rackets by tag or name in the scene
        var allRackets = GameObject.FindGameObjectsWithTag("Racket");
        
        if (allRackets.Length == 0)
        {
            // Try finding by name if not tagged
            var pingPongParent = GameObject.Find("pingpong");
            if (pingPongParent == null)
            {
                pingPongParent = GameObject.Find("PingPong");
            }
            
            if (pingPongParent != null)
            {
                // Find all children that might be rackets
                foreach (Transform child in pingPongParent.GetComponentsInChildren<Transform>())
                {
                    if (child.name.ToLower().Contains("racket") || child.name.ToLower().Contains("paddle"))
                    {
                        EnsureRacketSetup(child.gameObject);
                        
                        // Add to our tracking array
                        if (localRackets[0] == null)
                            localRackets[0] = child.gameObject;
                        else if (localRackets[1] == null)
                            localRackets[1] = child.gameObject;
                    }
                }
            }
        }
        else
        {
            // Found rackets by tag
            for (int i = 0; i < Mathf.Min(allRackets.Length, 2); i++)
            {
                localRackets[i] = allRackets[i];
                EnsureRacketSetup(allRackets[i]);
            }
        }
        
        int racketCount = (localRackets[0] != null ? 1 : 0) + (localRackets[1] != null ? 1 : 0);
        Debug.Log($"[TableTennisManager] Found {racketCount} existing rackets in scene");
    }
    
    private void EnsureRacketSetup(GameObject racket)
    {
        // Ensure it has a collider for ball detection
        if (racket.GetComponent<Collider>() == null)
        {
            var boxCollider = racket.AddComponent<BoxCollider>();
            // Adjust size based on typical racket dimensions
            boxCollider.size = new Vector3(0.15f, 0.01f, 0.17f);
        }
        
        // Ensure tagged for ball collision
        racket.tag = "Racket";
        
        // Add rigidbody for velocity tracking
        var rb = racket.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = racket.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true; // Start kinematic (on table)
        rb.useGravity = false;
    }
    
    private void SpawnBall()
    {
        if (ballPrefab == default)
        {
            Debug.LogError("[TableTennisManager] Ball prefab not assigned!");
            return;
        }
        
        if (Runner == null)
        {
            Debug.LogError("[TableTennisManager] Runner is null! Cannot spawn ball.");
            return;
        }
        
        Vector3 spawnPosition = Vector3.zero;
        
        // Try to find table position for ball spawn
        if (tableRoot != null)
        {
            // Spawn 50cm above the table center
            spawnPosition = tableRoot.transform.position + Vector3.up * 0.5f;
            Debug.Log($"[TableTennisManager] SpawnBall: Using tableRoot position: {tableRoot.transform.position}");
        }
        else if (tableTransform != null)
        {
            spawnPosition = tableTransform.TransformPoint(ballSpawnOffset);
            Debug.Log($"[TableTennisManager] SpawnBall: Using tableTransform: {tableTransform.position}");
        }
        else if (sharedAnchor != null)
        {
            // 1.2m above anchor (table height + ball offset)
            spawnPosition = sharedAnchor.position + Vector3.up * 1.2f;
            Debug.Log($"[TableTennisManager] SpawnBall: Using sharedAnchor: {sharedAnchor.position}");
        }
        else
        {
            // Last resort: spawn in front of head
            var head = FindObjectOfType<OVRCameraRig>()?.centerEyeAnchor;
            if (head != null)
            {
                spawnPosition = head.position + head.forward * 0.5f;
                Debug.Log($"[TableTennisManager] SpawnBall: Using head position fallback");
            }
            else
            {
                Debug.LogWarning("[TableTennisManager] SpawnBall: No reference found! Spawning at origin.");
            }
        }
        
        Debug.Log($"[TableTennisManager] Spawning ball at: {spawnPosition}");
        
        var ballObj = Runner.Spawn(
            ballPrefab,
            spawnPosition,
            Quaternion.identity,
            Object.InputAuthority
        );
        
        if (ballObj != null)
        {
            spawnedBall = ballObj.GetComponent<NetworkedBall>();
            CurrentPhase = GamePhase.BallPositioning;
            Debug.Log($"[TableTennisManager] Ball spawned successfully at {spawnPosition}");
        }
        else
        {
            Debug.LogError("[TableTennisManager] Runner.Spawn returned null!");
        }
    }
    
    /// <summary>
    /// Reset the game - respawn ball, reset rackets
    /// </summary>
    public void ResetGame()
    {
        // Rackets are now attached to controllers via ControllerRacket, no need to reset
        
        // Reset ball (handled by NetworkedBall)
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    /// <summary>
    /// Handle grip input to spawn ball and start game
    /// </summary>
    private void HandleGameStartInput()
    {
        // Don't process if ball already spawned
        if (spawnedBall != null) return;
        
        bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
        
        if (gripPressed && CurrentPhase == GamePhase.TableSetup)
        {
            CurrentPhase = GamePhase.BallPositioning;
            isInAdjustMode = false;
            Debug.Log("[TableTennisManager] Grip pressed! Spawning ball...");
            
            if (Object.HasStateAuthority || !Object.IsValid)
            {
                GameStarted = true;
                SpawnBall();
            }
        }
    }
    
    /// <summary>
    /// Serve the ball
    /// </summary>
    public void ServeBall()
    {
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    private void OnDestroy()
    {
        // Cleanup local rackets
        foreach (var racket in localRackets)
        {
            if (racket != null)
            {
                Destroy(racket);
            }
        }
    }
}
