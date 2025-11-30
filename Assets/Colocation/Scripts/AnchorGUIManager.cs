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
    [SerializeField] private TextMeshProUGUI anchorCountText;
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
    [SerializeField] private Transform rightControllerTransform; // Right controller reference
    [SerializeField] private Transform leftControllerTransform; // Left controller reference
    [SerializeField] private float cursorOffset = 0.1f; // Distance from controller tip
    [SerializeField] private float cursorScale = 0.05f;
    [SerializeField] private float anchorScale = 0.1f;

    private List<OVRSpatialAnchor> currentAnchors;
    private bool isHost;
    private Guid currentGroupUuid;
    private Transform cameraTransform;
    private Action pendingConfirmationAction;

    private bool isPlacingAnchor = false; // Are we in placement mode?
    private GameObject leftCursorInstance; // Left controller cursor
    private GameObject rightCursorInstance; // Right controller cursor
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

    private const int CUSTOM_ROOM_INDEX = 6;

#if FUSION2
    private NetworkRunner networkRunner;
#endif

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
            Debug.LogError("[AnchorGUI] ❌ Failed to load AnchorCursor!  Check: Assets/Resources/AnchorCursor.prefab");
        }
        else
        {
            Debug.Log($"[AnchorGUI] ✅ Loaded cursor prefab: {anchorCursorPrefab.name}");
        }


        Debug.Log("[AnchorGUI] Loading AnchorMarker from Resources...");
        GameObject anchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");

        if (anchorMarkerPrefab == null)
        {
            Debug.LogError("[AnchorGUI] ❌ Failed to load AnchorMarker!  Will use cursor prefab as fallback.");
        }
        else
        {
            Debug.Log($"[AnchorGUI] ✅ Loaded anchor marker prefab: {anchorMarkerPrefab.name}");
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

#if FUSION2

    private async void OnLeaveSessionClicked()
    {
        Debug.Log("[AnchorGUI] Leave Session clicked");
        LogStatus("Leaving session.. .");
        
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
            SetSessionState(SessionState.Idle);
            
            LogStatus("Left session");
            UpdateAllUI();
        }
        catch (Exception e)
        {
            Debug.LogError("[AnchorGUI] Error leaving session: " + e);
            LogStatus("Error leaving session: " + e. Message, true);
        }
    }

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
                await networkRunner. Shutdown();
                await System.Threading.Tasks.Task. Delay(500);
            }

            networkRunner. ProvideInput = true;
            
            Debug.Log("[AnchorGUI] Starting Host for room: " + roomName);
            
            var result = await networkRunner.StartGame(new StartGameArgs
            {
                GameMode = GameMode.Host,
                SessionName = roomName,
                SceneManager = networkRunner.GetComponent<INetworkSceneManager>()
            });

            if (result.Ok)
            {
                Debug. Log("[AnchorGUI] Host started successfully");
                LogStatus("Hosting room: " + roomName);
                SetSessionState(SessionState.Connected);
                
                await System.Threading.Tasks.Task. Delay(1000);
                await CreateHostAnchor();
                
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
                await networkRunner. Shutdown();
                await System.Threading.Tasks.Task.Delay(500);
            }

            networkRunner.ProvideInput = true;
            
            Debug.Log("[AnchorGUI] Starting Client for room: " + roomName);
            
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
                
                await System.Threading.Tasks.Task. Delay(2000);
                
                if (currentGroupUuid != Guid.Empty)
                {
                    await LoadSharedAnchors();
                }
                
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

    private async System.Threading.Tasks.Task CreateHostAnchor()
    {
        if (! isHost) return;
        
        Debug.Log("[AnchorGUI] Host creating colocation anchor");
        
        var anchor = await CreateAnchorAtPosition(Vector3.zero, Quaternion.identity);
        
        if (anchor != null)
        {
            currentAnchors.Add(anchor);
            
            var saveResult = await anchor.SaveAnchorAsync();
            if (saveResult.Success)
            {
                Debug. Log("[AnchorGUI] Host anchor saved");
                
                currentGroupUuid = Guid.NewGuid();
                
                var shareResult = await OVRSpatialAnchor.ShareAsync(
                    new List<OVRSpatialAnchor> { anchor }, 
                    currentGroupUuid
                );
                
                if (shareResult.Success)
                {
                    Debug.Log("[AnchorGUI] Anchor shared with UUID: " + currentGroupUuid);
                    LogStatus("Room ready!  UUID: " + currentGroupUuid. ToString(). Substring(0, 13) + "...");
                    UpdateAllUI();
                }
                else
                {
                    Debug.LogError("[AnchorGUI] Failed to share anchor: " + shareResult.Status);
                }
            }
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
        LogStatus("Loading shared anchors...");
        
        var unboundAnchors = new List<OVRSpatialAnchor. UnboundAnchor>();
        var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(
            currentGroupUuid,
            unboundAnchors
        );

        if (loadResult.Success && unboundAnchors.Count > 0)
        {
            foreach (var unboundAnchor in unboundAnchors)
            {
                bool localized = await unboundAnchor.LocalizeAsync();
                if (localized)
                {
                    var anchorGO = new GameObject("SharedAnchor_" + unboundAnchor. Uuid. ToString(). Substring(0, 8));
                    var spatialAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor. BindTo(spatialAnchor);
                    currentAnchors.Add(spatialAnchor);
                    
                    Debug.Log("[AnchorGUI] Loaded shared anchor: " + unboundAnchor.Uuid);
                }
            }
            
            LogStatus("Loaded " + unboundAnchors.Count + " anchor(s)");
        }
        else
        {
            LogStatus("No shared anchors found", true);
        }
        
        SetSessionState(SessionState.Connected);
        UpdateAllUI();
    }

#endif



    private void OnCreateAnchorClicked()
    {
        if (cameraTransform == null)
        {
            LogStatus("Camera not found!", true);
            return;
        }

        // ✅ Enter placement mode
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
                LogStatus("⚠️ Controllers not detected!", true);
                Debug.LogError($"[AnchorGUI] Still NULL!  Left: {leftControllerTransform == null}, Right: {rightControllerTransform == null}");
                return;
            }
        }

        Debug.Log($"[AnchorGUI] ✅ Controllers found - Left: {leftControllerTransform.name}, Right: {rightControllerTransform.name}");

        isPlacingAnchor = true;
        LogStatus("👉 Press A, B, X, or Y button to place anchor");

        // Check cursor prefab
        if (anchorCursorPrefab == null)
        {
            Debug.LogError("[AnchorGUI] ❌ ANCHOR CURSOR PREFAB IS NULL!  Check Inspector assignment!");
            LogStatus("ERROR: Cursor prefab not assigned!", true);
            return;
        }

        Debug.Log($"[AnchorGUI] ✅ Cursor prefab assigned: {anchorCursorPrefab.name}");

        // LEFT CONTROLLER CURSOR
        if (leftControllerTransform != null && leftCursorInstance == null)
        {
            Debug.Log($"[AnchorGUI] Creating LEFT cursor at controller position: {leftControllerTransform.position}");

            leftCursorInstance = Instantiate(anchorCursorPrefab, leftControllerTransform);

            if (leftCursorInstance == null)
            {
                Debug.LogError("[AnchorGUI] ❌ LEFT cursor instantiation FAILED!");
            }
            else
            {
                leftCursorInstance.name = "LeftControllerCursor";
                leftCursorInstance.transform.localPosition = Vector3.forward * cursorOffset;
                leftCursorInstance.transform.localRotation = Quaternion.identity;
                leftCursorInstance.transform.localScale = Vector3.one * cursorScale;
                leftCursorInstance.SetActive(true);

                Debug.Log($"[AnchorGUI] ✅ LEFT cursor created!");
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
                Debug.LogError("[AnchorGUI] ❌ RIGHT cursor instantiation FAILED!");
            }
            else
            {
                rightCursorInstance.name = "RightControllerCursor";
                rightCursorInstance.transform.localPosition = Vector3.forward * cursorOffset;
                rightCursorInstance.transform.localRotation = Quaternion.identity;
                rightCursorInstance.transform.localScale = Vector3.one * cursorScale;
                rightCursorInstance.SetActive(true);

                Debug.Log($"[AnchorGUI] ✅ RIGHT cursor created!");
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

            // ✅ TRY TRANSFORM FIRST
            if (controllerTransform != null)
            {
                anchorPosition = controllerTransform.position + controllerTransform.forward * cursorOffset;
                anchorRotation = controllerTransform.rotation;
            }
            // ✅ FALLBACK TO OVRINPUT
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
                LogStatus($"✅ Anchor created!   ({currentAnchors.Count} total)");

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
        if (anchorCountText == null) return;

        int localizedCount = 0;
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.Localized)
                localizedCount++;
        }

        anchorCountText.text = "Anchors: " + currentAnchors.Count + " (" + localizedCount + " localized)";
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
            createAnchorButton.interactable = true;

        if (saveAnchorButton != null)
            saveAnchorButton.interactable = currentAnchors.Count > 0;

        if (shareAnchorsButton != null)
            shareAnchorsButton.interactable = currentAnchors.Count > 0 && inSession;

        if (loadAnchorsButton != null)
            loadAnchorsButton.interactable = currentGroupUuid != Guid.Empty;

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
            // ✅ Add visual using ANCHOR MARKER prefab (not cursor!)
            GameObject visualPrefab = anchorMarkerPrefab != null ? anchorMarkerPrefab : anchorCursorPrefab;

            if (visualPrefab != null)
            {
                GameObject visual = Instantiate(visualPrefab, anchorGameObject.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one * anchorScale;

                Debug.Log($"[AnchorGUI] ✅ Added visual marker to anchor using: {visualPrefab.name}");
            }
            else
            {
                Debug.LogWarning("[AnchorGUI] ⚠️ No visual prefab available for anchor!");
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