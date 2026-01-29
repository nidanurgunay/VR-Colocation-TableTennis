using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;


#if FUSION2
using Fusion;
#endif

/// Manages passthrough table tennis game - table spawning, adjustment, rackets, and ball.
/// Works with AnchorGUIManager_AutoAlignment for anchor references.
public class PassthroughGameManager : NetworkBehaviour
{
    private const string LOG_TAG = "[PassthroughGameManager]";
    
    [Header("Prefabs")]
    [SerializeField] private TableTennisConfig sharedConfig;
    [SerializeField] private GameObject tablePrefab;
    [SerializeField] private GameObject racketPrefab;
    [SerializeField] private NetworkPrefabRef ballPrefab;
    
    [Header("Table Settings")]
    [SerializeField] private float tableRotateSpeed = 90f;
    [SerializeField] private float tableMoveSpeed = 1f;
    [SerializeField] private float tableXRotationOffset = 180f;
    [SerializeField] private float tableYRotationOffset = 90f;
    [SerializeField] private float defaultTableHeight = 0.8f; // AR table height for comfortable viewing/playing
    
    [Header("Debug Settings")]
    [Tooltip("Skip anchor alignment for quick testing. Table will spawn in front of player.")]
    [SerializeField] private bool skipAlignmentForDebug = false;
    [SerializeField] private Vector3 debugTablePosition = new Vector3(0, 0.76f, 2f);
    
    // Prefab accessors
    private GameObject TablePrefab => sharedConfig != null ? sharedConfig.TablePrefab : tablePrefab;
    private GameObject RacketPrefab => sharedConfig != null ? sharedConfig.RacketPrefab : racketPrefab;
    private NetworkPrefabRef BallPrefab => sharedConfig != null && sharedConfig.BallPrefab != default ? sharedConfig.BallPrefab : ballPrefab;
    private float TableRotateSpeed => sharedConfig != null ? sharedConfig.tableRotateSpeed : tableRotateSpeed;
    private float TableMoveSpeed => sharedConfig != null ? sharedConfig.tableMoveSpeed : tableMoveSpeed;

    // State (using shared GamePhase enum from GamePhaseDefinitions.cs)
    private bool isActive = false;
    private bool isGameMenuOpen = false;
    private bool racketsVisible = true;
    private bool ballNeedsRespawn = false;
    
    // // Racket offset/rotation settings (matching VR scene's ControllerRacket for consistency)
    // private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f);
    // private Vector3 racketRotation = new Vector3(-51f, 240f, 43f);
    // private float racketScale = 10f;
    
    // References
    private GameObject spawnedTable;
    private GameObject spawnedBall;
    private GameObject leftRacket;
    private GameObject rightRacket;
    private GameObject runtimeMenuPanel;
    private GameObject gameUIPanel;
    private TextMesh scoreText;
    private TextMesh roleText;          // Shows Client/Host
    private TextMesh phaseText;         // Shows current game phase
    private TextMesh authorityText;     // Shows active authority (Host Only / Your Turn / Opponent's Turn)
    private TextMesh controlsText;      // Shows controller instructions
    private OVRSpatialAnchor _localizedAnchor;
    private AnchorGUIManager_AutoAlignment mainManager;
    private ControllerRacket controllerRacket;
    
#if FUSION2
    [Networked] private float NetworkedTableYRotation { get; set; }
    [Networked] private float NetworkedTableHeight { get; set; }
    [Networked] private NetworkBool NetworkedGameActive { get; set; }
#endif

    // Properties
    public bool IsActive => isActive;
    public GamePhase CurrentPhase => currentPhase;
    public bool RacketsVisible => racketsVisible;

    // Local phase tracking (using shared GamePhase enum)
    private GamePhase currentPhase = GamePhase.Idle;
    
    private void Awake()
    {
        mainManager = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
        if (mainManager == null)
        {
            Debug.LogError($"{LOG_TAG} Could not find AnchorGUIManager_AutoAlignment!");
        }
    }
    
    private void Start()
    {
        // Check if we're returning from VR scene - if so, auto-start passthrough mode after alignment
        CheckReturnFromVRScene();
    }
    

    /// Start passthrough table tennis - spawns table at anchor midpoint without loading new scene
    public void OnStartPassthroughGameClicked()
    {
        Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Start");

        // Get current state from main manager
        if (mainManager != null)
        {
            _localizedAnchor = mainManager.GetLocalizedAnchor();
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked GetLocalizedAnchor: {_localizedAnchor}");
        }
        else
        {
            Debug.LogError($"{LOG_TAG} OnStartPassthroughGameClicked mainManager is null!");
            return;
        }

        // DEBUG MODE: Skip alignment checks but still require anchors to exist
        if (skipAlignmentForDebug)
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked DebugMode enabled");
        }

        // Check if aligned first (skip this check in debug mode)
        if (!skipAlignmentForDebug && (_localizedAnchor == null || !_localizedAnchor.Localized))
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Alignment required");
            return;
        }

        // Check if we have both anchors (skip this check in debug mode)
        var currentAnchors = mainManager?.GetCurrentAnchors();
        Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Current anchors: {currentAnchors?.Count ?? 0}");
        if (!skipAlignmentForDebug && (currentAnchors == null || currentAnchors.Count < 2))
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Need 2 anchors");
            return;
        }

#if FUSION2
        if (Runner == null || !Runner.IsRunning)
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Network not ready");
            return;
        }

        // Check if both devices are aligned (skip in debug mode)
        var currentState = mainManager.GetCurrentState();
        bool bothDevicesAligned = currentState == AnchorGUIManager_AutoAlignment.ColocationState.ClientAligned ||
                                  currentState == AnchorGUIManager_AutoAlignment.ColocationState.HostAligned ||
                                  currentState == AnchorGUIManager_AutoAlignment.ColocationState.Done;
        if (!skipAlignmentForDebug && !bothDevicesAligned)
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Waiting for both devices aligned");
            return;
        }

        // Either player can initiate
        if (Object.HasStateAuthority)
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Starting passthrough game as host");
            StartPassthroughGame();
            RPC_StartPassthroughGame();
        }
        else
        {
            Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Requesting passthrough game as client");
            RPC_RequestPassthroughGame();
        }
#else
        Debug.Log($"{LOG_TAG} OnStartPassthroughGameClicked Starting passthrough game (non-networked)");
        StartPassthroughGame();
