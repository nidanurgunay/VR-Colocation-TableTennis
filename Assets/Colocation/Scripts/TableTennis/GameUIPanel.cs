using UnityEngine;
using TMPro;
using Fusion;
using System.Linq;

/// <summary>
/// World-space score display using TextMeshPro 3D text.
/// Can use pre-created panels assigned in Inspector OR auto-create panels.
/// 
/// For pre-created panels:
/// 1. Create a panel in the scene with TextMeshPro children
/// 2. Assign the TextMeshPro references to this component
/// 3. The script will only update the text content
/// </summary>
public class GameUIPanel : MonoBehaviour
{
    [Header("=== Pre-Created Panels (Assign in Inspector) ===")]
    [Tooltip("If assigned, script will only update text. If empty, panels will be auto-created.")]
    [SerializeField] private TextMeshPro[] scorePanels;      // Main score display "0 - 0"
    [SerializeField] private TextMeshPro[] infoPanels;       // "First to 11 | You are P1"
    [SerializeField] private TextMeshPro[] statusPanels;     // "POSITION THE BALL"
    [SerializeField] private TextMeshPro[] controlsPanels;   // Detailed controls text
    
    [Header("=== Text Settings ===")]
    [SerializeField] private Color scoreColor = Color.yellow;
    [SerializeField] private Color infoColor = Color.green;
    [SerializeField] private Color statusColor = Color.white;
    [SerializeField] private Color controlsColor = Color.cyan;
    [SerializeField] private int winScore = 11;
    
    [Header("=== Auto-Create Settings (only used if no panels assigned) ===")]
    [SerializeField] private bool autoCreateIfEmpty = true;
    [SerializeField] private bool attachToSceneWalls = true; // Find walls in scene and attach panels
    [SerializeField] private string wallParentName = "Environment"; // Parent object containing walls
    [SerializeField] private float wallHeight = 1.5f;
    [SerializeField] private float wallDistance = 2.5f; // Only used if no walls found
    [SerializeField] private float panelOffsetFromWall = 0.15f; // How far from wall surface (enough to not intersect)
    [SerializeField] private float fontSize = 0.25f;
    [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.3f, 0.95f);
    [SerializeField] private Vector2 backgroundSize = new Vector2(1.6f, 1.0f);
    
    // Container for auto-created panels
    private class AutoWallPanel
    {
        public GameObject root;
        public TextMeshPro scoreText;
        public TextMeshPro infoText;
        public TextMeshPro statusText;
        public TextMeshPro controlsText;
        public GameObject background;
    }
    
    private AutoWallPanel[] autoWallPanels;
    private bool usingAutoCreatedPanels = false;
    
    // References
    private Transform tableObject;
    private TableTennisGameManager gameManager;
    private TableTennisManager tableTennisManager;
    private NetworkRunner runner;
    private bool initialized = false;
    
    private void Start()
    {
        // Check if we have pre-assigned panels
        bool hasPreCreatedPanels = HasAnyPreCreatedPanels();
        
        if (hasPreCreatedPanels)
        {
            Debug.Log("[GameUIPanel] Using pre-created panels from Inspector");
            ApplyColorsToPreCreatedPanels();
            initialized = true;
        }
        else if (autoCreateIfEmpty)
        {
            Debug.Log("[GameUIPanel] No panels assigned, will auto-create");
            StartCoroutine(DelayedAutoCreate());
        }
        else
        {
            Debug.LogWarning("[GameUIPanel] No panels assigned and auto-create disabled!");
        }
    }
    
    private bool HasAnyPreCreatedPanels()
    {
        return (scorePanels != null && scorePanels.Length > 0 && scorePanels[0] != null) ||
               (infoPanels != null && infoPanels.Length > 0 && infoPanels[0] != null) ||
               (statusPanels != null && statusPanels.Length > 0 && statusPanels[0] != null) ||
               (controlsPanels != null && controlsPanels.Length > 0 && controlsPanels[0] != null);
    }
    
    private void ApplyColorsToPreCreatedPanels()
    {
        if (scorePanels != null)
        {
            foreach (var tmp in scorePanels)
            {
                if (tmp != null) tmp.color = scoreColor;
            }
        }
        if (infoPanels != null)
        {
            foreach (var tmp in infoPanels)
            {
                if (tmp != null) tmp.color = infoColor;
            }
        }
        if (statusPanels != null)
        {
            foreach (var tmp in statusPanels)
            {
                if (tmp != null) tmp.color = statusColor;
            }
        }
        if (controlsPanels != null)
        {
            foreach (var tmp in controlsPanels)
            {
                if (tmp != null) tmp.color = controlsColor;
            }
        }
    }
    
