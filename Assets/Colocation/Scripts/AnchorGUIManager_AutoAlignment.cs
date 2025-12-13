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

    [Header("Status Display")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI anchorText;
    [SerializeField] private Image statusIndicator; // Alignment Status
    [SerializeField] private Image networkIndicator; // Host/Client Status

    [Header("Settings")]
    // alignmentManager is in base class
    [SerializeField] private float anchorScale = 0.3f; // Larger for better visibility
    
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
    // _sharedAnchorGroupId is in base class
    private bool isHost = false;
    private enum SessionState { Idle, Advertising, Discovering, HostAligned, ClientAligned }
    private SessionState currentState = SessionState.Idle;

#if FUSION2
    private NetworkRunner networkRunner;
    private CubeSpawner cubeSpawner;
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

#if FUSION2
        // Start Photon Fusion session automatically
        if (autoStartSession)
        {
            StartNetworkSession();
        }
#endif

        UpdateAllUI();
        Log("Ready - Click Auto Align to start");
    }

#if FUSION2
    private async void StartNetworkSession()
    {
        Log("Starting network session...");
        
        // Find or create NetworkRunner
        networkRunner = FindObjectOfType<NetworkRunner>();
        if (networkRunner == null)
        {
            var runnerGO = new GameObject("NetworkRunner");
            networkRunner = runnerGO.AddComponent<NetworkRunner>();
            DontDestroyOnLoad(runnerGO);
        }

        // Start Fusion in Shared mode (peer-to-peer)
        var result = await networkRunner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = sessionName,
            Scene = default,
            SceneManager = networkRunner.GetComponent<INetworkSceneManager>()
        });

        if (result.Ok)
        {
            Log($"Network session started: {sessionName}");
            Debug.Log($"[AnchorGUI] Fusion session started successfully");
            
            // Make sure CubeSpawner is in the scene and ready
            await System.Threading.Tasks.Task.Delay(100);
            EnsureCubeSpawnerExists();
        }
        else
        {
            Log($"Failed to start session: {result.ShutdownReason}", true);
            Debug.LogError($"[AnchorGUI] Failed to start Fusion session: {result.ShutdownReason}");
        }
    }
    
    private void EnsureCubeSpawnerExists()
    {
        cubeSpawner = FindObjectOfType<CubeSpawner>();
        if (cubeSpawner == null)
        {
            Debug.LogWarning("[AnchorGUI] CubeSpawner not found in scene! Make sure it exists and has a NetworkObject component.");
        }
        else
        {
            Debug.Log("[AnchorGUI] CubeSpawner found and ready.");
        }
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
        Log("Creating alignment anchor...");
        await CreateAnchor(Vector3.zero, Quaternion.identity);
#endif
    }

    // ==================== STANDALONE ALIGN (NO NETWORK) ====================
    
    // CreateAndAlignAnchor removed as it is now handled by CreateAnchor override and base class logic

    public override void Spawned()
    {
        base.Spawned(); // Calls PrepareColocation if autoStartColocation is true
        
        isHost = Object.HasStateAuthority;
        UpdateStatusIndicator();
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

        if (cubeSpawner == null)
        {
            cubeSpawner = FindObjectOfType<CubeSpawner>();
        }

        if (cubeSpawner != null)
        {
            // Get right controller position for spawn location
            Vector3 spawnPos = GetControllerSpawnPosition();
            cubeSpawner.SpawnCubeAtPosition(spawnPos, Quaternion.identity);
            Log("Spawning cube at controller!");
        }
        else
        {
            Log("CubeSpawner not found!", true);
        }
#else
        Log("Photon Fusion not available!", true);
#endif
    }

    private Vector3 GetControllerAnchorPosition()
    {
        // Try to get right controller position for anchor placement
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Create anchor 2cm in front of controller
            return rightHand.position + rightHand.forward * 0.02f;
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
        // Try to get right controller position
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Spawn 30cm in front and 10cm above the controller for better visibility and stability
            Vector3 spawnPos = rightHand.position + rightHand.forward * 0.3f + Vector3.up * 0.1f;
            
            // Ensure minimum height above ground (prevent spawning below floor)
            if (spawnPos.y < 0.5f)
            {
                spawnPos.y = 0.5f;
            }
            
            return spawnPos;
        }

        // Fallback to camera forward if controller not found
        if (cameraTransform != null)
        {
            Vector3 spawnPos = cameraTransform.position + cameraTransform.forward * 0.5f;
            
            // Ensure it's at a visible height
            if (spawnPos.y < 1.0f)
            {
                spawnPos.y = 1.0f;
            }
            
            return spawnPos;
        }

        return new Vector3(0, 1.0f, 0); // Default to 1m above origin
    }

    protected override async Task<OVRSpatialAnchor> CreateAnchor(Vector3 position, Quaternion rotation)
    {
        try
        {
            // Override position to be in front of the user/controller if position is zero
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
                statusIndicator.color = anchorAlignedColor; // Green when
                statusIndicator.color = clientColor; // Yellow when client is aligned
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
