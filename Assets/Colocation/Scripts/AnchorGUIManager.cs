using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Collections.Generic;
using Image = UnityEngine.UI.Image;
using Debug = UnityEngine.Debug;
using Application = UnityEngine.Application;

#if FUSION2
using Fusion;
#endif

public class AnchorGUIManager : MonoBehaviour
{
    [Header("Main Action Buttons")]
    [SerializeField] private Button hostSessionButton;
    [SerializeField] private Button joinSessionButton;
    [SerializeField] private Button leaveSessionButton;
    [SerializeField] private Button createAnchorButton;
    [SerializeField] private Button saveAnchorButton;
    [SerializeField] private Button loadAnchorsButton;
    [SerializeField] private Button shareAnchorsButton;
    [SerializeField] private Button clearAnchorsButton;

    [Header("Room Name Selection")]
    [SerializeField] private TMP_Dropdown roomNameDropdown;
    [SerializeField] private TMP_InputField roomNameInputField;
    [SerializeField] private TextMeshProUGUI roomNameDisplayText;

    [Header("Status Display")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI groupUuidText;
    [SerializeField] private TextMeshProUGUI anchorText;
    [SerializeField] private TextMeshProUGUI connectionStateText;
    [SerializeField] private Image statusIndicator;

    [Header("Confirmation Dialog")]
    [SerializeField] private GameObject confirmationDialog;
    [SerializeField] private TextMeshProUGUI confirmationText;
    [SerializeField] private Button confirmYesButton;
    [SerializeField] private Button confirmNoButton;

    [Header("Settings")]
    [SerializeField] private Color hostColor = Color.green;
    [SerializeField] private Color clientColor = Color.cyan;
    [SerializeField] private Color idleColor = Color.gray;

    [Header("Controller Anchor Creation")]
    private GameObject anchorCursorPrefab; // Icon shown on controller
    private GameObject anchorMarkerPrefab;
    [SerializeField] private Transform rightControllerTransform;
    [SerializeField] private Transform leftControllerTransform;
    [SerializeField] private float cursorOffset = 0.1f;
    [SerializeField] private float cursorScale = 0.05f;
    [SerializeField] private float anchorScale = 0.1f;

    private List<OVRSpatialAnchor> currentAnchors;
    private bool isHost;
    private Guid currentGroupUuid;
    private Transform cameraTransform;
    private Action pendingConfirmationAction;

    private bool isPlacingAnchor = false;
    private GameObject leftCursorInstance;
    private GameObject rightCursorInstance;

    // Room name options
    private readonly string[] roomNameOptions = new string[]
    {
        "Mars",
        "Venus",
        "Jupiter",
        "Saturn",
        "Nebula",
        "Comet",
        "Custom..."
    };
    private Dictionary<Guid, bool> savedAnchors = new Dictionary<Guid, bool>();
    private Dictionary<Guid, bool> sharedAnchors = new Dictionary<Guid, bool>();
    private const int CUSTOM_ROOM_INDEX = 6;

#if FUSION2
    private NetworkRunner networkRunner;
#endif
    private bool isAdvertising = false;
    private bool isDiscovering = false;

    private enum SessionState
    {
        Idle,
        Hosting,
        Joining,
        Connected,
        Loading,
        Sharing
    }

    private SessionState currentState;

    private void Start()
    {
        currentAnchors = new List<OVRSpatialAnchor>();
        currentGroupUuid = Guid.Empty;
        currentState = SessionState.Idle;
        isHost = false;
        savedAnchors = new Dictionary<Guid, bool>();
        sharedAnchors = new Dictionary<Guid, bool>();

        cameraTransform = Camera.main?.transform;
        if (cameraTransform == null)
        {
            Debug.LogError("[AnchorGUI] No main camera found!");
            return;
        }

        Debug.Log("[AnchorGUI] Loading AnchorCursor from Resources...");
        anchorCursorPrefab = Resources.Load<GameObject>("AnchorCursorSphere");

        if (anchorCursorPrefab == null)
        {
            Debug.LogError("[AnchorGUI]  Failed to load AnchorCursor!  Check: Assets/Resources/AnchorCursor.prefab");
        }
        else
        {
            Debug.Log($"[AnchorGUI] Loaded cursor prefab: {anchorCursorPrefab.name}");
        }


        Debug.Log("[AnchorGUI] Loading AnchorMarker from Resources...");
        GameObject anchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");

        if (anchorMarkerPrefab == null)
        {
            Debug.LogError("[AnchorGUI] Failed to load AnchorMarker!  Will use cursor prefab as fallback.");
        }
        else
        {
            Debug.Log($"[AnchorGUI]  Loaded anchor marker prefab: {anchorMarkerPrefab.name}");
            // Store it for later use
            this.anchorMarkerPrefab = anchorMarkerPrefab;
        }

        FindControllers();
        SetupButtonListeners();
        SetupInputFields();
        InitializeRoomDropdown();
        UpdateAllUI();

        if (confirmationDialog != null)
            confirmationDialog.SetActive(false);

        LogStatus("Anchor GUI initialized");
    }
    /// <summary>
    /// Finds controller transforms at runtime
    /// </summary>
    private void FindControllers()
    {
        // Method 1: Try to find by name
        if (rightControllerTransform == null)
        {
            GameObject rightHand = GameObject.Find("RightHandAnchor");
            if (rightHand != null)
            {
                rightControllerTransform = rightHand.transform;
                Debug.Log("[AnchorGUI] Found RightHandAnchor: " + rightControllerTransform.name);
            }
            else
            {
                Debug.LogWarning("[AnchorGUI] RightHandAnchor not found in scene!");
            }
        }

        if (leftControllerTransform == null)
        {
            GameObject leftHand = GameObject.Find("LeftHandAnchor");
            if (leftHand != null)
            {
                leftControllerTransform = leftHand.transform;
                Debug.Log("[AnchorGUI] Found LeftHandAnchor: " + leftControllerTransform.name);
            }
            else
            {
                Debug.LogWarning("[AnchorGUI] LeftHandAnchor not found in scene!");
            }
        }

        // Method 2: Fallback - find OVRCameraRig and get children
        if (rightControllerTransform == null || leftControllerTransform == null)
        {
            OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null)
            {
                Transform trackingSpace = cameraRig.trackingSpace;

                if (trackingSpace != null)
                {
                    if (rightControllerTransform == null)
                    {
                        rightControllerTransform = trackingSpace.Find("RightHandAnchor");
                        if (rightControllerTransform != null)
                            Debug.Log("[AnchorGUI] Found RightHandAnchor via OVRCameraRig");
                    }

                    if (leftControllerTransform == null)
                    {
                        leftControllerTransform = trackingSpace.Find("LeftHandAnchor");
                        if (leftControllerTransform != null)
                            Debug.Log("[AnchorGUI] Found LeftHandAnchor via OVRCameraRig");
                    }
                }
            }
        }

        // Final check
        if (rightControllerTransform == null)
        {
            Debug.LogError("[AnchorGUI] Failed to find RightHandAnchor!");
        }

        if (leftControllerTransform == null)
        {
            Debug.LogError("[AnchorGUI] Failed to find LeftHandAnchor!");
        }
    }
    private void Update()
    {
        UpdateStatusIndicator();
        if (isPlacingAnchor)
        {
            UpdateCursorPositions();
            CheckPlacementButtons();
        }
    }

