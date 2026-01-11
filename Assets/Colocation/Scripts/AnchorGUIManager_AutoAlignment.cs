using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System;
using System.Text;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

#if FUSION2
using Fusion;
#endif

/// <summary>
/// Simplified Anchor GUI Manager - Auto Align and Spawn Cube with built-in session management
/// Inherits from ColocationManager to reuse alignment logic.
/// </summary>
public class AnchorGUIManager_AutoAlignment : ColocationManager
{
    [Header("UI Buttons")]
    [SerializeField] private Button autoAlignButton;
    [SerializeField] private Button spawnCubeButton;
    [SerializeField] private Button resetButton; // Add this
    [SerializeField] private Button startGameButton; // Start Table Tennis game (with environment)
    [SerializeField] private Button startPassthroughGameButton; // Start passthrough table tennis
    
    [Header("UI Panels")]
    [SerializeField] private GameObject buttonPanel; // Panel containing all buttons
    [SerializeField] private GameObject instructionPanel; // Panel showing game instructions (hidden initially)
    [SerializeField] private TextMeshProUGUI instructionText; // Text for instructions

    [Header("Game Scene Settings")]
    [SerializeField] private string tableTennisSceneName = "TableTennis";
    
    [Header("Passthrough Game Settings")]
    [SerializeField] private TableTennisConfig sharedConfig; // Shared config with prefabs and settings
    [SerializeField] private GameObject tablePrefab; // Table prefab (fallback if no shared config)
    [SerializeField] private GameObject racketPrefab; // Racket prefab (fallback if no shared config)
    [SerializeField] private NetworkPrefabRef ballPrefab; // Ball prefab (fallback if no shared config)
    [SerializeField] private float tableRotateSpeed = 90f; // Degrees per second
    [SerializeField] private float tableMoveSpeed = 1f; // Meters per second
    [SerializeField] private float tableXRotationOffset = 180f; // X rotation to fix upside-down table (try 0 or 180)
    [SerializeField] private float tableYRotationOffset = 90f; // Extra Y rotation (90 = players face each other across table)
    [SerializeField] private float defaultTableHeight = 0f; // Table Y position (0 = table feet on ground)
    
    // Properties to get prefabs from shared config or fallback to direct assignment
    private GameObject TablePrefab => sharedConfig != null ? sharedConfig.TablePrefab : tablePrefab;
    private GameObject RacketPrefab => sharedConfig != null ? sharedConfig.RacketPrefab : racketPrefab;
    private NetworkPrefabRef BallPrefab => sharedConfig != null && sharedConfig.BallPrefab != default ? sharedConfig.BallPrefab : ballPrefab;
    private float TableRotateSpeed => sharedConfig != null ? sharedConfig.tableRotateSpeed : tableRotateSpeed;
    private float TableMoveSpeed => sharedConfig != null ? sharedConfig.tableMoveSpeed : tableMoveSpeed;
    private GameObject spawnedPassthroughTable; // Reference to spawned table
    private bool isPassthroughMode = false;
    private bool isSwitchedToVRMode = false; // True when switched to TableTennis VR scene
    
    // Passthrough game phases
    private enum PassthroughGamePhase { 
        Idle,           // Not in passthrough mode
        TableAdjust,    // Adjusting table position/rotation
        BallPosition,   // Ball spawned, A/X + thumbstick to adjust. Hit ball to start.
        Playing         // Ball hit, game in progress - racket switching locked
    }
    private PassthroughGamePhase passthroughPhase = PassthroughGamePhase.Idle;
    
    // Racket references
    private GameObject leftRacket;
    private GameObject rightRacket;
    private bool racketsVisible = true; // B/Y toggles visibility
    
    // Racket offset/rotation settings (matching VR scene's ControllerRacket for consistency)
    private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f);
    private Vector3 racketRotation = new Vector3(-51f, 240f, 43f);
    private float racketScale = 10f;
    private GameObject spawnedBall;
    private bool wasGripPressed = false; // Debounce for grip input
    private bool ballSpawnPending = false; // Prevent multiple spawn requests
    
    /// <summary>
    /// Returns whether passthrough rackets are currently visible (for NetworkedPlayer sync)
    /// </summary>
    public bool ArePassthroughRacketsVisible()
    {
        return isPassthroughMode && racketsVisible && (leftRacket != null || rightRacket != null);
    }
    
    // Passthrough Game UI Panel (replaces alignment panel during game)
    private GameObject passthroughGameUIPanel;
    private TextMesh passthroughScoreText;
    private TextMesh passthroughInfoText;
    private TextMesh passthroughStatusText;
    private TextMesh passthroughControlsText;
    
    // Game menu state
    private bool isGameMenuOpen = false;
    private GameObject runtimeMenuPanel;
    
#if FUSION2
    // Networked passthrough table state (synced across players)
    [Networked] private float NetworkedPassthroughTableYRotation { get; set; }
    [Networked] private float NetworkedPassthroughTableHeight { get; set; } // Y position (height) only
    [Networked] private NetworkBool NetworkedPassthroughGameActive { get; set; }
    
    // Networked anchor sharing state - client waits for this before loading anchors
    [Networked] private NetworkBool HostAnchorsShared { get; set; }
    [Networked, Capacity(64)] private NetworkString<_64> SharedAnchorGroupUuidString { get; set; }
    
    // Networked alignment state - tracks when both players are aligned
    [Networked] private NetworkBool ClientAlignedToAnchors { get; set; }
    
    // Networked hand positions for BOTH players (anchor-relative)
    // Host writes to Host* properties, Client writes to Client* properties via RPC
    [Networked] private Vector3 HostLeftHandPos { get; set; }
    [Networked] private Quaternion HostLeftHandRot { get; set; }
    [Networked] private Vector3 HostRightHandPos { get; set; }
    [Networked] private Quaternion HostRightHandRot { get; set; }
    [Networked] private NetworkBool HostRacketsVisible { get; set; }
    
    [Networked] private Vector3 ClientLeftHandPos { get; set; }
    [Networked] private Quaternion ClientLeftHandRot { get; set; }
    [Networked] private Vector3 ClientRightHandPos { get; set; }
    [Networked] private Quaternion ClientRightHandRot { get; set; }
    [Networked] private NetworkBool ClientRacketsVisible { get; set; }
