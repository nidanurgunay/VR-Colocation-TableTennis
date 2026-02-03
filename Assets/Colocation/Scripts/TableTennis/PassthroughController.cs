using UnityEngine;

/// <summary>
/// Controls passthrough visibility. Add to TableTennis scene to disable passthrough.
/// </summary>
public class PassthroughController : MonoBehaviour
{
    [Header("Passthrough Settings")]
    [SerializeField] private bool enablePassthrough = false; // Set to false for VR-only
    [SerializeField] private Color backgroundColor = Color.black;

    private OVRPassthroughLayer passthroughLayer;

    private void Start()
    {
        ConfigurePassthrough();
    }

    private void ConfigurePassthrough()
    {
        // Find and configure passthrough layer
        passthroughLayer = FindObjectOfType<OVRPassthroughLayer>();
        
        if (passthroughLayer != null)
        {
            passthroughLayer.enabled = enablePassthrough;
            Debug.Log($"[PassthroughController] Passthrough {(enablePassthrough ? "enabled" : "disabled")}");
        }
        else
        {
            Debug.Log("[PassthroughController] No OVRPassthroughLayer found");
        }

        // Configure camera background
        ConfigureCameraBackground();
    }

    private void ConfigureCameraBackground()
    {
        // Find all cameras and set their background
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            if (enablePassthrough)
            {
                // For passthrough, camera needs to show the passthrough layer
                mainCamera.clearFlags = CameraClearFlags.SolidColor;
                mainCamera.backgroundColor = new Color(0, 0, 0, 0); // Transparent for passthrough
            }
            else
            {
                // For VR-only, use solid color or skybox
                mainCamera.clearFlags = CameraClearFlags.SolidColor;
                mainCamera.backgroundColor = backgroundColor;
            }
            Debug.Log($"[PassthroughController] Camera background set to {mainCamera.clearFlags}");
        }

        // Also configure OVRManager if present
        OVRManager ovrManager = FindObjectOfType<OVRManager>();
        if (ovrManager != null)
        {
            ovrManager.isInsightPassthroughEnabled = enablePassthrough;
        }
    }

    /// <summary>
    /// Toggle passthrough at runtime (e.g., from a button)
    /// </summary>
    public void TogglePassthrough()
    {
        enablePassthrough = !enablePassthrough;
        ConfigurePassthrough();
    }

    /// <summary>
    /// Set passthrough state
    /// </summary>
    public void SetPassthrough(bool enabled)
    {
        enablePassthrough = enabled;
        ConfigurePassthrough();
    }
}
