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


///  Align and Spawn Cube with built-in session management
/// Inherits from ColocationManager to reuse alignment logic.
public class AnchorGUIManager_AutoAlignment : ColocationManager
{
    private const string LOG_TAG = "[AnchorGUIManager]";
    // ===================== SERIALIZED FIELDS =====================
    [Header("UI Buttons")]
    [SerializeField] private Button autoAlignButton;
    [SerializeField] private Button spawnCubeButton;
    [SerializeField] private Button resetButton;
    [SerializeField] private Button startGameButton;
    [SerializeField] private Button startPassthroughGameButton;
    [Header("UI Popups")]
    [SerializeField] private CubeInstructionsPopup cubeInstructionsPopup;
    [Header("Game Scene Settings")]
    [SerializeField] private string tableTennisSceneName = "TableTennis";


    [Header("Status Display")]
    [SerializeField] private TextMeshProUGUI guiStatusText;
    [SerializeField] private TextMeshProUGUI anchorText;
    [SerializeField] private Image statusIndicator;
    [SerializeField] private Image networkIndicator;

    [Header("Settings")]

    [Header("Cube Spawn Settings")]
    [SerializeField] private NetworkPrefabRef cubePrefab;
    [SerializeField] private float cubeScale = 0.1f;

    [Header("Status Colors")]
    [SerializeField] private Color hostColor = Color.blue;
    [SerializeField] private Color clientColor = Color.yellow;
    [SerializeField] private Color anchorAlignedColor = Color.green;
    [SerializeField] private Color anchorNotAlignedColor = Color.red;
    [SerializeField] private Color advertisingColor = new Color(0.5f, 0f, 1f);
    [SerializeField] private Color discoveringColor = new Color(1f, 0.5f, 0f);

    [Header("Debug / Development")]
    [Tooltip("Skip anchor alignment for quick testing. Uses local anchors and enables start buttons immediately.")]
    [SerializeField] private bool skipAlignmentForDebug = false;

    private enum PassthroughGamePhase { Idle, TableAdjust, BallPosition, Playing }

    // ===================== STATIC PROPERTIES =====================
    new public static Vector3 FirstAnchorPosition { get; private set; }
    new public static Vector3 SecondAnchorPosition { get; private set; }
    public static Guid FirstAnchorUuid { get; private set; }
    public static Guid SecondAnchorUuid { get; private set; }
    public static float TableHeightOffsetStatic { get; private set; }
    new public static bool AlignmentCompletedStatic { get; private set; }
    public static bool TableWasAligned { get; private set; }
    public static Vector3 AlignedTablePosition { get; private set; }
    public static Quaternion AlignedTableRotation { get; private set; }

    // ===================== NETWORKED FIELDS (PHOTON FUSION) =====================
#if FUSION2
      [Networked] private NetworkBool ClientAlignedToAnchors { get; set; }
#endif


    private GameObject spawnedPassthroughTable;
    private PassthroughGamePhase passthroughPhase = PassthroughGamePhase.Idle;
    private GameObject leftRacket;
    private GameObject rightRacket;
    private GameObject spawnedBall;
    private GameObject passthroughGameUIPanel;
    private GameObject remoteLeftRacket;
    private GameObject remoteRightRacket;
#if FUSION2
    private NetworkRunner networkRunner;
    private NetworkObject spawnedCube;
#endif

    private Transform cameraTransform;
    private GameObject guiAnchorMarkerPrefab;
    private bool isHost = false;
    private int guiClientLocalizedAnchorCount = 0;
    private Vector3 firstAnchorWorldPosition;
    private bool waitingForGripToPlaceAnchors = false;
    private bool anchor1Placed = false;
    private bool anchor2Placed = false;
    private bool hostAutoStarted = false;
    private bool isSwitchedToVRMode = false;
    // Note: passthrough mode is checked dynamically via passthroughGameManager.IsActive
    private bool isPlacingAnchor = false;
    private GameObject leftDistanceDisplay;
    private GameObject rightDistanceDisplay;
    private TextMesh leftDistanceText;
    private TextMesh rightDistanceText;
    private PassthroughGameManager passthroughGameManager;
    private void Start()
    {
        // Create distance displays FIRST - they should exist regardless of camera state
        CreateDistanceDisplays();

        cameraTransform = Camera.main?.transform;
        if (cameraTransform == null)
        {
            Debug.LogWarning($"{LOG_TAG} Start No main camera found - will retry in Update");
            // Don't return - continue initialization, camera can be found later
        }

        // Check if we're in the TableTennis VR scene - if so, disable this component
        // (same approach as main branch Start Game - scene transition means this object is destroyed)
        string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (currentScene.ToLower().Contains("tabletennis"))
        {
            isSwitchedToVRMode = true;

            // Disable this component - TableTennisManager handles VR game
            this.enabled = false;
            return;
        }

        guiAnchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");
        if (guiAnchorMarkerPrefab == null)
        {
            guiAnchorMarkerPrefab = Resources.Load<GameObject>("AnchorCursorSphere");
        }

        // Set both derived and base class fields for anchor visuals
        anchorMarkerPrefab = guiAnchorMarkerPrefab;

        if (alignmentManager == null)
        {
            alignmentManager = FindObjectOfType<AlignmentManager>();
        }

        if (passthroughGameManager == null)
        {
            passthroughGameManager = FindObjectOfType<PassthroughGameManager>();
        }

#if FUSION2
        // Cache NetworkRunner early if available
        if (networkRunner == null)
        {
            networkRunner = FindObjectOfType<NetworkRunner>();
        }
#endif
        // Hide and disable the Start VR Game button (not currently used)
        if (startGameButton != null)
        {
            startGameButton.gameObject.SetActive(false);
            startGameButton = null;
        }
        else
        {
            // Try to find and hide the button by name if not assigned
            var vrButton = GameObject.Find("StartGameButton") ?? GameObject.Find("Start VR Game") ?? GameObject.Find("StartVRGame");
            if (vrButton != null)
            {
                vrButton.SetActive(false);
            }
        }
        // Note: passthrough mode is checked dynamically in Update() via passthroughGameManager.IsActive
        autoAlignButton?.onClick.AddListener(OnAutoAlignClicked);
        spawnCubeButton?.onClick.AddListener(OnSpawnCubeClicked);
        resetButton?.onClick.AddListener(OnResetClicked);
        startPassthroughGameButton?.onClick.AddListener(OnStartPassthroughGameClicked);

        // Initialize status text BEFORE UpdateAllUI to prevent "Ready to play!" showing at start
        if (guiStatusText != null)
        {
            SetStatusText("Click Start Alignment to start", "Start");
        }

        UpdateAllUI();
        UpdateUIWizard(); // Init text
    }
    // ==================== UI UPDATES ====================

