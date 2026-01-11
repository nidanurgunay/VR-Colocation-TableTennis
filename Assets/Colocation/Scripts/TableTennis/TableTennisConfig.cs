using UnityEngine;
using Fusion;

/// <summary>
/// Shared configuration for Table Tennis game prefabs.
/// Create one asset and reference it from both TableTennisManager and AnchorGUIManager_AutoAlignment.
/// </summary>
[CreateAssetMenu(fileName = "TableTennisConfig", menuName = "Colocation/Table Tennis Config")]
public class TableTennisConfig : ScriptableObject
{
    [Header("Prefabs")]
    [Tooltip("Table prefab (local GameObject)")]
    [SerializeField] private GameObject _tablePrefab;
    
    [Tooltip("Racket prefab for controllers (local GameObject)")]
    [SerializeField] private GameObject _racketPrefab;
    
    [Tooltip("Networked ball prefab")]
    [SerializeField] private NetworkPrefabRef _ballPrefab;
    
    [Header("Table Settings")]
    public float defaultTableHeight = 0.76f;
    public float tableRotateSpeed = 90f;
    public float tableMoveSpeed = 1f;
    
    [Header("Ball Settings")]
    public Vector3 ballSpawnOffset = new Vector3(0f, 0.5f, 0f);
    
    // Properties for accessing prefabs
    public GameObject TablePrefab => _tablePrefab;
    public GameObject RacketPrefab => _racketPrefab;
    public NetworkPrefabRef BallPrefab => _ballPrefab;
}
