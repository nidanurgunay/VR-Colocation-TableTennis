using UnityEngine;
using System.Collections;
using System.Collections.Generic;

#if FUSION2
using Fusion;
#endif

/// <summary>
/// Manages passthrough table tennis game - table spawning, adjustment, rackets, and ball.
/// Works with AnchorGUIManager_AutoAlignment for anchor references.
/// </summary>
public class PassthroughGameManager : NetworkBehaviour
{
    private const string LOG_TAG = "[PassthroughGame]";
    
    [Header("Prefabs")]
    [SerializeField] private TableTennisConfig sharedConfig;
    [SerializeField] private GameObject tablePrefab;
    [SerializeField] private GameObject racketPrefab;
    [SerializeField] private NetworkPrefabRef ballPrefab;
    
    [Header("Table Settings")]
    [SerializeField] private float tableRotateSpeed = 90f;
    [SerializeField] private float tableMoveSpeed = 1f;
    [SerializeField] private float tableXRotationOffset = 180f;
    [SerializeField] private float tableYRotationOffset = 90f;
    [SerializeField] private float defaultTableHeight = 0.76f;
    
    // Prefab accessors
    private GameObject TablePrefab => sharedConfig != null ? sharedConfig.TablePrefab : tablePrefab;
    private GameObject RacketPrefab => sharedConfig != null ? sharedConfig.RacketPrefab : racketPrefab;
    private NetworkPrefabRef BallPrefab => sharedConfig != null && sharedConfig.BallPrefab != default ? sharedConfig.BallPrefab : ballPrefab;
    private float TableRotateSpeed => sharedConfig != null ? sharedConfig.tableRotateSpeed : tableRotateSpeed;
    private float TableMoveSpeed => sharedConfig != null ? sharedConfig.tableMoveSpeed : tableMoveSpeed;
    
    // Game phases
    public enum GamePhase { 
        Idle,           // Not in passthrough mode
        TableAdjust,    // Adjusting table position/rotation
        BallPosition,   // Ball spawned, adjusting position
        Playing         // Game in progress
    }
    
    // State
    private GamePhase currentPhase = GamePhase.Idle;
    private bool isActive = false;
    private bool isGameMenuOpen = false;
    private bool racketsVisible = true;
    
    // Racket offset/rotation settings (matching VR scene's ControllerRacket for consistency)
    private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f);
    private Vector3 racketRotation = new Vector3(-51f, 240f, 43f);
    private float racketScale = 10f;
    
    // References
    private GameObject spawnedTable;
    private GameObject spawnedBall;
    private GameObject leftRacket;
    private GameObject rightRacket;
    private List<OVRSpatialAnchor> anchors;
    private GameObject runtimeMenuPanel;
    private GameObject gameUIPanel;
    private TextMesh scoreText;
    private TextMesh infoText;
    private TextMesh statusText;
    private TextMesh controlsText;
    
#if FUSION2
    [Networked] private float NetworkedTableYRotation { get; set; }
    [Networked] private float NetworkedTableHeight { get; set; }
    [Networked] private NetworkBool NetworkedGameActive { get; set; }
#endif
    
    // Properties
    public bool IsActive => isActive;
    public GamePhase CurrentPhase => currentPhase;
    
    /// <summary>
    /// Initialize and start the passthrough game
    /// </summary>
    public void StartGame(List<OVRSpatialAnchor> anchorList)
    {
        if (anchorList == null || anchorList.Count < 2)
        {
            Debug.LogWarning($"{LOG_TAG} Cannot start - need 2 anchors");
            return;
        }
        
        anchors = anchorList;
        isActive = true;
        currentPhase = GamePhase.TableAdjust;
        
        Debug.Log($"{LOG_TAG} Starting passthrough game");
        
        SpawnTable();
        StartCoroutine(EnableRacketsDelayed());
        UpdateInstructions();
    }
    
    /// <summary>
    /// Stop the passthrough game and cleanup
    /// </summary>
    public void StopGame()
    {
        isActive = false;
        currentPhase = GamePhase.Idle;
        isGameMenuOpen = false;
        
        // Cleanup spawned objects
        if (spawnedBall != null)
        {
#if FUSION2
            if (Runner != null && Object.HasStateAuthority)
            {
                var netObj = spawnedBall.GetComponent<NetworkObject>();
                if (netObj != null) Runner.Despawn(netObj);
            }
            else
#endif
            {
                Destroy(spawnedBall);
            }
            spawnedBall = null;
        }
        
        if (spawnedTable != null)
        {
            spawnedTable.SetActive(false);
        }
        
        // Hide rackets
        if (leftRacket != null) leftRacket.SetActive(false);
        if (rightRacket != null) rightRacket.SetActive(false);
        
        // Cleanup UI
        if (gameUIPanel != null) Destroy(gameUIPanel);
        if (runtimeMenuPanel != null) Destroy(runtimeMenuPanel);
        
#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedGameActive = false;
        }