#endif
    }
    

    /// Initialize and start the passthrough game
    public void StartGame()
    {
        var currentAnchors = mainManager?.GetCurrentAnchors();
        if (currentAnchors == null || currentAnchors.Count < 2)
        {
            Debug.LogWarning($"{LOG_TAG} Cannot start - need 2 anchors");
            return;
        }
        isActive = true;
        currentPhase = GamePhase.TableAdjust;

        Debug.Log($"{LOG_TAG} Starting passthrough game");

        // Destroy any existing cubes when game starts
        DespawnAllCubes();

        SpawnTable();
        UpdateInstructions();
    }

    /// <summary>
    /// Despawn all NetworkedCube objects when game starts
    /// Delegates to AnchorGUIManager_AutoAlignment which manages cube spawning
    /// </summary>
    private void DespawnAllCubes()
    {
#if FUSION2
        var allCubes = FindObjectsOfType<NetworkedCube>();
        Debug.Log($"{LOG_TAG} DespawnAllCubes: Found {allCubes.Length} cubes to despawn");

        if (allCubes.Length == 0)
        {
            Debug.Log($"{LOG_TAG} DespawnAllCubes: No cubes to despawn");
            return;
        }

        // Use AnchorGUIManager's despawn method - it manages cube spawning and has proper references
        if (mainManager != null)
        {
            Debug.Log($"{LOG_TAG} DespawnAllCubes: Calling mainManager.DespawnAllCubes()");
            mainManager.DespawnAllCubes();
        }
        else
        {
            Debug.LogWarning($"{LOG_TAG} DespawnAllCubes: mainManager is null, attempting direct despawn");

            // Fallback: try to despawn directly
            if (Runner != null && Runner.IsRunning && Object != null && Object.HasStateAuthority)
            {
                foreach (var cube in allCubes)
                {
                    if (cube != null && cube.Object != null && cube.Object.IsValid)
                    {
                        cube.transform.SetParent(null);
                        Debug.Log($"{LOG_TAG} DespawnAllCubes: Despawning cube {cube.Object.Id}");
                        Runner.Despawn(cube.Object);
                    }
                }
            }
        }

        // Start verification coroutine to ensure cubes are actually destroyed
        StartCoroutine(VerifyAndForceDespawnCubes());
#else
        // Non-networked: just destroy
        var allCubes = FindObjectsOfType<NetworkedCube>();
        foreach (var cube in allCubes)
        {
            if (cube != null)
            {
                cube.transform.SetParent(null);
                Destroy(cube.gameObject);
            }
        }
#endif
    }

    /// <summary>
    /// Verification coroutine - if any cubes remain after despawn, force destroy them
    /// </summary>
    private IEnumerator VerifyAndForceDespawnCubes()
    {
        // Wait for network despawn to process
        yield return new WaitForSeconds(0.5f);

        var remainingCubes = FindObjectsOfType<NetworkedCube>();
        if (remainingCubes.Length > 0)
        {
            Debug.LogWarning($"{LOG_TAG} VerifyAndForceDespawnCubes: {remainingCubes.Length} cubes still exist after despawn! Force destroying...");

            foreach (var cube in remainingCubes)
            {
                if (cube != null)
                {
                    Debug.Log($"{LOG_TAG} VerifyAndForceDespawnCubes: Force destroying cube: {cube.gameObject.name}");
                    cube.transform.SetParent(null);
                    Destroy(cube.gameObject);
                }
            }
        }

        // Also check for any orphaned cube-like objects in anchors
        var anchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in anchors)
        {
            if (anchor == null) continue;

            var childrenToDestroy = new System.Collections.Generic.List<GameObject>();
            foreach (Transform child in anchor.transform)
            {
                if (child.name == "Visual" || child.name.Contains("Marker") || child.name.Contains("AnchorCursor"))
                    continue;

                string lowerName = child.name.ToLower();
                if (lowerName.Contains("cube") || lowerName.Contains("networked"))
                {
                    childrenToDestroy.Add(child.gameObject);
                }
            }

            foreach (var obj in childrenToDestroy)
            {
                Debug.Log($"{LOG_TAG} VerifyAndForceDespawnCubes: Destroying orphaned object: {obj.name}");
                obj.transform.SetParent(null);
                Destroy(obj);
            }
        }

        Debug.Log($"{LOG_TAG} VerifyAndForceDespawnCubes: Verification complete");
    }

    /// <summary>
    /// Stop the passthrough game and cleanup
    /// </summary>
    /// <param name="keepTableActive">If true, table stays ac tive (for VR scene transition). If false, table is disabled.</param>
    public void StopGame(bool keepTableActive = false)
    {
        isActive = false;
        currentPhase = GamePhase.Idle;
        isGameMenuOpen = false;
        Time.timeScale = 1.0f;

        // Cleanup spawned objects
        if (spawnedBall != null)
        {
#if FUSION2
            if (Runner != null && Object.HasStateAuthority)
            {
                var netObj = spawnedBall.GetComponent<NetworkObject>();
                if (netObj != null) Runner.Despawn(netObj);
            }
            else
#endif
            {
                Destroy(spawnedBall);
            }
            spawnedBall = null;
        }

        if (spawnedTable != null)
        {
            // Only disable table if not keeping it active for VR scene transition
            if (!keepTableActive)
            {
                spawnedTable.SetActive(false);
                Debug.Log($"{LOG_TAG} StopGame Table disabled");
            }
            else
            {
                Debug.Log($"{LOG_TAG} StopGame Table kept active for VR scene transition");
            }
        }

        // Hide rackets
        if (leftRacket != null) leftRacket.SetActive(false);
        if (rightRacket != null) rightRacket.SetActive(false);

        // Cleanup UI
        if (gameUIPanel != null) Destroy(gameUIPanel);
        if (runtimeMenuPanel != null) Destroy(runtimeMenuPanel);

        // Clear UI references
        scoreText = null;
        roleText = null;
        phaseText = null;
        authorityText = null;
        controlsText = null;

#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedGameActive = false;
        }
#endif

        Debug.Log($"{LOG_TAG} Game stopped (keepTableActive: {keepTableActive})");
    }
    
    private void Update()
    {
        if (!isActive) return;

        HandleInput();
    }

#if FUSION2
    public override void FixedUpdateNetwork()
    {
        // Table sync is now handled via RPC_SyncTableTransform instead of polling
        // No need to apply table state every frame - updates come via explicit RPCs
    }
#endif
    
    // ==================== INPUT HANDLING ====================
    
    private void HandleInput()
    {
        // MENU button - toggle game menu
        if (OVRInput.GetDown(OVRInput.Button.Start))
        {
            ToggleGameMenu();
            return;
        }
        
        if (isGameMenuOpen)
        {
            HandleMenuInput();
            return;
        }
        
        // B/Y button - (racket visibility now handled by ControllerRacket component)
        bool bPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool yPressed = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch);
        if ((bPressed || yPressed) && currentPhase != GamePhase.Playing)
        {
            // Racket toggling now handled by ControllerRacket in single mode
            return;
        }
        
        // A/X button for phase-specific actions
        bool aPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool xPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                if (aPressed || xPressed)
                {
#if FUSION2
                    // ONLY HOST can confirm table adjustment
                    // When host confirms, spawn ball and sync to all clients
                    if (Object != null && Object.HasStateAuthority)
                    {
                        Debug.Log($"{LOG_TAG} HOST confirming table adjustment - spawning ball and advancing phase");
                        ConfirmTableAdjustmentAndSpawnBall();
                    }
                    else
                    {
                        Debug.Log($"{LOG_TAG} CLIENT cannot confirm table adjustment - waiting for host");
                    }
#else
                    AdvancePhase();
#endif
                }
                else
                {
                    HandleTableAdjust();
                }
                break;
                
            case GamePhase.BallPosition:
                HandleBallPosition(aPressed || xPressed);
                break;

            case GamePhase.Playing:
                // Playing phase - game in progress
                break;

            case GamePhase.BallGrounded:
                // This phase is no longer used - we transition directly to BallPosition
                // Keep for backward compatibility
                break;
        }
    }
    
    private void HandleTableAdjust()
    {
        if (spawnedTable == null) return;

        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);

        bool hasInput = Mathf.Abs(rightStick.y) > 0.05f || Mathf.Abs(leftStick.x) > 0.05f;
        if (!hasInput) return;

        // Right stick Y = Height, Left stick X = Rotation
        float rotationDelta = leftStick.x * TableRotateSpeed * Time.deltaTime;
        float heightDelta = rightStick.y * TableMoveSpeed * Time.deltaTime;

