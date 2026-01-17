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
    [SerializeField] private float defaultTableHeight = 0f; // 0 for passthrough (touch ground), 0.76f for VR scene
    
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
        Playing         // Game in progress
    }
    
    // State
    private GamePhase currentPhase = GamePhase.Idle;
    private bool isActive = false;
    private bool isGameMenuOpen = false;
    private bool racketsVisible = true;
    
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
        Debug.Log($"{LOG_TAG} Start Passthrough Game clicked");
        
        // Get current state from main manager
        if (mainManager != null)
        {
            _localizedAnchor = mainManager.GetLocalizedAnchor();
            Debug.Log($"{LOG_TAG} Start Passthrough _localizedAnchor: {_localizedAnchor}");
        }
        
        // DEBUG MODE: Skip alignment checks but still require anchors to exist
        if (skipAlignmentForDebug)
        {
            Debug.Log($"{LOG_TAG} DEBUG MODE: Skipping client alignment check but requiring anchors!");
        }
        
        // Check if aligned first (skip this check in debug mode)
        if (!skipAlignmentForDebug && (_localizedAnchor == null || !_localizedAnchor.Localized))
        {
            Debug.Log($"{LOG_TAG} Please complete alignment first");
            return;
        }
        
        // Check if we have both anchors (skip this check in debug mode)
        var currentAnchors = mainManager?.GetCurrentAnchors();
        if (!skipAlignmentForDebug && (currentAnchors == null || currentAnchors.Count < 2))
        {
            Debug.Log($"{LOG_TAG} Need 2 anchors for passthrough mode");
            return;
        }

#if FUSION2
        if (Runner == null || !Runner.IsRunning)
        {
            Debug.Log($"{LOG_TAG} Network not ready. Please wait...");
            return;
        }
        
        // Check if both devices are aligned (skip in debug mode)
        var currentState = mainManager.GetCurrentState();
        bool bothDevicesAligned = currentState == AnchorGUIManager_AutoAlignment.ColocationState.ClientAligned || 
                                  currentState == AnchorGUIManager_AutoAlignment.ColocationState.HostAligned ||
                                  currentState == AnchorGUIManager_AutoAlignment.ColocationState.Done;
        if (!skipAlignmentForDebug && !bothDevicesAligned)
        {
            Debug.Log($"{LOG_TAG} Waiting for both devices to be aligned...");
            return;
        }

        // Either player can initiate
        if (Object.HasStateAuthority)
        {
            Debug.Log($"{LOG_TAG} Starting passthrough game...");
            StartPassthroughGame();
            RPC_StartPassthroughGame();
        }
        else
        {
            Debug.Log($"{LOG_TAG} Requesting passthrough game...");
            RPC_RequestPassthroughGame();
        }
#else
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
    public void StopGame()
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
            spawnedTable.SetActive(false);
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
        
        Debug.Log($"{LOG_TAG} Game stopped");
    }
    
    private void Update()
    {
        if (!isActive) return;
        
        HandleInput();
    }
    
// #if FUSION2
//     public override void FixedUpdateNetwork()
//     {
//         if (!NetworkedGameActive) return;
        
//         // Find table if not set
//         if (spawnedTable == null)
//         {
//             spawnedTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
//             var currentAnchors = mainManager?.GetCurrentAnchors();
//             if (spawnedTable != null && currentAnchors != null && currentAnchors.Count > 0)
//             {
//                 spawnedTable.transform.SetParent(currentAnchors[0].transform, worldPositionStays: false);
//             }
//         }
        