    private System.Collections.IEnumerator DelayedAutoCreate()
    {
        yield return new WaitForSeconds(3.0f);
        
        FindTable();
        
        int attempts = 0;
        while (tableObject == null && attempts < 20)
        {
            yield return new WaitForSeconds(0.5f);
            FindTable();
            attempts++;
        }
        
        CreateAutoPanels();
        usingAutoCreatedPanels = true;
        initialized = true;
        
        Debug.Log($"[GameUIPanel] Auto-created panels. Table found: {tableObject != null}");
    }
    
    private void FindTable()
    {
        if (tableObject != null) return;
        
        GameObject byTag = GameObject.FindGameObjectWithTag("Table");
        if (byTag != null)
        {
            tableObject = byTag.transform;
            return;
        }
        
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
    
    private void CreateAutoPanels()
    {
        GameObject container = new GameObject("AutoScoreDisplays");
        
        // Try to find walls in the scene first
        if (attachToSceneWalls)
        {
            Transform[] foundWalls = FindSceneWalls();
            if (foundWalls != null && foundWalls.Length > 0)
            {
                autoWallPanels = new AutoWallPanel[foundWalls.Length];
                for (int i = 0; i < foundWalls.Length; i++)
                {
                    autoWallPanels[i] = CreatePanelOnWall(foundWalls[i], container.transform);
                }
                Debug.Log($"[GameUIPanel] Created {foundWalls.Length} panels on scene walls");
                return;
            }
        }
        
        // Fallback: create panels at fixed positions around table
        Vector3[] directions = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
        string[] wallNames = { "RightWall", "LeftWall", "FrontWall", "BackWall" };
        
        autoWallPanels = new AutoWallPanel[4];
        
        for (int i = 0; i < 4; i++)
        {
            autoWallPanels[i] = CreateAutoPanel(wallNames[i], directions[i], container.transform);
        }
        
        Debug.Log("[GameUIPanel] Created panels at fixed positions (no walls found)");
    }
    
    /// <summary>
    /// Find wall objects in the scene under Environment parent
    /// </summary>
    private Transform[] FindSceneWalls()
    {
        System.Collections.Generic.List<Transform> walls = new System.Collections.Generic.List<Transform>();
        
        // Try to find walls under the specified parent
        GameObject wallParent = GameObject.Find(wallParentName);
        if (wallParent != null)
        {
            // Search for children with "wall" in the name
            foreach (Transform child in wallParent.GetComponentsInChildren<Transform>(true))
            {
                if (child == wallParent.transform) continue; // Skip parent itself
                
                string nameLower = child.name.ToLower();
                if (nameLower.Contains("wall"))
                {
                    walls.Add(child);
                    Debug.Log($"[GameUIPanel] Found wall: {child.name} at {child.position}");
                }
            }
        }
        
        // Also try finding walls at root level
        if (walls.Count == 0)
        {
            // Search for any object with "wall" in name
            var allObjects = FindObjectsOfType<Transform>();
            foreach (var obj in allObjects)
            {
                string nameLower = obj.name.ToLower();
                if (nameLower.Contains("wall") && !nameLower.Contains("panel") && !nameLower.Contains("score"))
                {
                    // Check if it has a renderer (is a visible wall)
                    if (obj.GetComponent<Renderer>() != null || obj.GetComponentInChildren<Renderer>() != null)
                    {
                        walls.Add(obj);
                        Debug.Log($"[GameUIPanel] Found wall at root: {obj.name}");
                    }
                }
            }
        }
        
        return walls.ToArray();
    }
    
    /// <summary>
    /// Create a panel attached to a wall object
    /// </summary>
    private AutoWallPanel CreatePanelOnWall(Transform wall, Transform parent)
    {
        AutoWallPanel panel = new AutoWallPanel();
        
        panel.root = new GameObject($"ScorePanel_On_{wall.name}");
        panel.root.transform.SetParent(parent);
        
        // Get wall bounds to find center and normal
        Renderer wallRenderer = wall.GetComponent<Renderer>();
        if (wallRenderer == null) wallRenderer = wall.GetComponentInChildren<Renderer>();
        
        Vector3 wallCenter;
        Vector3 wallNormal;
        
        if (wallRenderer != null)
        {
            // Use renderer bounds to get center
            wallCenter = wallRenderer.bounds.center;
            wallCenter.y = wallHeight;
            
            // Determine wall normal based on wall orientation
            // Check which axis the wall is thinnest on - that's the normal direction
            Vector3 size = wallRenderer.bounds.size;
            if (size.x < size.z)
            {
                // Wall faces +X or -X
                wallNormal = wall.right;
            }
            else
            {
                // Wall faces +Z or -Z
                wallNormal = wall.forward;
            }
            
            // Make normal point toward table center if we have a table reference
            if (tableObject != null)
            {
                Vector3 toTable = (tableObject.position - wallCenter).normalized;
                if (Vector3.Dot(wallNormal, toTable) < 0)
                {
                    wallNormal = -wallNormal;
                }
            }
        }
        else
        {
            // Fallback: use wall transform position and forward
            wallCenter = wall.position;
            wallCenter.y = wallHeight;
            wallNormal = -wall.forward; // Assume wall faces its negative forward
        }
        
        // Position panel in front of wall (offset along wall normal toward room)
        Vector3 panelPosition = wallCenter + wallNormal * panelOffsetFromWall;
        panel.root.transform.position = panelPosition;
        
        // Face INTO the room (opposite of wall normal)
        // TextMeshPro text is readable when looking at the object's -Z direction
        // So we look AWAY from the room (back toward the wall) so viewers in the room can read it
        panel.root.transform.rotation = Quaternion.LookRotation(-wallNormal, Vector3.up);
        
        Debug.Log($"[GameUIPanel] Panel on {wall.name}: wallCenter={wallCenter}, wallNormal={wallNormal}, panelPos={panelPosition}");
        
        // Create background
        panel.background = CreateBackground(panel.root);
        
        // Create text elements
        panel.scoreText = CreateTextMesh("Score", panel.root, new Vector3(0, 0.4f, 0), fontSize, scoreColor, FontStyles.Bold);
        panel.infoText = CreateTextMesh("Info", panel.root, new Vector3(0, 0.15f, 0), fontSize * 0.5f, infoColor, FontStyles.Normal);
        panel.statusText = CreateTextMesh("Status", panel.root, new Vector3(0, -0.1f, 0), fontSize * 0.6f, statusColor, FontStyles.Normal);
        panel.controlsText = CreateTextMesh("Controls", panel.root, new Vector3(0, -0.35f, 0), fontSize * 0.4f, controlsColor, FontStyles.Normal);
        
        Debug.Log($"[GameUIPanel] Created panel on {wall.name} at {panelPosition}, facing {wallNormal}");
        
        return panel;
    }

    private AutoWallPanel CreateAutoPanel(string name, Vector3 direction, Transform parent)
    {
        AutoWallPanel panel = new AutoWallPanel();
        
        panel.root = new GameObject($"ScorePanel_{name}");
        panel.root.transform.SetParent(parent);
        
        Vector3 wallPosition = (tableObject != null ? tableObject.position : Vector3.zero) + direction * wallDistance;
        wallPosition.y = wallHeight;
        
        panel.root.transform.position = wallPosition;
        
        Vector3 lookDir = (tableObject != null ? tableObject.position : Vector3.zero) - wallPosition;
        lookDir.y = 0;
        if (lookDir.sqrMagnitude > 0.01f)
        {
            panel.root.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
        }
        else
        {
            panel.root.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);
        }
        
        // Create background
        panel.background = CreateBackground(panel.root);
        
        // Create text elements
        panel.scoreText = CreateTextMesh("Score", panel.root, new Vector3(0, 0.4f, 0), fontSize, scoreColor, FontStyles.Bold);
        panel.infoText = CreateTextMesh("Info", panel.root, new Vector3(0, 0.15f, 0), fontSize * 0.5f, infoColor, FontStyles.Normal);
        panel.statusText = CreateTextMesh("Status", panel.root, new Vector3(0, -0.1f, 0), fontSize * 0.6f, statusColor, FontStyles.Normal);
        panel.controlsText = CreateTextMesh("Controls", panel.root, new Vector3(0, -0.35f, 0), fontSize * 0.4f, controlsColor, FontStyles.Normal);
        
        return panel;
    }
    
