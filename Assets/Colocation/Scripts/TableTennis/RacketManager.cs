#if FUSION2

using Fusion;
using UnityEngine;

/// <summary>
/// Manages both players' rackets in the table tennis game.
/// Each player has their own racket that follows their controller.
/// Only grab state is networked - position comes from local controller tracking + anchor alignment.
/// </summary>
public class RacketManager : NetworkBehaviour
{
    [Header("Racket Prefabs")]
    [SerializeField] private GameObject racketPrefab; // Visual racket prefab (not networked)
    
    [Header("Grab Settings")]
    [SerializeField] private OVRInput.Button grabButton = OVRInput.Button.PrimaryHandTrigger;
    [SerializeField] private float grabDistance = 0.3f; // How close to grab
    
    [Header("Player Colors")]
    [SerializeField] private Color player1Color = Color.red;
    [SerializeField] private Color player2Color = Color.blue;
    
    // Networked grab state - minimal data!
    [Networked] public NetworkBool Player1HasRacket { get; set; }
    [Networked] public GrabHand Player1Hand { get; set; }
    [Networked] public NetworkBool Player2HasRacket { get; set; }
    [Networked] public GrabHand Player2Hand { get; set; }
    
    // For other player's racket position (anchor-relative)
    [Networked] public Vector3 Player1RacketPos { get; set; }
    [Networked] public Quaternion Player1RacketRot { get; set; }
    [Networked] public Vector3 Player2RacketPos { get; set; }
    [Networked] public Quaternion Player2RacketRot { get; set; }
    
    public enum GrabHand { None = 0, Left = 1, Right = 2 }
    
    // Local references
    private OVRCameraRig cameraRig;
    private Transform leftController;
    private Transform rightController;
    private OVRSpatialAnchor localAnchor;
    
    // Local racket instances
    private GameObject myRacket;
    private GameObject otherPlayerRacket;
    private Renderer myRacketRenderer;
    private Renderer otherRacketRenderer;
    
    // Local grab state
    private bool amIPlayer1;
    private bool isGrabbing = false;
    private GrabHand myGrabHand = GrabHand.None;
    
    // Position sync
    private float lastSyncTime;
    private const float SYNC_INTERVAL = 0.033f; // ~30Hz sync rate

    public override void Spawned()
    {
        base.Spawned();
        
        // Determine if we're player 1 or 2 (host is player 1)
        amIPlayer1 = Object.HasStateAuthority;
        
        // Find controller references
        cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            leftController = cameraRig.leftControllerAnchor;
            rightController = cameraRig.rightControllerAnchor;
        }
        
        // Find local anchor
        FindLocalAnchor();
        
        // Create racket visuals
        CreateRackets();
        
