using UnityEngine;

/// <summary>
/// Handles local grabbing of a racket. No networking needed for rackets
/// since colocation alignment already syncs player positions.
/// Each player sees the other's controller (with racket) in the correct position.
/// </summary>
public class GrabbableRacket : MonoBehaviour
{
    [Header("Grab Settings")]
    [SerializeField] private float grabRadius = 0.1f;
    [SerializeField] private OVRInput.Button grabButton = OVRInput.Button.PrimaryHandTrigger;
    
    [Header("Visual Feedback")]
    [SerializeField] private Material normalMaterial;
    [SerializeField] private Material highlightMaterial;
    
    [Header("Audio")]
    [SerializeField] private AudioClip grabSound;
    [SerializeField] private AudioClip releaseSound;
    
    // State
    private bool isGrabbed = false;
    private OVRInput.Controller grabbingController = OVRInput.Controller.None;
    private Transform grabParent; // The controller transform
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private Transform originalParent;
    
    // References
    private Renderer racketRenderer;
    private AudioSource audioSource;
    private Rigidbody rb;
    private Collider col;
    
    // Controller tracking
    private Transform leftHandAnchor;
    private Transform rightHandAnchor;
    
    private void Start()
    {
        racketRenderer = GetComponentInChildren<Renderer>();
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.spatialBlend = 1f; // 3D sound
        }
        
        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();
        
        // Store original position for release
        originalPosition = transform.position;
        originalRotation = transform.rotation;
        originalParent = transform.parent;
        
        // Find hand anchors from OVRCameraRig
        FindHandAnchors();
        
        // Tag for ball collision detection
        if (!gameObject.CompareTag("Racket"))
        {
            gameObject.tag = "Racket";
        }
    }
    
    private void FindHandAnchors()
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            leftHandAnchor = cameraRig.leftHandAnchor;
            rightHandAnchor = cameraRig.rightHandAnchor;
            Debug.Log("[GrabbableRacket] Found hand anchors");
        }
        else
        {
            Debug.LogWarning("[GrabbableRacket] OVRCameraRig not found!");
        }
    }
    
    private void Update()
    {
        if (leftHandAnchor == null || rightHandAnchor == null)
        {
            FindHandAnchors();
            return;
        }
        
        if (!isGrabbed)
        {
            CheckForGrab();
        }
        else
        {
            CheckForRelease();
            FollowController();
        }
    }
    
    private void CheckForGrab()
    {
        // Check left hand
        if (IsControllerNearAndGrabbing(OVRInput.Controller.LTouch, leftHandAnchor))
        {
            Grab(OVRInput.Controller.LTouch, leftHandAnchor);
            return;
        }
        
        // Check right hand
        if (IsControllerNearAndGrabbing(OVRInput.Controller.RTouch, rightHandAnchor))
        {
            Grab(OVRInput.Controller.RTouch, rightHandAnchor);
            return;
        }
        
        // Visual feedback: highlight when controller is near
        UpdateHighlight();
    }
    
    private bool IsControllerNearAndGrabbing(OVRInput.Controller controller, Transform handAnchor)
    {
        if (handAnchor == null) return false;
        
        float distance = Vector3.Distance(handAnchor.position, transform.position);
        bool isNear = distance <= grabRadius;
        bool isGrabbing = OVRInput.Get(grabButton, controller);
        
        return isNear && isGrabbing;
    }
    
    private bool IsControllerNear(Transform handAnchor)
    {
        if (handAnchor == null) return false;
        return Vector3.Distance(handAnchor.position, transform.position) <= grabRadius;
    }
    
    private void UpdateHighlight()
    {
        if (racketRenderer == null || highlightMaterial == null) return;
        
        bool isNear = IsControllerNear(leftHandAnchor) || IsControllerNear(rightHandAnchor);
        racketRenderer.material = isNear ? highlightMaterial : normalMaterial;
    }
    
    private void Grab(OVRInput.Controller controller, Transform handAnchor)
    {
        isGrabbed = true;
        grabbingController = controller;
        grabParent = handAnchor;
        
        // Disable physics while grabbed
        if (rb != null)
        {
            rb.isKinematic = true;
        }
        
        // Parent to controller
        transform.SetParent(handAnchor);
        
        // Position racket nicely in hand
        transform.localPosition = new Vector3(0, 0, 0.05f); // Slightly forward
        transform.localRotation = Quaternion.Euler(0, 0, 0); // Adjust as needed for your racket model
        
        // Visual feedback
        if (racketRenderer != null && highlightMaterial != null)
        {
            racketRenderer.material = normalMaterial;
        }
        
        // Audio
        if (grabSound != null)
        {
            audioSource.PlayOneShot(grabSound);
        }
        
        // Haptic feedback
        OVRInput.SetControllerVibration(0.3f, 0.3f, controller);
        Invoke(nameof(StopVibration), 0.1f);
        
        Debug.Log($"[GrabbableRacket] Grabbed with {controller}");
    }
    
    private void CheckForRelease()
    {
        bool isStillGrabbing = OVRInput.Get(grabButton, grabbingController);
        
        if (!isStillGrabbing)
        {
            Release();
        }
    }
    
    private void Release()
    {
        isGrabbed = false;
        
        // Unparent
        transform.SetParent(originalParent);
        
        // Return to original position on table
        transform.position = originalPosition;
        transform.rotation = originalRotation;
        
        // Re-enable physics for table placement
        if (rb != null)
        {
            rb.isKinematic = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        
        // Audio
        if (releaseSound != null)
        {
            audioSource.PlayOneShot(releaseSound);
        }
        
        // Haptic feedback
        OVRInput.SetControllerVibration(0.2f, 0.2f, grabbingController);
        Invoke(nameof(StopVibration), 0.1f);
        
        Debug.Log($"[GrabbableRacket] Released, returning to table");
        
        grabbingController = OVRInput.Controller.None;
        grabParent = null;
    }
    
    private void FollowController()
    {
        // Already parented, so this follows automatically
        // But we can add velocity tracking here for ball hit detection
        
        if (rb != null && grabParent != null)
        {
            // Track velocity for hit detection (even though kinematic)
            // This velocity is used by NetworkedBall for hit response
            rb.velocity = (grabParent.position - transform.position) / Time.deltaTime;
        }
    }
    
    private void StopVibration()
    {
        if (grabbingController != OVRInput.Controller.None)
        {
            OVRInput.SetControllerVibration(0, 0, grabbingController);
        }
    }
    
    /// <summary>
    /// Force release (e.g., when scene changes)
    /// </summary>
    public void ForceRelease()
    {
        if (isGrabbed)
        {
            Release();
        }
    }
    
    /// <summary>
    /// Check if this racket is currently grabbed
    /// </summary>
    public bool IsGrabbed => isGrabbed;
    
    /// <summary>
    /// Get which hand is holding this racket
    /// </summary>
    public OVRInput.Controller GrabbingHand => grabbingController;
    
    private void OnDrawGizmosSelected()
    {
        // Visualize grab radius
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, grabRadius);
    }
}