#endif

    // Remote player racket visuals (created for the other player)
    private GameObject remoteLeftRacket;
    private GameObject remoteRightRacket;

    [Header("Status Display")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI anchorText;
    [SerializeField] private Image statusIndicator; // Alignment Status
    [SerializeField] private Image networkIndicator; // Host/Client Status

    [Header("Settings")]
    // alignmentManager is in base class
    [SerializeField] private float anchorScale = 0.3f; // Larger for better visibility
    
    [Header("Cube Spawn Settings")]
    [SerializeField] private NetworkPrefabRef cubePrefab;
    [SerializeField] private float cubeScale = 0.1f;
    
    [Header("Status Colors")]
    [SerializeField] private Color hostColor = Color.blue;
    [SerializeField] private Color clientColor = Color.yellow;
    [SerializeField] private Color anchorAlignedColor = Color.green;
    [SerializeField] private Color anchorNotAlignedColor = Color.red;
    [SerializeField] private Color advertisingColor = new Color(0.5f, 0f, 1f); // Purple
    [SerializeField] private Color discoveringColor = new Color(1f, 0.5f, 0f); // Orange
    
    [Header("Network Settings")]
    [SerializeField] private string sessionName = "MyVRSession";
    [SerializeField] private bool autoStartSession = true;
    [SerializeField] private bool autoAlignOnStart = false;
    
    [Header("Debug / Development")]
    [Tooltip("Skip anchor alignment for quick testing. Table will spawn in front of player.")]
    [SerializeField] private bool skipAlignmentForDebug = false;
    [SerializeField] private Vector3 debugTablePosition = new Vector3(0, 0.76f, 2f);
    
    private List<OVRSpatialAnchor> currentAnchors;
    private Transform cameraTransform;
    private GameObject anchorMarkerPrefab;
    // _sharedAnchorGroupId and _localizedAnchor are in base class
    private bool isHost = false;
    private enum SessionState { Idle, Advertising, Discovering, Sharing, HostAligned, ClientAligned }
    private SessionState currentState = SessionState.Idle;
    private int _clientLocalizedAnchorCount = 0; // Track how many anchors client has localized

    // Wizard State
    private enum AlignmentStep { 
        Start, 
        PlaceAnchor1, 
        PlaceAnchor2, 
        ReadyToShare,
        ShareFailed,  // Sharing failed - allow retry
        Done 
    }
    private AlignmentStep currentStep = AlignmentStep.Start;
    
    // Anchor placement preview
    private Vector3 firstAnchorWorldPosition; // Stored position of first anchor
    private LineRenderer distanceLine; // Line between anchors to visualize distance
    private bool waitingForGripToPlaceAnchors = false; // True when user should press grips to place anchors
    private bool anchor1Placed = false; // Track if anchor 1 is placed
    private bool anchor2Placed = false; // Track if anchor 2 is placed
    
    // Anchor placement state
    private bool hostAutoStarted = false; // Track if we auto-started for host
    private bool isPlacingAnchor = false; // Prevent double-placement during async anchor creation
    
    // Distance display above controllers
    private GameObject leftDistanceDisplay;
    private GameObject rightDistanceDisplay;
    private TextMesh leftDistanceText;
    private TextMesh rightDistanceText;
    
    // Static properties for cross-scene access (TableTennisManager, NetworkedBall)
    public static Vector3 FirstAnchorPosition { get; private set; }
    public static Vector3 SecondAnchorPosition { get; private set; }
    public static Guid FirstAnchorUuid { get; private set; }
    public static Guid SecondAnchorUuid { get; private set; }
    public static float TableHeightOffsetStatic { get; private set; }
    public static bool AlignmentCompletedStatic { get; private set; }
    public static bool TableWasAligned { get; private set; }
    public static Vector3 AlignedTablePosition { get; private set; }
    public static Quaternion AlignedTableRotation { get; private set; }
    
    // Instance variables for anchor positions (before static assignment)
    private Vector3 firstAnchorPosition;
    private Vector3 secondAnchorPosition;
    private float tableHeightOffset = 0f;
    
    // Alignment state flags
    private bool _alignmentCompleted = false;
    private bool _alignmentInProgress = false;

#if FUSION2
    private NetworkRunner networkRunner;
    private NetworkObject spawnedCube; // Track the single spawned cube
#endif

    private void Start()
    {
        currentAnchors = new List<OVRSpatialAnchor>();

        cameraTransform = Camera.main?.transform;
        if (cameraTransform == null)
        {
            Log("No main camera found!");
            return;
        }
        
        // Check if we're in the TableTennis VR scene - if so, disable this component
        // (same approach as main branch Start Game - scene transition means this object is destroyed)
        string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (currentScene.ToLower().Contains("tabletennis"))
        {
            Debug.Log($"[Anchor] VR Scene detected ({currentScene}), disabling AnchorGUIManager");
            isSwitchedToVRMode = true;
            
            // Hide UI panels
            if (buttonPanel != null) buttonPanel.SetActive(false);
            
            // Disable this component - TableTennisManager handles VR game
            this.enabled = false;
            return;
        }

        anchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");
        if (anchorMarkerPrefab == null)
        {
            Debug.LogWarning("[Anchor] AnchorMarker prefab not found, trying AnchorCursorSphere...");
            anchorMarkerPrefab = Resources.Load<GameObject>("AnchorCursorSphere");
        }
        
        if (anchorMarkerPrefab != null)
        {
            Debug.Log($"[Anchor] Anchor prefab loaded: {anchorMarkerPrefab.name}");
        }
        else
        {
            Debug.LogError("[Anchor] NO ANCHOR PREFAB FOUND! Anchors will have no visual.");
        }

        if (alignmentManager == null)
        {
            alignmentManager = FindObjectOfType<AlignmentManager>();
        }

        autoAlignButton?.onClick.AddListener(OnAutoAlignClicked);
        spawnCubeButton?.onClick.AddListener(OnSpawnCubeClicked);
        resetButton?.onClick.AddListener(OnResetClicked); // Add this
        startGameButton?.onClick.AddListener(OnStartGameClicked);
        startPassthroughGameButton?.onClick.AddListener(OnStartPassthroughGameClicked);
        
        // Initially hide instruction panel
        if (instructionPanel != null) instructionPanel.SetActive(false);

        UpdateAllUI();
        UpdateUIWizard(); // Init text
        Log("Ready - Grip button to place anchors");
        
        // Create distance displays and line for anchor placement
        CreateDistanceDisplays();
        
        // Check if we're returning from VR scene - if so, auto-start passthrough mode after alignment
        CheckReturnFromVRScene();
    }
    
    /// <summary>
    /// Check if returning from VR scene and auto-start passthrough mode
    /// </summary>
    private void CheckReturnFromVRScene()
    {
        if (PlayerPrefs.GetInt("ReturnFromVRScene", 0) == 1)
        {
            PlayerPrefs.SetInt("ReturnFromVRScene", 0);
            PlayerPrefs.Save();
            Debug.Log("[Anchor] Returning from VR scene - will auto-start passthrough after alignment");
            
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
        Debug.Log($"[Anchor] Found {allAnchors.Length} persistent spatial anchors");
        
        foreach (var anchor in allAnchors)
        {
            if (anchor.Localized && !currentAnchors.Contains(anchor))
            {
                currentAnchors.Add(anchor);
                Debug.Log($"[Anchor] Restored anchor: {anchor.Uuid}");
                
                // Set the first localized anchor as our reference
                if (_localizedAnchor == null)
                {
                    _localizedAnchor = anchor;
                }
            }
        }
        
        Debug.Log($"[Anchor] currentAnchors count after restore: {currentAnchors.Count}");
    }
    
    /// <summary>
    /// Wait for alignment to complete, then auto-start passthrough game
    /// </summary>
    private System.Collections.IEnumerator AutoStartPassthroughAfterAlignment()
    {
        Debug.Log("[Anchor] Waiting for alignment before auto-starting passthrough...");
        
        // Wait for anchors to be localized (max 30 seconds)
        float timeout = 30f;
        float elapsed = 0f;
        
        while (elapsed < timeout)
        {
            // Try to find persistent anchors if we don't have any yet
            if (currentAnchors == null || currentAnchors.Count < 2)
            {
                FindPersistentAnchors();
            }
            
            // Check if we have at least 2 anchors and alignment is complete
            if (currentAnchors != null && currentAnchors.Count >= 2 && _localizedAnchor != null && _localizedAnchor.Localized)
            {
                // Give UI a moment to update
                yield return new WaitForSeconds(1f);
                
                Debug.Log("[Anchor] Alignment ready - auto-starting passthrough game");
                OnStartPassthroughGameClicked();
                yield break;
            }
            
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        
        Debug.LogWarning("[Anchor] Timeout waiting for alignment, passthrough will not auto-start");
    }
    
    private void CreateDistanceDisplays()
    {
        // Create distance display above left controller
        leftDistanceDisplay = new GameObject("LeftDistanceDisplay");
        leftDistanceText = leftDistanceDisplay.AddComponent<TextMesh>();
        leftDistanceText.fontSize = 50;
        leftDistanceText.characterSize = 0.01f;
        leftDistanceText.anchor = TextAnchor.MiddleCenter;
        leftDistanceText.alignment = TextAlignment.Center;
        leftDistanceText.color = Color.white;
        leftDistanceText.text = "";
        leftDistanceDisplay.SetActive(false);
        
        // Create distance display above right controller
        rightDistanceDisplay = new GameObject("RightDistanceDisplay");
        rightDistanceText = rightDistanceDisplay.AddComponent<TextMesh>();
        rightDistanceText.fontSize = 50;
        rightDistanceText.characterSize = 0.01f;
        rightDistanceText.anchor = TextAnchor.MiddleCenter;
        rightDistanceText.alignment = TextAlignment.Center;
        rightDistanceText.color = Color.white;
        rightDistanceText.text = "";
        rightDistanceDisplay.SetActive(false);
        
        // Create line renderer for distance visualization
        var lineObj = new GameObject("AnchorDistanceLine");
        distanceLine = lineObj.AddComponent<LineRenderer>();
        distanceLine.startWidth = 0.02f;
        distanceLine.endWidth = 0.02f;
        distanceLine.positionCount = 2;
        distanceLine.material = new Material(Shader.Find("Sprites/Default"));
        distanceLine.startColor = Color.yellow;
        distanceLine.endColor = Color.yellow;
        distanceLine.enabled = false;
    }

#if FUSION2
    private async void StartNetworkSession()
    {
        Log("Network session ready. Awaiting user action to start colocation.");
    }
    #endif

    // ==================== AUTO ALIGN ====================

    private bool _discoveryStarted = false;
    private float _lastDiscoveryTime = 0f;
    private const float DISCOVERY_RETRY_INTERVAL = 15f; // Retry every 15 seconds if no anchors found

    private void Update()
    {
        // Don't run Update if we've switched to VR TableTennis scene
        if (isSwitchedToVRMode) return;
        
        UpdateStatusIndicator();
        UpdateButtonStates();
        
        // Handle passthrough game input if in passthrough mode
        if (isPassthroughMode)
        {
            HandlePassthroughGameInput();
        }
        
        // Update anchor placement cursor and distance preview (not in passthrough mode)
        if (!isPassthroughMode)
        {
            UpdateAnchorPlacementPreview();
        
            // Handle grip-to-place anchors with hold timer for intentional placement
            HandleGripAnchorPlacement();
        }
        
#if FUSION2
        // Auto-detect role and update UI text for Client
        if (networkRunner == null) networkRunner = FindObjectOfType<NetworkRunner>();
        
        if (networkRunner != null && networkRunner.IsRunning)
        {
            // Update role variable
            bool localIsHost = networkRunner.IsServer || networkRunner.IsSharedModeMasterClient;
            
            // If we are a client and NOT aligned yet, handle discovery
            if (!localIsHost && currentStep != AlignmentStep.Done)
            {
                // Auto-switch UI to show Client state
                if (isHost) // If we previously thought we were host (default)
                {
                    isHost = false; 
                    Log("Client Mode Detected");
                    
                    // Update UI immediately for client
                    if (statusText != null)
                    {
                        statusText.text = "Client Mode\nSearching for host session...";
                    }
                    
                    UpdateUIWizard();
                }
                
                // Auto-start or retry discovery if not aligned
                // BUT skip if we already have 2 anchors localized
                if (!IsAlignmentComplete() && _clientLocalizedAnchorCount < 2)
                {
                    // FAST PATH: Check if host has shared anchors via network (no discovery needed)
                    if (HostAnchorsShared && !string.IsNullOrEmpty(SharedAnchorGroupUuidString.ToString()))
                    {
                        if (!_discoveryStarted)
                        {
                            Log("Host shared anchors via network - loading directly...");
                            _discoveryStarted = true;
                            _lastDiscoveryTime = Time.time;
                            
                            // Parse the UUID from networked string
                            if (Guid.TryParse(SharedAnchorGroupUuidString.ToString(), out Guid groupUuid))
                            {
                                _sharedAnchorGroupId = groupUuid;
                                LoadAndAlignToAnchor(groupUuid);
                            }
                            else
                            {
                                Log($"Failed to parse UUID: {SharedAnchorGroupUuidString}", true);
                            }
                        }
                    }
                    // FALLBACK: Use OVR discovery (slower but works without network sync)
                    else if (!_discoveryStarted || (Time.time - _lastDiscoveryTime > DISCOVERY_RETRY_INTERVAL))
                    {
                        Log("Client: Starting/Retrying anchor discovery...");
                        
                        // Show status on UI
                        if (statusText != null && !statusText.text.Contains("Loading"))
                        {
                            statusText.text = "Client Mode\nSearching for host...\n\nMake sure host has:\n1. Placed 2 anchors\n2. Shared them";
                        }
                        
                        _discoveryStarted = true;
                        _lastDiscoveryTime = Time.time;
                        PrepareColocation();
                    }
                }
            }
            else if (localIsHost)
            {
                if (!isHost)
                {
                    isHost = true;
                    Log("Host Mode: Ready to create anchors");
                    UpdateUIWizard();
                }
                
                // AUTO-START anchor placement for host (no button click needed)
                // Skip if already in VR mode or anchors already placed
                if (!hostAutoStarted && !isSwitchedToVRMode && currentStep == AlignmentStep.Start && !anchor1Placed && !anchor2Placed)
                {
                    hostAutoStarted = true;
                    waitingForGripToPlaceAnchors = true;
                    currentStep = AlignmentStep.PlaceAnchor1;
                    Log("Press GRIP to place Anchor 1");
                    UpdateUIWizard();
                }
            }
        }
        else if (networkRunner == null)
        {
            // Show "Connecting..." state
            if (statusText != null && !statusText.text.Contains("Connect"))
            {
                statusText.text = "Connecting to network...";
            }
        }
#endif
    }
    
    /// <summary>
    /// Handle grip button to place anchors - simple single press (like main ColocationManager)
    /// Either controller can place either anchor
    /// </summary>
    private void HandleGripAnchorPlacement()
    {
        if (!waitingForGripToPlaceAnchors) return;
        if (anchor1Placed && anchor2Placed) return;
        if (isPlacingAnchor) return; // Prevent double-placement while async is running
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;
        
        // Check BOTH controllers for grip press
        bool leftGripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch);
        bool rightGripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        
        if (!leftGripPressed && !rightGripPressed) return;
        
        // Use whichever controller was pressed
        bool useLeftHand = leftGripPressed;
        Transform controllerTransform = useLeftHand ? cameraRig.leftControllerAnchor : cameraRig.rightControllerAnchor;
        
        if (controllerTransform == null) return;
        
        // Set flag to prevent double-placement
        isPlacingAnchor = true;
        
        // Place anchor at controller position
        if (!anchor1Placed)
        {
            PlaceAnchorAtController(1, useLeftHand);
        }
        else if (!anchor2Placed)
        {
            PlaceAnchorAtController(2, useLeftHand);
        }
    }
    
#if FUSION2
    /// <summary>
    /// Fusion network tick - sync passthrough table state and hand positions to clients
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        // Sync hand positions for remote player rackets
        SyncHandPositions();
        
        // Only sync table if passthrough game is active
        if (NetworkedPassthroughGameActive)
        {
            // Try to find table if not set
            if (spawnedPassthroughTable == null)
            {
                spawnedPassthroughTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
                
                if (spawnedPassthroughTable != null && currentAnchors != null && currentAnchors.Count > 0 && 
                    spawnedPassthroughTable.transform.parent != currentAnchors[0].transform)
                {
                    spawnedPassthroughTable.transform.SetParent(currentAnchors[0].transform, worldPositionStays: false);
                }
            }
            
            // Apply state if table exists
            if (spawnedPassthroughTable != null)
            {
                ApplyPassthroughTableState();
            }
        }
        
        // Update remote player racket visuals
        UpdateRemoteRackets();
    }
    
    /// <summary>
    /// Sync local controller positions to network (for other player to see)
    /// Uses controller anchors for stability (not hand anchors which can drift)
    /// Host writes directly, Client uses RPC
    /// </summary>
    private void SyncHandPositions()
    {
        if (!isPassthroughMode) return;
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;
        
        // Use CONTROLLER anchors for stability (not hand anchors which drift)
        Transform leftController = cameraRig.leftControllerAnchor;
        Transform rightController = cameraRig.rightControllerAnchor;
        
        if (_localizedAnchor == null) return;
        Transform anchor = _localizedAnchor.transform;
        
        Vector3 leftPos = Vector3.zero;
        Quaternion leftRot = Quaternion.identity;
        Vector3 rightPos = Vector3.zero;
        Quaternion rightRot = Quaternion.identity;
        
        if (leftController != null)
        {
            leftPos = anchor.InverseTransformPoint(leftController.position);
            leftRot = Quaternion.Inverse(anchor.rotation) * leftController.rotation;
        }
        
        if (rightController != null)
        {
            rightPos = anchor.InverseTransformPoint(rightController.position);
            rightRot = Quaternion.Inverse(anchor.rotation) * rightController.rotation;
        }
        
        // Host writes directly, client uses RPC
        if (Object.HasStateAuthority)
        {
            HostLeftHandPos = leftPos;
            HostLeftHandRot = leftRot;
            HostRightHandPos = rightPos;
            HostRightHandRot = rightRot;
            HostRacketsVisible = racketsVisible;
        }
        else
        {
            // Client sends hand positions via RPC
            RPC_SyncClientHands(leftPos, leftRot, rightPos, rightRot, racketsVisible);
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_SyncClientHands(Vector3 leftPos, Quaternion leftRot, Vector3 rightPos, Quaternion rightRot, NetworkBool visible)
    {
        ClientLeftHandPos = leftPos;
        ClientLeftHandRot = leftRot;
        ClientRightHandPos = rightPos;
        ClientRightHandRot = rightRot;
        ClientRacketsVisible = visible;
    }
    
    /// <summary>
    /// Client notifies host that it has aligned to anchors
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_NotifyClientAligned()
    {
        Debug.Log("[Anchor] HOST received: Client has aligned to anchors");
        Log("Client has aligned to anchors");
        ClientAlignedToAnchors = true;
        currentState = SessionState.ClientAligned;
        currentStep = AlignmentStep.Done;
        UpdateUIWizard();
        UpdateAllUI();
        
        // Update status text to show both aligned
        if (statusText != null)
        {
            statusText.text = "Both Players Aligned\n\nReady to play!";
        }
    }
    
    /// <summary>
    /// Update remote player's racket visuals based on their synced hand positions
    /// </summary>
    private void UpdateRemoteRackets()
    {
        if (!isPassthroughMode) return;
        
        // Create remote rackets if needed
        EnsureRemoteRacketsExist();
        
        if (_localizedAnchor == null) return;
        Transform anchor = _localizedAnchor.transform;
        
        // Determine which player's data to use for remote rackets
        // If we're host, show client's rackets. If we're client, show host's rackets.
        bool isHost = Object.HasStateAuthority;
        
        Vector3 remoteLeftPos = isHost ? ClientLeftHandPos : HostLeftHandPos;
        Quaternion remoteLeftRot = isHost ? ClientLeftHandRot : HostLeftHandRot;
        Vector3 remoteRightPos = isHost ? ClientRightHandPos : HostRightHandPos;
        Quaternion remoteRightRot = isHost ? ClientRightHandRot : HostRightHandRot;
        bool showRemoteRackets = isHost ? ClientRacketsVisible : HostRacketsVisible;
        
        if (remoteLeftRacket != null)
        {
            remoteLeftRacket.SetActive(showRemoteRackets);
            if (showRemoteRackets)
            {
                Vector3 worldPos = anchor.TransformPoint(remoteLeftPos);
                Quaternion worldRot = anchor.rotation * remoteLeftRot;
                // Apply offset in the hand's local space, then set world position
                remoteLeftRacket.transform.position = worldPos + worldRot * racketOffset;
                remoteLeftRacket.transform.rotation = worldRot * Quaternion.Euler(racketRotation);
            }
        }
        
        if (remoteRightRacket != null)
        {
            remoteRightRacket.SetActive(showRemoteRackets);
            if (showRemoteRackets)
            {
                Vector3 worldPos = anchor.TransformPoint(remoteRightPos);
                Quaternion worldRot = anchor.rotation * remoteRightRot;
                // Apply offset in the hand's local space, then set world position
                remoteRightRacket.transform.position = worldPos + worldRot * racketOffset;
                remoteRightRacket.transform.rotation = worldRot * Quaternion.Euler(racketRotation);
            }
        }
    }
    
    /// <summary>
    /// Create remote rackets if they don't exist (lazy init)
    /// </summary>
    private void EnsureRemoteRacketsExist()
    {
        if (remoteLeftRacket != null && remoteRightRacket != null) return;
        if (!isPassthroughMode) return;
        
        // Find a racket template
        GameObject template = FindRacketTemplate();
        
        if (template != null)
        {
            if (remoteLeftRacket == null)
            {
                remoteLeftRacket = Instantiate(template);
                remoteLeftRacket.name = "RemoteLeftRacket";
                remoteLeftRacket.transform.localScale = Vector3.one * 10f;
                CleanupRacketPhysics(remoteLeftRacket);
                remoteLeftRacket.SetActive(false);
                Debug.Log("[Passthrough] Created remote left racket");
            }
            
            if (remoteRightRacket == null)
            {
                remoteRightRacket = Instantiate(template);
                remoteRightRacket.name = "RemoteRightRacket";
                remoteRightRacket.transform.localScale = Vector3.one * 10f;
                CleanupRacketPhysics(remoteRightRacket);
                remoteRightRacket.SetActive(false);
                Debug.Log("[Passthrough] Created remote right racket");
            }
        }
        else
        {
            // Create placeholder rackets
            if (remoteLeftRacket == null)
            {
                remoteLeftRacket = CreatePlaceholderRacket("RemoteLeftRacket");
                remoteLeftRacket.SetActive(false);
            }
            if (remoteRightRacket == null)
            {
                remoteRightRacket = CreatePlaceholderRacket("RemoteRightRacket");
                remoteRightRacket.SetActive(false);
            }
        }
    }
    
    /// <summary>
    /// Find a racket template from the scene
    /// </summary>
    private GameObject FindRacketTemplate()
    {
        // Search by tag first
        var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var r in taggedRackets)
        {
            if (r.name.Contains("Remote") || r == leftRacket || r == rightRacket) continue;
            if (r.GetComponent<MeshFilter>() != null || r.GetComponentInChildren<MeshFilter>() != null)
            {
                return r;
            }
        }
        
        // Search under table
        if (spawnedPassthroughTable != null)
        {
            foreach (Transform child in spawnedPassthroughTable.GetComponentsInChildren<Transform>(true))
            {
                string nameLower = child.name.ToLower();
                if (nameLower.Contains("remote") || child.gameObject == leftRacket || child.gameObject == rightRacket) continue;
                
                if ((nameLower.Contains("racket") || nameLower.Contains("paddle")) && child.GetComponent<MeshFilter>() != null)
                {
                    return child.gameObject;
                }
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Remove physics from remote rackets (they're just visuals)
    /// </summary>
    private void CleanupRacketPhysics(GameObject racket)
    {
        var rb = racket.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        
        var colliders = racket.GetComponentsInChildren<Collider>();
        foreach (var col in colliders)
        {
            Destroy(col);
        }
    }
#endif

    // ==================== AUTO ALIGN ====================
    
    private void UpdateAnchorPlacementPreview()
    {
        bool inPlacementMode = waitingForGripToPlaceAnchors && (currentStep == AlignmentStep.PlaceAnchor1 || currentStep == AlignmentStep.PlaceAnchor2);
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;
        
        Transform leftHand = cameraRig.leftControllerAnchor;
        Transform rightHand = cameraRig.rightControllerAnchor;
        
        if (leftHand == null || rightHand == null) return;
        
        // Always hide distance line (not used in simplified flow)
        if (distanceLine != null) distanceLine.enabled = false;
        
        // Before Anchor 1 is placed - show instruction on both controllers
        if (inPlacementMode && !anchor1Placed)
        {
            if (leftDistanceDisplay != null && leftDistanceText != null)
            {
                leftDistanceDisplay.SetActive(true);
                leftDistanceDisplay.transform.position = leftHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    leftDistanceDisplay.transform.LookAt(cameraTransform);
                leftDistanceDisplay.transform.Rotate(0, 180, 0);
                leftDistanceText.text = "GRIP\nYour Side";
                leftDistanceText.color = Color.cyan;
            }
            
            if (rightDistanceDisplay != null && rightDistanceText != null)
            {
                rightDistanceDisplay.SetActive(true);
                rightDistanceDisplay.transform.position = rightHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    rightDistanceDisplay.transform.LookAt(cameraTransform);
                rightDistanceDisplay.transform.Rotate(0, 180, 0);
                rightDistanceText.text = "GRIP\nYour Side";
                rightDistanceText.color = Color.cyan;
            }
        }
        // After Anchor 1 is placed - show distance from anchor on each controller
        else if (inPlacementMode && anchor1Placed && !anchor2Placed)
        {
            // Get the actual anchor position from the created anchor
            Vector3 anchor1Pos = firstAnchorWorldPosition;
            if (currentAnchors.Count > 0 && currentAnchors[0] != null)
            {
                anchor1Pos = currentAnchors[0].transform.position;
            }
            
            // Calculate distance from Anchor 1 to each controller
            float distanceLeft = Vector3.Distance(anchor1Pos, leftHand.position);
            float distanceRight = Vector3.Distance(anchor1Pos, rightHand.position);
            
            // Show distance on LEFT controller
            if (leftDistanceDisplay != null && leftDistanceText != null)
            {
                leftDistanceDisplay.SetActive(true);
                leftDistanceDisplay.transform.position = leftHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    leftDistanceDisplay.transform.LookAt(cameraTransform);
                leftDistanceDisplay.transform.Rotate(0, 180, 0);
                
                Color col = distanceLeft < 0.5f ? Color.red : (distanceLeft < 1f ? Color.yellow : Color.green);
                leftDistanceText.text = $"GRIP\n{distanceLeft:F2}m";
                leftDistanceText.color = col;
            }
            
            // Show distance on RIGHT controller
            if (rightDistanceDisplay != null && rightDistanceText != null)
            {
                rightDistanceDisplay.SetActive(true);
                rightDistanceDisplay.transform.position = rightHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    rightDistanceDisplay.transform.LookAt(cameraTransform);
                rightDistanceDisplay.transform.Rotate(0, 180, 0);
                
                Color col = distanceRight < 0.5f ? Color.red : (distanceRight < 1f ? Color.yellow : Color.green);
                rightDistanceText.text = $"GRIP\n{distanceRight:F2}m";
                rightDistanceText.color = col;
            }
        }
        else
        {
            // Hide when not in placement mode or both anchors placed
            if (leftDistanceDisplay != null) leftDistanceDisplay.SetActive(false);
            if (rightDistanceDisplay != null) rightDistanceDisplay.SetActive(false);
        }
    }
    
    private void UpdateDistanceDisplay(float distance)
    {
        // Show distance in button text
        if (autoAlignButton != null)
        {
            var btnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                string distanceStr = $"{distance:F2}m";
                string warning = distance < 0.5f ? " (too close)" : (distance < 1f ? " (OK)" : "");
                btnText.text = $"Anchor 2: {distanceStr}{warning}";
            }
        }
        
        // Color the line based on distance (green if good, red if too close)
        if (distanceLine != null)
        {
            Color lineColor = distance < 0.5f ? Color.red : (distance < 1f ? Color.yellow : Color.green);
            distanceLine.startColor = lineColor;
            distanceLine.endColor = lineColor;
        }
    }
    
    private async void PlaceAnchorAtController(int anchorNumber, bool useLeftController)
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) 
        {
            isPlacingAnchor = false;
            return;
        }
        
        // Use specified controller
        Transform controllerTransform = useLeftController ? cameraRig.leftControllerAnchor : cameraRig.rightControllerAnchor;
        if (controllerTransform == null) 
        {
            isPlacingAnchor = false;
            return;
        }
        
        // Get position at controller (waist level - no Y manipulation)
        Vector3 anchorPosition = controllerTransform.position;
        string handName = useLeftController ? "LEFT" : "RIGHT";
        
        if (anchorNumber == 1)
        {
            // Place Anchor 1
            Log($"Creating Anchor 1 at {handName} hand...");
            var anchor1 = await CreateAnchor(anchorPosition, Quaternion.identity);
            
            if (anchor1 != null)
            {
                anchor1Placed = true;
                firstAnchorWorldPosition = anchorPosition;
                currentStep = AlignmentStep.PlaceAnchor2;
                Log($"Anchor 1 placed. Move to 2nd position, press GRIP for Anchor 2");
                UpdateUIWizard();
            }
            else
            {
                Log("Failed to create Anchor 1. Press grip to try again.", true);
            }
            isPlacingAnchor = false; // Allow next placement
        }
        else if (anchorNumber == 2)
        {
            // Check distance from first anchor
            float distance = Vector3.Distance(firstAnchorWorldPosition, anchorPosition);
            if (distance < 0.3f)
            {
                Log($"Too close ({distance:F2}m). Move further (at least 0.5m)", true);
                isPlacingAnchor = false; // Allow retry
                return;
            }
            
            Log("Creating Anchor 2...");
            var anchor2 = await CreateAnchor(anchorPosition, Quaternion.identity);
            
            if (anchor2 != null)
            {
                anchor2Placed = true;
                Log($"Anchor 2 placed. Distance: {distance:F2}m");
                
                // Both anchors placed - move to ready state
                CheckBothAnchorsPlaced();
            }
            else
            {
                Log("Failed to create Anchor 2. Try again.", true);
            }
            isPlacingAnchor = false; // Allow next action
        }
    }
    
    private void CheckBothAnchorsPlaced()
    {
        if (anchor1Placed && anchor2Placed)
        {
            waitingForGripToPlaceAnchors = false;
            
            // Hide all displays
            if (distanceLine != null) distanceLine.enabled = false;
            if (leftDistanceDisplay != null) leftDistanceDisplay.SetActive(false);
            if (rightDistanceDisplay != null) rightDistanceDisplay.SetActive(false);
            
            float distance = Vector3.Distance(currentAnchors[0].transform.position, currentAnchors[1].transform.position);
            Log($"Both anchors placed. Distance: {distance:F2}m - Ready to Share");
            
            currentStep = AlignmentStep.ReadyToShare;
            UpdateUIWizard();
        }
    }

    private async void OnAutoAlignClicked()
    {
        if (cameraTransform == null)
        {
            Log("Camera not found!", true);
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            Log("Network not ready! Starting session...");
            StartNetworkSession();
             // Don't return, let user click again or handle async? 
             // Ideally wait, but for now just return.
            return;
        }

        // Determine role
        isHost = networkRunner.IsServer || networkRunner.IsSharedModeMasterClient;
        if (statusIndicator != null) statusIndicator.color = isHost ? hostColor : clientColor;

        if (!isHost)
        {
            Log("Client Mode: Restarting discovery...");
            // Stop any existing discovery first
            OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
            _ = OVRColocationSession.StopDiscoveryAsync();
            _discoveryStarted = false;
            _lastDiscoveryTime = 0f;
            // Start fresh discovery
            PrepareColocation();
            return;
        }

        // HOST WIZARD LOGIC
        switch (currentStep)
        {
            case AlignmentStep.Start:
                // Start -> Grip to place anchors
                Log("Grip to place Anchor 1, then Grip again for Anchor 2");
                currentStep = AlignmentStep.PlaceAnchor1;
                waitingForGripToPlaceAnchors = true;
                anchor1Placed = false;
                anchor2Placed = false;
                UpdateUIWizard();
                break;

            case AlignmentStep.PlaceAnchor1:
                // Remind user to grip
                if (!waitingForGripToPlaceAnchors)
                    waitingForGripToPlaceAnchors = true;
                Log("Press any Grip to place Anchor 1");
                UpdateUIWizard();
                break;
                
            case AlignmentStep.PlaceAnchor2:
                // Remind user to grip
                if (!waitingForGripToPlaceAnchors)
                    waitingForGripToPlaceAnchors = true;
                Log("Move and press any Grip for Anchor 2");
                UpdateUIWizard();
                break;

            case AlignmentStep.ReadyToShare:
                // Share
                Log("Sharing anchors and aligning...");
                PrepareColocation(); // Triggers ShareAnchors()
                currentStep = AlignmentStep.Done;
                UpdateUIWizard();
                break;
            
            case AlignmentStep.ShareFailed:
                // Retry sharing
                Log("Retrying anchor sharing...");
                currentStep = AlignmentStep.ReadyToShare;
                PrepareColocation(); // Triggers ShareAnchors()
                currentStep = AlignmentStep.Done;
                UpdateUIWizard();
                break;
                
            case AlignmentStep.Done:
                Log("Already aligned!");
                break;
        }
#else
        Log("Photon Fusion not enabled.");
#endif
    }

    private void UpdateUIWizard()
    {
        if (autoAlignButton == null) return;
        
        var btnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
        if (btnText == null) return;

        switch (currentStep)
        {
            case AlignmentStep.Start:
                if (isHost) 
                    btnText.text = "[HOST] GRIP = Anchor 1";
                else 
                    btnText.text = "[CLIENT] Waiting...";
                break;
            case AlignmentStep.PlaceAnchor1:
                btnText.text = "GRIP = Place Anchor 1";
                break;
            case AlignmentStep.PlaceAnchor2:
                btnText.text = "Move, GRIP = Anchor 2";
                break;
            case AlignmentStep.ReadyToShare:
                btnText.text = "Share & Align";
                break;
            case AlignmentStep.ShareFailed:
                btnText.text = "RETRY Share";
                autoAlignButton.interactable = true;
                break;
            case AlignmentStep.Done:
                // Show different text based on role and client alignment state
                if (isHost)
                {
                    // Host: show waiting until client has aligned
                    if (ClientAlignedToAnchors)
                    {
                        btnText.text = "Both Aligned";
                        autoAlignButton.interactable = false;
                    }
                    else if (currentState == SessionState.Sharing || HostAnchorsShared)
                    {
                        btnText.text = "Waiting for Client...";
                        autoAlignButton.interactable = false;
                    }
                    else
                    {
                        btnText.text = "Aligned";
                        autoAlignButton.interactable = false;
                    }
                }
                else
                {
                    // Client: show aligned once done
                    btnText.text = "Aligned";
                    autoAlignButton.interactable = false;
                }
                break;
        }
    }

    // ==================== STANDALONE ALIGN (NO NETWORK) ====================
    
    // CreateAndAlignAnchor removed as it is now handled by CreateAnchor override and base class logic

    public override void Spawned()
    {
        base.Spawned();
        isHost = Object.HasStateAuthority;
        UpdateStatusIndicator();
        // Do NOT auto-start colocation/alignment here
    }

    /// <summary>
    /// Returns the localized spatial anchor for parenting cubes.
    /// </summary>
    public OVRSpatialAnchor GetLocalizedAnchor()
    {
        return _localizedAnchor;
    }

    protected override void Log(string message, bool isError = false)
    {
        base.Log(message, isError);

        // Anchor-related keywords
        bool isAnchorMsg = message.Contains("anchor") || message.Contains("Anchor") || message.Contains("Advertisement started") || message.Contains("Discovery started") || message.Contains("localized successfully") || message.Contains("shared") || message.Contains("UUID");

        if (isAnchorMsg && anchorText != null)
        {
            anchorText.text = message;
        }
        else if (statusText != null)
        {
            statusText.text = message;
        }

        if (message.Contains("Advertisement started")) currentState = SessionState.Advertising;
        else if (message.Contains("Discovery started")) currentState = SessionState.Discovering;
        else if (message.Contains("Sharing anchors") || message.Contains("Saving and sharing")) currentState = SessionState.Sharing;
        else if (message.Contains("Client aligned")) 
        {
            currentState = SessionState.ClientAligned;
            // Also set host to aligned when client successfully aligns
            // This means the colocation is complete
        }
        else if (message.Contains("Host: Aligning")) currentState = SessionState.Sharing; // Host still sharing until client joins

        UpdateStatusIndicator();
    }

    // ==================== STANDALONE ALIGN (NO NETWORK) ====================

    protected override async void DiscoverNearbySession()
    {
        Log("Client: Starting session discovery...");
        base.DiscoverNearbySession();
    }

    protected override void OnColocationSessionDiscovered(OVRColocationSession.Data session)
    {
        Log($"Session discovered. UUID: {session.AdvertisementUuid}");
        
        // Update UI to show discovery status
        if (statusText != null)
        {
            statusText.text = "Session found!\nLoading anchors...";
        }
        
        base.OnColocationSessionDiscovered(session);
    }

    protected override async void LoadAndAlignToAnchor(Guid groupUuid)
    {
        try
        {
            Log($"Client: Loading anchors for Group UUID: {groupUuid.ToString().Substring(0, 8)}...");

            // Update UI to show loading status
            if (statusText != null)
            {
                statusText.text = "Session found!\nLoading anchors...";
            }
            
            // Wait for anchors to propagate through Meta's cloud
            Log("Waiting 8 seconds for anchor propagation...");
            await Task.Delay(8000);

            // Retry loop with delays between attempts
            var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            bool loadSuccess = false;
            int retryCount = 0;
            const int MAX_RETRIES = 8;

            while (retryCount < MAX_RETRIES)
            {
                unboundAnchors.Clear();
                
                // Update UI with retry status
                if (statusText != null)
                {
                    statusText.text = $"Loading anchors...\nAttempt {retryCount + 1}/{MAX_RETRIES}\nEnsure host shared anchors";
                }
                
                Log($"Attempt {retryCount + 1}: Calling LoadUnboundSharedAnchorsAsync with UUID: {groupUuid}");
                var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);

                Log($"LoadResult: Success={loadResult.Success}, Status={loadResult.Status}, Count={unboundAnchors.Count}");

                if (loadResult.Success && unboundAnchors.Count > 0)
                {
                    Log($"Found {unboundAnchors.Count} shared anchors");
                    if (statusText != null)
                    {
                        statusText.text = $"Found {unboundAnchors.Count} anchors!\nLocalizing...";
                    }
                    loadSuccess = true;
                    break;
                }

                // Log the actual status to understand why it failed
                if (!loadResult.Success)
                {
                    Log($"Load failed with status: {loadResult.Status}", true);
                }
                else if (unboundAnchors.Count == 0)
                {
                    Log($"Load succeeded but no anchors returned (host may still be sharing)");
                }

                retryCount++;
                if (retryCount < MAX_RETRIES)
                {
                    int waitTime = 8; // 8 seconds between retries
                    Log($"Retry {retryCount}/{MAX_RETRIES}: Waiting {waitTime}s...");
                    
                    // Update UI with wait countdown
                    if (statusText != null)
                    {
                        statusText.text = $"Loading anchors...\nAttempt {retryCount}/{MAX_RETRIES}";
                    }
                    
                    await Task.Delay(waitTime * 1000);
                }
            }

            if (!loadSuccess || unboundAnchors.Count == 0)
            {
                Log($"Failed to load anchors after {MAX_RETRIES} retries", true);
                Log($"Count: {unboundAnchors.Count}", true);
                Log("TROUBLESHOOTING:", true);
                Log("1. Make sure HOST placed anchors (Grip x2) and shared them", true);
                Log("2. Check both devices have internet connection", true);
                Log("3. Verify spatial data sharing is enabled on both devices", true);
                Log("4. Try walking around the area where host placed anchors", true);
                Log($"5. Group UUID: {groupUuid}", true);
                
                // Update UI with failure message and allow retry
                if (statusText != null)
                {
                    statusText.text = "Failed to load anchors!\n\nHost must:\n1. Place 2 anchors (Grip)\n2. Wait for 'Shared' message\n\nRestarting discovery...";
                }
                
                // Reset discovery state to allow automatic retry
                _discoveryStarted = false;
                _lastDiscoveryTime = 0f;
                
                return;
            }

            Log($"Localizing {unboundAnchors.Count} anchors in the physical space...");
            Log("TIP: Walk around slowly to help Quest scan the environment");

            // Localize ALL found anchors
            var localizedAnchors = new List<OVRSpatialAnchor>();

            for (int i = 0; i < unboundAnchors.Count; i++)
            {
                var unboundAnchor = unboundAnchors[i];
                Log($"Localizing anchor {i + 1}/{unboundAnchors.Count}: {unboundAnchor.Uuid.ToString().Substring(0, 8)}...");

                // Give more time for localization with timeout
                var localizeTask = unboundAnchor.LocalizeAsync();
                int timeoutMs = 30000; // 30 second timeout per anchor
                int elapsed = 0;

                while (!localizeTask.IsCompleted && elapsed < timeoutMs)
                {
                    await Task.Delay(500);
                    elapsed += 500;

                    if (elapsed % 5000 == 0) // Log every 5 seconds
                    {
                        Log($"Still localizing... ({elapsed / 1000}s elapsed)");
                    }
                }

                if (await localizeTask)
                {
                    Log($"Anchor {i + 1} localized: {unboundAnchor.Uuid.ToString().Substring(0, 8)}");

                    var anchorGameObject = new GameObject($"Anchor_{unboundAnchor.Uuid.ToString().Substring(0, 8)}");
                    var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor.BindTo(spatialAnchor);
                    
                    // Wait a frame for transform to update after binding
                    await Task.Yield();
                    
                    // Log anchor position for debugging - compare with host positions
                    Debug.Log($"[Anchor] CLIENT Anchor {i + 1} position: {spatialAnchor.transform.position}, rotation: {spatialAnchor.transform.eulerAngles}");

                    // Add visual
                    if (anchorMarkerPrefab != null)
                    {
                        GameObject visual = Instantiate(anchorMarkerPrefab, anchorGameObject.transform);
                        visual.name = "Visual";
                        visual.transform.localPosition = Vector3.zero;
                        visual.transform.localRotation = Quaternion.identity;
                        float validScale = Mathf.Max(anchorScale, 0.01f);
                        visual.transform.localScale = Vector3.one * validScale;

                         // Remove physics from visual
                        foreach (var col in visual.GetComponentsInChildren<Collider>())
                            Destroy(col);
                        foreach (var rb in visual.GetComponentsInChildren<Rigidbody>())
                            Destroy(rb);
                    }

                    localizedAnchors.Add(spatialAnchor);
                    currentAnchors.Add(spatialAnchor); // Track locally
                }
                else
                {
                    Log($"Failed to localize anchor {i + 1}: {unboundAnchor.Uuid.ToString().Substring(0, 8)}", true);
                    Log("Try moving closer to where the host placed the anchors", true);
                }
            }

            // Now Align based on what we found
            if (localizedAnchors.Count >= 2)
            {
                Log($"Client aligned using 2-point alignment with {localizedAnchors.Count} anchors");
                currentStep = AlignmentStep.Done;
                _clientLocalizedAnchorCount = localizedAnchors.Count;

                _localizedAnchor = localizedAnchors[0]; // Set primary as main
                
                // Log anchor positions BEFORE alignment for comparison with host
                Debug.Log($"[Anchor] CLIENT Pre-align Anchor1: {localizedAnchors[0].transform.position}");
                Debug.Log($"[Anchor] CLIENT Pre-align Anchor2: {localizedAnchors[1].transform.position}");
                
                alignmentManager.AlignUserToTwoAnchors(localizedAnchors[0], localizedAnchors[1]);
                
                // Wait for alignment to complete before logging post-alignment positions
                await Task.Delay(1000);
                
                // Log anchor positions AFTER alignment - these should match host positions
                Debug.Log($"[Anchor] CLIENT Post-align Anchor1: {localizedAnchors[0].transform.position}");
                Debug.Log($"[Anchor] CLIENT Post-align Anchor2: {localizedAnchors[1].transform.position}");
                
                // Store anchor positions and mark alignment complete
                FirstAnchorPosition = localizedAnchors[0].transform.position;
                SecondAnchorPosition = localizedAnchors[1].transform.position;
                AlignmentCompletedStatic = true;
                
                Debug.Log($"[Anchor] CLIENT Stored positions: Anchor1={FirstAnchorPosition}, Anchor2={SecondAnchorPosition}");
                
                // Disable rediscovery - we have both anchors
                _discoveryStarted = false; // Prevent auto-retry
                _ = OVRColocationSession.StopDiscoveryAsync();
                Log("Client: 2 anchors localized - discovery disabled");
                
                // Notify host that client has aligned
                if (Runner != null && !Object.HasStateAuthority)
                {
                    Debug.Log("[Anchor] CLIENT sending RPC_NotifyClientAligned to host");
                    RPC_NotifyClientAligned();
                }
                
                // Update UI with success
                if (statusText != null)
                {
                    statusText.text = "Aligned\n2 anchors found\n\nReady to play";
                }
                
                UpdateUIWizard();
            }
            else if (localizedAnchors.Count == 1)
            {
                Log("Client aligned using single-point alignment (only 1 anchor found)");
                currentStep = AlignmentStep.Done;
                _clientLocalizedAnchorCount = 1;

                _localizedAnchor = localizedAnchors[0];
                alignmentManager.AlignUserToAnchor(localizedAnchors[0]);
                
                // Store anchor position and mark alignment complete
                FirstAnchorPosition = localizedAnchors[0].transform.position;
                AlignmentCompletedStatic = true;
                
                // Notify host that client has aligned
                if (Runner != null && !Object.HasStateAuthority)
                {
                    RPC_NotifyClientAligned();
                }
                
                // Update UI with success
                if (statusText != null)
                {
                    statusText.text = "Aligned\n1 anchor found\n\nReady to play";
                }
                
                UpdateUIWizard();
            }
            else
            {
                Log("No anchors localized. Cannot align.", true);
                Log("Make sure you're in the same physical location as the host", true);
                
                // Update UI with failure
                if (statusText != null)
                {
                    statusText.text = "Failed to localize\n\nWalk around slowly\nnear host's location";
                }
            }

            UpdateAllUI();
        }
        catch (Exception e)
        {
            Log($"Error during anchor loading: {e.Message}", true);
            Log($"Stack: {e.StackTrace}", true);
        }
    }

    // Override ShareAnchors to share the list we created
    protected override async void ShareAnchors()
    {
        try
        {
            if (currentAnchors.Count == 0)
            {
                Log("No anchors to share! Did you create anchors first?", true);
                return;
            }

            // Check if we have a valid group UUID
            if (_sharedAnchorGroupId == Guid.Empty)
            {
                Log("ERROR: Group UUID is empty! Advertisement may not have completed.", true);
                Log("Attempting to start advertisement first...");
                AdvertiseColocationSession();
                return; // Will retry via callback chain
            }

            Log($"Waiting for {currentAnchors.Count} anchors to fully stabilize...");

            // CRITICAL: Wait for all anchors to be fully localized and stable
            await Task.Delay(3000); // Give anchors 3 seconds to stabilize

            // Verify all anchors are still valid and localized
            int localizedCount = 0;
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.Localized)
                {
                    localizedCount++;
                    Log($"Anchor {anchor.Uuid.ToString().Substring(0, 8)} is localized at {anchor.transform.position}");
                }
                else
                {
                    Log($"WARNING: Anchor {anchor?.Uuid.ToString().Substring(0, 8)} is NOT localized yet", true);
                }
            }

            if (localizedCount < currentAnchors.Count)
            {
                Log($"Only {localizedCount}/{currentAnchors.Count} anchors localized. Waiting 5 more seconds...");
                await Task.Delay(5000);
                
                // Recheck
                localizedCount = 0;
                foreach (var anchor in currentAnchors)
                {
                    if (anchor != null && anchor.Localized)
                        localizedCount++;
                }
                Log($"After additional wait: {localizedCount}/{currentAnchors.Count} anchors localized");
            }
            
            // Abort if no anchors are localized
            if (localizedCount == 0)
            {
                Log("ERROR: No anchors are localized! Cannot share.", true);
                Log("Try moving around slowly to help Quest scan the environment.", true);
                return;
            }

            Log($"Saving and sharing {localizedCount} anchors to Group: {_sharedAnchorGroupId}...");

            // Save all anchors
            var anchorsToShare = new List<OVRSpatialAnchor>();
            foreach (var anchor in currentAnchors)
            {
                 if (anchor != null && anchor.Localized)
                 {
                     Log($"Saving anchor {anchor.Uuid.ToString().Substring(0, 8)}...");
                     var saveResult = await anchor.SaveAnchorAsync();
                     if (!saveResult.Success)
                     {
                         Log($"Failed to save anchor {anchor.Uuid.ToString().Substring(0, 8)}: Status={saveResult.Status}", true);

                         // Provide specific error guidance
                         if (saveResult.Status.ToString().Contains("Pending"))
                         {
                             Log("Anchor save is pending. Waiting and retrying...");
                             await Task.Delay(2000);
                             saveResult = await anchor.SaveAnchorAsync();
                             if (saveResult.Success)
                             {
                                 Log($"Anchor {anchor.Uuid.ToString().Substring(0, 8)} saved on retry");
                                 anchorsToShare.Add(anchor);
                             }
                         }
                     }
                     else
                     {
                         Log($"Anchor {anchor.Uuid.ToString().Substring(0, 8)} saved successfully");
                         anchorsToShare.Add(anchor);
                     }
                 }
                 else
                 {
                     Log($"Skipping anchor: null={anchor == null}, localized={anchor?.Localized}", true);
                 }
            }

            if (anchorsToShare.Count == 0)
            {
                Log("No valid anchors to share after save attempt.", true);
                Log("TROUBLESHOOTING: Make sure cloud storage is enabled in Meta Quest settings.", true);
                return;
            }

            Log($"Sharing {anchorsToShare.Count} anchors to group {_sharedAnchorGroupId}...");
            Log($"Anchor UUIDs being shared:");
            foreach (var anchor in anchorsToShare)
            {
                Log($"  - {anchor.Uuid} at {anchor.transform.position}");
            }
            
            var shareResult = await OVRSpatialAnchor.ShareAsync(anchorsToShare, _sharedAnchorGroupId);
            Log($"ShareAsync completed. Success={shareResult.Success}, Status={shareResult.Status}");

            if (shareResult.Success)
            {
                Log($"Sharing complete. {anchorsToShare.Count} anchors shared. Waiting for client...");
                currentStep = AlignmentStep.Done;
                currentState = SessionState.Sharing; // Host is sharing, not aligned until client joins
                
                // IMPORTANT: Set networked flag so client knows anchors are ready
                HostAnchorsShared = true;
                SharedAnchorGroupUuidString = _sharedAnchorGroupId.ToString();
                Log($"Networked anchor state set: HostAnchorsShared=true, UUID={_sharedAnchorGroupId.ToString().Substring(0, 8)}");

                // HOST ALIGNMENT
                if (anchorsToShare.Count >= 2)
                {
                    // Host aligns to its own anchors
                    Log("Host: Aligning to 2 anchors...");
                    _localizedAnchor = anchorsToShare[0];
                    
                    // Log anchor positions BEFORE alignment
                    Debug.Log($"[Anchor] HOST Pre-align Anchor1: {anchorsToShare[0].transform.position}");
                    Debug.Log($"[Anchor] HOST Pre-align Anchor2: {anchorsToShare[1].transform.position}");
                    
                    alignmentManager.AlignUserToTwoAnchors(anchorsToShare[0], anchorsToShare[1]);
                    
                    // Store anchor positions and mark alignment complete
                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    SecondAnchorPosition = anchorsToShare[1].transform.position;
                    AlignmentCompletedStatic = true;
                    
                    Debug.Log($"[Anchor] HOST Stored positions: Anchor1={FirstAnchorPosition}, Anchor2={SecondAnchorPosition}");
                }
                else
                {
                    Log("Host: Aligning to single anchor...");
                    _localizedAnchor = anchorsToShare[0];
                    alignmentManager.AlignUserToAnchor(anchorsToShare[0]);
                    
                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    AlignmentCompletedStatic = true;
                }

                UpdateUIWizard();
            }
            else
            {
                Log($"Failed to share anchors. Status: {shareResult.Status}", true);
                Log("TROUBLESHOOTING:", true);
                Log("1. Check Meta Quest Settings > Privacy > Spatial Data > Allow apps to share", true);
                Log("2. Make sure both devices are signed into Meta accounts", true);
                Log("3. Ensure internet connection is active", true);
                Log($"4. Group UUID: {_sharedAnchorGroupId}", true);
                
                // Reset state to allow retry
                currentStep = AlignmentStep.ShareFailed;
                currentState = SessionState.Advertising; // Keep advertising so client can still discover
                UpdateUIWizard();
                
                // Show error in status text
                if (statusText != null)
                {
                    statusText.text = $"Sharing FAILED!\n{shareResult.Status}\n\nPress button to RETRY";
                }
            }
        }
        catch (Exception e)
        {
            Log($"Error in ShareAnchors: {e.Message}", true);
            Log($"Stack trace: {e.StackTrace}", true);
        }
    }

    private void OnSpawnCubeClicked()
    {
#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            Log("Starting network session first...");
            StartNetworkSession();
            return;
        }

        if (!IsAlignmentComplete())
        {
            Log("Not aligned! Click Auto Align first.", true);
            return;
        }

        if (!cubePrefab.IsValid)
        {
            Log("Cube prefab not assigned!", true);
            return;
        }

        // Get controller position and convert to anchor-relative
        Vector3 worldPos = GetControllerSpawnPosition();
        Vector3 anchorRelativePos = worldPos;
        
        if (_localizedAnchor != null && _localizedAnchor.Localized)
        {
            anchorRelativePos = _localizedAnchor.transform.InverseTransformPoint(worldPos);
            Debug.Log($"[Anchor] Spawn: World {worldPos} -> Anchor-relative {anchorRelativePos}");
        }
        else
        {
            Debug.LogWarning("[Anchor] No localized anchor! Using world position directly.");
        }

        // Request spawn via RPC if not host
        if (!Object.HasStateAuthority)
        {
            RPC_RequestSpawnCube(anchorRelativePos);
        }
        else
        {
            SpawnCubeAtAnchorPosition(anchorRelativePos);
        }
        
        Log("Spawning cube!");
