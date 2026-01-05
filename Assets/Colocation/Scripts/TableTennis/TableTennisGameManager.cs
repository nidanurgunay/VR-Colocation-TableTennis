using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Fusion;
using TMPro;

/// <summary>
/// Manages table tennis game logic: scoring, serving, and game rules.
/// Networked via Photon Fusion - host has authority over game state.
/// </summary>
public class TableTennisGameManager : NetworkBehaviour
{
    [Header("Game Settings")]
    [SerializeField] private int pointsToWin = 11; // Standard table tennis
    [SerializeField] private int servesPerPlayer = 2; // Serves before switching
    [SerializeField] private int deucePointThreshold = 10; // When both at 10, alternate serves
    
    [Header("Table Zones (anchor-relative)")]
    [SerializeField] private float tableHalfLength = 1.37f; // Half of 2.74m table
    [SerializeField] private float tableWidth = 1.525f; // Full width
    [SerializeField] private float tableHeight = 0.76f;
    [SerializeField] private float netHeight = 0.1525f; // 15.25cm net
    
    [Header("Table Object")]
    [SerializeField] private Transform tableObject; // Reference to actual table object
    [SerializeField] private string tableTag = "Table"; // Tag to find table if not assigned
    
    [Header("UI References (optional)")]
    [SerializeField] private TextMeshProUGUI player1ScoreText;
    [SerializeField] private TextMeshProUGUI player2ScoreText;
    [SerializeField] private TextMeshProUGUI gameStatusText;
    
    [Header("Audio (optional)")]
    [SerializeField] private AudioClip scoreSound;
    [SerializeField] private AudioClip bounceSound;
    [SerializeField] private AudioClip winSound;
    
    // Networked game state
    [Networked] public int Player1Score { get; set; }
    [Networked] public int Player2Score { get; set; }
    [Networked] public int CurrentServer { get; set; } // 1 or 2
    [Networked] public int ServeCount { get; set; } // Serves since last switch
    [Networked] public GameState CurrentGameState { get; set; }
    [Networked] public int LastHitBy { get; set; } // Which player last hit the ball (1, 2, or 0 for none)
    [Networked] public int BounceCount { get; set; } // Bounces on current side
    [Networked] public int LastBounceSide { get; set; } // Which side ball bounced on (1 or 2)
    
    public enum GameState
    {
        WaitingToStart,
        Serving,
        Playing,
        PointScored,
        GameOver
    }
    
    // Local references
    private NetworkedBall ball;
    private Transform sharedAnchor;
    private AudioSource audioSource;
    private int localPlayerNumber = 0; // 1 or 2, determined by position
    
    // Events for UI/effects
    public System.Action<int, int> OnScoreChanged;
    public System.Action<int> OnPointScored;
    public System.Action<int> OnGameWon;
    