#endif
        
        Debug.Log($"{LOG_TAG} Game stopped");
    }
    
    private void Update()
    {
        if (!isActive) return;
        
        HandleInput();
    }
    
#if FUSION2
    public override void FixedUpdateNetwork()
    {
        if (!NetworkedGameActive) return;
        
        // Find table if not set
        if (spawnedTable == null)
        {
            spawnedTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
            if (spawnedTable != null && anchors != null && anchors.Count > 0)
            {
                spawnedTable.transform.SetParent(anchors[0].transform, worldPositionStays: false);
            }
        }
        
        if (spawnedTable != null)
        {
            ApplyTableState();
        }
    }
#endif
    
    // ==================== INPUT HANDLING ====================
    
    private void HandleInput()
    {
        // MENU button - toggle game menu
        if (OVRInput.GetDown(OVRInput.Button.Start))
        {
            ToggleGameMenu();
            return;
        }
        
        if (isGameMenuOpen)
        {
            HandleMenuInput();
            return;
        }
        
        // B/Y button - toggle racket visibility (except during Playing)
        bool bPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool yPressed = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch);
        if ((bPressed || yPressed) && currentPhase != GamePhase.Playing)
        {
            ToggleRackets();
            return;
        }
        
        // A/X button for phase-specific actions
        bool aPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool xPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                if (aPressed || xPressed)
                    AdvancePhase();
                else
                    HandleTableAdjust();
                break;
                
            case GamePhase.BallPosition:
                HandleBallPosition(aPressed || xPressed);
                break;
                
            case GamePhase.Playing:
                // Game in progress
                break;
        }
    }
    
    private void HandleTableAdjust()
    {
        if (spawnedTable == null) return;
        
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        bool hasInput = Mathf.Abs(rightStick.x) > 0.05f || Mathf.Abs(rightStick.y) > 0.05f;
        if (!hasInput) return;
        
        float rotationDelta = rightStick.x * TableRotateSpeed * Time.deltaTime;
        float heightDelta = rightStick.y * TableMoveSpeed * Time.deltaTime;
        
#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedTableYRotation += rotationDelta;
            NetworkedTableHeight += heightDelta;
            ApplyTableState();
        }
        else if (Runner != null)
        {
            RPC_RequestTableAdjust(rotationDelta, heightDelta);
        }
#else
        ApplyLocalAdjustment(rotationDelta, heightDelta);