#else
        Log("Photon Fusion not available!", true);
#endif
    }

#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnCube(Vector3 anchorRelativePos)
    {
        Debug.Log($"[Anchor] Host received spawn request at anchor-relative: {anchorRelativePos}");
        SpawnCubeAtAnchorPosition(anchorRelativePos);
    }

    private void SpawnCubeAtAnchorPosition(Vector3 anchorRelativePos)
    {
        // Clear existing cube first (limit to 1)
        if (spawnedCube != null && Runner != null)
        {
            Debug.Log($"[Anchor] Despawning existing cube: {spawnedCube.Id}");
            Runner.Despawn(spawnedCube);
            spawnedCube = null;
        }

        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            Debug.LogError("[Anchor] Cannot spawn cube - no localized anchor!");
            return;
        }

        // Spawn at world position first (required by Fusion)
        Vector3 worldPos = _localizedAnchor.transform.TransformPoint(anchorRelativePos);
        Debug.Log($"[Anchor] Spawning cube at world {worldPos}, will parent to anchor");

        var newCube = Runner.Spawn(
            cubePrefab,
            worldPos,
            Quaternion.identity,
            Object.InputAuthority
        );

        if (newCube != null)
        {
            // Parent to anchor so position is relative to anchor on all devices
            newCube.transform.SetParent(_localizedAnchor.transform, worldPositionStays: false);
            newCube.transform.localPosition = anchorRelativePos;
            newCube.transform.localRotation = Quaternion.identity;
            newCube.transform.localScale = Vector3.one * cubeScale;
            
            spawnedCube = newCube;
            Debug.Log($"[Anchor] Cube parented to anchor at local pos {anchorRelativePos}! NetworkId: {newCube.Id}");
        }
        else
        {
            Debug.LogError("[Anchor] Failed to spawn cube!");
        }
    }

    private void DespawnAllCubesOnHost()
    {
        // Only the host (state authority) should despawn networked cubes
        if (!Object.HasStateAuthority || Runner == null || !Runner.IsRunning)
        {
            Debug.Log("[Anchor] Not host or runner not ready, cannot despawn cubes");
            return;
        }
        
        // Find and despawn all NetworkedCube objects via Fusion
        var allCubes = FindObjectsOfType<NetworkedCube>();
        Debug.Log($"[Anchor] Host despawning {allCubes.Length} cubes via network");
        
        foreach (var cube in allCubes)
        {
            if (cube != null && cube.Object != null && cube.Object.IsValid)
            {
                Debug.Log($"[Anchor] Despawning cube: {cube.Object.Id}");
                Runner.Despawn(cube.Object);
            }
        }
        spawnedCube = null;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestDespawnAllCubes()
    {
        Debug.Log("[Anchor] Host received request to despawn all cubes");
        DespawnAllCubesOnHost();
    }
#endif

    private void OnResetClicked()
    {
        Debug.Log("[Anchor] Reset clicked - clearing scene and session");
        
        // Stop periodic alignment
        if (alignmentManager != null)
        {
            alignmentManager.StopPeriodicAlignment();
        }
        
        // Clear all spawned cubes via network
#if FUSION2
        if (Runner != null && Runner.IsRunning)
        {
            if (Object.HasStateAuthority)
            {
                // Host: despawn cubes directly
                DespawnAllCubesOnHost();
            }
            else
            {
                // Client: request host to despawn cubes
                Debug.Log("[Anchor] Client requesting host to despawn cubes");
                RPC_RequestDespawnAllCubes();
            }
            Log("Cleared all cubes");
        }
#endif

        // Destroy all anchors
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null)
            {
                Debug.Log($"[Anchor] Destroying anchor: {anchor.Uuid}");
                Destroy(anchor.gameObject);
            }
        }
        currentAnchors.Clear();

        // Reset colocation session
        ResetColocationSession();

        // Reset camera rig to origin for fresh alignment
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            cameraRig.transform.position = Vector3.zero;
            cameraRig.transform.rotation = Quaternion.identity;
            Debug.Log("[Anchor] Camera rig reset to origin");
        }

        // Reset state
        currentState = SessionState.Idle;
        currentStep = AlignmentStep.Start; // Reset wizard
        _sharedAnchorGroupId = Guid.Empty;
        _localizedAnchor = null;
        anchor1Placed = false;
        anchor2Placed = false;
        waitingForGripToPlaceAnchors = false;
        firstAnchorWorldPosition = Vector3.zero;
        isPlacingAnchor = false; // Reset placement lock
        hostAutoStarted = false; // Allow auto-start again
        
        // Hide all placement UI
        if (distanceLine != null) distanceLine.enabled = false;
        if (leftDistanceDisplay != null) leftDistanceDisplay.SetActive(false);
        if (rightDistanceDisplay != null) rightDistanceDisplay.SetActive(false);
        
        if (autoAlignButton != null) autoAlignButton.interactable = true; // Re-enable
        
        // Reset UI
        UpdateAllUI();
        UpdateUIWizard();
        Log("Scene reset. Click Start Alignment to start fresh");
    }

    // ==================== START GAME (TABLE TENNIS) ====================
    
    private void OnStartGameClicked()
    {
        Debug.Log("[Anchor] Start Game clicked");
        
        // Check if aligned first
        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            Log("\u26a0\ufe0f Please complete alignment first!", true);
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            Log("\u26a0\ufe0f Network not ready! Please wait...", true);
            return;
        }
        
        // Check if both devices are aligned
        bool bothDevicesAligned = currentState == SessionState.ClientAligned || currentState == SessionState.HostAligned;
        if (!bothDevicesAligned)
        {
            Log("\u23f3 Waiting for both devices to be aligned...", true);
            Log("Make sure your partner has completed alignment too!");
            return;
        }

        // Either player can initiate - request goes to host, host loads scene
        if (Object.HasStateAuthority)
        {
            Log("\u25b6\ufe0f Starting game for all players...");
            HideMainGUIPanel(); // Hide UI before loading scene
            LoadTableTennisSceneNetworked();
        }
        else
        {
            Log("Requesting to start game...");
            HideMainGUIPanel(); // Hide UI before scene loads
            RPC_RequestStartGame();
        }
