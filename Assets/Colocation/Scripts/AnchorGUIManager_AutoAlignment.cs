using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Collections.Generic;

#if FUSION2
using Fusion;
#endif

/// <summary>
/// Simplified Anchor GUI Manager - Only Auto Align and Spawn Cube
/// Session management is handled by ColocationManager
/// 
/// IMPORTANT: Make sure your UI Canvas is set to "Screen Space - Overlay" 
/// (not parented to camera rig) to prevent it from moving when alignment happens.
/// </summary>
public class AnchorAutoGUIManager : MonoBehaviour
{
    [Header("UI Buttons")]
    [SerializeField] private Button autoAlignButton;
    [SerializeField] private Button spawnCubeButton;

    [Header("Status Display")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI anchorText;
    [SerializeField] private Image statusIndicator;

    [Header("Settings")]
    [SerializeField] private AlignmentManager alignmentManager;
    [SerializeField] private Color alignedColor = Color.green;
    [SerializeField] private Color notAlignedColor = Color.red;
    [SerializeField] private float anchorScale = 0.1f;

    private List<OVRSpatialAnchor> currentAnchors;
    private Transform cameraTransform;
    private GameObject anchorMarkerPrefab;

#if FUSION2
    private NetworkRunner networkRunner;
    private CubeSpawner cubeSpawner;
#endif

    private void Start()
    {
        currentAnchors = new List<OVRSpatialAnchor>();

        cameraTransform = Camera.main?.transform;
        if (cameraTransform == null)
        {
            LogStatus("No main camera found!");
            return;
        }

        anchorMarkerPrefab = Resources.Load<GameObject>("AnchorMarker");
        if (anchorMarkerPrefab == null)
        {
            anchorMarkerPrefab = Resources.Load<GameObject>("AnchorCursorSphere");
        }

        if (alignmentManager == null)
        {
            alignmentManager = FindObjectOfType<AlignmentManager>();
        }

        autoAlignButton?.onClick.AddListener(OnAutoAlignClicked);
        spawnCubeButton?.onClick.AddListener(OnSpawnCubeClicked);

#if FUSION2
        networkRunner = FindObjectOfType<NetworkRunner>();
#endif

        UpdateAllUI();
        LogStatus("Ready - Click Auto Align to start");
    }

    private void Update()
    {
        UpdateStatusIndicator();
        UpdateButtonStates();
        
#if FUSION2
        if (networkRunner == null)
        {
            networkRunner = FindObjectOfType<NetworkRunner>();
        }
#endif
    }

    // ==================== AUTO ALIGN ====================

    private async void OnAutoAlignClicked()
    {
        if (cameraTransform == null)
        {
            LogStatus("Camera not found!");
            return;
        }

        LogStatus("Creating alignment anchor...");
        await CreateAndAlignAnchor();
    }

    private async System.Threading.Tasks.Task CreateAndAlignAnchor()
    {
        try
        {
            // Create anchor 30cm in front of camera
            Vector3 anchorPosition = cameraTransform.position + cameraTransform.forward * 0.3f;
            Quaternion anchorRotation = Quaternion.Euler(0, cameraTransform.eulerAngles.y, 0);

            LogStatus("Creating anchor...");
            var anchor = await CreateAnchorAtPosition(anchorPosition, anchorRotation);

            if (anchor == null)
            {
                LogStatus("Failed to create anchor!");
                return;
            }

            currentAnchors.Add(anchor);

            // Wait for localization
            LogStatus("Localizing anchor...");
            int timeout = 100;
            while (!anchor.Localized && timeout > 0)
            {
                await System.Threading.Tasks.Task.Delay(10);
                timeout--;
            }

            if (!anchor.Localized)
            {
                LogStatus("Anchor not fully localized");
                UpdateAllUI();
                return;
            }

            // Align camera rig
            if (alignmentManager != null)
            {
                LogStatus("Aligning camera rig...");
                alignmentManager.AlignUserToAnchor(anchor);
                await System.Threading.Tasks.Task.Delay(500);
            }

            LogStatus("ALIGNED! Ready to spawn cubes.");
            UpdateAllUI();
        }
        catch (Exception e)
        {
            LogStatus($"Error: {e.Message}");
            Debug.LogError($"[AnchorGUI] CreateAndAlignAnchor error: {e}");
        }
    }

    // ==================== SPAWN CUBE ====================

    private void OnSpawnCubeClicked()
    {
#if FUSION2
        if (networkRunner == null || !networkRunner.IsRunning)
        {
            LogStatus("No network session! Start from ColocationManager.");
            return;
        }

        if (!IsAlignmentComplete())
        {
            LogStatus("Not aligned! Click Auto Align first.");
            return;
        }

        if (cubeSpawner == null)
        {
            cubeSpawner = FindObjectOfType<CubeSpawner>();
        }

        if (cubeSpawner != null)
        {
            // Get right controller position for spawn location
            Vector3 spawnPos = GetControllerSpawnPosition();
            cubeSpawner.SpawnCubeAtPosition(spawnPos, Quaternion.identity);
            LogStatus("Spawning cube at controller!");
        }
        else
        {
            LogStatus("CubeSpawner not found!");
        }
#else
        LogStatus("Photon Fusion not available!");
#endif
    }

    private Vector3 GetControllerSpawnPosition()
    {
        // Try to get right controller position
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null && cameraRig.rightControllerAnchor != null)
        {
            Transform rightHand = cameraRig.rightControllerAnchor;
            // Spawn slightly in front of the controller
            return rightHand.position + rightHand.forward * 0.2f;
        }

        // Fallback to camera forward if controller not found
        if (cameraTransform != null)
        {
            return cameraTransform.position + cameraTransform.forward * 0.5f + Vector3.up * 0.3f;
        }

        return Vector3.zero;
    }

