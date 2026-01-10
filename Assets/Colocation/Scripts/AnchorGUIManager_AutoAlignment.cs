using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using System;
using System.Text;
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
    [SerializeField] private Button startGameButton; // Start Table Tennis game

    [Header("Game Scene Settings")]
    [SerializeField] private string tableTennisSceneName = "TableTennis";

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
        Done 
    }
    private AlignmentStep currentStep = AlignmentStep.Start;
    
    // Anchor placement preview
    private GameObject anchorCursor1; // Visual indicator for left controller anchor
    private GameObject anchorCursor2; // Visual indicator for right controller anchor
    private Vector3 firstAnchorWorldPosition; // Stored position of first anchor
    private LineRenderer distanceLine; // Line between anchors to visualize distance
    private bool waitingForGripToPlaceAnchors = false; // True when user should press grips to place anchors
    private bool anchor1Placed = false; // Track if anchor 1 (left grip) is placed
    private bool anchor2Placed = false; // Track if anchor 2 (right grip) is placed
    
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

        anchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");
        if (anchorMarkerPrefab == null)
        {
            Debug.LogWarning("[AnchorGUI] AnchorMarker prefab not found, trying AnchorCursorSphere...");
            anchorMarkerPrefab = Resources.Load<GameObject>("AnchorCursorSphere");
        }
        
        if (anchorMarkerPrefab != null)
        {
            Debug.Log($"[AnchorGUI] Anchor prefab loaded: {anchorMarkerPrefab.name}");
        }
        else
        {
            Debug.LogError("[AnchorGUI] NO ANCHOR PREFAB FOUND! Anchors will have no visual.");
        }

        if (alignmentManager == null)
        {
            alignmentManager = FindObjectOfType<AlignmentManager>();
        }

        autoAlignButton?.onClick.AddListener(OnAutoAlignClicked);
        spawnCubeButton?.onClick.AddListener(OnSpawnCubeClicked);
        resetButton?.onClick.AddListener(OnResetClicked); // Add this
        startGameButton?.onClick.AddListener(OnStartGameClicked);

        UpdateAllUI();
        UpdateUIWizard(); // Init text
        Log("Ready - Click Start Alignment (Host) to begin");
        
        // Create anchor placement cursor (preview sphere)
        CreateAnchorCursor();
    }
    
    private void CreateAnchorCursor()
    {
        // No spheres - only distance text displays
        anchorCursor1 = null;
        anchorCursor2 = null;
        
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
    private const float DISCOVERY_RETRY_INTERVAL = 5f;

    private void Update()
    {
        UpdateStatusIndicator();
        UpdateButtonStates();
        
        // Update anchor placement cursor and distance preview
        UpdateAnchorPlacementPreview();
        
        // Check for grip button press to place anchors
        // Either grip can place anchors - first grip = Anchor 1, second grip = Anchor 2
        if (waitingForGripToPlaceAnchors)
        {
            bool leftGripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger);
            bool rightGripPressed = OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
            
            if (leftGripPressed || rightGripPressed)
            {
                bool useLeft = leftGripPressed;
                
                if (!anchor1Placed)
                {
                    PlaceAnchorAtController(1, useLeft);
                }
                else if (!anchor2Placed)
                {
                    PlaceAnchorAtController(2, useLeft);
                }
            }
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
                    UpdateUIWizard();
                }
                
                // Auto-start or retry discovery if not aligned
                // BUT skip if we already have 2 anchors localized
                if (!IsAlignmentComplete() && _clientLocalizedAnchorCount < 2)
                {
                    // Start discovery if not started, OR retry after interval if no anchors found
                    if (!_discoveryStarted || (Time.time - _lastDiscoveryTime > DISCOVERY_RETRY_INTERVAL))
                    {
                        Log("Client: Starting/Retrying anchor discovery...");
                        _discoveryStarted = true;
                        _lastDiscoveryTime = Time.time;
                        PrepareColocation();
                    }
                }
            }
            else if (localIsHost && !isHost)
            {
                isHost = true;
                Log("Host Mode: Ready to create anchors");
                UpdateUIWizard();
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

    // ==================== AUTO ALIGN ====================
    
    private void UpdateAnchorPlacementPreview()
    {
        bool inPlacementMode = waitingForGripToPlaceAnchors && (currentStep == AlignmentStep.PlaceAnchor1 || currentStep == AlignmentStep.PlaceAnchor2);
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;
        
        Transform leftHand = cameraRig.leftControllerAnchor;
        Transform rightHand = cameraRig.rightControllerAnchor;
        
        if (leftHand == null || rightHand == null) return;
        
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
                leftDistanceText.text = "Grip\nAnchor 1";
                leftDistanceText.color = Color.cyan;
            }
            
            if (rightDistanceDisplay != null && rightDistanceText != null)
            {
                rightDistanceDisplay.SetActive(true);
                rightDistanceDisplay.transform.position = rightHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    rightDistanceDisplay.transform.LookAt(cameraTransform);
                rightDistanceDisplay.transform.Rotate(0, 180, 0);
                rightDistanceText.text = "Grip\nAnchor 1";
                rightDistanceText.color = Color.cyan;
            }
            
            if (distanceLine != null) distanceLine.enabled = false;
        }
        // After Anchor 1 is placed - show distance from ACTUAL anchor position
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
            
            // Update line from anchor 1 to the further controller
            if (distanceLine != null)
            {
                distanceLine.enabled = true;
                distanceLine.SetPosition(0, anchor1Pos);
                // Show line to whichever controller is further
                Vector3 targetPos = distanceLeft > distanceRight ? leftHand.position : rightHand.position;
                distanceLine.SetPosition(1, targetPos);
                
                float dist = Mathf.Max(distanceLeft, distanceRight);
                Color lineColor = dist < 0.5f ? Color.red : (dist < 1f ? Color.yellow : Color.green);
                distanceLine.startColor = lineColor;
                distanceLine.endColor = lineColor;
            }
            
            // Show on LEFT controller
            if (leftDistanceDisplay != null && leftDistanceText != null)
            {
                leftDistanceDisplay.SetActive(true);
                leftDistanceDisplay.transform.position = leftHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    leftDistanceDisplay.transform.LookAt(cameraTransform);
                leftDistanceDisplay.transform.Rotate(0, 180, 0);
                
                string warning = distanceLeft < 0.5f ? "\n\u26a0\ufe0f Close!" : (distanceLeft < 1f ? "" : "\n\u2713");
                leftDistanceText.text = $"Grip\n{distanceLeft:F2}m{warning}";
                leftDistanceText.color = distanceLeft < 0.5f ? Color.red : (distanceLeft < 1f ? Color.yellow : Color.green);
            }
            
            // Show on RIGHT controller
            if (rightDistanceDisplay != null && rightDistanceText != null)
            {
                rightDistanceDisplay.SetActive(true);
                rightDistanceDisplay.transform.position = rightHand.position + Vector3.up * 0.15f;
                if (cameraTransform != null)
                    rightDistanceDisplay.transform.LookAt(cameraTransform);
                rightDistanceDisplay.transform.Rotate(0, 180, 0);
                
                string warning = distanceRight < 0.5f ? "\n\u26a0\ufe0f Close!" : (distanceRight < 1f ? "" : "\n\u2713");
                rightDistanceText.text = $"Grip\n{distanceRight:F2}m{warning}";
                rightDistanceText.color = distanceRight < 0.5f ? Color.red : (distanceRight < 1f ? Color.yellow : Color.green);
            }
        }
        else
        {
            // Hide when not in placement mode or both anchors placed
            if (distanceLine != null) distanceLine.enabled = false;
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
                string warning = distance < 0.5f ? " ⚠️ Too close!" : (distance < 1f ? " (OK)" : " ✓");
                btnText.text = $"📍 Anchor 2 ({distanceStr}){warning}";
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
        if (cameraRig == null) return;
        
        // Use specified controller
        Transform controllerTransform = useLeftController ? cameraRig.leftControllerAnchor : cameraRig.rightControllerAnchor;
        if (controllerTransform == null) return;
        
        // Get position at controller (waist level - no Y manipulation)
        Vector3 anchorPosition = controllerTransform.position;
        
        if (anchorNumber == 1)
        {
            // Place Anchor 1
            Log("Creating Anchor 1...");
            var anchor1 = await CreateAnchor(anchorPosition, Quaternion.identity);
            
            if (anchor1 != null)
            {
                anchor1Placed = true;
                firstAnchorWorldPosition = anchorPosition;
                currentStep = AlignmentStep.PlaceAnchor2;
                Log($"✓ Anchor 1 placed! Now Grip for Anchor 2");
                UpdateUIWizard();
            }
            else
            {
                Log("Failed to create Anchor 1. Try again.", true);
            }
        }
        else if (anchorNumber == 2)
        {
            // Check distance from first anchor
            float distance = Vector3.Distance(firstAnchorWorldPosition, anchorPosition);
            if (distance < 0.3f)
            {
                Log($"⚠️ Too close ({distance:F2}m)! Move further (at least 0.5m)", true);
                return;
            }
            
            Log("Creating Anchor 2...");
            var anchor2 = await CreateAnchor(anchorPosition, Quaternion.identity);
            
            if (anchor2 != null)
            {
                anchor2Placed = true;
                Log($"✓ Anchor 2 placed! Distance: {distance:F2}m");
                
                // Both anchors placed - move to ready state
                CheckBothAnchorsPlaced();
            }
            else
            {
                Log("Failed to create Anchor 2. Try again.", true);
            }
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
            Log($"✓ Both anchors placed! Distance: {distance:F2}m - Ready to Share");
            
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
            OVRColocationSession.StopDiscoveryAsync();
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
                Log("👆 Press any Grip to place Anchor 1");
                UpdateUIWizard();
                break;
                
            case AlignmentStep.PlaceAnchor2:
                // Remind user to grip
                if (!waitingForGripToPlaceAnchors)
                    waitingForGripToPlaceAnchors = true;
                Log("👆 Move and press any Grip for Anchor 2");
                UpdateUIWizard();
                break;

            case AlignmentStep.ReadyToShare:
                // Share
                Log("Sharing anchors and aligning...");
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
                    btnText.text = "Start Alignment (Host)";
                else 
                    btnText.text = "Client: Waiting for Host...";
                break;
            case AlignmentStep.PlaceAnchor1:
                if (waitingForGripToPlaceAnchors)
                    btnText.text = "🎯 Grip = Anchor 1";
                else
                    btnText.text = "📍 Press to start placement";
                break;
            case AlignmentStep.PlaceAnchor2:
                if (waitingForGripToPlaceAnchors)
                    btnText.text = "🎯 Grip = Anchor 2";
                else
                    btnText.text = "📍 Grip for Anchor 2";
                break;
            case AlignmentStep.ReadyToShare:
                btnText.text = "✅ Share & Align";
                break;
            case AlignmentStep.Done:
                // Show different text based on sharing vs aligned state
                if (currentState == SessionState.Sharing)
                {
                    btnText.text = "📤 Sharing... Waiting for Client";
                    autoAlignButton.interactable = false;
                }
                else if (currentState == SessionState.ClientAligned || currentState == SessionState.HostAligned)
                {
                    btnText.text = "✓ Aligned!";
                    autoAlignButton.interactable = false;
                }
                else
                {
                    btnText.text = "✓ Aligned!";
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
        Log("🔎 Client: Starting session discovery...");
        base.DiscoverNearbySession();
    }

    protected override void OnColocationSessionDiscovered(OVRColocationSession.Data session)
    {
        Log($"🔍 Session discovered! UUID: {session.AdvertisementUuid}");
        base.OnColocationSessionDiscovered(session);
    }

    protected override async void LoadAndAlignToAnchor(Guid groupUuid)
    {
        try
        {
            Log($"Client: Loading anchors for Group UUID: {groupUuid.ToString().Substring(0, 8)}...");

            // CRITICAL: Wait a bit for host's ShareAsync to complete
            // This is necessary because the client might discover the session BEFORE 
            // the host has finished sharing the anchors to that session
            Log("⏳ Waiting 3 seconds for host to complete anchor sharing...");
            await Task.Delay(3000);

            // CRITICAL FIX: Retry loading with delays (anchors may take time to propagate)
            var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            bool loadSuccess = false;
            int retryCount = 0;
            const int MAX_RETRIES = 8; // Increased from 5

            while (retryCount < MAX_RETRIES)
            {
                unboundAnchors.Clear();
                Log($"Attempt {retryCount + 1}: Calling LoadUnboundSharedAnchorsAsync with UUID: {groupUuid}");
                var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);

                Log($"LoadResult: Success={loadResult.Success}, Status={loadResult.Status}, Count={unboundAnchors.Count}");

                if (loadResult.Success && unboundAnchors.Count > 0)
                {
                    Log($"✓ Found {unboundAnchors.Count} shared anchors!");
                    loadSuccess = true;
                    break;
                }

                // Log the actual status to understand why it failed
                if (!loadResult.Success)
                {
                    Log($"⚠️ Load failed with status: {loadResult.Status}", true);
                }
                else if (unboundAnchors.Count == 0)
                {
                    Log($"⚠️ Load succeeded but no anchors returned (host may still be sharing)");
                }

                retryCount++;
                if (retryCount < MAX_RETRIES)
                {
                    int waitTime = retryCount * 2; // Increasing wait: 2s, 4s, 6s, 8s
                    Log($"⏱️ Retry {retryCount}/{MAX_RETRIES}: Waiting {waitTime}s...");
                    await Task.Delay(waitTime * 1000);
                }
            }

            if (!loadSuccess || unboundAnchors.Count == 0)
            {
                Log($"❌ Failed to load anchors after {MAX_RETRIES} retries", true);
                Log($"Count: {unboundAnchors.Count}", true);
                Log("TROUBLESHOOTING:", true);
                Log("1. Make sure HOST clicked 'Share & Align' and it succeeded", true);
                Log("2. Check both devices have internet connection", true);
                Log("3. Verify spatial data sharing is enabled on both devices", true);
                Log("4. Try walking around the area where host placed anchors", true);
                Log($"5. Group UUID: {groupUuid}", true);
                return;
            }

            Log($"Localizing {unboundAnchors.Count} anchors in the physical space...");
            Log("💡 TIP: Walk around slowly to help Quest scan the environment");

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
                    Log($"✓ Anchor {i + 1} localized: {unboundAnchor.Uuid.ToString().Substring(0, 8)}");

                    var anchorGameObject = new GameObject($"Anchor_{unboundAnchor.Uuid.ToString().Substring(0, 8)}");
                    var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor.BindTo(spatialAnchor);

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
                    Log($"❌ Failed to localize anchor {i + 1}: {unboundAnchor.Uuid.ToString().Substring(0, 8)}", true);
                    Log("Try moving closer to where the host placed the anchors", true);
                }
            }

            // Now Align based on what we found
            if (localizedAnchors.Count >= 2)
            {
                Log($"✓ Client aligned! Using 2-point alignment with {localizedAnchors.Count} anchors");
                currentStep = AlignmentStep.Done;
                _clientLocalizedAnchorCount = localizedAnchors.Count;

                _localizedAnchor = localizedAnchors[0]; // Set primary as main
                alignmentManager.AlignUserToTwoAnchors(localizedAnchors[0], localizedAnchors[1]);
                
                // Store anchor positions and mark alignment complete
                FirstAnchorPosition = localizedAnchors[0].transform.position;
                SecondAnchorPosition = localizedAnchors[1].transform.position;
                AlignmentCompletedStatic = true;
                
                // Disable rediscovery - we have both anchors
                _discoveryStarted = false; // Prevent auto-retry
                OVRColocationSession.StopDiscoveryAsync();
                Log("Client: 2 anchors localized - discovery disabled");
                
                UpdateUIWizard();
            }
            else if (localizedAnchors.Count == 1)
            {
                Log("✓ Client aligned! Using single-point alignment (only 1 anchor found)");
                currentStep = AlignmentStep.Done;
                _clientLocalizedAnchorCount = 1;

                _localizedAnchor = localizedAnchors[0];
                alignmentManager.AlignUserToAnchor(localizedAnchors[0]);
                
                // Store anchor position and mark alignment complete
                FirstAnchorPosition = localizedAnchors[0].transform.position;
                AlignmentCompletedStatic = true;
                
                UpdateUIWizard();
            }
            else
            {
                Log("❌ No anchors localized! Cannot align.", true);
                Log("Make sure you're in the same physical location as the host", true);
            }

            UpdateAllUI();
        }
        catch (Exception e)
        {
            Log($"❌ Error during anchor loading: {e.Message}", true);
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

            // CRITICAL FIX: Wait for all anchors to be fully localized and stable
            await Task.Delay(1000); // Give anchors time to stabilize

            // Verify all anchors are still valid and localized
            int localizedCount = 0;
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.Localized)
                {
                    localizedCount++;
                    Log($"Anchor {anchor.Uuid.ToString().Substring(0, 8)} is localized");
                }
                else
                {
                    Log($"WARNING: Anchor {anchor?.Uuid.ToString().Substring(0, 8)} is NOT localized yet", true);
                }
            }

            if (localizedCount < currentAnchors.Count)
            {
                Log($"Only {localizedCount}/{currentAnchors.Count} anchors localized. Waiting longer...");
                await Task.Delay(1000);
            }

            Log($"Saving and sharing {currentAnchors.Count} anchors to Group: {_sharedAnchorGroupId}...");

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
                         Log($"❌ Failed to save anchor {anchor.Uuid.ToString().Substring(0, 8)}: Status={saveResult.Status}", true);

                         // Provide specific error guidance
                         if (saveResult.Status.ToString().Contains("Pending"))
                         {
                             Log("Anchor save is pending. Waiting and retrying...");
                             await Task.Delay(2000);
                             saveResult = await anchor.SaveAnchorAsync();
                             if (saveResult.Success)
                             {
                                 Log($"✓ Anchor {anchor.Uuid.ToString().Substring(0, 8)} saved on retry");
                                 anchorsToShare.Add(anchor);
                             }
                         }
                     }
                     else
                     {
                         Log($"✓ Anchor {anchor.Uuid.ToString().Substring(0, 8)} saved successfully");
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
            var shareResult = await OVRSpatialAnchor.ShareAsync(anchorsToShare, _sharedAnchorGroupId);

            if (shareResult.Success)
            {
                Log($"✓ Sharing anchors complete! {anchorsToShare.Count} anchors shared. Waiting for client...");
                currentStep = AlignmentStep.Done;
                currentState = SessionState.Sharing; // Host is sharing, not aligned until client joins

                // HOST ALIGNMENT
                if (anchorsToShare.Count >= 2)
                {
                    // Host aligns to its own anchors
                    Log("Host: Aligning to 2 anchors...");
                    _localizedAnchor = anchorsToShare[0];
                    alignmentManager.AlignUserToTwoAnchors(anchorsToShare[0], anchorsToShare[1]);
                    
                    // Store anchor positions and mark alignment complete
                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    SecondAnchorPosition = anchorsToShare[1].transform.position;
                    AlignmentCompletedStatic = true;
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
                Log($"❌ Failed to share anchors. Status: {shareResult.Status}", true);
                Log("TROUBLESHOOTING:", true);
                Log("1. Check Meta Quest Settings > Privacy > Spatial Data > Allow apps to share", true);
                Log("2. Make sure both devices are signed into Meta accounts", true);
                Log("3. Ensure internet connection is active", true);
                Log($"4. Group UUID: {_sharedAnchorGroupId}", true);
            }
        }
        catch (Exception e)
        {
            Log($"❌ Error in ShareAnchors: {e.Message}", true);
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
            Debug.Log($"[AnchorGUI] Spawn: World {worldPos} -> Anchor-relative {anchorRelativePos}");
        }
        else
        {
            Debug.LogWarning("[AnchorGUI] No localized anchor! Using world position directly.");
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
        Debug.Log($"[AnchorGUI] Host received spawn request at anchor-relative: {anchorRelativePos}");
        SpawnCubeAtAnchorPosition(anchorRelativePos);
    }

    private void SpawnCubeAtAnchorPosition(Vector3 anchorRelativePos)
    {
        // Clear existing cube first (limit to 1)
        if (spawnedCube != null && Runner != null)
        {
            Debug.Log($"[AnchorGUI] Despawning existing cube: {spawnedCube.Id}");
            Runner.Despawn(spawnedCube);
            spawnedCube = null;
        }

        if (_localizedAnchor == null || !_localizedAnchor.Localized)
        {
            Debug.LogError("[AnchorGUI] Cannot spawn cube - no localized anchor!");
            return;
        }

        // Spawn at world position first (required by Fusion)
        Vector3 worldPos = _localizedAnchor.transform.TransformPoint(anchorRelativePos);
        Debug.Log($"[AnchorGUI] Spawning cube at world {worldPos}, will parent to anchor");

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
            Debug.Log($"[AnchorGUI] Cube parented to anchor at local pos {anchorRelativePos}! NetworkId: {newCube.Id}");
        }
        else
        {
            Debug.LogError("[AnchorGUI] Failed to spawn cube!");
        }
    }

    private void DespawnAllCubesOnHost()
    {
        // Only the host (state authority) should despawn networked cubes
        if (!Object.HasStateAuthority || Runner == null || !Runner.IsRunning)
        {
            Debug.Log("[AnchorGUI] Not host or runner not ready, cannot despawn cubes");
            return;
        }
        
        // Find and despawn all NetworkedCube objects via Fusion
        var allCubes = FindObjectsOfType<NetworkedCube>();
        Debug.Log($"[AnchorGUI] Host despawning {allCubes.Length} cubes via network");
        
        foreach (var cube in allCubes)
        {
            if (cube != null && cube.Object != null && cube.Object.IsValid)
            {
                Debug.Log($"[AnchorGUI] Despawning cube: {cube.Object.Id}");
                Runner.Despawn(cube.Object);
            }
        }
        spawnedCube = null;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestDespawnAllCubes()
    {
        Debug.Log("[AnchorGUI] Host received request to despawn all cubes");
        DespawnAllCubesOnHost();
    }
#endif

    private void OnResetClicked()
    {
        Debug.Log("[AnchorGUI] Reset clicked - clearing scene and session");
        
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
                Debug.Log("[AnchorGUI] Client requesting host to despawn cubes");
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
                Debug.Log($"[AnchorGUI] Destroying anchor: {anchor.Uuid}");
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
            Debug.Log("[AnchorGUI] Camera rig reset to origin");
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
        Debug.Log("[AnchorGUI] Start Game clicked");
        
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
            LoadTableTennisSceneNetworked();
        }
        else
        {
            Log("Requesting to start game...");
            RPC_RequestStartGame();
        }
#else
        // Non-networked fallback
        LoadTableTennisSceneLocal();
#endif
    }

