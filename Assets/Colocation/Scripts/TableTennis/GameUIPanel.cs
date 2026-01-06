using UnityEngine;
using TMPro;

/// <summary>
/// Simple world-space score display using TextMeshPro 3D text.
/// Shows score on a wall, visible from the table area.
/// </summary>
public class GameUIPanel : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField] private float wallHeight = 1.5f; // Height on wall
    [SerializeField] private float wallDistance = 2.0f; // Distance from table center to wall (along table's right side)
    [SerializeField] private float fontSize = 3f; // Larger font for wall visibility
    [SerializeField] private Color scoreColor = Color.yellow;
    [SerializeField] private Color statusColor = Color.white;
    [SerializeField] private Color backgroundColor = new Color(0.1f, 0.1f, 0.3f, 0.95f); // Dark blue
    [SerializeField] private Vector2 backgroundSize = new Vector2(0.8f, 0.4f); // Larger background for wall
    
    // Text components (single sided now)
    private TextMeshPro scoreText;
    private TextMeshPro statusText;
    
    // Background quad
    private GameObject background;
    
    // References
    private Transform tableObject;
    private TableTennisGameManager gameManager;
    private GameObject panelRoot;
    
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
        // Create root object
        panelRoot = new GameObject("ScoreDisplay");
        
        if (tableObject != null)
        {
            // Position on the wall to the right side of the table (perpendicular to long axis)
            // Table's right direction is its local X axis
            Vector3 wallPosition = tableObject.position + tableObject.right * wallDistance;
            wallPosition.y = wallHeight;
            
            panelRoot.transform.position = wallPosition;
            
            // Face back toward the table (rotate to look at table center)
            Vector3 lookDir = tableObject.position - wallPosition;
            lookDir.y = 0; // Keep panel vertical
            if (lookDir.sqrMagnitude > 0.01f)
            {
                panelRoot.transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
            }
        }
        else
        {
            panelRoot.transform.position = new Vector3(wallDistance, wallHeight, 0);
            panelRoot.transform.rotation = Quaternion.Euler(0, -90f, 0); // Face left
        }
        
        // Create background panel (slightly behind text)
        background = CreateBackground("Background", new Vector3(0, 0, 0.01f));
        
        // Create score text
        scoreText = CreateTextMesh("ScoreText", Vector3.zero);
        scoreText.fontSize = fontSize;
        scoreText.color = scoreColor;
        scoreText.fontStyle = FontStyles.Bold;
        scoreText.alignment = TextAlignmentOptions.Center;
        scoreText.text = "0 - 0";
        
        // Create status text below score
        statusText = CreateTextMesh("StatusText", new Vector3(0, -0.12f, 0));
        statusText.fontSize = fontSize * 0.5f;
        statusText.color = statusColor;
        statusText.alignment = TextAlignmentOptions.Center;
        statusText.text = "GRIP to Start";
        
        Debug.Log($"[GameUIPanel] Wall panel created at {panelRoot.transform.position}, facing table at {tableObject?.position}");
    }
    
    private TextMeshPro CreateTextMesh(string name, Vector3 localOffset)
    {
        GameObject textObj = new GameObject(name);
        textObj.transform.SetParent(panelRoot.transform);
        textObj.transform.localPosition = localOffset;
        textObj.transform.localRotation = Quaternion.identity;
        textObj.transform.localScale = Vector3.one;
        
        TextMeshPro tmp = textObj.AddComponent<TextMeshPro>();
        tmp.rectTransform.sizeDelta = new Vector2(0.8f, 0.25f);
        
        return tmp;
    }
    
    private GameObject CreateBackground(string name, Vector3 localOffset)
    {
        GameObject bgObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bgObj.name = name;
        bgObj.transform.SetParent(panelRoot.transform);
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
        
        // Update score text
        if (gameManager != null)
        {
            string score = $"{gameManager.Player1Score} - {gameManager.Player2Score}";
            if (scoreText != null) scoreText.text = score;
            
            string status = GetStatusText();
            if (statusText != null) statusText.text = status;
        }
        else
        {
            gameManager = FindObjectOfType<TableTennisGameManager>();
        }
    }
    
    private string GetStatusText()
    {
        if (gameManager == null) return "GRIP to Start";
        
        switch (gameManager.CurrentGameState)
        {
            case TableTennisGameManager.GameState.GameOver:
                return gameManager.Player1Score > gameManager.Player2Score ? "P1 Wins!" : "P2 Wins!";
                
            case TableTennisGameManager.GameState.WaitingToStart:
                return "GRIP to Start";
                
            case TableTennisGameManager.GameState.Serving:
            case TableTennisGameManager.GameState.Playing:
            case TableTennisGameManager.GameState.PointScored:
                return gameManager.CurrentServer == 1 ? "P1 Serve" : "P2 Serve";
                
            default:
                return "GRIP to Start";
        }
    }
}