    private TextMeshPro CreateTextMesh(string name, GameObject parent, Vector3 localPos, float size, Color color, FontStyles style)
    {
        GameObject obj = new GameObject(name);
        obj.transform.SetParent(parent.transform);
        obj.transform.localPosition = localPos;
        obj.transform.localRotation = Quaternion.identity; // Face same direction as panel (outward from wall)
        obj.transform.localScale = Vector3.one;
        
        TextMeshPro tmp = obj.AddComponent<TextMeshPro>();
        tmp.fontSize = size;
        tmp.color = color;
        tmp.fontStyle = style;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.rectTransform.sizeDelta = new Vector2(1.5f, 0.15f);
        tmp.enableWordWrapping = false;
        
        return tmp;
    }
    
    private GameObject CreateBackground(GameObject parent)
    {
        GameObject bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "Background";
        bg.transform.SetParent(parent.transform);
        bg.transform.localPosition = new Vector3(0, 0, 0.02f); // Slightly behind text but still in front of wall
        bg.transform.localRotation = Quaternion.Euler(0, 180f, 0); // Flip to face viewer (Quad faces -Z by default)
        bg.transform.localScale = new Vector3(backgroundSize.x, backgroundSize.y, 1f);
        
        var collider = bg.GetComponent<Collider>();
        if (collider != null) Destroy(collider);
        
        var renderer = bg.GetComponent<Renderer>();
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
        
        return bg;
    }
    
