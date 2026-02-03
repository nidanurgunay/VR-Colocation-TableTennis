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
    private bool racketsVisible = true;
    private bool ballNeedsRespawn = false;
    private int lastBallAuthority = 1; // Track last authority to swap on respawn (1=Host, 2=Client)
    private int currentRound = 0; // Tracks the current round number

    // // Racket offset/rotation settings (matching VR scene's ControllerRacket for consistency)
    // private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f);
    // private Vector3 racketRotation = new Vector3(-51f, 240f, 43f);
    // private float racketScale = 10f;

    // References
    private GameObject spawnedTable;
    private GameObject spawnedBall;
    private GameObject leftRacket;
    private GameObject rightRacket;
    private GameObject gameUIPanel;
    private TextMesh roleText;          // Shows Client/Host
    private TextMesh phaseText;         // Shows current game phase
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
        // Find all NetworkObjects with cube-like names
        var allNetworkObjects = FindObjectsOfType<NetworkObject>();
        var cubesToDespawn = new System.Collections.Generic.List<NetworkObject>();

        foreach (var netObj in allNetworkObjects)
        {
            if (netObj == null || netObj.gameObject == null) continue;
            
            string lowerName = netObj.gameObject.name.ToLower();
            if (lowerName.Contains("cube") || lowerName.Contains("networked") || lowerName.Contains("grabbable"))
            {
                cubesToDespawn.Add(netObj);
            }
        }

        Debug.Log($"{LOG_TAG} DespawnAllCubes: Found {cubesToDespawn.Count} networked objects to despawn");

        if (cubesToDespawn.Count == 0)
        {
            Debug.Log($"{LOG_TAG} DespawnAllCubes: No networked objects to despawn");
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
                foreach (var cube in cubesToDespawn)
                {
                    if (cube != null && cube.IsValid)
                    {
                        cube.transform.SetParent(null);
                        Debug.Log($"{LOG_TAG} DespawnAllCubes: Despawning networked object {cube.Id}");
                        Runner.Despawn(cube);
                    }
                }
            }
        }

        // Start verification coroutine to ensure objects are actually destroyed
        StartCoroutine(VerifyAndForceDespawnCubes());