#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedTableYRotation += rotationDelta;
            NetworkedTableHeight += heightDelta;
            ApplyTableState();

            // Send RPC to sync table transform to all clients
            RPC_SyncTableTransform(NetworkedTableYRotation, NetworkedTableHeight);
        }
        else if (Runner != null)
        {
            RPC_RequestTableAdjust(rotationDelta, heightDelta);
        }
#else
        ApplyLocalAdjustment(rotationDelta, heightDelta);
#endif
    }
    
    private void HandleBallPosition(bool confirmPressed)
    {
        // GRIP spawns ball if not spawned
        bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);

        if (gripPressed && spawnedBall == null)
        {
            Debug.Log($"{LOG_TAG} HandleBallPosition Grip pressed - spawning ball");
            SpawnBall();
        }

        // Ball positioning is now handled by NetworkedBall component
        // No need for additional logic here - NetworkedBall handles thumbstick input directly
    }
    
    private void HandleMenuInput()
    {
        bool aPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool bPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool xPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        bool yPressed = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch);

        // GRIP = Return to main AnchorGUI menu for game mode selection
        bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);

        // A/X = Resume
        if (aPressed || xPressed)
        {
            ToggleGameMenu();
        }
        // B/Y = Restart
        else if (bPressed || yPressed)
        {
            RestartGame();
        }
        // GRIP = Return to main menu for game mode selection
        else if (gripPressed)
        {
            ReturnToMainMenu();
        }
    }
    
    // ==================== TABLE MANAGEMENT ====================
    
    private void SpawnTable()
    {
        Debug.Log($"{LOG_TAG} SpawnTable START");

        var anchors = mainManager?.GetCurrentAnchors();
        if (anchors == null || anchors.Count < 2)
        {
            Debug.LogError($"{LOG_TAG} SpawnTable FAILED - Not enough anchors! Count: {anchors?.Count ?? 0}");
            return;
        }

        Debug.Log($"{LOG_TAG} SpawnTable Anchors OK - Count: {anchors.Count}");
        Transform primary = anchors[0].transform;
        Transform secondary = anchors[1].transform;

        // CLEANUP: Remove any old tables that were children of preserved anchors from VR scene
        int cleanedCount = 0;
        foreach (var anchor in anchors)
        {
            foreach (Transform child in anchor.transform)
            {
                if (child.name.Contains("PingPongTable") || child.name.Contains("pingpongtable") ||
                    child.name.Contains("pingpong") || child.name.Contains("PingPong") ||
                    child.name.Contains("TableTennis"))
                {
                    Debug.Log($"{LOG_TAG} SpawnTable Cleaning up old table: {child.name}");
                    Destroy(child.gameObject);
                    cleanedCount++;
                }
            }
        }
        Debug.Log($"{LOG_TAG} SpawnTable Cleaned {cleanedCount} old tables");

        // Find or spawn table
        if (spawnedTable == null)
        {
            spawnedTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
            if (spawnedTable != null)
            {
                Debug.Log($"{LOG_TAG} SpawnTable Found existing table: {spawnedTable.name}");
            }
        }

        if (spawnedTable == null && TablePrefab != null)
        {
            Debug.Log($"{LOG_TAG} SpawnTable Instantiating table from prefab");
            spawnedTable = Instantiate(TablePrefab);
            Debug.Log($"{LOG_TAG} SpawnTable Instantiated table: {spawnedTable?.name ?? "NULL"}");
        }
        else if (spawnedTable == null && TablePrefab == null)
        {
            Debug.LogError($"{LOG_TAG} SpawnTable FAILED - TablePrefab is NULL! Check sharedConfig and tablePrefab assignments");
        }

        if (spawnedTable != null)
        {
            // Parent table to primary anchor for colocation
            spawnedTable.transform.SetParent(primary, worldPositionStays: false);

            // Calculate position between anchors
            Vector3 secondaryLocalPos = primary.InverseTransformPoint(secondary.position);
            Vector3 midpoint = secondaryLocalPos / 2f;

            // Calculate rotation
            Vector3 directionToSecondary = secondaryLocalPos;
            directionToSecondary.y = 0;
            float yRot = 0f;

            if (directionToSecondary.sqrMagnitude > 0.01f)
            {
                yRot = Mathf.Atan2(directionToSecondary.x, directionToSecondary.z) * Mathf.Rad2Deg + 90f;
            }

            // Position table
            Vector3 localTablePos = new Vector3(midpoint.x, defaultTableHeight, midpoint.z);
            spawnedTable.transform.localPosition = localTablePos;
            spawnedTable.transform.localRotation = Quaternion.Euler(tableXRotationOffset, yRot, 0);

            spawnedTable.SetActive(true);

#if FUSION2
            // Initialize networked values for both host and client
            // Host: Sets the initial values that will sync to clients
            // Client: Initializes local copy to match initial placement (will be overwritten when host's values sync)
            if (Object != null && Object.HasStateAuthority)
            {
                NetworkedTableYRotation = yRot;
                NetworkedTableHeight = defaultTableHeight;
                NetworkedGameActive = true;
                Debug.Log($"{LOG_TAG} SpawnTable HOST initialized networked values: Rotation={yRot}, Height={defaultTableHeight}");
            }
            else
            {
                // Client: Initialize local tracking of table state to match spawn position
                // This prevents ApplyTableState from using default(0) values before host's values arrive
                Debug.Log($"{LOG_TAG} SpawnTable CLIENT spawned with initial values: Rotation={yRot}, Height={defaultTableHeight}, waiting for host sync...");
            }
#endif

            Debug.Log($"{LOG_TAG} SpawnTable Table configured - Height: {defaultTableHeight}, LocalPos: {spawnedTable.transform.localPosition}, WorldPos: {spawnedTable.transform.position}");

            // Tag rackets in the spawned table for ControllerRacket to find
            foreach (Transform child in spawnedTable.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains("racket") || child.name.ToLower().Contains("paddle"))
                {
                    child.gameObject.tag = "Racket";
                    Debug.Log($"{LOG_TAG} RacketDebug: Tagged racket in table: {child.name}");
                }
            }
        }
        else
        {
            Debug.LogError($"{LOG_TAG} SpawnTable FAILED - spawnedTable is still NULL after all attempts!");
        }

        CreateGameUI();

        // Initialize ControllerRacket for single-hand passthrough mode
        InitializeControllerRacket();

        Debug.Log($"{LOG_TAG} SpawnTable COMPLETE - Table: {(spawnedTable != null ? "OK" : "FAILED")}");
    }
    

    /// Initialize ControllerRacket component for single-hand passthrough gameplay
    private void InitializeControllerRacket()
    {
        // Create ControllerRacket component if it doesn't exist
        if (controllerRacket == null)
        {
            controllerRacket = gameObject.AddComponent<ControllerRacket>();
        }
        
        // Set racket prefab from config if available
        if (RacketPrefab != null)
        {
            controllerRacket.SetRacketPrefab(RacketPrefab);
        }
        
        Debug.Log($"{LOG_TAG} RacketDebug: ControllerRacket component initialized in single-racket mode");
    }
    
    private void ApplyTableState()
    {
        var anchors = mainManager?.GetCurrentAnchors();
        if (spawnedTable == null || anchors == null || anchors.Count == 0) return;

#if FUSION2
        // CRITICAL FIX: Don't apply networked state until host has initialized values
        // When client first spawns table, NetworkedTableHeight will be 0 (default) until host's values sync
        // We detect this by checking if NetworkedGameActive is true (set by host along with other values)
        if (!NetworkedGameActive)
        {
            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] ApplyTableState SKIPPED - NetworkedGameActive=false (waiting for host to initialize values)");
            return;
        }

        Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) ApplyTableState - Before Y update: localPos={spawnedTable.transform.localPosition}, worldPos={spawnedTable.transform.position}");

        spawnedTable.transform.localRotation = Quaternion.Euler(
            tableXRotationOffset, NetworkedTableYRotation, 0);

        Vector3 pos = spawnedTable.transform.localPosition;
        pos.y = NetworkedTableHeight;
        spawnedTable.transform.localPosition = pos;

        Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) ApplyTableState - After Y update: localPos={spawnedTable.transform.localPosition}, worldPos={spawnedTable.transform.position}, NetworkedTableHeight={NetworkedTableHeight}");
