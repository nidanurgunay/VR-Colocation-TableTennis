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
        // Hide and disable the Start VR Game button (not currently used)
        if (startGameButton != null)
        {
            Debug.Log($"{LOG_TAG} Hiding startGameButton (assigned in Inspector)");
            startGameButton.gameObject.SetActive(false);
            startGameButton = null;
        }
        else
        {
            // Try to find and hide the button by name if not assigned
            var vrButton = GameObject.Find("StartGameButton") ?? GameObject.Find("Start VR Game") ?? GameObject.Find("StartVRGame");
            if (vrButton != null)
            {
                Debug.Log($"{LOG_TAG} Hiding startGameButton found by name: {vrButton.name}");
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
            SetStatusText("Click Auto Align to start", "Start");
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

        Debug.Log($"{LOG_TAG} CreateDistanceDisplays: Created left={leftDistanceDisplay != null}, right={rightDistanceDisplay != null}");
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

        Debug.Log($"{LOG_TAG} UpdateButtonStates isHost={isHost} isAligned={isAligned} bothAligned={bothAligned} ClientAlignedToAnchors={ClientAlignedToAnchors} currentState={currentState}");

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

    private bool IsAlignmentComplete()
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
                SetStatusText("Grip to place anchors.\n The table will be positioned between them and aligned to their direction.", "UpdateUIWizard");

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
                        SetStatusText("Client aligned to anchors.", "UpdateUIWizard");
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

        UpdateStatusIndicator();
        UpdateButtonStates();
        UpdateAnchorText(); // Update anchor status display every frame for real-time feedback

        try
        {

#if FUSION2
            // Auto-detect role and update UI text for Client
            if (networkRunner == null) networkRunner = FindObjectOfType<NetworkRunner>();

            if (networkRunner != null && networkRunner.IsRunning)
            {
                // Update role variable - ALWAYS update based on actual network role
                bool localIsHost = networkRunner.IsServer || networkRunner.IsSharedModeMasterClient;

                // Update isHost if it doesn't match the actual network role
                if (isHost != localIsHost)
                {
                    isHost = localIsHost;
                    UpdateUIWizard();
                }

                // If we are a client and NOT aligned yet, handle discovery
                if (!localIsHost && currentState != ColocationState.Done && currentState != ColocationState.ClientAligned)
                {
                    // Client-specific UI updates
                    if (guiStatusText != null && !guiStatusText.text.Contains("Client Mode"))
                    {
                        SetStatusText("Client Mode\nSearching for host session...", "Update-Client");
                    }

                    // Auto-start or retry discovery if not aligned
                    // BUT skip if we already have 2 anchors localized
                    if (!IsAlignmentComplete() && guiClientLocalizedAnchorCount < 2)
                    {
                        // FAST PATH: Check if host has shared anchors via network (no discovery needed)
                        if (HostAnchorsShared && !string.IsNullOrEmpty(SharedAnchorGroupUuidString.ToString()))
                        {
                            if (!guiDiscoveryStarted)
                            {
                                guiDiscoveryStarted = true;
                                guiLastDiscoveryTime = Time.time;

                                // Parse the UUID from networked string
                                if (Guid.TryParse(SharedAnchorGroupUuidString.ToString(), out Guid groupUuid))
                                {
                                    _sharedAnchorGroupId = groupUuid;
                                    LoadAndAlignToAnchor(groupUuid);
                                }
                                else
                                {
                                    // Failed to parse UUID
                                }
                            }
                        }
                        // FALLBACK: Use OVR discovery (slower but works without network sync)
                        else if (!guiDiscoveryStarted || (Time.time - guiLastDiscoveryTime > DISCOVERY_RETRY_INTERVAL))
                        {

                            // Show status on UI
                            if (guiStatusText != null && !guiStatusText.text.Contains("Loading"))
                            {
                                SetStatusText("Client Mode\nSearching for host...\n\nMake sure host has:\n1. Placed 2 anchors\n2. Shared them", "Update-Discovery");
                            }

                            guiDiscoveryStarted = true;
                            guiLastDiscoveryTime = Time.time;
                            PrepareColocation();
                        }
                    }
                }
                // No need for explicit host handling here - isHost is updated above for both host and client
            }
            else if (networkRunner == null)
            {
                // Show "Connecting..." state
                if (guiStatusText != null && !guiStatusText.text.Contains("Connect"))
                {
                    SetStatusText("Connecting to network...", "Update-Network");
                }
            }
#endif
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[AnchorGUIManager Update (general)] Error in Update(): {e.Message}\n{e.StackTrace}");
        }
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
            // Start fresh discovery
            PrepareColocation();
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
                // Share
                PrepareColocation(); 
                UpdateUIWizard();
                break;

            case ColocationState.ShareFailed:
                // Retry sharing
                currentState = ColocationState.ReadyToShare;
                PrepareColocation(); // Triggers ShareAnchors()
                UpdateUIWizard();
                break;

            case ColocationState.HostAligned:
                // Re-advertise anchors if client hasn't aligned yet
                if (!ClientAlignedToAnchors)
                {
                    currentState = ColocationState.ReadyToShare;
                    PrepareColocation(); // Triggers ShareAnchors()
                    UpdateUIWizard();
                }
                break;

            case ColocationState.Done:
                // Re-advertise anchors if client hasn't aligned yet
                if (!ClientAlignedToAnchors)
                {
                    currentState = ColocationState.ReadyToShare;
                    PrepareColocation(); // Triggers ShareAnchors()
                    UpdateUIWizard();
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

            if (distance < 0.3f)
            {
                isPlacingAnchor = false; // Allow retry
                SetStatusText("Anchors must be at least 0.3 meters apart. Please move further away and try again.", "PlaceAnchorAtController");
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
        Debug.Log("[AnchorGUIManager RPC_NotifyClientAligned (network)] HOST received: Client has aligned to anchors");
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
        Debug.Log("[AnchorGUIManager RPC_NotifyBothAligned (network)] Received: Both devices are aligned");

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
        isHost = Object.HasStateAuthority;
        UpdateStatusIndicator();
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
                Debug.Log($"{LOG_TAG} [STATUS] {source} changed status: '{oldText}' -> '{text}'");
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

        // Log camera rig position - this shows if alignment was applied
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            Debug.Log($"[SPAWN DEBUG] CameraRig position: {cameraRig.transform.position}, rotation: {cameraRig.transform.eulerAngles}");
        }
        
        // Log anchor position
        if (_localizedAnchor != null)
        {
            Debug.Log($"[SPAWN DEBUG] _localizedAnchor: {_localizedAnchor.name}, UUID: {_localizedAnchor.Uuid}");
            Debug.Log($"[SPAWN DEBUG] Anchor world pos: {_localizedAnchor.transform.position}, rot: {_localizedAnchor.transform.eulerAngles}");
        }
        else
        {
            Debug.LogError("[SPAWN DEBUG] _localizedAnchor is NULL!");
        }

        // Get controller position and convert to anchor-relative
        Vector3 worldPos = GetControllerSpawnPosition();
        Vector3 anchorRelativePos = worldPos;
        
        Debug.Log($"[SPAWN DEBUG] Controller spawn position (world): {worldPos}");

        if (_localizedAnchor != null && _localizedAnchor.Localized)
        {
            anchorRelativePos = _localizedAnchor.transform.InverseTransformPoint(worldPos);
            Debug.Log($"[SPAWN DEBUG] Anchor-relative pos: {anchorRelativePos}");
            
            // Verify: convert back to world to check consistency
            Vector3 verifyWorldPos = _localizedAnchor.transform.TransformPoint(anchorRelativePos);
            Debug.Log($"[SPAWN DEBUG] Verify (back to world): {verifyWorldPos}");
        }
        else
        {
            Debug.LogWarning("[SPAWN DEBUG] No localized anchor! Using world position directly.");
        }

        // Request spawn via RPC if not host
        if (!Object.HasStateAuthority)
        {
            Debug.Log("[SPAWN DEBUG] CLIENT: Sending RPC_RequestSpawnCube to host");
            RPC_RequestSpawnCube(anchorRelativePos);
        }
        else
        {
            Debug.Log("[SPAWN DEBUG] HOST: Calling SpawnCubeAtAnchorPosition directly");
            SpawnCubeAtAnchorPosition(anchorRelativePos);
        }

        Debug.Log("=== [SPAWN CUBE DEBUG END] ===");
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
            Debug.Log($"[AnchorGUIManager SpawnCubeAtAnchorPosition] Despawning existing cube: {spawnedCube.Id}");
            Runner.Despawn(spawnedCube);
            spawnedCube = null;
        }

        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            Debug.LogError("[AnchorGUIManager SpawnCubeAtAnchorPosition] Cannot spawn cube - no localized anchor!");
            return;
        }

        // SIMPLIFIED: Spawn at world position - NetworkedCube handles sync via world position
        Vector3 worldPos = _localizedAnchor.transform.TransformPoint(anchorRelativePos);
        Debug.Log($"[AnchorGUIManager SpawnCubeAtAnchorPosition] Spawning at world pos: {worldPos}");

        var newCube = Runner.Spawn(
            cubePrefab,
            worldPos,
            Quaternion.identity,
            Object.InputAuthority
        );

        if (newCube != null)
        {
            // DON'T parent to anchor - NetworkedCube uses world position sync
            newCube.transform.localScale = Vector3.one * cubeScale;
            spawnedCube = newCube;
            Debug.Log($"[AnchorGUIManager SpawnCubeAtAnchorPosition] Cube spawned at world pos: {worldPos}, NetworkId: {newCube.Id}");
        }
        else
        {
            Debug.LogError("[AnchorGUIManager SpawnCubeAtAnchorPosition] Failed to spawn cube!");
        }
    }

    private void DespawnAllCubesOnHost()
    {
        // Only the host (state authority) should despawn networked cubes
        if (!Object.HasStateAuthority || Runner == null || !Runner.IsRunning)
        {
            Debug.Log("[AnchorGUIManager DespawnAllCubesOnHost (cube)] Not host or runner not ready, cannot despawn cubes");
            return;
        }

        // Find and despawn all NetworkedCube objects via Fusion
        var allCubes = FindObjectsOfType<NetworkedCube>();
        Debug.Log($"[AnchorGUIManager DespawnAllCubesOnHost (cube)] Host despawning {allCubes.Length} cubes via network");

        foreach (var cube in allCubes)
        {
            if (cube != null && cube.Object != null && cube.Object.IsValid)
            {
                Debug.Log($"[AnchorGUIManager DespawnAllCubesOnHost (cube)] Despawning cube: {cube.Object.Id}");
                Runner.Despawn(cube.Object);
            }
        }
        spawnedCube = null;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestDespawnAllCubes()
    {
        Debug.Log("[AnchorGUIManager RPC_RequestDespawnAllCubes (cube)] Host received request to despawn all cubes");
        DespawnAllCubesOnHost();
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
                // Client: request host to despawn cubes
                Debug.Log("[AnchorGUIManager OnResetClicked (cube)] Client requesting host to despawn cubes");
                RPC_RequestDespawnAllCubes();
            }
        }
