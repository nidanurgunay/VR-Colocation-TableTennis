#if FUSION2

using Fusion;
using UnityEngine;

/// <summary>
/// Simple networked cube that spawns and stays in place.
/// Parented to the local spatial anchor to stay aligned across devices.
/// </summary>
public class NetworkedCube : NetworkBehaviour
{
    [Header("Visual Settings")]
    [SerializeField] private Color cubeColor = Color.cyan;

    private Renderer cubeRenderer;
    private MaterialPropertyBlock propertyBlock;

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

        // Parent to local anchor if not already parented (for clients receiving the spawn)
        ParentToLocalAnchor();

        propertyBlock = new MaterialPropertyBlock();
        UpdateVisualState();

        Debug.Log($"[NetworkedCube] Spawned at local pos {transform.localPosition}, parent: {(transform.parent != null ? transform.parent.name : "none")}");
    }

    private void ParentToLocalAnchor()
    {
        // If already parented to an anchor, skip
        if (transform.parent != null && transform.parent.GetComponent<OVRSpatialAnchor>() != null)
        {
            Debug.Log("[NetworkedCube] Already parented to anchor");
            return;
        }

        // Find the AnchorAutoGUIManager to get the localized anchor
        var guiManager = FindObjectOfType<AnchorAutoGUIManager>();
        if (guiManager != null)
        {
            var anchor = guiManager.GetLocalizedAnchor();
            if (anchor != null && anchor.Localized)
            {
                // Store current local position before reparenting
                Vector3 localPos = transform.localPosition;
                Quaternion localRot = transform.localRotation;
                Vector3 localScale = transform.localScale;
                
                transform.SetParent(anchor.transform, worldPositionStays: false);
                transform.localPosition = localPos;
                transform.localRotation = localRot;
                transform.localScale = localScale;
                
                Debug.Log($"[NetworkedCube] Parented to anchor {anchor.name} at local pos {localPos}");
                return;
            }
        }

        Debug.LogWarning("[NetworkedCube] Could not find localized anchor to parent to!");
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