#else
        // Non-networked fallback
        LoadTableTennisSceneLocal();
#endif
    }

    // ==================== PASSTHROUGH GAME ====================
    
    /// <summary>
    /// Start passthrough table tennis - spawns table at anchor midpoint without loading new scene
    /// </summary>
    private void OnStartPassthroughGameClicked()
    {
        Debug.Log("[Passthrough] Start Passthrough Game clicked");
        
        // DEBUG MODE: Skip all alignment checks
        if (skipAlignmentForDebug)
        {
            Debug.Log("[Passthrough] DEBUG MODE: Skipping alignment checks!");
            StartPassthroughGameDebug();
            return;
        }
        
        // Check if aligned first
        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            Log("Please complete alignment first", true);
            return;
        }
        
        // Check if we have both anchors
        if (currentAnchors == null || currentAnchors.Count < 2)
        {
            Log("Need 2 anchors for passthrough mode", true);
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            Log("Network not ready. Please wait...", true);
            return;
        }
        
        // Check if both devices are aligned
        bool bothDevicesAligned = currentState == SessionState.ClientAligned || currentState == SessionState.HostAligned;
        if (!bothDevicesAligned)
        {
            Log("Waiting for both devices to be aligned...", true);
            return;
        }

        // Either player can initiate
        if (Object.HasStateAuthority)
        {
            Log("Starting passthrough game...");
            StartPassthroughGame();
            RPC_StartPassthroughGame();
        }
        else
        {
            Log("Requesting passthrough game...");
            RPC_RequestPassthroughGame();
        }
#else
        StartPassthroughGame();
#endif
    }

#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestPassthroughGame()
    {
        Debug.Log("[Passthrough] Host received request for passthrough game");
        StartPassthroughGame();
        RPC_StartPassthroughGame();
    }
    
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_StartPassthroughGame()
    {
        Debug.Log("[Passthrough] Received passthrough game start notification");
        if (!isPassthroughMode) // Don't start twice on host
        {
            StartPassthroughGame();
        }
    }
