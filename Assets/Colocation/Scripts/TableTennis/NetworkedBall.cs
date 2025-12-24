using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Networked ping pong ball for colocation table tennis.
/// Host has physics authority, syncs anchor-relative position to clients.
/// </summary>
public class NetworkedBall : NetworkBehaviour
{
    [Header("Physics Settings")]
    [SerializeField] private float gravity = 9.81f;
    [SerializeField] private float bounciness = 0.85f;
    [SerializeField] private float airResistance = 0.02f;
    [SerializeField] private float tableHeight = 0.76f; // Standard table tennis height
    
    [Header("Serve Settings")]
    [SerializeField] private Vector3 servePosition = new Vector3(0, 1.2f, -1f); // Relative to anchor
    [SerializeField] private float serveForce = 3f;
    
    [Header("Sync Settings")]
    [SerializeField] private float syncRate = 30f; // Hz
    
    [Header("Reset Settings")]
    [SerializeField] private float resetBelowY = -1f; // Reset if ball falls below this
    [SerializeField] private float resetAfterSeconds = 5f; // Reset if no activity
    
    // Networked state - anchor relative
    [Networked] private Vector3 AnchorRelativePosition { get; set; }
    [Networked] private Vector3 AnchorRelativeVelocity { get; set; }
    [Networked] private NetworkBool IsInPlay { get; set; }
    
    // Local state
    private Transform sharedAnchor;
    private Rigidbody rb;
    private float lastSyncTime;
    private float lastHitTime;
    private Vector3 localVelocity;
    private bool isInitialized;
    
    // For interpolation on clients
    private Vector3 targetPosition;
    private Vector3 previousPosition;
    private float interpolationTime;
    
    public override void Spawned()
    {
        rb = GetComponent<Rigidbody>();
        
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
        }
        
        // Configure rigidbody
        rb.mass = 0.0027f; // Ping pong ball: 2.7 grams
        rb.drag = airResistance;
        rb.angularDrag = 0.5f;
        rb.useGravity = false; // We handle gravity manually for better control
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        
        // Only host simulates physics
        if (Object.HasStateAuthority)
        {
            rb.isKinematic = false;
            StartCoroutine(TryFindAnchorAndInitialize());
        }
        else
        {
            rb.isKinematic = true;
            StartCoroutine(TryFindAnchorAndInitialize());
        }
        