//         if (spawnedTable != null)
//         {
//             ApplyTableState();
//         }
//     }
// #endif
    
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
                // Game in progress
                break;
        }
    }
    
    private void HandleTableAdjust()
    {
        if (spawnedTable == null) return;
        
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        bool hasInput = Mathf.Abs(rightStick.x) > 0.05f || Mathf.Abs(rightStick.y) > 0.05f;
        if (!hasInput) return;
        
        float rotationDelta = rightStick.x * TableRotateSpeed * Time.deltaTime;
        float heightDelta = rightStick.y * TableMoveSpeed * Time.deltaTime;
        
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
            SpawnBall();
        }
        
        // A/X held + thumbstick adjusts ball position
        bool axHeld = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                     OVRInput.Get(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        
        if (spawnedBall != null && axHeld)
        {
            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
            if (Mathf.Abs(stick.x) > 0.1f || Mathf.Abs(stick.y) > 0.1f)
            {
                Vector3 movement = new Vector3(stick.x, stick.y, 0) * TableMoveSpeed * Time.deltaTime;
#if FUSION2
                if (Object.HasStateAuthority)
                {
                    spawnedBall.transform.position += movement;
                }
                else
                {
                    RPC_RequestBallMove(movement);
                }
#else
                spawnedBall.transform.position += movement;
#endif
            }
        }
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
        var anchors = mainManager?.GetCurrentAnchors();
        if (anchors == null || anchors.Count < 2) return;
        Debug.Log($"{LOG_TAG} Table spawned anchors {anchors}");
        Transform primary = anchors[0].transform;
        Transform secondary = anchors[1].transform;

        // CLEANUP: Remove any old tables that were children of preserved anchors from VR scene
        foreach (var anchor in anchors)
        {
            foreach (Transform child in anchor.transform)
            {
                if (child.name.Contains("PingPongTable") || child.name.Contains("pingpongtable") ||
                    child.name.Contains("pingpong") || child.name.Contains("PingPong") ||
                    child.name.Contains("TableTennis"))
                {
                    Debug.Log($"{LOG_TAG} Removing old table '{child.name}' from preserved anchor");
                    Destroy(child.gameObject);
                }
            }
        }

        // Find or spawn table
        if (spawnedTable == null)
        {
            spawnedTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
        }

        if (spawnedTable == null && TablePrefab != null)
        {
            spawnedTable = Instantiate(TablePrefab);
        }

        float yRot = 0f;

        if (spawnedTable != null)
        {
            // USE SAME LOGIC AS VR TableTennisManager for consistent table placement
            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Table found/instantiated: {spawnedTable.name}");
            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Initial table world pos: {spawnedTable.transform.position}, local pos: {spawnedTable.transform.localPosition}");
            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Primary anchor world pos: {primary.position}, Secondary anchor world pos: {secondary.position}");

            // Parent table to primary anchor - this is critical for colocation!
            // When parented, the table will automatically stay in correct position
            // even if the camera rig is adjusted
            spawnedTable.transform.SetParent(primary, worldPositionStays: false);

            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) After parenting - table local pos: {spawnedTable.transform.localPosition}, world pos: {spawnedTable.transform.position}");

            // Calculate LOCAL position (relative to primary anchor)
            Vector3 secondaryLocalPos = primary.InverseTransformPoint(secondary.position);
            Vector3 midpoint = secondaryLocalPos / 2f;

            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Secondary local pos (relative to primary): {secondaryLocalPos}");
            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Calculated midpoint: {midpoint}");

            // Calculate rotation to face from primary to secondary (table long axis)
            Vector3 directionToSecondary = secondaryLocalPos;
            directionToSecondary.y = 0; // Keep horizontal

            if (directionToSecondary.sqrMagnitude > 0.01f)
            {
                // Table Y rotation: face perpendicular to the anchor line (so players stand at each anchor)
                yRot = Mathf.Atan2(directionToSecondary.x, directionToSecondary.z) * Mathf.Rad2Deg;
                // Add 90° so table's LONG EDGE is along anchor line (players face each other across table)
                yRot += 90f;
            }
            else
            {
                yRot = tableYRotationOffset;
            }

            // RESTORED: Use original working logic - local Y relative to anchor (not world Y = 0)
            // This matches the VR TableTennisManager approach and the original PassthroughGameManager
            // For passthrough, defaultTableHeight should be 0 to touch ground
            Vector3 localTablePos = new Vector3(midpoint.x, defaultTableHeight, midpoint.z);

            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Using defaultTableHeight: {defaultTableHeight}");
            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) Final calculated local pos: {localTablePos}, Y rotation: {yRot}°");

            // Apply LOCAL position and rotation relative to anchor
            spawnedTable.transform.localPosition = localTablePos;
            spawnedTable.transform.localRotation = Quaternion.Euler(tableXRotationOffset, yRot, 0);

            Debug.Log($"{LOG_TAG} [AR TABLE DEBUG] (tablePosition) FINAL POSITION - LocalPos: {spawnedTable.transform.localPosition}, LocalRot: {spawnedTable.transform.localEulerAngles}, WorldPos: {spawnedTable.transform.position}");

            spawnedTable.SetActive(true);
        }
        else
        {
            Debug.LogWarning($"{LOG_TAG} No table prefab!");
            return;
        }

