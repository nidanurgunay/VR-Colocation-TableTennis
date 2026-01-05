using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Fusion;

/// <summary>
/// World-space UI panels showing game score and instructions.
/// Creates 4 wall panels (with instructions) and 1 table panel (score only).
/// </summary>
public class GameUIPanel : MonoBehaviour
{
    [Header("Anchor References")]
    [SerializeField] private Transform anchor1; // Player 1 side anchor
    [SerializeField] private Transform anchor2; // Player 2 side anchor
    
    [Header("Panel Settings")]
    [SerializeField] private Vector2 wallPanelSize = new Vector2(1.2f, 0.8f); // Wall panels
    [SerializeField] private Vector2 tablePanelSize = new Vector2(0.6f, 0.3f); // Table panel (smaller)
    [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.2f, 0.9f);
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private Color scoreColor = Color.yellow;
    
    [Header("Wall Panel Positions")]
    [SerializeField] private float wallDistance = 3f; // Distance from table center to walls
    [SerializeField] private float wallHeight = 1.8f; // Height of wall panels
    
    [Header("Table Panel Position")]
    [SerializeField] private float tableHeight = 0.5f; // Height above table surface
    [SerializeField] private Transform tableObject; // Reference to actual table object
    [SerializeField] private string tableTag = "Table"; // Tag to find table if not assigned
    
    // All created panels for updating
    private System.Collections.Generic.List<PanelUI> allPanels = new System.Collections.Generic.List<PanelUI>();
    
    // References
    private TableTennisGameManager gameManager;
    private Vector3 tableCenter;
    private Quaternion tableRotation;
    private Transform tablePanelParent; // Parent for table panel to follow table
    
    private class PanelUI
    {
        public Canvas canvas;
        public TextMeshProUGUI scoreText;
        public TextMeshProUGUI statusText;
        public TextMeshProUGUI instructionsText; // null for table panel
        public bool isTablePanel;
    }
    
    private void Start()
    {
        StartCoroutine(Initialize());
    }
    
    private System.Collections.IEnumerator Initialize()
    {
        yield return new WaitForSeconds(1.5f);
        
        // Find anchors if not assigned
        FindAnchors();
        
        // Find the actual table object
        FindTableObject();
        
        // Calculate table center and rotation from anchors
        CalculateTableTransform();
        
        // Find game manager
        gameManager = FindObjectOfType<TableTennisGameManager>();
        
        // Create all panels
        CreateWallPanels();
        CreateTablePanel();
        
        Debug.Log($"[GameUIPanel] Created {allPanels.Count} panels. Table: {(tableObject != null ? tableObject.name : "not found")}");
    }
    
