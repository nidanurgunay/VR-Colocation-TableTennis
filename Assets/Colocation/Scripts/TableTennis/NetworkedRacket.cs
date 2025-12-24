#if FUSION2

using Fusion;
using UnityEngine;

/// <summary>
/// Networked racket that syncs grab state and anchor-relative position.
/// Only syncs essential data to minimize network traffic.
/// Each player grabs with their controller, position syncs relative to shared anchor.
/// </summary>
public class NetworkedRacket : NetworkBehaviour
{
    [Header("Racket Settings")]
    [SerializeField] private Transform racketVisual; // The visual mesh of the racket
    [SerializeField] private float grabRadius = 0.15f; // How close to grab
    [SerializeField] private OVRInput.Button grabButton = OVRInput.Button.PrimaryHandTrigger;
    
    [Header("Ownership Colors")]
    [SerializeField] private Color ownedColor = Color.red;
    [SerializeField] private Color otherPlayerColor = Color.blue;
    [SerializeField] private Color ungrabbedColor = Color.gray;
    
    // Networked state - this is all we sync!
    [Networked] public NetworkBool IsGrabbed { get; set; }
    [Networked] public GrabHand GrabbingHand { get; set; }
    [Networked] public Vector3 AnchorRelativePosition { get; set; }
    [Networked] public Quaternion AnchorRelativeRotation { get; set; }
    [Networked] public PlayerRef OwnerPlayer { get; set; }
    
    public enum GrabHand { None, Left, Right }
    
    // Local state
    private OVRCameraRig cameraRig;
    private Transform leftController;
    private Transform rightController;
    private OVRSpatialAnchor localAnchor;
    private Renderer racketRenderer;
    private MaterialPropertyBlock propertyBlock;
    private bool isLocallyGrabbed = false;
    private GrabHand localGrabHand = GrabHand.None;
    
    // Sync settings
    private const float POSITION_SYNC_RATE = 0.05f; // Sync every 50ms (20Hz)
    private float lastSyncTime;

    public override void Spawned()
    {
        base.Spawned();
        
        // Find camera rig and controllers
        cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            leftController = cameraRig.leftControllerAnchor;
            rightController = cameraRig.rightControllerAnchor;
        }
        
        // Find anchor for relative positioning
        FindLocalAnchor();
        
        // Setup visuals
        if (racketVisual == null)
            racketVisual = transform;
            
        racketRenderer = racketVisual.GetComponent<Renderer>();
        if (racketRenderer == null)
            racketRenderer = racketVisual.GetComponentInChildren<Renderer>();
            
        propertyBlock = new MaterialPropertyBlock();
        
        UpdateVisuals();
        
