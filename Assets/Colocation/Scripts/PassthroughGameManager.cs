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
    
    // Game phases
    public enum GamePhase {
        Idle,           // Not in passthrough mode
        TableAdjust,    // Adjusting table position/rotation
        BallPosition,   // Ball spawned, adjusting position
        Playing,        // Game in progress
        BallGrounded    // Ball hit ground, waiting for respawn
    }

    // State
    private GamePhase currentPhase = GamePhase.Idle;
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
    private TextMesh infoText;
    private TextMesh statusText;
    private TextMesh controlsText;
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
        
        SpawnTable();
        UpdateInstructions();
    }
    
    /// <summary>
    /// Stop the passthrough game and cleanup
    /// </summary>
    /// <param name="keepTableActive">If true, table stays active (for VR scene transition). If false, table is disabled.</param>
    public void StopGame(bool keepTableActive = false)
    {
        isActive = false;
        currentPhase = GamePhase.Idle;
        isGameMenuOpen = false;

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
        if (!NetworkedGameActive) return;

        // Client: Apply networked table state every frame to see host's adjustments
        if (!Object.HasStateAuthority && spawnedTable != null)
        {
            ApplyTableState();
        }
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
                    AdvancePhase();
                else
                    HandleTableAdjust();
                break;
                
            case GamePhase.BallPosition:
                HandleBallPosition(aPressed || xPressed);
                break;

            case GamePhase.Playing:
                // Check for GRIP to respawn if ball is grounded
                if (ballNeedsRespawn)
                {
                    bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                                      OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
                    if (gripPressed)
                    {
                        RespawnBall();
                    }
                }
                break;

            case GamePhase.BallGrounded:
                // Wait for GRIP to respawn
                bool gripPressedGrounded = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
                if (gripPressedGrounded)
                {
                    Debug.Log($"{LOG_TAG} GRIP pressed in BallGrounded phase - respawning ball");
                    RespawnBall();
                }
                break;
        }
    }
    
    private void HandleTableAdjust()
    {
        if (spawnedTable == null) return;

        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);

        bool hasInput = Mathf.Abs(rightStick.x) > 0.05f || Mathf.Abs(leftStick.y) > 0.05f;
        if (!hasInput) return;

        // Right stick X = Rotation, Left stick Y = Height
        float rotationDelta = rightStick.x * TableRotateSpeed * Time.deltaTime;
        float heightDelta = leftStick.y * TableMoveSpeed * Time.deltaTime;
        
#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedTableYRotation += rotationDelta;
            NetworkedTableHeight += heightDelta;
            ApplyTableState();
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
        
        // GRIP = Switch to VR TableTennis scene
        bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
        
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
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
        // GRIP = Switch to VR Game
        else if (gripPressed)
        {
            SwitchToVRGame();
        }
        // Thumbstick down = Return to menu
        else if (stick.y < -0.7f)
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
        // IMPORTANT: Use world position, not relying on NetworkedTableHeight which may not be synced yet
        Vector3 tableSurfacePos = spawnedTable.transform.position;

        // Try to get table bounds to find actual surface height
        Renderer tableRenderer = spawnedTable.GetComponentInChildren<Renderer>();
        if (tableRenderer != null)
        {
            // Use bounds to find the TOP of the table (max Y of mesh bounds)
            Bounds bounds = tableRenderer.bounds;
            tableSurfacePos = bounds.center;
            tableSurfacePos.y = bounds.max.y; // Top of the table
            Debug.Log($"{LOG_TAG} SpawnBall Using table renderer bounds - Center: {bounds.center}, Max Y: {bounds.max.y}, Surface: {tableSurfacePos}");
        }
        else
        {
            // Fallback: assume table is at its transform position + default height
            tableSurfacePos.y += defaultTableHeight;
            Debug.LogWarning($"{LOG_TAG} SpawnBall No renderer found, using table transform + default height: {tableSurfacePos}");
        }

        // Spawn ball 0.75m above table surface
        Vector3 spawnPos = tableSurfacePos + Vector3.up * 0.75f;
        Debug.Log($"{LOG_TAG} SpawnBall FINAL - Table world pos: {spawnedTable.transform.position}, Surface: {tableSurfacePos}, Ball spawn: {spawnPos}");

