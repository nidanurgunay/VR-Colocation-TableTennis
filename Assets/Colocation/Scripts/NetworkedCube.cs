#if FUSION2

using Fusion;
using UnityEngine;

/// <summary>
/// Networked cube that can be grabbed and manipulated by multiple headsets.
/// Synchronizes position, rotation, and grab state across all clients.
/// </summary>
[RequireComponent(typeof(NetworkTransform))]
public class NetworkedCube : NetworkBehaviour
{
    [Header("Visual Settings")]
    [SerializeField] private Color defaultColor = Color.cyan;
    [SerializeField] private Color grabbedColor = Color.yellow;

    [Header("Physics Settings")]
    [SerializeField] private bool useGravity = false;

    [Networked] public NetworkBool IsGrabbed { get; set; }
    [Networked] public PlayerRef GrabbingPlayer { get; set; }

    private Renderer cubeRenderer;
    private Rigidbody rigidBody;
    private MaterialPropertyBlock propertyBlock;
    private bool previousGrabState;

    public override void Spawned()
    {
        base.Spawned();

        cubeRenderer = GetComponent<Renderer>();
        if (cubeRenderer == null)
        {
            cubeRenderer = GetComponentInChildren<Renderer>();
        }

        rigidBody = GetComponent<Rigidbody>();
        if (rigidBody != null)
        {
            rigidBody.useGravity = useGravity;
            rigidBody.isKinematic = true; // Start kinematic, becomes dynamic when thrown
        }

        propertyBlock = new MaterialPropertyBlock();
        previousGrabState = IsGrabbed;
        UpdateVisualState();

        Debug.Log($"[NetworkedCube] Spawned with Id: {Object.Id}");
    }

    public override void FixedUpdateNetwork()
    {
        // Empty - visual updates are handled in Render() for smooth visuals
    }

    public override void Render()
    {
        // Only update visual state when grab state has changed (optimization)
        if (previousGrabState != IsGrabbed)
        {
            previousGrabState = IsGrabbed;
            UpdateVisualState();
        }
    }

    /// <summary>
    /// Called when a player starts grabbing this cube.
    /// </summary>
    public void OnGrabbed(PlayerRef player)
    {
        if (!Object.HasStateAuthority)
        {
            RPC_RequestGrab(player);
            return;
        }

        SetGrabState(true, player);
    }

    /// <summary>
    /// Called when a player releases this cube.
    /// </summary>
    public void OnReleased()
    {
        if (!Object.HasStateAuthority)
        {
            RPC_RequestRelease();
            return;
        }

        SetGrabState(false, PlayerRef.None);
    }

    /// <summary>
    /// Request authority transfer to allow another player to manipulate the cube.
    /// </summary>
    public void RequestAuthority()
    {
        if (Runner == null) return;

        if (!Object.HasStateAuthority)
        {
            Object.RequestStateAuthority();
            Debug.Log($"[NetworkedCube] Requested state authority for cube {Object.Id}");
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestGrab(PlayerRef player)
    {
        SetGrabState(true, player);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRelease()
    {
        SetGrabState(false, PlayerRef.None);
    }

    private void SetGrabState(bool grabbed, PlayerRef player)
    {
        IsGrabbed = grabbed;
        GrabbingPlayer = player;

        if (rigidBody != null)
        {
            // When grabbed, cube follows hand movement (kinematic)
            // When released, cube becomes dynamic for physics (unless useGravity is false)
            rigidBody.isKinematic = grabbed || !useGravity;
        }

        // Update visual state when grab state changes
        UpdateVisualState();

        Debug.Log($"[NetworkedCube] Grab state changed - IsGrabbed: {grabbed}, Player: {player}");
    }

    private void UpdateVisualState()
    {
        if (cubeRenderer == null || propertyBlock == null) return;

        cubeRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor("_Color", IsGrabbed ? grabbedColor : defaultColor);
        propertyBlock.SetColor("_BaseColor", IsGrabbed ? grabbedColor : defaultColor);
        cubeRenderer.SetPropertyBlock(propertyBlock);
    }

    private void OnDestroy()
    {
        Debug.Log($"[NetworkedCube] Destroyed cube {(Object != null ? Object.Id.ToString() : "unknown")}");
    }
}

#endif