    protected override void UpdateAllUI()
    {
        UpdateAnchorText();
        UpdateButtonStates();
        UpdateStatusIndicator();
    }
    /// Distance display above controllers for anchor placement
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

        // Header with role and overall status
        string roleText = isHost ? "HOST" : "CLIENT";
        sb.AppendLine($"{roleText} - Anchors: {currentAnchors.Count} total, {localizedCount} localized");

        if (localizedCount == currentAnchors.Count && currentAnchors.Count >= 2)
            sb.AppendLine("[OK] ALL ANCHORS LOCALIZED - Ready!");
        else if (localizedCount > 0)
        {
            // Show which specific anchors are localized
            var localizedUuids = new System.Collections.Generic.List<string>();
            for (int i = 0; i < currentAnchors.Count; i++)
            {
                if (currentAnchors[i] != null && currentAnchors[i].Localized)
                {
                    localizedUuids.Add(currentAnchors[i].Uuid.ToString().Substring(0, 8));
                }
            }
            string uuidsText = string.Join(", ", localizedUuids);
            sb.AppendLine($"[ALIGNING] ANCHOR{(localizedUuids.Count > 1 ? "S" : "")} LOCALIZED: {uuidsText}");
        }
        else if (currentAnchors.Count > 0)
            sb.AppendLine("[NO] NO ANCHORS LOCALIZED - Waiting...");
        else
            sb.AppendLine("[NONE] NO ANCHORS PLACED");

        sb.AppendLine("================================");

        int index = 1;
        foreach (var anchor in currentAnchors)
        {
            if (anchor == null) continue;

            string statusIcon = anchor.Localized ? "[OK]" : "[NO]";
            string statusText = anchor.Localized ? "LOCALIZED" : "NOT LOCALIZED";
            string uuidShort = anchor.Uuid.ToString().Substring(0, 8);

            sb.AppendLine($"\nAnchor #{index}: {statusIcon} {statusText}");
            sb.AppendLine($"  UUID: {uuidShort}...");

            Vector3 pos = anchor.transform.position;
            sb.AppendLine($"  Position: ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})");

            // Add additional info for localized anchors
            if (anchor.Localized)
            {
                sb.AppendLine($"  Status: Tracking active");
            }
            else
            {
                sb.AppendLine($"  Status: Lost tracking - needs relocalization");
            }

            index++;
        }