    public override void Spawned()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
        }
        
        StartCoroutine(Initialize());
        
        Debug.Log($"[TableTennisGameManager] Spawned. HasStateAuthority: {Object.HasStateAuthority}");
    }
    
    private IEnumerator Initialize()
    {
        // Wait for anchor
        yield return new WaitForSeconds(1f);
        
        // Find shared anchor
        var anchors = FindObjectsOfType<OVRSpatialAnchor>();
        foreach (var anchor in anchors)
        {
            if (anchor.gameObject.activeInHierarchy)
            {
                sharedAnchor = anchor.transform;
                break;
            }
        }
        
        if (sharedAnchor == null)
        {
            var alignmentManager = FindObjectOfType<AlignmentManager>();
            if (alignmentManager != null)
            {
                var cameraRig = FindObjectOfType<OVRCameraRig>();
                if (cameraRig != null)
                {
                    sharedAnchor = cameraRig.transform;
                }
            }
        }
        
        // Find table object
        FindTableObject();
        
        // Find ball
        ball = FindObjectOfType<NetworkedBall>();
        
        // Determine local player number based on position relative to table
        DetermineLocalPlayerNumber();
        
        // Host initializes game state
        if (Object.HasStateAuthority)
        {
            ResetGame();
        }
        
        Debug.Log($"[TableTennisGameManager] Initialized. LocalPlayer: {localPlayerNumber}, Table: {(tableObject != null ? tableObject.name : "not found")}");
    }
    
    private void FindTableObject()
    {
        if (tableObject != null) return;
        
        // Try to find by tag
        GameObject tableByTag = GameObject.FindGameObjectWithTag(tableTag);
        if (tableByTag != null)
        {
            tableObject = tableByTag.transform;
            Debug.Log($"[TableTennisGameManager] Found table by tag: {tableObject.name}");
            return;
        }
        
        // Try to find by name containing "table"
        var allObjects = FindObjectsOfType<Transform>();
        foreach (var obj in allObjects)
        {
            if (obj.name.ToLower().Contains("table") && obj.GetComponent<Collider>() != null)
            {
                tableObject = obj;
                Debug.Log($"[TableTennisGameManager] Found table by name: {tableObject.name}");
                return;
            }
        }
        
        Debug.LogWarning("[TableTennisGameManager] Could not find table object. Assign it in Inspector or tag it as 'Table'");
    }
    
    /// <summary>
    /// Get the table's world position
    /// </summary>
    public Vector3 GetTableCenter()
    {
        if (tableObject != null)
            return tableObject.position;
        if (sharedAnchor != null)
            return sharedAnchor.position;
        return Vector3.zero;
    }
    
    /// <summary>
    /// Get the table's rotation
    /// </summary>
    public Quaternion GetTableRotation()
    {
        if (tableObject != null)
            return tableObject.rotation;
        if (sharedAnchor != null)
            return sharedAnchor.rotation;
        return Quaternion.identity;
    }
    
    /// <summary>
    /// Transform a world position to table-relative position
    /// </summary>
    public Vector3 WorldToTablePosition(Vector3 worldPos)
    {
        if (tableObject != null)
            return tableObject.InverseTransformPoint(worldPos);
        if (sharedAnchor != null)
            return sharedAnchor.InverseTransformPoint(worldPos);
        return worldPos;
    }
    
    private void DetermineLocalPlayerNumber()
    {
        // Player 1 is on negative Z side, Player 2 on positive Z side
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig != null)
        {
            Vector3 relativePos = WorldToTablePosition(cameraRig.centerEyeAnchor.position);
            localPlayerNumber = relativePos.z < 0 ? 1 : 2;
            Debug.Log($"[TableTennisGameManager] Local player is Player {localPlayerNumber} (z={relativePos.z:F2})");
        }
    }
    
    private void Update()
    {
        UpdateUI();
        
        // Check for game start input (GRIP button on either controller)
        if (CurrentGameState == GameState.WaitingToStart || CurrentGameState == GameState.GameOver)
        {
            bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch) ||
                               OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            
            if (gripPressed)
            {
                if (Object.HasStateAuthority)
                {
                    StartGame();
                }
                else
                {
                    RPC_RequestStartGame();
                }
            }
        }
    }
    
    private void UpdateUI()
    {
        if (player1ScoreText != null)
            player1ScoreText.text = Player1Score.ToString();
        
        if (player2ScoreText != null)
            player2ScoreText.text = Player2Score.ToString();
        
        if (gameStatusText != null)
        {
            switch (CurrentGameState)
            {
                case GameState.WaitingToStart:
                    gameStatusText.text = "Press A to Start";
                    break;
                case GameState.Serving:
                    gameStatusText.text = $"Player {CurrentServer} Serving";
                    break;
                case GameState.Playing:
                    gameStatusText.text = "";
                    break;
                case GameState.PointScored:
                    gameStatusText.text = $"Point for Player {(LastHitBy == 1 ? 2 : 1)}!";
                    break;
                case GameState.GameOver:
                    int winner = Player1Score > Player2Score ? 1 : 2;
                    gameStatusText.text = $"Player {winner} Wins!";
                    break;
            }
        }
    }
    
    /// <summary>
    /// Called by NetworkedBall when ball bounces on table
    /// </summary>
    public void OnBallBounce(Vector3 bouncePosition)
    {
        if (!Object.HasStateAuthority) return;
        if (CurrentGameState != GameState.Playing && CurrentGameState != GameState.Serving) return;
        
        // Determine which side of table (use table object for accurate positioning)
        Vector3 relPos = WorldToTablePosition(bouncePosition);
        
        int bounceSide = relPos.z < 0 ? 1 : 2;
        
        // Check if bounce is on table
        bool onTable = Mathf.Abs(relPos.x) <= tableWidth / 2 && 
                       Mathf.Abs(relPos.z) <= tableHalfLength;
        
        if (!onTable)
        {
            // Ball bounced off table - point against last hitter
            AwardPoint(LastHitBy == 1 ? 2 : 1);
            return;
        }
        
        // Track bounces
        if (bounceSide == LastBounceSide)
        {
            BounceCount++;
        }
        else
        {
            BounceCount = 1;
            LastBounceSide = bounceSide;
        }
        
        // Serving rules: must bounce on server's side first, then opponent's
        if (CurrentGameState == GameState.Serving)
        {
            if (BounceCount == 1 && bounceSide == CurrentServer)
            {
                // Good serve - ball bounced on server's side
                CurrentGameState = GameState.Playing;
            }
            else if (bounceSide != CurrentServer)
            {
                // Serve went directly to opponent's side - fault
                AwardPoint(CurrentServer == 1 ? 2 : 1);
            }
        }
        
        // Double bounce on same side = point for other player
        if (BounceCount >= 2 && CurrentGameState == GameState.Playing)
        {
            AwardPoint(bounceSide == 1 ? 2 : 1);
        }
        
        PlaySound(bounceSound);
        Debug.Log($"[TableTennisGameManager] Bounce on side {bounceSide}, count: {BounceCount}");
    }
    
    /// <summary>
    /// Called by NetworkedBall when ball is hit by racket
    /// </summary>
    public void OnBallHit(int playerNumber)
    {
        if (!Object.HasStateAuthority) return;
        
        LastHitBy = playerNumber;
        BounceCount = 0;
        
        if (CurrentGameState == GameState.Serving && playerNumber == CurrentServer)
        {
            // Server hit the ball - good
            Debug.Log($"[TableTennisGameManager] Player {playerNumber} served");
        }
        else if (CurrentGameState == GameState.Playing)
        {
            Debug.Log($"[TableTennisGameManager] Player {playerNumber} hit the ball");
        }
    }
    
    /// <summary>
    /// Called when ball falls off table or goes out of bounds
    /// </summary>
    public void OnBallOut()
    {
        if (!Object.HasStateAuthority) return;
        if (CurrentGameState != GameState.Playing && CurrentGameState != GameState.Serving) return;
        
        // Point goes to the player who didn't hit it last (or opponent of server if no one hit it)
        int pointFor = LastHitBy == 0 ? (CurrentServer == 1 ? 2 : 1) : (LastHitBy == 1 ? 2 : 1);
        AwardPoint(pointFor);
    }
    
    private void AwardPoint(int player)
    {
        if (player == 1)
            Player1Score++;
        else
            Player2Score++;
        
        CurrentGameState = GameState.PointScored;
        
        PlaySound(scoreSound);
        OnScoreChanged?.Invoke(Player1Score, Player2Score);
        OnPointScored?.Invoke(player);
        
        Debug.Log($"[TableTennisGameManager] Point for Player {player}! Score: {Player1Score}-{Player2Score}");
        
        // Check for game win
        if (CheckForWin())
        {
            int winner = Player1Score > Player2Score ? 1 : 2;
            CurrentGameState = GameState.GameOver;
            PlaySound(winSound);
            OnGameWon?.Invoke(winner);
            Debug.Log($"[TableTennisGameManager] Player {winner} wins!");
        }
        else
        {
            // Prepare for next serve
            StartCoroutine(PrepareNextServe());
        }
    }
    
    private bool CheckForWin()
    {
        int p1 = Player1Score;
        int p2 = Player2Score;
        
        // Need to win by 2 after deuce
        if (p1 >= deucePointThreshold && p2 >= deucePointThreshold)
        {
            return Mathf.Abs(p1 - p2) >= 2;
        }
        
        return p1 >= pointsToWin || p2 >= pointsToWin;
    }
    
    private IEnumerator PrepareNextServe()
    {
        yield return new WaitForSeconds(2f);
        
        // Update serve count and switch server if needed
        ServeCount++;
        
        bool inDeuce = Player1Score >= deucePointThreshold && Player2Score >= deucePointThreshold;
        int servesBeforeSwitch = inDeuce ? 1 : servesPerPlayer;
        
        if (ServeCount >= servesBeforeSwitch)
        {
            ServeCount = 0;
            CurrentServer = CurrentServer == 1 ? 2 : 1;
            Debug.Log($"[TableTennisGameManager] Server switched to Player {CurrentServer}");
        }
        
        // Reset for next point
        LastHitBy = 0;
        BounceCount = 0;
        LastBounceSide = 0;
        CurrentGameState = GameState.Serving;
        
        // Reset ball to serve position on current server's side
        if (ball != null)
        {
            ball.RequestServe(CurrentServer);
        }
    }
    
    public void StartGame()
    {
        if (!Object.HasStateAuthority) return;
        
        ResetGame();
        CurrentGameState = GameState.Serving;
        
        Debug.Log("[TableTennisGameManager] Game started!");
    }
    
    public void ResetGame()
    {
        Player1Score = 0;
        Player2Score = 0;
        CurrentServer = 1; // Player 1 serves first
        ServeCount = 0;
        LastHitBy = 0;
        BounceCount = 0;
        LastBounceSide = 0;
        CurrentGameState = GameState.WaitingToStart;
        
        // Position ball on Player 1's side (first server)
        if (ball != null)
        {
            ball.RequestServe(CurrentServer);
        }
        
        Debug.Log("[TableTennisGameManager] Game reset");
    }
    
    private void PlaySound(AudioClip clip)
    {
        if (clip != null && audioSource != null)
        {
            audioSource.PlayOneShot(clip);
        }
    }
    
    // RPCs for client requests
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestStartGame()
    {
        StartGame();
    }
    
    /// <summary>
    /// Get which player number the local player is (1 or 2)
    /// </summary>
    public int GetLocalPlayerNumber()
    {
        return localPlayerNumber;
    }
    
    /// <summary>
    /// Check if it's the local player's turn to serve
    /// </summary>
    public bool IsLocalPlayerServing()
    {
        return CurrentServer == localPlayerNumber;
    }
}