#endif
    }
    
    private void HandleBallPosition(bool confirmPressed)
    {
        // GRIP spawns ball if not spawned
        bool gripPressed = OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger) ||
                          OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger);
        
        if (gripPressed && spawnedBall == null)
        {
            SpawnBall();
        }
        
        // A/X held + thumbstick adjusts ball position
        bool axHeld = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch) ||
                     OVRInput.Get(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        
        if (spawnedBall != null && axHeld)
        {
            Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
            if (Mathf.Abs(stick.x) > 0.1f || Mathf.Abs(stick.y) > 0.1f)
            {
                Vector3 movement = new Vector3(stick.x, stick.y, 0) * TableMoveSpeed * Time.deltaTime;
#if FUSION2
                if (Object.HasStateAuthority)
                {
                    spawnedBall.transform.position += movement;
                }
                else
                {
                    RPC_RequestBallMove(movement);
                }
#else
                spawnedBall.transform.position += movement;
#endif
            }
        }
    }
    
    private void HandleMenuInput()
    {
        bool aPressed = OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
        bool bPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool xPressed = OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch);
        bool yPressed = OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch);
        
        Vector2 stick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        
        // A/X = Resume
        if (aPressed || xPressed)
        {
            ToggleGameMenu();
        }
        // B/Y = Restart
        else if (bPressed || yPressed)
        {
            RestartGame();
        }
        // Thumbstick down = Return to menu
        else if (stick.y < -0.7f)
        {
            ReturnToMainMenu();
        }
    }
    
    // ==================== TABLE MANAGEMENT ====================
    
    private void SpawnTable()
    {
        if (anchors == null || anchors.Count < 2) return;
        
        Transform primary = anchors[0].transform;
        Transform secondary = anchors[1].transform;
        
        // Calculate position in local space
        Vector3 secondaryLocal = primary.InverseTransformPoint(secondary.position);
        Vector3 localMidpoint = secondaryLocal / 2f;
        Vector3 localPos = new Vector3(localMidpoint.x, defaultTableHeight, localMidpoint.z);
        
        // Calculate rotation
        Vector3 dir = secondaryLocal;
        dir.y = 0;
        float yRot = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            yRot = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + tableYRotationOffset;
        }
        
        // Find or spawn table
        if (spawnedTable == null)
        {
            spawnedTable = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable");
        }
        
        if (spawnedTable != null)
        {
            spawnedTable.transform.SetParent(primary, worldPositionStays: false);
            spawnedTable.transform.localPosition = localPos;
            spawnedTable.transform.localRotation = Quaternion.Euler(tableXRotationOffset, yRot, 0);
            spawnedTable.SetActive(true);
        }
        else if (TablePrefab != null)
        {
            spawnedTable = Instantiate(TablePrefab);
            spawnedTable.transform.SetParent(primary, worldPositionStays: false);
            spawnedTable.transform.localPosition = localPos;
            spawnedTable.transform.localRotation = Quaternion.Euler(tableXRotationOffset, yRot, 0);
        }
        else
        {
            Debug.LogWarning($"{LOG_TAG} No table prefab!");
            return;
        }
        
#if FUSION2
        if (Object != null && Object.HasStateAuthority)
        {
            NetworkedTableYRotation = yRot;
            NetworkedTableHeight = localPos.y;
            NetworkedGameActive = true;
        }