#if FUSION2
        if (Object.HasStateAuthority && Runner != null)
        {
            Debug.Log($"{LOG_TAG} SpawnBall Host spawning networked ball");
            var ball = Runner.Spawn(BallPrefab, spawnPos, Quaternion.identity);
            if (ball != null)
            {
                spawnedBall = ball.gameObject;
                Debug.Log($"{LOG_TAG} SpawnBall Ball spawned successfully: {ball.Id}");

                var networkedBall = ball.GetComponent<NetworkedBall>();
                if (networkedBall != null)
                {
                    Debug.Log($"{LOG_TAG} SpawnBall Entering positioning mode");
                    networkedBall.EnterPositioningMode();
                }
                else
                {
                    Debug.LogWarning($"{LOG_TAG} SpawnBall No NetworkedBall component found");
                }

                RPC_NotifyBallSpawned();
                Debug.Log($"{LOG_TAG} SpawnBall Notified clients of ball spawn");
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
    /// Called by NetworkedBall when ball hits ground
    /// </summary>
    public void OnBallGroundHit()
    {
        Debug.Log($"{LOG_TAG} OnBallGroundHit called - setting phase to BallGrounded");
        ballNeedsRespawn = true;
        currentPhase = GamePhase.BallGrounded;
        UpdateInstructions();
        Debug.Log($"{LOG_TAG} Ball hit ground - Phase: {currentPhase}, ballNeedsRespawn: {ballNeedsRespawn} - Press GRIP to respawn");
    }

    /// <summary>
    /// Respawn ball at initial position above table
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
        currentPhase = GamePhase.Playing;
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
    
    private void AdvancePhase()
    {
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                currentPhase = GamePhase.BallPosition;
                Debug.Log($"{LOG_TAG} Phase: BallPosition");
                break;
                
            case GamePhase.BallPosition:
                if (spawnedBall != null)
                {
                    currentPhase = GamePhase.Playing;
                    Debug.Log($"{LOG_TAG} Phase: Playing");
                }
                break;
        }
        
        UpdateInstructions();
    }
    
    private void RestartGame()
    {
        isGameMenuOpen = false;
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
        runtimeMenuPanel.transform.position = cam.transform.position + cam.transform.forward * 1.5f;
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
        gameUIPanel.transform.position = primary.position - dir * 3f;
        gameUIPanel.transform.position = new Vector3(
            gameUIPanel.transform.position.x, 1.5f, gameUIPanel.transform.position.z);
        gameUIPanel.transform.rotation = Quaternion.LookRotation(-dir);
        
        // Status text
        // [COMMENTED OUT OLD UI]
        // var statusGO = new GameObject("StatusText");
        // statusGO.transform.SetParent(gameUIPanel.transform, false);
        // statusText = statusGO.AddComponent<TextMesh>();
        // statusText.fontSize = 48;
        // statusText.characterSize = 0.015f;
        // statusText.anchor = TextAnchor.MiddleCenter;
        // statusText.color = Color.white;
        
        // Create Background Panel
        var panelObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        panelObj.name = "Background";
        panelObj.transform.SetParent(gameUIPanel.transform, false);
        panelObj.transform.localScale = new Vector3(3.0f, 2.0f, 1f);
        var renderer = panelObj.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.shader = Shader.Find("Unlit/Color");
            renderer.material.color = new Color(0, 0, 0, 0.8f);
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

        // Create text elements
        scoreText = CreateText("ScoreText", new Vector3(0, 0.6f, -0.05f), 300, Color.yellow);
        scoreText.text = "0 - 0";

        infoText = CreateText("InfoText", new Vector3(0, 0.2f, -0.05f), 200, Color.green);
        infoText.text = "Passthrough Mode";

        statusText = CreateText("StatusText", new Vector3(0, -0.2f, -0.05f), 150, Color.white);

        controlsText = CreateText("ControlsText", new Vector3(0, -0.6f, -0.05f), 100, Color.cyan);
        
        UpdateInstructions();
    }
    
    private void UpdateInstructions()
    {
        if (statusText == null) return;
        
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                statusText.text = "TABLE ADJUST";
                if (controlsText) controlsText.text = "Right Stick X: Rotate\nLeft Stick Y: Height\nB/Y: Switch Racket Hand\nA/X: Confirm";
                break;
            case GamePhase.BallPosition:
                statusText.text = "BALL POSITION";
                if (controlsText) controlsText.text = "A/X: Toggle Adjust Mode\nLeft Stick: Move (X/Z)\nRight Stick Y: Height\nHit ball to start!";
                break;
            case GamePhase.Playing:
                statusText.text = "PLAYING";
                if (controlsText) controlsText.text = ballNeedsRespawn ? "GRIP: Respawn Ball\nMENU: Pause" : "MENU: Pause";
                break;
            case GamePhase.BallGrounded:
                statusText.text = "BALL OUT OF BOUNDS";
                if (controlsText) controlsText.text = "GRIP: Respawn Ball\nMENU: Pause";
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
            
            // Client: apply networked state immediately after spawning
#if FUSION2
            if (Object != null && !Object.HasStateAuthority && spawnedTable != null)
            {
                ApplyTableState();
                Debug.Log("[Passthrough] Client: Applied networked table state after spawn");
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