        Debug.Log($"[NetworkedRacket] Spawned. HasStateAuth: {Object.HasStateAuthority}");
    }

    private void FindLocalAnchor()
    {
        // Try via GUIManager first
        var guiManager = FindObjectOfType<AnchorAutoGUIManager>();
        if (guiManager != null)
        {
            localAnchor = guiManager.GetLocalizedAnchor();
        }
        
        // Fallback: find any localized anchor
        if (localAnchor == null)
        {
            var allAnchors = FindObjectsOfType<OVRSpatialAnchor>();
            foreach (var anchor in allAnchors)
            {
                if (anchor.Localized)
                {
                    localAnchor = anchor;
                    break;
                }
            }
        }
        
        if (localAnchor != null)
        {
            Debug.Log($"[NetworkedRacket] Found local anchor: {localAnchor.name}");
        }
    }

    public override void FixedUpdateNetwork()
    {
        // Only the input authority (local player) handles grab input
        if (Object.HasInputAuthority)
        {
            HandleGrabInput();
            
            // If we're grabbing, update the networked position
            if (isLocallyGrabbed)
            {
                UpdateNetworkedPosition();
            }
        }
        
        // Everyone updates the visual position
        UpdateRacketPosition();
        UpdateVisuals();
    }

    private void HandleGrabInput()
    {
        bool leftGrabPressed = OVRInput.Get(grabButton, OVRInput.Controller.LTouch);
        bool rightGrabPressed = OVRInput.Get(grabButton, OVRInput.Controller.RTouch);
        
        // Check if we should grab
        if (!isLocallyGrabbed)
        {
            // Try to grab with either hand
            if (leftGrabPressed && IsControllerNearRacket(leftController))
            {
                TryGrab(GrabHand.Left);
            }
            else if (rightGrabPressed && IsControllerNearRacket(rightController))
            {
                TryGrab(GrabHand.Right);
            }
        }
        else
        {
            // Check if we should release
            bool shouldRelease = (localGrabHand == GrabHand.Left && !leftGrabPressed) ||
                                (localGrabHand == GrabHand.Right && !rightGrabPressed);
            
            if (shouldRelease)
            {
                Release();
            }
        }
    }

    private bool IsControllerNearRacket(Transform controller)
    {
        if (controller == null) return false;
        
        float distance = Vector3.Distance(controller.position, racketVisual.position);
        return distance <= grabRadius;
    }

    private void TryGrab(GrabHand hand)
    {
        // Request authority if we don't have it
        if (!Object.HasStateAuthority)
        {
            RPC_RequestGrab(hand);
        }
        else
        {
            PerformGrab(hand, Runner.LocalPlayer);
        }
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void RPC_RequestGrab(GrabHand hand, RpcInfo info = default)
    {
        // Host validates and performs the grab
        if (!IsGrabbed) // Only if not already grabbed
        {
            PerformGrab(hand, info.Source);
            Debug.Log($"[NetworkedRacket] Player {info.Source} grabbed with {hand}");
        }
    }

    private void PerformGrab(GrabHand hand, PlayerRef player)
    {
        IsGrabbed = true;
        GrabbingHand = hand;
        OwnerPlayer = player;
        
        // Set local state if this is us
        if (Runner.LocalPlayer == player)
        {
            isLocallyGrabbed = true;
            localGrabHand = hand;
        }
    }

    private void Release()
    {
        if (Object.HasStateAuthority)
        {
            PerformRelease();
        }
        else
        {
            RPC_RequestRelease();
        }
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void RPC_RequestRelease()
    {
        PerformRelease();
    }

    private void PerformRelease()
    {
        IsGrabbed = false;
        GrabbingHand = GrabHand.None;
        OwnerPlayer = PlayerRef.None;
        
        isLocallyGrabbed = false;
        localGrabHand = GrabHand.None;
        
        Debug.Log("[NetworkedRacket] Released");
    }

    private void UpdateNetworkedPosition()
    {
        // Throttle sync rate
        if (Time.time - lastSyncTime < POSITION_SYNC_RATE) return;
        lastSyncTime = Time.time;
        
        Transform controller = (localGrabHand == GrabHand.Left) ? leftController : rightController;
        if (controller == null || localAnchor == null) return;
        
        // Convert to anchor-relative position
        if (Object.HasStateAuthority)
        {
            AnchorRelativePosition = localAnchor.transform.InverseTransformPoint(controller.position);
            AnchorRelativeRotation = Quaternion.Inverse(localAnchor.transform.rotation) * controller.rotation;
        }
        else
        {
            // If not authority, request position update via RPC
            Vector3 relPos = localAnchor.transform.InverseTransformPoint(controller.position);
            Quaternion relRot = Quaternion.Inverse(localAnchor.transform.rotation) * controller.rotation;
            RPC_UpdatePosition(relPos, relRot);
        }
    }

    [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]
    private void RPC_UpdatePosition(Vector3 relativePos, Quaternion relativeRot)
    {
        AnchorRelativePosition = relativePos;
        AnchorRelativeRotation = relativeRot;
    }

    private void UpdateRacketPosition()
    {
        if (!IsGrabbed)
        {
            // Not grabbed - could place at a rest position or leave as-is
            return;
        }
        
        // If we're the one grabbing, use direct controller tracking (smoother)
        if (isLocallyGrabbed && Runner.LocalPlayer == OwnerPlayer)
        {
            Transform controller = (localGrabHand == GrabHand.Left) ? leftController : rightController;
            if (controller != null)
            {
                racketVisual.position = controller.position;
                racketVisual.rotation = controller.rotation;
            }
        }
        else
        {
            // We're seeing another player's racket - use networked anchor-relative position
            if (localAnchor != null)
            {
                racketVisual.position = localAnchor.transform.TransformPoint(AnchorRelativePosition);
                racketVisual.rotation = localAnchor.transform.rotation * AnchorRelativeRotation;
            }
        }
    }

    private void UpdateVisuals()
    {
        if (racketRenderer == null || propertyBlock == null) return;
        
        Color targetColor;
        
        if (!IsGrabbed)
        {
            targetColor = ungrabbedColor;
        }
        else if (Runner.LocalPlayer == OwnerPlayer)
        {
            targetColor = ownedColor; // Our racket
        }
        else
        {
            targetColor = otherPlayerColor; // Other player's racket
        }
        
        racketRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor("_Color", targetColor);
        propertyBlock.SetColor("_BaseColor", targetColor);
        racketRenderer.SetPropertyBlock(propertyBlock);
    }

    private void OnDrawGizmosSelected()
    {
        // Show grab radius
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, grabRadius);
    }
}

#endif