#endif
    }
    
    private void ApplyLocalAdjustment(float rotDelta, float heightDelta)
    {
        if (spawnedTable == null) return;
        
        Vector3 euler = spawnedTable.transform.localEulerAngles;
        spawnedTable.transform.localRotation = Quaternion.Euler(euler.x, euler.y + rotDelta, 0);
        
        Vector3 pos = spawnedTable.transform.localPosition;
        pos.y += heightDelta;
        spawnedTable.transform.localPosition = pos;
    }
    
    
    // ==================== BALL MANAGEMENT ====================
    
    private void SpawnBall()
    {
        if (spawnedTable == null)
        {
            Debug.LogWarning($"{LOG_TAG} SpawnBall No table found");
            return;
        }

        // Calculate table surface position (top center of table)
        // FIX: Use TransformPoint to convert local center to world space
        // The table is parented to the anchor, so we need proper world space calculation
        Vector3 tableSurfacePos;

        // Try to get table bounds to find actual surface height
        Renderer tableRenderer = spawnedTable.GetComponentInChildren<Renderer>();
        if (tableRenderer != null)
        {
            // Use bounds to find the TOP of the table (max Y of mesh bounds)
            // Bounds are already in world space
            Bounds bounds = tableRenderer.bounds;
            tableSurfacePos = bounds.center;
            tableSurfacePos.y = bounds.max.y; // Top of the table
            Debug.Log($"{LOG_TAG} SpawnBall Using table renderer bounds - Center: {bounds.center}, Max Y: {bounds.max.y}, Surface: {tableSurfacePos}");
        }
        else
        {
            // Fallback: Calculate world position manually from local position
            // Get the anchor parent transform
            Transform anchorTransform = spawnedTable.transform.parent;
            if (anchorTransform != null)
            {
                // Convert local position to world space via parent transform
                tableSurfacePos = anchorTransform.TransformPoint(spawnedTable.transform.localPosition);
                tableSurfacePos.y += defaultTableHeight;
                Debug.LogWarning($"{LOG_TAG} SpawnBall No renderer found, using anchor.TransformPoint + default height: {tableSurfacePos}");
            }
            else
            {
                // No parent - use world position directly
                tableSurfacePos = spawnedTable.transform.position;
                tableSurfacePos.y += defaultTableHeight;
                Debug.LogWarning($"{LOG_TAG} SpawnBall No anchor parent, using table world position + default height: {tableSurfacePos}");
            }
        }

        // Spawn ball 0.75m above table surface
        Vector3 spawnPos = tableSurfacePos + Vector3.up * 0.75f;
        Debug.Log($"{LOG_TAG} SpawnBall FINAL - Table local pos: {spawnedTable.transform.localPosition}, Table world pos: {spawnedTable.transform.position}, Surface: {tableSurfacePos}, Ball spawn: {spawnPos}");

#if FUSION2
        if (Object.HasStateAuthority && Runner != null)
        {
            Debug.Log($"{LOG_TAG} SpawnBall Host spawning networked ball at {spawnPos}");

            // CRITICAL: Spawn at origin first, then move - Fusion may ignore spawn position if parent exists
            var ball = Runner.Spawn(BallPrefab, Vector3.zero, Quaternion.identity);
            if (ball != null)
            {
                spawnedBall = ball.gameObject;
                Debug.Log($"{LOG_TAG} SpawnBall Ball spawned at origin: {ball.transform.position}");

                // IMMEDIATELY set to correct world position before anything else
                ball.transform.position = spawnPos;

                // Get rigidbody and set its position too
                var rb = ball.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.position = spawnPos;
                    rb.velocity = Vector3.zero;
                }

                Debug.Log($"{LOG_TAG} SpawnBall Ball position IMMEDIATELY corrected to: {spawnPos}, actual: {ball.transform.position}");

                var networkedBall = ball.GetComponent<NetworkedBall>();
                if (networkedBall != null)
                {
                    // Disable the initialization coroutine from moving the ball
                    // CRITICAL: Pass table reference so TableRelativePosition can be set immediately
                    Debug.Log($"{LOG_TAG} SpawnBall Calling SetSpawnPosition to lock position at {spawnPos}, table={spawnedTable.name}");
                    networkedBall.SetSpawnPosition(spawnPos, spawnedTable.transform);
                    Debug.Log($"{LOG_TAG} SpawnBall Ball position after SetSpawnPosition: {ball.transform.position}");

                    Debug.Log($"{LOG_TAG} SpawnBall Entering positioning mode");
                    networkedBall.EnterPositioningMode();
                    Debug.Log($"{LOG_TAG} SpawnBall Ball position after EnterPositioningMode: {ball.transform.position}");

                    // Start coroutine to continuously force position during initialization
                    StartCoroutine(EnsureBallPositionAfterInit(networkedBall, spawnPos));
                }
                else
                {
                    Debug.LogWarning($"{LOG_TAG} SpawnBall No NetworkedBall component found");
                }

                RPC_NotifyBallSpawned();
                Debug.Log($"{LOG_TAG} SpawnBall Notified clients of ball spawn, final ball position: {ball.transform.position}");
            }
            else
            {
                Debug.LogError($"{LOG_TAG} SpawnBall Failed to spawn ball");
            }
        }
        else
        {
            Debug.Log($"{LOG_TAG} SpawnBall Client requesting ball spawn");
            RPC_RequestSpawnBall();
        }
