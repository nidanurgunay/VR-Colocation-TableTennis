using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Manages the table tennis game setup and spawns the networked ball.
/// SIMPLIFIED VERSION based on alignment_changes branch approach.
/// Key principle: Table rotation is anchor-relative, not network-synced rotation values.
/// </summary>
public class TableTennisManager : NetworkBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private NetworkPrefabRef ballPrefab;
    [SerializeField] private NetworkPrefabRef networkedRacketPrefab;
    [SerializeField] private GameObject racketPrefab;
    
    [Header("Table Placement (relative to anchor)")]
    [SerializeField] private Vector3 tablePositionOffset = Vector3.zero;
    [SerializeField] private float defaultTableHeight = 0.76f; // Standard ping pong table height
    [SerializeField] private float tableXRotationOffset = 180f; // X rotation offset - set to 180 if table appears upside down
    [SerializeField] private float tableYRotationOffset = 0f; // Y rotation offset
    
    [Header("Runtime Adjustment Controls")]
    [SerializeField] private float moveSpeed = 2.0f;
    [SerializeField] private float rotateSpeed = 90f;
    [SerializeField] private bool showAdjustmentInstructions = true;
    [SerializeField] private bool enableTableAdjustment = true;
    
    // Networked table state - SIMPLE approach from alignment_changes branch
    [Networked] private Vector3 NetworkedTablePosition { get; set; }
    [Networked] private float NetworkedTableYRotation { get; set; }
    [Networked] private float NetworkedFloorOffset { get; set; }
    
    // Game phase tracking
    public enum GamePhase { TableSetup, BallPositioning, Playing }
    [Networked] public NetworkBool GameStarted { get; set; }
    public GamePhase CurrentPhase { get; private set; } = GamePhase.TableSetup;
    
    // Runtime state
    private GameObject tableRoot;
    private bool isInAdjustMode = false;
    private bool tableInitialized = false;
    private Quaternion baseAnchorRotation = Quaternion.identity; // Store anchor rotation at placement time
    private OVRCameraRig cameraRig;
    
    [Header("Table Setup")]
    [SerializeField] private Transform tableTransform;
    [SerializeField] private Vector3 racket1Position = new Vector3(-0.3f, 0.1f, 0f);
    [SerializeField] private Vector3 racket2Position = new Vector3(0.3f, 0.1f, 0f);
    [SerializeField] private Vector3 racketRotation = new Vector3(0f, 0f, 0f);
    
    [Header("Ball Spawn")]
    [SerializeField] private Vector3 ballSpawnOffset = new Vector3(0f, 0.4f, 0f); // 40cm above anchor
    
    // References
    private NetworkedBall spawnedBall;
    private Transform sharedAnchor;
    private Transform secondaryAnchor;
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
        if (enableTableAdjustment)
        {
            HandleTableAdjustment();
        }
        HandleGameStartInput();
    }
    
    public override void FixedUpdateNetwork()
    {
        ApplyNetworkedTableState();
    }
    
    /// <summary>
    /// SIMPLIFIED: Apply networked table state - position + floor offset, anchor-relative rotation
    /// </summary>
    private void ApplyNetworkedTableState()
    {
        if (tableRoot == null) return;
        if (!tableInitialized) return; // Don't apply until PlaceTableAtAnchor has run
        
        // Apply networked position (Y includes floor offset)
        tableRoot.transform.position = new Vector3(
            NetworkedTablePosition.x,
            NetworkedTablePosition.y + NetworkedFloorOffset,
            NetworkedTablePosition.z
        );
        
        // Apply rotation - anchor-relative, with X offset and networked Y adjustment
        // baseAnchorRotation is captured at PlaceTableAtAnchor time
        tableRoot.transform.rotation = baseAnchorRotation * Quaternion.Euler(tableXRotationOffset, NetworkedTableYRotation, 0);
    }
    
    /// <summary>
    /// Handle grip input to spawn ball and start game
    /// </summary>
    private void HandleGameStartInput()
    {
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
    /// Handle runtime table adjustment via controller
    /// </summary>
    private void HandleTableAdjustment()
    {
        if (tableRoot == null)
        {
            tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable")
                        ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
            if (tableRoot == null) return;
        }
        
        if (cameraRig == null)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
        }
        
        // Toggle adjust mode with A button
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch) ||
            OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch))
        {
            isInAdjustMode = !isInAdjustMode;
            Debug.Log($"[TableTennisManager] Adjust mode: {(isInAdjustMode ? "ON" : "OFF")}");
        }
        
        if (!isInAdjustMode) return;
        
        if (!Object.HasStateAuthority)
        {
            HandleClientAdjustmentInput();
            return;
        }
        
        HandleHostAdjustmentInput();
    }
    
    private void HandleHostAdjustmentInput()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
        // Move table with left thumbstick (X/Z movement)
        if (leftStick.magnitude > 0.1f)
        {
            Vector3 movement = new Vector3(leftStick.x, 0, leftStick.y) * moveSpeed * Time.deltaTime;
            movement = Quaternion.Euler(0, NetworkedTableYRotation, 0) * movement;
            NetworkedTablePosition += movement;
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
    
    private void HandleClientAdjustmentInput()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
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
        yield return StartCoroutine(WaitForAnchor());
        
        PlaceTableAtAnchor();
        
        SetupControllerRackets();
    }
    
    private IEnumerator WaitForAnchor()
    {
        int attempts = 0;
        OVRSpatialAnchor primaryOVRAnchor = null;
        OVRSpatialAnchor secondaryOVRAnchor = null;
        
        // Find or create AlignmentManager
        alignmentManager = FindObjectOfType<AlignmentManager>();
        if (alignmentManager == null)
        {
            var alignObj = new GameObject("AlignmentManager");
            alignmentManager = alignObj.AddComponent<AlignmentManager>();
            Debug.Log("[TableTennisManager] Created AlignmentManager");
        }
        
        // Try getting from AnchorGUIManager first
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

        // Search for preserved anchors
        while ((sharedAnchor == null || secondaryAnchor == null) && attempts < 50)
        {
            var anchors = FindObjectsOfType<OVRSpatialAnchor>(true);
            
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
            if (tableTransform != null)
            {
                sharedAnchor = tableTransform;
                Debug.Log("[TableTennisManager] Using table as fallback anchor reference");
            }
        }
        else
        {
            // Check if AnchorGUIManager already completed alignment
            bool alreadyAligned = AnchorGUIManager_AutoAlignment.AlignmentCompletedStatic;
            
            if (alreadyAligned)
            {
                Debug.Log("[TableTennisManager] Alignment already completed by AnchorGUIManager, skipping re-alignment");
            }
            else
            {
                // CRITICAL: Re-align camera rig to preserved anchors
                Debug.Log("[TableTennisManager] Re-aligning camera rig to preserved anchors...");
                
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
                
                // Wait for alignment to complete (stabilization + iterations)
                yield return new WaitForSeconds(2.0f);
            }
        }
    }
    
    /// <summary>
    /// SIMPLIFIED: Place table at anchor position with anchor-relative rotation
    /// </summary>
    private void PlaceTableAtAnchor()
    {
        if (sharedAnchor == null)
        {
            Debug.LogWarning("[TableTennisManager] No anchor to place table at!");
            return;
        }
        
        tableRoot = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        
        if (tableRoot == null && tableTransform != null)
        {
            tableRoot = tableTransform.gameObject;
        }
        
        if (tableRoot != null)
        {
            // Calculate position offset from anchor
            Vector3 localOffset = Quaternion.Euler(0, tableYRotationOffset, 0) * tablePositionOffset;
            
            // Table position: anchor position + offset, at proper height
            Vector3 tablePos = sharedAnchor.position + localOffset;
            tablePos.y = sharedAnchor.position.y + defaultTableHeight;

            // CRITICAL: Store anchor rotation for use in ApplyNetworkedTableState
            // This ensures table rotation is always relative to anchor on this device
            baseAnchorRotation = sharedAnchor.rotation;
            Quaternion tableRotation = baseAnchorRotation * Quaternion.Euler(tableXRotationOffset, tableYRotationOffset, 0);

            // Host initializes networked values
            if (Object.HasStateAuthority)
            {
                NetworkedTablePosition = tablePos;
                NetworkedTableYRotation = tableYRotationOffset;
                NetworkedFloorOffset = 0f;
            }

            // Apply position and anchor-relative rotation
            tableRoot.transform.position = tablePos;
            tableRoot.transform.rotation = tableRotation;
            
            // Mark as initialized so ApplyNetworkedTableState can run
            tableInitialized = true;

            Debug.Log($"[TableTennisManager] Table placed at {tableRoot.transform.position}, " +
                      $"rotation: {tableRotation.eulerAngles} (anchor rot: {baseAnchorRotation.eulerAngles})");
        }
        else
        {
            Debug.LogWarning("[TableTennisManager] Could not find table object to place at anchor");
        }
    }
    
    private void SetupControllerRackets()
    {
        var existingManager = GameObject.Find("ControllerRacketManager");
        if (existingManager == null)
        {
            var manager = new GameObject("ControllerRacketManager");
            var component = manager.AddComponent(System.Type.GetType("ControllerRacket"));
            if (component != null)
            {
                Debug.Log("[TableTennisManager] Created ControllerRacketManager - press B/Y to show racket");
            }
            else
            {
                manager.AddComponent<ControllerRacket>();
                Debug.Log("[TableTennisManager] Created ControllerRacketManager (direct)");
            }
        }
    }
    
    private void SpawnBall()
    {
        Vector3 spawnPosition = Vector3.zero;
        
        // Use stored anchor positions if available
        Vector3 firstAnchor = AnchorGUIManager_AutoAlignment.FirstAnchorPosition;
        Vector3 secondAnchor = AnchorGUIManager_AutoAlignment.SecondAnchorPosition;
        
        if (firstAnchor.sqrMagnitude > 0.01f && secondAnchor.sqrMagnitude > 0.01f)
        {
            Vector3 anchorCenter = (firstAnchor + secondAnchor) / 2f;
            spawnPosition = anchorCenter + new Vector3(0, 0.4f, 0);
            Debug.Log($"[TableTennisManager] Ball spawn using anchors: {spawnPosition}");
        }
        else if (sharedAnchor != null)
        {
            spawnPosition = sharedAnchor.position + new Vector3(0, 0.4f, 0);
            Debug.Log($"[TableTennisManager] Ball spawn using sharedAnchor: {spawnPosition}");
        }
        else if (tableRoot != null)
        {
            spawnPosition = tableRoot.transform.position + ballSpawnOffset;
            Debug.Log($"[TableTennisManager] Ball spawn using tableRoot: {spawnPosition}");
        }
        else
        {
            var cam = Camera.main;
            if (cam != null)
            {
                spawnPosition = cam.transform.position + cam.transform.forward * 0.5f;
                spawnPosition.y = cam.transform.position.y;
            }
            else
            {
                spawnPosition = new Vector3(0, 1.0f, 0);
            }
            Debug.LogWarning($"[TableTennisManager] No anchor/table, spawning ball at {spawnPosition}");
        }
        
        Debug.Log($"[TableTennisManager] SPAWNING BALL at: {spawnPosition}");
        
        // Create local ball for immediate visibility
        CreateLocalBall(spawnPosition);
        
        // Spawn networked ball
        if (ballPrefab != default && Runner != null)
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
                Debug.Log($"[TableTennisManager] Spawned networked ball at {spawnPosition}");
            }
        }
    }
    
    private void CreateLocalBall(Vector3 position)
    {
        var localBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        localBall.name = "LocalBall_Fallback";
        localBall.transform.position = position;
        localBall.transform.localScale = Vector3.one * 0.04f; // 4cm diameter
        
        var renderer = localBall.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            renderer.material.color = Color.white;
        }
        
        var rb = localBall.AddComponent<Rigidbody>();
        rb.mass = 0.0027f; // Standard ping pong ball mass
        rb.drag = 0.1f;
        rb.angularDrag = 0.05f;
        rb.useGravity = true;
        
        var collider = localBall.GetComponent<SphereCollider>();
        if (collider != null)
        {
            var physicMat = new PhysicMaterial("BallPhysics");
            physicMat.bounciness = 0.9f;
            physicMat.bounceCombine = PhysicMaterialCombine.Maximum;
            collider.material = physicMat;
        }
        
        Debug.Log($"[TableTennisManager] Created local fallback ball at {position}");
    }
    
    public void OnBallHit()
    {
        if (CurrentPhase == GamePhase.BallPositioning)
        {
            CurrentPhase = GamePhase.Playing;
            Debug.Log("[TableTennisManager] Ball hit! Game is now in Playing phase.");
        }
    }
    
    public void ResetGame()
    {
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    public void ServeBall()
    {
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    private void OnDestroy()
    {
        foreach (var racket in localRackets)
        {
            if (racket != null)
            {
                Destroy(racket);
            }
        }
    }
}
