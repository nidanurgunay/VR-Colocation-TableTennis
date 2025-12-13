using UnityEngine;
using UnityEngine.UI;
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
public class AnchorAutoGUIManager : ColocationManager
{
    [Header("UI Buttons")]
    [SerializeField] private Button autoAlignButton;
    [SerializeField] private Button spawnCubeButton;
    [SerializeField] private Button resetButton; // Add this

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

        UpdateAllUI();
        Log("Ready - Click Auto Align to start");
    }

#if FUSION2
    private async void StartNetworkSession()
    {
        Log("Network session ready. Awaiting user action to start colocation.");
    }
    #endif

    private void Update()
    {
        UpdateStatusIndicator();
        UpdateButtonStates();
        
#if FUSION2
        if (networkRunner == null)
        {
            networkRunner = FindObjectOfType<NetworkRunner>();
        }
#endif
    }

    // ==================== AUTO ALIGN ====================

    private async void OnAutoAlignClicked()
    {
        if (cameraTransform == null)
        {
            Log("Camera not found!", true);
            Debug.LogError("[AnchorGUI] Camera.main is null! Make sure OVRCameraRig has MainCamera tag.");
            return;
        }

#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            Log("Network not ready! Starting session...");
            StartNetworkSession();
            return;
        }

        // Determine if this is host or client
        isHost = networkRunner.IsServer || networkRunner.IsSharedModeMasterClient;
        
        Debug.Log($"[AnchorGUI] Auto Align clicked. Role: {(isHost ? "HOST" : "CLIENT")}");
        Debug.Log($"[AnchorGUI] Camera position: {cameraTransform.position}");
        
        // Update status indicator color based on role
        if (statusIndicator != null)
        {
            statusIndicator.color = isHost ? hostColor : clientColor;
        }
        
        Log("Starting Colocation...");
        PrepareColocation();
#else
        Log("Creating alignment anchor..");
        await CreateAnchor(Vector3.zero, Quaternion.identity);
#endif
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
        else if (message.Contains("Alignment anchor shared")) currentState = SessionState.HostAligned;
        else if (message.Contains("Anchor localized successfully")) currentState = SessionState.ClientAligned;

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

            foreach (var unboundAnchor in unboundAnchors)
            {
                if (await unboundAnchor.LocalizeAsync())
                {
                    Log($"Anchor localized successfully. UUID: {unboundAnchor.Uuid}");

                    var anchorGameObject = new GameObject($"Anchor_{unboundAnchor.Uuid}");
                    var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor.BindTo(spatialAnchor);

                    // Add visual to the anchor so client can see it too
                    if (anchorMarkerPrefab != null)
                    {
                        GameObject visual = Instantiate(anchorMarkerPrefab, anchorGameObject.transform);
                        visual.name = "Visual";
                        visual.transform.localPosition = Vector3.zero;
                        visual.transform.localRotation = Quaternion.identity;
                        
                        float validScale = Mathf.Max(anchorScale, 0.01f);
                        visual.transform.localScale = Vector3.one * validScale;
                        
                        // Remove physics components from visual
                        foreach (var col in visual.GetComponentsInChildren<Collider>())
                            Destroy(col);
                        foreach (var rb in visual.GetComponentsInChildren<Rigidbody>())
                            Destroy(rb);
                            
                        Debug.Log("[AnchorGUI] Added visual to client anchor");
                    }

                    // Track this anchor locally for UI and reset
                    currentAnchors.Add(spatialAnchor);
                    
                    _localizedAnchor = spatialAnchor; // Store for relative positioning
                    alignmentManager.AlignUserToAnchor(spatialAnchor);
                    
                    UpdateAllUI();
                    return;
                }

                Log($"Failed to localize anchor: {unboundAnchor.Uuid}", true);
            }
        }
        catch (Exception e)
        {
            Log($"Error during anchor loading and alignment: {e.Message}", true);
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

    private void ClearAllCubesLocal()
    {
        // Find and destroy all cube GameObjects directly
        var allCubes = FindObjectsOfType<NetworkedCube>();
        Debug.Log($"[AnchorGUI] Force clearing {allCubes.Length} cubes locally");
        
        foreach (var cube in allCubes)
        {
            if (cube != null && cube.gameObject != null)
            {
                Destroy(cube.gameObject);
            }
        }
        spawnedCube = null;
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
        
        // Clear all spawned cubes
#if FUSION2
        // Try network despawn first, then force local destroy
        if (spawnedCube != null && Object.HasStateAuthority && Runner != null && Runner.IsRunning)
        {
            Debug.Log($"[AnchorGUI] Despawning cube via network: {spawnedCube.Id}");
            Runner.Despawn(spawnedCube);
        }
        
        // Always do local cleanup to ensure cubes are gone
        ClearAllCubesLocal();
        Log("Cleared all cubes");
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

        // Reset state
        currentState = SessionState.Idle;
        _sharedAnchorGroupId = Guid.Empty;
        _localizedAnchor = null;
        
        // Reset UI
        UpdateAllUI();
        Log("Scene reset. Click Auto Align to start fresh alignment");
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

        if (currentAnchors == null || currentAnchors.Count == 0)
            return false;

        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.Localized)
                return true;
        }

        return false;
    }


}