#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedTableYRotation = yRot;
            NetworkedTableHeight = defaultTableHeight;
            NetworkedGameActive = true;
        }
#endif

        Debug.Log($"{LOG_TAG} Table spawned at height {defaultTableHeight}");

        // Tag rackets in the spawned table for ControllerRacket to find
        if (spawnedTable != null)
        {
            foreach (Transform child in spawnedTable.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains("racket") || child.name.ToLower().Contains("paddle"))
                {
                    child.gameObject.tag = "Racket";
                    Debug.Log($"{LOG_TAG} RacketDebug: Tagged racket in table: {child.name}");
                }
            }
        }

        CreateGameUI();

        // Initialize ControllerRacket for single-hand passthrough mode
        InitializeControllerRacket();
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
        if (spawnedTable == null) return;
        
        Vector3 spawnPos = spawnedTable.transform.position + Vector3.up * 0.5f;
        
#if FUSION2
        if (Object.HasStateAuthority && Runner != null)
        {
            var ball = Runner.Spawn(BallPrefab, spawnPos, Quaternion.identity);
            if (ball != null)
            {
                spawnedBall = ball.gameObject;
                var networkedBall = ball.GetComponent<NetworkedBall>();
                if (networkedBall != null)
                {
                    networkedBall.EnterPositioningMode();
                }
                RPC_NotifyBallSpawned();
            }
        }
        else
        {
            RPC_RequestSpawnBall();
        }
#else
        // Non-networked fallback
        var ballPrefabObj = Resources.Load<GameObject>("Ball");
        if (ballPrefabObj != null)
        {
            spawnedBall = Instantiate(ballPrefabObj, spawnPos, Quaternion.identity);
        }
#endif
        
        Debug.Log($"{LOG_TAG} Ball spawned");
    }
    
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
        
        // Stop passthrough game
        StopGame();
        
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
                // [COMMENTED OUT OLD UI] statusText.text = "TABLE ADJUST\n\nRight Stick: Rotate/Height\nB/Y: Switch Racket Hand\nA/X: Confirm";
                statusText.text = "TABLE ADJUST";
                if (controlsText) controlsText.text = "Right Stick: Rotate/Height\nB/Y: Switch Racket Hand\nA/X: Confirm";
                break;
            case GamePhase.BallPosition:
                // [COMMENTED OUT OLD UI] statusText.text = "BALL POSITION\n\nGRIP: Spawn Ball\nA/X + Stick: Move Ball\nHit ball to play!";
                statusText.text = "BALL POSITION";
                if (controlsText) controlsText.text = "GRIP: Spawn Ball\nA/X + Stick: Move Ball\nHit ball to play!";
                break;
            case GamePhase.Playing:
                // [COMMENTED OUT OLD UI] statusText.text = "PLAYING\n\nMENU: Pause";
                statusText.text = "PLAYING";
                if (controlsText) controlsText.text = "MENU: Pause";
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
    private void RPC_RequestBallMove(Vector3 movement)
    {
        if (spawnedBall != null)
        {
            spawnedBall.transform.position += movement;
        }
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
