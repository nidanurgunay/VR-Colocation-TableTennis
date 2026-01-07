using UnityEngine;
using System.Collections;

public class AlignmentManager : MonoBehaviour
{
    [Header("Alignment Settings")]
    [SerializeField] private float stabilizationDelay = 0.5f; // Wait for anchor to settle
    [SerializeField] private int alignmentIterations = 3; // More iterations for better accuracy
    
    [Header("Periodic Re-alignment")]
    [SerializeField] private bool enablePeriodicAlignment = true;
    [SerializeField] private float realignmentInterval = 5.0f; // Re-align every X seconds
    [SerializeField] private bool smoothRealignment = true; // Smoothly interpolate instead of snap
    [SerializeField] private float smoothSpeed = 2.0f; // How fast to interpolate
    
    private Transform _cameraRigTransform;
    private OVRSpatialAnchor _currentAnchor; // Primary
    private OVRSpatialAnchor _secondaryAnchor; // Secondary (for 2-point alignment)
    private Coroutine _periodicAlignmentCoroutine;
    private bool _isAligned = false;
    private float _additionalYRotation = 0f; // Additional Y rotation for client alignment (180° for opposite side)

    private void Awake()
    {
        _cameraRigTransform = FindAnyObjectByType<OVRCameraRig>().transform;
    }
    
    public void AlignUserToAnchor(OVRSpatialAnchor anchor)
    {
        AlignUserToAnchor(anchor, enablePeriodicAlignment);
    }
    
    /// <summary>
    /// Align user to anchor with option to enable/disable periodic re-alignment
    /// </summary>
    /// <param name="anchor">The anchor to align to</param>
    /// <param name="enablePeriodic">Whether to enable periodic re-alignment after initial alignment</param>
    public void AlignUserToAnchor(OVRSpatialAnchor anchor, bool enablePeriodic)
    {
        if (!anchor || !anchor.Localized)
        {
            Debug.LogError("Colocation: Invalid or unlocalized anchor. Cannot align.");
            return;
        }

        Debug.Log($"Colocation: Starting alignment to anchor {anchor.Uuid}. Periodic: {enablePeriodic}");
        
        _currentAnchor = anchor;
        _secondaryAnchor = null; // Clear secondary
        StartCoroutine(AlignmentCoroutine(anchor, enablePeriodic));
    }

    public void AlignUserToTwoAnchors(OVRSpatialAnchor primaryAnchor, OVRSpatialAnchor secondaryAnchor, float additionalYRotation = 0f)
    {
        if (!primaryAnchor || !primaryAnchor.Localized || !secondaryAnchor || !secondaryAnchor.Localized)
        {
            Debug.LogError("Colocation: Invalid or unlocalized anchors for 2-point alignment.");
            return;
        }

        Debug.Log($"Colocation: Starting 2-point alignment. Primary: {primaryAnchor.Uuid}, Secondary: {secondaryAnchor.Uuid}, AdditionalRotation: {additionalYRotation}");
        
        _currentAnchor = primaryAnchor; 
        _secondaryAnchor = secondaryAnchor;
        _additionalYRotation = additionalYRotation; // Store for periodic re-alignment
        StartCoroutine(TwoPointAlignmentCoroutine(primaryAnchor, secondaryAnchor, additionalYRotation));
    }
    
    /// <summary>
    /// Stops periodic alignment. Call when resetting or leaving session.
    /// </summary>
    public void StopPeriodicAlignment()
    {
        if (_periodicAlignmentCoroutine != null)
        {
            StopCoroutine(_periodicAlignmentCoroutine);
            _periodicAlignmentCoroutine = null;
        }
        _isAligned = false;
        _currentAnchor = null;
        _secondaryAnchor = null;
        Debug.Log("Colocation: Periodic alignment stopped.");
    }
    