        anchorText.text = sb.ToString();
    }

    private void UpdateButtonStates()
    {
        bool isAligned = IsAlignmentComplete();
        bool bothAligned = AreBothDevicesAligned();

#if FUSION2
        bool hasNetwork = networkRunner != null && networkRunner.IsRunning;


        // Debug mode: enable start buttons if host has network (skip alignment checks)
        bool debugModeEnabled = skipAlignmentForDebug && isHost && hasNetwork;
#else
        bool hasNetwork = false;
        bool bothAligned = false;
        bool debugModeEnabled = false;
#endif

        if (autoAlignButton != null)
            autoAlignButton.interactable = !bothAligned; // Disable once aligned

        if (spawnCubeButton != null)
            spawnCubeButton.interactable = hasNetwork && bothAligned;

        if (resetButton != null)
            resetButton.interactable = true; // Always available

        // Start Game button - requires BOTH devices to be aligned
        if (startGameButton != null)
        {
            startGameButton.interactable = debugModeEnabled || (hasNetwork && bothAligned);

            // Update button text to show status
            var btnText = startGameButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                if (debugModeEnabled)
                {
                    btnText.text = "Debug Start VR";
                }
                else if (!hasNetwork)
                {
                    btnText.text = "Start VR Game";
                    startGameButton.interactable = false;
                }
                else if (!bothAligned)
                {
                    startGameButton.interactable = false;
                }
                else if (bothAligned)
                {
                    btnText.text = "Start VR Game";
                    startGameButton.interactable = true;
                }
            }
        }

        // Start Passthrough Game button - requires BOTH devices to be aligned
        if (startPassthroughGameButton != null)
        {
            startPassthroughGameButton.interactable = debugModeEnabled || (hasNetwork && bothAligned);

            // Update button text to show status
            var btnText = startPassthroughGameButton.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                if (debugModeEnabled)
                {
                    btnText.text = "Debug Start AR";
                }
                else if (!hasNetwork)
                {
                    btnText.text = "Start AR Scene";
                    startPassthroughGameButton.interactable = false;
                }
                else if (!bothAligned)
                {
                    startPassthroughGameButton.interactable = false;
                }
                else
                {
                    startPassthroughGameButton.interactable = true;
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
            case ColocationState.AdvertisingSession:
                statusIndicator.color = advertisingColor; // Purple while advertising
                break;
            case ColocationState.DiscoveringSession:
                statusIndicator.color = discoveringColor; // Orange while discovering
                break;
            case ColocationState.SharingAnchors:
                statusIndicator.color = Color.yellow; // Yellow while sharing anchors, waiting for client
                break;
            case ColocationState.HostAligned:
            case ColocationState.ClientAligned:
            case ColocationState.Done:
                statusIndicator.color = anchorAlignedColor; // Green when aligned
                break;
            case ColocationState.Idle:
            default:
                // More detailed status based on anchor localization
                int localizedCount = 0;
                int totalAnchors = currentAnchors != null ? currentAnchors.Count : 0;

                foreach (var anchor in currentAnchors)
                {
                    if (anchor != null && anchor.Localized)
                        localizedCount++;
                }

                if (totalAnchors == 0)
                {
                    statusIndicator.color = Color.gray; // No anchors placed
                }
                else if (localizedCount == totalAnchors && totalAnchors >= 2)
                {
                    statusIndicator.color = anchorAlignedColor; // All anchors localized - green
                }
                else if (localizedCount > 0)
                {
                    statusIndicator.color = Color.yellow; // Some anchors localized - yellow (partial)
                }
                else
                {
                    statusIndicator.color = anchorNotAlignedColor; // No anchors localized - red
                }
                break;
        }
    }

    public override bool IsAlignmentComplete()
    {
        // Check if THIS device is aligned (either host or client)
        bool thisDeviceAligned = currentState == ColocationState.HostAligned ||
                                  currentState == ColocationState.ClientAligned ||
                                  currentState == ColocationState.Done;

        return thisDeviceAligned;
    }


    /// Check if BOTH devices are aligned (required for starting game)
    private bool AreBothDevicesAligned()
    {
        bool thisDeviceAligned = IsAlignmentComplete();

        if (!isHost)
        {
            // Client: We're aligned, and ClientAlignedToAnchors is set by host when it receives our RPC
            // Since we just sent the RPC, we know we're aligned. Check if host is also aligned via state.
            // The host sets currentState to Done when both are aligned, which syncs via the networked object.
            return thisDeviceAligned && (currentState == ColocationState.Done || ClientAlignedToAnchors);
        }
        else
        {
            // Host: We're aligned AND client has notified us
            return thisDeviceAligned && ClientAlignedToAnchors;
        }
    }

    protected override void UpdateUIWizard()
    {
        if (autoAlignButton == null) return;

        var btnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
        if (btnText == null) return;

        switch (currentState)
        {
            case ColocationState.Idle:
                if (isHost)
                {
                    btnText.text = "Create Anchor";
                    SetStatusText("Click Create Anchors to begin alignment.", "UpdateUIWizard");
                }
                else
                {
                    btnText.text = "Discover Anchors";
                    SetStatusText("Click Discover Anchors to begin alignment.", "UpdateUIWizard");
                }
                break;

            case ColocationState.PlaceAnchor1:
                SetStatusText("Grip to place anchors.\nThe table will appear between them, aligned to their direction. Recommended distance: 2.5 meters.", "UpdateUIWizard");

                // Show progress based on anchors placed
                int anchorsPlaced = currentAnchors != null ? currentAnchors.Count : 0;
                if (anchorsPlaced == 0)
                {
                    btnText.text = "Place Anchor 1";
                }
                else if (anchorsPlaced == 1)
                {
                    btnText.text = "Place Anchor 2";
                }
                else if (anchorsPlaced >= 2)
                {
                    SetStatusText("Anchor placement complete. Click Share & Align to proceed.", "UpdateUIWizard");
                    btnText.text = "Share & Align";
                }
                else
                {
                    btnText.text = "Place Anchor";
                }
                break;
            case ColocationState.ReadyToShare:
                btnText.text = "Share & Align";
                break;
            case ColocationState.ShareFailed:
                SetStatusText("Error while sharing anchors. Click RETRY Share to try again.", "UpdateUIWizard");
                btnText.text = "RETRY Share";
                autoAlignButton.interactable = true;
                break;
            case ColocationState.SharingAnchors:
                if (isHost)
                {
                    SetStatusText("Sharing Anchors...", "UpdateUIWizard");
                    btnText.text = "Sharing...";
                    autoAlignButton.interactable = false;
                }
                else
                    btnText.text = "Discovering Anchors...";
                break;
            case ColocationState.HostAligned:
                // Host aligned, but check if client has aligned too
                if (ClientAlignedToAnchors)
                {
                    SetStatusText("Client aligned to anchors.", "UpdateUIWizard");
                    btnText.text = "Both Aligned";
                    autoAlignButton.interactable = false;
                }
                else
                {
                    SetStatusText("Host aligned. Waiting for client to align...\n\nClient may need to re-discover anchors.", "UpdateUIWizard");
                    btnText.text = "Re-advertise Anchors";
                    autoAlignButton.interactable = true;
                }
                break;
            case ColocationState.ClientAligned:
                btnText.text = "Aligned";
                autoAlignButton.interactable = false;
                break;
            case ColocationState.Done:
                // Show different text based on role and client alignment state
                if (isHost)
                {
                    // Host: show waiting until client has aligned
                    if (ClientAlignedToAnchors)
                    {
                        SetStatusText("Client aligned to anchors. You can start the colocation session.", "UpdateUIWizard");
                        btnText.text = "Both Aligned";
                        autoAlignButton.interactable = false;
                    }
                    else if (currentState == ColocationState.SharingAnchors || HostAnchorsShared)
                    {
                        SetStatusText("Host aligned. Waiting for client to align...\n\nClient may need to re-discover anchors.", "UpdateUIWizard");
                        btnText.text = "Re-advertise Anchors";
                        autoAlignButton.interactable = true; // Allow host to re-share if client failed
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



    // ==================== AUTO ALIGN ====================

    private bool guiDiscoveryStarted = false;
    private float guiLastDiscoveryTime = 0f;
    private const float DISCOVERY_RETRY_INTERVAL = 15f; // Retry every 15 seconds if no anchors found
    private ColocationState _lastKnownState = ColocationState.Idle; // Track state changes for event-driven UI updates
    private bool _lastClientAlignedState = false; // Track ClientAlignedToAnchors changes
    private bool _lastHostAnchorsSharedState = false; // Track HostAnchorsShared changes for client auto-discovery

    /// <summary>
    /// Called when colocation state changes - updates UI only when needed (event-driven)
    /// </summary>
    private void OnStateChanged(ColocationState newState)
    {
        if (_lastKnownState == newState) return;

        _lastKnownState = newState;

        // Update all UI components on state change
        UpdateStatusIndicator();
        UpdateButtonStates();
        UpdateAnchorText();
        UpdateUIWizard();
    }

    /// <summary>
    /// Check if state has changed and trigger UI update if needed
    /// </summary>
    private void CheckStateChanged()
    {
        if (currentState != _lastKnownState)
        {
            OnStateChanged(currentState);
        }

#if FUSION2
        // Also check if ClientAlignedToAnchors changed
        if (_lastClientAlignedState != ClientAlignedToAnchors)
        {
            _lastClientAlignedState = ClientAlignedToAnchors;
            UpdateButtonStates();
            UpdateUIWizard();
        }

        // Check if HostAnchorsShared changed - trigger client discovery automatically
        if (!isHost && _lastHostAnchorsSharedState != HostAnchorsShared)
        {
            _lastHostAnchorsSharedState = HostAnchorsShared;
            if (HostAnchorsShared && !guiDiscoveryStarted)
            {
                TryStartClientDiscovery();
            }
        }
#endif
    }

    private void Update()
    {
        // Don't run Update if we've switched to VR TableTennis scene or passthrough game is active
        bool passthroughActive = passthroughGameManager != null && passthroughGameManager.IsActive;
        if (isSwitchedToVRMode || passthroughActive) return;

        // Retry finding camera if it wasn't available at Start
        if (cameraTransform == null)
        {
            cameraTransform = Camera.main?.transform;
        }

        // IMPORTANT: Anchor placement preview should work even before network spawns
        // Update anchor placement cursor and distance preview (when not in VR mode)
        if (!isSwitchedToVRMode && !passthroughActive)
        {
            UpdateAnchorPlacementPreview();

            // Handle grip-to-place anchors
            HandleGripAnchorPlacement();
        }

        // Don't access networked properties if not spawned
        if (!Object.IsValid) return;

        // Event-driven UI updates - only update when state changes (not every frame)
        CheckStateChanged();
    }

    /// <summary>
    /// Called when HostAnchorsShared networked property changes - triggers client discovery
    /// </summary>
    private void OnHostAnchorsSharedChanged()
    {
#if FUSION2
        if (isHost) return; // Only clients need to respond


        if (HostAnchorsShared && !guiDiscoveryStarted)
        {
            TryStartClientDiscovery();
        }
#endif
    }

    /// <summary>
    /// Attempt to start client discovery using shared anchor UUID
    /// Called from OnAutoAlignClicked or when HostAnchorsShared changes
    /// </summary>
    private void TryStartClientDiscovery()
    {
#if FUSION2
        if (IsAlignmentComplete() || guiClientLocalizedAnchorCount >= 2)
        {
            return;
        }

        // FAST PATH: Check if host has shared anchors via network
        if (HostAnchorsShared && !string.IsNullOrEmpty(SharedAnchorGroupUuidString.ToString()))
        {
            guiDiscoveryStarted = true;
            guiLastDiscoveryTime = Time.time;

            // Parse the UUID from networked string
            if (Guid.TryParse(SharedAnchorGroupUuidString.ToString(), out Guid groupUuid))
            {
                SetStatusText("Loading shared anchors...", "ClientDiscovery");
                _sharedAnchorGroupId = groupUuid;
                LoadAndAlignToAnchor(groupUuid);
            }
            else
            {
                Debug.LogWarning($"{LOG_TAG} TryStartClientDiscovery: Failed to parse UUID");
                // Fall back to OVR discovery
                StartOvrDiscovery();
            }
        }
        else
        {
            // FALLBACK: Use OVR discovery
            StartOvrDiscovery();
        }
#endif
    }

    /// <summary>
    /// Start OVR-based anchor discovery (slower fallback)
    /// </summary>
    private void StartOvrDiscovery()
    {
#if FUSION2
        SetStatusText("Client Mode\nSearching for host...\n\nMake sure host has:\n1. Placed 2 anchors\n2. Shared them", "OvrDiscovery");

        guiDiscoveryStarted = true;
        guiLastDiscoveryTime = Time.time;
        PrepareColocation();
#endif
    }

    private async void OnAutoAlignClicked()
    {
        if (cameraTransform == null)
        {
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            SetStatusText("Network is not ready! Starting network session...", "OnAutoAlignClicked");
            return;
        }

        // Determine role
        isHost = networkRunner.IsServer || networkRunner.IsSharedModeMasterClient;
        if (statusIndicator != null) statusIndicator.color = isHost ? hostColor : clientColor;

        if (!isHost)
        {
            // Stop any existing discovery first
            OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
            _ = OVRColocationSession.StopDiscoveryAsync();
            guiDiscoveryStarted = false;
            guiLastDiscoveryTime = 0f;

            // Set client status text once (not per-frame)
            SetStatusText("Client Mode\nSearching for host session...", "OnAutoAlignClicked-Client");

            // Start fresh discovery using the new method
            TryStartClientDiscovery();
            return;
        }

        // HOST WIZARD LOGIC
        switch (currentState)
        {
            case ColocationState.Idle:
                // Start -> Grip to place anchors
                currentState = ColocationState.PlaceAnchor1;
                waitingForGripToPlaceAnchors = true;
                anchor1Placed = false;
                anchor2Placed = false;
                UpdateUIWizard();
                break;

            case ColocationState.PlaceAnchor1:
                // Remind user to grip
                if (!waitingForGripToPlaceAnchors)
                    waitingForGripToPlaceAnchors = true;
                UpdateUIWizard();
                break;
            // Check if we have 2 anchors to share
            case ColocationState.PlaceAnchor2:
                if (currentAnchors.Count >= 2)
                {
                    if (!waitingForGripToPlaceAnchors)
                        waitingForGripToPlaceAnchors = true;
                    UpdateUIWizard();
                    break;

                }
                else
                {
                    SetStatusText("Need more anchors to share.", "OnAutoAlignClicked");
                    UpdateUIWizard();
                }
                break;

            case ColocationState.ReadyToShare:
                // Disable button and show sharing state immediately
                autoAlignButton.interactable = false;
                var readyBtnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
                if (readyBtnText != null) readyBtnText.text = "Sharing...";
                PrepareColocation();
                break;

            case ColocationState.ShareFailed:
                // Retry sharing
                currentState = ColocationState.ReadyToShare;
                autoAlignButton.interactable = false;
                var retryBtnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
                if (retryBtnText != null) retryBtnText.text = "Sharing...";
                PrepareColocation();
                break;

            case ColocationState.HostAligned:
                // Re-advertise anchors if client hasn't aligned yet
                if (!ClientAlignedToAnchors)
                {
                    currentState = ColocationState.ReadyToShare;
                    autoAlignButton.interactable = false;
                    var readvBtnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (readvBtnText != null) readvBtnText.text = "Sharing...";
                    PrepareColocation();
                }
                break;

            case ColocationState.Done:
                // Re-advertise anchors if client hasn't aligned yet
                if (!ClientAlignedToAnchors)
                {
                    currentState = ColocationState.ReadyToShare;
                    autoAlignButton.interactable = false;
                    var doneBtnText = autoAlignButton.GetComponentInChildren<TextMeshProUGUI>();
                    if (doneBtnText != null) doneBtnText.text = "Sharing...";
                    PrepareColocation();
                }
                break;
        }
#endif
    }

    // ==================== ANCHOR PLACEMENT & ALIGNMENT ====================
    /// Handle grip button to place anchors 
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

    /// Update anchor placement cursor and distance preview (when not in VR mode)
    private void UpdateAnchorPlacementPreview()
    {
        bool inPlacementMode = waitingForGripToPlaceAnchors && (currentState == ColocationState.PlaceAnchor1 || currentState == ColocationState.PlaceAnchor2);


        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null)
        {
            return;
        }

        Transform leftHand = cameraRig.leftControllerAnchor;
        Transform rightHand = cameraRig.rightControllerAnchor;

        if (leftHand == null || rightHand == null)
        {
            Debug.LogWarning($"{LOG_TAG} UpdateAnchorPlacementPreview: Controller anchors not found! leftHand={leftHand != null}, rightHand={rightHand != null}");
            return;
        }



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

                Color col = distanceLeft < 1.0f ? Color.red : (distanceLeft < 2.5f ? Color.yellow : Color.green);
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

                Color col = distanceRight < 1.0f ? Color.red : (distanceRight < 2.5f ? Color.yellow : Color.green);
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

        if (anchorNumber == 1)
        {
            // Place Anchor 1
            var anchor1 = await CreateAnchor(anchorPosition, Quaternion.identity);

            if (anchor1 != null)
            {
                anchor1Placed = true;
                firstAnchorWorldPosition = anchorPosition;
                currentState = ColocationState.PlaceAnchor2;
                UpdateUIWizard();
            }
            else
            {
                // Failed to create Anchor 1
            }
            isPlacingAnchor = false; // Allow next placement
        }
        else if (anchorNumber == 2)
        {
            // Check distance from first anchor
            float distance = Vector3.Distance(firstAnchorWorldPosition, anchorPosition);

            if (distance < 0.5f)
            {
                isPlacingAnchor = false; // Allow retry
                SetStatusText("Anchors must be at least 0.5 meters apart. Please move further away and try again.", "PlaceAnchorAtController");
                return;
            }

            var anchor2 = await CreateAnchor(anchorPosition, Quaternion.identity);

            if (anchor2 != null)
            {
                anchor2Placed = true;
                CheckBothAnchorsPlaced();
            }
            else
            {
                SetStatusText("Failed to create Anchor 2.", "PlaceAnchorAtController");
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
            if (leftDistanceDisplay != null) leftDistanceDisplay.SetActive(false);
            if (rightDistanceDisplay != null) rightDistanceDisplay.SetActive(false);

            float distance = Vector3.Distance(currentAnchors[0].transform.position, currentAnchors[1].transform.position);
            currentState = ColocationState.ReadyToShare;
            UpdateUIWizard();
        }
        else
        {
        }
    }


    // ==================== PUBLIC ACCESSORS FOR PassthroughGameManager ====================

    /// Get the current list of anchors
    public List<OVRSpatialAnchor> GetCurrentAnchors()
    {
        return currentAnchors;
    }

    /// Get the current session state
    public ColocationState GetCurrentState()
    {
        return currentState;
    }

    /// Set waiting for grip to place anchors
    public void SetWaitingForGripToPlaceAnchors(bool value)
    {
        waitingForGripToPlaceAnchors = value;
    }

    /// Check if passthrough rackets are currently visible
    public bool ArePassthroughRacketsVisible()
    {
        // Delegate to PassthroughGameManager if available
        if (passthroughGameManager != null)
        {
            return passthroughGameManager.IsActive && passthroughGameManager.RacketsVisible;
        }
        // No passthrough game manager available
        return false;
    }

#if FUSION2
    /// Client notifies host that it has aligned to anchors
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    protected override void RPC_NotifyClientAligned()
    {
        ClientAlignedToAnchors = true;

        // Only set to Done when BOTH host and client are aligned
        if (currentState == ColocationState.HostAligned || currentState == ColocationState.Done || IsAlignmentComplete())
        {
            currentState = ColocationState.Done;
            UpdateUIWizard();
            UpdateAllUI();

            // Update status text to show both aligned
            if (guiStatusText != null)
            {
                SetStatusText("Both Players Aligned\n\nReady to play!", "RPC-BothAligned");
            }

            // Notify ALL clients that both devices are now aligned
            RPC_NotifyBothAligned();
        }
        else
        {
            // Host not aligned yet, just set client aligned state
            currentState = ColocationState.ClientAligned;
            UpdateUIWizard();
            UpdateAllUI();

            // Update status text to show waiting for host
            if (guiStatusText != null)
            {
                SetStatusText("Client Aligned\n\nWaiting for Host...", "RPC-ClientAligned");
            }
        }
    }

    /// Host notifies all clients that both devices are aligned
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyBothAligned()
    {

        // Update state to Done on all clients
        currentState = ColocationState.Done;
        UpdateUIWizard();
        UpdateAllUI();

        // Update status text
        if (guiStatusText != null)
        {
            SetStatusText("Both Players Aligned\n\nReady to play!", "RPC-BothAligned");
        }
    }

#endif

    // ==================== OVERRIDE BASE CLASS METHODS ====================

    public override void Spawned()
    {
        base.Spawned();

#if FUSION2
        // Cache NetworkRunner on spawn (one-time lookup)
        if (networkRunner == null)
        {
            networkRunner = Runner;
        }

        // Detect role once on spawn - role doesn't change mid-session
        isHost = Object.HasStateAuthority;
#endif

        UpdateStatusIndicator();
        UpdateUIWizard();
        // Do NOT auto-start colocation/alignment here
    }

    /// Returns the localized spatial anchor for parenting cubes.
    public OVRSpatialAnchor GetLocalizedAnchor()
    {
        return _localizedAnchor;
    }

    /// Helper method to update status text with logging
    private void SetStatusText(string text, string source = "")
    {
        if (guiStatusText != null)
        {
            string oldText = guiStatusText.text;
            guiStatusText.text = text;

            // Log status text changes for debugging
            if (oldText != text)
            {
            }
        }
        UpdateStatusIndicator();
    }

    // ==================== OVERRIDE BASE CLASS METHODS ====================
    protected override async void DiscoverNearbySession()
    {
        base.DiscoverNearbySession();
    }

    protected override void OnColocationSessionDiscovered(OVRColocationSession.Data session)
    {
        // Update UI to show discovery status
        if (guiStatusText != null)
        {
            SetStatusText("Session found!\nLoading anchors...", "SessionDiscovered");
        }

        base.OnColocationSessionDiscovered(session);
    }

    private void OnSpawnCubeClicked()
    {
#if FUSION2
        
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            return;
        }

        if (!IsAlignmentComplete())
        {
            return;
        }

        if (!cubePrefab.IsValid)
        {
            return;
        }

        // Get controller position and convert to anchor-relative
        Vector3 worldPos = GetControllerSpawnPosition();
        Vector3 anchorRelativePos = worldPos;

        if (_localizedAnchor != null && _localizedAnchor.Localized)
        {
            anchorRelativePos = _localizedAnchor.transform.InverseTransformPoint(worldPos);
        }

        if (!Object.HasStateAuthority)
        {
            RPC_RequestSpawnCube(anchorRelativePos);
        }
        else
        {
            SpawnCubeAtAnchorPosition(anchorRelativePos);
        }

        // Show cube instructions popup (auto-create if not assigned in Inspector)
        if (cubeInstructionsPopup == null)
        {
            var popupObj = new GameObject("CubeInstructionsPopup");
            cubeInstructionsPopup = popupObj.AddComponent<CubeInstructionsPopup>();
        }
        cubeInstructionsPopup.ShowPopup();
#else
#endif
    }

#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnCube(Vector3 anchorRelativePos)
    {
        SpawnCubeAtAnchorPosition(anchorRelativePos);
    }

    private void SpawnCubeAtAnchorPosition(Vector3 anchorRelativePos)
    {
        // Clear existing cube first (limit to 1)
        if (spawnedCube != null && Runner != null)
        {
            Runner.Despawn(spawnedCube);
            spawnedCube = null;
        }

        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            Debug.LogError("[AnchorGUIManager SpawnCubeAtAnchorPosition] Cannot spawn cube - no localized anchor!");
            return;
        }

        Vector3 worldPos = _localizedAnchor.transform.TransformPoint(anchorRelativePos);

        var newCube = Runner.Spawn(
            cubePrefab,
            worldPos,
            Quaternion.identity,
            Object.InputAuthority
        );

        if (newCube != null)
        {
            newCube.transform.localScale = Vector3.one * cubeScale;
            spawnedCube = newCube;
        }
        else
        {
            Debug.LogError("[AnchorGUIManager] Failed to spawn cube");
        }
    }

    private void DespawnAllCubesOnHost()
    {
        if (!Object.HasStateAuthority || Runner == null || !Runner.IsRunning)
            return;

        // Find all NetworkObjects and check for cube-like names
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

        Debug.Log($"[AnchorGUIManager DespawnAllCubesOnHost] Found {cubesToDespawn.Count} networked objects to despawn");

        foreach (var cube in cubesToDespawn)
        {
            if (cube == null || cube.gameObject == null) continue;

            // CRITICAL: Unparent from anchor first to prevent anchor from keeping the object alive
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

        // Also destroy any GameObjects with "Cube" in name that might not have NetworkObject component
        var allObjects = FindObjectsOfType<GameObject>();
        foreach (var obj in allObjects)
        {
            if (obj == null) continue;
            
            string lowerName = obj.name.ToLower();
            if (lowerName.Contains("cube") || lowerName.Contains("networked") || lowerName.Contains("grabbable"))
            {
                // Skip visual markers and UI elements
                if (obj.name == "Visual" || obj.name.Contains("Marker") || obj.name.Contains("Canvas") || obj.name.Contains("UI")) continue;
                
                // Check if it's a NetworkObject (already handled above) or regular GameObject
                var netObj = obj.GetComponent<NetworkObject>();
                if (netObj == null)
                {
                    Destroy(obj);
                }
            }
        }

        spawnedCube = null;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestDespawnAllCubes()
    {
        DespawnAllCubesOnHost();
    }

    public void DespawnAllCubes()
    {
        if (Runner == null || !Runner.IsRunning)
            return;

        if (Object.HasStateAuthority)
            DespawnAllCubesOnHost();
        else
            RPC_RequestDespawnAllCubes();
    }
#endif

    private void OnResetClicked()
    {

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
                RPC_RequestDespawnAllCubes();
            }
        }
#endif

        // Destroy all anchors
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null)
            {
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
        }

        // Reset state
        currentState = ColocationState.Idle; // Reset wizard
        _sharedAnchorGroupId = Guid.Empty;
        _localizedAnchor = null;
        anchor1Placed = false;
        anchor2Placed = false;
        waitingForGripToPlaceAnchors = false;
        firstAnchorWorldPosition = Vector3.zero;
        isPlacingAnchor = false; // Reset placement lock
        hostAutoStarted = false; // Allow auto-start again

        // Reset client-specific discovery state
        guiDiscoveryStarted = false;
        guiLastDiscoveryTime = 0f;
        guiClientLocalizedAnchorCount = 0;

        // Reset event-driven state tracking
        _lastKnownState = ColocationState.Idle;
        _lastClientAlignedState = false;
        _lastHostAnchorsSharedState = false;

        // Hide all placement UI
        if (leftDistanceDisplay != null) leftDistanceDisplay.SetActive(false);
        if (rightDistanceDisplay != null) rightDistanceDisplay.SetActive(false);

        if (autoAlignButton != null) autoAlignButton.interactable = true; // Re-enable

        // Reset UI
        UpdateAllUI();
        UpdateUIWizard();
    }



    // ==================== START GAME (TABLE TENNIS) ====================
    private void OnStartGameClicked()
    {

        // Check if aligned first
        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            return;
        }

        // Check if both devices are aligned
        if (!IsAlignmentComplete())
        {
            return;
        }

        // Despawn any existing cubes before loading VR scene
        DespawnAllCubes();

        // Either player can initiate - request goes to host, host loads scene
        if (Object.HasStateAuthority)
        {
            HideMainGUIPanel(); // Hide UI before loading scene
            LoadTableTennisSceneNetworked();
        }
        else
        {
            HideMainGUIPanel(); // Hide UI before scene loads
            RPC_RequestStartGame();
        }
#else
        // Non-networked fallback
        LoadTableTennisSceneLocal();
#endif
    }

    // ==================== PASSTHROUGH GAME ====================
    /// Start passthrough table tennis - delegates to PassthroughGameManager
    private void OnStartPassthroughGameClicked()
    {
        passthroughGameManager?.OnStartPassthroughGameClicked();
    }


    // Reference to hidden main GUI for restoring later
    private GameObject hiddenMainGUICanvas;
    private List<GameObject> hiddenUIElements = new List<GameObject>();


    /// Hide the main GUI panel that's a child of the Camera Rig and any other alignment UI
    public void HideMainGUIPanel()
    {
        hiddenUIElements.Clear();
        // Find and hide Canvas under Camera Rig
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            var canvases = cameraRig.GetComponentsInChildren<Canvas>(true);
            foreach (var canvas in canvases)
            {
                if (canvas != null && canvas.gameObject.activeSelf)
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
    }

    /// Restore the main GUI panel that was hidden when starting the game
    public void ShowMainGUIPanel()
    {
        foreach (var uiElement in hiddenUIElements)
        {
            if (uiElement != null)
            {
                uiElement.SetActive(true);
            }
        }
        hiddenUIElements.Clear();
        hiddenMainGUICanvas = null;
        Debug.Log("[AnchorGUIManager] Main GUI panel restored");
    }

#if FUSION2
    /// Client requests host to start the game
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestStartGame()
    {
        LoadTableTennisSceneNetworked();
    }

    /// Called by host to notify all clients to preserve their anchors before scene transition
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_PrepareForSceneTransition()
    {

        // Cleanup passthrough objects to prevent duplicates in VR scene
        CleanupPassthroughObjects();

        // Preserve anchors for the new scene
        PreserveObjectsForSceneTransition();
    }


    /// Cleanup passthrough-specific objects before switching to VR scene
    private void CleanupPassthroughObjects()
    {
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

        if (passthroughGameUIPanel != null)
        {
            Destroy(passthroughGameUIPanel);
            passthroughGameUIPanel = null;
        }

        passthroughPhase = PassthroughGamePhase.Idle;
    }


    /// Host loads scene using Fusion's networked scene loading
    /// This automatically syncs to all connected clients
    private void LoadTableTennisSceneNetworked()
    {
        if (Runner != null && Runner.IsRunning)
        {

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
                Debug.LogError($"[AnchorGUIManager LoadTableTennisSceneNetworked (scene)] Scene '{tableTennisSceneName}' not found in Build Settings! Add it via File > Build Settings");
            }
        }
        else
        {
            Debug.LogError("[AnchorGUIManager LoadTableTennisSceneNetworked (scene)] Cannot load scene - Runner not available");
        }
    }


    /// Preserve anchor and spawned cube across scene transitions for alignment verification
    private void PreserveObjectsForSceneTransition()
    {
        // CLEANUP: Destroy any existing tables before scene transition to prevent duplicates
        var existingTables = new List<GameObject>();
        existingTables.AddRange(GameObject.FindObjectsOfType<GameObject>(true).Where(go => 
            go.name.Contains("PingPongTable") || go.name.Contains("pingpongtable") || 
            go.name.Contains("pingpong") || go.name.Contains("PingPong") || 
            go.name.Contains("TableTennis")));
        
        foreach (var table in existingTables)
        {
            if (table != null && table.scene.name != "DontDestroyOnLoad")
                Destroy(table);
        }

        if (_localizedAnchor != null)
            DontDestroyOnLoad(_localizedAnchor.gameObject);

        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.gameObject != null)
                DontDestroyOnLoad(anchor.gameObject);
        }
    }


