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

    // Networked transforms for Head, Left Hand, Right Hand (ANCHOR-RELATIVE for proper alignment)
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
    private Transform _sharedAnchor; // Reference to shared anchor for anchor-relative positioning

    // Standalone remote racket objects (lazy initialized)
    private GameObject _leftRemoteRacket;
    private GameObject _rightRemoteRacket;
    private bool _racketSearchAttempted = false;

    public override void Spawned()
    {
        Debug.Log($"[NetworkedPlayer] Spawned for Player {Object.InputAuthority.PlayerId}. IsLocal: {Object.HasInputAuthority}");

        // Find shared anchor for anchor-relative positioning
        FindSharedAnchor();

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

    private void FindSharedAnchor()
    {
        // Try to find the shared anchor for anchor-relative positioning
        var anchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in anchors)
        {
            if (anchor != null && anchor.Localized)
            {
                _sharedAnchor = anchor.transform;
                Debug.Log($"[NetworkedPlayer] Found shared anchor: {anchor.name}");
                return;
            }
        }

        // If not found, try getting from AnchorGUIManager
        var anchorGUI = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
        if (anchorGUI != null)
        {
            var localizedAnchor = anchorGUI.GetLocalizedAnchor();
            if (localizedAnchor != null)
            {
                _sharedAnchor = localizedAnchor.transform;
                Debug.Log($"[NetworkedPlayer] Found shared anchor from GUIManager: {localizedAnchor.name}");
            }
        }

        if (_sharedAnchor == null)
        {
            Debug.LogWarning("[NetworkedPlayer] Shared anchor not found - will retry later");
        }
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

        // Re-find anchor if lost
        if (_sharedAnchor == null)
        {
            FindSharedAnchor();
        }

        // FIXED: Store positions as ANCHOR-RELATIVE for proper alignment across devices
        if (_sharedAnchor != null)
        {
            // Convert world positions to anchor-relative positions
            HeadPos = _sharedAnchor.InverseTransformPoint(_localHead.position);
            HeadRot = Quaternion.Inverse(_sharedAnchor.rotation) * _localHead.rotation;

            if (_localLeftHand)
            {
                LeftHandPos = _sharedAnchor.InverseTransformPoint(_localLeftHand.position);
                LeftHandRot = Quaternion.Inverse(_sharedAnchor.rotation) * _localLeftHand.rotation;
            }

            if (_localRightHand)
            {
                RightHandPos = _sharedAnchor.InverseTransformPoint(_localRightHand.position);
                RightHandRot = Quaternion.Inverse(_sharedAnchor.rotation) * _localRightHand.rotation;
            }
        }
        else
        {
            // Fallback to world positions if anchor not found yet
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
        }

        // Sync racket active states from ControllerRacket OR passthrough mode rackets
        var controllerRacket = FindObjectOfType<ControllerRacket>();
        if (controllerRacket != null)
        {
            LeftRacketActive = controllerRacket.IsRacketActive(OVRInput.Controller.LTouch);
            RightRacketActive = controllerRacket.IsRacketActive(OVRInput.Controller.RTouch);
        }
        else
        {
            // In passthrough mode, check if rackets exist and are active in AnchorGUIManager
            var anchorGUI = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
            if (anchorGUI != null)
            {
                // Check if passthrough rackets are visible
                bool passthroughRacketsVisible = anchorGUI.ArePassthroughRacketsVisible();
                LeftRacketActive = passthroughRacketsVisible;
                RightRacketActive = passthroughRacketsVisible;
            }
        }
    }

    private void UpdateVisuals()
    {
        // Re-find anchor if lost
        if (_sharedAnchor == null)
        {
            FindSharedAnchor();
        }

        // Lazy create remote rackets (waits for scene to have the template)
        EnsureRemoteRacketsExist();

        // FIXED: Convert anchor-relative positions back to world positions for display
        Vector3 worldHeadPos = HeadPos;
        Quaternion worldHeadRot = HeadRot;
        Vector3 worldLeftHandPos = LeftHandPos;
        Quaternion worldLeftHandRot = LeftHandRot;
        Vector3 worldRightHandPos = RightHandPos;
        Quaternion worldRightHandRot = RightHandRot;

        if (_sharedAnchor != null)
        {
            // Convert from anchor-relative to world space
            worldHeadPos = _sharedAnchor.TransformPoint(HeadPos);
            worldHeadRot = _sharedAnchor.rotation * HeadRot;
            worldLeftHandPos = _sharedAnchor.TransformPoint(LeftHandPos);
            worldLeftHandRot = _sharedAnchor.rotation * LeftHandRot;
            worldRightHandPos = _sharedAnchor.TransformPoint(RightHandPos);
            worldRightHandRot = _sharedAnchor.rotation * RightHandRot;
        }

        // Update optional head/hand sphere visuals (if assigned)
        if (headVisual)
        {
            headVisual.transform.position = worldHeadPos;
            headVisual.transform.rotation = worldHeadRot;
        }

        if (leftHandVisual)
        {
            leftHandVisual.transform.position = worldLeftHandPos;
            leftHandVisual.transform.rotation = worldLeftHandRot;
        }

        if (rightHandVisual)
        {
            rightHandVisual.transform.position = worldRightHandPos;
            rightHandVisual.transform.rotation = worldRightHandRot;
        }

        // Update standalone remote rackets - position them at hand positions
        if (_leftRemoteRacket != null)
        {
            _leftRemoteRacket.SetActive(LeftRacketActive);
            if (LeftRacketActive)
            {
                // Position at left hand with offset and rotation
                _leftRemoteRacket.transform.position = worldLeftHandPos + (worldLeftHandRot * racketOffset);
                _leftRemoteRacket.transform.rotation = worldLeftHandRot * Quaternion.Euler(racketRotation);
            }
        }

        if (_rightRemoteRacket != null)
        {
            _rightRemoteRacket.SetActive(RightRacketActive);
            if (RightRacketActive)
            {
                // Position at right hand with offset and rotation
                _rightRemoteRacket.transform.position = worldRightHandPos + (worldRightHandRot * racketOffset);
                _rightRemoteRacket.transform.rotation = worldRightHandRot * Quaternion.Euler(racketRotation);
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