    private IEnumerator AlignmentCoroutine(OVRSpatialAnchor anchor, bool enablePeriodic = true)
    {
        var anchorTransform = anchor.transform;

        // Wait for anchor to stabilize before aligning
        Debug.Log($"Colocation: Waiting {stabilizationDelay}s for anchor to stabilize...");
        yield return new WaitForSeconds(stabilizationDelay);

        // Perform multiple alignment iterations for better accuracy
        for (var alignmentCount = alignmentIterations; alignmentCount > 0; alignmentCount--)
        {
            _cameraRigTransform.position = Vector3.zero;
            _cameraRigTransform.eulerAngles = Vector3.zero;

            yield return null;

            // Align X/Z position and Y rotation only
            // Y position is kept at 0 to trust Guardian floor calibration
            Vector3 anchorPos = anchorTransform.InverseTransformPoint(Vector3.zero);
            _cameraRigTransform.position = new Vector3(anchorPos.x, 0, anchorPos.z);
            _cameraRigTransform.eulerAngles = new Vector3(0, -anchorTransform.eulerAngles.y, 0);

            Debug.Log($"Colocation: Alignment iteration {alignmentIterations - alignmentCount + 1}/{alignmentIterations} - Position: {_cameraRigTransform.position}, Rotation: {_cameraRigTransform.eulerAngles}");

            yield return new WaitForEndOfFrame();
        }

        Debug.Log("Colocation: Initial alignment complete.");
        _isAligned = true;
        
        // Start periodic re-alignment if enabled and requested
        if (enablePeriodic && enablePeriodicAlignment)
        {
            if (_periodicAlignmentCoroutine != null)
            {
                StopCoroutine(_periodicAlignmentCoroutine);
            }
            _periodicAlignmentCoroutine = StartCoroutine(PeriodicAlignmentCoroutine());
        }
    }

    private IEnumerator TwoPointAlignmentCoroutine(OVRSpatialAnchor primary, OVRSpatialAnchor secondary, float additionalYRotation = 0f)
    {
        var primaryTransform = primary.transform;
        var secondaryTransform = secondary.transform;

        Debug.Log($"Colocation: Waiting {stabilizationDelay}s for anchors to stabilize...");
        yield return new WaitForSeconds(stabilizationDelay);

        for (var alignmentCount = alignmentIterations; alignmentCount > 0; alignmentCount--)
        {
            // 1. Reset Rig to Local Identity to measure raw offsets
            _cameraRigTransform.position = Vector3.zero;
            _cameraRigTransform.eulerAngles = Vector3.zero;
            yield return null; // Wait for physics/transform update? Actually standard Unity update is immediate but OVR might need frame.
            // Safe to force update if needed, but yield is safer.

            // 2. Calculate Correction Rotation
            Vector3 realVector = secondaryTransform.position - primaryTransform.position;
            realVector.y = 0; 
            
            if (realVector.sqrMagnitude < 0.001f)
            {
                Debug.LogWarning("Colocation: Anchors too close! Fallback.");
                _cameraRigTransform.position = primaryTransform.InverseTransformPoint(Vector3.zero);
                _cameraRigTransform.eulerAngles = new Vector3(0, -primaryTransform.eulerAngles.y, 0);
            }
            else
            {
                // We want Real Vector to align with Virtual Forward (+Z)
                float realHeading = Quaternion.LookRotation(realVector).eulerAngles.y;
                // Add additional rotation (e.g., 180° for client to face opposite direction)
                Vector3 targetRot = new Vector3(0, -realHeading + additionalYRotation, 0);

                // Apply Rotation
                _cameraRigTransform.eulerAngles = targetRot;
                
                 // 3. Calculate Correction Position (After Rotation)
                 // Keep Y at 0 to trust Guardian floor calibration
                 Vector3 targetPos = -primaryTransform.position;
                 targetPos.y = 0; // Trust Guardian floor
                 _cameraRigTransform.position = targetPos;
            }
            
            Debug.Log($"Colocation: 2-Point Iteration {alignmentIterations - alignmentCount + 1}");
            yield return new WaitForEndOfFrame();
        }

        Debug.Log($"Colocation: 2-Point alignment complete. Additional rotation: {additionalYRotation}");
        _isAligned = true;
        // Periodic alignment not fully implemented for 2-points yet in this snippet
    }
    