#endif

    /// <summary>
    /// Actually start the passthrough game - spawn table and show instructions
    /// </summary>
    private void StartPassthroughGame()
    {
        isPassthroughMode = true;
        passthroughPhase = PassthroughGamePhase.TableAdjust;
        
        // Disable anchor placement mode
        waitingForGripToPlaceAnchors = false;
        
        // Hide alignment panel
        if (buttonPanel != null)
        {
            buttonPanel.SetActive(false);
        }
        
        // Hide the entire main UI Canvas that's attached to the Camera Rig
        HideMainGUIPanel();
        
        // Check if we have anchors - client might need to wait for anchor loading
        if (currentAnchors == null || currentAnchors.Count < 2)
        {
            Debug.Log("[Passthrough] No anchors yet, waiting for anchor loading...");
            StartCoroutine(WaitForAnchorsAndSpawnTable());
        }
        else
        {
            // Spawn or enable the table at anchor midpoint
            SpawnPassthroughTable();
            
            // Client: apply networked state immediately after spawning
#if FUSION2
            if (Object != null && !Object.HasStateAuthority && spawnedPassthroughTable != null)
            {
                ApplyPassthroughTableState();
                Debug.Log("[Passthrough] Client: Applied networked table state after spawn");
            }
#endif
        }
        
        // Enable rackets on both hands (NetworkedPlayer will sync visibility to remote players)
        StartCoroutine(EnableRacketsDelayed());
        
        // Switch UI to show table adjustment instructions
        UpdatePassthroughInstructions();
        
        Log("Table Adjust Mode - Use thumbstick to adjust, B/Y toggles rackets, A/X when ready");
    }
    
    /// <summary>
    /// DEBUG MODE: Start passthrough game without anchor alignment
    /// Places table at a fixed position in front of the player
    /// </summary>
    private void StartPassthroughGameDebug()
    {
        Debug.Log("[Passthrough] Starting DEBUG mode without anchors");
        
        isPassthroughMode = true;
        passthroughPhase = PassthroughGamePhase.TableAdjust;
        
        // Disable anchor placement mode
        waitingForGripToPlaceAnchors = false;
        
        // Hide alignment panel
        if (buttonPanel != null)
        {
            buttonPanel.SetActive(false);
        }
        
        // Hide the entire main UI Canvas
        HideMainGUIPanel();
        
        // Find camera rig for reference
        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        
        // Spawn table at debug position
        SpawnPassthroughTableDebug(cameraRig);
        
        // Enable rackets
        StartCoroutine(EnableRacketsDelayed());
        
        // Update UI
        UpdatePassthroughInstructions();
        
        Log("DEBUG MODE - Table placed without anchors");
    }
    
    /// <summary>
    /// DEBUG: Spawn table at fixed position without anchors
    /// </summary>
    private void SpawnPassthroughTableDebug(OVRCameraRig cameraRig)
    {
        // Calculate world position
        Vector3 worldPos = debugTablePosition;
        if (cameraRig != null)
        {
            // Place in front of player
            worldPos = cameraRig.transform.position + cameraRig.transform.forward * debugTablePosition.z;
            worldPos.y = debugTablePosition.y;
        }
        
        // Find existing table in scene first
        spawnedPassthroughTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
        
        if (spawnedPassthroughTable != null)
        {
            // Use existing table
            spawnedPassthroughTable.transform.position = worldPos;
            spawnedPassthroughTable.transform.rotation = Quaternion.identity;
            spawnedPassthroughTable.SetActive(true);
            Debug.Log($"[Passthrough DEBUG] Activated existing table at {worldPos}");
        }
        else if (tablePrefab != null)
        {
            // Spawn from prefab
            spawnedPassthroughTable = Instantiate(tablePrefab, worldPos, Quaternion.identity);
            spawnedPassthroughTable.name = "PingPongTable";
            Debug.Log($"[Passthrough DEBUG] Spawned table prefab at {worldPos}");
        }
        else
        {
            Debug.LogWarning("[Passthrough DEBUG] No table found and no prefab assigned!");
            return;
        }
    }
    
    /// <summary>
    /// Coroutine to wait for anchors to be loaded before spawning table (for client)
    /// </summary>
    private IEnumerator WaitForAnchorsAndSpawnTable()
    {
        float timeout = 10f;
        float elapsed = 0f;
        
        while ((currentAnchors == null || currentAnchors.Count < 2) && elapsed < timeout)
        {
            Debug.Log($"[Passthrough] Waiting for anchors... ({elapsed:F1}s)");
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }
        
        if (currentAnchors != null && currentAnchors.Count >= 2)
        {
            Debug.Log("[Passthrough] Anchors loaded, spawning table");
            SpawnPassthroughTable();
            
            // Client: apply networked state immediately after spawning
#if FUSION2
            if (Object != null && !Object.HasStateAuthority && spawnedPassthroughTable != null)
            {
                ApplyPassthroughTableState();
                Debug.Log("[Passthrough] Client: Applied networked table state after delayed spawn");
            }
#endif
        }
        else
        {
            Debug.LogError("[Passthrough] Timeout waiting for anchors! Cannot spawn table.");
            Log("Anchor loading timeout - table not spawned");
        }
    }
    
    // Reference to hidden main GUI for restoring later
    private GameObject hiddenMainGUICanvas;
    private List<GameObject> hiddenUIElements = new List<GameObject>();
    
    /// <summary>
    /// Hide the main GUI panel that's a child of the Camera Rig and any other alignment UI
    /// </summary>
    private void HideMainGUIPanel()
    {
        hiddenUIElements.Clear();
        
        // Hide button panel
        if (buttonPanel != null && buttonPanel.activeSelf)
        {
            buttonPanel.SetActive(false);
            hiddenUIElements.Add(buttonPanel);
        }
        
        // Hide instruction panel
        if (instructionPanel != null && instructionPanel.activeSelf)
        {
            instructionPanel.SetActive(false);
            hiddenUIElements.Add(instructionPanel);
        }
        
        // Find and hide Canvas under Camera Rig
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            var canvases = cameraRig.GetComponentsInChildren<Canvas>(true);
            foreach (var canvas in canvases)
            {
                if (canvas != null && canvas.gameObject != passthroughGameUIPanel && canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(false);
                    hiddenUIElements.Add(canvas.gameObject);
                    hiddenMainGUICanvas = canvas.gameObject;
                }
            }
        }
        
        // Also disable this script's gameobject if it has UI components attached
        var myCanvas = GetComponentInChildren<Canvas>(true);
        if (myCanvas != null && myCanvas.gameObject.activeSelf)
        {
            myCanvas.gameObject.SetActive(false);
            hiddenUIElements.Add(myCanvas.gameObject);
        }
        
        // Also find and hide ALL world-space canvases in the scene
        var allCanvases = FindObjectsOfType<Canvas>(true);
        foreach (var canvas in allCanvases)
        {
            if (canvas == null) continue;
            if (canvas.gameObject == passthroughGameUIPanel) continue;
            if (hiddenUIElements.Contains(canvas.gameObject)) continue;
            
            string nameLower = canvas.gameObject.name.ToLower();
            if (canvas.renderMode == RenderMode.WorldSpace || 
                nameLower.Contains("menu") || nameLower.Contains("panel") || 
                nameLower.Contains("gui") || nameLower.Contains("ui") ||
                nameLower.Contains("button") || nameLower.Contains("align"))
            {
                if (canvas.gameObject.activeSelf)
                {
                    canvas.gameObject.SetActive(false);
                    hiddenUIElements.Add(canvas.gameObject);
                }
            }
        }
    }
    
    /// <summary>
    /// Show the main GUI panel again (when exiting passthrough mode)
    /// </summary>
    private void ShowMainGUIPanel()
    {
        foreach (var uiElement in hiddenUIElements)
        {
            if (uiElement != null)
            {
                uiElement.SetActive(true);
            }
        }
        hiddenUIElements.Clear();
        
        if (hiddenMainGUICanvas != null)
        {
            hiddenMainGUICanvas.SetActive(true);
            hiddenMainGUICanvas = null;
        }
        
        Debug.Log("[Passthrough] Restored main GUI panel");
    }
    private System.Collections.IEnumerator EnableRacketsDelayed()
    {
        Debug.Log("[Passthrough] Enabling rackets after short delay...");
        yield return new WaitForSeconds(0.5f);
        EnableRackets();
    }
    
    /// <summary>
    /// Spawn the table tennis table at the midpoint between anchors
    /// </summary>
    private void SpawnPassthroughTable()
    {
        if (currentAnchors == null || currentAnchors.Count < 2)
        {
            Debug.LogWarning("[Passthrough] Cannot spawn table - need 2 anchors");
            return;
        }
        
        Transform primaryAnchor = currentAnchors[0].transform;
        Transform secondaryAnchor = currentAnchors[1].transform;
        
        // Calculate midpoint in LOCAL space (relative to primary anchor)
        // Primary anchor is at (0,0,0) local, secondary is at its local position
        Vector3 secondaryLocalPos = primaryAnchor.InverseTransformPoint(secondaryAnchor.position);
        Vector3 localMidpoint = secondaryLocalPos / 2f;
        
        // Use defaultTableHeight for proper table height (in local space)
        Vector3 localTablePos = new Vector3(localMidpoint.x, defaultTableHeight, localMidpoint.z);
        
        // Calculate rotation so table's LONG EDGE is along anchor line (players face each other)
        Vector3 direction = secondaryLocalPos;
        direction.y = 0;
        float yRotation = 0f;
        if (direction.sqrMagnitude > 0.01f)
        {
            // Base rotation to face along anchor line
            yRotation = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            // Add configurable offset so table edge faces the anchor line
            yRotation += tableYRotationOffset;
        }
        
        // X rotation to flip table right-side up if needed (some prefabs are upside down)
        float xRotation = tableXRotationOffset;
        
        // If table already exists, just update its local position
        if (spawnedPassthroughTable != null)
        {
            // Ensure parented to anchor
            if (spawnedPassthroughTable.transform.parent != primaryAnchor)
            {
                spawnedPassthroughTable.transform.SetParent(primaryAnchor, worldPositionStays: false);
            }
            spawnedPassthroughTable.transform.localPosition = localTablePos;
            spawnedPassthroughTable.transform.localRotation = Quaternion.Euler(xRotation, yRotation, 0);
            spawnedPassthroughTable.SetActive(true);
            Debug.Log($"[Passthrough] Repositioned table at localPos: {localTablePos}, rotation: Y={yRotation}");
            return;
        }
        
        // Try to find existing table in scene first
        spawnedPassthroughTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
        
        if (spawnedPassthroughTable != null)
        {
            // Use existing table - parent to primary anchor and set local position
            spawnedPassthroughTable.transform.SetParent(primaryAnchor, worldPositionStays: false);
            spawnedPassthroughTable.transform.localPosition = localTablePos;
            spawnedPassthroughTable.transform.localRotation = Quaternion.Euler(xRotation, yRotation, 0);
            spawnedPassthroughTable.SetActive(true);
            Debug.Log($"[Passthrough] Positioned existing table at localPos: {localTablePos}, rotation: Y={yRotation}");
        }
        else if (TablePrefab != null)
        {
            // Spawn from prefab - parent to primary anchor with local coordinates
            spawnedPassthroughTable = Instantiate(TablePrefab);
            spawnedPassthroughTable.transform.SetParent(primaryAnchor, worldPositionStays: false);
            spawnedPassthroughTable.transform.localPosition = localTablePos;
            spawnedPassthroughTable.transform.localRotation = Quaternion.Euler(xRotation, yRotation, 0);
            Debug.Log($"[Passthrough] Spawned table at localPos: {localTablePos}, rotation: Y={yRotation}");
        }
        else
        {
            Debug.LogWarning("[Passthrough] No table prefab assigned and no table found in scene!");
            Log("Table not found. Assign TablePrefab in inspector or shared config.");
            return;
        }
        
#if FUSION2
        // Initialize networked table state (host only)
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedPassthroughTableYRotation = yRotation;
            NetworkedPassthroughTableHeight = localTablePos.y;
            NetworkedPassthroughGameActive = true;
            Debug.Log($"[Passthrough] Host: Initialized networked table state - Y rot: {yRotation}, height: {localTablePos.y}");
        }
