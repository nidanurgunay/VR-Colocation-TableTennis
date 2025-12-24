using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Manages the table tennis game setup and spawns the networked ball.
/// Attach to a GameObject in the TableTennis scene.
/// </summary>
public class TableTennisManager : NetworkBehaviour
{
    [Header("Prefabs")]
    [SerializeField] private NetworkPrefabRef ballPrefab;
    [SerializeField] private GameObject racketPrefab; // Local prefab, not networked
    
    [Header("Table Setup")]
    [SerializeField] private Transform tableTransform;
    [SerializeField] private Vector3 racket1Position = new Vector3(-0.3f, 0.8f, -0.6f); // Relative to table
    [SerializeField] private Vector3 racket2Position = new Vector3(0.3f, 0.8f, -0.6f);
    [SerializeField] private Vector3 racketRotation = new Vector3(90f, 0f, 0f); // Lying flat
    
    [Header("Ball Spawn")]
    [SerializeField] private Vector3 ballSpawnOffset = new Vector3(0f, 0.5f, 0f); // Above table center
    
    // References
    private NetworkedBall spawnedBall;
    private Transform sharedAnchor;
    private GameObject[] localRackets = new GameObject[2];
    
    public override void Spawned()
    {
        Debug.Log($"[TableTennisManager] Spawned. HasStateAuthority: {Object.HasStateAuthority}");
        
        StartCoroutine(InitializeGame());
    }
    
    private IEnumerator InitializeGame()
    {
        // Wait for anchor to be available
        yield return StartCoroutine(WaitForAnchor());
        
        // Setup rackets (local, not networked)
        SetupLocalRackets();
        
        // Host spawns the ball
        if (Object.HasStateAuthority)
        {
            yield return new WaitForSeconds(0.5f);
            SpawnBall();
        }
    }
    
    private IEnumerator WaitForAnchor()
    {
        int attempts = 0;
        while (sharedAnchor == null && attempts < 50)
        {
            var anchors = FindObjectsOfType<OVRSpatialAnchor>();
            foreach (var anchor in anchors)
            {
                if (anchor.gameObject.name.Contains("Shared") || 
                    anchor.gameObject.name.Contains("Anchor"))
                {
                    sharedAnchor = anchor.transform;
                    Debug.Log($"[TableTennisManager] Found anchor: {anchor.gameObject.name}");
                    break;
                }
            }
            
            attempts++;
            yield return new WaitForSeconds(0.2f);
        }
        
        if (sharedAnchor == null)
        {
            Debug.LogWarning("[TableTennisManager] Could not find shared anchor");
            
            // Use table as fallback reference
            if (tableTransform != null)
            {
                sharedAnchor = tableTransform;
            }
        }
    }
    
    private void SetupLocalRackets()
    {
        if (racketPrefab == null)
        {
            Debug.LogWarning("[TableTennisManager] Racket prefab not assigned");
            return;
        }
        
        Transform spawnParent = tableTransform != null ? tableTransform : sharedAnchor;
        
        if (spawnParent == null)
        {
            Debug.LogError("[TableTennisManager] No spawn parent for rackets");
            return;
        }
        
        // Spawn two rackets on the table
        Vector3 worldPos1 = spawnParent.TransformPoint(racket1Position);
        Vector3 worldPos2 = spawnParent.TransformPoint(racket2Position);
        Quaternion worldRot = spawnParent.rotation * Quaternion.Euler(racketRotation);
        
        localRackets[0] = Instantiate(racketPrefab, worldPos1, worldRot);
        localRackets[0].name = "Racket_1";
        EnsureGrabbableRacket(localRackets[0]);
        
        localRackets[1] = Instantiate(racketPrefab, worldPos2, worldRot);
        localRackets[1].name = "Racket_2";
        EnsureGrabbableRacket(localRackets[1]);
        
        Debug.Log("[TableTennisManager] Spawned 2 local rackets on table");
    }
    
    private void EnsureGrabbableRacket(GameObject racket)
    {
        // Add GrabbableRacket component if not present
        if (racket.GetComponent<GrabbableRacket>() == null)
        {
            racket.AddComponent<GrabbableRacket>();
        }
        
        // Ensure it has a collider for ball detection
        if (racket.GetComponent<Collider>() == null)
        {
            var boxCollider = racket.AddComponent<BoxCollider>();
            // Adjust size based on typical racket dimensions
            boxCollider.size = new Vector3(0.15f, 0.01f, 0.17f);
        }
        
        // Ensure tagged for ball collision
        racket.tag = "Racket";
        
        // Add rigidbody for velocity tracking
        var rb = racket.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = racket.AddComponent<Rigidbody>();
        }
        rb.isKinematic = true; // Start kinematic (on table)
        rb.useGravity = false;
    }
    
    private void SpawnBall()
    {
        if (ballPrefab == default)
        {
            Debug.LogError("[TableTennisManager] Ball prefab not assigned!");
            return;
        }
        
        Vector3 spawnPosition = Vector3.zero;
        
        if (tableTransform != null)
        {
            spawnPosition = tableTransform.TransformPoint(ballSpawnOffset);
        }
        else if (sharedAnchor != null)
        {
            spawnPosition = sharedAnchor.TransformPoint(new Vector3(0, 1.2f, 0));
        }
        
        var ballObj = Runner.Spawn(
            ballPrefab,
            spawnPosition,
            Quaternion.identity,
            Object.InputAuthority
        );
        
        if (ballObj != null)
        {
            spawnedBall = ballObj.GetComponent<NetworkedBall>();
            Debug.Log($"[TableTennisManager] Spawned networked ball at {spawnPosition}");
        }
    }
    
    /// <summary>
    /// Reset the game - respawn ball, reset rackets
    /// </summary>
    public void ResetGame()
    {
        // Release all grabbed rackets
        foreach (var racket in localRackets)
        {
            if (racket != null)
            {
                var grabbable = racket.GetComponent<GrabbableRacket>();
                if (grabbable != null)
                {
                    grabbable.ForceRelease();
                }
            }
        }
        
        // Reset ball (handled by NetworkedBall)
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    /// <summary>
    /// Serve the ball
    /// </summary>
    public void ServeBall()
    {
        if (spawnedBall != null)
        {
            spawnedBall.RequestServe(Vector3.forward);
        }
    }
    
    private void OnDestroy()
    {
        // Cleanup local rackets
        foreach (var racket in localRackets)
        {
            if (racket != null)
            {
                Destroy(racket);
            }
        }
    }
}
