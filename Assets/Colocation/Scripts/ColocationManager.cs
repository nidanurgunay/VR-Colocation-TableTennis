#if FUSION2

using Fusion;
using System;
using System.Text;
using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Generic;
using TMPro;

public class ColocationManager : NetworkBehaviour, IPlayerJoined, IPlayerLeft
{
    [SerializeField] protected AlignmentManager alignmentManager;
    [SerializeField] protected bool autoStartColocation = false;
    [SerializeField] protected TextMeshProUGUI statusText; // Optional UI element
    [SerializeField] protected GameObject anchorMarkerPrefab; // Optional visual prefab
    [SerializeField] protected float anchorScale = 0.1f; // Scale for anchor visuals

    protected Guid _sharedAnchorGroupId;
    protected OVRSpatialAnchor _localizedAnchor; // The anchor used for alignment (host-created or client-localized)
    protected List<OVRSpatialAnchor> currentAnchors = new List<OVRSpatialAnchor>();
    
    // Static properties for external access (optional)
    public static Vector3 FirstAnchorPosition { get; protected set; }
    public static Vector3 SecondAnchorPosition { get; protected set; }
    public static bool AlignmentCompletedStatic { get; protected set; }
    
    // Optional state for derived classes
    protected int _clientLocalizedAnchorCount = 0;
    protected bool _discoveryStarted = false;
    protected float _lastDiscoveryTime = 0f;
    
    // Colocation state enum
    public enum ColocationState 
    { 
        Idle,               // Initial state
        PlaceAnchor1,       // Host placing first anchor
        PlaceAnchor2,       // Host placing second anchor
        ReadyToShare,       // Anchors placed, ready to share
        AdvertisingSession, // Host advertising session for discovery
        DiscoveringSession, // Client discovering host sessions
        SharingAnchors,     // Sharing/loading spatial anchors
        HostAligned,        // Host aligned
        ClientAligned,      // Client aligned
        ShareFailed,        // Sharing failed
        Done                // Both devices aligned
    }
    
    protected ColocationState currentState = ColocationState.Idle;
    
#if FUSION2
    [Networked] protected NetworkBool HostAnchorsShared { get; set; }
    [Networked, Capacity(64)] protected NetworkString<_64> SharedAnchorGroupUuidString { get; set; }
    // Individual anchor UUIDs for consistent ordering between host and client
    [Networked, Capacity(64)] protected NetworkString<_64> FirstAnchorUuidString { get; set; }
    [Networked, Capacity(64)] protected NetworkString<_64> SecondAnchorUuidString { get; set; }
#endif
    

    public override void Spawned()
    {
        base.Spawned();
        if (autoStartColocation)
        {
            PrepareColocation();
        }
    }

    public virtual void PrepareColocation()
    {
        if (Object.HasStateAuthority)
        {
            Log("[ColocationManager PrepareColocation] Starting advertisement...");
            AdvertiseColocationSession();
        }
        else
        {
            Log("[ColocationManager PrepareColocation] Starting discovery...");
            DiscoverNearbySession();
        }
    }

    protected virtual async void AdvertiseColocationSession()
    {
        try
        {
            var advertisementData = Encoding.UTF8.GetBytes("SharedSpatialAnchorSession");
            var startAdvertisementResult = await OVRColocationSession.StartAdvertisementAsync(advertisementData);

            if (startAdvertisementResult.Success)
            {
                _sharedAnchorGroupId = startAdvertisementResult.Value;
                Log($"[ColocationManager AdvertiseColocationSession] Advertisement started successfully. UUID: {_sharedAnchorGroupId}");
                ShareAnchors(); // Changed from CreateAndShareAlignmentAnchor
            }
            else
            {
                Log($"[ColocationManager AdvertiseColocationSession] Advertisement failed with status: {startAdvertisementResult.Status}", true);
            }
        }
        catch (Exception e)
        {
            Log($"[ColocationManager AdvertiseColocationSession] Error during advertisement: {e.Message}", true);
        }
    }

