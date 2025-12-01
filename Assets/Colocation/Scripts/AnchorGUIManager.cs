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
    private GameObject anchorCursorPrefab;
    private GameObject anchorMarkerPrefab;
    [SerializeField] private Transform rightControllerTransform;
    [SerializeField] private Transform leftControllerTransform;
    [SerializeField] private float cursorOffset = 0.1f;
    [SerializeField] private float cursorScale = 0.05f;
    [SerializeField] private float anchorScale = 1.0f;

    [Header("Debug Settings")]
    [SerializeField] private bool enableVerboseLogging = false;

    private List<OVRSpatialAnchor> currentAnchors;
    private bool isHost;
    private Guid currentGroupUuid;
    private Transform cameraTransform;
    private Action pendingConfirmationAction;

    private bool isPlacingAnchor = false;
    private GameObject leftCursorInstance;
    private GameObject rightCursorInstance;

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

        anchorCursorPrefab = Resources.Load<GameObject>("AnchorCursorSphere");
        if (anchorCursorPrefab == null)
        {
            Debug.LogError("[AnchorGUI] Failed to load AnchorCursor from Resources");
        }

        anchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");
        if (anchorMarkerPrefab == null)
        {
            Debug.LogWarning("[AnchorGUI] Failed to load AnchorMarker, using cursor as fallback");
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

    private void FindControllers()
    {
        if (rightControllerTransform == null)
        {
            GameObject rightHand = GameObject.Find("RightHandAnchor");
            if (rightHand != null)
                rightControllerTransform = rightHand.transform;
        }

        if (leftControllerTransform == null)
        {
            GameObject leftHand = GameObject.Find("LeftHandAnchor");
            if (leftHand != null)
                leftControllerTransform = leftHand.transform;
        }

        if (rightControllerTransform == null || leftControllerTransform == null)
        {
            OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null && cameraRig.trackingSpace != null)
            {
                if (rightControllerTransform == null)
                    rightControllerTransform = cameraRig.trackingSpace.Find("RightHandAnchor");

                if (leftControllerTransform == null)
                    leftControllerTransform = cameraRig.trackingSpace.Find("LeftHandAnchor");
            }
        }

        if (rightControllerTransform == null)
            Debug.LogError("[AnchorGUI] Failed to find RightHandAnchor");

        if (leftControllerTransform == null)
            Debug.LogError("[AnchorGUI] Failed to find LeftHandAnchor");
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
            roomNameInputField. shouldHideMobileInput = false;
            roomNameInputField. shouldHideSoftKeyboard = false;
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
        
        if (string. IsNullOrEmpty(roomName))
        {
            LogStatus("Please select or enter a room name!", true);
            return;
        }

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
        LogStatus("Leaving session.. .");
        
        try
        {
            if (networkRunner != null && networkRunner.IsRunning)
            {
                await networkRunner.Shutdown();
                
                if (networkRunner.gameObject != null)
                    Destroy(networkRunner.gameObject);
                
                networkRunner = null;
            }
            
            isHost = false;
            
            if (isAdvertising)
            {
                var stopAdvert = await OVRColocationSession.StopAdvertisementAsync();
                if (stopAdvert. Success)
                    isAdvertising = false;
            }

            if (isDiscovering)
            {
                var stopDisco = await OVRColocationSession.StopDiscoveryAsync();
                if (stopDisco. Success)
                    isDiscovering = false;
                
                OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
            }

            currentGroupUuid = Guid.Empty;

            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.gameObject != null)
                    Destroy(anchor.gameObject);
            }
            currentAnchors.Clear();

            SetSessionState(SessionState.Idle);
            LogStatus("Left session");
            UpdateAllUI();
        }
        catch (Exception e)
        {
            Debug.LogError("[AnchorGUI] Error leaving session: " + e);
            LogStatus("Error leaving session: " + e.Message, true);
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
                var runnerGO = new GameObject("NetworkRunner");
                networkRunner = runnerGO.AddComponent<NetworkRunner>();
                DontDestroyOnLoad(runnerGO);
            }
            else if (networkRunner.IsRunning)
            {
                await networkRunner.Shutdown();
                await System.Threading.Tasks.Task. Delay(500);
            }

            networkRunner. ProvideInput = true;
            
            var result = await networkRunner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Host,
                SessionName = roomName,
                SceneManager = networkRunner.GetComponent<INetworkSceneManager>()
            });

            if (result.Ok)
            {
                Debug.Log("[AnchorGUI] Host started successfully");
                LogStatus("Hosting room: " + roomName);
                SetSessionState(SessionState.Connected);
                
                await LoadLocalSavedAnchors();
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
                var runnerGO = new GameObject("NetworkRunner");
                networkRunner = runnerGO.AddComponent<NetworkRunner>();
                DontDestroyOnLoad(runnerGO);
            }
            else if (networkRunner.IsRunning)
            {
                await networkRunner. Shutdown();
                await System. Threading.Tasks.Task.Delay(500);
            }

            networkRunner.ProvideInput = true;
            
            var result = await networkRunner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Client,
                SessionName = roomName,
                SceneManager = networkRunner.GetComponent<INetworkSceneManager>()
            });

            if (result.Ok)
            {
                Debug.Log("[AnchorGUI] Client joined successfully");
                LogStatus("Joined room: " + roomName);
                SetSessionState(SessionState. Connected);
        
                await System.Threading.Tasks.Task. Delay(500);
                await StartDiscoveringGroupUuid();
        
                UpdateAllUI();
            }
            else
            {
                Debug.LogError("[AnchorGUI] Join failed: " + result.ShutdownReason);
                LogStatus("Failed to join: " + result.ShutdownReason, true);
                SetSessionState(SessionState.Idle);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[AnchorGUI] Join exception: " + e);
            LogStatus("Error: " + e.Message, true);
            SetSessionState(SessionState.Idle);
        }
    }

    private async System.Threading.Tasks.Task StartAdvertisingGroupUuid()
    {
        if (isAdvertising)
            return;
        
        byte[] uuidBytes = currentGroupUuid.ToByteArray();
        
        try
        {
            var startAdvert = await OVRColocationSession.StartAdvertisementAsync(uuidBytes);
            
            if (startAdvert.Success && startAdvert.TryGetValue(out Guid advertisementUuid))
            {
                Debug.Log($"[AnchorGUI] Started advertising");
                isAdvertising = true;
            }
            else
            {
                Debug.LogError($"[AnchorGUI] Failed to start advertising: {startAdvert. Status}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[AnchorGUI] Exception starting advertisement: {e. Message}");
        }
    }

    private async System.Threading.Tasks.Task StartDiscoveringGroupUuid()
    {
        if (isDiscovering)
            return;
        
        OVRColocationSession.ColocationSessionDiscovered += OnColocationSessionDiscovered;
        
        try
        {
            var startDisco = await OVRColocationSession.StartDiscoveryAsync();
            
            if (startDisco.Success)
            {
                Debug.Log("[AnchorGUI] Started discovering");
                isDiscovering = true;
                LogStatus("Discovering anchor sessions.. .");
            }
            else
            {
                Debug.LogError($"[AnchorGUI] Failed to start discovery: {startDisco.Status}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[AnchorGUI] Exception starting discovery: {e.Message}");
        }
    }

    private async void OnColocationSessionDiscovered(OVRColocationSession.Data data)
    {
        if (data. Metadata != null && data.Metadata.Length == 16)
        {
            try
            {
                Guid discoveredGroupUuid = new Guid(data.Metadata);
                Debug.Log($"[AnchorGUI] Received Group UUID");
                
                currentGroupUuid = discoveredGroupUuid;
                UpdateAllUI();
                
                LogStatus("Found anchor session!  Loading...");
                await System.Threading.Tasks.Task. Delay(200);
                await LoadSharedAnchors();
            }
            catch (Exception e)
            {
                Debug.LogError($"[AnchorGUI] Failed to parse Group UUID: {e.Message}");
            }
        }
    }

    private async System.Threading.Tasks.Task LoadSharedAnchors()
    {
        if (currentGroupUuid == Guid.Empty)
            return;
        
        SetSessionState(SessionState.Loading);
        LogStatus("Requesting anchors from cloud...");
        
        var unboundAnchors = new List<OVRSpatialAnchor. UnboundAnchor>();
        var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(
            currentGroupUuid,
            unboundAnchors
        );

        if (loadResult.Success && unboundAnchors.Count > 0)
        {
            LogStatus($"Localizing {unboundAnchors. Count} anchor(s)...");
            
            int loadedCount = 0;
            foreach (var unboundAnchor in unboundAnchors)
            {
                bool localized = await unboundAnchor.LocalizeAsync();
                if (localized)
                {
                    var anchorGO = new GameObject("SharedAnchor_" + unboundAnchor.Uuid. ToString(). Substring(0, 8));
                    var spatialAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor. BindTo(spatialAnchor);
                    
                    GameObject visualPrefab = anchorMarkerPrefab != null ?  anchorMarkerPrefab : anchorCursorPrefab;
                    if (visualPrefab != null)
                    {
                        GameObject visual = Instantiate(visualPrefab, anchorGO.transform);
                        visual.name = "Visual";
                        visual.transform.localPosition = Vector3.zero;
                        visual.transform.localRotation = Quaternion.identity;
                        visual.transform.localScale = Vector3.one * anchorScale;
                    }
                    
                    currentAnchors.Add(spatialAnchor);
                    loadedCount++;
                    
                    LogStatus($"Loading...  ({loadedCount}/{unboundAnchors.Count})");
                }
            }
            
            LogStatus($"Loaded {loadedCount} anchor(s)");
        }
        else
        {
            LogStatus("No shared anchors found", true);
        }
        
        SetSessionState(SessionState.Connected);
        UpdateAllUI();
    }

private async System.Threading.Tasks.Task LoadLocalSavedAnchors()
{
    Debug.Log("[AnchorGUI] ===== LOAD LOCAL ANCHORS DEBUG START =====");
    LogStatus("Loading saved anchors...");
    SetSessionState(SessionState.Loading);
    
    try
    {
        var unboundAnchors = new List<OVRSpatialAnchor. UnboundAnchor>();
        
        Debug.Log("[AnchorGUI] Calling LoadUnboundAnchorsAsync with empty UUID list (load all)");
        var queryResult = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(new List<Guid>(), unboundAnchors);
        
        Debug.Log($"[AnchorGUI] Load query result:");
        Debug.Log($"[AnchorGUI]   - Success: {queryResult.Success}");
        Debug.Log($"[AnchorGUI]   - Status: {queryResult.Status}");
        Debug.Log($"[AnchorGUI]   - Anchors found: {unboundAnchors. Count}");
        
        if (! queryResult.Success)
        {
            Debug.LogError($"[AnchorGUI] Load query failed with status: {queryResult.Status}");
            LogStatus($"Load failed: {queryResult.Status}", true);
            SetSessionState(SessionState.Connected);
            return;
        }
        
        if (unboundAnchors.Count == 0)
        {
            Debug.LogWarning("[AnchorGUI] No saved anchors found on device");
            LogStatus("No saved anchors found");
            SetSessionState(SessionState.Connected);
            return;
        }
        
        Debug. Log($"[AnchorGUI] Found {unboundAnchors.Count} saved anchor(s), attempting to localize...");
        
        int loadedCount = 0;
        int failedCount = 0;
        
        foreach (var unboundAnchor in unboundAnchors)
        {
            Debug.Log($"[AnchorGUI] Processing anchor {unboundAnchor. Uuid}");
            Debug.Log($"[AnchorGUI]   - Localized: {unboundAnchor.Localized}");
            
            bool localized = await unboundAnchor.LocalizeAsync();
            
            Debug.Log($"[AnchorGUI]   - Localize result: {localized}");
            
            if (localized)
            {
                var anchorGO = new GameObject("SavedAnchor_" + unboundAnchor. Uuid. ToString(). Substring(0, 8));
                var spatialAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();
                unboundAnchor. BindTo(spatialAnchor);
                
                GameObject visualPrefab = anchorMarkerPrefab != null ? anchorMarkerPrefab : anchorCursorPrefab;
                if (visualPrefab != null)
                {
                    GameObject visual = Instantiate(visualPrefab, anchorGO. transform);
                    visual.name = "Visual";
                    visual.transform.localPosition = Vector3.zero;
                    visual. transform.localRotation = Quaternion.identity;
                    visual.transform.localScale = Vector3.one * anchorScale;
                }
                
                currentAnchors. Add(spatialAnchor);
                savedAnchors[spatialAnchor.Uuid] = true;
                loadedCount++;
                
                Debug.Log($"[AnchorGUI] SUCCESS: Loaded anchor {unboundAnchor. Uuid}");
            }
            else
            {
                failedCount++;
                Debug.LogWarning($"[AnchorGUI] FAILED to localize anchor {unboundAnchor.Uuid}");
            }
        }
        
        Debug.Log("[AnchorGUI] ===== LOAD LOCAL ANCHORS DEBUG END =====");
        Debug.Log($"[AnchorGUI] Loaded: {loadedCount}, Failed: {failedCount}");
        
        LogStatus($"Loaded {loadedCount} saved anchor(s)");
        SetSessionState(SessionState.Connected);
        UpdateAllUI();
    }
    catch (Exception e)
    {
        Debug.LogError($"[AnchorGUI] Exception loading saved anchors: {e}");
        Debug.LogError($"[AnchorGUI] Stack trace: {e.StackTrace}");
        LogStatus("Error loading saved anchors", true);
        SetSessionState(SessionState.Connected);
    }
}



#endif

    private void OnCreateAnchorClicked()
    {
        if (cameraTransform == null)
        {
            LogStatus("Camera not found!", true);
            return;
        }

        if (!isHost && currentAnchors.Count == 0)
        {
            LogStatus("Only host can create the first anchor!", true);
            return;
        }

        EnterAnchorPlacementMode();
    }

    private void EnterAnchorPlacementMode()
    {
        if (leftControllerTransform == null || rightControllerTransform == null)
        {
            FindControllers();

            if (leftControllerTransform == null || rightControllerTransform == null)
            {
                LogStatus("Controllers not detected!", true);
                return;
            }
        }

        isPlacingAnchor = true;
        LogStatus("Press A, B, X, or Y to place anchor");

        if (anchorCursorPrefab == null)
        {
            Debug.LogError("[AnchorGUI] Cursor prefab is NULL");
            LogStatus("ERROR: Cursor prefab not assigned!", true);
            return;
        }

        if (leftControllerTransform != null && leftCursorInstance == null)
        {
            leftCursorInstance = Instantiate(anchorCursorPrefab, leftControllerTransform);
            leftCursorInstance.name = "LeftControllerCursor";
            leftCursorInstance.transform.localPosition = Vector3.forward * cursorOffset;
            leftCursorInstance.transform.localRotation = Quaternion.identity;
            leftCursorInstance.transform.localScale = Vector3.one * cursorScale;
        }
        else if (leftCursorInstance != null)
        {
            leftCursorInstance.SetActive(true);
        }

        if (rightControllerTransform != null && rightCursorInstance == null)
        {
            rightCursorInstance = Instantiate(anchorCursorPrefab, rightControllerTransform);
            rightCursorInstance.name = "RightControllerCursor";
            rightCursorInstance.transform.localPosition = Vector3.forward * cursorOffset;
            rightCursorInstance.transform.localRotation = Quaternion.identity;
            rightCursorInstance.transform.localScale = Vector3.one * cursorScale;
        }
        else if (rightCursorInstance != null)
        {
            rightCursorInstance.SetActive(true);
        }
    }

    private void UpdateCursorPositions()
    {
        if (leftCursorInstance != null)
            leftCursorInstance.SetActive(true);

        if (rightCursorInstance != null)
            rightCursorInstance.SetActive(true);
    }

    private void CheckPlacementButtons()
    {
        bool pressedA = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool pressedB = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool pressedX = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch);
        bool pressedY = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch);

        if (pressedA || pressedB)
        {
            CreateAnchorAtController(rightControllerTransform, OVRInput.Controller.RTouch);
        }
        else if (pressedX || pressedY)
        {
            CreateAnchorAtController(leftControllerTransform, OVRInput.Controller.LTouch);
        }

        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger))
        {
            CancelAnchorPlacement();
        }
    }

    private Vector3 GetControllerPosition(OVRInput.Controller controller)
    {
        Vector3 localPos = OVRInput.GetLocalControllerPosition(controller);

        OVRCameraRig cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            return cameraRig.trackingSpace.TransformPoint(localPos);
        }

        return localPos;
    }

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

    private async void CreateAnchorAtController(Transform controllerTransform, OVRInput.Controller controller)
    {
        LogStatus("Creating anchor.. .");
        OVRInput.SetControllerVibration(0.5f, 0.5f, controller);

        try
        {
            Vector3 anchorPosition;
            Quaternion anchorRotation;

            if (controllerTransform != null)
            {
                anchorPosition = controllerTransform.position + controllerTransform.forward * cursorOffset;
                anchorRotation = controllerTransform.rotation;
            }
            else
            {
                anchorPosition = GetControllerPosition(controller);
                anchorRotation = GetControllerRotation(controller);
                anchorPosition += anchorRotation * Vector3.forward * cursorOffset;
            }

            var anchor = await CreateAnchorAtPosition(anchorPosition, anchorRotation);

            if (anchor != null)
            {
                currentAnchors.Add(anchor);
                LogStatus($"Anchor created!  ({currentAnchors.Count} total)");

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

    private void CancelAnchorPlacement()
    {
        LogStatus("Anchor placement cancelled");
        ExitAnchorPlacementMode();
    }

    private void ExitAnchorPlacementMode()
    {
        isPlacingAnchor = false;

        if (leftCursorInstance != null)
        {
            Destroy(leftCursorInstance);
            leftCursorInstance = null;
        }

        if (rightCursorInstance != null)
        {
            Destroy(rightCursorInstance);
            rightCursorInstance = null;
        }
    }

    private async void OnSaveAnchorClicked()
    {
        if (currentAnchors.Count == 0)
        {
            LogStatus("No anchors to save!", true);
            return;
        }

        LogStatus("Saving " + currentAnchors.Count + " anchor(s)...");
        Debug.Log($"[AnchorGUI] ===== SAVE ANCHOR DEBUG START =====");
        Debug.Log($"[AnchorGUI] Total anchors to save: {currentAnchors.Count}");

        try
        {
            int savedCount = 0;
            int failedCount = 0;

            foreach (var anchor in currentAnchors)
            {
                if (anchor == null)
                {
                    Debug.LogWarning("[AnchorGUI] Skipping null anchor");
                    continue;
                }

                Debug.Log($"[AnchorGUI] Attempting to save anchor {anchor.Uuid}");
                Debug.Log($"[AnchorGUI]   - Created: {anchor.Created}");
                Debug.Log($"[AnchorGUI]   - Localized: {anchor.Localized}");
                Debug.Log($"[AnchorGUI]   - Pending: {anchor.PendingCreation}");

                if (!anchor.Created)
                {
                    Debug.LogError($"[AnchorGUI] Anchor {anchor.Uuid} not created yet, cannot save!");
                    failedCount++;
                    continue;
                }

                var saveResult = await anchor.SaveAnchorAsync();

                Debug.Log($"[AnchorGUI] Save result for {anchor.Uuid}:");
                Debug.Log($"[AnchorGUI]   - Success: {saveResult.Success}");
                Debug.Log($"[AnchorGUI]   - Status: {saveResult.Status}");

                if (saveResult.Success)
                {
                    savedCount++;
                    savedAnchors[anchor.Uuid] = true;
                    Debug.Log($"[AnchorGUI] SUCCESS: Anchor {anchor.Uuid} saved to device");
                }
                else
                {
                    failedCount++;
                    Debug.LogError($"[AnchorGUI] FAILED: Anchor {anchor.Uuid} - Status: {saveResult.Status}");
                }
            }

            Debug.Log($"[AnchorGUI] ===== SAVE ANCHOR DEBUG END =====");
            Debug.Log($"[AnchorGUI] Saved: {savedCount}, Failed: {failedCount}");

            LogStatus($"Saved {savedCount}/{currentAnchors.Count} anchor(s) (Failed: {failedCount})");
            UpdateAllUI();
        }
        catch (Exception e)
        {
            Debug.LogError($"[AnchorGUI] Exception during save: {e}");
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

                if (!isAdvertising)
                {
                    await StartAdvertisingGroupUuid();
                    Debug.Log("[AnchorGUI] Started advertising after share");
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
            "Clear " + currentAnchors.Count + " anchor(s)? ",
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
            stateText = "Joining...";
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
            groupUuidText.text = "UUID: " + currentGroupUuid.ToString().Substring(0, 13) + "...";
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

            string uuidShort = anchor.Uuid.ToString().Substring(0, 8);
            sb.AppendLine($"  ID: {uuidShort}");
            sb.AppendLine($"  Full: {anchor.Uuid}");

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

            Vector3 pos = anchor.transform.position;
            sb.AppendLine($"  Pos: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");

            index++;
        }

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

        if (createAnchorButton != null)
            createAnchorButton.interactable = isHost && inSession;

        if (saveAnchorButton != null)
            saveAnchorButton.interactable = isHost && currentAnchors.Count > 0;

        if (shareAnchorsButton != null)
            shareAnchorsButton.interactable = isHost && currentAnchors.Count > 0 && inSession;

        if (loadAnchorsButton != null)
            loadAnchorsButton.interactable = currentGroupUuid != Guid.Empty && inSession;

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
        else if (enableVerboseLogging)
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
            var anchorGameObject = new GameObject("Anchor_" + DateTime.Now.ToString("HHmmss"));
            anchorGameObject.transform.position = position;
            anchorGameObject.transform.rotation = rotation;

            GameObject visualPrefab = anchorMarkerPrefab != null ? anchorMarkerPrefab : anchorCursorPrefab;

            if (visualPrefab != null)
            {
                GameObject visual = Instantiate(visualPrefab, anchorGameObject.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * anchorScale;
            }

            var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();

            int timeout = 100;
            while (!spatialAnchor.Created && timeout > 0)
            {
                await System.Threading.Tasks.Task.Yield();
                timeout--;
            }

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
            return;

        roomNameDropdown.ClearOptions();
        roomNameDropdown.AddOptions(new List<string>(roomNameOptions));
        roomNameDropdown.value = 0;
        roomNameDropdown.RefreshShownValue();
        roomNameDropdown.onValueChanged.AddListener(OnRoomNameDropdownChanged);

        if (roomNameInputField != null)
        {
            roomNameInputField.gameObject.SetActive(false);
        }
    }

    private void OnRoomNameDropdownChanged(int index)
    {
        if (roomNameInputField == null) return;

        if (index == CUSTOM_ROOM_INDEX)
        {
            roomNameInputField.gameObject.SetActive(true);
            roomNameInputField.text = "";
        }
        else
        {
            roomNameInputField.gameObject.SetActive(false);
        }
    }

    private string GetSelectedRoomName()
    {
        if (roomNameDropdown == null)
            return "";

        int selectedIndex = roomNameDropdown.value;

        if (selectedIndex == CUSTOM_ROOM_INDEX)
        {
            if (roomNameInputField != null)
            {
                return roomNameInputField.text.Trim();
            }
            return "";
        }
        else if (selectedIndex >= 0 && selectedIndex < roomNameOptions.Length)
        {
            return roomNameOptions[selectedIndex];
        }

        return "";
    }
}