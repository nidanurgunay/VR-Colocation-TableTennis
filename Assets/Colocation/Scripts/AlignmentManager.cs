using UnityEngine;
using System.Collections;

public class AlignmentManager : MonoBehaviour
{
    [Header("Alignment Settings")]
    [SerializeField] private float stabilizationDelay = 0.5f; // Wait for anchor to settle
    [SerializeField] private int alignmentIterations = 3; // More iterations for better accuracy
    
    [Header("Periodic Re-alignment")]
    [SerializeField] private bool enablePeriodicAlignment = true; // ENABLED to reduce drift
    [SerializeField] private float realignmentInterval = 5.0f; // Check every 5 seconds
    [SerializeField] private bool smoothRealignment = true; // Smoothly interpolate instead of snap
    [SerializeField] private float smoothSpeed = 2.0f; // How fast to interpolate
    [Tooltip("If true, periodic 2-point alignment only corrects position (more stable). If false, uses full rotation recalculation (original logic).")]
    [SerializeField] private bool positionOnlyPeriodicFor2Point = true; // Simpler mode for 2-point periodic (more stable)

    [Header("Drift Thresholds (only realign if drift exceeds these)")]
    [SerializeField] private float positionDriftThreshold = 0.03f; // 3cm position drift (reduced from 5cm)
    [SerializeField] private float rotationDriftThreshold = 1.5f; // 1.5 degrees rotation drift (reduced from 2°)
    
    private Transform _cameraRigTransform;
    private OVRSpatialAnchor _currentAnchor; // Primary
    private OVRSpatialAnchor _secondaryAnchor; // Secondary (for 2-point alignment)
    private Coroutine _periodicAlignmentCoroutine;
    private bool _isAligned = false;


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

