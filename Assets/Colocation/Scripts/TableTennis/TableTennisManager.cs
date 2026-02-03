using UnityEngine;
using Fusion;
using System.Collections;
using System.Linq;

/// <summary>
/// Manages the table tennis game setup and spawns the networked ball.
/// Attach to a GameObject in the TableTennis scene.
/// </summary>
public class TableTennisManager : NetworkBehaviour
{
    [Header("Shared Config (assign this OR individual prefabs below)")]
    [SerializeField] private TableTennisConfig sharedConfig;
    
    [Header("Prefabs (used if sharedConfig is not assigned)")]
    [SerializeField] private NetworkPrefabRef ballPrefab;
    [SerializeField] private GameObject racketPrefab; // Local prefab, not networked
    
    // Properties to get prefabs from config or direct assignment
    private NetworkPrefabRef BallPrefab => sharedConfig != null ? sharedConfig.BallPrefab : ballPrefab;
    private GameObject RacketPrefab => sharedConfig != null ? sharedConfig.RacketPrefab : racketPrefab;
    
    [Header("Table Placement (relative to anchor)")]
    [SerializeField] private Vector3 tablePositionOffset = Vector3.zero; // Position offset from anchor
    [SerializeField] private float defaultTableHeight = 0.76f; // Standard ping pong table height
    [SerializeField] private float tableXRotationOffset = 180f; // X rotation offset in degrees (180 to fix upside-down table)
    [SerializeField] private float tableYRotationOffset = 0f; // Y rotation offset in degrees
    
    // Ensure table X rotation is set correctly (in case serialized value was different)
    private void OnValidate()
    {
        // If value is 0 but table appears upside down, it should be 180h
        // Keep synchronized with passthrough mode's tableXRotationOffset
    }
    
    [Header("Runtime Adjustment Controls")]
    [SerializeField] private float moveSpeed = 2.0f; // Meters per second
    [SerializeField] private float rotateSpeed = 90f; // Degrees per second
    // Networked table position/rotation for syncing across players (LOCAL to anchor)
    [Networked] private Vector3 NetworkedTableLocalPosition { get; set; } // Position relative to primary anchor
    [Networked] private float NetworkedTableYRotation { get; set; }
    [Networked] private float NetworkedFloorOffset { get; set; } // Shared floor level adjustment
    [Networked] public NetworkBool GameStarted { get; set; } // True when game has started (ball spawned)
    
    // Networked anchor UUIDs to ensure host and client use the SAME anchors
    [Networked, Capacity(64)] private NetworkString<_64> NetworkedPrimaryAnchorUUID { get; set; }
    [Networked, Capacity(64)] private NetworkString<_64> NetworkedSecondaryAnchorUUID { get; set; }
    
    private bool _tableParented = false; // Track if table is parented to anchor

    // Game phase tracking (using shared GamePhase enum from GamePhaseDefinitions.cs)
    [Networked] public GamePhase CurrentPhase { get; private set; } = GamePhase.TableAdjust;

    // Racket state tracking - actual rackets are managed by ControllerRacket
    private bool isRacketOnRightHand = true; // Which hand holds the racket
    private bool racketsVisible = true; // Tracked for ray interactor logic
    
    /// <summary>
    /// Called when ball is hit by a racket - transitions to Playing phase
    /// </summary>
    public void OnBallHit()
    {
        if (CurrentPhase != GamePhase.Playing)
        {
            if (Object.HasStateAuthority)
            {
                CurrentPhase = GamePhase.Playing;
            }
            else
            {
                // Client requests host to start playing
                RPC_RequestStartPlaying();
            }
        }
    }

