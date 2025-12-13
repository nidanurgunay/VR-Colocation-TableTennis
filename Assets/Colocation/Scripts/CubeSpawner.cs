#if FUSION2

using Fusion;
using UnityEngine;

/// <summary>
/// Handles spawning networked interactable cubes in the colocation scene.
/// Cubes are spawned via button press and can be grabbed by multiple headsets.
/// </summary>
public class CubeSpawner : NetworkBehaviour
{
    [Header("Spawn Settings")]
    [SerializeField] private NetworkPrefabRef cubePrefab;
    [SerializeField] private Vector3 spawnOffset = new Vector3(0f, 1.5f, 1f);
    [SerializeField] private float cubeScale = 0.1f;

    [Header("References")]
    [SerializeField] private Transform cameraRigTransform;

    private void Awake()
    {
        if (cameraRigTransform == null)
        {
            var cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null)
            {
                cameraRigTransform = cameraRig.transform;
            }
        }
    }

    /// <summary>
    /// Spawns a networked cube at the specified position.
    /// Can be called by any client - will use RPC if not host.
    /// </summary>
    public void SpawnCube()
    {
        if (!Object.HasStateAuthority)
        {
            Debug.Log("[CubeSpawner] Requesting spawn via RPC...");
            RPC_RequestSpawnCube();
            return;
        }

        SpawnCubeInternal();
    }

    /// <summary>
    /// Spawns a cube at a specific world position.
    /// Can be called by any client - will use RPC if not host.
    /// </summary>
    public void SpawnCubeAtPosition(Vector3 position, Quaternion rotation)
    {
        if (!Object.HasStateAuthority)
        {
            Debug.Log("[CubeSpawner] Requesting spawn at position via RPC...");
            RPC_RequestSpawnCubeAtPosition(position, rotation);
            return;
        }

        SpawnCubeAtPositionInternal(position, rotation);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnCube()
    {
        SpawnCubeInternal();
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnCubeAtPosition(Vector3 position, Quaternion rotation)
    {
        SpawnCubeAtPositionInternal(position, rotation);
    }

    private void SpawnCubeInternal()
    {
        Vector3 spawnPosition = CalculateSpawnPosition();
        SpawnCubeAtPositionInternal(spawnPosition, Quaternion.identity);
    }

    private void SpawnCubeAtPositionInternal(Vector3 position, Quaternion rotation)
    {
        if (Runner == null || !Runner.IsRunning)
        {
            Debug.LogError("[CubeSpawner] NetworkRunner is not running!");
            return;
        }

        if (!cubePrefab.IsValid)
        {
            Debug.LogError("[CubeSpawner] Cube prefab is not assigned or invalid!");
            return;
        }

        Debug.Log($"[CubeSpawner] Spawning cube at position: {position}");

        var spawnedObject = Runner.Spawn(
            cubePrefab,
            position,
            rotation,
            Object.InputAuthority
        );

        if (spawnedObject != null)
        {
            spawnedObject.transform.localScale = Vector3.one * cubeScale;
            Debug.Log($"[CubeSpawner] Cube spawned successfully! NetworkId: {spawnedObject.Id}");
        }
        else
        {
            Debug.LogError("[CubeSpawner] Failed to spawn cube!");
        }
    }

    private Vector3 CalculateSpawnPosition()
    {
        if (cameraRigTransform != null)
        {
            // Spawn in front of the user
            Vector3 forward = cameraRigTransform.forward;
            forward.y = 0; // Keep horizontal
            forward.Normalize();

            return cameraRigTransform.position + forward * spawnOffset.z + Vector3.up * spawnOffset.y;
        }

        // Fallback to world origin with offset
        return spawnOffset;
    }
}

#endif