#else
        // Non-networked fallback
        var ballPrefabObj = Resources.Load<GameObject>("Ball");
        if (ballPrefabObj != null)
        {
            spawnedBall = Instantiate(ballPrefabObj, spawnPos, Quaternion.identity);
            Debug.Log($"{LOG_TAG} SpawnBall Non-networked ball spawned");
        }
        else
        {
            Debug.LogError($"{LOG_TAG} SpawnBall Ball prefab not found in Resources");
        }
#endif

        Debug.Log($"{LOG_TAG} SpawnBall Completed");
    }

    /// <summary>
    /// Called by NetworkedBall when ball is hit by racket - transitions to Playing phase
    /// Sets timeScale to 0.5 for slow-motion gameplay
    /// </summary>
    public void OnBallHit(int playerNumber)
    {
        if (currentPhase == GamePhase.Playing) return;

        currentPhase = GamePhase.Playing;
        Time.timeScale = 0.5f;
        Debug.Log($"{LOG_TAG} OnBallHit by player {playerNumber} - Phase: Playing, TimeScale: 0.5");

#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            RPC_NotifyPhaseChange(GamePhase.Playing);
        }
#endif
        UpdateInstructions();
    }

    /// <summary>
    /// Called by NetworkedBall when ball hits ground
    /// Authority is swapped by NetworkedBall, so transition to BallPosition for next serve
    /// </summary>
    public void OnBallGroundHit()
    {
        Debug.Log($"{LOG_TAG} OnBallGroundHit called - transitioning to BallPosition for next serve");
        ballNeedsRespawn = true;
        currentPhase = GamePhase.BallPosition;
        Time.timeScale = 1.0f;
#if FUSION2
        // Notify peer of phase change
        if (Object != null && Object.HasStateAuthority)
        {
            RPC_NotifyPhaseChange(GamePhase.BallPosition);
        }
#endif
        UpdateScoreDisplay();
        UpdateInstructions();
        Debug.Log($"{LOG_TAG} Ball hit ground - Phase: {currentPhase}, ready for next serve");
    }

    /// <summary>
    /// Called when ball authority changes (via RPC from NetworkedBall)
    /// Updates UI to reflect new authority
    /// </summary>
    public void OnAuthorityChanged(int newAuthority)
    {
        Debug.Log($"{LOG_TAG} OnAuthorityChanged called - new authority: Player {newAuthority}");
        UpdateScoreDisplay();
        UpdateInstructions();
    }

    /// <summary>
    /// Ensure ball stays at spawn position after initialization completes
    /// Check multiple times to prevent any async initialization from moving it
    /// </summary>
    private IEnumerator EnsureBallPositionAfterInit(NetworkedBall ball, Vector3 targetPosition)
    {
        // Check and correct position multiple times during initialization
        for (int i = 0; i < 10; i++)
        {
            yield return new WaitForSeconds(0.1f);

            float distance = Vector3.Distance(ball.transform.position, targetPosition);
            if (distance > 0.01f) // If ball moved away from target
            {
                Debug.Log($"{LOG_TAG} EnsureBallPositionAfterInit[{i}]: Ball drifted to {ball.transform.position}, correcting to {targetPosition} (distance: {distance:F3}m)");

                // Force back to correct position
                ball.transform.position = targetPosition;

                // Also update rigidbody
                var rb = ball.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.position = targetPosition;
                    rb.velocity = Vector3.zero;
                }
            }
            else
            {
                Debug.Log($"{LOG_TAG} EnsureBallPositionAfterInit[{i}]: Ball stable at correct position {ball.transform.position}");
            }
        }

        Debug.Log($"{LOG_TAG} EnsureBallPositionAfterInit: Complete. Final position: {ball.transform.position}");
    }

    /// <summary>
    /// Update score display from NetworkedBall
    /// </summary>
    private void UpdateScoreDisplay()
    {
        if (scoreText == null || spawnedBall == null) return;

        var networkedBall = spawnedBall.GetComponent<NetworkedBall>();
        if (networkedBall != null)
        {
            scoreText.text = $"{networkedBall.ScorePlayer1} - {networkedBall.ScorePlayer2}";
            Debug.Log($"{LOG_TAG} Score updated: {scoreText.text}, Authority: Player {networkedBall.CurrentAuthority}");
        }
    }

    /// <summary>
    /// Respawn ball at initial position above table
    /// NOTE: This method is deprecated - ball respawn now happens automatically via NetworkedBall
    /// when transitioning to BallPosition phase after ground hit
    /// </summary>
    private void RespawnBall()
    {
#if FUSION2
        if (!Object.HasStateAuthority)
        {
            Debug.Log($"{LOG_TAG} Client requesting ball respawn");
            RPC_RequestRespawnBall();
            return;
        }
#endif

        Debug.Log($"{LOG_TAG} Respawning ball");

        // Despawn old ball
        if (spawnedBall != null)
        {
#if FUSION2
            if (Runner != null && Object.HasStateAuthority)
            {
                var netObj = spawnedBall.GetComponent<NetworkObject>();
                if (netObj != null) Runner.Despawn(netObj);
            }
#endif
            spawnedBall = null;
        }

        // Spawn new ball
        SpawnBall();

        // Reset state
        ballNeedsRespawn = false;
        // Stay in BallPosition phase - ball will be positioned for serve
        UpdateInstructions();
    }

#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRespawnBall()
    {
        RespawnBall();
    }
#endif

    // ==================== GAME FLOW ====================

#if FUSION2
    /// <summary>
    /// HOST ONLY: Confirms table adjustment, spawns ball, and syncs state to all clients
    /// </summary>
    private void ConfirmTableAdjustmentAndSpawnBall()
    {
        if (!Object.HasStateAuthority)
        {
            Debug.LogWarning($"{LOG_TAG} ConfirmTableAdjustmentAndSpawnBall called on client - ignoring");
            return;
        }

        Debug.Log($"{LOG_TAG} ConfirmTableAdjustmentAndSpawnBall - Host confirming table adjustment");

        // Advance phase to BallPosition
        currentPhase = GamePhase.BallPosition;
        Debug.Log($"{LOG_TAG} Phase: BallPosition (confirmed by host)");

        // Spawn ball at correct position above table
        SpawnBall();

        // Notify all clients of phase change and ball spawn
        RPC_NotifyTableAdjustmentConfirmed();

        UpdateInstructions();
    }

    /// <summary>
    /// RPC to notify all clients that host has confirmed table adjustment and ball is spawned
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyTableAdjustmentConfirmed()
    {
        Debug.Log($"{LOG_TAG} RPC_NotifyTableAdjustmentConfirmed received - updating phase to BallPosition");

        // Update local phase
        currentPhase = GamePhase.BallPosition;

        // Update UI
        UpdateScoreDisplay();
        UpdateInstructions();

        Debug.Log($"{LOG_TAG} Table adjustment confirmed by host - ball should now be visible at spawn position");
    }