    /// <summary>
    /// Called by NetworkedBall when ball hits ground - triggers round end
    /// Authority is swapped by NetworkedBall, so transition to BallPosition for next serve
    /// </summary>
    public void OnBallGroundHit()
    {
        if (Object.HasStateAuthority)
        {
            CurrentPhase = GamePhase.BallPosition;
            RPC_NotifyPhaseChange(GamePhase.BallPosition);
        }
        else
        {
            RPC_RequestGroundHit();
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestGroundHit()
    {
        CurrentPhase = GamePhase.BallPosition;
        RPC_NotifyPhaseChange(GamePhase.BallPosition);
    }
    
    // Runtime adjustment state
    private GameObject tableRoot; // Reference to table object for positioning
    private OVRCameraRig cameraRig;
    
    [Header("Table Setup")]
    [SerializeField] private Transform tableTransform;
    [SerializeField] private Vector3 racket1Position = new Vector3(-0.3f, 0.1f, 0f); // On table surface, player 1 side
    [SerializeField] private Vector3 racket2Position = new Vector3(0.3f, 0.1f, 0f);  // On table surface, player 2 side
    [SerializeField] private Vector3 racketRotation = new Vector3(0f, 0f, 0f); // Handle up
    
    [Header("Ball Spawn")]
    [SerializeField] private Vector3 ballSpawnOffset = new Vector3(0f, 0.5f, 0f); // Above table center
    
    [Header("Game Menu")]
    [SerializeField] private string mainMenuSceneName = "Demo2_cube"; // Scene to return to for mode selection (passthrough)
    [SerializeField] private GameObject gameMenuUI; // Optional: UI panel for menu (spawned at runtime if null)
    
    // Game menu state
    private bool isGameMenuOpen = false;
    private GameObject runtimeMenuPanel; // Menu spawned at runtime
    private bool wasGripPressed = false; // Debounce for grip input
    private bool ballSpawnPending = false; // Prevent multiple spawn requests
    
    // References
    private NetworkedBall spawnedBall;
    private Transform sharedAnchor;
    private Transform secondaryAnchor; // For 2-point alignment after scene transition
    private AlignmentManager alignmentManager;
    
    public override void Spawned()
    {
        
        // Force tableXRotationOffset to 180 (in case Unity serialized an old value of 0)
        // This fixes upside-down table issue
        if (tableXRotationOffset == 0f)
        {
            tableXRotationOffset = 180f;
        }
        
        StartCoroutine(InitializeGame());
        
        // Enable rackets on both hands by default
        StartCoroutine(EnableRacketsDelayed());
    }
    
    private System.Collections.IEnumerator EnableRacketsDelayed()
    {
        yield return new WaitForSeconds(1.0f);
        EnableRackets();
    }
    
    private void Update()
    {
        HandlePhaseInput();
    }
    
    // Fusion calls this every network tick - apply networked table state
    public override void FixedUpdateNetwork()
    {
        // Apply networked table state to local table object
        ApplyNetworkedTableState();
    }
    
    /// <summary>
    /// Apply the networked position/rotation to the Environment (or table if no Environment)
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
        }
        
        // Apply LOCAL position relative to anchor (Y includes floor offset)
        tableRoot.transform.localPosition = new Vector3(
            NetworkedTableLocalPosition.x,
            NetworkedTableLocalPosition.y + NetworkedFloorOffset,
            NetworkedTableLocalPosition.z
        );

        // Apply LOCAL rotation relative to anchor
        // Only apply X rotation offset if tableRoot is the table directly (not Environment)
        bool isEnvironmentRoot = tableRoot.name.Contains("Environment");
        float xRotation = isEnvironmentRoot ? 0f : tableXRotationOffset;
        Quaternion localRotation = Quaternion.Euler(xRotation, NetworkedTableYRotation, 0);
        tableRoot.transform.localRotation = localRotation;
    }
    
    /// <summary>
    /// Handle all input based on current game phase
    /// </summary>
    private void HandlePhaseInput()
    {
        // Initialize references if needed
        if (tableRoot == null)
        {
            // Try to find Environment first (preferred), then fall back to table directly
            tableRoot = GameObject.Find("Environment") ??
                        GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") ??
                        GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
        }
        if (cameraRig == null)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
        }
        