    private void FindAnchors()
    {
        if (anchor1 != null && anchor2 != null) return;
        
        var anchors = FindObjectsOfType<OVRSpatialAnchor>();
        var activeAnchors = new System.Collections.Generic.List<OVRSpatialAnchor>();
        
        foreach (var anchor in anchors)
        {
            if (anchor.gameObject.activeInHierarchy)
            {
                activeAnchors.Add(anchor);
            }
        }
        
        if (activeAnchors.Count >= 2)
        {
            anchor1 = activeAnchors[0].transform;
            anchor2 = activeAnchors[1].transform;
            Debug.Log($"[GameUIPanel] Found 2 anchors: {anchor1.name}, {anchor2.name}");
        }
        else if (activeAnchors.Count == 1)
        {
            anchor1 = activeAnchors[0].transform;
            anchor2 = activeAnchors[0].transform;
            Debug.LogWarning("[GameUIPanel] Only 1 anchor found, using same for both");
        }
        else
        {
            // Fallback to camera rig
            var cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig != null)
            {
                anchor1 = cameraRig.transform;
                anchor2 = cameraRig.transform;
                Debug.LogWarning("[GameUIPanel] No anchors found, using camera rig");
            }
        }
    }
    
    private void FindTableObject()
    {
        if (tableObject != null) return;
        
        // Try to find by tag
        GameObject tableByTag = GameObject.FindGameObjectWithTag(tableTag);
        if (tableByTag != null)
        {
            tableObject = tableByTag.transform;
            Debug.Log($"[GameUIPanel] Found table by tag: {tableObject.name}");
            return;
        }
        
        // Try to find by name containing "table"
        var allObjects = FindObjectsOfType<Transform>();
        foreach (var obj in allObjects)
        {
            if (obj.name.ToLower().Contains("table") && obj.GetComponent<Collider>() != null)
            {
                tableObject = obj;
                Debug.Log($"[GameUIPanel] Found table by name: {tableObject.name}");
                return;
            }
        }
        
        Debug.LogWarning("[GameUIPanel] Could not find table object. Assign it in Inspector or tag it as 'Table'");
    }
    
    private void CalculateTableTransform()
    {
        if (anchor1 == null || anchor2 == null)
        {
            tableCenter = Vector3.zero;
            tableRotation = Quaternion.identity;
            return;
        }
        
        // Table center is midpoint between anchors
        tableCenter = (anchor1.position + anchor2.position) / 2f;
        
        // Table rotation - use anchor1's rotation (assumes anchors are aligned with table)
        tableRotation = anchor1.rotation;
    }
    
    private void CreateWallPanels()
    {
        // 4 wall panels around the table
        // Using table rotation to orient them correctly
        
        Vector3 forward = tableRotation * Vector3.forward;
        Vector3 right = tableRotation * Vector3.right;
        
        // Wall positions: front, back, left, right of table
        Vector3[] wallPositions = new Vector3[]
        {
            tableCenter + forward * wallDistance + Vector3.up * wallHeight,  // Front
            tableCenter - forward * wallDistance + Vector3.up * wallHeight,  // Back
            tableCenter + right * wallDistance + Vector3.up * wallHeight,    // Right
            tableCenter - right * wallDistance + Vector3.up * wallHeight,    // Left
        };
        
        // Each wall faces the table center
        for (int i = 0; i < wallPositions.Length; i++)
        {
            CreateWallPanel(wallPositions[i], i);
        }
    }
    
    private void CreateWallPanel(Vector3 position, int index)
    {
        GameObject panelObj = new GameObject($"WallPanel_{index}");
        panelObj.transform.SetParent(transform);
        panelObj.transform.position = position;
        
        // Face the table center
        panelObj.transform.LookAt(new Vector3(tableCenter.x, position.y, tableCenter.z));
        panelObj.transform.Rotate(0, 180, 0); // Face outward from table
        
        PanelUI panel = CreatePanelCanvas(panelObj, wallPanelSize, includeInstructions: true);
        panel.isTablePanel = false;
        allPanels.Add(panel);
    }
    
    private void CreateTablePanel()
    {
        // Create a container that follows the table
        tablePanelParent = new GameObject("TablePanelContainer").transform;
        
        if (tableObject != null)
        {
            // Parent to the table so it moves with it
            tablePanelParent.SetParent(tableObject);
            tablePanelParent.localPosition = Vector3.up * tableHeight;
            tablePanelParent.localRotation = Quaternion.identity;
        }
        else
        {
            // Fallback to anchor-based position
            tablePanelParent.SetParent(transform);
            tablePanelParent.position = tableCenter + Vector3.up * tableHeight;
            tablePanelParent.rotation = tableRotation;
        }
        
        // Panel facing one direction
        GameObject panelObj = new GameObject("TablePanel");
        panelObj.transform.SetParent(tablePanelParent);
        panelObj.transform.localPosition = Vector3.zero;
        panelObj.transform.localRotation = Quaternion.Euler(45, 0, 0); // Tilt toward player
        
        PanelUI panel = CreatePanelCanvas(panelObj, tablePanelSize, includeInstructions: false);
        panel.isTablePanel = true;
        allPanels.Add(panel);
        
        // Panel facing opposite direction (for other player)
        GameObject panelObj2 = new GameObject("TablePanel_Back");
        panelObj2.transform.SetParent(tablePanelParent);
        panelObj2.transform.localPosition = Vector3.zero;
        panelObj2.transform.localRotation = Quaternion.Euler(45, 180, 0); // Face other way
        
        PanelUI panel2 = CreatePanelCanvas(panelObj2, tablePanelSize, includeInstructions: false);
        panel2.isTablePanel = true;
        allPanels.Add(panel2);
    }
    
    private PanelUI CreatePanelCanvas(GameObject parent, Vector2 size, bool includeInstructions)
    {
        PanelUI panelUI = new PanelUI();
        
        // Create canvas
        GameObject canvasObj = new GameObject("Canvas");
        canvasObj.transform.SetParent(parent.transform);
        canvasObj.transform.localPosition = Vector3.zero;
        canvasObj.transform.localRotation = Quaternion.identity;
        
        panelUI.canvas = canvasObj.AddComponent<Canvas>();
        panelUI.canvas.renderMode = RenderMode.WorldSpace;
        
        var scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 100;
        
        canvasObj.AddComponent<GraphicRaycaster>();
        
        // Set canvas size
        RectTransform canvasRect = panelUI.canvas.GetComponent<RectTransform>();
        canvasRect.sizeDelta = size * 1000; // Convert to UI units
        canvasRect.localScale = Vector3.one * 0.001f; // Scale down to world units
        
        // Create background
        GameObject bgObj = new GameObject("Background");
        bgObj.transform.SetParent(canvasRect);
        var bgImage = bgObj.AddComponent<Image>();
        bgImage.color = backgroundColor;
        
        RectTransform bgRect = bgObj.GetComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        
        if (includeInstructions)
        {
            // WALL PANEL: Score + Status + Instructions
            
            // Score text (top)
            panelUI.scoreText = CreateText("ScoreText", canvasRect,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0, -40), new Vector2(800, 120));
            panelUI.scoreText.fontSize = 100;
            panelUI.scoreText.color = scoreColor;
            panelUI.scoreText.alignment = TextAlignmentOptions.Center;
            panelUI.scoreText.fontStyle = FontStyles.Bold;
            panelUI.scoreText.text = "0 - 0";
            
            // Status text (below score)
            panelUI.statusText = CreateText("StatusText", canvasRect,
                new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0, -140), new Vector2(800, 60));
            panelUI.statusText.fontSize = 40;
            panelUI.statusText.color = textColor;
            panelUI.statusText.alignment = TextAlignmentOptions.Center;
            panelUI.statusText.text = "Press GRIP to Start";
            
            // Instructions text (bottom half)
            panelUI.instructionsText = CreateText("InstructionsText", canvasRect,
                new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0, 180), new Vector2(1100, 350));
            panelUI.instructionsText.fontSize = 28;
            panelUI.instructionsText.color = textColor;
            panelUI.instructionsText.alignment = TextAlignmentOptions.Left;
            panelUI.instructionsText.text = GetInstructionsText();
        }
        else
        {
            // TABLE PANEL: Score only (larger text)
            
            panelUI.scoreText = CreateText("ScoreText", canvasRect,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, 30), new Vector2(500, 150));
            panelUI.scoreText.fontSize = 140;
            panelUI.scoreText.color = scoreColor;
            panelUI.scoreText.alignment = TextAlignmentOptions.Center;
            panelUI.scoreText.fontStyle = FontStyles.Bold;
            panelUI.scoreText.text = "0 - 0";
            
            // Small status text below
            panelUI.statusText = CreateText("StatusText", canvasRect,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0, -60), new Vector2(500, 50));
            panelUI.statusText.fontSize = 36;
            panelUI.statusText.color = textColor;
            panelUI.statusText.alignment = TextAlignmentOptions.Center;
            panelUI.statusText.text = "";
        }
        
        return panelUI;
    }
    
    private TextMeshProUGUI CreateText(string name, Transform parent,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
    {
        GameObject textObj = new GameObject(name);
        textObj.transform.SetParent(parent);
        
        TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
        
        RectTransform rect = textObj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one;
        rect.localRotation = Quaternion.identity;
        
        return tmp;
    }
    
    private string GetInstructionsText()
    {
        return @"<b>CONTROLS</b>
<color=yellow>B</color> / <color=yellow>Y</color>  -  Show Racket
<color=yellow>A</color>  -  Table Adjust Mode
   + <color=cyan>L-Stick</color>  -  Move (X/Z)
   + <color=cyan>R-Stick X</color>  -  Rotate
   + <color=cyan>R-Stick Y</color>  -  Up/Down
<color=yellow>GRIP</color>  -  Start Game

<b>RULES</b>
• First to 11 (win by 2)
• Serve bounces on your side first
• Double bounce = point";
    }
    
    private void Update()
    {
        if (gameManager == null)
        {
            gameManager = FindObjectOfType<TableTennisGameManager>();
            return;
        }
        
        // Update all panels
        foreach (var panel in allPanels)
        {
            if (panel.scoreText != null)
            {
                panel.scoreText.text = $"{gameManager.Player1Score} - {gameManager.Player2Score}";
            }
            
            if (panel.statusText != null)
            {
                switch (gameManager.CurrentGameState)
                {
                    case TableTennisGameManager.GameState.WaitingToStart:
                        panel.statusText.text = panel.isTablePanel ? "" : "Press GRIP to Start";
                        break;
                    case TableTennisGameManager.GameState.Serving:
                        panel.statusText.text = $"P{gameManager.CurrentServer} Serve";
                        break;
                    case TableTennisGameManager.GameState.Playing:
                        panel.statusText.text = panel.isTablePanel ? "" : "Playing";
                        break;
                    case TableTennisGameManager.GameState.PointScored:
                        panel.statusText.text = "Point!";
                        break;
                    case TableTennisGameManager.GameState.GameOver:
                        int winner = gameManager.Player1Score > gameManager.Player2Score ? 1 : 2;
                        panel.statusText.text = $"P{winner} Wins!";
                        break;
                }
            }
        }
    }
}
