using UnityEngine;
using System.Collections;

public class AlignmentManager : MonoBehaviour
{
    [Header("Alignment Settings")]
    [SerializeField] private float stabilizationDelay = 0.5f; // Wait for anchor to settle
    [SerializeField] private int alignmentIterations = 3; // More iterations for better accuracy
    
    [Header("Periodic Re-alignment")]
    [SerializeField] private bool enablePeriodicAlignment = false; // DISABLED to test drift
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
    }
    
    private IEnumerator AlignmentCoroutine(OVRSpatialAnchor anchor, bool enablePeriodic = true)
    {
        var anchorTransform = anchor.transform;

        // Wait for anchor to stabilize before aligning
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


            yield return new WaitForEndOfFrame();
        }

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

        yield return new WaitForSeconds(stabilizationDelay);

        for (var alignmentCount = alignmentIterations; alignmentCount > 0; alignmentCount--)
        {
            // 1. Reset Rig to Local Identity to measure raw offsets
            _cameraRigTransform.position = Vector3.zero;
            _cameraRigTransform.eulerAngles = Vector3.zero;
            yield return null;

            // DEBUG: Log anchor positions after rig reset

            // 2. Calculate Correction Rotation
            Vector3 realVector = secondaryTransform.position - primaryTransform.position;
            realVector.y = 0; // Flatten to horizontal plane
            
            if (realVector.sqrMagnitude < 0.001f)
            {
                _cameraRigTransform.position = primaryTransform.InverseTransformPoint(Vector3.zero);
                _cameraRigTransform.eulerAngles = new Vector3(0, -primaryTransform.eulerAngles.y, 0);
            }
            else
            {
                // We want Real Vector to align with Virtual Forward (+Z)
                float realHeading = Quaternion.LookRotation(realVector).eulerAngles.y;
                Vector3 targetRot = new Vector3(0, -realHeading, 0);

                // Apply Rotation
                _cameraRigTransform.eulerAngles = targetRot;
                
                // 3. Calculate Correction Position (After Rotation)
                // Keep Y at 0 to trust Guardian floor calibration
                Vector3 targetPos = -primaryTransform.position;
                targetPos.y = 0; // Trust Guardian floor
                _cameraRigTransform.position = targetPos;
            }
            
            yield return new WaitForEndOfFrame();
        }

        // DEBUG: Log final rig state
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
        
        while (_isAligned && _currentAnchor != null && _currentAnchor.Localized)
        {
            yield return new WaitForSeconds(realignmentInterval);
            
            if (_currentAnchor == null || !_currentAnchor.Localized)
            {
                break;
            }
            
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
                    
                }
                else
                {
                    // ORIGINAL 2-POINT PERIODIC LOGIC (full rotation recalculation)
                    Vector3 realVector = _secondaryAnchor.transform.position - _currentAnchor.transform.position;
                    realVector.y = 0; 
                    
                    float realHeading = Quaternion.LookRotation(realVector).eulerAngles.y;
                    targetRotation = new Vector3(0, -realHeading, 0);
                    
                    // Calculate position accounting for rotation pivot
                    Vector3 anchorLocal = _cameraRigTransform.InverseTransformPoint(_currentAnchor.transform.position);
                    Vector3 anchorRotatedVector = Quaternion.Euler(targetRotation) * anchorLocal;
                    targetPosition = -anchorRotatedVector;
                    targetPosition.y = 0; // Trust Guardian floor
                }
            }
            else
            {
                // SINGLE POINT LOGIC
                targetPosition = _currentAnchor.transform.InverseTransformPoint(Vector3.zero);
                targetRotation = new Vector3(0, -_currentAnchor.transform.eulerAngles.y, 0);
            }
            
            // Calculate drift from current position
            Vector3 currentPos = _cameraRigTransform.position;
            Vector3 currentRot = _cameraRigTransform.eulerAngles;
            float posDrift = Vector3.Distance(currentPos, targetPosition);
            float rotDrift = Mathf.Abs(Mathf.DeltaAngle(currentRot.y, targetRotation.y));
            
            // Only realign if drift exceeds threshold
            if (posDrift < positionDriftThreshold && rotDrift < rotationDriftThreshold)
            {
                continue; // Skip this cycle, wait for next interval
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
            yield break;
        }
        
        
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
        
    }
    
    private void OnDisable()
    {
        StopPeriodicAlignment();
    }
}