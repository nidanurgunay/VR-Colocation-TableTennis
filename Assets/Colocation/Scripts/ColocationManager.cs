#if FUSION2

using Fusion;
using System;
using System.Text;
using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Generic;

public class ColocationManager : NetworkBehaviour
{
    [SerializeField] protected AlignmentManager alignmentManager;
    [SerializeField] protected bool autoStartColocation = false;

    protected Guid _sharedAnchorGroupId;
    protected OVRSpatialAnchor _localizedAnchor; // The anchor used for alignment (host-created or client-localized)

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
                CreateAndShareAlignmentAnchor();
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

    protected virtual async void DiscoverNearbySession()
    {
        try
        {
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
        OVRColocationSession.ColocationSessionDiscovered -= OnColocationSessionDiscovered;

        _sharedAnchorGroupId = session.AdvertisementUuid;
        Log($"Colocation: Discovered session with UUID: {_sharedAnchorGroupId}");
        LoadAndAlignToAnchor(_sharedAnchorGroupId);
    }

    protected virtual async void CreateAndShareAlignmentAnchor()
    {
        try
        {
            Log("Colocation: Creating alignment anchor...");
            var anchor = await CreateAnchor(Vector3.zero, Quaternion.identity);

            if (anchor == null)
            {
                Log("Colocation: Failed to create alignment anchor.", true);
                return;
            }

            if (!anchor.Localized)
            {
                Log("Colocation: Anchor is not localized. Cannot proceed with sharing.", true);
                return;
            }

            var saveResult = await anchor.SaveAnchorAsync();
            if (!saveResult.Success)
            {
                Log($"Colocation: Failed to save alignment anchor. Error: {saveResult}", true);
                return;
            }

            Log($"Colocation: Alignment anchor saved successfully. UUID: {anchor.Uuid}");
            
            var shareResult = await OVRSpatialAnchor.ShareAsync(new List<OVRSpatialAnchor> { anchor }, _sharedAnchorGroupId);

            if (!shareResult.Success)
            {
                Log($"Colocation: Failed to share alignment anchor. Error: {shareResult}", true);
                return;
            }

            Log($"Colocation: Alignment anchor shared successfully. Group UUID: {_sharedAnchorGroupId}");
        }
        catch (Exception e)
        {
            Log($"Colocation: Error during anchor creation and sharing: {e.Message}", true);
        }
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
                    Log($"Colocation: Anchor localized successfully. UUID: {unboundAnchor.Uuid}");

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