        // MENU button - toggle game menu (check both controllers)
        bool menuButtonPressed = OVRInput.GetDown(OVRInput.Button.Start) || 
                                 OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch) ||
                                 OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.RTouch);
        if (menuButtonPressed)
        {
            ToggleGameMenu();
            return;
        }
        
        // If game menu is open, handle menu input instead of game input
        if (isGameMenuOpen)
        {
            HandleGameMenuInput();
            return;
        }
        
        // B/Y button - NO LONGER HANDLED HERE
        // Racket visibility is now managed by ControllerRacket component
        // This prevents dual-handling conflicts
        
        // GRIP button - only spawns ball in BallPosition phase (not TableAdjust, not after spawned)
        // Use grip axis (squeeze with middle fingers) - threshold of 0.8 (high to avoid accidental triggers)
        float leftGrip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger);
        float rightGrip = OVRInput.Get(OVRInput.Axis1D.SecondaryHandTrigger);
        bool gripPressed = leftGrip > 0.8f || rightGrip > 0.8f;
        
        if (gripPressed && !wasGripPressed && CurrentPhase == GamePhase.BallPosition && !isGameMenuOpen)
        {
            // GRIP spawns ball only in BallPosition phase (not after spawned)
            if (!GameStarted && !ballSpawnPending)
            {
                ballSpawnPending = true; // Prevent multiple spawn requests
                SpawnBallForPositioning();
            }
            // No hand switching - B/Y handles racket visibility via ControllerRacket
        }
        wasGripPressed = gripPressed;
        
        // A/X button detection for phase-specific handling
        bool aButtonPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool xButtonPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        bool axButtonHeld = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) || 
                            OVRInput.Get(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        
        // Handle phase-specific input
        switch (CurrentPhase)
        {
            case GamePhase.TableAdjust:
                // A/X OR GRIP confirms table position and advances to BallPosition
                if (aButtonPressed || xButtonPressed || (gripPressed && !wasGripPressed))
                {
                    AdvancePhase();
                }
                else
                {
                    HandleTableAdjustInput();
                }
                break;

            case GamePhase.BallPosition:
                HandleBallPositionInput(aButtonPressed || xButtonPressed, axButtonHeld);
                break;

            case GamePhase.Playing:
                // Playing phase - racket switching locked
                break;

            case GamePhase.BallGrounded:
                // This phase is no longer used - we transition directly to BallPosition
                // Keep for backward compatibility
                break;
        }
    }
    
    /// <summary>
    /// Toggle game menu visibility (MENU button)
    /// Menu shows options: Resume, Restart, Return to Main Menu
    /// </summary>
    private void ToggleGameMenu()
    {
        isGameMenuOpen = !isGameMenuOpen;
        
        if (isGameMenuOpen)
        {
            ShowGameMenu();
        }
        else
        {
            HideGameMenu();
        }
        
    }
    
    /// <summary>
    /// Show the in-game menu
    /// </summary>
    private void ShowGameMenu()
    {
        // Use assigned UI or create runtime menu
        if (gameMenuUI != null)
        {
            gameMenuUI.SetActive(true);
        }
        else
        {
            CreateRuntimeMenu();
        }
        
        // Enable ray interactors for menu interaction
        EnableRayInteractors();
        
        // Rackets managed by ControllerRacket - no need to hide here
        // ControllerRacket handles visibility via B/Y buttons
        
    }
    
    /// <summary>
    /// Hide the in-game menu
    /// </summary>
    private void HideGameMenu()
    {
        if (gameMenuUI != null)
        {
            gameMenuUI.SetActive(false);
        }
        
        if (runtimeMenuPanel != null)
        {
            runtimeMenuPanel.SetActive(false);
        }
        
        // Restore ray interactors based on visibility setting
        if (racketsVisible)
        {
            DisableRayInteractors();
            // Rackets managed by ControllerRacket - no need to show here
        }
    }
    
    /// <summary>
    /// Create a simple runtime menu panel (if no UI assigned)
    /// </summary>
    private void CreateRuntimeMenu()
    {
        if (runtimeMenuPanel != null)
        {
            runtimeMenuPanel.SetActive(true);
            PositionMenuInFrontOfUser();
            return;
        }
        
        // Create a simple world-space canvas menu
        runtimeMenuPanel = new GameObject("GameMenu");
        var canvas = runtimeMenuPanel.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        
        var canvasScaler = runtimeMenuPanel.AddComponent<UnityEngine.UI.CanvasScaler>();
        runtimeMenuPanel.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        
        // Set canvas size
        var rectTransform = runtimeMenuPanel.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(400, 300);
        runtimeMenuPanel.transform.localScale = Vector3.one * 0.001f; // Scale down for VR
        
        // Create background panel
        var panel = new GameObject("Panel");
        panel.transform.SetParent(runtimeMenuPanel.transform, false);
        var panelImage = panel.AddComponent<UnityEngine.UI.Image>();
        panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.sizeDelta = Vector2.zero;
        
        // Create title text
        var titleGO = new GameObject("Title");
        titleGO.transform.SetParent(panel.transform, false);
        var titleText = titleGO.AddComponent<TMPro.TextMeshProUGUI>();
        titleText.text = "GAME MENU";
        titleText.fontSize = 36;
        titleText.alignment = TMPro.TextAlignmentOptions.Center;
        titleText.color = Color.white;
        var titleRect = titleGO.GetComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0, 0.75f);
        titleRect.anchorMax = new Vector2(1, 1);
        titleRect.sizeDelta = Vector2.zero;
        
        // Create instructions text
        var instructGO = new GameObject("Instructions");
        instructGO.transform.SetParent(panel.transform, false);
        var instructText = instructGO.AddComponent<TMPro.TextMeshProUGUI>();
        instructText.text = "A/X: Resume\nB/Y: Restart\nGRIP: Passthrough\nMENU: Close";
        instructText.fontSize = 24;
        instructText.alignment = TMPro.TextAlignmentOptions.Center;
        instructText.color = Color.white;
        var instructRect = instructGO.GetComponent<RectTransform>();
        instructRect.anchorMin = new Vector2(0.1f, 0.1f);
        instructRect.anchorMax = new Vector2(0.9f, 0.7f);
        instructRect.sizeDelta = Vector2.zero;
        
        PositionMenuInFrontOfUser();
    }
    
    /// <summary>
    /// Position menu in front of user's head
    /// </summary>
    private void PositionMenuInFrontOfUser()
    {
        if (runtimeMenuPanel == null) return;
        
        if (cameraRig == null)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
        }
        
        if (cameraRig != null && cameraRig.centerEyeAnchor != null)
        {
            var head = cameraRig.centerEyeAnchor;
            runtimeMenuPanel.transform.position = head.position + head.forward * 0.5f;
            runtimeMenuPanel.transform.rotation = Quaternion.LookRotation(head.forward);
        }
    }
    
    /// <summary>
    /// Handle input while game menu is open
    /// </summary>
    private void HandleGameMenuInput()
    {
        // A/X: Resume
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch))
        {
            ToggleGameMenu(); // Close menu and resume
            return;
        }
        
        // B/Y: Restart
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch))
        {
            RestartGame();
            ToggleGameMenu();
            return;
        }
        
        // GRIP: Return to Passthrough Mode (same pattern as AnchorGUIManager)
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
            OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            ReturnToMainMenu();
            return;
        }
    }
    
    /// <summary>
    /// Restart the current game
    /// </summary>
    private void RestartGame()
    {
        
        // Destroy ball if exists
        if (spawnedBall != null && Object.HasStateAuthority)
        {
            Runner.Despawn(spawnedBall.Object);
            spawnedBall = null;
        }
        
        // Reset to TableAdjust phase
        if (Object.HasStateAuthority)
        {
            CurrentPhase = GamePhase.TableAdjust;
            GameStarted = false;
        }
        else
        {
            RPC_RequestRestart();
        }
        
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRestart()
    {
        
        if (spawnedBall != null)
        {
            Runner.Despawn(spawnedBall.Object);
            spawnedBall = null;
        }
        
        CurrentPhase = GamePhase.TableAdjust;
        GameStarted = false;
    }
    
    /// <summary>
    /// Return to main menu scene for game mode selection
    /// </summary>
    public void ReturnToMainMenu()
    {
        
        // Cleanup - ControllerRacket manages its own rackets
        if (runtimeMenuPanel != null) Destroy(runtimeMenuPanel);
        
        // Destroy ControllerRacketManager if it exists
        var racketManager = GameObject.Find("ControllerRacketManager");
        if (racketManager != null) Destroy(racketManager);
        
        // Re-enable ray interactors
        EnableRayInteractors();
        
        // Only host can load scenes in Fusion
        if (Object.HasStateAuthority && Runner != null)
        {
            // Get scene index for main menu
            int sceneIndex = UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath(mainMenuSceneName);
            
            if (sceneIndex < 0)
            {
                // Try with full path (Demo2_cube is in Demo2 folder)
                sceneIndex = UnityEngine.SceneManagement.SceneUtility.GetBuildIndexByScenePath($"Assets/Colocation/Scenes/Demo2/{mainMenuSceneName}.unity");
            }
            
            if (sceneIndex >= 0)
            {
                // Mark that we're returning from VR scene so passthrough mode auto-starts
                PlayerPrefs.SetInt("ReturnFromVRScene", 1);
                PlayerPrefs.Save();
                
                // Use Fusion's networked scene loading
                Runner.LoadScene(SceneRef.FromIndex(sceneIndex));
            }
            else
            {
                Debug.LogError($"[TableTennisManager] Scene '{mainMenuSceneName}' not found in Build Settings!");
                // Fallback to Unity's scene manager
                UnityEngine.SceneManagement.SceneManager.LoadScene(mainMenuSceneName);
            }
        }
        else
        {
            // Client requests host to load main menu
            RPC_RequestReturnToMainMenu();
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestReturnToMainMenu()
    {
        ReturnToMainMenu();
    }
    
    /// <summary>
    /// Toggle racket visibility (B/Y button)
    /// </summary>
    // ToggleRacketVisibility is no longer used - rackets managed by ControllerRacket
    // Kept for reference but not called
    private void ToggleRacketVisibility()
    {
        // Rackets are now managed by ControllerRacket component
        // This method is deprecated
    }
    
    /// <summary>
    /// Switch racket between left and right hand (GRIP button)
    /// Note: Main branch ControllerRacket uses B/Y to toggle racket visibility
    /// </summary>
    private void SwitchRacketHand()
    {
        isRacketOnRightHand = !isRacketOnRightHand;
        
        // Note: ControllerRacket uses B/Y buttons to toggle racket visibility
        // This method just tracks which hand is "active" for game logic
    }
    
    /// <summary>
    /// Handle table adjustment input (rotation and height only)
    /// RIGHT STICK X: Rotate table
    /// LEFT STICK Y: Adjust height
    /// </summary>
    private void HandleTableAdjustInput()
    {
        if (tableRoot == null)
        {
            // Try to find Environment first (preferred), then fall back to table directly
            tableRoot = GameObject.Find("Environment") ??
                        GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") ??
                        GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");
            if (tableRoot == null) return;
        }
        
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        // Right stick X for rotation, Left stick Y for height
        bool hasRotationInput = Mathf.Abs(rightStick.x) > 0.1f;
        bool hasHeightInput = Mathf.Abs(leftStick.y) > 0.1f;
        
        if (!hasRotationInput && !hasHeightInput)
        {
            return;
        }
        
        // Client sends adjustment requests to host via RPC
        if (!Object.HasStateAuthority)
        {
            // Right stick X: rotation
            if (hasRotationInput)
            {
                float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
                RPC_RequestTableRotate(rotation);
            }
            // Left stick Y: height
            if (hasHeightInput)
            {
                float verticalMove = leftStick.y * moveSpeed * Time.deltaTime;
                RPC_RequestFloorAdjust(verticalMove);
            }
            return;
        }
        
        // Host: directly adjust networked values
        // Right stick X: Rotate table
        if (hasRotationInput)
        {
            float rotation = rightStick.x * rotateSpeed * Time.deltaTime;
            NetworkedTableYRotation += rotation;
        }
        
        // Left stick Y: Adjust height
        if (hasHeightInput)
        {
            float verticalMove = leftStick.y * moveSpeed * Time.deltaTime;
            NetworkedFloorOffset += verticalMove;
        }
    }
    
    /// <summary>
    /// Handle ball position phase input (GRIP to spawn, A/X held + thumbstick to adjust ball position)
    /// NO table adjustment in this phase
    /// </summary>
    private void HandleBallPositionInput(bool axButtonPressed, bool axButtonHeld)
    {
        // NO table adjustment in BallPosition phase - table is locked
        
        // Ball is spawned - A/X HELD + thumbstick adjusts ball position
        if (GameStarted && axButtonHeld)
        {
            HandleBallPositionAdjust();
        }
        // When ball is hit with racket, OnBallHit() transitions to Playing
    }
    
    /// <summary>
    /// Handle ball position adjustment with A/X held + thumbstick
    /// RIGHT STICK: X/Y movement
    /// LEFT STICK Y: Z movement (forward/back)
    /// </summary>
    private void HandleBallPositionAdjust()
    {
        if (spawnedBall == null) return;
        
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        
        // Right stick: X/Y, Left stick Y: Z
        float xMove = Mathf.Abs(rightStick.x) > 0.1f ? rightStick.x : 0f;
        float yMove = Mathf.Abs(rightStick.y) > 0.1f ? rightStick.y : 0f;
        float zMove = Mathf.Abs(leftStick.y) > 0.1f ? leftStick.y : 0f;
        
        if (Mathf.Abs(xMove) > 0.01f || Mathf.Abs(yMove) > 0.01f || Mathf.Abs(zMove) > 0.01f)
        {
            Vector3 movement = new Vector3(xMove, yMove, zMove) * moveSpeed * Time.deltaTime;
            
            if (Object.HasStateAuthority)
            {
                // Host directly moves ball
                spawnedBall.transform.position += movement;
            }
            else
            {
                // Client requests host to move ball
                RPC_RequestBallMove(movement);
            }
        }
    }
    
    /// <summary>
    /// Spawn ball for positioning (not game start yet)
    /// </summary>
    private void SpawnBallForPositioning()
    {
        if (Object.HasStateAuthority)
        {
            SpawnBall();
            GameStarted = true; // Ball exists, but game hasn't truly started until hit
            ballSpawnPending = false; // Reset for host
            RPC_NotifyBallSpawned(); // Notify all clients
        }
        else
        {
            // Client requests host to spawn ball
            RPC_RequestGameStart();
        }
    }
    
    /// <summary>
    /// Advance to next game phase when A/X is pressed
    /// </summary>
    private void AdvancePhase()
    {
        if (!Object.HasStateAuthority)
        {
            // Client requests host to advance phase
            RPC_RequestAdvancePhase();
            return;
        }

        switch (CurrentPhase)
        {
            case GamePhase.TableAdjust:
                // Move to ball position phase
                CurrentPhase = GamePhase.BallPosition;
                // Notify all clients of phase change
                RPC_NotifyPhaseChange(GamePhase.BallPosition);
                break;

            case GamePhase.BallPosition:
                // No phase advance - hit ball to start
                break;

            case GamePhase.Playing:
                // No phase advance
                break;
        }
    }
    
    /// <summary>
    /// Enable racket on the dominant hand (right by default)
    /// Rackets are now managed by ControllerRacket component
    /// </summary>
    private void EnableRackets()
    {
        if (cameraRig == null)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
        }
        if (cameraRig == null)
        {
            return;
        }
        
        // Disable ray interactors
        DisableRayInteractors();
        
        racketsVisible = true;
        
        // Rackets are now managed entirely by ControllerRacket component
        // SetupControllerRackets() is called in Start() to create the manager
        // ControllerRacket handles B/Y for visibility and auto-enables right hand racket
        
    }
    
    /// <summary>
    /// Create a simple placeholder racket (handle + paddle head)
    /// </summary>
    private GameObject CreatePlaceholderRacket(string name)
    {
        var racket = new GameObject(name);
        
        // Handle (cylinder)
        var handle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        handle.transform.SetParent(racket.transform);
        handle.transform.localPosition = new Vector3(0, -0.08f, 0);
        handle.transform.localScale = new Vector3(0.025f, 0.06f, 0.025f);
        handle.GetComponent<Renderer>().material.color = new Color(0.4f, 0.2f, 0.1f); // Brown
        Destroy(handle.GetComponent<Collider>());
        
        // Paddle head (flattened sphere)
        var paddle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        paddle.transform.SetParent(racket.transform);
        paddle.transform.localPosition = new Vector3(0, 0.04f, 0);
        paddle.transform.localScale = new Vector3(0.12f, 0.01f, 0.14f);
        paddle.GetComponent<Renderer>().material.color = Color.red;
        paddle.tag = "Racket";
        
        // Add rigidbody for collision detection
        var rb = paddle.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        
        return racket;
    }
    
    /// <summary>
    /// Disable ray interactors on controllers
    /// </summary>
    private void DisableRayInteractors()
    {
        // Find and disable components with "Ray" or "Interactor" in name
        var allMonoBehaviours = FindObjectsOfType<MonoBehaviour>();
        foreach (var mb in allMonoBehaviours)
        {
            string typeName = mb.GetType().Name.ToLower();
            if (typeName.Contains("rayinteractor") || (typeName.Contains("ray") && typeName.Contains("interactor")))
            {
                mb.enabled = false;
            }
        }
        
        // Also disable line renderers used for rays
        var lineRenderers = FindObjectsOfType<LineRenderer>().Where(lr => 
            lr.gameObject.name.ToLower().Contains("ray") || 
            lr.gameObject.name.ToLower().Contains("pointer")).ToArray();
        foreach (var lr in lineRenderers)
        {
            lr.enabled = false;
        }
        
    }
    
    /// <summary>
    /// Re-enable ray interactors on controllers
    /// </summary>
    private void EnableRayInteractors()
    {
        // Find and enable components with "Ray" or "Interactor" in name
        var allMonoBehaviours = FindObjectsOfType<MonoBehaviour>(true);
        foreach (var mb in allMonoBehaviours)
        {
            string typeName = mb.GetType().Name.ToLower();
            if (typeName.Contains("rayinteractor") || (typeName.Contains("ray") && typeName.Contains("interactor")))
            {
                mb.enabled = true;
            }
        }
        
        // Also enable line renderers used for rays
        var lineRenderers = FindObjectsOfType<LineRenderer>(true).Where(lr => 
            lr.gameObject.name.ToLower().Contains("ray") || 
            lr.gameObject.name.ToLower().Contains("pointer")).ToArray();
        foreach (var lr in lineRenderers)
        {
            lr.enabled = true;
        }
    }
    
    /// <summary>
    /// Called when ball is hit - transition to Playing phase
    /// </summary>
    public void StartPlayingPhase()
    {
        CurrentPhase = GamePhase.Playing;
    }
    
    /// <summary>
    /// Exit game - return to main menu (legacy method, use ReturnToMainMenu instead)
    /// </summary>
    private void ExitGame()
    {
        ReturnToMainMenu();
    }
    
    // Legacy methods kept for compatibility
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
        ApplyNetworkedTableState(); // Apply immediately so host sees changes
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
    private void RPC_RequestFloorAdjust(float verticalMove)
    {
        NetworkedFloorOffset += verticalMove;
        ApplyNetworkedTableState(); // Apply immediately so host sees changes
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestAdvancePhase()
    {
        // Called by client to request phase advancement
        if (CurrentPhase == GamePhase.TableAdjust)
        {
            CurrentPhase = GamePhase.BallPosition;
            // Notify all clients of phase change
            RPC_NotifyPhaseChange(GamePhase.BallPosition);
        }
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyPhaseChange(GamePhase newPhase)
    {
        // Ensure all clients are synchronized to the new phase
        if (!Object.HasStateAuthority)
        {
            CurrentPhase = newPhase;
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestStartPlaying()
    {
        // Called by client when they hit the ball
        if (CurrentPhase != GamePhase.Playing)
        {
            CurrentPhase = GamePhase.Playing;
        }
    }
    
    private IEnumerator InitializeGame()
    {
        // Wait for anchor to be available
        yield return StartCoroutine(WaitForAnchor());
        
        // Place the table at the anchor position
        PlaceTableAtAnchor();
        
        // Setup controller-based rackets (replaces old grab system)
        SetupControllerRackets();
        
        // Ball is NOT auto-spawned here - user presses GRIP in BallPosition phase to spawn
    }
    
    /// <summary>
    /// Place the Environment BETWEEN the two anchors, parented to primary anchor.
    /// The table is at the center of the Environment, so moving Environment aligns table with anchor midpoint.
    /// This ensures consistent positioning for both host and client.
    /// </summary>
    private void PlaceTableAtAnchor()
    {
        if (sharedAnchor == null)
        {
            return;
        }

        // Find the Environment object FIRST - this contains the table and room (walls, floor, etc.)
        GameObject environmentRoot = GameObject.Find("Environment");

        // Find the PingPongTable (used for reference and verification)
        GameObject pingPongTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable")
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("TableTennis");

        // Determine which object to use as tableRoot (the one we'll parent to anchor and position)
        if (environmentRoot != null)
        {
            // PREFERRED: Use Environment as root (contains table + room)
            tableRoot = environmentRoot;
        }
        else if (pingPongTable != null)
        {
            // FALLBACK: Use PingPongTable directly if no Environment found
            tableRoot = pingPongTable;
        }
        else
        {
            tableRoot = null;
            Debug.LogError($"[TableTennisManager] PlaceTableAtAnchor CRITICAL: Neither Environment nor PingPongTable found in scene!");
        }

        // CLEANUP: Remove OLD Environment/tables that were children of preserved anchors from previous scene
        // BUT keep the current scene's tableRoot
        var preservedAnchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in preservedAnchors)
        {
            // Remove any child Environment or tables from the anchor (except our current tableRoot)
            foreach (Transform child in anchor.transform)
            {
                if (child.gameObject == tableRoot) continue; // Don't destroy current scene's root

                if (child.name.Contains("Environment") || child.name.Contains("PingPongTable") ||
                    child.name.Contains("pingpongtable") || child.name.Contains("pingpong") ||
                    child.name.Contains("PingPong") || child.name.Contains("TableTennis"))
                {
                    Destroy(child.gameObject);
                }
            }
        }

        // Fallback to assigned tableTransform if needed
        if (tableRoot == null && tableTransform != null)
        {
            tableRoot = tableTransform.gameObject;
        }
        
        if (tableRoot != null)
        {
            // CRITICAL FIX: Ensure table is active (might be disabled by PassthroughGameManager.StopGame)
            if (!tableRoot.activeSelf)
            {
                tableRoot.SetActive(true);
            }

            // Parent Environment/table to primary anchor - this is critical for colocation!
            // When parented, the Environment (and its children: table, room, etc.) will automatically
            // stay in correct position even if the camera rig is adjusted
            tableRoot.transform.SetParent(sharedAnchor, worldPositionStays: false);
            _tableParented = true;

            // Calculate LOCAL position (relative to primary anchor)
            // NOTE: If tableRoot is Environment, the table should be at (0,0,0) within Environment.
            // So positioning Environment positions the table at Environment's origin.
            Vector3 localEnvPos;  // Position of Environment (which centers the table)
            float envYRotation;

            if (secondaryAnchor != null)
            {
                // PLACE ENVIRONMENT (AND TABLE) BETWEEN TWO ANCHORS
                // Get secondary anchor position in primary anchor's local space
                Vector3 secondaryLocalPos = sharedAnchor.InverseTransformPoint(secondaryAnchor.position);

                // Midpoint between primary (0,0,0 in local) and secondary
                Vector3 midpoint = secondaryLocalPos / 2f;

                // Calculate rotation to face from primary to secondary (table long axis)
                Vector3 directionToSecondary = secondaryLocalPos;
                directionToSecondary.y = 0; // Keep horizontal

                if (directionToSecondary.sqrMagnitude > 0.01f)
                {
                    // Environment Y rotation: face perpendicular to the anchor line (so players stand at each anchor)
                    envYRotation = Mathf.Atan2(directionToSecondary.x, directionToSecondary.z) * Mathf.Rad2Deg;
                    // Add 90° so table's LONG EDGE is along anchor line (players face each other across table)
                    envYRotation += 90f;
                }
                else
                {
                    envYRotation = tableYRotationOffset;
                }

                // Position Environment at midpoint, at table height
                // This centers the table (which is at Environment's origin) between the anchors
                localEnvPos = new Vector3(midpoint.x, defaultTableHeight, midpoint.z) + tablePositionOffset;
            }
            else
            {
                // SINGLE ANCHOR: Use offset from primary anchor
                localEnvPos = new Vector3(tablePositionOffset.x, defaultTableHeight, tablePositionOffset.z);
                envYRotation = tableYRotationOffset;
            }
            
            // Host initializes networked values (LOCAL coordinates)
            if (Object.HasStateAuthority)
            {
                NetworkedTableLocalPosition = localEnvPos;
                NetworkedTableYRotation = envYRotation;
                NetworkedFloorOffset = 0f;
            }

            // Apply LOCAL position and rotation relative to anchor
            tableRoot.transform.localPosition = localEnvPos;

            // Apply X rotation offset ONLY if positioning table directly (not Environment)
            // Environment already has table oriented correctly, so no X flip needed
            bool isEnvironmentRoot = tableRoot.name.Contains("Environment");
            float xRotation = isEnvironmentRoot ? 0f : tableXRotationOffset;
            tableRoot.transform.localRotation = Quaternion.Euler(xRotation, envYRotation, 0);

            string rootType = isEnvironmentRoot ? "Environment (room + table)" : "Table only";
        }
    }
    
    /// <summary>
    /// Setup ControllerRacket component to show racket on controllers
    /// </summary>
    private void SetupControllerRackets()
    {
        // Check if ControllerRacket already exists in scene (on any object)
        var existingControllerRacket = FindObjectOfType<ControllerRacket>();
        if (existingControllerRacket != null)
        {
            return;
        }
        
        // ControllerRacket will auto-find rackets in the scene
        // Just create the manager if it doesn't exist
        var existingManager = GameObject.Find("ControllerRacketManager");
        if (existingManager == null)
        {
            var manager = new GameObject("ControllerRacketManager");
            // Use string-based AddComponent to avoid compile order issues
            var component = manager.AddComponent(System.Type.GetType("ControllerRacket"));
            if (component == null)
            {
                // Fallback: try direct add
                manager.AddComponent<ControllerRacket>();
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
            }
        }

        // HOST: Find anchors and share their UUIDs with clients
        // CLIENT: Wait for host's UUIDs, then find matching anchors
        if (Object.HasStateAuthority)
        {
            // HOST: Search for anchors and set networked UUIDs
            while ((sharedAnchor == null || secondaryAnchor == null) && attempts < 50)
            {
                var anchors = FindObjectsOfType<OVRSpatialAnchor>(true);
                
                foreach (var anchor in anchors)
                {
                    if (anchor != null) // Removed Localized check for preserved anchors from scene transition
                    {
                        
                        if (sharedAnchor == null)
                        {
                            sharedAnchor = anchor.transform;
                            primaryOVRAnchor = anchor;
                            NetworkedPrimaryAnchorUUID = anchor.Uuid.ToString();
                        }
                        else if (anchor.transform != sharedAnchor && secondaryAnchor == null)
                        {
                            secondaryAnchor = anchor.transform;
                            secondaryOVRAnchor = anchor;
                            NetworkedSecondaryAnchorUUID = anchor.Uuid.ToString();
                        }
                    }
                }
                
                attempts++;
                yield return new WaitForSeconds(0.2f);
            }
        }
        else
        {
            // CLIENT: Wait for host to set the UUIDs, then find matching anchors
            
            while (string.IsNullOrEmpty(NetworkedPrimaryAnchorUUID.ToString()) && attempts < 50)
            {
                attempts++;
                yield return new WaitForSeconds(0.2f);
            }
            
            string primaryUUID = NetworkedPrimaryAnchorUUID.ToString();
            string secondaryUUID = NetworkedSecondaryAnchorUUID.ToString();
            
            // Now find the matching anchors by UUID
            attempts = 0;
            while ((sharedAnchor == null || secondaryAnchor == null) && attempts < 50)
            {
                var anchors = FindObjectsOfType<OVRSpatialAnchor>(true);
                
                foreach (var anchor in anchors)
                {
                    if (anchor != null) // Removed Localized check for preserved anchors from scene transition
                    {
                        string anchorUUID = anchor.Uuid.ToString();
                        
                        if (sharedAnchor == null && anchorUUID == primaryUUID)
                        {
                            sharedAnchor = anchor.transform;
                            primaryOVRAnchor = anchor;
                        }
                        else if (secondaryAnchor == null && anchorUUID == secondaryUUID)
                        {
                            secondaryAnchor = anchor.transform;
                            secondaryOVRAnchor = anchor;
                        }
                    }
                }
                
                attempts++;
                yield return new WaitForSeconds(0.2f);
            }
        }
        
        if (sharedAnchor == null)
        {
            
            // Use table as fallback reference
            if (tableTransform != null)
            {
                sharedAnchor = tableTransform;
            }
        }
        else
        {
            // CRITICAL: Re-align the camera rig to the preserved anchors!
            // Without this, each headset's OVRCameraRig starts at default (0,0,0) and objects appear misaligned.
            
            if (primaryOVRAnchor != null && secondaryOVRAnchor != null)
            {
                alignmentManager.AlignUserToTwoAnchors(primaryOVRAnchor, secondaryOVRAnchor);
            }
            else if (primaryOVRAnchor != null)
            {
                alignmentManager.AlignUserToAnchor(primaryOVRAnchor);
            }
            
            // Wait for alignment to complete before placing table
            yield return new WaitForSeconds(1.0f);
        }
        
        // SUMMARY: Log final anchor positions before table placement
        if (sharedAnchor != null)
        {
            if (secondaryAnchor != null)
            {
                Vector3 distance = secondaryAnchor.position - sharedAnchor.position;
            }
        }
    }
    
    private void SpawnBall()
    {
        if (BallPrefab == default)
        {
            Debug.LogError($"[TableTennisManager] SpawnBall Ball prefab not assigned");
            return;
        }

        if (Runner == null)
        {
            Debug.LogError($"[TableTennisManager] SpawnBall Runner is null! Cannot spawn ball");
            return;
        }

        Vector3 spawnPosition = Vector3.zero;

        // BEST: Use anchor + networked local position (most reliable)
        if (sharedAnchor != null)
        {
            // Calculate table world position from anchor + networked local position
            Vector3 tableLocalPos = NetworkedTableLocalPosition;
            Vector3 tableWorldPos = sharedAnchor.TransformPoint(tableLocalPos);

            // Try to get accurate table surface height from renderer bounds
            Vector3 tableSurfacePos = tableWorldPos;
            if (tableRoot != null)
            {
                Renderer tableRenderer = tableRoot.GetComponentInChildren<Renderer>();
                if (tableRenderer != null)
                {
                    // Use bounds to find the TOP of the table (max Y of mesh bounds)
                    // Bounds are already in world space
                    Bounds bounds = tableRenderer.bounds;
                    tableSurfacePos = bounds.center;
                    tableSurfacePos.y = bounds.max.y; // Top of the table
                }
                else
                {
                    // Fallback: add default table height to calculated world position
                    tableSurfacePos.y = tableWorldPos.y + defaultTableHeight;
                }
            }

            spawnPosition = tableSurfacePos + Vector3.up * 0.75f;
        }
        // FALLBACK: Use tableRoot if anchor not available
        else if (tableRoot != null)
        {
            // Calculate world position properly - tableRoot is parented to anchor
            Vector3 tableSurfacePos;
            Renderer tableRenderer = tableRoot.GetComponentInChildren<Renderer>();
            if (tableRenderer != null)
            {
                // Use bounds to find the TOP of the table (max Y of mesh bounds)
                Bounds bounds = tableRenderer.bounds;
                tableSurfacePos = bounds.center;
                tableSurfacePos.y = bounds.max.y;
            }
            else
            {
                // Fallback: Calculate world position from parent transform
                Transform anchorTransform = tableRoot.transform.parent;
                if (anchorTransform != null)
                {
                    tableSurfacePos = anchorTransform.TransformPoint(tableRoot.transform.localPosition);
                    tableSurfacePos.y += defaultTableHeight;
                }
                else
                {
                    // No parent - use world position directly
                    tableSurfacePos = tableRoot.transform.position + Vector3.up * defaultTableHeight;
                }
            }
            spawnPosition = tableSurfacePos + Vector3.up * 0.75f;
        }
        else if (tableTransform != null)
        {
            spawnPosition = tableTransform.TransformPoint(ballSpawnOffset);
        }
        else
        {
            // Last resort: spawn in front of head
            var head = FindObjectOfType<OVRCameraRig>()?.centerEyeAnchor;
            if (head != null)
            {
                spawnPosition = head.position + head.forward * 0.5f;
            }
        }


        var ballObj = Runner.Spawn(
            BallPrefab,
            spawnPosition,
            Quaternion.identity,
            Object.InputAuthority
        );

        if (ballObj != null)
        {
            spawnedBall = ballObj.GetComponent<NetworkedBall>();
            // Stay in BallPosition phase - transition to Playing when ball is hit
        }
        else
        {
            Debug.LogError($"[TableTennisManager] SpawnBall Runner.Spawn returned null!");
        }
    }
    
    /// <summary>
    /// Respawn ball at serve position after ground hit
    /// </summary>
    private void RespawnBall()
    {
        if (spawnedBall != null)
        {
            // NetworkedBall's ResetToServePosition will use CurrentAuthority to determine position
            // Authority was already set by OnGroundHit based on who won the round
            spawnedBall.ResetToServePosition(spawnedBall.CurrentAuthority);
            CurrentPhase = GamePhase.BallPosition;
        }
        else if (Object.HasStateAuthority)
        {
            // If ball was somehow destroyed, spawn a new one
            SpawnBall();
            CurrentPhase = GamePhase.BallPosition;
        }
        else
        {
            // Client requests host to respawn
            RPC_RequestRespawnBall();
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRespawnBall()
    {
        RespawnBall();
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
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestGameStart()
    {
        if (!GameStarted)
        {
            GameStarted = true;
            SpawnBall();
            RPC_NotifyBallSpawned(); // Notify all clients
        }
    }
    
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyBallSpawned()
    {
        ballSpawnPending = false; // Reset pending flag for all clients
        StartCoroutine(FindSpawnedBallDelayed());
    }
    
    private IEnumerator FindSpawnedBallDelayed()
    {
        // Wait for Fusion to finish spawning
        yield return null;
        yield return null;
        
        // Try to find the ball
        var ball = FindObjectOfType<NetworkedBall>();
        if (ball != null && spawnedBall == null)
        {
            spawnedBall = ball;
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
        // Rackets are managed by ControllerRacket - it handles its own cleanup
        // Destroy ControllerRacketManager if we created it
        var racketManager = GameObject.Find("ControllerRacketManager");
        if (racketManager != null) Destroy(racketManager);
    }
}