#if FUSION2
    /// <summary>
    /// Client requests host to start the game
    /// </summary>
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestStartGame()
    {
        Debug.Log("[AnchorGUI] Host received request to start game - loading scene for all");
        LoadTableTennisSceneNetworked();
    }

    /// <summary>
    /// Called by host to notify all clients to preserve their anchors before scene transition
    /// </summary>
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_PrepareForSceneTransition()
    {
        Debug.Log("[AnchorGUI] Received scene transition notification - preserving anchors");
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
            Debug.Log($"[AnchorGUI] Host loading networked scene: {tableTennisSceneName}");
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
                Debug.LogError($"[AnchorGUI] Scene '{tableTennisSceneName}' not found in Build Settings! Add it via File > Build Settings");
                Log("Scene not in Build Settings!", true);
            }
        }
        else
        {
            Debug.LogError("[AnchorGUI] Cannot load scene - Runner not available");
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
            Debug.Log($"[AnchorGUI] Preserved anchor for scene transition: {_localizedAnchor.Uuid}");
        }
        else
        {
            Debug.LogWarning("[AnchorGUI] No localized anchor to preserve!");
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
#endif

    private Vector3 GetControllerAnchorPosition()
    {
        // Get right controller position at waist level (no Y manipulation)
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Use actual controller position (waist level) - no Y manipulation
            Debug.Log($"[AnchorGUI] Placing anchor at waist level, position: {rightHand.position}");
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

            Log($"Anchor created: {spatialAnchor.Uuid}");
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
            sb.AppendLine($"  {(anchor.Localized ? "✓" : "||")} {anchor.Uuid.ToString().Substring(0, 8)}");
            
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
