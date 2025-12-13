#if FUSION2

using Fusion;
using UnityEngine;

/// <summary>
/// Bridges OVRGrabbable with NetworkedCube for networked grabbing.
/// Attach this to the same GameObject as OVRGrabbable and NetworkedCube.
/// </summary>
[RequireComponent(typeof(OVRGrabbable))]
[RequireComponent(typeof(NetworkedCube))]
public class NetworkedCubeGrabber : MonoBehaviour
{
    private OVRGrabbable ovrGrabbable;
    private NetworkedCube networkedCube;
    private NetworkRunner runner;
    private bool wasGrabbed;

    private void Awake()
    {
        ovrGrabbable = GetComponent<OVRGrabbable>();
        networkedCube = GetComponent<NetworkedCube>();
    }

    private void Start()
    {
        runner = FindObjectOfType<NetworkRunner>();
    }

    private void Update()
    {
        if (ovrGrabbable == null || networkedCube == null || runner == null)
            return;

        bool isCurrentlyGrabbed = ovrGrabbable.isGrabbed;

        // Detect grab state change
        if (isCurrentlyGrabbed && !wasGrabbed)
        {
            // Just grabbed
            OnLocalGrabbed();
        }
        else if (!isCurrentlyGrabbed && wasGrabbed)
        {
            // Just released
            OnLocalReleased();
        }

        // If we're grabbing, sync position to hand
        if (isCurrentlyGrabbed && networkedCube.IsGrabbed)
        {
            // Position is automatically updated by OVRGrabbable
            // NetworkTransform will sync it across network
        }

        wasGrabbed = isCurrentlyGrabbed;
    }

    private void OnLocalGrabbed()
    {
        if (runner == null || !runner.IsRunning)
            return;

        PlayerRef localPlayer = runner.LocalPlayer;
        
        Debug.Log($"[NetworkedCubeGrabber] Local player {localPlayer} grabbed cube");
        
        // Request authority so we can control the cube
        networkedCube.RequestAuthority();
        
        // Notify the networked cube
        networkedCube.OnGrabbed(localPlayer);
    }

    private void OnLocalReleased()
    {
        Debug.Log("[NetworkedCubeGrabber] Local player released cube");
        
        // Notify the networked cube
        networkedCube.OnReleased();
    }
}

#endif