    private void SetupButtonListeners()
    {
        hostSessionButton?.onClick.AddListener(OnHostSessionClicked);

        joinSessionButton?.onClick.AddListener(OnJoinSessionClicked);

        leaveSessionButton?.onClick.AddListener(OnLeaveSessionClicked);

        createAnchorButton?.onClick.AddListener(OnCreateAnchorClicked);

        saveAnchorButton?.onClick.AddListener(OnSaveAnchorClicked);

        loadAnchorsButton?.onClick.AddListener(OnLoadAnchorsClicked);

        shareAnchorsButton?.onClick.AddListener(OnShareAnchorsClicked);

        clearAnchorsButton?.onClick.AddListener(OnClearAnchorsClicked);

        confirmYesButton?.onClick.AddListener(OnConfirmationYes);

        confirmNoButton?.onClick.AddListener(OnConfirmationNo);
    }

    private void SetupInputFields()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (roomNameInputField != null)
        {
            roomNameInputField.shouldHideMobileInput = false;
            roomNameInputField.shouldHideSoftKeyboard = false;
        }
#endif
    }

    private void OnHostSessionClicked()
    {
#if FUSION2
        string roomName = GetSelectedRoomName();
        
        if (string.IsNullOrEmpty(roomName))
        {
            LogStatus("Please select or enter a room name!", true);
            return;
        }

        Debug.Log("[AnchorGUI] Hosting room: " + roomName);
        LogStatus("Creating Photon room: " + roomName);
        isHost = true;
        StartPhotonHostSession(roomName);
#else
        LogStatus("Photon Fusion not available!", true);
#endif
    }

    private void OnJoinSessionClicked()
    {
#if FUSION2
        string roomName = GetSelectedRoomName();
        
        if (string.IsNullOrEmpty(roomName))
        {
            LogStatus("Please select or enter a room name!", true);
            return;
        }

        Debug.Log("[AnchorGUI] Joining room: " + roomName);
        LogStatus("Joining Photon room: " + roomName);
        isHost = false;
        StartPhotonClientSession(roomName);
#else
        LogStatus("Photon Fusion not available!", true);
#endif
    }


    private async void OnLeaveSessionClicked()
    {
#if FUSION2
    Debug.Log("[AnchorGUI] Leave Session clicked");
    LogStatus("Leaving session...");
    
    try
    {
        if (networkRunner != null && networkRunner.IsRunning)
        {
            Debug.Log("[AnchorGUI] Shutting down NetworkRunner");
            await networkRunner. Shutdown();
            
            if (networkRunner.gameObject != null)
            {
                Destroy(networkRunner.gameObject);
            }
            
            networkRunner = null;
            Debug.Log("[AnchorGUI] NetworkRunner shut down successfully");
        }
        else
        {
            Debug.Log("[AnchorGUI] No active NetworkRunner to shut down");
        }
        
        isHost = false;
        if (isAdvertising)
    {
        Debug.Log("[AnchorGUI] Stopping advertisement...");
        var stopAdvert = await OVRColocationSession.StopAdvertisementAsync();
        if (stopAdvert.Success)
        {
            Debug.Log("[AnchorGUI] Advertisement stopped");
            isAdvertising = false;
        }
    }

    if (isDiscovering)
    {
        Debug.Log("[AnchorGUI] Stopping discovery...");
        var stopDisco = await OVRColocationSession.StopDiscoveryAsync();
        if (stopDisco.Success)
        {
            Debug.Log("[AnchorGUI] Discovery stopped");
            isDiscovering = false;
        }
    
        // Unsubscribe from events
        OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
    }

    currentGroupUuid = Guid.Empty;

        SetSessionState(SessionState.Idle);
        
        LogStatus("Left session");
        UpdateAllUI();
    }
    catch (Exception e)
    {
        Debug.LogError("[AnchorGUI] Error leaving session: " + e);
        LogStatus("Error leaving session: " + e. Message, true);
    }
#else
        LogStatus("Photon Fusion not available!", true);
#endif
    }