        Debug.Log($"[NetworkedBall] Spawned. HasStateAuthority: {Object.HasStateAuthority}");
    }
    
    private IEnumerator TryFindAnchorAndInitialize()
    {
        int attempts = 0;
        while (sharedAnchor == null && attempts < 50)
        {
            // Try to find the shared anchor
            var anchors = FindObjectsOfType<OVRSpatialAnchor>();
            foreach (var anchor in anchors)
            {
                if (anchor.gameObject.name.Contains("Shared") || 
                    anchor.gameObject.name.Contains("Anchor"))
                {
                    sharedAnchor = anchor.transform;
                    Debug.Log($"[NetworkedBall] Found anchor: {anchor.gameObject.name}");
                    break;
                }
            }
            
            if (sharedAnchor == null)
            {
                // Also try finding by tag or AlignmentManager reference
                var alignmentManager = FindObjectOfType<AlignmentManager>();
                if (alignmentManager != null)
                {
                    var anchorObj = GameObject.FindGameObjectWithTag("SharedAnchor");
                    if (anchorObj != null)
                    {
                        sharedAnchor = anchorObj.transform;
                    }
                }
            }
            
            attempts++;
            yield return new WaitForSeconds(0.2f);
        }
        
        if (sharedAnchor != null)
        {
            isInitialized = true;
            
            if (Object.HasStateAuthority)
            {
                ResetToServePosition();
            }
            else
            {
                UpdateLocalPositionFromNetwork();
            }
        }
        else
        {
            Debug.LogWarning("[NetworkedBall] Could not find shared anchor after 50 attempts");
            sharedAnchor = new GameObject("FallbackAnchor").transform;
            isInitialized = true;
        }
    }
    
    public override void FixedUpdateNetwork()
    {
        if (!isInitialized || sharedAnchor == null) return;
        
        if (Object.HasStateAuthority)
        {
            SimulatePhysics();
            SyncToNetwork();
            CheckForReset();
        }
    }
    
    private void Update()
    {
        if (!isInitialized || sharedAnchor == null) return;
        
        if (!Object.HasStateAuthority)
        {
            InterpolatePosition();
        }
    }
    
    private void SimulatePhysics()
    {
        localVelocity = rb.velocity;
        localVelocity.y -= gravity * Runner.DeltaTime;
        rb.velocity = localVelocity;
        
        // Simple table bounce
        if (transform.position.y <= tableHeight + 0.02f && localVelocity.y < 0)
        {
            Vector3 relPos = sharedAnchor.InverseTransformPoint(transform.position);
            if (Mathf.Abs(relPos.x) < 0.76f && Mathf.Abs(relPos.z) < 1.37f)
            {
                localVelocity.y = -localVelocity.y * bounciness;
                rb.velocity = localVelocity;
                transform.position = new Vector3(transform.position.x, tableHeight + 0.02f, transform.position.z);
            }
        }
    }
    
    private void SyncToNetwork()
    {
        if (Time.time - lastSyncTime < 1f / syncRate) return;
        lastSyncTime = Time.time;
        
        AnchorRelativePosition = sharedAnchor.InverseTransformPoint(transform.position);
        AnchorRelativeVelocity = sharedAnchor.InverseTransformDirection(rb.velocity);
    }
    
    private void UpdateLocalPositionFromNetwork()
    {
        if (sharedAnchor == null) return;
        
        previousPosition = targetPosition;
        targetPosition = sharedAnchor.TransformPoint(AnchorRelativePosition);
        interpolationTime = 0f;
    }
    
    private void InterpolatePosition()
    {
        interpolationTime += Time.deltaTime * syncRate;
        
        if (interpolationTime <= 1f)
        {
            transform.position = Vector3.Lerp(previousPosition, targetPosition, interpolationTime);
        }
        else
        {
            Vector3 worldVelocity = sharedAnchor.TransformDirection(AnchorRelativeVelocity);
            transform.position = targetPosition + worldVelocity * (interpolationTime - 1f) / syncRate;
        }
    }
    
    public override void Render()
    {
        if (!Object.HasStateAuthority && isInitialized)
        {
            UpdateLocalPositionFromNetwork();
        }
    }
    
    private void CheckForReset()
    {
        if (transform.position.y < resetBelowY)
        {
            Debug.Log("[NetworkedBall] Ball fell below threshold, resetting");
            ResetToServePosition();
        }
        
        if (Time.time - lastHitTime > resetAfterSeconds && IsInPlay)
        {
            Debug.Log("[NetworkedBall] Ball inactive too long, resetting");
            ResetToServePosition();
        }
    }
    
    private void ResetToServePosition()
    {
        if (sharedAnchor == null) return;
        
        Vector3 worldServePos = sharedAnchor.TransformPoint(servePosition);
        transform.position = worldServePos;
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        localVelocity = Vector3.zero;
        
        IsInPlay = false;
        lastHitTime = Time.time;
        
        AnchorRelativePosition = servePosition;
        AnchorRelativeVelocity = Vector3.zero;
        
        Debug.Log($"[NetworkedBall] Reset to serve position: {worldServePos}");
    }
    
    /// <summary>
    /// Called when racket hits the ball. Only processed on host.
    /// </summary>
    public void OnRacketHit(Vector3 hitVelocity, Vector3 hitPoint)
    {
        if (!Object.HasStateAuthority) return;
        
        rb.velocity = hitVelocity;
        localVelocity = hitVelocity;
        IsInPlay = true;
        lastHitTime = Time.time;
        
        Debug.Log($"[NetworkedBall] Hit with velocity: {hitVelocity}");
    }
    
    /// <summary>
    /// Request a serve (can be called by any player)
    /// </summary>
    public void RequestServe(Vector3 direction)
    {
        if (Object.HasStateAuthority)
        {
            Serve(direction);
        }
        else
        {
            RPC_RequestServe(direction);
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestServe(Vector3 direction)
    {
        Serve(direction);
    }
    
    private void Serve(Vector3 direction)
    {
        ResetToServePosition();
        StartCoroutine(ApplyServeVelocity(direction.normalized * serveForce));
    }
    
    private IEnumerator ApplyServeVelocity(Vector3 velocity)
    {
        yield return new WaitForSeconds(0.5f);
        
        rb.velocity = velocity;
        localVelocity = velocity;
        IsInPlay = true;
        lastHitTime = Time.time;
    }
    
    private void OnCollisionEnter(Collision collision)
    {
        if (!Object.HasStateAuthority) return;
        
        if (collision.gameObject.CompareTag("Racket") || 
            collision.gameObject.layer == LayerMask.NameToLayer("Racket"))
        {
            Rigidbody racketRb = collision.gameObject.GetComponent<Rigidbody>();
            Vector3 hitVelocity;
            
            if (racketRb != null)
            {
                hitVelocity = racketRb.velocity * 1.5f;
            }
            else
            {
                hitVelocity = collision.relativeVelocity * 0.8f;
            }
            
            hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
            
            OnRacketHit(hitVelocity, collision.contacts[0].point);
        }
    }
    
    private void OnTriggerEnter(Collider other)
    {
        if (!Object.HasStateAuthority) return;
        
        if (other.CompareTag("Racket"))
        {
            Rigidbody racketRb = other.GetComponent<Rigidbody>();
            if (racketRb != null)
            {
                Vector3 hitVelocity = racketRb.velocity * 1.5f;
                hitVelocity.y = Mathf.Max(hitVelocity.y, 1f);
                OnRacketHit(hitVelocity, other.ClosestPoint(transform.position));
            }
        }
    }
}