#endif

    private void AdvancePhase()
    {
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                currentPhase = GamePhase.BallPosition;
                Debug.Log($"{LOG_TAG} Phase: BallPosition");
#if FUSION2
                // Notify peer of phase change
                if (Object != null && Object.HasStateAuthority)
                {
                    RPC_NotifyPhaseChange(GamePhase.BallPosition);
                }
                else if (Runner != null)
                {
                    RPC_RequestPhaseAdvance();
                }
#endif
                break;

            case GamePhase.BallPosition:
                if (spawnedBall != null)
                {
                    currentPhase = GamePhase.Playing;
                    Debug.Log($"{LOG_TAG} Phase: Playing");
#if FUSION2
                    // Notify peer of phase change
                    if (Object != null && Object.HasStateAuthority)
                    {
                        RPC_NotifyPhaseChange(GamePhase.Playing);
                    }
#endif
                }
                break;
        }

        UpdateInstructions();
    }
    
    private void RestartGame()
    {
        isGameMenuOpen = false;
        Time.timeScale = 1.0f;
        if (runtimeMenuPanel != null) runtimeMenuPanel.SetActive(false);

        // Cleanup ball
        if (spawnedBall != null)
        {
#if FUSION2
            if (Runner != null && Object.HasStateAuthority)
            {
                var netObj = spawnedBall.GetComponent<NetworkObject>();
                if (netObj != null) Runner.Despawn(netObj);
            }
#endif
            spawnedBall = null;
        }

        currentPhase = GamePhase.TableAdjust;
#if FUSION2
        // Notify peer of phase change
        if (Object != null && Object.HasStateAuthority)
        {
            RPC_NotifyPhaseChange(GamePhase.TableAdjust);
        }
#endif
        UpdateInstructions();
        Debug.Log($"{LOG_TAG} Game restarted");
    }
    
    private void ReturnToMainMenu()
    {
        StopGame();
        // The main GUI will be restored automatically when returning to the main scene
        // No need to call OnPassthroughGameEnded as the scene transition handles UI restoration
        Debug.Log($"{LOG_TAG} Returned to mode selection");
    }
    
    private void SwitchToVRGame()
    {
        Debug.Log($"{LOG_TAG} Switching to VR TableTennis scene...");

        // Stop passthrough game but KEEP TABLE ACTIVE for VR scene to use
        // Table is parented to anchor (which has DontDestroyOnLoad), so it persists across scenes
        StopGame(keepTableActive: true);
        
        // Load the TableTennis scene using Fusion's networked scene loading
#if FUSION2
        if (Runner != null && Runner.IsRunning && Object.HasStateAuthority)
        {
            // Get the table tennis scene name from main manager
            string sceneName = "TableTennis"; // Default value
            
            Debug.Log($"[Passthrough] Host loading networked scene: {sceneName}");
            
            // Get scene index from Build Settings by name
            int sceneIndex = UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath(sceneName);
            
            // If not found by name alone, try common path patterns
            if (sceneIndex < 0)
            {
                sceneIndex = UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath($"Assets/Colocation/Scenes/Table Tennis/{sceneName}.unity");
            }
            
            if (sceneIndex >= 0)
            {
                // Use Fusion's scene loading - this syncs to all clients automatically
                Runner.LoadScene(Fusion.SceneRef.FromIndex(sceneIndex));
            }
            else
            {
                Debug.LogError($"[Passthrough] Scene '{sceneName}' not found in Build Settings!");
            }
        }
        else if (!Object.HasStateAuthority)
        {
            // Client requests host to load the scene
            RPC_RequestSwitchToVRGame();
        }
#else
        // Fallback for non-Fusion
        UnityEngine.SceneManagement.SceneManager.LoadScene("TableTennis");
#endif
    }
    
#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSwitchToVRGame()
    {
        Debug.Log("[Passthrough] Client requested switch to VR game");
        SwitchToVRGame();
    }
