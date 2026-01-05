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
    private enum SessionState { Idle, Advertising, Discovering, HostAligned, ClientAligned }
    private SessionState currentState = SessionState.Idle;

    // Wizard State
    private enum AlignmentStep { 
        Start, 
        PlaceAnchor1, 
        PlaceAnchor2, 
        ReadyToShare, 
        Done 
    }
    private AlignmentStep currentStep = AlignmentStep.Start;
    private bool anchorPlacementMode = false; // True when user can place anchors with A/X buttons
    private Transform firstAnchorTransform; // Reference to first anchor for distance calculation
    private GameObject distanceDisplayObj; // Floating text showing distance
    private TextMeshPro distanceText; // 3D text component

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
        
        // Check for anchor placement via controller buttons
        if (anchorPlacementMode && isHost)
        {
            CheckControllerAnchorPlacement();
            UpdateDistanceDisplay(); // Show distance from first anchor
        }
        else if (distanceDisplayObj != null)
        {
            // Hide distance display when not in placement mode
            distanceDisplayObj.SetActive(false);
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
                if (!IsAlignmentComplete())
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
            Log("Client Mode: Waiting for host...");
            PrepareColocation(); // Start discovery
            return;
        }

        // HOST WIZARD LOGIC
        switch (currentStep)
        {
            case AlignmentStep.Start:
                // Start -> Enter placement mode
                Log("Press A/X at table edges to place anchors");
                if (statusText != null)
                {
                    statusText.text = "Point controller at TABLE EDGE and press A or X";
                }
                currentStep = AlignmentStep.PlaceAnchor1;
                anchorPlacementMode = true; // Enable controller button placement
                UpdateUIWizard();
                break;

            case AlignmentStep.PlaceAnchor1:
                // Already in placement mode, remind user
                Log("Press A/X at first table edge");
                if (statusText != null)
                {
                    statusText.text = "Point at TABLE EDGE #1 → Press A or X";
                }
                break;

            case AlignmentStep.PlaceAnchor2:
                // Already in placement mode, remind user
                Log("Press A/X at second table edge");
                if (statusText != null)
                {
                    statusText.text = "Point at TABLE EDGE #2 (opposite side) → Press A or X";
                }
                break;

            case AlignmentStep.ReadyToShare:
                // Share
                Log("Sharing anchors and aligning...");
                PrepareColocation(); // Triggers ShareAnchors()
                currentStep = AlignmentStep.Done;
                anchorPlacementMode = false;
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
                    btnText.text = "Place Anchors";
                else 
                    btnText.text = "Client: Waiting for Host...";
                break;
            case AlignmentStep.PlaceAnchor1:
                btnText.text = "Press A/X: Anchor 1";
                break;
            case AlignmentStep.PlaceAnchor2:
                btnText.text = "Press A/X: Anchor 2";
                break;
            case AlignmentStep.ReadyToShare:
                btnText.text = "Share & Align";
                break;
            case AlignmentStep.Done:
                btnText.text = "Aligned (Done)";
                autoAlignButton.interactable = false;
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
        else if (message.Contains("Host aligned")) currentState = SessionState.HostAligned;
        else if (message.Contains("Client aligned")) currentState = SessionState.ClientAligned;

        UpdateStatusIndicator();
    }

    // ==================== STANDALONE ALIGN (NO NETWORK) ====================

    protected override async void LoadAndAlignToAnchor(Guid groupUuid)
    {
        try
        {
            Log($"Loading anchors for Group UUID: {groupUuid}...");

            var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);

            if (!loadResult.Success || unboundAnchors.Count == 0)
            {
                Log($"Failed to load anchors. Success: {loadResult.Success}, Count: {unboundAnchors.Count}", true);
                return;
            }

            Log($"Found {unboundAnchors.Count} shared anchors. Localizing...");

            // Localize ALL found anchors
            var localizedAnchors = new List<OVRSpatialAnchor>();

            foreach (var unboundAnchor in unboundAnchors)
            {
                if (await unboundAnchor.LocalizeAsync())
                {
                    Log($"Anchor localized: {unboundAnchor.Uuid}");

                    var anchorGameObject = new GameObject($"Anchor_{unboundAnchor.Uuid}");
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
                    Log($"Failed to localize anchor: {unboundAnchor.Uuid}", true);
                }
            }
            
            // Now Align based on what we found
            if (localizedAnchors.Count >= 2)
            {
                Log("Aligning to TWO anchors...");
                // Assume first is Primary, second is Secondary (Forward)
                // Or sort by creation time? OVR doesn't give creation time easily. 
                // We rely on List order. Usually consistent if created and shared in order.
                
                _localizedAnchor = localizedAnchors[0]; // Set primary as main
                alignmentManager.AlignUserToTwoAnchors(localizedAnchors[0], localizedAnchors[1]);
            }
            else if (localizedAnchors.Count == 1)
            {
                Log("Only 1 anchor localized. Using single point alignment.");
                _localizedAnchor = localizedAnchors[0];
                alignmentManager.AlignUserToAnchor(localizedAnchors[0]);
            }
            else
            {
                Log("No anchors localized!", true);
            }
            
            UpdateAllUI();
        }
        catch (Exception e)
        {
            Log($"Error during anchor loading and alignment: {e.Message}", true);
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
            
            Log($"Saving and sharing {currentAnchors.Count} anchors to Group: {_sharedAnchorGroupId}...");
            
            // Save all anchors
            var anchorsToShare = new List<OVRSpatialAnchor>();
            foreach (var anchor in currentAnchors)
            {
                 if (anchor != null && anchor.Localized)
                 {
                     Log($"Saving anchor {anchor.Uuid}...");
                     var saveResult = await anchor.SaveAnchorAsync();
                     if (!saveResult.Success)
                     {
                         Log($"Failed to save anchor {anchor.Uuid}: Status={saveResult.Status}", true);
                     }
                     else
                     {
                         Log($"Anchor {anchor.Uuid} saved successfully");
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
                return;
            }

            Log($"Sharing {anchorsToShare.Count} anchors to group {_sharedAnchorGroupId}...");
            var shareResult = await OVRSpatialAnchor.ShareAsync(anchorsToShare, _sharedAnchorGroupId);

            if (shareResult.Success)
            {
                Log($"Success! Shared {anchorsToShare.Count} anchors. Group UUID: {_sharedAnchorGroupId}");
                currentStep = AlignmentStep.Done;
                
                // HOST ALIGNMENT
                if (anchorsToShare.Count >= 2)
                {
                    // Host aligns to its own anchors
                    Log("Host: Aligning to 2 anchors...");
                    _localizedAnchor = anchorsToShare[0];
                    alignmentManager.AlignUserToTwoAnchors(anchorsToShare[0], anchorsToShare[1]);
                }
                else
                {
                    Log("Host: Aligning to single anchor...");
                    _localizedAnchor = anchorsToShare[0];
                    alignmentManager.AlignUserToAnchor(anchorsToShare[0]);
                }
                
                UpdateUIWizard();
            }
            else
            {
                Log($"Failed to share anchors. Status: {shareResult.Status}", true);
                Log("Make sure both devices have sharing permissions enabled in Meta settings.", true);
            }
        }
        catch (Exception e)
        {
            Log($"Error in ShareAnchors: {e.Message}", true);
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
            Log("Please align first before starting game!", true);
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            Log("Network not ready!", true);
            return;
        }

        // Either player can initiate - request goes to host, host loads scene
        if (Object.HasStateAuthority)
        {
            Log("Starting game for all players...");
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

    /// <summary>
    /// Check if A or X button is pressed to place anchor at controller position
    /// </summary>
    private void CheckControllerAnchorPlacement()
    {
        // A button on right controller (Button.One on RTouch)
        // X button on left controller (Button.Three on LTouch)
        bool aPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool xPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        
        if (aPressed || xPressed)
        {
            // Get the controller that was pressed
            var cameraRig = FindObjectOfType<OVRCameraRig>();
            Vector3 anchorPosition = Vector3.zero;
            Quaternion anchorRotation = Quaternion.identity;
            
            if (cameraRig != null)
            {
                Transform controller = aPressed ? cameraRig.rightControllerAnchor : cameraRig.leftControllerAnchor;
                if (controller != null)
                {
                    // Place anchor at controller position, flat rotation (only Y axis)
                    anchorPosition = controller.position;
                    anchorRotation = Quaternion.Euler(0, controller.eulerAngles.y, 0);
                }
            }
            
            if (anchorPosition != Vector3.zero)
            {
                PlaceAnchorAtPosition(anchorPosition, anchorRotation);
            }
        }
    }
    
    /// <summary>
    /// Place anchor at specified position (called from controller button press)
    /// </summary>
    private async void PlaceAnchorAtPosition(Vector3 position, Quaternion rotation)
    {
        if (currentStep == AlignmentStep.PlaceAnchor1)
        {
            Log("Placing Anchor 1...");
            var anchor1 = await CreateAnchor(position, rotation);
            
            if (anchor1 != null)
            {
                // Store reference to first anchor for distance calculation
                firstAnchorTransform = anchor1.transform;
                
                Log("✓ Anchor 1 placed at table edge!");
                if (statusText != null)
                {
                    statusText.text = "✓ Anchor 1 Done! Now point at OPPOSITE table edge → Press A/X";
                }
                currentStep = AlignmentStep.PlaceAnchor2;
                UpdateUIWizard();
                
                // Create distance display for second anchor placement
                CreateDistanceDisplay();
            }
            else
            {
                Log("Failed to create Anchor 1. Try again.", true);
                if (statusText != null)
                {
                    statusText.text = "Failed! Try again - Press A/X at table edge";
                }
            }
        }
        else if (currentStep == AlignmentStep.PlaceAnchor2)
        {
            Log("Placing Anchor 2...");
            var anchor2 = await CreateAnchor(position, rotation);
            
            if (anchor2 != null)
            {
                Log("✓ Anchor 2 placed! Both anchors ready.");
                if (statusText != null)
                {
                    statusText.text = "✓ Both anchors placed! Click 'Share & Align' to continue";
                }
                currentStep = AlignmentStep.ReadyToShare;
                anchorPlacementMode = false; // Exit placement mode
                UpdateUIWizard();
                
                // Hide and destroy distance display
                if (distanceDisplayObj != null)
                {
                    Destroy(distanceDisplayObj);
                    distanceDisplayObj = null;
                }
            }
            else
            {
                Log("Failed to create Anchor 2. Try again.", true);
                if (statusText != null)
                {
                    statusText.text = "Failed! Try again - Press A/X at table edge";
                }
            }
        }
    }
    
    /// <summary>
    /// Create floating 3D text to show distance from first anchor
    /// </summary>
    private void CreateDistanceDisplay()
    {
        if (distanceDisplayObj != null) return;
        
        distanceDisplayObj = new GameObject("DistanceDisplay");
        
        // Add TextMeshPro 3D text
        distanceText = distanceDisplayObj.AddComponent<TextMeshPro>();
        distanceText.text = "0.00m";
        distanceText.fontSize = 0.5f;
        distanceText.alignment = TextAlignmentOptions.Center;
        distanceText.color = Color.yellow;
        
        // Add background for better visibility
        var rectTransform = distanceDisplayObj.GetComponent<RectTransform>();
        if (rectTransform != null)
        {
            rectTransform.sizeDelta = new Vector2(0.3f, 0.1f);
        }
        
        Debug.Log("[AnchorGUI] Distance display created");
    }
    
    /// <summary>
    /// Update distance display position and value
    /// </summary>
    private void UpdateDistanceDisplay()
    {
        if (currentStep != AlignmentStep.PlaceAnchor2 || firstAnchorTransform == null)
        {
            if (distanceDisplayObj != null) distanceDisplayObj.SetActive(false);
            return;
        }
        
        if (distanceDisplayObj == null) CreateDistanceDisplay();
        distanceDisplayObj.SetActive(true);
        
        // Get active controller position
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;
        
        // Check which controller is active (prefer right, fallback to left)
        Transform activeController = null;
        if (OVRInput.IsControllerConnected(OVRInput.Controller.RTouch))
        {
            activeController = cameraRig.rightControllerAnchor;
        }
        else if (OVRInput.IsControllerConnected(OVRInput.Controller.LTouch))
        {
            activeController = cameraRig.leftControllerAnchor;
        }
        
        if (activeController == null) return;
        
        Vector3 controllerPos = activeController.position;
        
        // Calculate distance from first anchor
        float distance = Vector3.Distance(firstAnchorTransform.position, controllerPos);
        float heightDiff = controllerPos.y - firstAnchorTransform.position.y;
        
        // Position text above controller
        distanceDisplayObj.transform.position = controllerPos + Vector3.up * 0.15f;
        
        // Face the camera
        if (cameraTransform != null)
        {
            distanceDisplayObj.transform.LookAt(cameraTransform);
            distanceDisplayObj.transform.Rotate(0, 180, 0); // Flip to face user
        }
        
        // Update text with distance and height
        if (distanceText != null)
        {
            string heightStr = heightDiff >= 0 ? $"+{heightDiff:F2}m" : $"{heightDiff:F2}m";
            distanceText.text = $"<size=120%>{distance:F2}m</size>\n<size=80%>Height: {heightStr}</size>";
            
            // Color based on typical table tennis table length (2.74m)
            if (distance >= 2.5f && distance <= 3.0f)
            {
                distanceText.color = Color.green; // Good table length
            }
            else if (distance >= 2.0f && distance <= 3.5f)
            {
                distanceText.color = Color.yellow; // Acceptable
            }
            else
            {
                distanceText.color = Color.red; // Too short or too long
            }
        }
    }

    private Vector3 GetControllerAnchorPosition()
    {
        // Try to get right controller position for anchor placement
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Create anchor 5cm in front of controller tip
            return rightHand.position + rightHand.forward * 0.05f;
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
#else
        bool hasNetwork = false;
#endif

        if (autoAlignButton != null)
            autoAlignButton.interactable = !isAligned; // Disable once aligned

        if (spawnCubeButton != null)
            spawnCubeButton.interactable = hasNetwork && isAligned;

        if (resetButton != null)
            resetButton.interactable = true; // Always available
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