#endif
        
        // Spawn rackets for passthrough mode (hidden initially, will be attached to controllers by EnableRackets)
        SpawnPassthroughRackets();
        
        // Create game UI panel 2m away from table
        CreatePassthroughGameUIPanel();
    }
    
    // Spawned racket templates for passthrough (hidden, used as source for controller rackets)
    private GameObject passthroughRacketTemplate;
    
    /// <summary>
    /// Spawn racket templates for passthrough mode. These are hidden and used as templates
    /// for the rackets that get attached to controllers.
    /// </summary>
    private void SpawnPassthroughRackets()
    {
        // Don't spawn if already exists
        if (passthroughRacketTemplate != null) return;
        
        // Create a racket template (will be cloned for left/right hands)
        if (RacketPrefab != null)
        {
            // Use prefab if available
            passthroughRacketTemplate = Instantiate(RacketPrefab);
            passthroughRacketTemplate.name = "PassthroughRacketTemplate";
            Debug.Log("[Passthrough] Spawned racket template from prefab");
        }
        else
        {
            // Create a simple placeholder racket
            passthroughRacketTemplate = CreatePlaceholderRacket("PassthroughRacketTemplate");
            Debug.Log("[Passthrough] Created placeholder racket template");
        }
        
        // Tag it so EnableRackets can find it
        passthroughRacketTemplate.tag = "Racket";
        
        // Parent to table and hide it (it's just a template)
        if (spawnedPassthroughTable != null)
        {
            passthroughRacketTemplate.transform.SetParent(spawnedPassthroughTable.transform);
        }
        passthroughRacketTemplate.transform.localPosition = new Vector3(0, 0.5f, 0); // Above table
        passthroughRacketTemplate.SetActive(false); // Hidden - just a template
        
        Debug.Log("[Passthrough] Racket template created and hidden (will be cloned for controllers)");
    }
    
    /// <summary>
    /// Create a world-space UI panel 2m from the table, facing toward it
    /// Shows: Score, Info, Status, Controls (like GameUIPanel_Simple)
    /// </summary>
    private void CreatePassthroughGameUIPanel()
    {
        if (passthroughGameUIPanel != null) return; // Already created
        
        if (currentAnchors == null || currentAnchors.Count < 2) return;
        
        Transform primaryAnchor = currentAnchors[0].transform;
        Transform secondaryAnchor = currentAnchors[1].transform;
        
        // Calculate table midpoint (world space)
        Vector3 tableMidpoint = (primaryAnchor.position + secondaryAnchor.position) / 2f;
        tableMidpoint.y = 1.5f; // Eye level for readability
        
        // Direction from primary anchor to secondary (along table)
        Vector3 anchorDirection = (secondaryAnchor.position - primaryAnchor.position).normalized;
        anchorDirection.y = 0;
        
        // Position panel 3m behind primary anchor (player 1 side)
        Vector3 panelPosition = primaryAnchor.position - anchorDirection * 3f;
        panelPosition.y = 1.5f; // Eye level
        
        // Face toward the table (rotate 180 on Y to fix mirrored text)
        Quaternion panelRotation = Quaternion.LookRotation(-anchorDirection);
        
        // Create the panel
        passthroughGameUIPanel = new GameObject("PassthroughGameUI");
        passthroughGameUIPanel.transform.position = panelPosition;
        passthroughGameUIPanel.transform.rotation = panelRotation;
        
        // Create background quad
        var background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Background";
        background.transform.SetParent(passthroughGameUIPanel.transform);
        background.transform.localPosition = Vector3.zero;
        background.transform.localRotation = Quaternion.identity;
        background.transform.localScale = new Vector3(2.5f, 2.8f, 1f); // Larger panel for better visibility
        
        // Semi-transparent dark blue background
        var bgRenderer = background.GetComponent<Renderer>();
        var bgMaterial = new Material(Shader.Find("UI/Default"));
        bgMaterial.color = new Color(0.05f, 0.1f, 0.3f, 0.8f); // Dark blue
        bgRenderer.material = bgMaterial;
        
        // Remove collider
        var bgCollider = background.GetComponent<Collider>();
        if (bgCollider != null) Destroy(bgCollider);
        
        // Create Score text (top, large, yellow) - larger for visibility
        passthroughScoreText = CreatePanelText("ScoreText", "0 - 0", 
            new Vector3(0, 1.0f, -0.01f), 0.07f, Color.yellow);
        
        // Create Info text (below score, green) - larger text
        passthroughInfoText = CreatePanelText("InfoText", "Passthrough Mode", 
            new Vector3(0, 0.65f, -0.01f), 0.02f, Color.green);
        
        // Create Status text (center, white) - larger text
        passthroughStatusText = CreatePanelText("StatusText", "Adjusting Table...", 
            new Vector3(0, 0.2f, -0.01f), 0.022f, Color.white);
        
        // Create Controls text (bottom, cyan) - larger text
        passthroughControlsText = CreatePanelText("ControlsText", 
            "R-Stick: Rotate/Height | A/X: Next | GRIP: Ball", 
            new Vector3(0, -0.7f, -0.01f), 0.014f, Color.cyan);
        
        Debug.Log($"[Passthrough] Created game UI panel at {panelPosition}");
    }
    
    /// <summary>
    /// Helper to create TextMesh for the panel
    /// </summary>
    private TextMesh CreatePanelText(string name, string text, Vector3 localPos, float charSize, Color color)
    {
        var textObj = new GameObject(name);
        textObj.transform.SetParent(passthroughGameUIPanel.transform);
        textObj.transform.localPosition = localPos;
        textObj.transform.localRotation = Quaternion.identity;
        textObj.transform.localScale = Vector3.one;
        
        var textMesh = textObj.AddComponent<TextMesh>();
        textMesh.text = text;
        textMesh.characterSize = charSize;
        textMesh.fontSize = 100;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = color;
        
        // Use a font that renders well in VR
        textMesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (textMesh.font == null)
        {
            textMesh.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        
        return textMesh;
    }
    
    /// <summary>
    /// Update the passthrough game UI panel text based on current phase
    /// </summary>
    private void UpdatePassthroughGameUI()
    {
        if (passthroughGameUIPanel == null) return;
        
        // Score - get from game manager if available, else 0-0
        string scoreText = "0 - 0";
        // TODO: Hook up to actual score tracking if implemented
        
        // Info
        bool isHost = false;
#if FUSION2
        isHost = Object != null && Object.HasStateAuthority;
#endif
        string infoText = isHost ? "You are P1 (Host)" : "You are P2 (Client)";
        
        // Status based on phase
        string statusText = "";
        switch (passthroughPhase)
        {
            case PassthroughGamePhase.TableAdjust:
                statusText = "Adjusting Table...";
                break;
            case PassthroughGamePhase.BallPosition:
                statusText = "Grip to Spawn Ball";
                break;
            case PassthroughGamePhase.Playing:
                statusText = "PLAY!";
                break;
            default:
                statusText = "Ready";
                break;
        }
        
        // Controls based on phase
        string controlsText = "";
        switch (passthroughPhase)
        {
            case PassthroughGamePhase.TableAdjust:
                controlsText = "R-Stick: Rotate/Height\nA/X: Confirm | B/Y: Rackets";
                break;
            case PassthroughGamePhase.BallPosition:
                controlsText = "GRIP: Spawn Ball\nHit ball to start!";
                break;
            case PassthroughGamePhase.Playing:
                controlsText = "GRIP: Switch Hand\nMENU: Game Menu";
                break;
            default:
                controlsText = "";
                break;
        }
        
        // Apply text
        if (passthroughScoreText != null) passthroughScoreText.text = scoreText;
        if (passthroughInfoText != null) passthroughInfoText.text = infoText;
        if (passthroughStatusText != null) passthroughStatusText.text = statusText;
        if (passthroughControlsText != null) passthroughControlsText.text = controlsText;
    }
    
    /// <summary>
    /// Update instruction panel based on current game phase
    /// </summary>
    private void UpdatePassthroughInstructions()
    {
        // Hide button panel
        if (buttonPanel != null)
        {
            buttonPanel.SetActive(false);
        }
        
        // Update the world-space game UI panel
        UpdatePassthroughGameUI();
        
        // Show instruction panel (if still used for something)
        if (instructionPanel != null)
        {
            instructionPanel.SetActive(false); // Hide old instruction panel, use new world-space one
        }
        
        string instructions = "";
        
        switch (passthroughPhase)
        {
            case PassthroughGamePhase.TableAdjust:
                if (Object.HasStateAuthority)
                {
                    instructions = "TABLE ADJUSTMENT (HOST)\n\n" +
                        "Right Stick X: Rotate\n" +
                        "Right Stick Y: Height\n" +
                        "B/Y: Toggle rackets\n" +
                        "A/X: Confirm";
                }
                else
                {
                    instructions = "WAITING FOR HOST\n\n" +
                        "Host is adjusting table...\n" +
                        "B/Y: Toggle rackets";
                }
                break;
                
            case PassthroughGamePhase.BallPosition:
                instructions = "BALL POSITION\n\n" +
                    "Right Stick: Adjust table\n" +
                    "GRIP: Spawn ball\n" +
                    "A/X + Stick: Move ball\n" +
                    "B/Y: Toggle rackets\n\n" +
                    "Hit ball to START!";
                break;
                
            case PassthroughGamePhase.Playing:
                instructions = "GAME ON\n\n" +
                    "Hit ball back and forth!\n" +
                    "First to 11 wins!";
                break;
        }
        
        if (instructionText != null)
        {
            instructionText.text = instructions;
        }
        else
        {
            Log(instructions);
        }
    }
    
    /// <summary>
    /// Handle input during passthrough game
    /// </summary>
    private void HandlePassthroughGameInput()
    {
        // MENU button - toggle game menu
        bool menuButtonPressed = OVRInput.GetDown(OVRInput.Button.Start);
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
        
        // B/Y button - toggle racket visibility (except during Playing phase)
        bool bButtonPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool yButtonPressed = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch);
        
        if ((bButtonPressed || yButtonPressed) && passthroughPhase != PassthroughGamePhase.Playing)
        {
            ToggleRacketVisibility();
            return;
        }
        
        // A/X button detection for phase-specific handling
        bool aButtonPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool xButtonPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        // Also check if A/X is HELD (for continuous ball adjustment)
        bool aButtonHeld = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool xButtonHeld = OVRInput.Get(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        bool axButtonHeld = aButtonHeld || xButtonHeld;
        
        // Handle phase-specific input
        switch (passthroughPhase)
        {
            case PassthroughGamePhase.TableAdjust:
                if (aButtonPressed || xButtonPressed)
                {
                    AdvancePassthroughPhase();
                }
                else
                {
                    HandleTableAdjustInput();
                    
                    // Allow GRIP to spawn ball directly from TableAdjust phase (auto-advance)
                    if (spawnedBall == null && !ballSpawnPending)
                    {
                        float leftGrip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger);
                        float rightGrip = OVRInput.Get(OVRInput.Axis1D.SecondaryHandTrigger);
                        bool gripPressed = leftGrip > 0.5f || rightGrip > 0.5f;
                        
                        if (gripPressed && !wasGripPressed)
                        {
                            Debug.Log($"[Passthrough] Grip in TableAdjust - auto-advancing to BallPosition and spawning ball");
                            ballSpawnPending = true;
                            passthroughPhase = PassthroughGamePhase.BallPosition;
                            SpawnPassthroughBall();
                            UpdatePassthroughInstructions();
                        }
                        wasGripPressed = gripPressed;
                    }
                }
                break;
                
            case PassthroughGamePhase.BallPosition:
                HandleBallPositionInput(aButtonPressed || xButtonPressed, axButtonHeld);
                break;
                
            case PassthroughGamePhase.Playing:
                // Game in progress - racket switching locked
                break;
        }
    }
    
    /// <summary>
    /// Toggle game menu visibility (MENU button)
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
        
        Log($"Game menu {(isGameMenuOpen ? "opened" : "closed")}");
    }
    
    /// <summary>
    /// Show the in-game menu (during passthrough game)
    /// </summary>
    private void ShowGameMenu()
    {
        // DON'T show the main buttonPanel - that's the alignment UI!
        // Instead, always use the runtime menu for passthrough game menu
        CreateRuntimeMenu();
        
        // Enable ray interactors for menu interaction
        EnableRayInteractors();
        
        // Hide rackets while menu is open
        if (leftRacket != null) leftRacket.SetActive(false);
        if (rightRacket != null) rightRacket.SetActive(false);
        
        Log("GAME MENU:\n  A/X: Resume Game\n  B/Y: Exit to Mode Selection\n  GRIP: Switch to VR Game\n  MENU: Close Menu");
    }
    
    /// <summary>
    /// Hide the in-game menu
    /// </summary>
    private void HideGameMenu()
    {
        // Hide button panel if it was shown
        if (buttonPanel != null && isPassthroughMode)
        {
            buttonPanel.SetActive(false);
        }
        
        if (runtimeMenuPanel != null)
        {
            runtimeMenuPanel.SetActive(false);
        }
        
        // Restore rackets if visible
        if (racketsVisible)
        {
            DisableRayInteractors();
            if (leftRacket != null) leftRacket.SetActive(true);
            if (rightRacket != null) rightRacket.SetActive(true);
        }
    }
    
    /// <summary>
    /// Create a simple runtime menu panel
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
        
        runtimeMenuPanel.AddComponent<UnityEngine.UI.CanvasScaler>();
        runtimeMenuPanel.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        
        var rectTransform = runtimeMenuPanel.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(400, 300);
        runtimeMenuPanel.transform.localScale = Vector3.one * 0.001f;
        
        // Create background panel
        var panel = new GameObject("Panel");
        panel.transform.SetParent(runtimeMenuPanel.transform, false);
        var panelImage = panel.AddComponent<UnityEngine.UI.Image>();
        panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.one;
        panelRect.sizeDelta = Vector2.zero;
        
        // Create title
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
        
        // Create instructions
        var instructGO = new GameObject("Instructions");
        instructGO.transform.SetParent(panel.transform, false);
        var instructText = instructGO.AddComponent<TMPro.TextMeshProUGUI>();
        instructText.text = "A/X: Resume\nB/Y: Exit to Mode Selection\nGRIP: Switch to VR Game\nMENU: Close";
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
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
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
            ToggleGameMenu();
            return;
        }
        
        // B/Y: Exit to mode selection
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) ||
            OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch))
        {
            ExitPassthroughGame();
            return;
        }
        
        // GRIP: Switch to normal VR TableTennis scene
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
            OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            SwitchToNormalGame();
            return;
        }
    }
    
    /// <summary>
    /// Exit passthrough game and return to mode selection
    /// </summary>
    private void ExitPassthroughGame()
    {
        Log("Exiting passthrough game...");
        
        // Cleanup spawned objects
        if (spawnedPassthroughTable != null)
        {
            Destroy(spawnedPassthroughTable);
            spawnedPassthroughTable = null;
        }
        
        if (spawnedBall != null)
        {
            Destroy(spawnedBall);
            spawnedBall = null;
        }
        
        if (leftRacket != null)
        {
            Destroy(leftRacket);
            leftRacket = null;
        }
        
        if (rightRacket != null)
        {
            Destroy(rightRacket);
            rightRacket = null;
        }
        
        if (runtimeMenuPanel != null)
        {
            Destroy(runtimeMenuPanel);
            runtimeMenuPanel = null;
        }
        
        // Destroy passthrough game UI panel
        if (passthroughGameUIPanel != null)
        {
            Destroy(passthroughGameUIPanel);
            passthroughGameUIPanel = null;
        }
        
        // Reset state
        isPassthroughMode = false;
        isGameMenuOpen = false;
        passthroughPhase = PassthroughGamePhase.Idle;
        racketsVisible = true;
        
        // Show the button panel for mode selection
        if (buttonPanel != null)
        {
            buttonPanel.SetActive(true);
        }
        
        // Restore the main GUI panel
        ShowMainGUIPanel();
        
        // Re-enable ray interactors
        EnableRayInteractors();
        
        Log("Returned to mode selection. Choose: Start Game (TableTennis scene) or Passthrough Game");
    }
    
    /// <summary>
    /// Called by PassthroughGameManager when the game ends
    /// </summary>
    public void OnPassthroughGameEnded()
    {
        ExitPassthroughGame();
    }
    
    /// <summary>
    /// Switch from passthrough game to normal VR TableTennis scene
    /// Uses the same approach as the Start Game button on main branch.
    /// Scene transition will destroy all objects except preserved anchors.
    /// </summary>
    private void SwitchToNormalGame()
    {
        Log("Switching to VR TableTennis scene...");
        
        // Mark state before scene transition
        isSwitchedToVRMode = true;
        isPassthroughMode = false;
        isGameMenuOpen = false;
        
        // IMPORTANT: Destroy passthrough objects before scene switch to prevent duplicates
        CleanupPassthroughObjects();
        
        // Load the TableTennis scene (same as Start Game button)
#if FUSION2
        if (Object.HasStateAuthority)
        {
            // Host loads scene - this calls PreserveObjectsForSceneTransition and notifies clients
            LoadTableTennisSceneNetworked();
        }
        else
        {
            // Client requests host to load the scene
            RPC_RequestStartGame();
        }
#else
        // Fallback for non-Fusion
        UnityEngine.SceneManagement.SceneManager.LoadScene(tableTennisSceneName);
#endif
    }
    
    /// <summary>
    /// Cleanup passthrough-specific objects before switching to VR scene
    /// </summary>
    private void CleanupPassthroughObjects()
    {
        Debug.Log("[Passthrough] Cleaning up passthrough objects before VR scene switch...");
        
        // Destroy passthrough table
        if (spawnedPassthroughTable != null)
        {
            Debug.Log($"[Passthrough] Destroying passthrough table: {spawnedPassthroughTable.name}");
            Destroy(spawnedPassthroughTable);
            spawnedPassthroughTable = null;
        }
        
        // Destroy passthrough ball
        if (spawnedBall != null)
        {
            Debug.Log($"[Passthrough] Destroying passthrough ball: {spawnedBall.name}");
            Destroy(spawnedBall);
            spawnedBall = null;
        }
        
        // Destroy passthrough rackets (local)
        if (leftRacket != null)
        {
            Debug.Log($"[Passthrough] Destroying left racket");
            Destroy(leftRacket);
            leftRacket = null;
        }
        if (rightRacket != null)
        {
            Debug.Log($"[Passthrough] Destroying right racket");
            Destroy(rightRacket);
            rightRacket = null;
        }
        
        // Destroy remote rackets
        if (remoteLeftRacket != null)
        {
            Destroy(remoteLeftRacket);
            remoteLeftRacket = null;
        }
        if (remoteRightRacket != null)
        {
            Destroy(remoteRightRacket);
            remoteRightRacket = null;
        }
        
        // Destroy passthrough template
        if (passthroughRacketTemplate != null)
        {
            Destroy(passthroughRacketTemplate);
            passthroughRacketTemplate = null;
        }
        
        // Destroy passthrough UI panel
        if (passthroughGameUIPanel != null)
        {
            Destroy(passthroughGameUIPanel);
            passthroughGameUIPanel = null;
        }
        
        // Reset state
        racketsVisible = true;
        passthroughPhase = PassthroughGamePhase.Idle;
        
        Debug.Log("[Passthrough] Passthrough cleanup complete");
    }
    
    /// <summary>
    /// Toggle racket visibility (B/Y button)
    /// </summary>
    private void ToggleRacketVisibility()
    {
        racketsVisible = !racketsVisible;
        
        if (leftRacket != null) leftRacket.SetActive(racketsVisible);
        if (rightRacket != null) rightRacket.SetActive(racketsVisible);
        
        if (racketsVisible)
        {
            DisableRayInteractors();
        }
        else
        {
            EnableRayInteractors();
        }
        
        Log($"🎾 Rackets {(racketsVisible ? "visible" : "hidden")}");
    }
    
    /// <summary>
    /// Handle table adjustment input (rotation and height) - NETWORKED
    /// BOTH host and client can adjust in any phase (TableAdjust or BallPosition)
    /// </summary>
    private void HandleTableAdjustInput()
    {
        if (spawnedPassthroughTable == null) return;
        
        // Read right thumbstick for rotation/height (matches VR TableTennisManager)
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        // Early exit if no input
        if (Mathf.Abs(rightStick.x) < 0.1f && Mathf.Abs(rightStick.y) < 0.1f)
        {
            return;
        }

#if FUSION2
        // Match VR TableTennisManager pattern - client uses RPCs, host updates directly
        if (Object != null && Object.HasStateAuthority)
        {
            // Host: directly adjust networked values
            if (Mathf.Abs(rightStick.x) > 0.1f)
            {
                float rotation = rightStick.x * TableRotateSpeed * Time.deltaTime;
                NetworkedPassthroughTableYRotation += rotation;
            }
            if (Mathf.Abs(rightStick.y) > 0.1f)
            {
                float verticalMove = rightStick.y * TableMoveSpeed * Time.deltaTime;
                NetworkedPassthroughTableHeight += verticalMove;
            }
            ApplyPassthroughTableState();
        }
        else if (Runner != null)
        {
            // Client: send adjustment requests to host via RPC
            if (Mathf.Abs(rightStick.x) > 0.1f)
            {
                float rotation = rightStick.x * TableRotateSpeed * Time.deltaTime;
                RPC_RequestPassthroughTableRotate(rotation);
            }
            if (Mathf.Abs(rightStick.y) > 0.1f)
            {
                float verticalMove = rightStick.y * TableMoveSpeed * Time.deltaTime;
                RPC_RequestPassthroughFloorAdjust(verticalMove);
            }
        }
#else
        // Non-networked: local adjustment only
        float rotationDelta = 0f;
        float heightDelta = 0f;
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            rotationDelta = rightStick.x * TableRotateSpeed * Time.deltaTime;
        }
        if (Mathf.Abs(rightStick.y) > 0.1f)
        {
            heightDelta = rightStick.y * TableMoveSpeed * Time.deltaTime;
        }
        ApplyLocalTableAdjustment(rotationDelta, heightDelta);
#endif
    }
    
    /// <summary>
    /// Apply local table adjustment (non-networked fallback) - uses LOCAL coordinates since parented to anchor
    /// </summary>
    private void ApplyLocalTableAdjustment(float rotationDelta, float heightDelta)
    {
        if (spawnedPassthroughTable == null) return;
        
        if (Mathf.Abs(rotationDelta) > 0.001f)
        {
            Vector3 localEuler = spawnedPassthroughTable.transform.localEulerAngles;
            spawnedPassthroughTable.transform.localRotation = Quaternion.Euler(localEuler.x, localEuler.y + rotationDelta, 0);
        }
        
        if (Mathf.Abs(heightDelta) > 0.001f)
        {
            Vector3 localPos = spawnedPassthroughTable.transform.localPosition;
            localPos.y += heightDelta;
            spawnedPassthroughTable.transform.localPosition = localPos;
        }
    }
    