#else
        // Non-networked: just destroy
        var allNetworkObjects = FindObjectsOfType<NetworkObject>();
        foreach (var netObj in allNetworkObjects)
        {
            if (netObj == null || netObj.gameObject == null) continue;

            string lowerName = netObj.gameObject.name.ToLower();
            if (lowerName.Contains("cube") || lowerName.Contains("networked") || lowerName.Contains("grabbable"))
            {
                netObj.transform.SetParent(null);
                Destroy(netObj.gameObject);
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

        // Find remaining NetworkObjects with cube-like names
        var allNetworkObjects = FindObjectsOfType<NetworkObject>();
        var remainingCubes = new System.Collections.Generic.List<NetworkObject>();

        foreach (var netObj in allNetworkObjects)
        {
            if (netObj == null || netObj.gameObject == null) continue;

            string lowerName = netObj.gameObject.name.ToLower();
            if (lowerName.Contains("cube") || lowerName.Contains("networked") || lowerName.Contains("grabbable"))
            {
                remainingCubes.Add(netObj);
            }
        }

        if (remainingCubes.Count > 0)
        {
            Debug.LogWarning($"{LOG_TAG} VerifyAndForceDespawnCubes: {remainingCubes.Count} networked objects still exist after despawn! Force destroying...");

            foreach (var cube in remainingCubes)
            {
                if (cube != null && cube.gameObject != null)
                {
                    Debug.Log($"{LOG_TAG} VerifyAndForceDespawnCubes: Force destroying networked object: {cube.gameObject.name}");
                    cube.transform.SetParent(null);
                    if (cube.IsValid)
                    {
                        Runner.Despawn(cube);
                    }
                    else
                    {
                        Destroy(cube.gameObject);
                    }
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
                if (lowerName.Contains("cube") || lowerName.Contains("networked") || lowerName.Contains("grabbable"))
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
            if (!keepTableActive)
            {
                // Destroy table completely when returning to main menu
                Destroy(spawnedTable);
                spawnedTable = null;
                Debug.Log($"{LOG_TAG} StopGame Table destroyed");
            }
            else
            {
                Debug.Log($"{LOG_TAG} StopGame Table kept active for VR scene transition");
            }
        }

        // Destroy rackets
        if (leftRacket != null) { Destroy(leftRacket); leftRacket = null; }
        if (rightRacket != null) { Destroy(rightRacket); rightRacket = null; }

        // Cleanup UI
        if (gameUIPanel != null) Destroy(gameUIPanel);

        // Clear UI references
        roleText = null;
        roundText = null;
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

        // Apply per-axis dead zones to prevent cross-axis bleed
        float deadZone = 0.15f;
        float leftX = Mathf.Abs(leftStick.x) > deadZone ? leftStick.x : 0f;
        float rightY = Mathf.Abs(rightStick.y) > deadZone ? rightStick.y : 0f;

        if (leftX == 0f && rightY == 0f) return;

        // Right stick Y = Height, Left stick X = Rotation
        float rotationDelta = leftX * TableRotateSpeed * Time.deltaTime;
        float heightDelta = rightY * TableMoveSpeed * Time.deltaTime;

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

        // Calculate spawn position: 0.5m above the world-space midpoint of the two anchors
        var anchors = mainManager?.GetCurrentAnchors();
        Vector3 anchorMidpoint;
        if (anchors != null && anchors.Count >= 2)
        {
            anchorMidpoint = (anchors[0].transform.position + anchors[1].transform.position) / 2f;
        }
        else
        {
            // Fallback to table world position if anchors unavailable
            anchorMidpoint = spawnedTable.transform.position;
            Debug.LogWarning($"{LOG_TAG} SpawnBall Not enough anchors, falling back to table position: {anchorMidpoint}");
        }

        Vector3 spawnPos = anchorMidpoint + Vector3.up * 0.5f;
        Debug.Log($"{LOG_TAG} SpawnBall FINAL - Anchor midpoint: {anchorMidpoint}, Ball spawn (midpoint + 0.5 up): {spawnPos}");

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
                    // Set authority to Player 1 (host) on first spawn only
                    // Subsequent rounds: authority is set by NetworkedBall.OnGroundHit
                    if (networkedBall.CurrentAuthority == 0)
                    {
                        networkedBall.CurrentAuthority = 1;
                        Debug.Log($"{LOG_TAG} SpawnBall First spawn - authority set to Player 1 (Host)");
                    }
                    else
                    {
                        Debug.Log($"{LOG_TAG} SpawnBall Authority already set to Player {networkedBall.CurrentAuthority}");
                    }

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

        if (currentRound == 0) currentRound = 1;
        currentPhase = GamePhase.Playing;
        Time.timeScale = 0.5f;
        Debug.Log($"{LOG_TAG} OnBallHit by player {playerNumber} - Round: {currentRound}, Phase: Playing, TimeScale: 0.5");

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
        currentRound++;
        currentPhase = GamePhase.BallPosition;
        Time.timeScale = 1.0f;
#if FUSION2
        // Notify peer of phase change
        if (Object != null && Object.HasStateAuthority)
        {
            RPC_NotifyPhaseChange(GamePhase.BallPosition);
        }
#endif
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
        Time.timeScale = 1.0f;

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

        // Reset authority and round tracking for fresh game
        lastBallAuthority = 1;
        currentRound = 0;

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

        // Restore the main aligned GUI panel
        if (mainManager != null)
        {
            mainManager.ShowMainGUIPanel();
        }

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

        // Role: Client/Host (green) - at top
        roleText = CreateText("RoleText", new Vector3(0, 0.85f, -0.05f), 180, Color.green);
#if FUSION2
        roleText.text = Object != null && Object.HasStateAuthority ? "HOST" : "CLIENT";
#else
        roleText.text = "SOLO";
#endif

        // Game Phase (white)
        phaseText = CreateText("PhaseText", new Vector3(0, 0.2f, -0.05f), 200, Color.white);

        // Controller Info (cyan, smaller)
        controlsText = CreateText("ControlsText", new Vector3(0, -0.65f, -0.05f), 150, Color.cyan);

        UpdateInstructions();
    }

    private void UpdateInstructions()
    {
        if (phaseText == null) return;

        // Update phase display
        phaseText.text = currentPhase.GetDisplayName();

        // Update controls text based on phase
        if (controlsText == null) return;

        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
#if FUSION2
                controlsText.text =
                    "[Right Stick Y] Adjust Height\n" +
                    "[Left Stick X] Rotate Table\n" +
                    "[A/X] Confirm & Spawn Ball\n" +
                    "[B/Y] Get Bat";
#else
                controlsText.text =
                    "[Right Stick Y] Adjust Height\n" +
                    "[Left Stick X] Rotate Table\n" +
                    "[A/X] Confirm\n" +
                    "[B/Y] Get Bat";
#endif
                break;

            case GamePhase.BallPosition:
                if (spawnedBall == null)
                {
                    controlsText.text = "[GRIP] Spawn Ball";
                }
                else
                {
                    controlsText.text =
                        "Grab ball to reposition\n" +
                        "[A/X] Reset Ball Position\n" +
                        "Hit ball with racket to start!";
                }
                break;

            case GamePhase.Playing:
                controlsText.text = "[A/X] Reset Ball";
                break;

            case GamePhase.BallGrounded:
                controlsText.text = "Transitioning...";
                break;

            case GamePhase.Idle:
                controlsText.text = "";
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