#endif
    
    // ==================== UI ====================
    
    private void ToggleGameMenu()
    {
        isGameMenuOpen = !isGameMenuOpen;
        
        if (isGameMenuOpen)
        {
            ShowGameMenu();
        }
        else if (runtimeMenuPanel != null)
        {
            runtimeMenuPanel.SetActive(false);
        }
        
        Debug.Log($"{LOG_TAG} Menu {(isGameMenuOpen ? "opened" : "closed")}");
    }
    
    private void ShowGameMenu()
    {
        if (runtimeMenuPanel == null)
        {
            CreateMenuPanel();
        }
        runtimeMenuPanel.SetActive(true);
    }
    
    private void CreateMenuPanel()
    {
        var cam = Camera.main;
        if (cam == null) return;
        
        runtimeMenuPanel = new GameObject("PassthroughGameMenu");
        Vector3 menuPosition = cam.transform.position + cam.transform.forward * 1.5f;
        menuPosition.y += 1.0f; // Move GUI higher on Y axis
        runtimeMenuPanel.transform.position = menuPosition;
        runtimeMenuPanel.transform.rotation = Quaternion.LookRotation(
            runtimeMenuPanel.transform.position - cam.transform.position);
        
        // Create menu text
        var textGO = new GameObject("MenuText");
        textGO.transform.SetParent(runtimeMenuPanel.transform, false);
        var textMesh = textGO.AddComponent<TextMesh>();
        textMesh.text = "GAME MENU\n\nA/X: Resume\nB/Y: Restart\nGRIP: Switch to VR Game\nStick Down: Exit";
        textMesh.fontSize = 40; // Increased from 32
        textMesh.characterSize = 0.025f; // Increased from 0.02f
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = new Color(0.1f, 0.1f, 0.6f); // Dark blue instead of white
    }
    
    private void CreateGameUI()
    {
        var anchors = mainManager?.GetCurrentAnchors();
        if (gameUIPanel != null || anchors == null || anchors.Count < 2) return;
        
        Transform primary = anchors[0].transform;
        Transform secondary = anchors[1].transform;
        
        Vector3 midpoint = (primary.position + secondary.position) / 2f;
        midpoint.y = 1.5f;
        
        Vector3 dir = (secondary.position - primary.position).normalized;
        dir.y = 0;
        
        gameUIPanel = new GameObject("PassthroughGameUI");

        // FIX: Parent UI to camera rig so it stays stable during periodic realignment
        // Find the OVR camera rig
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        Camera mainCam = Camera.main;

        if (cameraRig != null && mainCam != null)
        {
            // Parent to camera rig (or tracking space) to stay stable during realignment
            gameUIPanel.transform.SetParent(cameraRig.trackingSpace != null ? cameraRig.trackingSpace : cameraRig.transform, false);

            // Position 2.5m away from the table, in the table direction
            Vector3 cameraPos = mainCam.transform.position;
            Vector3 tablePos = midpoint;
            Vector3 dirToTable = (tablePos - cameraPos).normalized;
            Vector3 targetWorldPos = tablePos - dirToTable * 4.0f;
            targetWorldPos.y += 0.3f; // Move GUI higher on Y axis
            gameUIPanel.transform.localPosition = gameUIPanel.transform.parent.InverseTransformPoint(targetWorldPos);

            // Face AWAY from camera (flip 180°) to maximize passthrough visibility
            // The backside is transparent, UI elements face away
            Vector3 dirAwayFromCamera = gameUIPanel.transform.position - mainCam.transform.position;
            dirAwayFromCamera.y = 0;
            if (dirAwayFromCamera.sqrMagnitude > 0.01f)
            {
                gameUIPanel.transform.rotation = Quaternion.LookRotation(dirAwayFromCamera);
            }

            Debug.Log($"{LOG_TAG} CreateGameUI: UI panel parented to camera rig at world position {targetWorldPos}, 2.5m from table, flipped to maximize passthrough visibility");
        }

        // Create Background Panel
        var panelObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        panelObj.name = "Background";
        panelObj.transform.SetParent(gameUIPanel.transform, false);
        panelObj.transform.localScale = new Vector3(4f, 3f, 1f);
        var renderer = panelObj.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.shader = Shader.Find("Unlit/Color");
            renderer.material.color = new Color(0, 0, 0, 0.85f);
        }

        // Helper to create text
        TextMesh CreateText(string name, Vector3 pos, int fontSize, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(gameUIPanel.transform, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.fontSize = fontSize;
            tm.characterSize = 0.01f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            return tm;
        }

        // NEW LAYOUT MATCHING TABLETENNISMANAGER:
        // Top: Large Score (yellow)
        scoreText = CreateText("ScoreText", new Vector3(0, 0.85f, -0.05f), 450, Color.yellow);
        scoreText.text = "0 - 0";

        // Role: Client/Host (green)
        roleText = CreateText("RoleText", new Vector3(0, 0.5f, -0.05f), 180, Color.green);
#if FUSION2
        roleText.text = Object != null && Object.HasStateAuthority ? "HOST" : "CLIENT";
#else
        roleText.text = "SOLO";
#endif

        // Game Phase (white)
        phaseText = CreateText("PhaseText", new Vector3(0, 0.2f, -0.05f), 200, Color.white);

        // Active Authority (cyan/yellow based on whose turn)
        authorityText = CreateText("AuthorityText", new Vector3(0, -0.1f, -0.05f), 160, Color.cyan);

        // Controller Info (cyan, smaller)
        controlsText = CreateText("ControlsText", new Vector3(0, -0.65f, -0.05f), 150, Color.cyan);

        UpdateInstructions();
    }
    
    private void UpdateInstructions()
    {
        if (phaseText == null) return;

        // Update phase display
        phaseText.text = currentPhase.GetDisplayName();

        // Get current ball authority
        int ballAuthority = 0;
        bool isLocalPlayerAuthority = false;
        if (spawnedBall != null)
        {
            var networkedBall = spawnedBall.GetComponent<NetworkedBall>();
            if (networkedBall != null)
            {
                ballAuthority = networkedBall.CurrentAuthority;
#if FUSION2
                // Determine if local player has authority
                // Player 1 = Host, Player 2 = Client
                if (Object != null)
                {
                    isLocalPlayerAuthority = (ballAuthority == 1 && Object.HasStateAuthority) ||
                                            (ballAuthority == 2 && !Object.HasStateAuthority);
                }
#endif
            }
        }

        // Update authority display based on phase
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                // TableAdjust: Host Only can confirm
#if FUSION2
                authorityText.text = "Adjust Table with thumbsticks.\nHost should press A/X to confirm";
                authorityText.color = Color.green;
                if (controlsText) controlsText.text =
                    "[Right Stick Y] Adjust Height\n" +
                    "[Left Stick X] Rotate Table\n" +
                    "[A/X] Confirm & Spawn Ball\n" +
                    "[B/Y] Get Bat" ;
#else
                authorityText.text = "ADJUST TABLE";
                authorityText.color = Color.cyan;
                if (controlsText) controlsText.text =
                    "[Right Stick Y] Adjust Height\n" +
                    "[Left Stick X] Rotate Table\n" +
                    "[A/X] Confirm\n" +
                    "[B/Y] Get Bat" ;
#endif
                break;

            case GamePhase.BallPosition:
                // BallPosition: Show current turn owner
                if (ballAuthority > 0)
                {
                    if (isLocalPlayerAuthority)
                    {
                        authorityText.text = "YOUR SERVE";
                        authorityText.color = Color.yellow;
                    }
                    else
                    {
                        authorityText.text = $"OPPONENT'S SERVE (P{ballAuthority})";
                        authorityText.color = Color.gray;
                    }
                }
                else
                {
                    authorityText.text = "READY TO SERVE";
                    authorityText.color = Color.cyan;
                }
                if (controlsText)
                {
                    if (spawnedBall == null)
                    {
                        controlsText.text = "[GRIP] Spawn Ball";
                    }
                    else
                    {
                        controlsText.text =
                            "[B/Y] Toggle Ball Adjust\n" +
                            "[Left Stick] Move Ball XZ\n" +
                            "[Right Stick Y] Move Ball Up/Down\n" +
                            "[A/X] Reset Ball Position\n" +
                            "Hit ball with racket to start!";
                    }
                }
                break;

            case GamePhase.Playing:
                // Playing: Show whose turn it is
                if (ballAuthority > 0)
                {
                    if (isLocalPlayerAuthority)
                    {
                        authorityText.text = "YOUR TURN";
                        authorityText.color = Color.yellow;
                    }
                    else
                    {
                        authorityText.text = $"OPPONENT'S TURN (P{ballAuthority})";
                        authorityText.color = Color.gray;
                    }
                }
                else
                {
                    authorityText.text = "PLAYING";
                    authorityText.color = Color.white;
                }
                if (controlsText) controlsText.text =
                    "[MENU] Pause Game\n" +
                    "[A/X] Reset Ball";
                break;

            case GamePhase.BallGrounded:
                // BallGrounded: No longer used, but keep for compatibility
                if (ballAuthority > 0)
                {
                    if (isLocalPlayerAuthority)
                    {
                        authorityText.text = "YOU WON ROUND";
                        authorityText.color = Color.yellow;
                    }
                    else
                    {
                        authorityText.text = $"OPPONENT WON (P{ballAuthority})";
                        authorityText.color = Color.gray;
                    }
                }
                if (controlsText) controlsText.text = "Transitioning...";
                break;

            case GamePhase.Idle:
                authorityText.text = "WAITING";
                authorityText.color = Color.gray;
                if (controlsText) controlsText.text = "";
                break;
        }
    }
    
    // ==================== NETWORK RPCs ====================
    
