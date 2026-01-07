using UnityEngine;
using TMPro;
using Fusion;
using System.Linq;

/// <summary>
/// Simple game UI panel for 4 walls with TextMeshPro 3D text.
/// Each wall has: Score, Info, Status, Controls
/// </summary>
public class GameUIPanel_Simple : MonoBehaviour
{
    [Header("=== Wall 1 ===")]
    [SerializeField] private TextMeshPro wall1_Score;
    [SerializeField] private TextMeshPro wall1_Info;
    [SerializeField] private TextMeshPro wall1_Status;
    [SerializeField] private TextMeshPro wall1_Controls;
    
    [Header("=== Wall 2 ===")]
    [SerializeField] private TextMeshPro wall2_Score;
    [SerializeField] private TextMeshPro wall2_Info;
    [SerializeField] private TextMeshPro wall2_Status;
    [SerializeField] private TextMeshPro wall2_Controls;
    
    [Header("=== Wall 3 ===")]
    [SerializeField] private TextMeshPro wall3_Score;
    [SerializeField] private TextMeshPro wall3_Info;
    [SerializeField] private TextMeshPro wall3_Status;
    [SerializeField] private TextMeshPro wall3_Controls;
    
    [Header("=== Wall 4 ===")]
    [SerializeField] private TextMeshPro wall4_Score;
    [SerializeField] private TextMeshPro wall4_Info;
    [SerializeField] private TextMeshPro wall4_Status;
    [SerializeField] private TextMeshPro wall4_Controls;
    
    [Header("=== Text Colors ===")]
    [SerializeField] private Color scoreColor = Color.yellow;
    [SerializeField] private Color infoColor = Color.green;
    [SerializeField] private Color statusColor = Color.white;
    [SerializeField] private Color controlsColor = Color.cyan;
    
    [Header("=== Game Settings ===")]
    [SerializeField] private int winScore = 11;
    
    // References
    private TableTennisGameManager gameManager;
    private NetworkRunner runner;
    private bool initialized = false;
    private bool isHost = false;
    private bool hostDetermined = false;
    
    private void Start()
    {
        ApplyColors();
        
        // Set default text
        SetAllScore("0 - 0");
        SetAllInfo("Waiting...");
        SetAllStatus("Initializing...");
        SetAllControls("Y = Left | B = Right");
        
        initialized = true;
        Debug.Log("[GameUIPanel_Simple] Initialized");
    }
    
    private void ApplyColors()
    {
        // Wall 1
        if (wall1_Score != null) wall1_Score.color = scoreColor;
        if (wall1_Info != null) wall1_Info.color = infoColor;
        if (wall1_Status != null) wall1_Status.color = statusColor;
        if (wall1_Controls != null) wall1_Controls.color = controlsColor;
        
        // Wall 2
        if (wall2_Score != null) wall2_Score.color = scoreColor;
        if (wall2_Info != null) wall2_Info.color = infoColor;
        if (wall2_Status != null) wall2_Status.color = statusColor;
        if (wall2_Controls != null) wall2_Controls.color = controlsColor;
        
        // Wall 3
        if (wall3_Score != null) wall3_Score.color = scoreColor;
        if (wall3_Info != null) wall3_Info.color = infoColor;
        if (wall3_Status != null) wall3_Status.color = statusColor;
        if (wall3_Controls != null) wall3_Controls.color = controlsColor;
        
        // Wall 4
        if (wall4_Score != null) wall4_Score.color = scoreColor;
        if (wall4_Info != null) wall4_Info.color = infoColor;
        if (wall4_Status != null) wall4_Status.color = statusColor;
        if (wall4_Controls != null) wall4_Controls.color = controlsColor;
    }
    
    private void Update()
    {
        if (!initialized) return;
        
        // Find references if not set
        if (gameManager == null)
        {
            gameManager = FindObjectOfType<TableTennisGameManager>();
        }
        
        // Only determine host status once
        if (!hostDetermined)
        {
            runner = NetworkRunner.Instances.FirstOrDefault(r => r != null && r.IsRunning);
            if (runner != null)
            {
                isHost = runner.IsServer || runner.IsSharedModeMasterClient;
                hostDetermined = true;
                Debug.Log($"[GameUIPanel_Simple] Host determined: {isHost}");
            }
        }
        
        UpdateDisplay();
    }
    
    private void UpdateDisplay()
    {
        // Get scores
        int p1Score = 0, p2Score = 0;
        if (gameManager != null)
        {
            p1Score = gameManager.Player1Score;
            p2Score = gameManager.Player2Score;
        }
        
        // Update all walls
        SetAllScore($"{p1Score} - {p2Score}");
        
        // Info
        string playerStr = isHost ? "You are P1 (Host)" : "You are P2 (Client)";
        SetAllInfo($"First to {winScore} | {playerStr}");
        
        // Status
        SetAllStatus(GetStatusText(p1Score, p2Score, isHost));
    }
    
    private string GetStatusText(int p1Score, int p2Score, bool isHost)
    {
        // Check for winner
        if (p1Score >= winScore || p2Score >= winScore)
        {
            bool hostWon = p1Score >= winScore;
            bool youWon = (hostWon && isHost) || (!hostWon && !isHost);
            return youWon ? "YOU WIN!" : "YOU LOSE";
        }
        
        if (gameManager == null) return "Waiting for game...";
        
        var state = gameManager.CurrentGameState;
        
        switch (state)
        {
            case TableTennisGameManager.GameState.WaitingToStart:
                return "Waiting to start...";
            case TableTennisGameManager.GameState.Serving:
                return "-- SERVE --";
            case TableTennisGameManager.GameState.Playing:
                return "PLAY!";
            case TableTennisGameManager.GameState.PointScored:
                return "Point!";
            case TableTennisGameManager.GameState.GameOver:
                return "GAME OVER";
            default:
                return "Ready";
        }
    }
    
    #region Text Setters
    
    private void SetAllScore(string text)
    {
        if (wall1_Score != null) wall1_Score.text = text;
        if (wall2_Score != null) wall2_Score.text = text;
        if (wall3_Score != null) wall3_Score.text = text;
        if (wall4_Score != null) wall4_Score.text = text;
    }
    
    private void SetAllInfo(string text)
    {
        if (wall1_Info != null) wall1_Info.text = text;
        if (wall2_Info != null) wall2_Info.text = text;
        if (wall3_Info != null) wall3_Info.text = text;
        if (wall4_Info != null) wall4_Info.text = text;
    }
    
    private void SetAllStatus(string text)
    {
        if (wall1_Status != null) wall1_Status.text = text;
        if (wall2_Status != null) wall2_Status.text = text;
        if (wall3_Status != null) wall3_Status.text = text;
        if (wall4_Status != null) wall4_Status.text = text;
    }
    
    private void SetAllControls(string text)
    {
        if (wall1_Controls != null) wall1_Controls.text = text;
        if (wall2_Controls != null) wall2_Controls.text = text;
        if (wall3_Controls != null) wall3_Controls.text = text;
        if (wall4_Controls != null) wall4_Controls.text = text;
    }
    
    #endregion
}