    // ==================== ANCHOR HELPERS ====================

    private async System.Threading.Tasks.Task<OVRSpatialAnchor> CreateAnchorAtPosition(Vector3 position, Quaternion rotation)
    {
        try
        {
            var anchorGO = new GameObject("Anchor_" + DateTime.Now.ToString("HHmmss"));
            anchorGO.transform.position = position;
            anchorGO.transform.rotation = rotation;

            if (anchorMarkerPrefab != null)
            {
                GameObject visual = Instantiate(anchorMarkerPrefab, anchorGO.transform);
                visual.name = "Visual";
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                
                float validScale = Mathf.Max(anchorScale, 0.01f);
                visual.transform.localScale = Vector3.one * validScale;
                
                // Remove physics components
                foreach (var col in visual.GetComponentsInChildren<Collider>())
                    Destroy(col);
                foreach (var rb in visual.GetComponentsInChildren<Rigidbody>())
                    Destroy(rb);
            }

            var spatialAnchor = anchorGO.AddComponent<OVRSpatialAnchor>();

            int timeout = 100;
            while (!spatialAnchor.Created && timeout > 0)
            {
                await System.Threading.Tasks.Task.Yield();
                timeout--;
            }

            if (!spatialAnchor.Created)
            {
Debug.LogError($"[AnchorGUI] Anchor creation timed out");
            Destroy(anchorGO);
            return null;
        }

        Debug.Log($"[AnchorGUI] Anchor created: {spatialAnchor.Uuid}");
        return spatialAnchor;
    }
    catch (Exception e)
    {
        Debug.LogError($"[AnchorGUI] Anchor creation error: {e.Message}");
            return null;
        }
    }

    // ==================== UI UPDATES ====================

    private void UpdateAllUI()
    {
        UpdateAnchorText();
        UpdateButtonStates();
        UpdateStatusIndicator();
    }

    private void UpdateAnchorText()
    {
        if (anchorText == null) return;

        var sb = new System.Text.StringBuilder();
        
        int localizedCount = 0;
        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.Localized)
                localizedCount++;
        }

        sb.AppendLine($"Anchors: {currentAnchors.Count} ({localizedCount} localized)");
        
        if (localizedCount > 0)
            sb.AppendLine("ALIGNED - Ready!");
        else if (currentAnchors.Count > 0)
            sb.AppendLine("ALIGNING...");
        else
            sb.AppendLine("NOT ALIGNED");

        sb.AppendLine("================================");

        int index = 1;
        foreach (var anchor in currentAnchors)
        {
            if (anchor == null) continue;

            sb.AppendLine($"\nAnchor #{index}");
            sb.AppendLine($"  {(anchor.Localized ? "✓" : "||")} {anchor.Uuid.ToString().Substring(0, 8)}");
            
            Vector3 pos = anchor.transform.position;
            sb.AppendLine($"  Pos: ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
            index++;
        }

        anchorText.text = sb.ToString();
    }

    private void UpdateButtonStates()
    {
        bool isAligned = IsAlignmentComplete();
        
#if FUSION2
        bool hasNetwork = networkRunner != null && networkRunner.IsRunning;
#else
        bool hasNetwork = false;
#endif

        if (autoAlignButton != null)
            autoAlignButton.interactable = !isAligned;

        if (spawnCubeButton != null)
            spawnCubeButton.interactable = hasNetwork && isAligned;
    }

    private void UpdateStatusIndicator()
    {
        if (statusIndicator == null) return;

        bool isAligned = IsAlignmentComplete();
        statusIndicator.color = isAligned ? alignedColor : notAlignedColor;
    }

    private bool IsAlignmentComplete()
    {
        if (currentAnchors == null || currentAnchors.Count == 0)
            return false;

        foreach (var anchor in currentAnchors)
        {
            if (anchor != null && anchor.Localized)
                return true;
        }

        return false;
    }

    private void LogStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message;
        }

        Debug.Log($"[AnchorGUI] {message}");
    }
}
