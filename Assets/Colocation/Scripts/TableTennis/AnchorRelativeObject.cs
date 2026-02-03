using UnityEngine;
using System.Collections;

/// <summary>
/// Places an object relative to the shared spatial anchor.
/// Attach to any object (like table) that should be aligned across sessions.
/// The object will be positioned at the specified offset from the anchor.
/// Supports automatic floor detection and runtime height adjustment.
/// </summary>
public class AnchorRelativeObject : MonoBehaviour
{
    [Header("Position Relative to Anchor")]
    [Tooltip("Position offset from the anchor (in anchor's local space)")]
    [SerializeField] private Vector3 anchorOffset = Vector3.zero;
    
    [Tooltip("Rotation offset from the anchor (in anchor's local space)")]
    [SerializeField] private Vector3 anchorRotationOffset = Vector3.zero;
    
    [Header("Floor Detection")]
    [Tooltip("If true, automatically detect floor and place object on it")]
    [SerializeField] private bool autoPlaceOnFloor = true;
    
    [Tooltip("Height of the object above the floor (e.g., table height = 0.76m)")]
    [SerializeField] private float heightAboveFloor = 0.76f;
    
    [Tooltip("Layer mask for floor detection raycast")]
    [SerializeField] private LayerMask floorLayerMask = ~0; // All layers by default
    
    [Tooltip("Maximum distance to search for floor")]
    [SerializeField] private float maxFloorSearchDistance = 3f;
    
    [Header("Runtime Adjustment")]
    [Tooltip("Allow runtime height adjustment via public methods")]
    [SerializeField] private bool allowRuntimeAdjustment = true;
    
    [Tooltip("Height adjustment step (meters)")]
    [SerializeField] private float heightAdjustmentStep = 0.05f;
    
    [Header("Options")]
    [Tooltip("If true, this object will become a child of the anchor")]
    [SerializeField] private bool parentToAnchor = true;
    
    [Tooltip("If true, continuously update position (for moving anchors)")]
    [SerializeField] private bool continuousUpdate = false;
    
    [Header("Debug")]
    [SerializeField] private bool showDebugInfo = true;
    
    private Transform sharedAnchor;
    private bool isAligned = false;
    private int retryCount = 0;
    private const int MAX_RETRIES = 100; // Try for ~10 seconds
    private float currentHeightOffset = 0f; // Additional height adjustment by user
    private float detectedFloorY = 0f;

    // Public property to get/set height
    public float HeightAboveFloor
    {
        get => heightAboveFloor + currentHeightOffset;
        set
        {
            if (allowRuntimeAdjustment)
            {
                currentHeightOffset = value - heightAboveFloor;
                if (isAligned) UpdatePosition();
            }
        }
    }

    private void Start()
    {
        StartCoroutine(WaitForAnchorAndAlign());
    }

    private IEnumerator WaitForAnchorAndAlign()
    {
        if (showDebugInfo)

        while (sharedAnchor == null && retryCount < MAX_RETRIES)
        {
            // Try to find localized anchor
            sharedAnchor = FindLocalizedAnchor();
            
            if (sharedAnchor == null)
            {
                retryCount++;
                yield return new WaitForSeconds(0.1f);
            }
        }

        if (sharedAnchor != null)
        {
            AlignToAnchor();
        }
        else
        {
            Debug.LogError($"[AnchorRelativeObject] {gameObject.name} failed to find anchor after {MAX_RETRIES} attempts!");
        }
    }

    private Transform FindLocalizedAnchor()
    {
        // Method 1: Find via AnchorGUIManager_AutoAlignment
        var guiManager = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
        if (guiManager != null)
        {
            var anchor = guiManager.GetLocalizedAnchor();
            if (anchor != null && anchor.Localized)
            {
                if (showDebugInfo)
                return anchor.transform;
            }
        }

        // Method 2: Find any localized OVRSpatialAnchor
        var allAnchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in allAnchors)
        {
            if (anchor != null && anchor.Localized)
            {
                if (showDebugInfo)
                return anchor.transform;
            }
        }