#if FUSION2
   private async void StartPhotonHostSession(string roomName)
{
    try
    {
        SetSessionState(SessionState.Hosting);
        
        networkRunner = FindObjectOfType<NetworkRunner>();
        
        if (networkRunner == null)
        {
            Debug.Log("[AnchorGUI] Creating new NetworkRunner");
            var runnerGO = new GameObject("NetworkRunner");
            networkRunner = runnerGO.AddComponent<NetworkRunner>();
            DontDestroyOnLoad(runnerGO);
        }
        else if (networkRunner.IsRunning)
        {
            Debug.Log("[AnchorGUI] Shutting down existing session");
            await networkRunner.Shutdown();
            await System.Threading.Tasks.Task.  Delay(500);
        }

        networkRunner. ProvideInput = true;
        
        Debug.Log("[AnchorGUI] Starting Host for room: " + roomName);
        
        var result = await networkRunner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Host,
            SessionName = roomName,
            SceneManager = networkRunner.GetComponent<INetworkSceneManager>()
        });

        //  CORRECT HOST CODE:
             if (result.Ok)
        {
            Debug.Log("[AnchorGUI] Host started successfully");
            LogStatus("Hosting room: " + roomName);
            SetSessionState(SessionState.Connected);
    
            // Auto-create anchor disabled - use Create Anchor button instead
            // await System.Threading.Tasks.Task. Delay(1000);
            // await CreateHostAnchor();
    
            UpdateAllUI();
        }
        else
        {
            Debug.LogError("[AnchorGUI] Host failed: " + result.ShutdownReason);
            LogStatus("Failed to host: " + result.ShutdownReason, true);
            SetSessionState(SessionState.Idle);
        }
    }
    catch (Exception e)
    {
        Debug.LogError("[AnchorGUI] Host exception: " + e);
        LogStatus("Error: " + e.Message, true);
        SetSessionState(SessionState.Idle);
    }
}

   private async void StartPhotonClientSession(string roomName)
{
    try
    {
        SetSessionState(SessionState.Joining);
        
        networkRunner = FindObjectOfType<NetworkRunner>();
        
        if (networkRunner == null)
        {
            Debug.Log("[AnchorGUI] Creating new NetworkRunner");
            var runnerGO = new GameObject("NetworkRunner");
            networkRunner = runnerGO.AddComponent<NetworkRunner>();
            DontDestroyOnLoad(runnerGO);
        }
        else if (networkRunner.IsRunning)
        {
            Debug.Log("[AnchorGUI] Shutting down existing session");
            await networkRunner.Shutdown();
            await System.Threading.Tasks. Task. Delay(500);
        }

        networkRunner.ProvideInput = true;
        
        Debug.Log("[AnchorGUI] Starting Client for room: " + roomName);
        
        var result = await networkRunner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Client,
            SessionName = roomName,
            SceneManager = networkRunner.GetComponent<INetworkSceneManager>()
        });

        //  CORRECT CLIENT CODE:
        if (result.Ok)
        {
            Debug.Log("[AnchorGUI] Client joined successfully");
            LogStatus("Joined room: " + roomName);
            SetSessionState(SessionState.Connected);
    
            await System.Threading.Tasks.Task.Delay(500);
            
            //  CLIENT DISCOVERS ANCHORS (not creates!)
            await StartDiscoveringGroupUuid();
    
            UpdateAllUI();
        }
        else
        {
            Debug.LogError("[AnchorGUI] Join failed: " + result.ShutdownReason);
            LogStatus("Failed to join: " + result.ShutdownReason, true);
            SetSessionState(SessionState. Idle);
        }
    }
    catch (Exception e)
    {
        Debug.LogError("[AnchorGUI] Join exception: " + e);
        LogStatus("Error: " + e.Message, true);
        SetSessionState(SessionState.Idle);
    }
}

//   private async System.Threading.Tasks.Task CreateHostAnchor()
//{
//    if (! isHost) return;
    
//    Debug.Log("[AnchorGUI] Host creating colocation anchor");
    
//    var anchor = await CreateAnchorAtPosition(Vector3.zero, Quaternion.identity);
    
//    if (anchor != null)
//    {
//        currentAnchors.Add(anchor);
        
//        var saveResult = await anchor.SaveAnchorAsync();
//        if (saveResult.Success)
//        {
//            Debug.Log("[AnchorGUI] Host anchor saved");
            
//            // Generate Group UUID
//            currentGroupUuid = Guid.NewGuid();
            
//            var shareResult = await OVRSpatialAnchor.ShareAsync(
//                new List<OVRSpatialAnchor> { anchor }, 
//                currentGroupUuid
//            );
            
//            if (shareResult.Success)
//            {
//                Debug.Log("[AnchorGUI] Anchor shared with UUID: " + currentGroupUuid);
                
//                // START ADVERTISING THE GROUP UUID
//                await StartAdvertisingGroupUuid();
                
//                LogStatus("Room ready!   UUID: " + currentGroupUuid. ToString(). Substring(0, 10) + "...");
//                UpdateAllUI();
//            }
//            else
//            {
//                Debug.LogError("[AnchorGUI] Failed to share anchor: " + shareResult.Status);
//            }
//        }
//    }
//}
private async System.Threading.Tasks. Task StartAdvertisingGroupUuid()
{
    if (isAdvertising)
    {
        Debug.Log("[AnchorGUI] Already advertising");
        return;
    }
    
    Debug.Log("[AnchorGUI] Starting to advertise Group UUID: " + currentGroupUuid);
    
    // Convert Group UUID to bytes for advertisement
    byte[] uuidBytes = currentGroupUuid. ToByteArray();
    
    try
    {
        // KEY API CALL: Advertise the Group UUID
        var startAdvert = await OVRColocationSession.StartAdvertisementAsync(uuidBytes);
        
        if (startAdvert.Success && startAdvert.TryGetValue(out Guid advertisementUuid))
        {
            Debug.Log($"[AnchorGUI] Started advertising with UUID: {advertisementUuid}");
            isAdvertising = true;
        }
        else
        {
            Debug.LogError($"[AnchorGUI] Failed to start advertising: {startAdvert.Status}");
        }
    }
    catch (Exception e)
    {
        Debug.LogError($"[AnchorGUI] Exception starting advertisement: {e.Message}");
    }
}
private async System.Threading.Tasks.Task StartDiscoveringGroupUuid()
{
    if (isDiscovering)
    {
        Debug.Log("[AnchorGUI] Already discovering");
        return;
    }
    
    Debug.Log("[AnchorGUI] Starting to discover Group UUID.. .");
    
    // Subscribe to discovery events
    OVRColocationSession.ColocationSessionDiscovered += OnColocationSessionDiscovered;
    
    try
    {
        // KEY API CALL: Start discovering sessions
        var startDisco = await OVRColocationSession.StartDiscoveryAsync();
        
        if (startDisco.Success)
        {
            Debug.Log("[AnchorGUI] Started discovering sessions");
            isDiscovering = true;
            LogStatus("Discovering anchor sessions...");
        }
        else
        {
            Debug. LogError($"[AnchorGUI] Failed to start discovery: {startDisco.Status}");
        }
    }
    catch (Exception e)
    {
        Debug.LogError($"[AnchorGUI] Exception starting discovery: {e.Message}");
    }
}

