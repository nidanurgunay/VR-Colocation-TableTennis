using UnityEngine;
using Fusion;

/// <summary>
/// Networked racket that syncs position/rotation across all players.
/// Each player owns one racket (spawned by host), controlled locally, visible to all.
/// </summary>
public class NetworkedRacket : NetworkBehaviour
{
    [Header("Settings")]
    [SerializeField] private float interpolationSpeed = 20f;
    
    // Networked state - synced to all clients
    [Networked] private Vector3 NetworkedPosition { get; set; }
    [Networked] private Quaternion NetworkedRotation { get; set; }
    [Networked] private NetworkBool IsVisible { get; set; }
    [Networked] public PlayerRef OwningPlayer { get; set; }
    
    // Local tracking
    private Transform controllerTransform;
    private bool isLocalPlayer = false;
    private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f);
    private Vector3 racketRotationEuler = new Vector3(-51f, 184f, 81f);
    private float racketScale = 10f;
    private float lastHitTime = 0f; // Prevent double hits
    
    // Rendering
    private Renderer[] renderers;
    private bool initialized = false;
    
    public override void Spawned()
    {
        Debug.Log($"[NetworkedRacket] Spawned. Owner={OwningPlayer}, Local={Runner.LocalPlayer}");
        
        // Check if this racket belongs to the local player
        isLocalPlayer = (OwningPlayer == Runner.LocalPlayer);
        
        // Cache renderers
        renderers = GetComponentsInChildren<Renderer>();
        
        if (isLocalPlayer)
        {
            // Find our controller
            StartCoroutine(FindLocalController());
        }
        else
        {
            // This is a remote player's racket - just follow networked state
            Debug.Log("[NetworkedRacket] This is a remote player's racket");
        }
        
        initialized = true;
    }
    
    private System.Collections.IEnumerator FindLocalController()
    {
        yield return new WaitForSeconds(0.5f);
        
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            // Use right controller by default - could be made configurable
            controllerTransform = cameraRig.rightControllerAnchor;
            Debug.Log($"[NetworkedRacket] Local player - attached to right controller");
            
            // Show the racket
            IsVisible = true;
        }
        else
        {
            Debug.LogWarning("[NetworkedRacket] OVRCameraRig not found!");
        }
    }
    
    private void Update()
    {
        if (!initialized || Object == null || !Object.IsValid) return;
        
        // Update visibility
        UpdateVisibility();
    }
    
    public override void FixedUpdateNetwork()
    {
        if (!initialized) return;
        
        if (isLocalPlayer && controllerTransform != null)
        {
            // Local player: Update networked state from controller
            Vector3 worldPos = controllerTransform.TransformPoint(racketOffset);
            Quaternion worldRot = controllerTransform.rotation * Quaternion.Euler(racketRotationEuler);
            
            NetworkedPosition = worldPos;
            NetworkedRotation = worldRot;
            
            // Apply directly (no interpolation for local)
            transform.position = worldPos;
            transform.rotation = worldRot;
        }
        else
        {
            // Remote player: Interpolate toward networked state
            transform.position = Vector3.Lerp(transform.position, NetworkedPosition, Time.deltaTime * interpolationSpeed);
            transform.rotation = Quaternion.Slerp(transform.rotation, NetworkedRotation, Time.deltaTime * interpolationSpeed);
        }
    }
    
    private void UpdateVisibility()
    {
        bool shouldBeVisible = IsVisible;
        
        foreach (var r in renderers)
        {
            if (r != null && r.enabled != shouldBeVisible)
            {
                r.enabled = shouldBeVisible;
            }
        }
    }
    
    /// <summary>
    /// Set the controller this racket follows (for local player)
    /// </summary>
    public void SetController(Transform controller)
    {
        controllerTransform = controller;
    }
    
    /// <summary>
    /// Set racket offset/rotation (matches ControllerRacket settings)
    /// </summary>
    public void SetOffsetAndRotation(Vector3 offset, Vector3 rotation, float scale)
    {
        racketOffset = offset;
        racketRotationEuler = rotation;
        racketScale = scale;
        transform.localScale = Vector3.one * racketScale;
    }
    
    /// <summary>
    /// Show/hide the racket
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (Object != null && Object.HasStateAuthority)
        {
            IsVisible = visible;
        }
        else if (isLocalPlayer)
        {
            // Request visibility change via RPC
            RPC_RequestVisibility(visible);
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestVisibility(NetworkBool visible)
    {
        IsVisible = visible;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!isLocalPlayer) return;

        // Prevent double hits (cooldown)
        if (Time.time - lastHitTime < 0.3f) return;

        // Check if colliding with ball
        var ball = collision.gameObject.GetComponent<NetworkedBall>();
        if (ball != null)
        {
            Debug.Log($"[NetworkedRacket] Local player hit ball! Sending RPC...");
            lastHitTime = Time.time;

            // Calculate hit velocity from racket
            Vector3 hitVelocity = Vector3.zero;
            if (GetComponent<Rigidbody>() != null)
            {
                hitVelocity = GetComponent<Rigidbody>().velocity * 2.2f; // Use same multiplier as ball
            }
            else
            {
                // Fallback: use controller velocity
                hitVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch) * 2.2f;
                if (hitVelocity.magnitude < 0.5f)
                {
                    hitVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.LTouch) * 2.2f;
                }
            }

            // Ensure minimum upward velocity
            hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);

            // Send RPC to ball to apply hit
            ball.RPC_RequestHit(hitVelocity, collision.contacts[0].point);
        }
    }
}