    private IEnumerator PeriodicAlignmentCoroutine()
    {
        Debug.Log($"Colocation: Starting periodic re-alignment every {realignmentInterval}s");
        
        while (_isAligned && _currentAnchor != null && _currentAnchor.Localized)
        {
            yield return new WaitForSeconds(realignmentInterval);
            
            if (_currentAnchor == null || !_currentAnchor.Localized)
            {
                Debug.Log("Colocation: Anchor lost, stopping periodic alignment.");
                break;
            }
            
            // Calculate target position/rotation
            Vector3 targetPosition;
            Vector3 targetRotation;
            
            if (_secondaryAnchor != null && _secondaryAnchor.Localized)
            {
               // 2-POINT LOGIC
                Vector3 realVector = _secondaryAnchor.transform.position - _currentAnchor.transform.position;
                realVector.y = 0; 
                
                float realHeading = Quaternion.LookRotation(realVector).eulerAngles.y;
                // Apply the stored additional rotation (e.g., 180° for client)
                targetRotation = new Vector3(0, -realHeading + _additionalYRotation, 0);
                
                // For position, we effectively want to apply the translation that brings _currentAnchor to 0,0,0
                // BUT we must account for the pivot of rotation.
                // Since this is SmoothRealign, we are calculating the DESTINATION Rig Pose.
                // If Rig was at TargetRotation...
                // Anchor would be at `RotatedPos`.
                // We want to translate by `-RotatedPos`.
                // RotatedPos = RefRig.TransformPoint(AnchorLocal).
                // RefRig is (0,0,0) with rotation TargetRotation.
                // AnchorLocal = Rig_Current.InverseTransformPoint(Anchor_Current).
                // So:
                // 1. Get Anchor Local Pos relative to current rig:
                Vector3 anchorLocal = _cameraRigTransform.InverseTransformPoint(_currentAnchor.transform.position);
                // 2. Rotate this local pos by Target Rotation:
                Vector3 anchorRotatedVector = Quaternion.Euler(targetRotation) * anchorLocal;
                // 3. Target Position is negative of that:
                targetPosition = -anchorRotatedVector;
                
                // Note: This assumes Rig Origin is the pivot.
            }
            else
            {
                // SINGLE POINT LOGIC
                targetPosition = _currentAnchor.transform.InverseTransformPoint(Vector3.zero);
                targetRotation = new Vector3(0, -_currentAnchor.transform.eulerAngles.y, 0);
            }
            
            if (smoothRealignment)
            {
                // Smoothly interpolate to target over several frames
                yield return StartCoroutine(SmoothRealignCoroutine(targetPosition, targetRotation));
            }
            else
            {
                // Snap to target
                _cameraRigTransform.position = Vector3.zero;
                _cameraRigTransform.eulerAngles = Vector3.zero;
                yield return null;
                
                _cameraRigTransform.position = targetPosition;
                _cameraRigTransform.eulerAngles = targetRotation;
                
                Debug.Log($"Colocation: Periodic re-alignment (snap) - Pos: {targetPosition}, Rot: {targetRotation}");
            }
        }
        
        _periodicAlignmentCoroutine = null;
    }
    
    private IEnumerator SmoothRealignCoroutine(Vector3 targetPosition, Vector3 targetRotation)
    {
        // First reset to origin to get proper transform
        Vector3 startPos = _cameraRigTransform.position;
        Vector3 startRot = _cameraRigTransform.eulerAngles;
        
        // Calculate the difference (drift amount)
        _cameraRigTransform.position = Vector3.zero;
        _cameraRigTransform.eulerAngles = Vector3.zero;
        yield return null;
        
        Vector3 correctPos = _currentAnchor.transform.InverseTransformPoint(Vector3.zero);
        Vector3 correctRot = new Vector3(0, -_currentAnchor.transform.eulerAngles.y, 0);
        
        // Calculate drift
        Vector3 posDrift = correctPos - startPos;
        float rotDrift = Mathf.DeltaAngle(startRot.y, correctRot.y);
        
        // Only correct if drift is significant (more than 1cm or 0.5 degrees)
        if (posDrift.magnitude < 0.01f && Mathf.Abs(rotDrift) < 0.5f)
        {
            // Restore original position - drift is negligible
            _cameraRigTransform.position = startPos;
            _cameraRigTransform.eulerAngles = startRot;
            yield break;
        }
        
        Debug.Log($"Colocation: Correcting drift - Position: {posDrift.magnitude:F3}m, Rotation: {rotDrift:F1}°");
        
        // Smoothly interpolate
        float elapsed = 0f;
        float duration = 1f / smoothSpeed;
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0, 1, elapsed / duration);
            
            _cameraRigTransform.position = Vector3.Lerp(startPos, correctPos, t);
            _cameraRigTransform.eulerAngles = new Vector3(0, Mathf.LerpAngle(startRot.y, correctRot.y, t), 0);
            
            yield return null;
        }
        
        // Ensure we end at exact target
        _cameraRigTransform.position = correctPos;
        _cameraRigTransform.eulerAngles = correctRot;
        
        Debug.Log($"Colocation: Smooth re-alignment complete");
    }
    
    private void OnDisable()
    {
        StopPeriodicAlignment();
    }
}