        return null;
    }

    private void AlignToAnchor()
    {
        if (sharedAnchor == null) return;

        // Detect floor if enabled
        if (autoPlaceOnFloor)
        {
            DetectFloor();
        }

        UpdatePosition();
        isAligned = true;
    }

    private void DetectFloor()
    {
        // Raycast down from anchor position to find floor
        Vector3 rayOrigin = sharedAnchor.position + Vector3.up * 0.5f; // Start slightly above anchor
        
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, maxFloorSearchDistance, floorLayerMask))
        {
            detectedFloorY = hit.point.y;
            if (showDebugInfo)
            {
                Debug.Log($"[AnchorRelativeObject] {gameObject.name} detected floor at Y: {detectedFloorY}");
            }
        }
        else
        {
            // No floor found, use anchor Y position as floor
            detectedFloorY = sharedAnchor.position.y;
            if (showDebugInfo)
            {
                Debug.Log($"[AnchorRelativeObject] {gameObject.name} no floor found, using anchor Y: {detectedFloorY}");
            }
        }
    }

    private void UpdatePosition()
    {
        if (sharedAnchor == null) return;

        Vector3 localScale = transform.localScale;
        Vector3 targetPosition;
        Quaternion targetRotation;

        if (autoPlaceOnFloor)
        {
            // Position at anchor X/Z, but at floor + height
            float totalHeight = heightAboveFloor + currentHeightOffset;
            targetPosition = new Vector3(
                sharedAnchor.position.x + anchorOffset.x,
                detectedFloorY + totalHeight,
                sharedAnchor.position.z + anchorOffset.z
            );
            targetRotation = sharedAnchor.rotation * Quaternion.Euler(anchorRotationOffset);
        }
        else
        {
            // Use pure anchor-relative positioning
            targetPosition = sharedAnchor.TransformPoint(anchorOffset);
            targetRotation = sharedAnchor.rotation * Quaternion.Euler(anchorRotationOffset);
        }

        if (parentToAnchor)
        {
            transform.SetParent(sharedAnchor, worldPositionStays: true);
            transform.position = targetPosition;
            transform.rotation = targetRotation;
            transform.localScale = localScale;
        }
        else
        {
            transform.position = targetPosition;
            transform.rotation = targetRotation;
        }
    }

    private void Update()
    {
        // If continuous update is enabled, keep updating position
        if (continuousUpdate && isAligned && sharedAnchor != null)
        {
            UpdatePosition();
        }
    }

    // ==================== PUBLIC METHODS FOR RUNTIME ADJUSTMENT ====================

    /// <summary>
    /// Raise the object by one step
    /// </summary>
    public void RaiseHeight()
    {
        if (!allowRuntimeAdjustment) return;
        currentHeightOffset += heightAdjustmentStep;
        if (isAligned) UpdatePosition();
        if (showDebugInfo)
        {
            Debug.Log($"[AnchorRelativeObject] {gameObject.name} raised height to: {currentHeightOffset}");
        }
    }

    /// <summary>
    /// Lower the object by one step
    /// </summary>
    public void LowerHeight()
    {
        if (!allowRuntimeAdjustment) return;
        currentHeightOffset -= heightAdjustmentStep;
        if (isAligned) UpdatePosition();
        if (showDebugInfo)
        {
            Debug.Log($"[AnchorRelativeObject] {gameObject.name} lowered height to: {currentHeightOffset}");
        }
    }

    /// <summary>
    /// Set exact height above floor
    /// </summary>
    public void SetHeightAboveFloor(float height)
    {
        if (!allowRuntimeAdjustment) return;
        currentHeightOffset = height - heightAboveFloor;
        if (isAligned) UpdatePosition();
    }

    /// <summary>
    /// Reset height to default
    /// </summary>
    public void ResetHeight()
    {
        currentHeightOffset = 0f;
        if (isAligned) UpdatePosition();
    }

    /// <summary>
    /// Re-detect floor and realign
    /// </summary>
    public void RedetectFloorAndAlign()
    {
        if (sharedAnchor != null)
        {
            DetectFloor();
            UpdatePosition();
        }
    }

    /// <summary>
    /// Call this to manually set the offset based on current position relative to anchor
    /// Useful for initial setup in editor
    /// </summary>
    [ContextMenu("Capture Current Offset From Anchor")]
    public void CaptureCurrentOffset()
    {
        var anchor = FindLocalizedAnchor();
        if (anchor != null)
        {
            anchorOffset = anchor.InverseTransformPoint(transform.position);
            anchorRotationOffset = (Quaternion.Inverse(anchor.rotation) * transform.rotation).eulerAngles;
        }
    }

    /// <summary>
    /// Force re-alignment to anchor
    /// </summary>
    public void RealignToAnchor()
    {
        if (sharedAnchor != null)
        {
            AlignToAnchor();
        }
        else
        {
            StartCoroutine(WaitForAnchorAndAlign());
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Show the target position in editor
        if (sharedAnchor != null)
        {
            Gizmos.color = Color.green;
            Vector3 targetPos = sharedAnchor.TransformPoint(anchorOffset);
            Gizmos.DrawWireSphere(targetPos, 0.1f);
            Gizmos.DrawLine(sharedAnchor.position, targetPos);
            
            // Show floor detection ray
            if (autoPlaceOnFloor)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(sharedAnchor.position + Vector3.up * 0.5f, 
                               sharedAnchor.position + Vector3.down * maxFloorSearchDistance);
            }
        }
    }
}
