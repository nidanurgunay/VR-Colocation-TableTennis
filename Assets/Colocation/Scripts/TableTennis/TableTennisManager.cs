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
    [SerializeField] private NetworkPrefabRef networkedRacketPrefab; // Networked racket for multiplayer
    [SerializeField] private GameObject racketPrefab; // Local prefab template
    
    [Header("Table Placement (relative to anchor)")]
    [SerializeField] private Vector3 tablePositionOffset = Vector3.zero; // Position offset from anchor
    [SerializeField] private float defaultTableHeight = 0.76f; // Standard ping pong table height
    [SerializeField] private float tableXRotationOffset = 0f; // X rotation offset in degrees (set to 180 if table is upside down)
    [SerializeField] private float tableYRotationOffset = 0f; // Y rotation offset in degrees
    
    [Header("Feature Toggles")]
    [SerializeField] private bool enableTableAdjustment = true; // Enable/disable table position/rotation adjustment with controllers
    
    // Game phase tracking
    public enum GamePhase { TableSetup, BallPositioning, Playing }
    [Networked] public NetworkBool GameStarted { get; set; }
    public GamePhase CurrentPhase { get; private set; } = GamePhase.TableSetup;
    
    [Header("Runtime Adjustment Controls")]
    [SerializeField] private float moveSpeed = 2.0f; // Meters per second
    [SerializeField] private float rotateSpeed = 90f; // Degrees per second
    [SerializeField] private bool showAdjustmentInstructions = true;
    
    // Networked table ADJUSTMENTS (relative to anchor-aligned position, not absolute world position)
    // These are offsets that both host and client apply to their own aligned table position
    [Networked] private float NetworkedHeightAdjustment { get; set; } // Y offset from aligned position
    [Networked] private float NetworkedYRotationAdjustment { get; set; } // Y rotation offset from aligned rotation
    [Networked] private float NetworkedXRotation { get; set; } // X rotation for upside-down correction
    
    // Legacy - kept for backward compatibility but using new approach
    [Networked] private Vector3 NetworkedTablePosition { get; set; }
    [Networked] private float NetworkedTableYRotation { get; set; }
    [Networked] private float NetworkedTableXRotation { get; set; } // X rotation for upside-down correction
    [Networked] private float NetworkedFloorOffset { get; set; } // Shared floor level adjustment
    
    // Networked room alignment - syncs room offset between host and client
    [Networked] private Vector3 NetworkedRoomPositionOffset { get; set; }
    [Networked] private float NetworkedRoomRotationOffset { get; set; }
    [Networked] private NetworkBool RoomAlignmentApplied { get; set; }
    
    // Runtime adjustment state
    private GameObject tableRoot;
    private Transform roomParentTransform; // Cached reference to Environment
    private bool isInAdjustMode = false;
    private bool tableInitialized = false; // True after PlaceTableAtAnchor completes
    private bool localRoomAligned = false; // Track if this client has aligned the room
    private bool clientAlignmentComplete = false; // Track if client finished alignment (prevents overwriting)
    private OVRCameraRig cameraRig;
    
    // Store the initial aligned position/rotation (before any adjustments)
    private Vector3 baseAlignedPosition;
    private float baseAlignedXRotation; // Base X rotation (for upside-down detection)
    private float baseAlignedYRotation;
    
    [Header("Table Setup")]
    [SerializeField] private Transform tableTransform;
    [SerializeField] private Vector3 racket1Position = new Vector3(-0.3f, 0.1f, 0f); // On table surface, player 1 side
    [SerializeField] private Vector3 racket2Position = new Vector3(0.3f, 0.1f, 0f);  // On table surface, player 2 side
    [SerializeField] private Vector3 racketRotation = new Vector3(0f, 0f, 0f); // Handle up
    
    [Header("Ball Spawn")]
    [SerializeField] private Vector3 ballSpawnOffset = new Vector3(0f, 0.4f, 0f); // 40cm above anchor/table
    
    // References
    private NetworkedBall spawnedBall;
    private Transform sharedAnchor;
    private Transform secondaryAnchor; // For 2-point alignment after scene transition
    private AlignmentManager alignmentManager;
    private GameObject[] localRackets = new GameObject[2];
    
    /// <summary>
    /// Log detailed anchor and table positions for debugging alignment issues.
    /// Search for [ALIGN_DEBUG] in logs to find these entries.
    /// </summary>
    private void LogAlignmentDebug(string context)
    {
        string role = (Object != null && Object.HasStateAuthority) ? "HOST" : "CLIENT";
        Debug.Log($"[ALIGN_DEBUG] === {context} ({role}) ===");
        
        // Log camera rig position
        var cameraRigObj = FindObjectOfType<OVRCameraRig>();
        if (cameraRigObj != null)
        {
            Debug.Log($"[ALIGN_DEBUG] CameraRig: world={cameraRigObj.transform.position}, rot={cameraRigObj.transform.eulerAngles}");
        }
        
        // Log anchor positions
        if (sharedAnchor != null)
        {
            Debug.Log($"[ALIGN_DEBUG] PrimaryAnchor: world={sharedAnchor.position}, local={sharedAnchor.localPosition}, rot={sharedAnchor.eulerAngles}");
        }
        else
        {
            Debug.Log("[ALIGN_DEBUG] PrimaryAnchor: NULL");
        }
        
        if (secondaryAnchor != null)
        {
            Debug.Log($"[ALIGN_DEBUG] SecondaryAnchor: world={secondaryAnchor.position}, local={secondaryAnchor.localPosition}, rot={secondaryAnchor.eulerAngles}");
        }
        else
        {
            Debug.Log("[ALIGN_DEBUG] SecondaryAnchor: NULL");
        }
        
        // Log table position
        if (tableRoot != null)
        {
            Debug.Log($"[ALIGN_DEBUG] Table: world={tableRoot.transform.position}, local={tableRoot.transform.localPosition}, rot={tableRoot.transform.eulerAngles}");
            if (tableRoot.transform.parent != null)
            {
                Debug.Log($"[ALIGN_DEBUG] Table parent: {tableRoot.transform.parent.name}, parentWorld={tableRoot.transform.parent.position}");
            }
        }
        else
        {
            Debug.Log("[ALIGN_DEBUG] Table: NULL");
        }
        
        // Log room parent
        if (roomParentTransform != null)
        {
            Debug.Log($"[ALIGN_DEBUG] RoomParent: world={roomParentTransform.position}, rot={roomParentTransform.eulerAngles}");
        }
        
        // Log base aligned values
        Debug.Log($"[ALIGN_DEBUG] BaseAligned: pos={baseAlignedPosition}, xRot={baseAlignedXRotation}, yRot={baseAlignedYRotation}");
        Debug.Log($"[ALIGN_DEBUG] NetworkedAdj: height={NetworkedHeightAdjustment}, yRot={NetworkedYRotationAdjustment}, xRot={NetworkedXRotation}");
        Debug.Log($"[ALIGN_DEBUG] Flags: tableInit={tableInitialized}, localRoomAligned={localRoomAligned}, clientAlignComplete={clientAlignmentComplete}");
        Debug.Log($"[ALIGN_DEBUG] === END {context} ===");
    }
    
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
        }
    }
    
    private void Update()
    {
        // Only allow table adjustment during setup phase
        if (CurrentPhase == GamePhase.TableSetup)
        {
            HandleTableAdjustment();
        }
        
        // Check if client needs to apply room alignment from host
        ApplyNetworkedRoomAlignment();
        
        // Check for grip to start game (transition from TableSetup to BallPositioning)
        HandleGameStartInput();
    }
    
    /// <summary>
    /// Handle grip input to transition game phases
    /// </summary>
    private void HandleGameStartInput()
    {
        // Check for grip on either controller
        bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
        
        if (gripPressed && CurrentPhase == GamePhase.TableSetup)
        {
            // Transition to ball positioning
            CurrentPhase = GamePhase.BallPositioning;
            isInAdjustMode = false; // Disable table adjust mode
            Debug.Log("[TableTennisManager] Grip pressed! Spawning ball...");
            
            // Spawn ball (host spawns for networked, anyone can spawn for local)
            if (Object.HasStateAuthority || !Object.IsValid)
            {
                GameStarted = true;
                SpawnBall();
            }
        }
    }
    
    private IEnumerator SpawnBallDelayed()
    {
        yield return new WaitForSeconds(0.5f);
        SpawnBall();
    }
    
    /// <summary>
    /// Called when ball is hit for the first time - transitions from BallPositioning to Playing
    /// </summary>
    public void OnBallHit()
    {
        if (CurrentPhase == GamePhase.BallPositioning)
        {
            CurrentPhase = GamePhase.Playing;
            Debug.Log("[TableTennisManager] Ball hit! Game is now in Playing phase.");
        }
    }
    
    // Fusion calls this every network tick - apply networked table state
    public override void FixedUpdateNetwork()
    {
        // Apply networked table state to local table object
        ApplyNetworkedTableState();
    }
    
    /// <summary>
    /// Apply table placement for client.
    /// IMPORTANT: With the new AlignmentManager, both host and client have anchor center at world origin.
    /// So the client just needs to place the table at origin, same as the host!
    /// </summary>
    private void ApplyNetworkedRoomAlignment()
    {
        // Skip if already initialized or if we're the host
        if (localRoomAligned) return;
        if (Object == null || !Object.IsValid) return;
        if (Object.HasStateAuthority) return; // Host handles its own alignment in PlaceTableAtAnchor
        
        // Check if host has applied room alignment (signal that alignment is ready)
        if (!RoomAlignmentApplied) return;
        
        // Find table if not yet found
        if (tableRoot == null)
        {
            tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        }
        
        if (tableRoot == null)
        {
            Debug.LogWarning("[TableTennisManager] CLIENT: Cannot find table!");
            return;
        }
        
        Debug.Log($"[ALIGN_DEBUG] CLIENT ApplyNetworkedRoomAlignment: Table found at {tableRoot.transform.position}");
        
        // Get anchor positions for rotation calculation
        Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
        Vector3 secondAnchor = AnchorGUIManager_AutoAlignment.SecondAnchorPosition;
        
        Debug.Log($"[ALIGN_DEBUG] CLIENT: firstAnchor={firstAnchor}, secondAnchor={secondAnchor}");
        
        // Calculate table rotation based on anchor direction
        Quaternion targetRotation = Quaternion.identity;
        
        if (firstAnchor.sqrMagnitude > 0.01f && secondAnchor.sqrMagnitude > 0.01f)
        {
            Vector3 direction = (secondAnchor - firstAnchor).normalized;
            direction.y = 0;
            if (direction.sqrMagnitude > 0.001f)
            {
                targetRotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(0, 90f, 0);
            }
        }
        else if (sharedAnchor != null && secondaryAnchor != null)
        {
            Vector3 direction = (secondaryAnchor.position - sharedAnchor.position).normalized;
            direction.y = 0;
            if (direction.sqrMagnitude > 0.001f)
            {
                targetRotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(0, 90f, 0);
            }
        }
        
        // Place table at WORLD ORIGIN (since AlignmentManager makes anchor center = origin)
        float heightOffset = AnchorGUIManager_AutoAlignment.TableHeightOffsetStatic;
        Vector3 tablePosition = new Vector3(0f, heightOffset, 0f);
        
        Debug.Log($"[ALIGN_DEBUG] CLIENT: Placing table at origin. Position={tablePosition}, Rotation={targetRotation.eulerAngles}");
        
        // Apply position and rotation
        tableRoot.transform.position = tablePosition;
        tableRoot.transform.rotation = targetRotation;
        
        // Store the base aligned position for relative adjustments
        baseAlignedPosition = tablePosition;
        baseAlignedXRotation = 0f;
        baseAlignedYRotation = targetRotation.eulerAngles.y;
        
        localRoomAligned = true;
        clientAlignmentComplete = true;
        tableInitialized = true;
        
        LogAlignmentDebug("CLIENT_PLACE_TABLE_AT_ORIGIN");
    }
    
    /// <summary>
    /// Apply the networked table adjustments to the local table object
    /// Uses RELATIVE adjustments so both host and client apply the same offsets to their own aligned position
    /// </summary>
    private void ApplyNetworkedTableState()
    {
        if (tableRoot == null) return;
        
        // Don't apply until table is properly initialized and aligned
        if (!tableInitialized) return;
        
        // Both host and client apply the SAME relative adjustments to their own aligned position
        // This works because both devices aligned the table to the same physical location via anchors
        
        // Apply height adjustment (relative to base aligned position)
        Vector3 adjustedPosition = baseAlignedPosition;
        adjustedPosition.y += NetworkedHeightAdjustment;
        tableRoot.transform.position = adjustedPosition;
        
        // Apply rotation adjustment
        // X rotation: Use the networked value directly (0 means upright, 180 means upside down correction)
        // Y rotation: Apply relative offset to base aligned rotation
        float xRot = NetworkedXRotation; // Direct from network - host sets this to correct upside-down table
        float yRot = baseAlignedYRotation + NetworkedYRotationAdjustment;
        tableRoot.transform.rotation = Quaternion.Euler(xRot, yRot, 0);
    }
    
    /// <summary>
    /// Handle runtime table position/rotation adjustment via controller
    /// Only the state authority (host) can adjust
    /// </summary>
    private void HandleTableAdjustment()
    {
        // Skip if table adjustment is disabled in inspector
        if (!enableTableAdjustment) return;
        
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
    /// Host directly modifies networked table state
    /// Note: X/Z position is set by anchors. Only height and Y rotation can be adjusted.
    /// </summary>
    private void HandleHostAdjustmentInput()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
        // Left stick Y: Adjust table height (relative adjustment)
        if (Mathf.Abs(leftStick.y) > 0.1f)
        {
            float verticalMove = leftStick.y * moveSpeed * Time.deltaTime;
            NetworkedHeightAdjustment += verticalMove;
        }
        
        // Right stick X: Rotate table (relative adjustment)
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            NetworkedYRotationAdjustment += rotation;
        }
        // Right stick Y: Not used (rotation only from right stick X)
    }
    
    /// <summary>
    /// Client sends adjustment requests to host via RPC
    /// Note: X/Z position is set by anchors. Only height and Y rotation can be adjusted.
    /// </summary>
    private void HandleClientAdjustmentInput()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
        // Left stick Y: Request height adjustment
        if (Mathf.Abs(leftStick.y) > 0.1f)
        {
            float verticalMove = leftStick.y * moveSpeed * Time.deltaTime;
            // Send to host to sync (adjustment will be applied via ApplyNetworkedTableState)
            RPC_RequestHeightAdjust(verticalMove);
        }
        
        // Right stick X: Request rotation
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            // Send to host to sync (adjustment will be applied via ApplyNetworkedTableState)
            RPC_RequestRotationAdjust(rotation);
        }
        
        // Right stick Y: Not used (rotation only from right stick X)
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestHeightAdjust(float heightDelta)
    {
        NetworkedHeightAdjustment += heightDelta;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRotationAdjust(float rotationDelta)
    {
        NetworkedYRotationAdjustment += rotationDelta;
    }
    
    // Legacy RPCs - kept for compatibility
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableMove(Vector3 movement)
    {
        NetworkedHeightAdjustment += movement.y;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableRotate(float rotation)
    {
        NetworkedYRotationAdjustment += rotation;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestFloorAdjust(float verticalMove)
    {
        NetworkedHeightAdjustment += verticalMove;
    }
    
    private IEnumerator InitializeGame()
    {
        // Wait for anchor to be available (this also aligns the camera rig via AlignmentManager)
        yield return StartCoroutine(WaitForAnchor());
        
        // Only HOST should place/align the table and room
        // Client will receive alignment via network
        if (Object.HasStateAuthority)
        {
            // Place the table at the anchor position
            PlaceTableAtAnchor();
        }
        else
        {
            // Client: After WaitForAnchor completes, camera rig is already aligned to anchors
            // The table should appear at the correct position relative to the anchors
            Debug.Log("[TableTennisManager] Client: Camera rig aligned to anchors");
            
            // Wait a bit for alignment to fully settle
            yield return new WaitForSeconds(0.5f);
            
            // Find table reference for client
            tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong");
            
            if (tableRoot != null)
            {
                Debug.Log($"[TableTennisManager] Client found table: {tableRoot.name} at position {tableRoot.transform.position}, rotation {tableRoot.transform.eulerAngles}");
                
                // Log detailed anchor and table positions for debugging (BEFORE room alignment)
                LogAlignmentDebug("CLIENT_INIT_BEFORE_ROOM_ALIGN");
                
                // NOTE: The actual room alignment will be done by ApplyNetworkedRoomAlignment() in Update()
                // It will wait for RoomAlignmentApplied from host and then move the room/table
                Debug.Log("[TableTennisManager] Client: Waiting for room alignment from host (will be applied in Update)...");
            }
            else
            {
                Debug.LogWarning("[TableTennisManager] Client: Could not find table!");
            }
        }
        
        // Setup controller-based rackets (replaces old grab system)
        SetupControllerRackets();
        
        // Ball will be spawned when player presses GRIP to start the game
        // (handled in HandleGameStartInput)
    }
    
    /// <summary>
    /// Place the table at the world origin with correct rotation.
    /// IMPORTANT: With the new AlignmentManager, the anchor center is at world origin (0,0,0).
    /// Both host and client have their camera rigs aligned so anchor center = origin.
    /// So we just need to place the table at the origin!
    /// </summary>
    private void PlaceTableAtAnchor()
    {
        // Find the PingPongTable (now a separate root object) or fallback to old names
        tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        
        if (tableRoot == null && tableTransform != null)
        {
            tableRoot = tableTransform.gameObject;
        }
        
        if (tableRoot == null)
        {
            Debug.LogWarning("[TableTennisManager] Could not find table object!");
            return;
        }
        
        // Get anchor info for rotation calculation
        Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
        Vector3 secondAnchor = AnchorGUIManager_AutoAlignment.SecondAnchorPosition;
        
        Debug.Log($"[ALIGN_DEBUG] PlaceTableAtAnchor: first={firstAnchor}, second={secondAnchor}");
        
        // Calculate table rotation based on anchor direction
        // Table long axis should be perpendicular to the line between anchors (so players face each other across the net)
        Quaternion targetRotation = Quaternion.identity;
        
        if (firstAnchor.sqrMagnitude > 0.01f && secondAnchor.sqrMagnitude > 0.01f)
        {
            Vector3 direction = (secondAnchor - firstAnchor).normalized;
            direction.y = 0;
            if (direction.sqrMagnitude > 0.001f)
            {
                // Players stand at the ends of the table (along the anchor line)
                // So table's long axis should be along the direction
                targetRotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(0, 90f, 0);
            }
        }
        else if (sharedAnchor != null && secondaryAnchor != null)
        {
            // Fallback to scene anchors
            Vector3 direction = (secondaryAnchor.position - sharedAnchor.position).normalized;
            direction.y = 0;
            if (direction.sqrMagnitude > 0.001f)
            {
                targetRotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(0, 90f, 0);
            }
        }
        
        // Place table at WORLD ORIGIN (since AlignmentManager makes anchor center = origin)
        // Height can be adjusted with tableHeightOffset or NetworkedHeightAdjustment
        float heightOffset = AnchorGUIManager_AutoAlignment.TableHeightOffsetStatic;
        Vector3 tablePosition = new Vector3(0f, heightOffset, 0f);
        
        Debug.Log($"[ALIGN_DEBUG] PlaceTableAtAnchor: Placing table at origin. Position={tablePosition}, Rotation={targetRotation.eulerAngles}");
        
        // Apply position and rotation
        tableRoot.transform.position = tablePosition;
        tableRoot.transform.rotation = targetRotation;
        
        // Store the base aligned position for relative adjustments
        baseAlignedPosition = tablePosition;
        baseAlignedXRotation = 0f; // Table is upright
        baseAlignedYRotation = targetRotation.eulerAngles.y;
        
        // Host syncs to network
        if (Object.HasStateAuthority)
        {
            NetworkedTablePosition = tablePosition;
            NetworkedTableYRotation = targetRotation.eulerAngles.y;
            NetworkedTableXRotation = 0f;
            NetworkedFloorOffset = 0f;
            
            // Initialize relative adjustment values to zero
            NetworkedHeightAdjustment = 0f;
            NetworkedYRotationAdjustment = 0f;
            NetworkedXRotation = 0f;
            
            // Signal that room alignment is done (for clients waiting)
            RoomAlignmentApplied = true;
        }
        
        localRoomAligned = true;
        tableInitialized = true;
        
        LogAlignmentDebug("HOST_PLACE_TABLE_AT_ORIGIN");
    }
    
    /// <summary>
    /// Setup ControllerRacket component to show racket on controllers
    /// Now spawns as a networked object so other players can see the racket
    /// </summary>
    private void SetupControllerRackets()
    {
        // Each player spawns their own networked ControllerRacket
        // The host spawns for itself, client spawns for itself
        StartCoroutine(SpawnNetworkedRacketController());
    }
    
    private IEnumerator SpawnNetworkedRacketController()
    {
        yield return new WaitForSeconds(0.5f);
        
        // Check if we already have a ControllerRacket for this player
        var existingRacket = FindObjectsOfType<ControllerRacket>();
        foreach (var r in existingRacket)
        {
            if (r.Object != null && r.Object.HasInputAuthority)
            {
                Debug.Log("[TableTennisManager] Already have a ControllerRacket for this player");
                yield break;
            }
        }
        
        // If networkedRacketPrefab is set, spawn it via Fusion
        if (networkedRacketPrefab.IsValid && Runner != null)
        {
            Debug.Log("[TableTennisManager] Spawning networked ControllerRacket...");
            
            var spawnedRacket = Runner.Spawn(
                networkedRacketPrefab, 
                Vector3.zero, 
                Quaternion.identity, 
                Runner.LocalPlayer // Input authority to local player
            );
            
            if (spawnedRacket != null)
            {
                Debug.Log($"[TableTennisManager] Spawned networked ControllerRacket for player {Runner.LocalPlayer}");
            }
        }
        else
        {
            // Fallback: Create local-only ControllerRacket (won't be visible to other players)
            Debug.LogWarning("[TableTennisManager] networkedRacketPrefab not set - using local-only racket (won't be visible to other players)");
            
            var existingManager = GameObject.Find("ControllerRacketManager");
            if (existingManager == null)
            {
                var manager = new GameObject("ControllerRacketManager");
                manager.AddComponent<ControllerRacket>();
                Debug.Log("[TableTennisManager] Created local ControllerRacketManager");
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
                // Log anchor positions BEFORE alignment
                Debug.Log($"[ALIGN_DEBUG] PRE_ALIGN: Primary anchor world={primaryOVRAnchor.transform.position}, local={primaryOVRAnchor.transform.localPosition}, rot={primaryOVRAnchor.transform.eulerAngles}");
                Debug.Log($"[ALIGN_DEBUG] PRE_ALIGN: Secondary anchor world={secondaryOVRAnchor.transform.position}, local={secondaryOVRAnchor.transform.localPosition}, rot={secondaryOVRAnchor.transform.eulerAngles}");
                
                // Both host and client use the same alignment
                // The anchors define the shared coordinate system
                alignmentManager.AlignUserToTwoAnchors(primaryOVRAnchor, secondaryOVRAnchor, 0f);
                Debug.Log($"[TableTennisManager] Applied 2-point alignment");
            }
            else if (primaryOVRAnchor != null)
            {
                Debug.Log($"[ALIGN_DEBUG] PRE_ALIGN: Single anchor world={primaryOVRAnchor.transform.position}, local={primaryOVRAnchor.transform.localPosition}, rot={primaryOVRAnchor.transform.eulerAngles}");
                alignmentManager.AlignUserToAnchor(primaryOVRAnchor);
                Debug.Log("[TableTennisManager] Applied single-point alignment");
            }
            
            // Wait for alignment to complete before placing table
            yield return new WaitForSeconds(1.0f);
            
            // Log anchor positions AFTER alignment
            if (primaryOVRAnchor != null)
            {
                Debug.Log($"[ALIGN_DEBUG] POST_ALIGN: Primary anchor world={primaryOVRAnchor.transform.position}, local={primaryOVRAnchor.transform.localPosition}, rot={primaryOVRAnchor.transform.eulerAngles}");
            }
            if (secondaryOVRAnchor != null)
            {
                Debug.Log($"[ALIGN_DEBUG] POST_ALIGN: Secondary anchor world={secondaryOVRAnchor.transform.position}, local={secondaryOVRAnchor.transform.localPosition}, rot={secondaryOVRAnchor.transform.eulerAngles}");
            }
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
        Vector3 spawnPosition = Vector3.zero;
        
        // PRIORITY 1: Use stored anchor positions (40cm above center of anchors)
        Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
        Vector3 secondAnchor = AnchorGUIManager_AutoAlignment.SecondAnchorPosition;
        
        if (firstAnchor.sqrMagnitude > 0.01f && secondAnchor.sqrMagnitude > 0.01f)
        {
            Vector3 anchorCenter = (firstAnchor + secondAnchor) / 2f;
            spawnPosition = anchorCenter + new Vector3(0, 0.4f, 0); // 40cm above anchor center
            Debug.Log($"[TableTennisManager] Ball spawn using stored anchors: center={anchorCenter}, spawn={spawnPosition}");
        }
        // PRIORITY 2: Use sharedAnchor transform
        else if (sharedAnchor != null)
        {
            spawnPosition = sharedAnchor.position + new Vector3(0, 0.4f, 0); // 40cm above anchor
            Debug.Log($"[TableTennisManager] Ball spawn using sharedAnchor: {sharedAnchor.position}, spawn={spawnPosition}");
        }
        // PRIORITY 3: Use table position
        else if (tableRoot != null)
        {
            spawnPosition = tableRoot.transform.position + ballSpawnOffset;
            Debug.Log($"[TableTennisManager] Ball spawn using tableRoot: {tableRoot.transform.position}");
        }
        // PRIORITY 4: Fallback to camera position
        else
        {
            var cam = Camera.main;
            if (cam != null)
            {
                spawnPosition = cam.transform.position + cam.transform.forward * 0.5f;
                spawnPosition.y = cam.transform.position.y; // Same height as camera
            }
            else
            {
                spawnPosition = new Vector3(0, 1.0f, 0);
            }
            Debug.LogWarning($"[TableTennisManager] No anchor/table reference, spawning ball at {spawnPosition}");
        }
        
        Debug.Log($"[TableTennisManager] SPAWNING BALL at position: {spawnPosition}");
        
        // Always create a visible local ball first (guaranteed to be visible)
        CreateLocalBall(spawnPosition);
        
        // Also try to spawn networked ball if prefab is assigned
        if (ballPrefab != default && Runner != null)
        {
            try
            {
                var ballObj = Runner.Spawn(
                    ballPrefab,
                    spawnPosition,
                    Quaternion.identity,
                    Object.InputAuthority
                );
                
                if (ballObj != null)
                {
                    spawnedBall = ballObj.GetComponent<NetworkedBall>();
                    
                    // IMPORTANT: Unparent the ball so it's not affected by PingPong object transforms
                    ballObj.transform.SetParent(null);
                    
                    // Force world position (in case parent had offset)
                    ballObj.transform.position = spawnPosition;
                    
                    Debug.Log($"[TableTennisManager] Spawned networked ball at {spawnPosition}, unparented to world root");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[TableTennisManager] Networked ball spawn failed: {e.Message}");
            }
        }
    }
    
    /// <summary>
    /// Create a simple visible ball for testing when networked spawn fails
    /// </summary>
    private void CreateLocalBall(Vector3 position)
    {
        Debug.Log($"[TableTennisManager] Creating local ball at WORLD position: {position}");
        
        // Create sphere primitive (at world root, not parented)
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "LocalPingPongBall";
        ball.tag = "Ball"; // Tag for collision detection
        ball.transform.SetParent(null); // Ensure it's at world root
        ball.transform.position = position; // Set WORLD position
        ball.transform.localScale = Vector3.one * 0.08f; // 8cm diameter for visibility
        
        // Set material to bright orange for visibility - use URP compatible shader
        var renderer = ball.GetComponent<Renderer>();
        if (renderer != null)
        {
            // Try different shaders in order of preference for URP
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Standard");
            
            Material mat;
            if (shader != null)
            {
                mat = new Material(shader);
                mat.color = new Color(1f, 0.5f, 0f); // Orange color
                
                // Set base color for URP shaders
                if (mat.HasProperty("_BaseColor"))
                {
                    mat.SetColor("_BaseColor", new Color(1f, 0.5f, 0f));
                }
            }
            else
            {
                // Fallback: use the primitive's default material and just change color
                mat = renderer.material;
                mat.color = new Color(1f, 0.5f, 0f);
            }
            
            renderer.material = mat;
        }
        
        // Add rigidbody - non-kinematic for collision detection
        var rb = ball.AddComponent<Rigidbody>();
        rb.mass = 0.0027f;
        rb.drag = 0.1f;
        rb.isKinematic = false; // Non-kinematic for collision detection
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeAll; // Freeze until hit
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        
        // Add local ball handler for collision detection
        var ballHandler = ball.AddComponent<LocalBallHandler>();
        ballHandler.Initialize(rb);
        
        Debug.Log($"[TableTennisManager] Created local ball at {ball.transform.position} - WORLD ROOT, IN POSITIONING MODE");
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
    /// Serve the ball
    /// </summary>
    public void ServeBall()
    {
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    /// <summary>
    /// Find the room parent object that contains the table and room environment
    /// </summary>
    private Transform FindRoomParent(Transform table)
    {
        // First try to find Environment directly in scene root
        GameObject envObj = GameObject.Find("Environment");
        if (envObj != null)
        {
            Debug.Log($"[TableTennisManager] Found Environment directly: {envObj.name}");
            return envObj.transform;
        }
        
        // Look for common room parent names
        string[] roomNames = { "Environment", "Room", "Scene", "World", "Level", "pingpong", "PingPong", "TableTennisRoom" };
        
        // Check if table's parent or grandparent is the room
        Transform current = table.parent;
        while (current != null)
        {
            string nameLower = current.name.ToLower();
            foreach (string roomName in roomNames)
            {
                if (nameLower.Contains(roomName.ToLower()))
                {
                    Debug.Log($"[TableTennisManager] Found room parent: {current.name}");
                    return current;
                }
            }
            current = current.parent;
        }
        
        // If table has a parent, use that as the room
        if (table.parent != null)
        {
            Debug.Log($"[TableTennisManager] Using table's parent as room: {table.parent.name}");
            return table.parent;
        }
        
        return null;
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
