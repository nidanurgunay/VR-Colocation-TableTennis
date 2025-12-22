#if FUSION2

using Fusion;
using UnityEngine;
using System.Collections;

/// <summary>
/// Simple networked cube that spawns and stays in place.
/// Parented to the local spatial anchor to stay aligned across devices.
/// </summary>
public class NetworkedCube : NetworkBehaviour
{
    [Header("Visual Settings")]
    [SerializeField] private Color cubeColor = Color.cyan;
    
    // Networked anchor-relative position (synced from host to clients)
    [Networked] private Vector3 AnchorRelativePosition { get; set; }
    [Networked] private Quaternion AnchorRelativeRotation { get; set; }

    private Renderer cubeRenderer;
    private MaterialPropertyBlock propertyBlock;
    private bool isParentedToAnchor = false;
    private int retryCount = 0;
    private const int MAX_RETRIES = 50; // Try for ~5 seconds

    public override void Spawned()
    {
        base.Spawned();

        cubeRenderer = GetComponent<Renderer>();
        if (cubeRenderer == null)
        {
            cubeRenderer = GetComponentInChildren<Renderer>();
        }

        // Remove any Rigidbody - we want static objects
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            Destroy(rb);
        }

        // If we have state authority (host), store the relative position
        if (Object.HasStateAuthority)
        {
            AnchorRelativePosition = transform.localPosition;
            AnchorRelativeRotation = transform.localRotation;
            Debug.Log($"[NetworkedCube] Host storing anchor-relative pos: {AnchorRelativePosition}");
        }

        propertyBlock = new MaterialPropertyBlock();
        UpdateVisualState();

        // Start trying to parent to anchor (will retry if anchor not ready yet)
        StartCoroutine(TryParentToAnchorRoutine());

        Debug.Log($"[NetworkedCube] Spawned. HasStateAuth: {Object.HasStateAuthority}, localPos: {transform.localPosition}");
    }

    /// <summary>
    /// Retry parenting to anchor until successful or max retries reached
    /// </summary>
    private IEnumerator TryParentToAnchorRoutine()
    {
        while (!isParentedToAnchor && retryCount < MAX_RETRIES)
        {
            if (TryParentToLocalAnchor())
            {
                Debug.Log($"[NetworkedCube] Successfully parented to anchor after {retryCount} attempts");
                yield break;
            }
            
            retryCount++;
            yield return new WaitForSeconds(0.1f); // Retry every 100ms
        }

        if (!isParentedToAnchor)
        {
            Debug.LogError($"[NetworkedCube] Failed to parent to anchor after {MAX_RETRIES} attempts!");
        }
    }

    private bool TryParentToLocalAnchor()
    {
        // If already parented to an anchor, we're done
        if (transform.parent != null && transform.parent.GetComponent<OVRSpatialAnchor>() != null)
        {
            isParentedToAnchor = true;
            return true;
        }

        // Method 1: Find via AnchorAutoGUIManager
        var guiManager = FindObjectOfType<AnchorAutoGUIManager>();
        if (guiManager != null)
        {
            var anchor = guiManager.GetLocalizedAnchor();
            if (anchor != null && anchor.Localized)
            {
                ParentToAnchor(anchor);
                return true;
            }
        }

        // Method 2: Find any localized OVRSpatialAnchor in scene
        var allAnchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in allAnchors)
        {
            if (anchor != null && anchor.Localized)
            {
                Debug.Log($"[NetworkedCube] Found anchor via scene search: {anchor.name}");
                ParentToAnchor(anchor);
                return true;
            }
        }

        return false;
    }

    private void ParentToAnchor(OVRSpatialAnchor anchor)
    {
        // Use the networked anchor-relative position
        Vector3 targetLocalPos = AnchorRelativePosition;
        Quaternion targetLocalRot = AnchorRelativeRotation;
        Vector3 localScale = transform.localScale;
        
        // If networked values aren't set yet (all zeros), use current local position
        if (targetLocalPos == Vector3.zero && !Object.HasStateAuthority)
        {
            // Wait for networked values to sync
            Debug.Log("[NetworkedCube] Waiting for networked position to sync...");
            return;
        }

        transform.SetParent(anchor.transform, worldPositionStays: false);
        transform.localPosition = targetLocalPos;
        transform.localRotation = targetLocalRot;
        transform.localScale = localScale;
        
        isParentedToAnchor = true;
        Debug.Log($"[NetworkedCube] Parented to anchor '{anchor.name}' at local pos {targetLocalPos}");
    }

    private void UpdateVisualState()
    {
        if (cubeRenderer == null || propertyBlock == null) return;

        cubeRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetColor("_Color", cubeColor);
        propertyBlock.SetColor("_BaseColor", cubeColor);
        cubeRenderer.SetPropertyBlock(propertyBlock);
    }

    private void OnDestroy()
    {
        Debug.Log($"[NetworkedCube] Destroyed cube {(Object != null ? Object.Id.ToString() : "unknown")}");
    }
}

#endif
