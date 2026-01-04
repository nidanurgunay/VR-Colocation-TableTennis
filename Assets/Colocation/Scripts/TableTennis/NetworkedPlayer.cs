using UnityEngine;
using Fusion;
using System.Collections.Generic;

/// <summary>
/// Represents a networked player in the colocation session.
/// Synchronizes Head and Hand positions so players can see each other.
/// Also syncs racket active state so other players can see your racket.
/// </summary>
public class NetworkedPlayer : NetworkBehaviour
{
    [Header("Visuals (Optional - for head/hand spheres)")]
    [SerializeField] private GameObject headVisual;
    [SerializeField] private GameObject leftHandVisual;
    [SerializeField] private GameObject rightHandVisual;
    
    [Header("Racket Settings")]
    [SerializeField] private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f);
    [SerializeField] private Vector3 racketRotation = new Vector3(0, 270, 40);
    [SerializeField] private float racketScale = 10f;

    // Networked transforms for Head, Left Hand, Right Hand
    [Networked] private Vector3 HeadPos { get; set; }
    [Networked] private Quaternion HeadRot { get; set; }
    
    [Networked] private Vector3 LeftHandPos { get; set; }
    [Networked] private Quaternion LeftHandRot { get; set; }
    
    [Networked] private Vector3 RightHandPos { get; set; }
    [Networked] private Quaternion RightHandRot { get; set; }
    
    // Networked racket active states - so other players see our rackets!
    [Networked] private NetworkBool LeftRacketActive { get; set; }
    [Networked] private NetworkBool RightRacketActive { get; set; }

    // Local references
    private Transform _localHead;
    private Transform _localLeftHand;
    private Transform _localRightHand;
    
    // Standalone remote racket objects (lazy initialized)
    private GameObject _leftRemoteRacket;
    private GameObject _rightRemoteRacket;
    private bool _racketSearchAttempted = false;

    public override void Spawned()
    {
        Debug.Log($"[NetworkedPlayer] Spawned for Player {Object.InputAuthority.PlayerId}. IsLocal: {Object.HasInputAuthority}");
        
        // If this is OUR player, find local hardware to drive values
        if (Object.HasInputAuthority)
        {
            FindLocalHardware();
            
            // Hide visuals for self (so we don't see our own floating head)
            SetVisualsActive(false);
        }
        else
        {
            // For remote players, ensure visuals are ON (if assigned)
            SetVisualsActive(true);
            
            // DON'T create rackets here - they will be created lazily when needed
            // because the racket template might not exist yet (scene not loaded)
        }
        
        // Name the object for easier debugging
        gameObject.name = $"NetworkedPlayer_{Object.InputAuthority.PlayerId}";
        DontDestroyOnLoad(gameObject);
    }
    
    /// <summary>
    /// Lazy initialization: Create rackets when they're first needed
    /// </summary>
    private void EnsureRemoteRacketsExist()
    {
        // Only for remote players
        if (Object.HasInputAuthority) return;
        
        // Already created?
        if (_leftRemoteRacket != null && _rightRemoteRacket != null) return;
        
        // Already tried and failed? Retry occasionally
        if (_racketSearchAttempted && Time.frameCount % 60 != 0) return;
        
        _racketSearchAttempted = true;
        
        // Find racket template from scene
        var racketTemplate = FindRacketTemplate();
        
        if (racketTemplate == null)
        {
            // Will retry on next frame
            return;
        }
        
        Debug.Log($"[NetworkedPlayer] Found racket template: {racketTemplate.name}, creating remote rackets...");
        
        // Create left hand racket (standalone, hidden initially)
        if (_leftRemoteRacket == null)
        {
            _leftRemoteRacket = Instantiate(racketTemplate);
            _leftRemoteRacket.name = $"RemoteLeftRacket_P{Object.InputAuthority.PlayerId}";
            _leftRemoteRacket.transform.localScale = Vector3.one * racketScale;
            _leftRemoteRacket.SetActive(false);
            CleanupPhysics(_leftRemoteRacket);
            DontDestroyOnLoad(_leftRemoteRacket);
        }
        
        // Create right hand racket (standalone, hidden initially)
        if (_rightRemoteRacket == null)
        {
            _rightRemoteRacket = Instantiate(racketTemplate);
            _rightRemoteRacket.name = $"RemoteRightRacket_P{Object.InputAuthority.PlayerId}";
            _rightRemoteRacket.transform.localScale = Vector3.one * racketScale;
            _rightRemoteRacket.SetActive(false);
            CleanupPhysics(_rightRemoteRacket);
            DontDestroyOnLoad(_rightRemoteRacket);
        }
        
        Debug.Log("[NetworkedPlayer] Created standalone remote racket objects (lazy init)");
    }
    
    private GameObject FindRacketTemplate()
    {
        // Method 1: Search by tag (active objects only)  
        var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var r in taggedRackets)
        {
            // Skip copies we created
            if (r.name.Contains("Controller") || r.name.Contains("Remote")) continue;
            Debug.Log($"[NetworkedPlayer] Found racket by tag: {r.name}");
            return r;
        }
        
        // Method 2: Search ALL objects including inactive using Resources
        var allObjects = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (var t in allObjects)
        {
            if (!t.gameObject.scene.isLoaded) continue; // Skip prefabs
            
            string nameLower = t.name.ToLower();
            if (nameLower.Contains("controller") || nameLower.Contains("remote")) continue;
            
            if ((nameLower == "racket" || nameLower == "racket2" || nameLower.Contains("paddle") || nameLower.Contains("bat"))
                && t.GetComponent<MeshFilter>() != null)
            {
                Debug.Log($"[NetworkedPlayer] Found racket by Resources: {t.name}");
                return t.gameObject;
            }
        }
        
        return null;
    }
    
    private void CleanupPhysics(GameObject obj)
    {
        var rb = obj.GetComponent<Rigidbody>();
        if (rb) Destroy(rb);
        // Keep collider for potential ball hits
    }

    private void FindLocalHardware()
    {
        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig != null)
        {
            _localHead = rig.centerEyeAnchor;
            _localLeftHand = rig.leftHandAnchor;
            _localRightHand = rig.rightHandAnchor;
            Debug.Log("[NetworkedPlayer] Found OVRCameraRig references.");
        }
        else
        {
            Debug.LogWarning("[NetworkedPlayer] OVRCameraRig not found yet, will retry...");
        }
    }

    private void SetVisualsActive(bool active)
    {
        if (headVisual) headVisual.SetActive(active);
        if (leftHandVisual) leftHandVisual.SetActive(active);
        if (rightHandVisual) rightHandVisual.SetActive(active);
    }

    public override void FixedUpdateNetwork()
    {
        // Only the owner writes to network variables
        if (Object.HasInputAuthority)
        {
            UpdateNetworkState();
        }
        else
        {
            // Proxies read from network variables and update transforms
            UpdateVisuals();
        }
    }

    private void UpdateNetworkState()
    {
        // Re-find hardware if lost (scene transition)
        if (_localHead == null)
        {
            FindLocalHardware();
            if (_localHead == null) return;
        }
        
        HeadPos = _localHead.position;
        HeadRot = _localHead.rotation;

        if (_localLeftHand)
        {
            LeftHandPos = _localLeftHand.position;
            LeftHandRot = _localLeftHand.rotation;
        }

        if (_localRightHand)
        {
            RightHandPos = _localRightHand.position;
            RightHandRot = _localRightHand.rotation;
        }
        
        // Sync racket active states from ControllerRacket
        var controllerRacket = FindObjectOfType<ControllerRacket>();
        if (controllerRacket != null)
        {
            LeftRacketActive = controllerRacket.IsRacketActive(OVRInput.Controller.LTouch);
            RightRacketActive = controllerRacket.IsRacketActive(OVRInput.Controller.RTouch);
        }
    }

    private void UpdateVisuals()
    {
        // Lazy create remote rackets (waits for scene to have the template)
        EnsureRemoteRacketsExist();
        
        // Update optional head/hand sphere visuals (if assigned)
        if (headVisual)
        {
            headVisual.transform.position = HeadPos;
            headVisual.transform.rotation = HeadRot;
        }

        if (leftHandVisual)
        {
            leftHandVisual.transform.position = LeftHandPos;
            leftHandVisual.transform.rotation = LeftHandRot;
        }

        if (rightHandVisual)
        {
            rightHandVisual.transform.position = RightHandPos;
            rightHandVisual.transform.rotation = RightHandRot;
        }
        
        // Update standalone remote rackets - position them at hand positions
        if (_leftRemoteRacket != null)
        {
            _leftRemoteRacket.SetActive(LeftRacketActive);
            if (LeftRacketActive)
            {
                // Position at left hand with offset and rotation
                _leftRemoteRacket.transform.position = LeftHandPos + (LeftHandRot * racketOffset);
                _leftRemoteRacket.transform.rotation = LeftHandRot * Quaternion.Euler(racketRotation);
            }
        }
        
        if (_rightRemoteRacket != null)
        {
            _rightRemoteRacket.SetActive(RightRacketActive);
            if (RightRacketActive)
            {
                // Position at right hand with offset and rotation
                _rightRemoteRacket.transform.position = RightHandPos + (RightHandRot * racketOffset);
                _rightRemoteRacket.transform.rotation = RightHandRot * Quaternion.Euler(racketRotation);
            }
        }
    }
    
    private void OnDestroy()
    {
        // Clean up standalone racket objects
        if (_leftRemoteRacket != null) Destroy(_leftRemoteRacket);
        if (_rightRemoteRacket != null) Destroy(_rightRemoteRacket);
    }
}