    private void Update()
    {
        if (!initialized) return;
        
        // Cache references
        if (runner == null) runner = FindObjectOfType<NetworkRunner>();
        if (tableTennisManager == null) tableTennisManager = FindObjectOfType<TableTennisManager>();
        if (gameManager == null) gameManager = FindObjectOfType<TableTennisGameManager>();
        
        // Get text content
        string score = GetScoreText();
        string info = GetPlayerInfo();
        string status = GetStatusText();
        string controls = GetControlsText();
        
        // Update panels based on mode
        if (usingAutoCreatedPanels && autoWallPanels != null)
        {
            UpdateAutoPanels(score, info, status, controls);
        }
        else
        {
            UpdatePreCreatedPanels(score, info, status, controls);
        }
    }
    
    private void UpdateAutoPanels(string score, string info, string status, string controls)
    {
        foreach (var panel in autoWallPanels)
        {
            if (panel == null) continue;
            if (panel.scoreText != null) panel.scoreText.text = score;
            if (panel.infoText != null) panel.infoText.text = info;
            if (panel.statusText != null) panel.statusText.text = status;
            if (panel.controlsText != null) panel.controlsText.text = controls;
        }
    }
    
    private void UpdatePreCreatedPanels(string score, string info, string status, string controls)
    {
        if (scorePanels != null)
        {
            foreach (var tmp in scorePanels)
            {
                if (tmp != null) tmp.text = score;
            }
        }
        if (infoPanels != null)
        {
            foreach (var tmp in infoPanels)
            {
                if (tmp != null) tmp.text = info;
            }
        }
        if (statusPanels != null)
        {
            foreach (var tmp in statusPanels)
            {
                if (tmp != null) tmp.text = status;
            }
        }
        if (controlsPanels != null)
        {
            foreach (var tmp in controlsPanels)
            {
                if (tmp != null) tmp.text = controls;
            }
        }
    }
    
    private string GetScoreText()
    {
        if (gameManager != null)
        {
            return $"{gameManager.Player1Score} - {gameManager.Player2Score}";
        }
        return "0 - 0";
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
            connectionStatus = playerCount >= 2 ? " | Connected" : " | Waiting...";
        }
        
        return $"First to {winScore} | {playerRole}{connectionStatus}";
    }
    
    private string GetStatusText()
    {
        if (tableTennisManager != null)
        {
            switch (tableTennisManager.CurrentPhase)
            {
                case TableTennisManager.GamePhase.TableSetup:
                    return "TABLE SETUP";
                    
                case TableTennisManager.GamePhase.BallPositioning:
                    return "POSITION THE BALL";
                    
                case TableTennisManager.GamePhase.Playing:
                    if (gameManager != null)
                    {
                        if (gameManager.CurrentGameState == TableTennisGameManager.GameState.GameOver)
                        {
                            return gameManager.Player1Score > gameManager.Player2Score ? "P1 WINS!" : "P2 WINS!";
                        }
                        return gameManager.CurrentServer == 1 ? "P1 Serve" : "P2 Serve";
                    }
                    return "PLAYING";
            }
        }
        
        return "GRIP to Start";
    }
    
    private string GetControlsText()
    {
        if (tableTennisManager != null)
        {
            switch (tableTennisManager.CurrentPhase)
            {
                case TableTennisManager.GamePhase.TableSetup:
                    return "Press A for Table Adjust Mode\n" +
                           "Left Stick Y: Height | Right Stick X: Rotate\n" +
                           "Press B or Y: Toggle Racket\n" +
                           "Press GRIP to Start Game";
                    
                case TableTennisManager.GamePhase.BallPositioning:
                    return "Left Stick: Move Ball (X/Z)\n" +
                           "Right Stick Y: Ball Height\n" +
                           "Hit the ball to start playing!";
                    
                case TableTennisManager.GamePhase.Playing:
                    return "Hit ball with racket | First to 11 wins";
            }
        }
        
        return "GRIP to Start";
    }
}
