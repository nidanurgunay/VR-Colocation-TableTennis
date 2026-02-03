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
            AdvertiseColocationSession();
        }
        else
        {
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
                ShareAnchors();
            }
            else
            {
                Debug.LogError($"[ColocationManager] Advertisement failed: {startAdvertisementResult.Status}");
                currentState = ColocationState.ShareFailed;
                UpdateUIWizard();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColocationManager] Advertisement error: {e.Message}");
            currentState = ColocationState.ShareFailed;
            UpdateUIWizard();
        }
    }

    // Renamed and refactored to share EXISTING anchors if available, or create one if none
    protected virtual async void ShareAnchors()
    {
        try
        {
           

            // Check if we have a valid group UUID
            if (_sharedAnchorGroupId == Guid.Empty)
            {
                Debug.LogError("[ColocationManager] Group UUID empty - restarting advertisement");
                AdvertiseColocationSession();
                return;
            }

            // Wait for anchors to stabilize
            await Task.Delay(3000);

            // Count localized anchors
            int localizedCount = 0;
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.Localized)
                    localizedCount++;
            }

            if (localizedCount < currentAnchors.Count)
            {
                await Task.Delay(5000);
                localizedCount = 0;
                foreach (var anchor in currentAnchors)
                {
                    if (anchor != null && anchor.Localized)
                        localizedCount++;
                }
            }

            if (localizedCount == 0)
            {
                Debug.LogError("[ColocationManager] No anchors localized - cannot share");
                currentState = ColocationState.ShareFailed;
                UpdateUIWizard();
                return;
            }

            // Save all anchors
            var anchorsToShare = new List<OVRSpatialAnchor>();
            foreach (var anchor in currentAnchors)
            {
                 if (anchor != null && anchor.Localized)
                 {
                     var saveResult = await anchor.SaveAnchorAsync();
                     if (!saveResult.Success)
                     {
                         if (saveResult.Status.ToString().Contains("Pending"))
                         {
                             await Task.Delay(2000);
                             saveResult = await anchor.SaveAnchorAsync();
                             if (saveResult.Success)
                                 anchorsToShare.Add(anchor);
                         }
                     }
                     else
                     {
                         anchorsToShare.Add(anchor);
                     }
                 }
            }

            if (anchorsToShare.Count == 0)
            {
                Debug.LogError("[ColocationManager] No anchors saved - sharing failed");
                currentState = ColocationState.ShareFailed;
                UpdateUIWizard();
                return;
            }

            
            var shareResult = await OVRSpatialAnchor.ShareAsync(anchorsToShare, _sharedAnchorGroupId);

            if (shareResult.Success)
            {
                currentState = ColocationState.SharingAnchors; // Host is sharing, not aligned until client joins

                // IMPORTANT: Set networked flag so client knows anchors are ready
                HostAnchorsShared = true;
                SharedAnchorGroupUuidString = _sharedAnchorGroupId.ToString();

                // HOST ALIGNMENT
                if (anchorsToShare.Count >= 2)
                {
                    _localizedAnchor = anchorsToShare[0];
                    FirstAnchorUuidString = anchorsToShare[0].Uuid.ToString();
                    SecondAnchorUuidString = anchorsToShare[1].Uuid.ToString();

                    alignmentManager.AlignUserToTwoAnchors(anchorsToShare[0], anchorsToShare[1]);

                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    SecondAnchorPosition = anchorsToShare[1].transform.position;
                    AlignmentCompletedStatic = true;
                    SortAnchorsConsistently();
                    currentState = ColocationState.HostAligned;

                    Debug.Log($"[ColocationManager] HOST aligned with 2 anchors");
                }
                else
                {
                    _localizedAnchor = anchorsToShare[0];
                    alignmentManager.AlignUserToAnchor(anchorsToShare[0]);
                    FirstAnchorPosition = anchorsToShare[0].transform.position;
                    AlignmentCompletedStatic = true;
                    currentState = ColocationState.HostAligned;
                }

                UpdateUIWizard();
            }
            else
            {
                Debug.LogError("[ColocationManager] Anchor sharing failed");
                currentState = ColocationState.ShareFailed;
                UpdateUIWizard();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColocationManager] ShareAnchors error: {e.Message}");
            currentState = ColocationState.ShareFailed;
            UpdateUIWizard();
        }
    }

    protected virtual void UpdateUIWizard() { }
    protected virtual void UpdateAllUI() { }



    protected virtual async void DiscoverNearbySession()
    {
        try
        {
            OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
            OVRColocationSession.ColocationSessionDiscovered += OnColocationSessionDiscovered;

            var discoveryResult = await OVRColocationSession.StartDiscoveryAsync();
            if (!discoveryResult.Success)
            {
                Debug.LogError($"[ColocationManager] Discovery failed: {discoveryResult.Status}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColocationManager] Discovery error: {e.Message}");
        }
    }

    protected virtual void OnColocationSessionDiscovered(OVRColocationSession.Data session)
    {
        OVRColocationSession.StopDiscoveryAsync();
        OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;

        _sharedAnchorGroupId = session.AdvertisementUuid;
        Debug.Log($"[ColocationManager] Session discovered - loading anchors");
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
                Debug.LogError("[ColocationManager] Anchor creation timed out");
                Destroy(anchorGO);
                return null;
            }

            // Wait for anchor to be LOCALIZED (required for save/share)
            timeout = 300;
            while (!spatialAnchor.Localized && timeout > 0)
            {
                await Task.Delay(50);
                timeout--;
            }

            if (!spatialAnchor.Localized)
            {
                Debug.LogError("[ColocationManager] Anchor localization timed out");
            }

            // Make anchor persist across scene transitions
            DontDestroyOnLoad(anchorGO);

            currentAnchors.Add(spatialAnchor); // Track it locally for UI
            _localizedAnchor = spatialAnchor; // Store as the shared anchor for relative positioning
            return spatialAnchor;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColocationManager] Anchor creation error: {e.Message}");
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
            if (statusText != null)
                statusText.text = "Session found!\nLoading anchors...";

            // Wait for anchors to propagate through Meta's cloud
            await Task.Delay(8000);

            // Retry loop with delays between attempts
            var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            bool loadSuccess = false;
            int retryCount = 0;
            const int MAX_RETRIES = 8;

            while (retryCount < MAX_RETRIES)
            {
                unboundAnchors.Clear();

                if (statusText != null)
                    statusText.text = $"Loading anchors...\nAttempt {retryCount + 1}/{MAX_RETRIES}";

                var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);

                if (loadResult.Success && unboundAnchors.Count > 0)
                {
                    if (statusText != null)
                        statusText.text = $"Found {unboundAnchors.Count} anchors!\nLocalizing...";
                    loadSuccess = true;
                    break;
                }

                retryCount++;
                if (retryCount < MAX_RETRIES)
                {
                    if (statusText != null)
                        statusText.text = $"Loading anchors...\nAttempt {retryCount}/{MAX_RETRIES}";
                    await Task.Delay(8000);
                }
            }

            if (!loadSuccess || unboundAnchors.Count == 0)
            {
                Debug.LogError($"[ColocationManager] Failed to load anchors after {MAX_RETRIES} retries");

                if (statusText != null)
                    statusText.text = "Failed to load anchors!\n\nHost must:\n1. Place 2 anchors (Grip)\n2. Wait for 'Shared' message";

                _discoveryStarted = false;
                _lastDiscoveryTime = 0f;
                return;
            }

            // Localize ALL found anchors
            var localizedAnchors = new List<OVRSpatialAnchor>();

            for (int i = 0; i < unboundAnchors.Count; i++)
            {
                var unboundAnchor = unboundAnchors[i];

                var localizeTask = unboundAnchor.LocalizeAsync();
                int timeoutMs = 30000;
                int elapsed = 0;

                while (!localizeTask.IsCompleted && elapsed < timeoutMs)
                {
                    await Task.Delay(500);
                    elapsed += 500;
                }

                if (await localizeTask)
                {
                    var anchorGameObject = new GameObject($"Anchor_{unboundAnchor.Uuid.ToString().Substring(0, 8)}");
                    var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor.BindTo(spatialAnchor);

                    DontDestroyOnLoad(anchorGameObject);
                    await Task.Yield();

                    if (anchorMarkerPrefab != null)
                    {
                        GameObject visual = Instantiate(anchorMarkerPrefab, anchorGameObject.transform);
                        visual.name = "Visual";
                        visual.transform.localPosition = Vector3.zero;
                        visual.transform.localRotation = Quaternion.identity;
                        float validScale = Mathf.Max(anchorScale, 0.01f);
                        visual.transform.localScale = Vector3.one * validScale;

                        foreach (var col in visual.GetComponentsInChildren<Collider>())
                            Destroy(col);
                        foreach (var rb in visual.GetComponentsInChildren<Rigidbody>())
                            Destroy(rb);
                    }

                    localizedAnchors.Add(spatialAnchor);
                    currentAnchors.Add(spatialAnchor);
                }
                else
                {
                    Debug.LogError($"[ColocationManager] Failed to localize anchor {i + 1}");
                }
            }

            // Now Align based on what we found
            if (localizedAnchors.Count >= 2)
            {
                OVRSpatialAnchor anchor1 = null;
                OVRSpatialAnchor anchor2 = null;

                string firstUuid = FirstAnchorUuidString.ToString();
                string secondUuid = SecondAnchorUuidString.ToString();

                if (!string.IsNullOrEmpty(firstUuid) && !string.IsNullOrEmpty(secondUuid))
                {
                    foreach (var anchor in localizedAnchors)
                    {
                        string anchorUuid = anchor.Uuid.ToString();
                        if (anchorUuid == firstUuid)
                            anchor1 = anchor;
                        else if (anchorUuid == secondUuid)
                            anchor2 = anchor;
                    }

                    if (anchor1 == null || anchor2 == null)
                    {
                        anchor1 = localizedAnchors[0];
                        anchor2 = localizedAnchors[1];
                    }
                }
                else
                {
                    anchor1 = localizedAnchors[0];
                    anchor2 = localizedAnchors[1];
                }

                _clientLocalizedAnchorCount = localizedAnchors.Count;
                _localizedAnchor = anchor1;

                alignmentManager.AlignUserToTwoAnchors(anchor1, anchor2);
                await Task.Delay(1000);

                FirstAnchorPosition = anchor1.transform.position;
                SecondAnchorPosition = anchor2.transform.position;
                AlignmentCompletedStatic = true;
                SortAnchorsConsistently();

                _discoveryStarted = false;
                _ = OVRColocationSession.StopDiscoveryAsync();

                if (Runner != null && !Object.HasStateAuthority)
                    RPC_NotifyClientAligned();

                if (statusText != null)
                    statusText.text = "Aligned\n2 anchors found\n\nReady to play";

                Debug.Log("[ColocationManager] CLIENT aligned with 2 anchors");
                UpdateUIWizard();
            }
            else if (localizedAnchors.Count == 1)
            {
                _clientLocalizedAnchorCount = 1;
                _localizedAnchor = localizedAnchors[0];
                alignmentManager.AlignUserToAnchor(localizedAnchors[0]);

                FirstAnchorPosition = localizedAnchors[0].transform.position;
                AlignmentCompletedStatic = true;

                if (Runner != null && !Object.HasStateAuthority)
                    RPC_NotifyClientAligned();

                if (statusText != null)
                    statusText.text = "Aligned\n1 anchor found\n\nReady to play";

                Debug.Log("[ColocationManager] CLIENT aligned with 1 anchor");
                UpdateUIWizard();
            }
            else
            {
                Debug.LogError("[ColocationManager] No anchors localized - cannot align");

                if (statusText != null)
                    statusText.text = "Failed to localize\n\nWalk around slowly\nnear host's location";
            }

            UpdateAllUI();
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColocationManager] Anchor loading error: {e.Message}");
        }
    }

    /// <summary>
    /// Resets the colocation session by stopping advertisement/discovery
    /// </summary>
    protected virtual void ResetColocationSession()
    {
        try
        {
            OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;
            OVRColocationSession.StopAdvertisementAsync();
            OVRColocationSession.StopDiscoveryAsync();
            _sharedAnchorGroupId = Guid.Empty;
            _localizedAnchor = null;
        }
        catch (Exception e)
        {
            Debug.LogError($"[ColocationManager] Session reset error: {e.Message}");
        }
    }

    public void PlayerJoined(PlayerRef player) { }
    public void PlayerLeft(PlayerRef player) { }

    // ==================== PUBLIC ACCESSORS FOR NetworkedCube ====================
    
    /// <summary>
    /// Get the PRIMARY anchor used for alignment.
    /// NetworkedCube and other objects should use this to ensure consistent positioning.
    /// </summary>
    public virtual OVRSpatialAnchor GetPrimaryAnchor()
    {
        if (_localizedAnchor != null && _localizedAnchor.Localized)
            return _localizedAnchor;

        if (currentAnchors != null && currentAnchors.Count > 0)
        {
            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && anchor.Localized)
                    return anchor;
            }
        }

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
            return;

#if FUSION2
        string firstUuid = FirstAnchorUuidString.ToString();
        string secondUuid = SecondAnchorUuidString.ToString();

        if (!string.IsNullOrEmpty(firstUuid) && !string.IsNullOrEmpty(secondUuid))
        {
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

            foreach (var anchor in currentAnchors)
            {
                if (anchor != null && !sortedList.Contains(anchor))
                    sortedList.Add(anchor);
            }

            currentAnchors = sortedList;
            return;
        }
#endif

        currentAnchors.Sort((a, b) =>
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return string.Compare(a.Uuid.ToString(), b.Uuid.ToString(), StringComparison.Ordinal);
        });
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