#endif

        // Destroy all anchors
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null)
            {
                Debug.Log($"[AnchorGUIManager OnResetClicked (reset)] Destroying anchor: {anchor.Uuid}");
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
            Debug.Log("[AnchorGUIManager OnResetClicked (reset)] Camera rig reset to origin");
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

#if FUSION2
    /// Client requests host to start the game
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestStartGame()
    {
        Debug.Log("[AnchorGUIManager RPC_RequestStartGame (network)] Host received request to start game - loading scene for all");
        LoadTableTennisSceneNetworked();
    }

    /// Called by host to notify all clients to preserve their anchors before scene transition
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_PrepareForSceneTransition()
    {
        Debug.Log("[AnchorGUIManager RPC_PrepareForSceneTransition (network)] Received scene transition notification - cleaning up and preserving anchors");

        // Cleanup passthrough objects to prevent duplicates in VR scene
        CleanupPassthroughObjects();

        // Preserve anchors for the new scene
        PreserveObjectsForSceneTransition();
    }


    /// Cleanup passthrough-specific objects before switching to VR scene
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

        // Destroy passthrough UI panel
        if (passthroughGameUIPanel != null)
        {
            Destroy(passthroughGameUIPanel);
            passthroughGameUIPanel = null;
        }

        // Reset state
        passthroughPhase = PassthroughGamePhase.Idle;

        Debug.Log("[Passthrough] Passthrough cleanup complete");
    }


    /// Host loads scene using Fusion's networked scene loading
    /// This automatically syncs to all connected clients
    private void LoadTableTennisSceneNetworked()
    {
        if (Runner != null && Runner.IsRunning)
        {
            Debug.Log($"[AnchorGUIManager LoadTableTennisSceneNetworked (scene)] Host loading networked scene: {tableTennisSceneName}");

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
            if (table != null && table.scene.name != "DontDestroyOnLoad") // Don't destroy preserved objects
            {
                Debug.Log($"[AnchorGUIManager PreserveObjectsForSceneTransition (scene)] Destroying existing table: {table.name}");
                Destroy(table);
            }
        }

        // Preserve the localized anchor (this is crucial for alignment in new scene)
        if (_localizedAnchor != null)
        {
            Debug.Log($"[AnchorGUIManager PreserveObjectsForSceneTransition (scene)] Preserving localized anchor: {_localizedAnchor.Uuid}, world pos: {_localizedAnchor.transform.position}");
            DontDestroyOnLoad(_localizedAnchor.gameObject);
            Debug.Log($"[AnchorGUIManager PreserveObjectsForSceneTransition (scene)] Preserved anchor for scene transition: {_localizedAnchor.Uuid}");
        }
        else
        {
            Debug.LogWarning("[AnchorGUIManager PreserveObjectsForSceneTransition (scene)] No localized anchor to preserve!");
        }

        // Also preserve all tracked anchors
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.gameObject != null)
            {
                Debug.Log($"[AnchorGUIManager PreserveObjectsForSceneTransition (scene)] Preserving tracked anchor: {anchor.Uuid}, world pos: {anchor.transform.position}");
                DontDestroyOnLoad(anchor.gameObject);
            }
        }
        
        Debug.Log($"[AnchorGUIManager PreserveObjectsForSceneTransition (scene)] Total anchors preserved: {(currentAnchors?.Count ?? 0) + (_localizedAnchor != null ? 1 : 0)}");
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
            // Perform local alignment as if anchors were shared
            _localizedAnchor = currentAnchors[0];

            // Log anchor positions BEFORE alignment
            Debug.Log($"[Anchor] DEBUG Pre-align Anchor1: {currentAnchors[0].transform.position}");
            Debug.Log($"[Anchor] DEBUG Pre-align Anchor2: {currentAnchors[1].transform.position}");

            alignmentManager.AlignUserToTwoAnchors(currentAnchors[0], currentAnchors[1]);

            // Store anchor positions and mark alignment complete
            FirstAnchorPosition = currentAnchors[0].transform.position;
            SecondAnchorPosition = currentAnchors[1].transform.position;
            AlignmentCompletedStatic = true;

            // Set state to indicate we're "aligned" for UI purposes
            currentState = ColocationState.HostAligned;

            Debug.Log($"[Anchor] DEBUG Stored positions: Anchor1={FirstAnchorPosition}, Anchor2={SecondAnchorPosition}");

            return;
        }

        base.ShareAnchors();
    }

}