#endif
        
        Debug.Log($"{LOG_TAG} Table spawned at height {localPos.y}");
        CreateGameUI();
    }
    
    private void ApplyTableState()
    {
        if (spawnedTable == null || anchors == null || anchors.Count == 0) return;
        
#if FUSION2
        spawnedTable.transform.localRotation = Quaternion.Euler(
            tableXRotationOffset, NetworkedTableYRotation, 0);
        
        Vector3 pos = spawnedTable.transform.localPosition;
        pos.y = NetworkedTableHeight;
        spawnedTable.transform.localPosition = pos;
#endif
    }
    
    private void ApplyLocalAdjustment(float rotDelta, float heightDelta)
    {
        if (spawnedTable == null) return;
        
        Vector3 euler = spawnedTable.transform.localEulerAngles;
        spawnedTable.transform.localRotation = Quaternion.Euler(euler.x, euler.y + rotDelta, 0);
        
        Vector3 pos = spawnedTable.transform.localPosition;
        pos.y += heightDelta;
        spawnedTable.transform.localPosition = pos;
    }
    
    // ==================== RACKET MANAGEMENT ====================
    
    private IEnumerator EnableRacketsDelayed()
    {
        yield return new WaitForSeconds(0.5f);
        EnableRackets();
    }
    
    private void EnableRackets()
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>();
        if (cameraRig == null) return;
        
        // Disable any existing ControllerRacket scripts (from VR mode)
        var existingRackets = FindObjectsOfType<ControllerRacket>();
        foreach (var r in existingRackets)
        {
            r.enabled = false;
            r.gameObject.SetActive(false);
        }
        
        // Try to find or create rackets
        GameObject racketPrefabToUse = RacketPrefab;
        if (racketPrefabToUse == null)
        {
            racketPrefabToUse = Resources.Load<GameObject>("PingPongBat");
        }
        
        if (racketPrefabToUse != null)
        {
            // Left hand
            if (leftRacket == null)
            {
                leftRacket = Instantiate(racketPrefabToUse, cameraRig.leftControllerAnchor);
                leftRacket.transform.localPosition = racketOffset;
                leftRacket.transform.localRotation = Quaternion.Euler(racketRotation);
                leftRacket.transform.localScale = Vector3.one * racketScale;
                leftRacket.name = "LeftRacket";
            }
            leftRacket.SetActive(true);
            
            // Right hand
            if (rightRacket == null)
            {
                rightRacket = Instantiate(racketPrefabToUse, cameraRig.rightControllerAnchor);
                rightRacket.transform.localPosition = racketOffset;
                rightRacket.transform.localRotation = Quaternion.Euler(racketRotation);
                rightRacket.transform.localScale = Vector3.one * racketScale;
                rightRacket.name = "RightRacket";
            }
            rightRacket.SetActive(true);
        }
        
        racketsVisible = true;
        Debug.Log($"{LOG_TAG} Rackets enabled");
    }
    
    private void ToggleRackets()
    {
        racketsVisible = !racketsVisible;
        if (leftRacket != null) leftRacket.SetActive(racketsVisible);
        if (rightRacket != null) rightRacket.SetActive(racketsVisible);
        Debug.Log($"{LOG_TAG} Rackets {(racketsVisible ? "visible" : "hidden")}");
    }
    
    // ==================== BALL MANAGEMENT ====================
    
    private void SpawnBall()
    {
        if (spawnedTable == null) return;
        
        Vector3 spawnPos = spawnedTable.transform.position + Vector3.up * 0.5f;
        
#if FUSION2
        if (Object.HasStateAuthority && Runner != null)
        {
            var ball = Runner.Spawn(BallPrefab, spawnPos, Quaternion.identity);
            if (ball != null)
            {
                spawnedBall = ball.gameObject;
                var networkedBall = ball.GetComponent<NetworkedBall>();
                if (networkedBall != null)
                {
                    networkedBall.EnterPositioningMode();
                }
                RPC_NotifyBallSpawned();
            }
        }
        else
        {
            RPC_RequestSpawnBall();
        }
#else
        // Non-networked fallback
        var ballPrefabObj = Resources.Load<GameObject>("Ball");
        if (ballPrefabObj != null)
        {
            spawnedBall = Instantiate(ballPrefabObj, spawnPos, Quaternion.identity);
        }
#endif
        
        Debug.Log($"{LOG_TAG} Ball spawned");
    }
    
    // ==================== GAME FLOW ====================
    
    private void AdvancePhase()
    {
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                currentPhase = GamePhase.BallPosition;
                Debug.Log($"{LOG_TAG} Phase: BallPosition");
                break;
                
            case GamePhase.BallPosition:
                if (spawnedBall != null)
                {
                    currentPhase = GamePhase.Playing;
                    Debug.Log($"{LOG_TAG} Phase: Playing");
                }
                break;
        }
        
        UpdateInstructions();
    }
    
    private void RestartGame()
    {
        isGameMenuOpen = false;
        if (runtimeMenuPanel != null) runtimeMenuPanel.SetActive(false);
        
        // Cleanup ball
        if (spawnedBall != null)
        {
#if FUSION2
            if (Runner != null && Object.HasStateAuthority)
            {
                var netObj = spawnedBall.GetComponent<NetworkObject>();
                if (netObj != null) Runner.Despawn(netObj);
            }
#endif
            spawnedBall = null;
        }
        
        currentPhase = GamePhase.TableAdjust;
        UpdateInstructions();
        Debug.Log($"{LOG_TAG} Game restarted");
    }
    
    private void ReturnToMainMenu()
    {
        StopGame();
        // Notify parent to show main menu
        var anchorManager = FindObjectOfType<AnchorGUIManager_AutoAlignment>();
        if (anchorManager != null)
        {
            anchorManager.OnPassthroughGameEnded();
        }
    }
    
    // ==================== UI ====================
    
    private void ToggleGameMenu()
    {
        isGameMenuOpen = !isGameMenuOpen;
        
        if (isGameMenuOpen)
        {
            ShowGameMenu();
        }
        else if (runtimeMenuPanel != null)
        {
            runtimeMenuPanel.SetActive(false);
        }
        
        Debug.Log($"{LOG_TAG} Menu {(isGameMenuOpen ? "opened" : "closed")}");
    }
    
    private void ShowGameMenu()
    {
        if (runtimeMenuPanel == null)
        {
            CreateMenuPanel();
        }
        runtimeMenuPanel.SetActive(true);
    }
    
    private void CreateMenuPanel()
    {
        var cam = Camera.main;
        if (cam == null) return;
        
        runtimeMenuPanel = new GameObject("PassthroughGameMenu");
        runtimeMenuPanel.transform.position = cam.transform.position + cam.transform.forward * 1.5f;
        runtimeMenuPanel.transform.rotation = Quaternion.LookRotation(
            runtimeMenuPanel.transform.position - cam.transform.position);
        
        // Create menu text
        var textGO = new GameObject("MenuText");
        textGO.transform.SetParent(runtimeMenuPanel.transform, false);
        var textMesh = textGO.AddComponent<TextMesh>();
        textMesh.text = "GAME MENU\n\nA/X: Resume\nB/Y: Restart\nStick Down: Exit";
        textMesh.fontSize = 32;
        textMesh.characterSize = 0.02f;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = Color.white;
    }
    
    private void CreateGameUI()
    {
        if (gameUIPanel != null || anchors == null || anchors.Count < 2) return;
        
        Transform primary = anchors[0].transform;
        Transform secondary = anchors[1].transform;
        
        Vector3 midpoint = (primary.position + secondary.position) / 2f;
        midpoint.y = 1.5f;
        
        Vector3 dir = (secondary.position - primary.position).normalized;
        dir.y = 0;
        
        gameUIPanel = new GameObject("PassthroughGameUI");
        gameUIPanel.transform.position = primary.position - dir * 3f;
        gameUIPanel.transform.position = new Vector3(
            gameUIPanel.transform.position.x, 1.5f, gameUIPanel.transform.position.z);
        gameUIPanel.transform.rotation = Quaternion.LookRotation(-dir);
        
        // Status text
        var statusGO = new GameObject("StatusText");
        statusGO.transform.SetParent(gameUIPanel.transform, false);
        statusText = statusGO.AddComponent<TextMesh>();
        statusText.fontSize = 48;
        statusText.characterSize = 0.015f;
        statusText.anchor = TextAnchor.MiddleCenter;
        statusText.color = Color.white;
        
        UpdateInstructions();
    }
    
    private void UpdateInstructions()
    {
        if (statusText == null) return;
        
        switch (currentPhase)
        {
            case GamePhase.TableAdjust:
                statusText.text = "TABLE ADJUST\n\nRight Stick: Rotate/Height\nB/Y: Toggle Rackets\nA/X: Confirm";
                break;
            case GamePhase.BallPosition:
                statusText.text = "BALL POSITION\n\nGRIP: Spawn Ball\nA/X + Stick: Move Ball\nHit ball to play!";
                break;
            case GamePhase.Playing:
                statusText.text = "PLAYING\n\nMENU: Pause";
                break;
        }
    }
    
    // ==================== NETWORK RPCs ====================
    
#if FUSION2
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestTableAdjust(float rotDelta, float heightDelta)
    {
        NetworkedTableYRotation += rotDelta;
        NetworkedTableHeight += heightDelta;
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestBallMove(Vector3 movement)
    {
        if (spawnedBall != null)
        {
            spawnedBall.transform.position += movement;
        }
    }
    
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestSpawnBall()
    {
        SpawnBall();
    }
    
    [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
    private void RPC_NotifyBallSpawned()
    {
        StartCoroutine(FindBallDelayed());
    }
    
    private IEnumerator FindBallDelayed()
    {
        yield return null;
        yield return null;
        
        var ball = FindObjectOfType<NetworkedBall>();
        if (ball != null)
        {
            spawnedBall = ball.gameObject;
        }
    }
#endif
}
