#if FUSION2

using Fusion;
using System;
using System.Text;
using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Generic;

public class ColocationManager : NetworkBehaviour, IPlayerJoined, IPlayerLeft
{
    [SerializeField] protected AlignmentManager alignmentManager;
    [SerializeField] protected bool autoStartColocation = false;
    [SerializeField] private NetworkPrefabRef networkedPlayerPrefab; // Avatar for players

    protected Guid _sharedAnchorGroupId;
    protected OVRSpatialAnchor _localizedAnchor; // The anchor used for alignment (host-created or client-localized)
    
    // Track spawned avatars
    private Dictionary<PlayerRef, NetworkObject> _spawnedPlayerAvatars = new Dictionary<PlayerRef, NetworkObject>();

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
            Log("Colocation: Starting advertisement...");
            AdvertiseColocationSession();
        }
        else
        {
            Log("Colocation: Starting discovery...");
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
                Log($"Colocation: Advertisement started successfully. UUID: {_sharedAnchorGroupId}");
                ShareAnchors(); // Changed from CreateAndShareAlignmentAnchor
            }
            else
            {
                Log($"Colocation: Advertisement failed with status: {startAdvertisementResult.Status}", true);
            }
        }
        catch (Exception e)
        {
            Log($"Colocation: Error during advertisement: {e.Message}", true);
        }
    }

    // Renamed and refactored to share EXISTING anchors if available, or create one if none
    protected virtual async void ShareAnchors()
    {
        try
        {
             List<OVRSpatialAnchor> anchorsToShare = new List<OVRSpatialAnchor>();

             // If this class has access to currentAnchors (it doesn't in base class), we need a virtual accessor or pass it in.
             // But Wait! ColocationManager doesn't have 'currentAnchors' list. AnchorGUIManager does.
             // We need to override this method in AnchorGUIManager OR make it virtual and robust.
             
             // Base implementation: Create one and share it (legacy behavior)
             Log("Colocation: Creating default alignment anchor...");
             var anchor = await CreateAnchor(Vector3.zero, Quaternion.identity);
             if (anchor != null && anchor.Localized)
             {
                 var saveResult = await anchor.SaveAnchorAsync();
                 if (saveResult.Success)
                 {
                     var shareResult = await OVRSpatialAnchor.ShareAsync(new List<OVRSpatialAnchor> { anchor }, _sharedAnchorGroupId);
                     if (shareResult.Success)
                     {
                         Log($"Colocation: Host aligned! Anchor shared. Group UUID: {_sharedAnchorGroupId}");
                         _localizedAnchor = anchor;
                         if (alignmentManager != null) alignmentManager.AlignUserToAnchor(anchor);
                     }
                 }
             }
        }
        catch (Exception e)
        {
             Log($"Error share: {e.Message}", true);
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
                Log($"Colocation: Discovery failed with status: {discoveryResult.Status}", true);
                return;
            }

            Log("Colocation: Discovery started successfully.");
        }
        catch (Exception e)
        {
            Log($"Colocation: Error during discovery: {e.Message}", true);
        }
    }

    protected virtual void OnColocationSessionDiscovered(OVRColocationSession.Data session)
    {
        // Stop discovery after finding a session to prevent duplicate callbacks
        OVRColocationSession.StopDiscoveryAsync();
        OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;

        _sharedAnchorGroupId = session.AdvertisementUuid;
        Log($"Colocation: Discovered session with UUID: {_sharedAnchorGroupId}");
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
            var anchorGameObject = new GameObject("Alignment Anchor")
            {
                transform =
                {
                    position = position,
                    rotation = rotation
                }
            };

            var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
            
            while (!spatialAnchor.Created)
            {
                await Task.Yield();
            }

            Log($"Colocation: Anchor created successfully. UUID: {spatialAnchor.Uuid}");
            return spatialAnchor;
        }
        catch (Exception e)
        {
            Log($"Colocation: Error during anchor creation: {e.Message}", true);
            return null;
        }
    }

    protected virtual async void LoadAndAlignToAnchor(Guid groupUuid)
    {
        try
        {
            Log($"Colocation: Loading anchors for Group UUID: {groupUuid}...");

            var unboundAnchors = new List<OVRSpatialAnchor.UnboundAnchor>();
            var loadResult = await OVRSpatialAnchor.LoadUnboundSharedAnchorsAsync(groupUuid, unboundAnchors);

            if (!loadResult.Success || unboundAnchors.Count == 0)
            {
                Log($"Colocation: Failed to load anchors. Success: {loadResult.Success}, Count: {unboundAnchors.Count}", true);
                return;
            }

            foreach (var unboundAnchor in unboundAnchors)
            {
                if (await unboundAnchor.LocalizeAsync())
                {
                    Log($"Colocation: Client aligned! Anchor UUID: {unboundAnchor.Uuid}");

                    var anchorGameObject = new GameObject($"Anchor_{unboundAnchor.Uuid}");
                    var spatialAnchor = anchorGameObject.AddComponent<OVRSpatialAnchor>();
                    unboundAnchor.BindTo(spatialAnchor);

                    _localizedAnchor = spatialAnchor; // Store for relative positioning
                    alignmentManager.AlignUserToAnchor(spatialAnchor);
                    return;
                }

                Log($"Colocation: Failed to localize anchor: {unboundAnchor.Uuid}", true);
            }
        }
        catch (Exception e)
        {
            Log($"Colocation: Error during anchor loading and alignment: {e.Message}", true);
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
            
            Log("Colocation: Session reset successfully");
        }
        catch (Exception e)
        {
            Log($"Colocation: Error during session reset: {e.Message}", true);
        }
    }

    public void PlayerJoined(PlayerRef player)
    {
        if (Runner.IsServer && networkedPlayerPrefab != default)
        {
            Log($"[ColocationManager] Player {player} joined. Spawning avatar...");
            // Host spawns the avatar object, assigning Input Authority to the specific player
            NetworkObject playerObj = Runner.Spawn(networkedPlayerPrefab, Vector3.zero, Quaternion.identity, player);
            
            if (playerObj != null)
            {
                _spawnedPlayerAvatars[player] = playerObj;
                Log($"[ColocationManager] Avatar spawned for Player {player}");
            }
        }
    }

    public void PlayerLeft(PlayerRef player)
    {
        if (Runner.IsServer)
        {
            Log($"[ColocationManager] Player {player} left. Despawning avatar.");
            if (_spawnedPlayerAvatars.TryGetValue(player, out var playerObj))
            {
                Runner.Despawn(playerObj);
                _spawnedPlayerAvatars.Remove(player);
            }
        }
    }

    protected virtual void Log(string message, bool isError = false)
    {
        if (isError)
        {
            Debug.LogError(message);
        }
        else
        {
            Debug.Log(message);
        }
    }
}
#endif