#if FUSION2
    /// <summary>
    /// Apply networked passthrough table state - uses LOCAL coordinates since parented to anchor
    /// </summary>
    private void ApplyPassthroughTableState()
    {
        if (spawnedPassthroughTable == null) return;
        
        // Apply local rotation (keep X rotation for upside-down fix)
        spawnedPassthroughTable.transform.localRotation = Quaternion.Euler(
            tableXRotationOffset,
            NetworkedPassthroughTableYRotation,
            0
        );
        
        // Apply local height (Y position relative to anchor)
        Vector3 localPos = spawnedPassthroughTable.transform.localPosition;
        localPos.y = NetworkedPassthroughTableHeight;
        spawnedPassthroughTable.transform.localPosition = localPos;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestPassthroughTableRotate(float rotation)
    {
        NetworkedPassthroughTableYRotation += rotation;
        ApplyPassthroughTableState();
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestPassthroughFloorAdjust(float verticalMove)
    {
        NetworkedPassthroughTableHeight += verticalMove;
        ApplyPassthroughTableState();
    }
#endif
    
    /// <summary>
    /// Handle ball position phase input (GRIP to spawn, thumbstick to adjust table, A/X + thumbstick to adjust ball)
    /// In this phase, BOTH host and client can adjust the table
    /// </summary>
    private void HandleBallPositionInput(bool axButtonPressed, bool axButtonHeld)
    {
        // Allow table adjustment in this phase (both host and client)
        if (!axButtonHeld)
        {
            HandleTableAdjustInput();
        }
        
        // GRIP spawns ball if not yet spawned
        if (spawnedBall == null && !ballSpawnPending)
        {
            // Use grip axis (squeeze with middle fingers) - threshold of 0.5
            float leftGrip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger);
            float rightGrip = OVRInput.Get(OVRInput.Axis1D.SecondaryHandTrigger);
            bool gripPressed = leftGrip > 0.5f || rightGrip > 0.5f;
            
            // Debounce to prevent multiple spawns
            if (gripPressed && !wasGripPressed)
            {
                Debug.Log($"[Passthrough] Grip detected! Left: {leftGrip}, Right: {rightGrip}");
                ballSpawnPending = true; // Prevent multiple spawn requests
                SpawnPassthroughBall();
                UpdatePassthroughInstructions();
                Log("Ball spawned. Hit ball with racket to start!");
            }
            wasGripPressed = gripPressed;
        }
        else if (spawnedBall != null)
        {
            // Ball spawned - A/X HELD + thumbstick can adjust position
            if (axButtonHeld)
            {
                HandleBallPositionAdjust();
            }
            // When ball is hit with racket, call StartPlayingPhase()
        }
    }
    
    /// <summary>
    /// Handle ball position adjustment with A/X held + thumbstick
    /// </summary>
    private void HandleBallPositionAdjust()
    {
        if (spawnedBall == null) return;
        
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        // Only adjust if thumbstick is moved
        if (Mathf.Abs(rightStick.x) > 0.1f || Mathf.Abs(rightStick.y) > 0.1f)
        {
            Vector3 movement = new Vector3(rightStick.x, rightStick.y, 0) * TableMoveSpeed * Time.deltaTime;
            
#if FUSION2
            if (Object != null && Object.HasStateAuthority)
            {
                // Host directly moves ball
                spawnedBall.transform.position += movement;
            }
            else
            {
                // Client requests host to move ball
                RPC_RequestBallMove(movement);
            }
#else
            spawnedBall.transform.position += movement;
#endif
        }
    }
    
    /// <summary>
    /// Called when ball is hit - transition to Playing phase
    /// </summary>
    public void StartPlayingPhase()
    {
        passthroughPhase = PassthroughGamePhase.Playing;
        UpdatePassthroughInstructions();
        Log("Game started");
    }
    
    /// <summary>
    /// Advance to next game phase when A/X is pressed
    /// </summary>
    private void AdvancePassthroughPhase()
    {
        switch (passthroughPhase)
        {
            case PassthroughGamePhase.TableAdjust:
                // Move to ball position phase
                passthroughPhase = PassthroughGamePhase.BallPosition;
                UpdatePassthroughInstructions();
                Log("Ball Position - Press GRIP to spawn ball");
                break;
                
            case PassthroughGamePhase.BallPosition:
                // No phase advance - hit ball to start
                break;
                
            case PassthroughGamePhase.Playing:
                // No phase advance
                break;
        }
    }
    
    /// <summary>
    /// Enable rackets on both controllers (uses controller anchors for stability, not hand anchors)
    /// </summary>
    private void EnableRackets()
    {
        Debug.Log("[Passthrough] EnableRackets called - using controller anchors for stability");
        
        // Disable any existing ControllerRacket scripts to prevent conflicts
        var existingControllerRackets = FindObjectsOfType<ControllerRacket>();
        foreach (var cr in existingControllerRackets)
        {
            cr.enabled = false;
        }
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null)
        {
            Debug.LogWarning("[Passthrough] No OVRCameraRig found!");
            return;
        }
        
        // Use CONTROLLER anchors (stable) instead of hand anchors (can drift)
        Transform leftController = cameraRig.leftControllerAnchor;
        Transform rightController = cameraRig.rightControllerAnchor;
        
        if (leftController == null || rightController == null)
        {
            Debug.LogWarning("[Passthrough] Controller anchors not found!");
            return;
        }
        
        Debug.Log($"[Passthrough] Found controller anchors. Left: {leftController.name}, Right: {rightController.name}");
        Debug.Log($"[Passthrough] RacketPrefab is {(RacketPrefab != null ? RacketPrefab.name : "NULL")}");
        
        DisableRayInteractors();
        racketsVisible = true;
        
        bool racketsCreated = false;
        
        // Method 1: Spawn from prefab (but verify it has visible meshes)
        if (RacketPrefab != null)
        {
            Debug.Log("[Passthrough] Creating rackets from prefab...");
            
            // First check if the prefab has any visible meshes
            bool prefabHasMesh = RacketPrefab.GetComponent<MeshRenderer>() != null ||
                                  RacketPrefab.GetComponent<SkinnedMeshRenderer>() != null ||
                                  RacketPrefab.GetComponentInChildren<MeshRenderer>() != null ||
                                  RacketPrefab.GetComponentInChildren<SkinnedMeshRenderer>() != null;
            
            if (!prefabHasMesh)
            {
                Debug.LogWarning($"[Passthrough] RacketPrefab '{RacketPrefab.name}' has no visible meshes! Searching scene for racket models...");
            }
            else
            {
                if (leftRacket == null)
                {
                    // Instantiate without parent first to avoid inheriting rotations (matching ControllerRacket)
                    leftRacket = Instantiate(RacketPrefab);
                    leftRacket.name = "LeftRacket";
                    
                    // Set parent with worldPositionStays=false
                    leftRacket.transform.SetParent(leftController, false);
                    
                    // Reset to identity first, then apply our offset/rotation
                    leftRacket.transform.localPosition = racketOffset;
                    leftRacket.transform.localRotation = Quaternion.identity;
                    leftRacket.transform.localRotation = Quaternion.Euler(racketRotation);
                    leftRacket.transform.localScale = Vector3.one * racketScale;
                    
                    // Remove ControllerRacket component if it exists (we handle it manually)
                    var controllerRacket = leftRacket.GetComponent<ControllerRacket>();
                    if (controllerRacket != null) Destroy(controllerRacket);
                    
                    // Remove physics components - racket follows controller rigidly
                    CleanupRacketPhysics(leftRacket);
                    
                    Debug.Log($"[Passthrough] Created left racket: {leftRacket.name} with offset {racketOffset}, rotation {racketRotation}");
                }
                leftRacket.SetActive(true);
                
                if (rightRacket == null)
                {
                    // Instantiate without parent first to avoid inheriting rotations (matching ControllerRacket)
                    rightRacket = Instantiate(RacketPrefab);
                    rightRacket.name = "RightRacket";
                    
                    // Set parent with worldPositionStays=false
                    rightRacket.transform.SetParent(rightController, false);
                    
                    // Reset to identity first, then apply our offset/rotation
                    rightRacket.transform.localPosition = racketOffset;
                    rightRacket.transform.localRotation = Quaternion.identity;
                    rightRacket.transform.localRotation = Quaternion.Euler(racketRotation);
                    rightRacket.transform.localScale = Vector3.one * racketScale;
                    
                    // Remove ControllerRacket component if it exists (we handle it manually)
                    var controllerRacket = rightRacket.GetComponent<ControllerRacket>();
                    if (controllerRacket != null) Destroy(controllerRacket);
                    
                    // Remove physics components - racket follows controller rigidly
                    CleanupRacketPhysics(rightRacket);
                    
                    Debug.Log($"[Passthrough] Created right racket: {rightRacket.name} with offset {racketOffset}, rotation {racketRotation}");
                }
                rightRacket.SetActive(true);
                
                racketsCreated = true;
            }
        }
        
        // Method 2: Search scene for racket models (if prefab didn't work or had no mesh)
        if (!racketsCreated)
        {
            Debug.Log("[Passthrough] Searching scene for racket models...");
            
            // Method 2a: Search by tag (active objects only)
            GameObject foundRacket = null;
            var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
            foreach (var r in taggedRackets)
            {
                if (r.name.Contains("Controller") || r.name.Contains("Left") || r.name.Contains("Right")) continue;
                // Must have mesh
                if (r.GetComponent<MeshFilter>() != null || r.GetComponentInChildren<MeshFilter>() != null)
                {
                    foundRacket = r;
                    Debug.Log($"[Passthrough] Found racket by tag: {foundRacket.name}");
                    break;
                }
            }
            
            // Method 2b: Search under spawned passthrough table
            if (foundRacket == null && spawnedPassthroughTable != null)
            {
                Debug.Log($"[Passthrough] Searching passthrough table '{spawnedPassthroughTable.name}' with {spawnedPassthroughTable.GetComponentsInChildren<Transform>(true).Length} children");
                foreach (Transform child in spawnedPassthroughTable.GetComponentsInChildren<Transform>(true))
                {
                    string nameLower = child.name.ToLower();
                    // Log all mesh-containing children for debugging
                    if (child.GetComponent<MeshFilter>() != null)
                    {
                        Debug.Log($"[Passthrough] Table child with mesh: {child.name}");
                    }
                    if (nameLower.Contains("controller") || nameLower.Contains("left") || nameLower.Contains("right")) continue;
                    
                    if ((nameLower.Contains("racket") || nameLower.Contains("paddle") || nameLower.Contains("bat"))
                        && child.GetComponent<MeshFilter>() != null)
                    {
                        foundRacket = child.gameObject;
                        Debug.Log($"[Passthrough] Found racket under passthrough table: {foundRacket.name}");
                        break;
                    }
                }
            }
            
            // Method 2c: Search ALL objects including inactive using Resources
            if (foundRacket == null)
            {
                var allObjects = Resources.FindObjectsOfTypeAll<Transform>();
                foreach (var t in allObjects)
                {
                    if (!t.gameObject.scene.isLoaded) continue; // Skip prefabs
                    
                    string nameLower = t.name.ToLower();
                    if (nameLower.Contains("controller") || nameLower.Contains("left") || nameLower.Contains("right")) continue;
                    
                    if ((nameLower == "racket" || nameLower == "racket2" || nameLower.Contains("paddle") || nameLower.Contains("bat"))
                        && t.GetComponent<MeshFilter>() != null)
                    {
                        foundRacket = t.gameObject;
                        Debug.Log($"[Passthrough] Found racket in scene (may be inactive): {foundRacket.name}");
                        break;
                    }
                }
            }
            
            // Method 2d: Search under pingpong parent
            if (foundRacket == null)
            {
                var pingPongParent = GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("PingPongTable");
                if (pingPongParent != null)
                {
                    foreach (Transform child in pingPongParent.GetComponentsInChildren<Transform>(true))
                    {
                        string nameLower = child.name.ToLower();
                        if (nameLower.Contains("controller") || nameLower.Contains("left") || nameLower.Contains("right")) continue;
                        
                        if (nameLower.Contains("racket") || nameLower.Contains("paddle") || nameLower.Contains("bat"))
                        {
                            foundRacket = child.gameObject;
                            Debug.Log($"[Passthrough] Found racket under pingpong: {foundRacket.name}");
                            break;
                        }
                    }
                }
            }
            
            // Create rackets from found template
            if (foundRacket != null)
            {
                Debug.Log($"[Passthrough] Creating rackets from scene template: {foundRacket.name}");
                
                // Hide the original
                foundRacket.SetActive(false);
                
                if (leftRacket == null)
                {
                    // Instantiate without parent first (matching ControllerRacket)
                    leftRacket = Instantiate(foundRacket);
                    leftRacket.name = "LeftRacket";
                    leftRacket.transform.SetParent(leftController, false);
                    leftRacket.transform.localPosition = racketOffset;
                    leftRacket.transform.localRotation = Quaternion.identity;
                    leftRacket.transform.localRotation = Quaternion.Euler(racketRotation);
                    leftRacket.transform.localScale = Vector3.one * racketScale;
                    var cr = leftRacket.GetComponent<ControllerRacket>();
                    if (cr != null) Destroy(cr);
                    CleanupRacketPhysics(leftRacket);
                }
                leftRacket.SetActive(true);
                
                if (rightRacket == null)
                {
                    // Instantiate without parent first (matching ControllerRacket)
                    rightRacket = Instantiate(foundRacket);
                    rightRacket.name = "RightRacket";
                    rightRacket.transform.SetParent(rightController, false);
                    rightRacket.transform.localPosition = racketOffset;
                    rightRacket.transform.localRotation = Quaternion.identity;
                    rightRacket.transform.localRotation = Quaternion.Euler(racketRotation);
                    rightRacket.transform.localScale = Vector3.one * racketScale;
                    var cr = rightRacket.GetComponent<ControllerRacket>();
                    if (cr != null) Destroy(cr);
                    CleanupRacketPhysics(rightRacket);
                }
                rightRacket.SetActive(true);
                
                racketsCreated = true;
            }
        }
        
        // Method 3: Create placeholder rackets (last resort)
        if (!racketsCreated)
        {
            Debug.Log("[Passthrough] No rackets found in scene, creating placeholder rackets...");
            leftRacket = CreatePlaceholderRacket("LeftRacket");
            leftRacket.transform.SetParent(leftController, false);
            leftRacket.transform.localPosition = racketOffset;
            leftRacket.transform.localRotation = Quaternion.identity;
            leftRacket.transform.localRotation = Quaternion.Euler(racketRotation);
            
            rightRacket = CreatePlaceholderRacket("RightRacket");
            rightRacket.transform.SetParent(rightController, false);
            rightRacket.transform.localPosition = racketOffset;
            rightRacket.transform.localRotation = Quaternion.identity;
            rightRacket.transform.localRotation = Quaternion.Euler(racketRotation);
            
            racketsCreated = true;
            Log("Using placeholder rackets - assign RacketPrefab for better visuals");
        }
        
        // Ensure rackets are visible
        if (leftRacket != null)
        {
            leftRacket.SetActive(true);
            Debug.Log($"[Passthrough] Left racket active: {leftRacket.activeSelf}, parent: {leftRacket.transform.parent?.name}");
        }
        if (rightRacket != null)
        {
            rightRacket.SetActive(true);
            Debug.Log($"[Passthrough] Right racket active: {rightRacket.activeSelf}, parent: {rightRacket.transform.parent?.name}");
        }
        
        Debug.Log($"[Passthrough] Rackets enabled: {racketsCreated}, leftRacket={leftRacket != null}, rightRacket={rightRacket != null}");
    }
    
    /// <summary>
    /// Setup physics components on a racket for collision detection
    /// </summary>
    private void SetupRacketPhysics(GameObject racket)
    {
        // Ensure racket has proper tag
        racket.tag = "Racket";
        
        // Reuse existing Rigidbody or add one
        var rb = racket.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = racket.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        
        // Ensure there's a collider (check children too)
        var colliders = racket.GetComponentsInChildren<Collider>();
        if (colliders.Length == 0)
        {
            // Add a box collider for paddle shape
            var boxCollider = racket.AddComponent<BoxCollider>();
            boxCollider.size = new Vector3(0.15f, 0.01f, 0.15f);
            boxCollider.isTrigger = false;
        }
        else
        {
            // Ensure existing colliders are not triggers
            foreach (var col in colliders)
            {
                col.isTrigger = false;
                col.gameObject.tag = "Racket";
            }
        }
        
        Debug.Log($"[Passthrough] Setup physics for racket: {racket.name}");
    }
    
    /// <summary>
    /// Create a simple placeholder racket (cylinder + flat disc)
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
        Destroy(handle.GetComponent<Collider>()); // Remove collider from handle
        
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
        var allMonoBehaviours = FindObjectsOfType<MonoBehaviour>();
        foreach (var mb in allMonoBehaviours)
        {
            string typeName = mb.GetType().Name.ToLower();
            if (typeName.Contains("rayinteractor") || typeName.Contains("ray") && typeName.Contains("interactor"))
            {
                mb.enabled = false;
            }
        }
        
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
        var allMonoBehaviours = FindObjectsOfType<MonoBehaviour>(true);
        foreach (var mb in allMonoBehaviours)
        {
            string typeName = mb.GetType().Name.ToLower();
            if (typeName.Contains("rayinteractor") || typeName.Contains("ray") && typeName.Contains("interactor"))
            {
                mb.enabled = true;
            }
        }
        
        var lineRenderers = FindObjectsOfType<LineRenderer>(true).Where(lr => 
            lr.gameObject.name.ToLower().Contains("ray") || 
            lr.gameObject.name.ToLower().Contains("pointer")).ToArray();
        foreach (var lr in lineRenderers)
        {
            lr.enabled = true;
        }
    }
    
    /// <summary>
    /// Spawn the ball above the TABLE (stable in air for positioning)
    /// </summary>
    private void SpawnPassthroughBall()
    {
        if (currentAnchors == null || currentAnchors.Count == 0)
        {
            Debug.LogWarning("[Passthrough] Cannot spawn ball - no anchors!");
            return;
        }
        
        Transform primaryAnchor = currentAnchors[0].transform;
        
        Debug.Log($"[Passthrough] SpawnPassthroughBall - anchors count: {currentAnchors.Count}, table: {(spawnedPassthroughTable != null ? spawnedPassthroughTable.name : "NULL")}");
        
        // Try to find table if not already set
        if (spawnedPassthroughTable == null)
        {
            spawnedPassthroughTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
            if (spawnedPassthroughTable != null)
            {
                Debug.Log($"[Passthrough] Found table in scene: {spawnedPassthroughTable.name}");
            }
        }
        
        // Calculate ball spawn position at midpoint between anchors (where table is), 0.5m above ground
        Vector3 spawnPos;
        
        if (spawnedPassthroughTable != null)
        {
            // Use table's actual world position
            Vector3 tableWorldPos = spawnedPassthroughTable.transform.position;
            spawnPos = new Vector3(tableWorldPos.x, tableWorldPos.y + 0.5f, tableWorldPos.z);
            Debug.Log($"[Passthrough] Ball spawn using table position: {spawnPos}");
        }
        else if (currentAnchors.Count >= 2)
        {
            // Calculate midpoint between two anchors (same as table position)
            Transform secondaryAnchor = currentAnchors[1].transform;
            Vector3 midpoint = (primaryAnchor.position + secondaryAnchor.position) / 2f;
            spawnPos = new Vector3(midpoint.x, midpoint.y + 0.5f, midpoint.z);
            Debug.Log($"[Passthrough] Ball spawn using anchor midpoint: {spawnPos}");
        }
        else
        {
            // Fallback: use primary anchor position but calculate midpoint direction if _localizedAnchor exists
            // This handles the case where we have anchors but they're not all in currentAnchors list
            spawnPos = primaryAnchor.position + Vector3.up * 0.5f;
            Debug.Log($"[Passthrough] Ball spawn using single anchor (count={currentAnchors.Count}): {spawnPos}");
        }
        
#if FUSION2
        if (networkRunner != null && networkRunner.IsRunning && Object.HasStateAuthority)
        {
            // Networked spawn (Host)
            if (BallPrefab != default)
            {
                var ballObj = networkRunner.Spawn(BallPrefab, spawnPos, Quaternion.identity, Object.InputAuthority);
                if (ballObj != null)
                {
                    var networkedBall = ballObj.GetComponent<NetworkedBall>();
                    if (networkedBall != null)
                    {
                        networkedBall.EnterPositioningMode();
                    }
                    spawnedBall = ballObj.gameObject;
                    RPC_NotifyBallSpawned();
                    Debug.Log($"[Passthrough] Ball spawned");
                }
            }
            else
            {
                Debug.LogWarning("[Passthrough] Ball prefab not assigned!");
            }
        }
        else if (!Object.HasStateAuthority)
        {
            RPC_RequestSpawnBall(spawnPos);
        }
        else
#endif
        {
            // Local spawn fallback
            var existingBall = GameObject.Find("Ball") ?? GameObject.Find("PingPongBall");
            if (existingBall != null)
            {
                existingBall.transform.position = spawnPos;
                existingBall.SetActive(true);
                spawnedBall = existingBall;
                
                // Make ball kinematic (stable)
                var rb = existingBall.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                }
            }
        }
    }
    
