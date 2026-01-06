using UnityEngine;
using TMPro;
using Fusion;
using System.Linq;

/// <summary>
/// Simple world-space score display using TextMeshPro 3D text.
/// Shows score on all 4 walls around the table, visible from any angle.
/// </summary>
public class GameUIPanel : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float wallHeight = 1.5f; // Height on wall
    [SerializeField] private float wallDistance = 2.5f; // Distance from table center to wall
    [SerializeField] private float fontSize = 0.5f; // Smaller font size
    [SerializeField] private Color scoreColor = Color.yellow;
    [SerializeField] private Color infoColor = Color.green; // Player info color
    [SerializeField] private Color statusColor = Color.white;
    [SerializeField] private Color controlsColor = Color.cyan; // Color for controls info
    [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.3f, 0.95f); // Dark blue
    [SerializeField] private Vector2 backgroundSize = new Vector2(1.8f, 1.2f); // Wider background panel
    [SerializeField] private int winScore = 11; // Score needed to win
    
    // Container for all 4 wall panels
    private class WallPanel
    {
        public GameObject root;
        public TextMeshPro scoreText;
        public TextMeshPro infoText; // "First to 11 | You are P1 (Host)"
        public TextMeshPro statusText;
        public TextMeshPro controlsText; // Detailed controls
        public GameObject background;
    }
    
    private WallPanel[] wallPanels = new WallPanel[4];
    
    // References
    private Transform tableObject;
    private TableTennisGameManager gameManager;
    private GameObject panelsContainer;
    private NetworkRunner runner;
    
    private void Start()
    {
        StartCoroutine(DelayedInit());
    }
    
    private System.Collections.IEnumerator DelayedInit()
    {
        // Wait for table to be positioned
        yield return new WaitForSeconds(3.0f);
        
        // Find table
        FindTable();
        
        // Keep trying if not found
        int attempts = 0;
        while (tableObject == null && attempts < 20)
        {
            yield return new WaitForSeconds(0.5f);
            FindTable();
            attempts++;
        }
        
        // Find game manager
        gameManager = FindObjectOfType<TableTennisGameManager>();
        
        // Create the score display
        CreateScoreDisplay();
        
        if (tableObject != null)
        {
            Debug.Log($"[GameUIPanel] Score display created above {tableObject.name} at {tableObject.position}");
        }
        else
        {
            Debug.LogWarning("[GameUIPanel] Could not find table!");
        }
    }
    
    private void FindTable()
    {
        if (tableObject != null) return;
        
        // Try by tag
        GameObject byTag = GameObject.FindGameObjectWithTag("Table");
        if (byTag != null)
        {
            tableObject = byTag.transform;
            return;
        }
        
        // Try by name
        string[] names = { "PingPongTable", "pingpongtable", "pingpong", "PingPong", "Table" };
        foreach (string n in names)
        {
            GameObject found = GameObject.Find(n);
            if (found != null)
            {
                tableObject = found.transform;
                return;
            }
        }
    }
    
    private void CreateScoreDisplay()
    {
        // Create container for all panels
        panelsContainer = new GameObject("ScoreDisplays");
        
        if (tableObject != null)
        {
            // Create 4 panels on each wall around the table
            // Wall 0: Right side (+X direction from table)
            // Wall 1: Left side (-X direction from table)
            // Wall 2: Front side (+Z direction from table)
            // Wall 3: Back side (-Z direction from table)
            
            Vector3[] directions = new Vector3[]
            {
                tableObject.right,      // Right wall
                -tableObject.right,     // Left wall
                tableObject.forward,    // Front wall
                -tableObject.forward    // Back wall
            };
            
            string[] wallNames = { "RightWall", "LeftWall", "FrontWall", "BackWall" };
            
            for (int i = 0; i < 4; i++)
            {
                wallPanels[i] = CreateWallPanel(wallNames[i], directions[i]);
            }
            
            Debug.Log($"[GameUIPanel] Created 4 wall panels around table at {tableObject.position}");
        }
        else
        {
            // Fallback: create panels at fixed positions if no table found
            Vector3[] directions = new Vector3[]
            {
                Vector3.right,
                Vector3.left,
                Vector3.forward,
                Vector3.back
            };
            
            string[] wallNames = { "RightWall", "LeftWall", "FrontWall", "BackWall" };
            
            for (int i = 0; i < 4; i++)
            {
                wallPanels[i] = CreateWallPanelFixed(wallNames[i], directions[i]);
            }
            
            Debug.LogWarning("[GameUIPanel] Created wall panels without table reference");
        }
    }
    
    private WallPanel CreateWallPanel(string name, Vector3 direction)
    {
        WallPanel panel = new WallPanel();
        
        // Create root object for this wall
        panel.root = new GameObject($"ScorePanel_{name}");
        panel.root.transform.SetParent(panelsContainer.transform);
        
        // Position on the wall in the given direction from table
        Vector3 wallPosition = tableObject.position + direction * wallDistance;
        wallPosition.y = wallHeight;
        
        panel.root.transform.position = wallPosition;
        
        // Face back toward the table center
        Vector3 lookDir = tableObject.position - wallPosition;
        lookDir.y = 0; // Keep panel vertical
        if (lookDir.sqrMagnitude > 0.01f)
        {
            panel.root.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        }
        
        // Create background panel
        panel.background = CreateBackground("Background", panel.root, new Vector3(0, 0, 0.01f));
        
        // Create score text (top) - VERY spread out positions to prevent overlap
        panel.scoreText = CreateTextMesh("ScoreText", panel.root, new Vector3(0, 0.4f, 0));
        panel.scoreText.fontSize = fontSize;
        panel.scoreText.color = scoreColor;
        panel.scoreText.fontStyle = FontStyles.Bold;
        panel.scoreText.alignment = TextAlignmentOptions.Center;
        panel.scoreText.text = "0 - 0";
        
        // Create info text (player role and win condition) - 25cm below score
        panel.infoText = CreateTextMesh("InfoText", panel.root, new Vector3(0, 0.15f, 0));
        panel.infoText.fontSize = fontSize * 0.5f;
        panel.infoText.color = infoColor;
        panel.infoText.alignment = TextAlignmentOptions.Center;
        panel.infoText.text = $"First to {winScore} | Connecting...";
        
        // Create status text (game state) - 25cm below info
        panel.statusText = CreateTextMesh("StatusText", panel.root, new Vector3(0, -0.1f, 0));
        panel.statusText.fontSize = fontSize * 0.6f;
        panel.statusText.color = statusColor;
        panel.statusText.alignment = TextAlignmentOptions.Center;
        panel.statusText.text = "GRIP to Spawn Ball";
        
        // Create controls info text (detailed controls) - 25cm below status
        panel.controlsText = CreateTextMesh("ControlsText", panel.root, new Vector3(0, -0.35f, 0));
        panel.controlsText.fontSize = fontSize * 0.4f;
        panel.controlsText.color = controlsColor;
        panel.controlsText.alignment = TextAlignmentOptions.Center;
        panel.controlsText.text = "Sticks: Move Ball | Hit to Start";
        
        return panel;
    }
    
    private WallPanel CreateWallPanelFixed(string name, Vector3 direction)
    {
        WallPanel panel = new WallPanel();
        
        panel.root = new GameObject($"ScorePanel_{name}");
        panel.root.transform.SetParent(panelsContainer.transform);
        
        Vector3 wallPosition = direction * wallDistance;
        wallPosition.y = wallHeight;
        
        panel.root.transform.position = wallPosition;
        panel.root.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);
        
        // Create background panel
        panel.background = CreateBackground("Background", panel.root, new Vector3(0, 0, 0.01f));
        
        // Create score text (top) - VERY spread out positions to prevent overlap
        panel.scoreText = CreateTextMesh("ScoreText", panel.root, new Vector3(0, 0.4f, 0));
        panel.scoreText.fontSize = fontSize;
        panel.scoreText.color = scoreColor;
        panel.scoreText.fontStyle = FontStyles.Bold;
        panel.scoreText.alignment = TextAlignmentOptions.Center;
        panel.scoreText.text = "0 - 0";
        
        // Create info text (player role and win condition) - 25cm below score
        panel.infoText = CreateTextMesh("InfoText", panel.root, new Vector3(0, 0.15f, 0));
        panel.infoText.fontSize = fontSize * 0.5f;
        panel.infoText.color = infoColor;
        panel.infoText.alignment = TextAlignmentOptions.Center;
        panel.infoText.text = $"First to {winScore} | Connecting...";
        
        // Create status text (game state) - 25cm below info
        panel.statusText = CreateTextMesh("StatusText", panel.root, new Vector3(0, -0.1f, 0));
        panel.statusText.fontSize = fontSize * 0.6f;
        panel.statusText.color = statusColor;
        panel.statusText.alignment = TextAlignmentOptions.Center;
        panel.statusText.text = "GRIP to Spawn Ball";
        
        // Create controls info text (detailed controls) - 25cm below status
        panel.controlsText = CreateTextMesh("ControlsText", panel.root, new Vector3(0, -0.35f, 0));
        panel.controlsText.fontSize = fontSize * 0.4f;
        panel.controlsText.color = controlsColor;
        panel.controlsText.alignment = TextAlignmentOptions.Center;
        panel.controlsText.text = "Sticks: Move Ball | Hit to Start";
        
        return panel;
    }
    
    private TextMeshPro CreateTextMesh(string name, GameObject parent, Vector3 localOffset)
    {
        GameObject textObj = new GameObject(name);
        textObj.transform.SetParent(parent.transform);
        textObj.transform.localPosition = localOffset;
        // TextMeshPro text faces -Z by default, so rotate 180 to face forward (+Z)
        textObj.transform.localRotation = Quaternion.Euler(0, 180f, 0);
        textObj.transform.localScale = Vector3.one;
        
        TextMeshPro tmp = textObj.AddComponent<TextMeshPro>();
        tmp.rectTransform.sizeDelta = new Vector2(1.5f, 0.15f); // Wider text area
        tmp.enableWordWrapping = false; // Prevent wrapping
        
        return tmp;
    }
    
    private GameObject CreateBackground(string name, GameObject parent, Vector3 localOffset)
    {
        GameObject bgObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bgObj.name = name;
        bgObj.transform.SetParent(parent.transform);
        bgObj.transform.localPosition = localOffset;
        bgObj.transform.localRotation = Quaternion.identity;
        bgObj.transform.localScale = new Vector3(backgroundSize.x, backgroundSize.y, 1f);
        
        // Remove collider (not needed for UI)
        var collider = bgObj.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        
        // Create opaque material for wall panel
        var renderer = bgObj.GetComponent<Renderer>();
        if (renderer != null)
        {
            Material mat = new Material(Shader.Find("Unlit/Color"));
            if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
            {
                mat = new Material(Shader.Find("Standard"));
            }
            mat.color = backgroundColor;
            renderer.material = mat;
        }
        
        return bgObj;
    }
    
    private void Update()
    {
        // Keep trying to find table
        if (tableObject == null)
        {
            FindTable();
        }
        
        // Find NetworkRunner if not cached
        if (runner == null)
        {
            runner = FindObjectOfType<NetworkRunner>();
        }
        
        // Update all panels
        string score = "0 - 0";
        string status = "GRIP to Start";
        string info = GetPlayerInfo();
        
        if (gameManager != null)
        {
            score = $"{gameManager.Player1Score} - {gameManager.Player2Score}";
            status = GetStatusText();
        }
        else
        {
            gameManager = FindObjectOfType<TableTennisGameManager>();
        }
        
        // Update all 4 wall panels
        foreach (var panel in wallPanels)
        {
            if (panel != null)
            {
                if (panel.scoreText != null) panel.scoreText.text = score;
                if (panel.infoText != null) panel.infoText.text = info;
                if (panel.statusText != null) panel.statusText.text = status;
            }
        }
    }
    
    private string GetPlayerInfo()
    {
        string playerRole = "Connecting...";
        string connectionStatus = "";
        
        if (runner != null && runner.IsRunning)
        {
            bool isHost = runner.IsServer || runner.IsSharedModeMasterClient;
            playerRole = isHost ? "You are P1 (Host)" : "You are P2 (Client)";
            
            int playerCount = runner.ActivePlayers.Count();
            if (playerCount >= 2)
            {
                connectionStatus = " | Connected";
            }
            else
            {
                connectionStatus = " | Waiting for player...";
            }
        }
        
        return $"First to {winScore} | {playerRole}{connectionStatus}";
    }
    
    private string GetStatusText()
    {
        // Check if ball is in positioning mode
        var ball = FindObjectOfType<NetworkedBall>();
        if (ball != null && ball.InPositioningMode)
        {
            return "Adjust ball position, hit to start!";
        }
        
        if (gameManager == null) return "GRIP to Spawn Ball";
        
        switch (gameManager.CurrentGameState)
        {
            case TableTennisGameManager.GameState.GameOver:
                return gameManager.Player1Score > gameManager.Player2Score ? "P1 Wins!" : "P2 Wins!";
                
            case TableTennisGameManager.GameState.WaitingToStart:
                return "GRIP to Spawn Ball";
                
            case TableTennisGameManager.GameState.Serving:
            case TableTennisGameManager.GameState.Playing:
            case TableTennisGameManager.GameState.PointScored:
                return gameManager.CurrentServer == 1 ? "P1 Serve" : "P2 Serve";
                
            default:
                return "GRIP to Spawn Ball";
        }
    }
}