/// <summary>
/// Called when a colocation session is discovered
/// </summary>
private async void OnColocationSessionDiscovered(OVRColocationSession. Data data)
{
    Debug.Log($"[AnchorGUI] Discovered session with UUID: {data. AdvertisementUuid}");
    
    if (data. Metadata != null && data.Metadata.Length == 16)
    {
        try
        {
            Guid discoveredGroupUuid = new Guid(data.Metadata);
            Debug.Log($"[AnchorGUI] Received Group UUID: {discoveredGroupUuid}");
            
            currentGroupUuid = discoveredGroupUuid;
            UpdateAllUI();
            
            // Reduce delay to 200ms (was 500ms)
            LogStatus("Found anchor session!   Loading...");
            await System.Threading.Tasks.Task.Delay(200);
            await LoadSharedAnchors();
        }
        catch (Exception e)
        {
            Debug.LogError($"[AnchorGUI] Failed to parse Group UUID: {e.Message}");
        }
    }
    else
    {
        Debug. LogWarning("[AnchorGUI] Received session without valid metadata");
    }
}
  private async System.Threading.Tasks.Task LoadSharedAnchors()
{
    if (currentGroupUuid == Guid.Empty)
    {
        Debug.LogWarning("[AnchorGUI] No UUID to load from");
        return;
    }
    
    SetSessionState(SessionState.Loading);
    LogStatus("Step 1/3: Requesting anchors from cloud...");
    
    var unboundAnchors = new List<OVRSpatialAnchor. UnboundAnchor>();
    var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(
        currentGroupUuid,
        unboundAnchors
    );

    if (loadResult.Success && unboundAnchors.Count > 0)
    {
        Debug.Log($"[AnchorGUI] Received {unboundAnchors.Count} unbound anchor(s)");
        
        LogStatus($"Step 2/3: Localizing {unboundAnchors.Count} anchor(s)...");
        
        int loadedCount = 0;
        foreach (var unboundAnchor in unboundAnchors)
        {
            Debug.Log($"[AnchorGUI] Localizing anchor: {unboundAnchor. Uuid. ToString(). Substring(0, 8)}");
            
            bool localized = await unboundAnchor.LocalizeAsync();
            if (localized)
            {
                Debug.Log($"[AnchorGUI] Anchor localized, creating GameObject...");
                
                var anchorGO = new GameObject("SharedAnchor_" + unboundAnchor.Uuid. ToString(). Substring(0, 8));
                var spatialAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();
                unboundAnchor.BindTo(spatialAnchor);
                
                // ADD VISUAL MARKER
                GameObject visualPrefab = anchorMarkerPrefab != null ? anchorMarkerPrefab : anchorCursorPrefab;
                if (visualPrefab != null)
                {
                    GameObject visual = Instantiate(visualPrefab, anchorGO.transform);
                    visual.name = "Visual";
                    visual.transform.localPosition = Vector3.zero;
                    visual.transform.localRotation = Quaternion.identity;
                    visual.transform.localScale = Vector3.one * anchorScale;
                    Debug.Log($"[AnchorGUI] Added visual to loaded anchor");
                }
                
                currentAnchors.Add(spatialAnchor);
                loadedCount++;
                
                // Show progress
                LogStatus($"Step 3/3: Loading...  ({loadedCount}/{unboundAnchors.Count})");
                
                Debug.Log("[AnchorGUI] Loaded shared anchor: " + unboundAnchor. Uuid);
            }
            else
            {
                Debug. LogWarning($"[AnchorGUI] Failed to localize anchor: {unboundAnchor. Uuid. ToString().Substring(0, 8)}");
            }
        }
        
        LogStatus($"SUCCESS: Loaded {loadedCount} anchor(s)");
    }
    else
    {
        if (loadResult.Success)
        {
            Debug. LogWarning("[AnchorGUI] Load succeeded but received 0 anchors");
        }
        else
        {
            Debug.LogError($"[AnchorGUI] Load failed: {loadResult.Status}");
        }
        LogStatus("No shared anchors found", true);
    }
    
    SetSessionState(SessionState.Connected);
    UpdateAllUI();
}



    private void OnCreateAnchorClicked()
    {
        if (cameraTransform == null)
        {
            LogStatus("Camera not found!", true);
            return;
        }

        // Only host can create additional anchors after initial colocation anchor
        if (!isHost && currentAnchors.Count == 0)
        {
            LogStatus("Only host can create the first anchor!  Join a session to load anchors.", true);
            return;
        }

        //  Enter placement mode
        EnterAnchorPlacementMode();
    }
    // ============================================================================
    // CONTROLLER-BASED ANCHOR PLACEMENT METHODS
    // ============================================================================

    /// <summary>
    /// Enters placement mode - shows cursors on controllers
    /// </summary>
    private void EnterAnchorPlacementMode()
    {
        Debug.Log("[AnchorGUI] ===== ENTERING PLACEMENT MODE =====");

        // Verify controllers exist
        if (leftControllerTransform == null || rightControllerTransform == null)
        {
            Debug.LogWarning("[AnchorGUI] Controllers NULL, trying to find them...");
            FindControllers();

            if (leftControllerTransform == null || rightControllerTransform == null)
            {
                LogStatus(" Controllers not detected!", true);
                Debug.LogError($"[AnchorGUI] Still NULL!  Left: {leftControllerTransform == null}, Right: {rightControllerTransform == null}");
                return;
            }
        }

        Debug.Log($"[AnchorGUI]  Controllers found - Left: {leftControllerTransform.name}, Right: {rightControllerTransform.name}");

        isPlacingAnchor = true;
        LogStatus("👉 Press A, B, X, or Y button to place anchor");

        // Check cursor prefab
        if (anchorCursorPrefab == null)
        {
            Debug.LogError("[AnchorGUI]  ANCHOR CURSOR PREFAB IS NULL!  Check Inspector assignment!");
            LogStatus("ERROR: Cursor prefab not assigned!", true);
            return;
        }

        Debug.Log($"[AnchorGUI]  Cursor prefab assigned: {anchorCursorPrefab.name}");

        // LEFT CONTROLLER CURSOR
        if (leftControllerTransform != null && leftCursorInstance == null)
        {
            Debug.Log($"[AnchorGUI] Creating LEFT cursor at controller position: {leftControllerTransform.position}");

            leftCursorInstance = Instantiate(anchorCursorPrefab, leftControllerTransform);

            if (leftCursorInstance == null)
            {
                Debug.LogError("[AnchorGUI]  LEFT cursor instantiation FAILED!");
            }
            else
            {
                leftCursorInstance.name = "LeftControllerCursor";
                leftCursorInstance.transform.localPosition = Vector3.forward * cursorOffset;
                leftCursorInstance.transform.localRotation = Quaternion.identity;
                leftCursorInstance.transform.localScale = Vector3.one * cursorScale;
                leftCursorInstance.SetActive(true);

                Debug.Log($"[AnchorGUI]  LEFT cursor created!");
                Debug.Log($"[AnchorGUI]    - World Position: {leftCursorInstance.transform.position}");
                Debug.Log($"[AnchorGUI]    - Local Position: {leftCursorInstance.transform.localPosition}");
                Debug.Log($"[AnchorGUI]    - Scale: {leftCursorInstance.transform.localScale}");
                Debug.Log($"[AnchorGUI]    - Active: {leftCursorInstance.activeSelf}");
                Debug.Log($"[AnchorGUI]    - Parent: {leftCursorInstance.transform.parent.name}");

                // Check if it has a renderer
                Renderer[] renderers = leftCursorInstance.GetComponentsInChildren<Renderer>();
                Debug.Log($"[AnchorGUI]    - Renderers found: {renderers.Length}");
                foreach (var r in renderers)
                {
                    Debug.Log($"[AnchorGUI]       - Renderer: {r.name}, Enabled: {r.enabled}, Material: {r.material.name}");
                }
            }
        }
        else if (leftCursorInstance != null)
        {
            Debug.Log("[AnchorGUI] LEFT cursor already exists, reusing");
            leftCursorInstance.SetActive(true);
        }

        // RIGHT CONTROLLER CURSOR
        if (rightControllerTransform != null && rightCursorInstance == null)
        {
            Debug.Log($"[AnchorGUI] Creating RIGHT cursor at controller position: {rightControllerTransform.position}");

            rightCursorInstance = Instantiate(anchorCursorPrefab, rightControllerTransform);

            if (rightCursorInstance == null)
            {
                Debug.LogError("[AnchorGUI]  RIGHT cursor instantiation FAILED!");
            }
            else
            {
                rightCursorInstance.name = "RightControllerCursor";
                rightCursorInstance.transform.localPosition = Vector3.forward * cursorOffset;
                rightCursorInstance.transform.localRotation = Quaternion.identity;
                rightCursorInstance.transform.localScale = Vector3.one * cursorScale;
                rightCursorInstance.SetActive(true);

                Debug.Log($"[AnchorGUI]  RIGHT cursor created!");
                Debug.Log($"[AnchorGUI]    - World Position: {rightCursorInstance.transform.position}");
                Debug.Log($"[AnchorGUI]    - Local Position: {rightCursorInstance.transform.localPosition}");
                Debug.Log($"[AnchorGUI]    - Scale: {rightCursorInstance.transform.localScale}");
                Debug.Log($"[AnchorGUI]    - Active: {rightCursorInstance.activeSelf}");
                Debug.Log($"[AnchorGUI]    - Parent: {rightCursorInstance.transform.parent.name}");

                // Check if it has a renderer
                Renderer[] renderers = rightCursorInstance.GetComponentsInChildren<Renderer>();
                Debug.Log($"[AnchorGUI]    - Renderers found: {renderers.Length}");
                foreach (var r in renderers)
                {
                    Debug.Log($"[AnchorGUI]       - Renderer: {r.name}, Enabled: {r.enabled}, Material: {r.material.name}");
                }
            }
        }
        else if (rightCursorInstance != null)
        {
            Debug.Log("[AnchorGUI] RIGHT cursor already exists, reusing");
            rightCursorInstance.SetActive(true);
        }

        Debug.Log("[AnchorGUI] ===== PLACEMENT MODE ENTERED =====");
    }

    /// <summary>
    /// Updates cursor visibility (runs every frame when in placement mode)
    /// </summary>
    private void UpdateCursorPositions()
    {
        // Cursors automatically follow controllers since they're parented! 
        // Just make sure they're visible
        if (leftCursorInstance != null)
            leftCursorInstance.SetActive(true);

        if (rightCursorInstance != null)
            rightCursorInstance.SetActive(true);
    }

    /// <summary>
    /// Checks for A/B button presses to confirm placement
    /// </summary>
    private void CheckPlacementButtons()
    {
        // Check A button (right controller)
        bool pressedA = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);

        // Check B button (right controller) 
        bool pressedB = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);

        // Check X button (left controller)
        bool pressedX = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch);

        // Check Y button (left controller)
        bool pressedY = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch);

        if (pressedA || pressedB)
        {
            // Right controller button pressed
            CreateAnchorAtController(rightControllerTransform, OVRInput.Controller.RTouch);
        }
        else if (pressedX || pressedY)
        {
            // Left controller button pressed
            CreateAnchorAtController(leftControllerTransform, OVRInput.Controller.LTouch);
        }

        // Cancel with grip buttons
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger))
        {
            CancelAnchorPlacement();
        }
    }
    /// <summary>
    /// Gets controller position using OVRInput (backup method)
    /// </summary>
    private Vector3 GetControllerPosition(OVRInput.Controller controller)
    {
        // Get local position from OVR
        Vector3 localPos = OVRInput.GetLocalControllerPosition(controller);

        // Convert to world space
        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            return cameraRig.trackingSpace.TransformPoint(localPos);
        }

        return localPos; // Fallback to local
    }

    /// <summary>
    /// Gets controller rotation using OVRInput (backup method)
    /// </summary>
    private Quaternion GetControllerRotation(OVRInput.Controller controller)
    {
        Quaternion localRot = OVRInput.GetLocalControllerRotation(controller);

        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            return cameraRig.trackingSpace.rotation * localRot;
        }

        return localRot;
    }

    /// <summary>
    /// Creates anchor at the specified controller's cursor position
    /// </summary>
    private async void CreateAnchorAtController(Transform controllerTransform, OVRInput.Controller controller)
    {
        LogStatus("Creating anchor.. .");

        // Haptic feedback
        OVRInput.SetControllerVibration(0.5f, 0.5f, controller);

        try
        {
            Vector3 anchorPosition;
            Quaternion anchorRotation;

            // TRY TRANSFORM FIRST
            if (controllerTransform != null)
            {
                anchorPosition = controllerTransform.position + controllerTransform.forward * cursorOffset;
                anchorRotation = controllerTransform.rotation;
            }
            // FALLBACK TO OVRINPUT
            else
            {
                Debug.LogWarning("[AnchorGUI] Using OVRInput fallback for controller position");
                anchorPosition = GetControllerPosition(controller);
                anchorRotation = GetControllerRotation(controller);
                anchorPosition += anchorRotation * Vector3.forward * cursorOffset;
            }

            var anchor = await CreateAnchorAtPosition(anchorPosition, anchorRotation);

            if (anchor != null)
            {
                currentAnchors.Add(anchor);
                LogStatus(" Anchor created!   ({currentAnchors.Count} total)");

                // Success vibration
                OVRInput.SetControllerVibration(1f, 1f, controller);
                await System.Threading.Tasks.Task.Delay(100);
                OVRInput.SetControllerVibration(0, 0, controller);

                UpdateAllUI();
            }
            else
            {
                LogStatus("Failed to create anchor", true);
            }
        }
        catch (Exception e)
        {
            LogStatus("Error: " + e.Message, true);
            Debug.LogError("[AnchorGUI] Exception creating anchor: " + e);
        }
        finally
        {
            ExitAnchorPlacementMode();
        }
    }
    /// <summary>
    /// Cancels anchor placement without creating anchor
    /// </summary>
    private void CancelAnchorPlacement()
    {
        LogStatus("Anchor placement cancelled");
        ExitAnchorPlacementMode();
    }

    /// <summary>
    /// Exits placement mode - hides cursors and resets state
    /// </summary>
    private void ExitAnchorPlacementMode()
    {
        isPlacingAnchor = false;

        // Destroy left cursor
        if (leftCursorInstance != null)
        {
            Destroy(leftCursorInstance);
            leftCursorInstance = null;
        }

        // Destroy right cursor
        if (rightCursorInstance != null)
        {
            Destroy(rightCursorInstance);
            rightCursorInstance = null;
        }

        Debug.Log("[AnchorGUI] Exited anchor placement mode");
    }

    private async void OnSaveAnchorClicked()
    {
        if (currentAnchors.Count == 0)
        {
            LogStatus("No anchors to save!", true);
            return;
        }

        LogStatus("Saving " + currentAnchors.Count + " anchor(s)...");

        try
        {
            int savedCount = 0;
            foreach (var anchor in currentAnchors)
            {
                if (anchor == null) continue;

                var saveResult = await anchor.SaveAnchorAsync();
                if (saveResult.Success)
                {
                    savedCount++;
                    savedAnchors[anchor.Uuid] = true;
                }
            }

            LogStatus("Saved " + savedCount + "/" + currentAnchors.Count + " anchor(s)");
            UpdateAllUI();
        }
        catch (Exception e)
        {
            LogStatus("Error: " + e.Message, true);
        }
    }

    private async void OnLoadAnchorsClicked()
    {
        if (currentGroupUuid == Guid.Empty)
        {
            LogStatus("No Group UUID!  Host or join a session first.", true);
            return;
        }

#if FUSION2
        await LoadSharedAnchors();
#endif
    }

    private async void OnShareAnchorsClicked()
    {
        if (currentAnchors.Count == 0)
        {
            LogStatus("No anchors to share!", true);
            return;
        }

        if (currentGroupUuid == Guid.Empty)
        {
            currentGroupUuid = Guid.NewGuid();
        }

        LogStatus("Sharing " + currentAnchors.Count + " anchor(s)...");
        SetSessionState(SessionState.Sharing);

        try
        {
            var validAnchors = new List<OVRSpatialAnchor>();
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.Localized)
                {
                    validAnchors.Add(anchor);
                }
            }

            if (validAnchors.Count == 0)
            {
                LogStatus("No valid anchors to share!", true);
                SetSessionState(SessionState.Connected);
                return;
            }

            var shareResult = await OVRSpatialAnchor.ShareAsync(validAnchors, currentGroupUuid);

           if (shareResult.Success)
{
    foreach (var anchor in validAnchors)
    {
        sharedAnchors[anchor.Uuid] = true;
    }
    
    // If this is the first share, start advertising the Group UUID
    if (!  isAdvertising)
    {
        await StartAdvertisingGroupUuid();
        Debug.Log("[AnchorGUI] Started advertising Group UUID after share");
    }
    
    LogStatus("Shared " + validAnchors.Count + " anchor(s)!");
}
            else
            {
                LogStatus("Failed to share: " + shareResult.Status, true);
            }

            SetSessionState(SessionState.Connected);
            UpdateAllUI();
        }
        catch (Exception e)
        {
            LogStatus("Error: " + e.Message, true);
            SetSessionState(SessionState.Connected);
        }
    }

    private void OnClearAnchorsClicked()
    {
        if (currentAnchors.Count == 0)
        {
            LogStatus("No anchors to clear!", true);
            return;
        }
        ShowConfirmationDialog(
            "Clear " + currentAnchors.Count + " anchor(s)?",
            () =>
            {
                foreach (var anchor in currentAnchors)
                {
                    if (anchor != null && anchor.gameObject != null)
                    {
                        Destroy(anchor.gameObject);
                    }
                }

                currentAnchors.Clear();

                // ADD: Clear tracking when clearing anchors
                savedAnchors.Clear();
                sharedAnchors.Clear();

                LogStatus("Anchors cleared");
                UpdateAllUI();
            }
        );
    }

    private void UpdateAllUI()
    {
        UpdateConnectionState();
        UpdateGroupUuidDisplay();
        UpdateAnchorCount();
        UpdateButtonStates();
    }

    private void UpdateConnectionState()
    {
        if (connectionStateText == null) return;

        string stateText = "IDLE";

        if (currentState == SessionState.Idle)
            stateText = "IDLE";
        else if (currentState == SessionState.Hosting)
            stateText = "Hosting... ";
        else if (currentState == SessionState.Joining)
            stateText = "Joining... ";
        else if (currentState == SessionState.Connected)
            stateText = isHost ? "HOST (Connected)" : "CLIENT (Connected)";
        else if (currentState == SessionState.Loading)
            stateText = "Loading Anchors...";
        else if (currentState == SessionState.Sharing)
            stateText = "Sharing Anchors...";

        connectionStateText.text = stateText;
    }

    private void UpdateGroupUuidDisplay()
    {
        if (groupUuidText == null) return;

#if FUSION2
        if (networkRunner != null && networkRunner.IsRunning)
        {
            string roomName = networkRunner.SessionInfo.Name;
            string role = networkRunner.IsServer ? "HOST" : "CLIENT";
            groupUuidText.text = "Room: " + roomName + " (" + role + ")";
            
            if (roomNameDisplayText != null)
            {
                roomNameDisplayText.text = "Connected: " + roomName;
            }
            return;
        }
#endif

        if (currentGroupUuid == Guid.Empty)
        {
            groupUuidText.text = "UUID: None";
            if (roomNameDisplayText != null)
                roomNameDisplayText.text = "Not connected";
        }
        else
        {
            groupUuidText.text = "UUID: " + currentGroupUuid.ToString().Substring(0, 13) + "... ";
        }
    }
    private void UpdateAnchorCount()
    {
        if (anchorText == null) return;

        if (currentAnchors.Count == 0)
        {
            anchorText.text = "Anchors: (none)";
            return;
        }

        System.Text.StringBuilder sb = new System.Text.StringBuilder();

        // Header with role and count
        string roleIcon = isHost ? "[HOST]" : "[CLIENT]";

        int localizedCount = 0;
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.Localized)
                localizedCount++;
        }

        sb.AppendLine($"{roleIcon} {currentAnchors.Count} Anchor(s) ({localizedCount} localized)");
        sb.AppendLine("================================");

        int index = 1;
        foreach (var anchor in currentAnchors)
        {
            if (anchor == null) continue;

            sb.AppendLine($"\nAnchor #{index}");

            // Short UUID
            string uuidShort = anchor.Uuid.ToString().Substring(0, 8);
            sb.AppendLine($"  ID: {uuidShort}");

            // Full UUID
            sb.AppendLine($"  Full: {anchor.Uuid}");

            // Status badges (text only)
            List<string> badges = new List<string>();

            if (anchor.Created)
                badges.Add("CREATED");
            else
                badges.Add("CREATING");

            if (anchor.Localized)
                badges.Add("LOCALIZED");
            else
                badges.Add("LOCALIZING");

            if (savedAnchors.ContainsKey(anchor.Uuid) && savedAnchors[anchor.Uuid])
                badges.Add("SAVED");

            if (sharedAnchors.ContainsKey(anchor.Uuid) && sharedAnchors[anchor.Uuid])
                badges.Add("SHARED");

            sb.AppendLine($"  Status: {string.Join(" | ", badges)}");

            // Position
            Vector3 pos = anchor.transform.position;
            sb.AppendLine($"  Pos: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");

            index++;
        }

        // Footer with Group UUID
        if (currentGroupUuid != Guid.Empty)
        {
            sb.AppendLine("\n================================");
            string groupShort = currentGroupUuid.ToString().Substring(0, 13);
            sb.AppendLine($"Group UUID: {groupShort}.. .");
        }

        anchorText.text = sb.ToString();
    }
    private void UpdateButtonStates()
    {
        bool canStartSession = (currentState == SessionState.Idle);
        bool inSession = (currentState == SessionState.Connected || currentState == SessionState.Hosting || currentState == SessionState.Joining);

        if (hostSessionButton != null)
            hostSessionButton.interactable = canStartSession;

        if (joinSessionButton != null)
            joinSessionButton.interactable = canStartSession;

        if (leaveSessionButton != null)
            leaveSessionButton.interactable = inSession;

        // CREATE ANCHOR: Only host can create
        if (createAnchorButton != null)
            createAnchorButton.interactable = isHost && inSession;

        // SAVE ANCHOR: Only host can save (clients just load shared ones)
        if (saveAnchorButton != null)
            saveAnchorButton.interactable = isHost && currentAnchors.Count > 0;

        // SHARE ANCHOR: Only host can share
        if (shareAnchorsButton != null)
            shareAnchorsButton.interactable = isHost && currentAnchors.Count > 0 && inSession;

        // LOAD ANCHOR: Client can load if UUID exists, Host doesn't need it
        if (loadAnchorsButton != null)
            loadAnchorsButton.interactable = currentGroupUuid != Guid.Empty && inSession;

        // CLEAR ANCHOR: Both can clear their local anchors
        if (clearAnchorsButton != null)
            clearAnchorsButton.interactable = currentAnchors.Count > 0;
    }
    private void UpdateStatusIndicator()
    {
        if (statusIndicator == null) return;

        Color indicatorColor = idleColor;

        if (currentState == SessionState.Idle)
            indicatorColor = idleColor;
        else if (currentState == SessionState.Hosting)
            indicatorColor = hostColor;
        else if (currentState == SessionState.Connected)
            indicatorColor = isHost ? hostColor : clientColor;
        else if (currentState == SessionState.Loading)
            indicatorColor = clientColor;
        else if (currentState == SessionState.Sharing)
            indicatorColor = hostColor;

        statusIndicator.color = indicatorColor;
    }

    private void SetSessionState(SessionState newState)
    {
        currentState = newState;
        UpdateAllUI();
    }

    private void LogStatus(string message, bool isError = false)
    {
        if (statusText != null)
        {
            statusText.text = message;
            statusText.color = isError ? Color.red : Color.white;
        }

        if (isError)
        {
            Debug.LogWarning("[AnchorGUI] " + message);
        }
        else
        {
            Debug.Log("[AnchorGUI] " + message);
        }
    }

    private void ShowConfirmationDialog(string message, Action onConfirm)
    {
        if (confirmationDialog == null)
        {
            onConfirm?.Invoke();
            return;
        }

        confirmationDialog.SetActive(true);
        if (confirmationText != null)
            confirmationText.text = message;
        pendingConfirmationAction = onConfirm;
    }

    private void OnConfirmationYes()
    {
        if (confirmationDialog != null)
            confirmationDialog.SetActive(false);
        pendingConfirmationAction?.Invoke();
        pendingConfirmationAction = null;
    }

    private void OnConfirmationNo()
    {
        if (confirmationDialog != null)
            confirmationDialog.SetActive(false);
        pendingConfirmationAction = null;
    }

    private async System.Threading.Tasks.Task<OVRSpatialAnchor> CreateAnchorAtPosition(Vector3 position, Quaternion rotation)
    {
        try
        {
            // Create anchor GameObject
            var anchorGameObject = new GameObject("Anchor_" + DateTime.Now.ToString("HHmmss"));
            anchorGameObject.transform.position = position;
            anchorGameObject.transform.rotation = rotation;

            // Add visual using cursor prefab
            // Add visual using ANCHOR MARKER prefab (not cursor!)
            GameObject visualPrefab = anchorMarkerPrefab != null ? anchorMarkerPrefab : anchorCursorPrefab;

            if (visualPrefab != null)
            {
                GameObject visual = Instantiate(visualPrefab, anchorGameObject.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * anchorScale;

                Debug.Log($"[AnchorGUI]  Added visual marker to anchor using: {visualPrefab.name}");
            }
            else
            {
                Debug.LogWarning("[AnchorGUI]  No visual prefab available for anchor!");
            }

            // Add spatial anchor component
            var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();

            // Wait for anchor creation
            int timeout = 100;
            while (!spatialAnchor.Created && timeout > 0)
            {
                await System.Threading.Tasks.Task.Yield();
                timeout--;
            }

            // Check if successful
            if (!spatialAnchor.Created)
            {
                Debug.LogError("[AnchorGUI] Anchor creation timed out");
                Destroy(anchorGameObject);
                return null;
            }

            Debug.Log("[AnchorGUI] Anchor created: " + spatialAnchor.Uuid);
            return spatialAnchor;
        }
        catch (Exception e)
        {
            Debug.LogError("[AnchorGUI] Anchor creation error: " + e.Message);
            return null;
        }
    }

    private void InitializeRoomDropdown()
    {
        if (roomNameDropdown == null)
        {
            Debug.LogWarning("[AnchorGUI] Room name dropdown not assigned!");
            return;
        }

        roomNameDropdown.ClearOptions();
        roomNameDropdown.AddOptions(new List<string>(roomNameOptions));
        roomNameDropdown.value = 0;
        roomNameDropdown.RefreshShownValue();
        roomNameDropdown.onValueChanged.AddListener(OnRoomNameDropdownChanged);

        if (roomNameInputField != null)
        {
            roomNameInputField.gameObject.SetActive(false);
        }

        Debug.Log("[AnchorGUI] Room dropdown initialized with " + roomNameOptions.Length + " options");
    }

    private void OnRoomNameDropdownChanged(int index)
    {
        if (roomNameInputField == null) return;

        if (index == CUSTOM_ROOM_INDEX)
        {
            roomNameInputField.gameObject.SetActive(true);
            roomNameInputField.text = "";
            Debug.Log("[AnchorGUI] Custom room name selected - input field shown");
        }
        else
        {
            roomNameInputField.gameObject.SetActive(false);
            Debug.Log("[AnchorGUI] Preset room selected: " + roomNameOptions[index]);
        }
    }

    private string GetSelectedRoomName()
    {
        if (roomNameDropdown == null)
        {
            Debug.LogWarning("[AnchorGUI] Dropdown not assigned!");
            return "";
        }

        int selectedIndex = roomNameDropdown.value;

        if (selectedIndex == CUSTOM_ROOM_INDEX)
        {
            if (roomNameInputField != null)
            {
                string customName = roomNameInputField.text.Trim();
                Debug.Log("[AnchorGUI] Using custom room name: " + customName);
                return customName;
            }
            else
            {
                Debug.LogWarning("[AnchorGUI] Input field not assigned!");
                return "";
            }
        }
        else if (selectedIndex >= 0 && selectedIndex < roomNameOptions.Length)
        {
            string presetName = roomNameOptions[selectedIndex];
            Debug.Log("[AnchorGUI] Using preset room name: " + presetName);
            return presetName;
        }

        return "";
    }
}
#endif