#endif

    private Vector3 GetControllerAnchorPosition()
    {
        // Get right controller position at waist level (no Y manipulation)
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
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
        // If both anchors are available, spawn above their midpoint
        if (currentAnchors != null && currentAnchors.Count >= 2 && currentAnchors[0] != null && currentAnchors[1] != null)
        {
            Vector3 anchorMid = (currentAnchors[0].transform.position + currentAnchors[1].transform.position) * 0.5f;
            Vector3 aboveAnchors = anchorMid + Vector3.up * 0.5f; // 0.5m above midpoint
            if (aboveAnchors.y < 0.5f) aboveAnchors.y = 0.5f;
            return aboveAnchors;
        }

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


    /// Override to provide controller-based anchor positioning
    protected override Vector3 GetDefaultAnchorPosition()
    {
        return GetControllerAnchorPosition();
    }

    /// Override to provide camera-facing anchor rotation
    protected override Quaternion GetDefaultAnchorRotation()
    {
        return Quaternion.Euler(0, cameraTransform.eulerAngles.y, 0);
    }


    /// Override ShareAnchors to handle debug mode
    protected override async void ShareAnchors()
    {
        // DEBUG MODE: Use local anchors without sharing
        if (skipAlignmentForDebug && currentAnchors.Count >= 2)
        {
            // Sort anchors by UUID for consistent ordering (even in debug mode)
            SortAnchorsConsistently();

            _localizedAnchor = currentAnchors[0];
            alignmentManager.AlignUserToTwoAnchors(currentAnchors[0], currentAnchors[1]);

            FirstAnchorPosition = currentAnchors[0].transform.position;
            SecondAnchorPosition = currentAnchors[1].transform.position;
            FirstAnchorUuid = currentAnchors[0].Uuid;
            SecondAnchorUuid = currentAnchors[1].Uuid;
            AlignmentCompletedStatic = true;
            currentState = ColocationState.HostAligned;

            return;
        }

        base.ShareAnchors();
    }

}