    public void AlignUserToTwoAnchors(OVRSpatialAnchor primaryAnchor, OVRSpatialAnchor secondaryAnchor)
    {
        if (!primaryAnchor || !primaryAnchor.Localized || !secondaryAnchor || !secondaryAnchor.Localized)
        {
            Debug.LogError("Colocation: Invalid or unlocalized anchors for 2-point alignment.");
            return;
        }

        Debug.Log($"Colocation: Starting 2-point alignment. Primary: {primaryAnchor.Uuid}, Secondary: {secondaryAnchor.Uuid}");
        
        _currentAnchor = primaryAnchor; 
        _secondaryAnchor = secondaryAnchor;
        StartCoroutine(TwoPointAlignmentCoroutine(primaryAnchor, secondaryAnchor));
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

    private IEnumerator TwoPointAlignmentCoroutine(OVRSpatialAnchor primary, OVRSpatialAnchor secondary)
    {
        var primaryTransform = primary.transform;
        var secondaryTransform = secondary.transform;

        // DEBUG: Log anchor UUIDs and initial positions
        Debug.Log($"[COLOC] Primary Anchor UUID: {primary.Uuid}");
        Debug.Log($"[COLOC] Secondary Anchor UUID: {secondary.Uuid}");
        Debug.Log($"[COLOC] Primary Initial Pos: {primaryTransform.position}, Rot: {primaryTransform.eulerAngles}");
        Debug.Log($"[COLOC] Secondary Initial Pos: {secondaryTransform.position}, Rot: {secondaryTransform.eulerAngles}");

        Debug.Log($"Colocation: Waiting {stabilizationDelay}s for anchors to stabilize...");
        yield return new WaitForSeconds(stabilizationDelay);

        for (var alignmentCount = alignmentIterations; alignmentCount > 0; alignmentCount--)
        {
            // 1. Reset Rig to Local Identity to measure raw offsets
            _cameraRigTransform.position = Vector3.zero;
            _cameraRigTransform.eulerAngles = Vector3.zero;
            yield return null;

            // DEBUG: Log anchor positions after rig reset
            Debug.Log($"[COLOC] After reset - Primary Pos: {primaryTransform.position}, Secondary Pos: {secondaryTransform.position}");

            // 2. Calculate Correction Rotation
            Vector3 realVector = secondaryTransform.position - primaryTransform.position;
            Debug.Log($"[COLOC] Raw realVector (P->S): {realVector}");
            realVector.y = 0; // Flatten to horizontal plane
            Debug.Log($"[COLOC] Flattened realVector: {realVector}, magnitude: {realVector.magnitude}");
            
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
                Vector3 targetRot = new Vector3(0, -realHeading, 0);
                Debug.Log($"[COLOC] realHeading: {realHeading}, targetRot: {targetRot}");

                // Apply Rotation
                _cameraRigTransform.eulerAngles = targetRot;
                Debug.Log($"[COLOC] Applied Rig Rotation: {_cameraRigTransform.eulerAngles}");
                
                // 3. Calculate Correction Position (After Rotation)
                // Keep Y at 0 to trust Guardian floor calibration
                Vector3 targetPos = -primaryTransform.position;
                targetPos.y = 0; // Trust Guardian floor
                _cameraRigTransform.position = targetPos;
                Debug.Log($"[COLOC] Applied Rig Position: {_cameraRigTransform.position}");
            }
            
            Debug.Log($"Colocation: 2-Point Iteration {alignmentIterations - alignmentCount + 1}");
            yield return new WaitForEndOfFrame();
        }

        // DEBUG: Log final rig state
        Debug.Log($"[COLOC] FINAL Rig Position: {_cameraRigTransform.position}, Rotation: {_cameraRigTransform.eulerAngles}");
        Debug.Log("Colocation: 2-Point alignment complete.");
        _isAligned = true;
        
        // Start periodic re-alignment if enabled
        if (enablePeriodicAlignment)
        {
            if (_periodicAlignmentCoroutine != null)
            {
                StopCoroutine(_periodicAlignmentCoroutine);
            }
            _periodicAlignmentCoroutine = StartCoroutine(PeriodicAlignmentCoroutine());
        }
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
            
            // DEBUG: Log current state before periodic alignment
            Debug.Log($"[COLOC] PERIODIC Before - Rig Pos: {_cameraRigTransform.position}, Rot: {_cameraRigTransform.eulerAngles}");
            Debug.Log($"[COLOC] PERIODIC Primary Anchor Pos: {_currentAnchor.transform.position}");
            if (_secondaryAnchor != null)
                Debug.Log($"[COLOC] PERIODIC Secondary Anchor Pos: {_secondaryAnchor.transform.position}");
            
            // Calculate target position/rotation
            Vector3 targetPosition;
            Vector3 targetRotation;
            
            if (_secondaryAnchor != null && _secondaryAnchor.Localized)
            {
                if (positionOnlyPeriodicFor2Point)
                {
                    // SIMPLIFIED 2-POINT PERIODIC LOGIC (more stable)
                    // Keep current rotation stable, only correct position drift.
                    // The rotation was established correctly during initial 2-point alignment.
                    
                    targetRotation = _cameraRigTransform.eulerAngles;
                    
                    // For position: we want primary anchor to appear at world origin (0,0,0)
                    Vector3 anchorWorldPos = _currentAnchor.transform.position;
                    targetPosition = _cameraRigTransform.position - anchorWorldPos;
                    targetPosition.y = 0; // Trust Guardian floor
                    
                    Debug.Log($"[COLOC] PERIODIC 2-Point (position-only) targetPosition: {targetPosition}, keeping rotation: {targetRotation}");
                }
                else
                {
                    // ORIGINAL 2-POINT PERIODIC LOGIC (full rotation recalculation)
                    Vector3 realVector = _secondaryAnchor.transform.position - _currentAnchor.transform.position;
                    Debug.Log($"[COLOC] PERIODIC 2-Point realVector (before flatten): {realVector}");
                    realVector.y = 0; 
                    Debug.Log($"[COLOC] PERIODIC 2-Point realVector (after flatten): {realVector}");
                    
                    float realHeading = Quaternion.LookRotation(realVector).eulerAngles.y;
                    targetRotation = new Vector3(0, -realHeading, 0);
                    Debug.Log($"[COLOC] PERIODIC realHeading: {realHeading}, targetRotation: {targetRotation}");
                    
                    // Calculate position accounting for rotation pivot
                    Vector3 anchorLocal = _cameraRigTransform.InverseTransformPoint(_currentAnchor.transform.position);
                    Vector3 anchorRotatedVector = Quaternion.Euler(targetRotation) * anchorLocal;
                    targetPosition = -anchorRotatedVector;
                    targetPosition.y = 0; // Trust Guardian floor
                    Debug.Log($"[COLOC] PERIODIC 2-Point targetPosition: {targetPosition}");
                }
            }
            else
            {
                // SINGLE POINT LOGIC
                targetPosition = _currentAnchor.transform.InverseTransformPoint(Vector3.zero);
                targetRotation = new Vector3(0, -_currentAnchor.transform.eulerAngles.y, 0);
                Debug.Log($"[COLOC] PERIODIC Single-Point targetPos: {targetPosition}, targetRot: {targetRotation}");
            }
            
            // Calculate drift from current position
            Vector3 currentPos = _cameraRigTransform.position;
            Vector3 currentRot = _cameraRigTransform.eulerAngles;
            float posDrift = Vector3.Distance(currentPos, targetPosition);
            float rotDrift = Mathf.Abs(Mathf.DeltaAngle(currentRot.y, targetRotation.y));
            
            // Only realign if drift exceeds threshold
            if (posDrift < positionDriftThreshold && rotDrift < rotationDriftThreshold)
            {
                Debug.Log($"[COLOC] PERIODIC Drift below threshold (Pos: {posDrift:F3}m < {positionDriftThreshold}m, Rot: {rotDrift:F1}° < {rotationDriftThreshold}°) - skipping realign");
                continue; // Skip this cycle, wait for next interval
            }
            
            Debug.Log($"[COLOC] PERIODIC Drift exceeded threshold! (Pos: {posDrift:F3}m, Rot: {rotDrift:F1}°) - realigning...");
            
            if (smoothRealignment)
            {
                // Smoothly interpolate to target over several frames
                yield return StartCoroutine(SmoothRealignCoroutine(targetPosition, targetRotation));
                Debug.Log($"[COLOC] PERIODIC After smooth realign - Rig Pos: {_cameraRigTransform.position}, Rot: {_cameraRigTransform.eulerAngles}");
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
        // Store starting position/rotation
        Vector3 startPos = _cameraRigTransform.position;
        Vector3 startRot = _cameraRigTransform.eulerAngles;
        
        // Calculate drift from target (passed from periodic alignment calculation)
        Vector3 posDrift = targetPosition - startPos;
        float rotDrift = Mathf.DeltaAngle(startRot.y, targetRotation.y);
        
        // Only correct if drift is significant (more than 1cm or 0.5 degrees)
        if (posDrift.magnitude < 0.01f && Mathf.Abs(rotDrift) < 0.5f)
        {
            // Drift is negligible, no correction needed
            Debug.Log($"[COLOC] Drift negligible - Pos: {posDrift.magnitude:F3}m, Rot: {rotDrift:F1}° - skipping");
            yield break;
        }
        
        Debug.Log($"[COLOC] Correcting drift - Position: {posDrift.magnitude:F3}m, Rotation: {rotDrift:F1}°");
        
        // Smoothly interpolate
        float elapsed = 0f;
        float duration = 1f / smoothSpeed;
        
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0, 1, elapsed / duration);
            
            _cameraRigTransform.position = Vector3.Lerp(startPos, targetPosition, t);
            _cameraRigTransform.eulerAngles = new Vector3(0, Mathf.LerpAngle(startRot.y, targetRotation.y, t), 0);
            
            yield return null;
        }
        
        // Ensure we end at exact target
        _cameraRigTransform.position = targetPosition;
        _cameraRigTransform.eulerAngles = new Vector3(0, targetRotation.y, 0);
        
        Debug.Log($"Colocation: Smooth re-alignment complete");
    }
    
    private void OnDisable()
    {
        StopPeriodicAlignment();
    }
}