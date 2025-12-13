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
    private OVRSpatialAnchor _currentAnchor;
    private Coroutine _periodicAlignmentCoroutine;
    private bool _isAligned = false;

    private void Awake()
    {
        _cameraRigTransform = FindAnyObjectByType<OVRCameraRig>().transform;
    }
    
    public void AlignUserToAnchor(OVRSpatialAnchor anchor)
    {
        if (!anchor || !anchor.Localized)
        {
            Debug.LogError("Colocation: Invalid or unlocalized anchor. Cannot align.");
            return;
        }

        Debug.Log($"Colocation: Starting alignment to anchor {anchor.Uuid}.");
        
        _currentAnchor = anchor;
        StartCoroutine(AlignmentCoroutine(anchor));
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
        Debug.Log("Colocation: Periodic alignment stopped.");
    }
    
    private IEnumerator AlignmentCoroutine(OVRSpatialAnchor anchor)
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

            _cameraRigTransform.position = anchorTransform.InverseTransformPoint(Vector3.zero);
            _cameraRigTransform.eulerAngles = new Vector3(0, -anchorTransform.eulerAngles.y, 0);

            Debug.Log($"Colocation: Alignment iteration {alignmentIterations - alignmentCount + 1}/{alignmentIterations} - Position: {_cameraRigTransform.position}, Rotation: {_cameraRigTransform.eulerAngles}");

            yield return new WaitForEndOfFrame();
        }

        Debug.Log("Colocation: Initial alignment complete.");
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
            
            // Calculate target position/rotation
            Vector3 targetPosition = _currentAnchor.transform.InverseTransformPoint(Vector3.zero);
            Vector3 targetRotation = new Vector3(0, -_currentAnchor.transform.eulerAngles.y, 0);
            
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