        Debug.Log($"[RacketManager] Spawned as {(amIPlayer1 ? "Player 1 (Host)" : "Player 2 (Client)")}");
    }

    private void FindLocalAnchor()
    {
        var guiManager = FindObjectOfType<AnchorAutoGUIManager>();
        if (guiManager != null)
        {
            localAnchor = guiManager.GetLocalizedAnchor();
        }
        
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
    }

    private void CreateRackets()
    {
        if (racketPrefab == null)
        {
            Debug.LogError("[RacketManager] Racket prefab not assigned!");
            return;
        }
        
        // Create my racket
        myRacket = Instantiate(racketPrefab);
        myRacket.name = "MyRacket";
        myRacketRenderer = myRacket.GetComponentInChildren<Renderer>();
        SetRacketColor(myRacketRenderer, amIPlayer1 ? player1Color : player2Color);
        myRacket.SetActive(false); // Hidden until grabbed
        
        // Create other player's racket
        otherPlayerRacket = Instantiate(racketPrefab);
        otherPlayerRacket.name = "OtherPlayerRacket";
        otherRacketRenderer = otherPlayerRacket.GetComponentInChildren<Renderer>();
        SetRacketColor(otherRacketRenderer, amIPlayer1 ? player2Color : player1Color);
        otherPlayerRacket.SetActive(false); // Hidden until other player grabs
        
        Debug.Log("[RacketManager] Rackets created");
    }

    private void SetRacketColor(Renderer renderer, Color color)
    {
        if (renderer == null) return;
        
        MaterialPropertyBlock block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        block.SetColor("_Color", color);
        block.SetColor("_BaseColor", color);
        renderer.SetPropertyBlock(block);
    }

    public override void FixedUpdateNetwork()
    {
        HandleLocalGrabInput();
        UpdateMyRacketPosition();
        UpdateOtherPlayerRacket();
        SyncMyPosition();
    }

    private void HandleLocalGrabInput()
    {
        bool leftPressed = OVRInput.Get(grabButton, OVRInput.Controller.LTouch);
        bool rightPressed = OVRInput.Get(grabButton, OVRInput.Controller.RTouch);
        
        if (!isGrabbing)
        {
            // Try to grab
            if (leftPressed)
            {
                Grab(GrabHand.Left);
            }
            else if (rightPressed)
            {
                Grab(GrabHand.Right);
            }
        }
        else
        {
            // Check for release
            bool shouldRelease = (myGrabHand == GrabHand.Left && !leftPressed) ||
                                (myGrabHand == GrabHand.Right && !rightPressed);
            
            if (shouldRelease)
            {
                Release();
            }
        }
    }

    private void Grab(GrabHand hand)
    {
        isGrabbing = true;
        myGrabHand = hand;
        myRacket.SetActive(true);
        
        // Update networked state
        if (Object.HasStateAuthority)
        {
            // Host updates directly
            if (amIPlayer1)
            {
                Player1HasRacket = true;
                Player1Hand = hand;
            }
            else
            {
                Player2HasRacket = true;
                Player2Hand = hand;
            }
        }
        else
        {
            // Client requests via RPC
            RPC_NotifyGrab(hand);
        }
        
        Debug.Log($"[RacketManager] Grabbed with {hand}");
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_NotifyGrab(GrabHand hand, RpcInfo info = default)
    {
        // Host receives grab notification from client
        if (info.Source != Runner.LocalPlayer)
        {
            Player2HasRacket = true;
            Player2Hand = hand;
            Debug.Log($"[RacketManager] Player 2 grabbed with {hand}");
        }
    }

    private void Release()
    {
        isGrabbing = false;
        myGrabHand = GrabHand.None;
        myRacket.SetActive(false);
        
        if (Object.HasStateAuthority)
        {
            if (amIPlayer1)
            {
                Player1HasRacket = false;
                Player1Hand = GrabHand.None;
            }
            else
            {
                Player2HasRacket = false;
                Player2Hand = GrabHand.None;
            }
        }
        else
        {
            RPC_NotifyRelease();
        }
        
        Debug.Log("[RacketManager] Released racket");
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_NotifyRelease(RpcInfo info = default)
    {
        if (info.Source != Runner.LocalPlayer)
        {
            Player2HasRacket = false;
            Player2Hand = GrabHand.None;
        }
    }

    private void UpdateMyRacketPosition()
    {
        if (!isGrabbing || myRacket == null) return;
        
        Transform controller = (myGrabHand == GrabHand.Left) ? leftController : rightController;
        if (controller != null)
        {
            myRacket.transform.position = controller.position;
            myRacket.transform.rotation = controller.rotation;
        }
    }

    private void SyncMyPosition()
    {
        if (!isGrabbing || localAnchor == null) return;
        
        // Throttle sync rate
        if (Time.time - lastSyncTime < SYNC_INTERVAL) return;
        lastSyncTime = Time.time;
        
        Transform controller = (myGrabHand == GrabHand.Left) ? leftController : rightController;
        if (controller == null) return;
        
        // Convert to anchor-relative
        Vector3 relPos = localAnchor.transform.InverseTransformPoint(controller.position);
        Quaternion relRot = Quaternion.Inverse(localAnchor.transform.rotation) * controller.rotation;
        
        if (Object.HasStateAuthority)
        {
            if (amIPlayer1)
            {
                Player1RacketPos = relPos;
                Player1RacketRot = relRot;
            }
            else
            {
                Player2RacketPos = relPos;
                Player2RacketRot = relRot;
            }
        }
        else
        {
            RPC_SyncPosition(relPos, relRot);
        }
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_SyncPosition(Vector3 relPos, Quaternion relRot, RpcInfo info = default)
    {
        if (info.Source != Runner.LocalPlayer)
        {
            Player2RacketPos = relPos;
            Player2RacketRot = relRot;
        }
    }

    private void UpdateOtherPlayerRacket()
    {
        if (otherPlayerRacket == null || localAnchor == null) return;
        
        // Get other player's state
        bool otherHasRacket = amIPlayer1 ? Player2HasRacket : Player1HasRacket;
        Vector3 otherPos = amIPlayer1 ? Player2RacketPos : Player1RacketPos;
        Quaternion otherRot = amIPlayer1 ? Player2RacketRot : Player1RacketRot;
        
        otherPlayerRacket.SetActive(otherHasRacket);
        
        if (otherHasRacket)
        {
            // Convert anchor-relative to world position
            otherPlayerRacket.transform.position = localAnchor.transform.TransformPoint(otherPos);
            otherPlayerRacket.transform.rotation = localAnchor.transform.rotation * otherRot;
        }
    }

    private void OnDestroy()
    {
        if (myRacket != null) Destroy(myRacket);
        if (otherPlayerRacket != null) Destroy(otherPlayerRacket);
    }
}

#endif