#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableAdjust(float rotDelta, float heightDelta)
    {
        NetworkedTableYRotation += rotDelta;
        NetworkedTableHeight += heightDelta;
        ApplyTableState(); // Apply immediately so host sees changes

        // Sync updated position to all clients
        RPC_SyncTableTransform(NetworkedTableYRotation, NetworkedTableHeight);
    }

    /// <summary>
    /// RPC to sync table position and rotation from host to all clients.
    /// Called whenever host adjusts table position.
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_SyncTableTransform(float yRotation, float height)
    {
        if (Object.HasStateAuthority) return; // Host already applied

        // Update local networked values cache
        NetworkedTableYRotation = yRotation;
        NetworkedTableHeight = height;

        // Apply to table immediately
        ApplyTableState();
        Debug.Log($"{LOG_TAG} [RPC] Client received table sync: Rotation={yRotation}, Height={height}");
    }

    /// <summary>
    /// Client requests current table state from host (used on join/spawn)
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableState()
    {
        Debug.Log($"{LOG_TAG} [RPC] Host received request for current table state");
        // Send current state to all clients
        RPC_SyncTableTransform(NetworkedTableYRotation, NetworkedTableHeight);
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnBall()
    {
        SpawnBall();
    }
    
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyBallSpawned()
    {
        StartCoroutine(FindBallDelayed());
    }

    private IEnumerator FindBallDelayed()
    {
        yield return null;
        yield return null;

        var ball = FindObjectOfType<NetworkedBall>();
        if (ball != null)
        {
            spawnedBall = ball.gameObject;
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestPhaseAdvance()
    {
        // Client requests host to advance phase
        if (currentPhase == GamePhase.TableAdjust)
        {
            currentPhase = GamePhase.BallPosition;
            Debug.Log($"{LOG_TAG} Host: Client requested phase advance to BallPosition");
            // Notify all clients of phase change
            RPC_NotifyPhaseChange(GamePhase.BallPosition);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyPhaseChange(GamePhase newPhase)
    {
        // Synchronize phase on all clients
        if (!Object.HasStateAuthority)
        {
            currentPhase = newPhase;
            UpdateInstructions();
            Debug.Log($"{LOG_TAG} Client: Phase synchronized to {newPhase}");
        }
    }
#endif

    /// <summary>
    /// Check if returning from VR scene and auto-start passthrough mode
    /// </summary>
    private void CheckReturnFromVRScene()
    {
        if (PlayerPrefs.GetInt("ReturnFromVRScene", 0) == 1)
        {
            PlayerPrefs.SetInt("ReturnFromVRScene", 0);
            PlayerPrefs.Save();
            Debug.Log($"{LOG_TAG} DEBUG_VR-AR] Returning from VR scene - will auto-start passthrough after alignment");

            // Try to find persistent anchors from previous session
            FindPersistentAnchors();

            // Start a coroutine to wait for alignment and then auto-start passthrough
            StartCoroutine(AutoStartPassthroughAfterAlignment());
        }
    }

    /// <summary>
    /// Find any persistent spatial anchors from the DontDestroyOnLoad scene
    /// </summary>
    private void FindPersistentAnchors()
    {
        var allAnchors = FindObjectsOfType<OVRSpatialAnchor>();
        Debug.Log($"{LOG_TAG} DEBUG_VR-AR] Found {allAnchors.Length} persistent spatial anchors");

        if (mainManager != null)
        {
            var currentAnchors = mainManager.GetCurrentAnchors();
            
            foreach (var anchor in allAnchors)
            {
                if (anchor.Localized && !currentAnchors.Contains(anchor))
                {
                    currentAnchors.Add(anchor);
                    Debug.Log($"{LOG_TAG} DEBUG_VR-AR] Restored anchor: {anchor.Uuid}");

                    // Set the first localized anchor as our reference
                    if (_localizedAnchor == null)
                    {
                        _localizedAnchor = anchor;
                    }
                }
            }

            Debug.Log($"{LOG_TAG} [DEBUG VR-AR] currentAnchors count after restore: {currentAnchors.Count}");
        }
    }
    /// Wait for alignment to complete, then auto-start passthrough game
 
    private System.Collections.IEnumerator AutoStartPassthroughAfterAlignment()
    {
        Debug.Log($"{LOG_TAG} [DEBUG VR-AR] Waiting for alignment before auto-starting passthrough...");

        // Wait for anchors to be localized (max 30 seconds)
        float timeout = 30f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            // Try to find persistent anchors if we don't have any yet
            if (mainManager == null || mainManager.GetCurrentAnchors() == null || mainManager.GetCurrentAnchors().Count < 2)
            {
                FindPersistentAnchors();
            }

            // Check if we have at least 2 anchors and alignment is complete
            var currentAnchors = mainManager?.GetCurrentAnchors();
            var localizedAnchor = mainManager?.GetLocalizedAnchor();
            if (currentAnchors != null && currentAnchors.Count >= 2 && localizedAnchor != null && localizedAnchor.Localized)
            {
                // Give UI a moment to update
                yield return new WaitForSeconds(1f);

                Debug.Log($"{LOG_TAG} [DEBUG VR-AR] Alignment ready - auto-starting passthrough game");
                StartGameFromAuto();
                yield break;
            }

            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }

        Debug.LogWarning($"{LOG_TAG} [DEBUG VR-AR] Timeout waiting for alignment, passthrough will not auto-start");
    }

    /// Start the game automatically after alignment is detected
     private void StartGameFromAuto()
    {
        var currentAnchors = mainManager?.GetCurrentAnchors();
        if (currentAnchors != null && currentAnchors.Count >= 2)
        {
            StartGame();
        }
        else
        {
            Debug.LogWarning($"{LOG_TAG} Cannot auto-start - insufficient anchors");
        }
    }

    /// Actually start the passthrough game - spawn table and show instructions
    private void StartPassthroughGame()
    {
        isActive = true;
        currentPhase = GamePhase.TableAdjust;

        // Despawn all existing cubes when AR game starts (host handles actual despawn)
        Debug.Log($"{LOG_TAG} StartPassthroughGame: Despawning all cubes before starting AR game");
        DespawnAllCubes();

        // Disable anchor placement mode
        if (mainManager != null)
        {
            mainManager.SetWaitingForGripToPlaceAnchors(false);
        }

        // Hide the entire main UI Canvas
        if (mainManager != null)
        {
            mainManager.HideMainGUIPanel();
        }

        // Check if we have anchors - client might need to wait for anchor loading
        var currentAnchors = mainManager?.GetCurrentAnchors();
        if (currentAnchors == null || currentAnchors.Count < 2)
        {
            Debug.Log("[Passthrough] No anchors yet, waiting for anchor loading...");
            StartCoroutine(WaitForAnchorsAndSpawnTable());
        }
        else
        {
            // Spawn or enable the table at anchor midpoint
            SpawnTable();
            
            // Client: request current table state from host
#if FUSION2
            if (Object != null && !Object.HasStateAuthority && spawnedTable != null)
            {
                RPC_RequestTableState();
                Debug.Log("[Passthrough] Client: Requested current table state from host");
            }
#endif
        }
        
        // Rackets are now initialized by ControllerRacket component in SpawnTabl
        // Switch UI to show table adjustment instructions
        UpdateInstructions();
        Debug.Log($"{LOG_TAG} Table Adjust Mode - Use thumbstick to adjust, B/Y switches racket hand, A/X when ready");
    }

#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestPassthroughGame() 
    { 
        Debug.Log($"{LOG_TAG} Host received request for passthrough game");
        StartPassthroughGame();
        RPC_StartPassthroughGame();
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_StartPassthroughGame()
    {
        Debug.Log($"{LOG_TAG} Received passthrough game start notification");
        if (!isActive) // Don't start twice on host
        {
            StartPassthroughGame();
        }
    }

    private System.Collections.IEnumerator WaitForAnchorsAndSpawnTable()
    {
        float timeout = 10f;
        float elapsed = 0f;
        
        while (elapsed < timeout)
        {
            var currentAnchors = mainManager?.GetCurrentAnchors();
            if (currentAnchors != null && currentAnchors.Count >= 2)
            {
                Debug.Log($"{LOG_TAG} Anchors loaded, spawning table");
                SpawnTable();
                yield break;
            }
            
            Debug.Log($"{LOG_TAG} Waiting for anchors... ({elapsed:F1}s)");
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        
        Debug.LogError($"{LOG_TAG} Timeout waiting for anchors! Cannot spawn table.");
    }
#endif
}