#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnBall(Vector3 position)
    {
        Debug.Log("[Passthrough] Host received ball spawn request");
        if (BallPrefab != default && networkRunner != null)
        {
            var ballObj = networkRunner.Spawn(BallPrefab, position, Quaternion.identity);
            if (ballObj != null)
            {
                // Set ball to positioning mode (stable in air)
                var networkedBall = ballObj.GetComponent<NetworkedBall>();
                if (networkedBall != null)
                {
                    networkedBall.EnterPositioningMode();
                    Debug.Log($"[Passthrough] Ball spawned in positioning mode at {position}");
                }
                
                // Notify all clients to find the ball
                RPC_NotifyBallSpawned();
            }
        }
    }
    
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyBallSpawned()
    {
        Debug.Log("[Passthrough] Received notification that ball was spawned");
        // Client needs to find the ball object
        StartCoroutine(FindSpawnedBallDelayed());
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestBallMove(Vector3 movement)
    {
        if (spawnedBall != null)
        {
            spawnedBall.transform.position += movement;
        }
    }
    
    private IEnumerator FindSpawnedBallDelayed()
    {
        // Wait a frame for Fusion to finish spawning
        yield return null;
        yield return null;
        
        // Try to find the ball
        var networkedBall = FindObjectOfType<NetworkedBall>();
        if (networkedBall != null)
        {
            spawnedBall = networkedBall.gameObject;
            ballSpawnPending = false; // Reset pending flag
            Debug.Log($"[Passthrough] Found spawned ball: {spawnedBall.name}");
        }
        else
        {
            // Try by name
            spawnedBall = GameObject.Find("Ball") ?? GameObject.Find("PingPongBall") ?? GameObject.Find("NetworkedBall");
            if (spawnedBall != null)
            {
                ballSpawnPending = false; // Reset pending flag
                Debug.Log($"[Passthrough] Found ball by name: {spawnedBall.name}");
            }
            else
            {
                ballSpawnPending = false; // Reset to allow retry
                Debug.LogWarning("[Passthrough] Could not find spawned ball!");
            }
        }
    }
#endif
    
    /// <summary>
    /// Switch UI to show game instructions instead of buttons (legacy - kept for compatibility)
    /// </summary>
    private void ShowGameInstructions()
    {
        UpdatePassthroughInstructions();
    }

#if FUSION2
    /// <summary>
    /// Client requests host to start the game
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestStartGame()
    {
        Debug.Log("[Anchor] Host received request to start game - loading scene for all");
        LoadTableTennisSceneNetworked();
    }

    /// <summary>
    /// Called by host to notify all clients to preserve their anchors before scene transition
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_PrepareForSceneTransition()
    {
        Debug.Log("[Anchor] Received scene transition notification - cleaning up and preserving anchors");
        
        // Cleanup passthrough objects to prevent duplicates in VR scene
        CleanupPassthroughObjects();
        
        // Preserve anchors for the new scene
        PreserveObjectsForSceneTransition();
    }

    /// <summary>
    /// Host loads scene using Fusion's networked scene loading
    /// This automatically syncs to all connected clients
    /// </summary>
    private void LoadTableTennisSceneNetworked()
    {
        if (Runner != null && Runner.IsRunning)
        {
            Debug.Log($"[Anchor] Host loading networked scene: {tableTennisSceneName}");
            Log("Loading Table Tennis...");
            
            // Notify ALL clients to preserve their anchors BEFORE scene loads
            RPC_PrepareForSceneTransition();
            
            // Host also needs to preserve anchors
            PreserveObjectsForSceneTransition();
            
            // Get scene index from Build Settings by name
            int sceneIndex = SceneUtility.GetBuildIndexByScenePath(tableTennisSceneName);
            
            // If not found by name alone, try with path variations
            if (sceneIndex < 0)
            {
                // Try common path patterns
                sceneIndex = SceneUtility.GetBuildIndexByScenePath($"Assets/Colocation/Scenes/Table Tennis/{tableTennisSceneName}.unity");
            }
            
            if (sceneIndex >= 0)
            {
                // Use Fusion's scene loading - this syncs to all clients automatically
                Runner.LoadScene(SceneRef.FromIndex(sceneIndex));
            }
            else
            {
                Debug.LogError($"[Anchor] Scene '{tableTennisSceneName}' not found in Build Settings! Add it via File > Build Settings");
                Log("Scene not in Build Settings!", true);
            }
        }
        else
        {
            Debug.LogError("[Anchor] Cannot load scene - Runner not available");
        }
    }

    /// <summary>
    /// Preserve anchor and spawned cube across scene transitions for alignment verification
    /// </summary>
    private void PreserveObjectsForSceneTransition()
    {
        // Preserve the localized anchor (this is crucial for alignment in new scene)
        if (_localizedAnchor != null)
        {
            DontDestroyOnLoad(_localizedAnchor.gameObject);
            Debug.Log($"[Anchor] Preserved anchor for scene transition: {_localizedAnchor.Uuid}");
        }
        else
        {
            Debug.LogWarning("[Anchor] No localized anchor to preserve!");
        }

        // Also preserve all tracked anchors
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.gameObject != null)
            {
                DontDestroyOnLoad(anchor.gameObject);
            }
        }
    }
    
    /// <summary>
    /// Hide the visual representation of an anchor (for VR mode transition)
    /// </summary>
    private void HideAnchorVisual(GameObject anchorGO)
    {
        if (anchorGO == null) return;
        
        // Hide all renderers (the visual marker)
        var renderers = anchorGO.GetComponentsInChildren<Renderer>(true);
        foreach (var renderer in renderers)
        {
            renderer.enabled = false;
        }
        
        // Also hide any "Visual" child objects
        var visual = anchorGO.transform.Find("Visual");
        if (visual != null)
        {
            visual.gameObject.SetActive(false);
        }
        
        Debug.Log($"[Anchor] Hidden anchor visual: {anchorGO.name}");
    }
#endif

    private Vector3 GetControllerAnchorPosition()
    {
        // Get right controller position at waist level (no Y manipulation)
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Use actual controller position (waist level) - no Y manipulation
            Debug.Log($"[Anchor] Placing anchor at waist level, position: {rightHand.position}");
            return rightHand.position;
        }

        // Fallback to camera position if controller not found
        if (cameraTransform != null)
        {
            return cameraTransform.position + cameraTransform.forward * 0.3f;
        }

        return Vector3.zero;
    }

    private Vector3 GetControllerSpawnPosition()
    {
        Vector3 worldPos;
        
        // Try to get right controller position
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Spawn 30cm in front and 10cm above the controller
            worldPos = rightHand.position + rightHand.forward * 0.3f + Vector3.up * 0.1f;
        }
        else if (cameraTransform != null)
        {
            // Fallback to camera forward
            worldPos = cameraTransform.position + cameraTransform.forward * 0.5f;
        }
        else
        {
            worldPos = new Vector3(0, 1.0f, 0);
        }
        
        // Ensure minimum height above ground
        if (worldPos.y < 0.5f)
        {
            worldPos.y = 0.5f;
        }
        
        return worldPos;
    }

    protected override async Task<OVRSpatialAnchor> CreateAnchor(Vector3 position, Quaternion rotation)
    {
        try
        {
            // Create anchor at controller position
            if (position == Vector3.zero)
            {
                position = GetControllerAnchorPosition();
                rotation = Quaternion.Euler(0, cameraTransform.eulerAngles.y, 0);
            }

            var anchorGO = new GameObject("Anchor_" + DateTime.Now.ToString("HHmmss"));
            anchorGO.transform.position = position;
            anchorGO.transform.rotation = rotation;

            if (anchorMarkerPrefab != null)
            {
                GameObject visual = Instantiate(anchorMarkerPrefab, anchorGO.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                
                float validScale = Mathf.Max(anchorScale, 0.01f);
                visual.transform.localScale = Vector3.one * validScale;
                
                // Remove physics components from visual to avoid interference
                foreach (var col in visual.GetComponentsInChildren<Collider>())
                    Destroy(col);
                foreach (var rb in visual.GetComponentsInChildren<Rigidbody>())
                    Destroy(rb);
            }

            var spatialAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();

            // Wait for anchor to be CREATED
            int timeout = 100;
            while (!spatialAnchor.Created && timeout > 0)
            {
                await Task.Yield();
                timeout--;
            }

            if (!spatialAnchor.Created)
            {
                Log("Anchor creation timed out", true);
                Destroy(anchorGO);
                return null;
            }
            
            Log($"Anchor created: {spatialAnchor.Uuid}, waiting for localization...");
            
            // CRITICAL: Wait for anchor to be LOCALIZED (required for save/share)
            timeout = 300; // 5 seconds max
            while (!spatialAnchor.Localized && timeout > 0)
            {
                await Task.Delay(50);
                timeout--;
            }
            
            if (!spatialAnchor.Localized)
            {
                Log("Anchor localization timed out - anchor may not be shareable", true);
            }
            else
            {
                Log($"Anchor localized: {spatialAnchor.Uuid}");
            }

            currentAnchors.Add(spatialAnchor); // Track it locally for UI
            _localizedAnchor = spatialAnchor; // Store as the shared anchor for relative positioning
            return spatialAnchor;
        }
        catch (Exception e)
        {
            Log($"Anchor creation error: {e.Message}", true);
            return null;
        }
    }

    // ==================== UI UPDATES ====================

    private void UpdateAllUI()
    {
        UpdateAnchorText();
        UpdateButtonStates();
        UpdateStatusIndicator();
    }

    private void UpdateAnchorText()
    {
        if (anchorText == null) return;

        var sb = new System.Text.StringBuilder();
        
        int localizedCount = 0;
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.Localized)
                localizedCount++;
        }

        sb.AppendLine($"Anchors: {currentAnchors.Count} ({localizedCount} localized)");
        
        if (localizedCount > 0)
            sb.AppendLine("ALIGNED - Ready!");
        else if (currentAnchors.Count > 0)
            sb.AppendLine("ALIGNING...");
        else
            sb.AppendLine("NOT ALIGNED");

        sb.AppendLine("================================");

        int index = 1;
        foreach (var anchor in currentAnchors)
        {
            if (anchor == null) continue;

            sb.AppendLine($"\nAnchor #{index}");
            sb.AppendLine($"  {(anchor.Localized ? "[OK]" : "[--]")} {anchor.Uuid.ToString().Substring(0, 8)}");
            
            Vector3 pos = anchor.transform.position;
            sb.AppendLine($"  Pos: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
            index++;
        }

        anchorText.text = sb.ToString();
    }

    private void UpdateButtonStates()
    {
        bool isAligned = IsAlignmentComplete();
        
#if FUSION2
        bool hasNetwork = networkRunner != null && networkRunner.IsRunning;
        
        // Check if both host and client are aligned (for Start Game button)
        bool bothDevicesAligned = isAligned && (currentState == SessionState.ClientAligned || currentState == SessionState.HostAligned);
#else
        bool hasNetwork = false;
        bool bothDevicesAligned = false;
#endif

        if (autoAlignButton != null)
            autoAlignButton.interactable = !isAligned; // Disable once aligned

        if (spawnCubeButton != null)
            spawnCubeButton.interactable = hasNetwork && isAligned;

        if (resetButton != null)
            resetButton.interactable = true; // Always available
            
        // Start Game button - only enabled when both devices are aligned
        if (startGameButton != null)
        {
            startGameButton.interactable = hasNetwork && bothDevicesAligned;
            
            // Update button text to show status
            var btnText = startGameButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                if (!hasNetwork)
                {
                    btnText.text = "\u23f3 Connecting...";
                }
                else if (!isAligned)
                {
                    btnText.text = "\u26a0\ufe0f Align first";
                }
                else if (!bothDevicesAligned)
                {
                    btnText.text = "\u23f3 Waiting for partner...";
                }
                else
                {
                    btnText.text = "\u25b6\ufe0f Start Game";
                }
            }
        }
        
        // Start Passthrough Game button - same logic as Start Game button
        if (startPassthroughGameButton != null)
        {
            startPassthroughGameButton.interactable = hasNetwork && bothDevicesAligned;
            
            // Update button text to show status
            var btnText = startPassthroughGameButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                if (!hasNetwork)
                {
                    btnText.text = "Connecting...";
                }
                else if (!isAligned)
                {
                    btnText.text = "Align first";
                }
                else if (!bothDevicesAligned)
                {
                    btnText.text = "Waiting for partner...";
                }
                else
                {
                    btnText.text = "▶️ Start Passthrough";
                }
            }
        }
    }

    private void UpdateStatusIndicator()
    {// 1. Update Network Indicator (Host/Client)
        if (networkIndicator != null)
        {
#if FUSION2
            if (networkRunner != null && networkRunner.IsRunning)
            {
                networkIndicator.color = isHost ? hostColor : clientColor;
            }
            else
            {
                networkIndicator.color = Color.gray;
            }
#else
            networkIndicator.color = Color.gray;
#endif
        }

        // 2. Update Alignment Indicator
        if (statusIndicator == null) return;

        switch (currentState)
        {
            case SessionState.Advertising:
                statusIndicator.color = advertisingColor; // Purple while advertising
                break;
            case SessionState.Discovering:
                statusIndicator.color = discoveringColor; // Orange while discovering
                break;
            case SessionState.Sharing:
                statusIndicator.color = Color.yellow; // Yellow while sharing anchors, waiting for client
                break;
            case SessionState.HostAligned:
            case SessionState.ClientAligned:
                statusIndicator.color = anchorAlignedColor; // Green when aligned
                break;
            case SessionState.Idle:
            default:
                bool isAligned = IsAlignmentComplete();
                statusIndicator.color = isAligned ? anchorAlignedColor : anchorNotAlignedColor;
                break;
        }
    }

    private bool IsAlignmentComplete()
    {
        if (currentState == SessionState.HostAligned || currentState == SessionState.ClientAligned) return true;

        // Bug fix: Don't return true just because one local anchor exists.
        // Waiting for explicit state change to HostAligned/ClientAligned
        // OR if locally aligned with 2 anchors (Standalone)
        
        if (currentAnchors != null && currentAnchors.Count >= 2 && currentAnchors[0].Localized && currentAnchors[1].Localized)
        {
             // Potential standalone alignment?
             // But for wizard, we prefer explicit state.
        }

        return false;
    }


}
