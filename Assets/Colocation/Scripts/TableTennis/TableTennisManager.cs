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
    
    [Header("Runtime Adjustment Controls")]
    [SerializeField] private float moveSpeed = 2.0f; // Meters per second
    [SerializeField] private float rotateSpeed = 90f; // Degrees per second
    [SerializeField] private bool showAdjustmentInstructions = true;
    
    // Networked table position/rotation for syncing across players
    [Networked] private Vector3 NetworkedTablePosition { get; set; }
    [Networked] private float NetworkedTableYRotation { get; set; }
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
    private OVRCameraRig cameraRig;
    
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
        HandleTableAdjustment();
        
        // Check if client needs to apply room alignment from host
        ApplyNetworkedRoomAlignment();
    }
    
    // Fusion calls this every network tick - apply networked table state
    public override void FixedUpdateNetwork()
    {
        // Apply networked table state to local table object
        ApplyNetworkedTableState();
    }
    
    /// <summary>
    /// Apply room alignment from network (for clients)
    /// </summary>
    private void ApplyNetworkedRoomAlignment()
    {
        // Skip if already aligned locally or if we're the host
        if (localRoomAligned) return;
        if (Object == null || !Object.IsValid) return;
        if (Object.HasStateAuthority) return; // Host doesn't need to apply - it already aligned
        
        // Check if host has applied room alignment
        if (!RoomAlignmentApplied) return;
        
        // Find room parent if not cached
        if (roomParentTransform == null)
        {
            GameObject envObj = GameObject.Find("Environment");
            if (envObj != null)
            {
                roomParentTransform = envObj.transform;
            }
            else if (tableRoot != null)
            {
                roomParentTransform = FindRoomParent(tableRoot.transform);
            }
        }
        
        if (roomParentTransform == null) 
        {
            Debug.Log("[TableTennisManager] Client: Room parent not found yet");
            return;
        }
        
        // Find table if not cached
        if (tableRoot == null)
        {
            tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong");
        }
        
        if (tableRoot == null)
        {
            Debug.Log("[TableTennisManager] Client: Table not found yet");
            return;
        }
        
        Debug.Log($"[TableTennisManager] Client applying room alignment from host...");
        Debug.Log($"[TableTennisManager] Host sent: roomPos={NetworkedRoomPositionOffset}, rotOffset={NetworkedRoomRotationOffset}");
        Debug.Log($"[TableTennisManager] Current: roomPos={roomParentTransform.position}, tablePos={tableRoot.transform.position}");
        
        // The host sent the final room position and rotation offset
        // We need to:
        // 1. Set the room to the same final position as the host
        // 2. Apply the same rotation
        
        // First, move the room to match host's final position
        roomParentTransform.position = NetworkedRoomPositionOffset;
        
        // Also set table local position to zero (same as host did)
        tableRoot.transform.localPosition = Vector3.zero;
        
        Debug.Log($"[TableTennisManager] Client room aligned. Room at: {roomParentTransform.position}, Table at: {tableRoot.transform.position}");
        
        localRoomAligned = true;
    }
    
    /// <summary>
    /// Apply the networked table position/rotation to the local table object
    /// </summary>
    private void ApplyNetworkedTableState()
    {
        if (tableRoot == null) return;
        
        // Don't apply until table is properly initialized
        // This prevents overwriting the position set by AnchorGUIManager
        if (!tableInitialized) return;
        
        // Don't apply if networked position hasn't been set yet (still at origin)
        if (NetworkedTablePosition.sqrMagnitude < 0.01f) return;
        
        // Apply networked position (Y includes floor offset)
        tableRoot.transform.position = new Vector3(
            NetworkedTablePosition.x,
            NetworkedTablePosition.y + NetworkedFloorOffset,
            NetworkedTablePosition.z
        );
        
        // Apply networked rotation (Y from network + X offset for upside-down correction)
        tableRoot.transform.rotation = Quaternion.Euler(tableXRotationOffset, NetworkedTableYRotation, 0);
        
        // Apply floor offset to camera rig so player sees correct floor level
        if (cameraRig != null && NetworkedFloorOffset != 0)
        {
            // Store the original floor offset from when we joined
            // This is a simplified approach - both players adjust together
        }
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
        
        // Left stick Y: Adjust table height (up/down)
        if (Mathf.Abs(leftStick.y) > 0.1f)
        {
            float verticalMove = leftStick.y * moveSpeed * Time.deltaTime;
            Vector3 pos = NetworkedTablePosition;
            pos.y += verticalMove;
            NetworkedTablePosition = pos;
        }
        
        // Right stick X: Rotate table (Y axis only, no Y position change)
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            NetworkedTableYRotation += rotation;
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
            Vector3 movement = new Vector3(0, leftStick.y * moveSpeed * Time.deltaTime, 0);
            RPC_RequestTableMove(movement);
        }
        
        // Right stick X: Request rotation
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            RPC_RequestTableRotate(rotation);
        }
        
        // Right stick Y: Not used (rotation only from right stick X)
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableMove(Vector3 movement)
    {
        movement = Quaternion.Euler(0, NetworkedTableYRotation, 0) * movement;
        NetworkedTablePosition += movement;
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
        
        // Only HOST should place/align the table and room
        // Client will receive alignment via network
        if (Object.HasStateAuthority)
        {
            // Place the table at the anchor position
            PlaceTableAtAnchor();
        }
        else
        {
            // Client: Wait for room alignment from host
            Debug.Log("[TableTennisManager] Client waiting for room alignment from host...");
            yield return new WaitForSeconds(0.5f);
            
            // Find table reference for client
            tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong");
            
            if (tableRoot != null)
            {
                Debug.Log($"[TableTennisManager] Client found table: {tableRoot.name}");
            }
        }
        
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
    /// Place the table/pingpong at the anchor position with adjustable offset and Y rotation
    /// If table was already placed by AnchorGUIManager (via AlignTableToAnchors), use its current position
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
        
        // PRIORITY 1: Check if anchor positions were stored (recalculate and move room here in TableTennis scene)
        Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
        Vector3 secondAnchor = AnchorGUIManager_AutoAlignment.SecondAnchorPosition;
        
        Debug.Log($"[TableTennisManager] Static anchor positions: first={firstAnchor}, second={secondAnchor}");
        Debug.Log($"[TableTennisManager] Static first magnitude={firstAnchor.sqrMagnitude}, second magnitude={secondAnchor.sqrMagnitude}");
        Debug.Log($"[TableTennisManager] TableWasAligned={AnchorGUIManager_AutoAlignment.TableWasAligned}, AlignedPos={AnchorGUIManager_AutoAlignment.AlignedTablePosition}");
        Debug.Log($"[TableTennisManager] Anchor UUIDs: first={AnchorGUIManager_AutoAlignment.FirstAnchorUuid}, second={AnchorGUIManager_AutoAlignment.SecondAnchorUuid}");
        Debug.Log($"[TableTennisManager] HasStateAuthority={Object.HasStateAuthority}");
        
        // FALLBACK: If static variables are empty, use the localized anchors in scene
        if (firstAnchor.sqrMagnitude < 0.01f || secondAnchor.sqrMagnitude < 0.01f)
        {
            Debug.Log("[TableTennisManager] Static anchor positions empty, trying to use scene anchors...");
            
            // Use sharedAnchor and secondaryAnchor if available
            if (sharedAnchor != null && secondaryAnchor != null)
            {
                firstAnchor = sharedAnchor.position;
                secondAnchor = secondaryAnchor.position;
                Debug.Log($"[TableTennisManager] FALLBACK: Using scene anchors: first={firstAnchor}, second={secondAnchor}");
                Debug.Log($"[TableTennisManager] WARNING: Scene anchor order may not match placement order!");
            }
            else
            {
                Debug.Log($"[TableTennisManager] Scene anchors: sharedAnchor={(sharedAnchor != null ? sharedAnchor.position.ToString() : "NULL")}, secondaryAnchor={(secondaryAnchor != null ? secondaryAnchor.position.ToString() : "NULL")}");
            }
        }
        else
        {
            Debug.Log($"[TableTennisManager] Using STATIC anchor positions (correct order from AnchorGUI)");
        }
        
        if (firstAnchor.sqrMagnitude > 0.01f && secondAnchor.sqrMagnitude > 0.01f)
        {
            // Recalculate target center from stored anchor positions
            Vector3 targetCenter = (firstAnchor + secondAnchor) / 2f;
            float heightOffset = AnchorGUIManager_AutoAlignment.TableHeightOffsetStatic;
            targetCenter.y = (firstAnchor.y + secondAnchor.y) / 2f + heightOffset;
            
            // Calculate target rotation
            // Table long axis should be ALONG the line between anchors (players at ends)
            Vector3 direction = (secondAnchor - firstAnchor).normalized;
            direction.y = 0;
            Quaternion targetRotation = Quaternion.identity;
            if (direction.sqrMagnitude > 0.001f)
            {
                // LookRotation points Z along direction, but table's long axis is typically Z
                // Add 90 degrees if table model has long axis on X instead
                targetRotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(0, 90f, 0);
            }
            
            Debug.Log($"[TableTennisManager] Recalculating from anchors: first={firstAnchor}, second={secondAnchor}, target={targetCenter}, rotation={targetRotation.eulerAngles}");
            
            // Find room parent (Environment)
            Transform roomParent = FindRoomParent(tableRoot.transform);
            roomParentTransform = roomParent; // Cache for later use
            
            // Check if table is parented to an anchor (not to Environment)
            bool tableIsChildOfRoom = roomParent != null && tableRoot.transform.IsChildOf(roomParent);
            Debug.Log($"[TableTennisManager] Table: {tableRoot.name}, Parent: {(tableRoot.transform.parent != null ? tableRoot.transform.parent.name : "NONE")}");
            Debug.Log($"[TableTennisManager] Is table child of Environment? {tableIsChildOfRoom}");
            
            // If table is parented to anchor, unparent it and reparent to Environment
            if (!tableIsChildOfRoom && tableRoot.transform.parent != null)
            {
                Debug.Log($"[TableTennisManager] Unparenting table from {tableRoot.transform.parent.name}");
                Vector3 worldPos = tableRoot.transform.position;
                Quaternion worldRot = tableRoot.transform.rotation;
                
                // Reparent to Environment if available
                if (roomParent != null)
                {
                    tableRoot.transform.SetParent(roomParent);
                    Debug.Log($"[TableTennisManager] Reparented table to {roomParent.name}");
                }
                else
                {
                    tableRoot.transform.SetParent(null);
                }
                
                // Restore world position/rotation
                tableRoot.transform.position = worldPos;
                tableRoot.transform.rotation = worldRot;
            }
            
            if (roomParent != null)
            {
                Debug.Log($"[TableTennisManager] RoomParent: {roomParent.name}, RoomPos: {roomParent.position}");
                
                // Calculate offsets
                Vector3 currentTablePosition = tableRoot.transform.position;
                Debug.Log($"[TableTennisManager] Table current position (before move): {currentTablePosition}");
                Vector3 positionOffset = targetCenter - currentTablePosition;
                
                float currentYRot = tableRoot.transform.eulerAngles.y;
                float targetYRotation = targetRotation.eulerAngles.y;
                float rotationOffset = targetYRotation - currentYRot;
                
                Debug.Log($"[TableTennisManager] Moving room: offset={positionOffset}, rotOffset={rotationOffset}°");
                
                // First rotate the room around the table's position
                if (Mathf.Abs(rotationOffset) > 0.1f)
                {
                    roomParent.RotateAround(currentTablePosition, Vector3.up, rotationOffset);
                    Debug.Log($"[TableTennisManager] After rotation, table at: {tableRoot.transform.position}");
                }
                
                // Then move the room so table ends up at target
                Vector3 roomPosBefore = roomParent.position;
                roomParent.position += positionOffset;
                Debug.Log($"[TableTennisManager] Room moved from {roomPosBefore} to {roomParent.position}");
                Debug.Log($"[TableTennisManager] After room move, table at: {tableRoot.transform.position}, expected: {targetCenter}");
                
                // Now center Environment's origin at the table position
                // Store table's current world position
                Vector3 tableWorldPos = tableRoot.transform.position;
                
                // Calculate offset to move room origin to table position
                Vector3 tableLocalPos = tableRoot.transform.localPosition;
                
                // Move room so its origin is at table's world position
                roomParent.position = tableWorldPos;
                
                // Adjust table's local position to compensate (keep it at world origin of room)
                tableRoot.transform.localPosition = Vector3.zero;
                
                Debug.Log($"[TableTennisManager] Environment origin centered at table. Room at: {roomParent.position}, Table local: {tableRoot.transform.localPosition}");
                
                // Host syncs room offset to network so clients can apply same alignment
                if (Object.HasStateAuthority)
                {
                    NetworkedRoomPositionOffset = roomParent.position; // Store final room position
                    NetworkedRoomRotationOffset = rotationOffset;
                    RoomAlignmentApplied = true;
                    Debug.Log($"[TableTennisManager] Room final position synced: {roomParent.position}, rot={rotationOffset}");
                }
                
                localRoomAligned = true;
                Debug.Log($"[TableTennisManager] Alignment complete. Table at: {tableRoot.transform.position}, Room origin at: {roomParent.position}");
            }
            else
            {
                // Fallback: move just the table
                tableRoot.transform.position = targetCenter;
                tableRoot.transform.rotation = targetRotation;
            }
            
            // Host syncs to network
            if (Object.HasStateAuthority)
            {
                NetworkedTablePosition = tableRoot.transform.position;
                NetworkedTableYRotation = tableRoot.transform.eulerAngles.y;
                NetworkedFloorOffset = 0f;
            }
            
            tableInitialized = true;
            Debug.Log($"[TableTennisManager] Table positioned at: {tableRoot.transform.position}");
            return;
        }
        
        // PRIORITY 2: Check if AnchorGUIManager aligned the table (static flag - fallback)
        if (AnchorGUIManager_AutoAlignment.TableWasAligned)
        {
            Vector3 alignedPos = AnchorGUIManager_AutoAlignment.AlignedTablePosition;
            Quaternion alignedRot = AnchorGUIManager_AutoAlignment.AlignedTableRotation;
            
            Debug.Log($"[TableTennisManager] Using AnchorGUI aligned position: {alignedPos}");
            
            // Apply the aligned position
            tableRoot.transform.position = alignedPos;
            tableRoot.transform.rotation = alignedRot;
            
            // Host syncs to network
            if (Object.HasStateAuthority)
            {
                NetworkedTablePosition = alignedPos;
                NetworkedTableYRotation = alignedRot.eulerAngles.y;
                NetworkedFloorOffset = 0f;
            }
            
            tableInitialized = true;
            return;
        }
        
        // PRIORITY 3: Check if table was already positioned (not at origin)
        Vector3 currentTablePos = tableRoot.transform.position;
        bool tableAlreadyPlaced = currentTablePos.sqrMagnitude > 0.1f; // Not at origin
        
        if (tableAlreadyPlaced)
        {
            Debug.Log($"[TableTennisManager] Table already placed at {currentTablePos}, using existing position");
            
            // Host syncs the current position to network
            if (Object.HasStateAuthority)
            {
                NetworkedTablePosition = currentTablePos;
                NetworkedTableYRotation = tableRoot.transform.eulerAngles.y;
                NetworkedFloorOffset = 0f;
            }
            
            tableInitialized = true;
            return;
        }
        
        // Fallback: Place at anchor if table wasn't pre-positioned
        if (sharedAnchor == null)
        {
            Debug.LogWarning("[TableTennisManager] No anchor to place table at!");
            return;
        }
        
        // Calculate position: X/Z from anchor, Y = Standard Height (since anchor is floor)
        Vector3 rotatedOffset = Quaternion.Euler(0, tableYRotationOffset, 0) * tablePositionOffset;
        Vector3 anchorPos = sharedAnchor.position + rotatedOffset;
        
        // Set table at standard height relative to WORLD FLOOR (ignoring anchor Y)
        // This is safer if anchor was placed in the air (held in hand)
        Vector3 tablePos = new Vector3(anchorPos.x, defaultTableHeight, anchorPos.z);
        
        // Host initializes networked values
        if (Object.HasStateAuthority)
        {
            NetworkedTablePosition = tablePos;
            NetworkedTableYRotation = tableYRotationOffset;
            NetworkedFloorOffset = 0f;
        }
        
        // Apply initial position
        tableRoot.transform.position = tablePos;
        tableRoot.transform.rotation = Quaternion.Euler(0, tableYRotationOffset, 0); // Keep table upright
        
        tableInitialized = true;
        Debug.Log($"[TableTennisManager] Table placed at {tableRoot.transform.position}");
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