    // Renamed and refactored to share EXISTING anchors if available, or create one if none
    protected virtual async void ShareAnchors()
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
                Log("Sharing Error!", true);
                return;
            }

            Log("Sharing...");
            
            var shareResult = await OVRSpatialAnchor.ShareAsync(anchorsToShare, _sharedAnchorGroupId);

            if (shareResult.Success)
            {
                Log("Sharing Success!");
                currentState = ColocationState.SharingAnchors; // Host is sharing, not aligned until client joins

                // IMPORTANT: Set networked flag so client knows anchors are ready
                HostAnchorsShared = true;
                SharedAnchorGroupUuidString = _sharedAnchorGroupId.ToString();

                // HOST ALIGNMENT
                if (anchorsToShare.Count >= 2)
                {
                    // Host aligns to its own anchors
                    _localizedAnchor = anchorsToShare[0];

                    // Store individual anchor UUIDs for client to match order
                    FirstAnchorUuidString = anchorsToShare[0].Uuid.ToString();
                    SecondAnchorUuidString = anchorsToShare[1].Uuid.ToString();
                    Debug.Log($"[ColocationManager ShareAnchors (anchor)] HOST sharing anchor UUIDs: First={FirstAnchorUuidString}, Second={SecondAnchorUuidString}");

                    // Log anchor positions BEFORE alignment
                    Debug.Log($"[ColocationManager ShareAnchors (anchor)] HOST Pre-align Anchor1: {anchorsToShare[0].transform.position}");
                    Debug.Log($"[ColocationManager ShareAnchors (anchor)] HOST Pre-align Anchor2: {anchorsToShare[1].transform.position}");

                    alignmentManager.AlignUserToTwoAnchors(anchorsToShare[0], anchorsToShare[1]);

                    // Store anchor positions and mark alignment complete
                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    SecondAnchorPosition = anchorsToShare[1].transform.position;
                    AlignmentCompletedStatic = true;

                    // CRITICAL: Sort currentAnchors list to match the order we're sharing
                    SortAnchorsConsistently();

                    // Set state to HostAligned after alignment
                    currentState = ColocationState.HostAligned;

                    Debug.Log($"[ColocationManager ShareAnchors (anchor)] HOST Stored positions: Anchor1={FirstAnchorPosition}, Anchor2={SecondAnchorPosition}");
                }
                else
                {
                    Log("Host: Aligning to single anchor...");
                    _localizedAnchor = anchorsToShare[0];
                    alignmentManager.AlignUserToAnchor(anchorsToShare[0]);
                    
                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    AlignmentCompletedStatic = true;
                    
                    // Set state to HostAligned after alignment
                    currentState = ColocationState.HostAligned;
                }

                UpdateUIWizard();
            }
            else
            {
                Log("Sharing Error!", true);
                
                // Reset state to allow retry
                currentState = ColocationState.ShareFailed;
                UpdateUIWizard();
            }
        }
        catch (Exception e)
        {
            Log("Sharing Error!", true);
            Debug.LogError($"[ColocationManager ShareAnchors (anchor)] ShareAnchors exception: {e.Message}\n{e.StackTrace}");
            
            // Allow retry
            currentState = ColocationState.ShareFailed;
            UpdateUIWizard();
        }
    }

    protected virtual void UpdateUIWizard() { }
    protected virtual void UpdateAllUI() { }

    protected virtual void Log(string message, bool isError = false)
    {
        if (isError)
        {
            Debug.LogError($"[ColocationManager] {message}");
        }
        else
        {
            Debug.Log($"[ColocationManager] {message}");
        }
    }

    protected virtual async void DiscoverNearbySession()
    {
        try
        {
            // Ensure we're not already subscribed (prevents duplicate handlers)
            OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
            OVRColocationSession.ColocationSessionDiscovered += OnColocationSessionDiscovered;

            var discoveryResult = await OVRColocationSession.StartDiscoveryAsync();
            if (!discoveryResult.Success)
            {
                Log($" Discovery failed with status: {discoveryResult.Status}", true);
                return;
            }

            Log("Discovery started successfully.");
        }
        catch (Exception e)
        {
            Log($" Error during discovery: {e.Message}", true);
        }
    }

    protected virtual void OnColocationSessionDiscovered(OVRColocationSession.Data session)
    {
        // Stop discovery after finding a session to prevent duplicate callbacks
        OVRColocationSession.StopDiscoveryAsync();
        OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;

        _sharedAnchorGroupId = session.AdvertisementUuid;
        Log($"Discovered session with UUID: {_sharedAnchorGroupId}");
        LoadAndAlignToAnchor(_sharedAnchorGroupId);
    }

    protected virtual async void CreateAndShareAlignmentAnchor()
    {
       // Legacy method, kept for compatibility if called directly, but redirected to ShareAnchors
       ShareAnchors();
    }

    protected virtual async Task<OVRSpatialAnchor> CreateAnchor(Vector3 position, Quaternion rotation)
    {
        try
        {
            // Handle default position case
            if (position == Vector3.zero)
            {
                position = GetDefaultAnchorPosition();
                rotation = GetDefaultAnchorRotation();
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

            // Make anchor persist across scene transitions
            DontDestroyOnLoad(anchorGO);

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

    /// <summary>
    /// Get default anchor position when Vector3.zero is passed
    /// Can be overridden by derived classes
    /// </summary>
    protected virtual Vector3 GetDefaultAnchorPosition()
    {
        return Vector3.zero;
    }

    /// <summary>
    /// Get default anchor rotation when Vector3.zero position is passed
    /// Can be overridden by derived classes
    /// </summary>
    protected virtual Quaternion GetDefaultAnchorRotation()
    {
        return Quaternion.identity;
    }

    protected virtual async void LoadAndAlignToAnchor(Guid groupUuid)
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
                Log("TROUBLESHOUTING:", true);
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
                    
                    // Make anchor persist across scene transitions
                    DontDestroyOnLoad(anchorGameObject);
                    
                    // Wait a frame for transform to update after binding
                    await Task.Yield();
                    
                    // Log anchor position for debugging - compare with host positions
                    Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT Anchor {i + 1} position: {spatialAnchor.transform.position}, rotation: {spatialAnchor.transform.eulerAngles}");

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

                // CRITICAL: Sort anchors to match host's order using networked UUIDs
                OVRSpatialAnchor anchor1 = null;
                OVRSpatialAnchor anchor2 = null;

                string firstUuid = FirstAnchorUuidString.ToString();
                string secondUuid = SecondAnchorUuidString.ToString();

                if (!string.IsNullOrEmpty(firstUuid) && !string.IsNullOrEmpty(secondUuid))
                {
                    // Match anchors by UUID from host
                    foreach (var anchor in localizedAnchors)
                    {
                        string anchorUuid = anchor.Uuid.ToString();
                        if (anchorUuid == firstUuid)
                        {
                            anchor1 = anchor;
                            Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT matched First anchor: {anchorUuid.Substring(0, 8)}");
                        }
                        else if (anchorUuid == secondUuid)
                        {
                            anchor2 = anchor;
                            Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT matched Second anchor: {anchorUuid.Substring(0, 8)}");
                        }
                    }

                    if (anchor1 == null || anchor2 == null)
                    {
                        Log($"Warning: Could not match all anchors by UUID. First={firstUuid.Substring(0, 8)}, Second={secondUuid.Substring(0, 8)}", true);
                        // Fallback to order received
                        anchor1 = localizedAnchors[0];
                        anchor2 = localizedAnchors[1];
                    }
                }
                else
                {
                    Log("Warning: Host anchor UUIDs not received via network, using load order");
                    anchor1 = localizedAnchors[0];
                    anchor2 = localizedAnchors[1];
                }

                _clientLocalizedAnchorCount = localizedAnchors.Count;
                _localizedAnchor = anchor1; // Set primary as main

                // Log anchor positions BEFORE alignment for comparison with host
                Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT Pre-align Anchor1: {anchor1.transform.position}");
                Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT Pre-align Anchor2: {anchor2.transform.position}");

                alignmentManager.AlignUserToTwoAnchors(anchor1, anchor2);
                
                // Wait for alignment to complete before logging post-alignment positions
                await Task.Delay(1000);

                // Log anchor positions AFTER alignment - these should match host positions
                Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT Post-align Anchor1: {anchor1.transform.position}");
                Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT Post-align Anchor2: {anchor2.transform.position}");

                // Store anchor positions and mark alignment complete (using correctly ordered anchors)
                FirstAnchorPosition = anchor1.transform.position;
                SecondAnchorPosition = anchor2.transform.position;
                AlignmentCompletedStatic = true;

                // CRITICAL: Reorder currentAnchors list to match host's order
                SortAnchorsConsistently();

                Debug.Log($"[ColocationManager LoadAndAlignToAnchor (anchor)] CLIENT Stored positions: Anchor1={FirstAnchorPosition}, Anchor2={SecondAnchorPosition}");
                
                // Disable rediscovery - we have both anchors
                _discoveryStarted = false; // Prevent auto-retry
                _ = OVRColocationSession.StopDiscoveryAsync();
                Log("Client: 2 anchors localized - discovery disabled");
                
                // Notify host that client has aligned
                if (Runner != null && !Object.HasStateAuthority)
                {
                    Debug.Log("[ColocationManager LoadAndAlignToAnchor (network)] CLIENT sending RPC_NotifyClientAligned to host");
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

    /// <summary>
    /// Resets the colocation session by stopping advertisement/discovery
    /// </summary>
    protected virtual void ResetColocationSession()
    {
        try
        {
            // Unsubscribe from discovery events
            OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;

            // Stop any active advertisement or discovery
            OVRColocationSession.StopAdvertisementAsync();
            OVRColocationSession.StopDiscoveryAsync();

            _sharedAnchorGroupId = Guid.Empty;
            _localizedAnchor = null;
            
            Log("[ColocationManager ResetColocationSession] Session reset successfully");
        }
        catch (Exception e)
        {
            Log($"[ColocationManager ResetColocationSession] Error during session reset: {e.Message}", true);
        }
    }

    public void PlayerJoined(PlayerRef player)
    {
        Log($"[ColocationManager PlayerJoined] Player {player} joined.");
    }

    public void PlayerLeft(PlayerRef player)
    {
        Log($"[ColocationManager PlayerLeft] Player {player} left.");
    }

    // ==================== PUBLIC ACCESSORS FOR NetworkedCube ====================
    
    /// <summary>
    /// Get the PRIMARY anchor used for alignment.
    /// NetworkedCube and other objects should use this to ensure consistent positioning.
    /// </summary>
    public virtual OVRSpatialAnchor GetPrimaryAnchor()
    {
        if (_localizedAnchor != null && _localizedAnchor.Localized)
        {
            Debug.Log($"[ColocationManager] GetPrimaryAnchor returning: {_localizedAnchor.Uuid}, pos: {_localizedAnchor.transform.position}");
            return _localizedAnchor;
        }
        
        // Fallback: return first localized anchor from list
        if (currentAnchors != null && currentAnchors.Count > 0)
        {
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.Localized)
                {
                    Debug.Log($"[ColocationManager] GetPrimaryAnchor fallback to currentAnchors[0]: {anchor.Uuid}");
                    return anchor;
                }
            }
        }
        
        Debug.LogWarning("[ColocationManager] GetPrimaryAnchor: No localized anchor found!");
        return null;
    }
    
    /// <summary>
    /// Sort currentAnchors list to ensure consistent order across devices.
    /// Uses the networked UUIDs (FirstAnchorUuidString, SecondAnchorUuidString) to order anchors.
    /// If UUIDs not available, falls back to sorting by UUID string comparison.
    /// </summary>
    protected void SortAnchorsConsistently()
    {
        if (currentAnchors == null || currentAnchors.Count < 2)
        {
            Debug.Log("[ColocationManager] SortAnchorsConsistently: Not enough anchors to sort");
            return;
        }

#if FUSION2
        string firstUuid = FirstAnchorUuidString.ToString();
        string secondUuid = SecondAnchorUuidString.ToString();

        if (!string.IsNullOrEmpty(firstUuid) && !string.IsNullOrEmpty(secondUuid))
        {
            // Reorder based on networked UUIDs from host
            var sortedList = new List<OVRSpatialAnchor>();
            OVRSpatialAnchor anchor1 = null;
            OVRSpatialAnchor anchor2 = null;

            foreach (var anchor in currentAnchors)
            {
                if (anchor == null) continue;
                string anchorUuid = anchor.Uuid.ToString();
                if (anchorUuid == firstUuid)
                    anchor1 = anchor;
                else if (anchorUuid == secondUuid)
                    anchor2 = anchor;
            }

            if (anchor1 != null) sortedList.Add(anchor1);
            if (anchor2 != null) sortedList.Add(anchor2);

            // Add any remaining anchors that didn't match (shouldn't happen normally)
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && !sortedList.Contains(anchor))
                    sortedList.Add(anchor);
            }

            currentAnchors = sortedList;
            Debug.Log($"[ColocationManager] SortAnchorsConsistently: Reordered by networked UUIDs. First={firstUuid.Substring(0, 8)}, Second={secondUuid.Substring(0, 8)}");
            return;
        }
#endif

        // Fallback: Sort by UUID string comparison for consistent ordering
        currentAnchors.Sort((a, b) =>
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return string.Compare(a.Uuid.ToString(), b.Uuid.ToString(), StringComparison.Ordinal);
        });
        Debug.Log($"[ColocationManager] SortAnchorsConsistently: Sorted by UUID comparison. First={currentAnchors[0]?.Uuid.ToString().Substring(0, 8)}, Second={currentAnchors[1]?.Uuid.ToString().Substring(0, 8)}");
    }

    /// <summary>
    /// Get the list of all current anchors
    /// </summary>
    public virtual List<OVRSpatialAnchor> GetAnchors()
    {
        return currentAnchors;
    }
    
    /// <summary>
    /// Check if alignment has completed
    /// </summary>
    public virtual bool IsAlignmentComplete()
    {
        return AlignmentCompletedStatic;
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    protected virtual void RPC_NotifyClientAligned() { }
}
#endif