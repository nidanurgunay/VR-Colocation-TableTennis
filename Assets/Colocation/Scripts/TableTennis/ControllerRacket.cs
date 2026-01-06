using UnityEngine;
using Fusion;

/// <summary>
/// Attaches a racket visual to the controller when grip is pressed.
/// Controllers remain visible. Racket offset/rotation adjustable with thumbsticks when in adjust mode.
/// Also syncs racket position/visibility to other players via Fusion.
/// </summary>
public class ControllerRacket : NetworkBehaviour
{
    [Header("Racket Prefab (optional - will auto-find if not set)")]
    [SerializeField] private GameObject racketPrefab; // Racket model to show on controller
    
    [Header("Settings")]
    [SerializeField] private OVRInput.Button rightActivateButton = OVRInput.Button.Two; // B button for right controller
    [SerializeField] private OVRInput.Button leftActivateButton = OVRInput.Button.Two; // Y button for left controller
    [SerializeField] private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f); // Position offset from controller
    [SerializeField] private Vector3 racketRotation = new Vector3(-51f, 184f, 81f); // Default rotation for natural grip
    [SerializeField] private float racketScale = 10f; // 10x scale for visibility
    
    [Header("Adjustment Settings")]
    [SerializeField] private float rotationAdjustSpeed = 45f; // Degrees per second
    
    [Header("Controller Visibility")]
    [Tooltip("When enabled, Quest controllers remain visible alongside the rackets")]
    [SerializeField] private bool showControllersAlways = false; // Set to true to keep controllers visible alongside rackets
    
    [Header("Network Settings")]
    [SerializeField] private float networkUpdateRate = 0.05f; // 20 updates per second
    
    // Networked state for other players to see our racket
    [Networked] private Vector3 NetworkedRightRacketPos { get; set; }
    [Networked] private Quaternion NetworkedRightRacketRot { get; set; }
    [Networked] private NetworkBool NetworkedRightRacketVisible { get; set; }
    [Networked] private Vector3 NetworkedLeftRacketPos { get; set; }
    [Networked] private Quaternion NetworkedLeftRacketRot { get; set; }
    [Networked] private NetworkBool NetworkedLeftRacketVisible { get; set; }
    
    // Adjust mode - toggle with A button
    private bool isAdjustMode = false;
    
    // Controller references
    private Transform leftController;
    private Transform rightController;
    
    // Controller visual components (to hide when racket is shown)
    private GameObject leftControllerVisual;
    private GameObject rightControllerVisual;
    
    // Racket instances attached to controllers (local player)
    private GameObject leftRacket;
    private GameObject rightRacket;
    
    // Rigidbodies for velocity tracking
    private Rigidbody leftRacketRb;
    private Rigidbody rightRacketRb;
    private Vector3 lastLeftPos;
    private Vector3 lastRightPos;
    
    // Remote player racket representations
    private GameObject remoteLeftRacket;
    private GameObject remoteRightRacket;
    
    // Toggle states
    private bool leftActive = false;
    private bool rightActive = false;
    private bool leftWasPressed = false;
    private bool rightWasPressed = false;
    
    // Network sync timing
    private float lastNetworkUpdate = 0f;
    private bool isLocalPlayer = false;
    private bool isInitialized = false;
    private bool isNetworked = false; // True if spawned via Fusion, false if local-only
    
    /// <summary>
    /// Called when spawned via Fusion network
    /// </summary>
    public override void Spawned()
    {
        base.Spawned();
        isNetworked = true;
        isLocalPlayer = Object.HasInputAuthority;
        Debug.Log($"[ControllerRacket] Spawned via Fusion - IsLocalPlayer: {isLocalPlayer}");
        
        if (isLocalPlayer)
        {
            // Local player - setup controller tracking
            InitializeLocalPlayer();
        }
        else
        {
            // Remote player - create visual representations
            StartCoroutine(CreateRemoteRackets());
        }
    }
    
    /// <summary>
    /// Called for local-only (non-networked) instances
    /// </summary>
    private void Start()
    {
        // If already initialized via Spawned(), skip
        if (isInitialized) return;
        
        // Check if we have a NetworkObject - if not, this is a local-only instance
        if (Object == null || !Object.IsValid)
        {
            Debug.Log("[ControllerRacket] Starting as local-only (no network sync)");
            isNetworked = false;
            isLocalPlayer = true; // Local-only always controls itself
            InitializeLocalPlayer();
        }
    }
    
    private void InitializeLocalPlayer()
    {
        if (isInitialized) return;
        isInitialized = true;
        Debug.Log($"[RACKET_DEBUG] ControllerRacket Start - Rotation: {racketRotation}, Offset: {racketOffset}, Scale: {racketScale}");
        FindControllers();
        
        // Try to find racket prefab - if not found, will retry in Update
        TryFindRacketTemplate();
        
        // Create rackets attached to controllers (hidden initially)
        if (racketPrefab != null)
        {
            CreateControllerRackets();
            Debug.Log("[ControllerRacket] Initialized - press B/Y to show racket on controller");
        }
        else
        {
            Debug.LogWarning("[ControllerRacket] Racket not found yet - will retry...");
        }
    }
    
    private void TryFindRacketTemplate()
    {
        if (racketPrefab != null) return; // Already found
        
        // Method 1: Search by tag (active objects only)
        var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var r in taggedRackets)
        {
            // Skip our created copies
            if (r.name.Contains("Controller") || r.name.Contains("Remote")) continue;
            racketPrefab = r;
            Debug.Log($"[ControllerRacket] Found racket by tag: {racketPrefab.name}");
            return;
        }
        
        // Method 2: Search ALL objects including inactive using Resources
        var allObjects = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (var t in allObjects)
        {
            if (!t.gameObject.scene.isLoaded) continue; // Skip prefabs
            
            string nameLower = t.name.ToLower();
            if (nameLower.Contains("controller") || nameLower.Contains("remote")) continue;
            
            if ((nameLower == "racket" || nameLower == "racket2" || nameLower.Contains("paddle") || nameLower.Contains("bat"))
                && t.GetComponent<MeshFilter>() != null)
            {
                racketPrefab = t.gameObject;
                Debug.Log($"[ControllerRacket] Found racket by Resources search: {racketPrefab.name}");
                return;
            }
        }
    }
    
    private void FindRacketTemplate()
    {
        // Try to find by tag (only finds active objects)
        var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
        if (taggedRackets.Length > 0)
        {
            racketPrefab = taggedRackets[0];
            Debug.Log($"[ControllerRacket] Found racket by tag: {racketPrefab.name}");
            return;
        }
        
        // Try to find by name under pingpong parent (including inactive!)
        var pingPongParent = GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("PingPongTable");
        if (pingPongParent != null)
        {
            foreach (Transform child in pingPongParent.GetComponentsInChildren<Transform>(true)) // true = include inactive
            {
                string nameLower = child.name.ToLower();
                if (nameLower.Contains("racket") || nameLower.Contains("paddle") || nameLower.Contains("bat"))
                {
                    racketPrefab = child.gameObject;
                    Debug.Log($"[ControllerRacket] Found racket by name (may be inactive): {racketPrefab.name}");
                    return;
                }
            }
        }
        
        Debug.LogWarning("[ControllerRacket] Could not find racket template in scene!");
    }
    
    private void FindControllers()
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>(true);
        if (cameraRig != null)
        {
            leftController = cameraRig.leftControllerAnchor;
            rightController = cameraRig.rightControllerAnchor;
            Debug.Log($"[ControllerRacket] Found controllers - Left: {leftController != null}, Right: {rightController != null}");
            
            // Find controller visuals (OVRControllerHelper or child renderers)
            if (leftController != null)
            {
                leftControllerVisual = FindControllerVisual(leftController);
            }
            if (rightController != null)
            {
                rightControllerVisual = FindControllerVisual(rightController);
            }
        }
        else
        {
            Debug.LogWarning("[ControllerRacket] OVRCameraRig not found!");
        }
    }
    
    private GameObject FindControllerVisual(Transform controllerAnchor)
    {
        // Try to find OVRControllerHelper component
        var controllerHelper = controllerAnchor.GetComponentInChildren<OVRControllerHelper>(true);
        if (controllerHelper != null)
        {
            Debug.Log($"[ControllerRacket] Found OVRControllerHelper on {controllerAnchor.name}");
            return controllerHelper.gameObject;
        }
        
        // Try to find by common names
        foreach (Transform child in controllerAnchor.GetComponentsInChildren<Transform>(true))
        {
            string nameLower = child.name.ToLower();
            if (nameLower.Contains("controller") || nameLower.Contains("model") || nameLower.Contains("visual"))
            {
                if (child.GetComponent<Renderer>() != null || child.GetComponentInChildren<Renderer>() != null)
                {
                    Debug.Log($"[ControllerRacket] Found controller visual: {child.name}");
                    return child.gameObject;
                }
            }
        }
        
        return null;
    }
    
    private void CreateControllerRackets()
    {
        if (racketPrefab == null)
        {
            Debug.LogError("[ControllerRacket] Racket prefab not assigned and couldn't be found!");
            return;
        }
        
        // Hide the original racket(s) on the table
        racketPrefab.SetActive(false);
        
        // Also hide any other rackets in the scene
        var allRackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var r in allRackets)
        {
            r.SetActive(false);
        }
        
        // Find and hide rackets by name too
        var pingPongParent = GameObject.Find("pingpong") ?? GameObject.Find("PingPong");
        if (pingPongParent != null)
        {
            foreach (Transform child in pingPongParent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name.ToLower().Contains("racket") || child.name.ToLower().Contains("paddle"))
                {
                    if (child.gameObject != leftRacket && child.gameObject != rightRacket)
                    {
                        child.gameObject.SetActive(false);
                        Debug.Log($"[ControllerRacket] Hiding scene racket: {child.name}");
                    }
                }
            }
        }
        
        // Now create the controller rackets (only once, after hiding scene rackets)
        CreateRacketOnController();
    }
    
    private void CreateRacketOnController()
    {
        Debug.Log($"[RACKET_DEBUG] Prefab original rotation: {racketPrefab.transform.eulerAngles}");
        
        // Create left controller racket
        if (leftController != null && leftRacket == null)
        {
            // Instantiate without parent first to avoid inheriting rotations
            leftRacket = Instantiate(racketPrefab);
            leftRacket.name = "LeftControllerRacket";
            
            // Reset to identity, then set parent
            leftRacket.transform.SetParent(leftController, false);
            leftRacket.transform.localPosition = racketOffset;
            leftRacket.transform.localRotation = Quaternion.identity; // Reset first
            leftRacket.transform.localRotation = Quaternion.Euler(racketRotation); // Then apply our rotation
            leftRacket.transform.localScale = Vector3.one * racketScale;
            leftRacket.SetActive(false); // Hidden until activated
            
            Debug.Log($"[RACKET_DEBUG] Left racket created with rotation: {leftRacket.transform.localEulerAngles}");
            
            // Remove any physics/grab components
            CleanupRacketComponents(leftRacket);
            leftRacketRb = leftRacket.GetComponent<Rigidbody>();
        }
        
        // Create right controller racket
        if (rightController != null && rightRacket == null)
        {
            // Instantiate without parent first to avoid inheriting rotations
            rightRacket = Instantiate(racketPrefab);
            rightRacket.name = "RightControllerRacket";
            
            // Reset to identity, then set parent
            rightRacket.transform.SetParent(rightController, false);
            rightRacket.transform.localPosition = racketOffset;
            rightRacket.transform.localRotation = Quaternion.identity; // Reset first
            rightRacket.transform.localRotation = Quaternion.Euler(racketRotation); // Then apply our rotation
            rightRacket.transform.localScale = Vector3.one * racketScale;
            rightRacket.SetActive(false); // Hidden until activated
            
            Debug.Log($"[RACKET_DEBUG] Right racket created with rotation: {rightRacket.transform.localEulerAngles}");
            
            // Remove any physics/grab components
            CleanupRacketComponents(rightRacket);
            rightRacketRb = rightRacket.GetComponent<Rigidbody>();
        }
        
        Debug.Log("[ControllerRacket] Created racket visuals on controllers (press B/Y to show)");
    }
    
    private void CleanupRacketComponents(GameObject racket)
    {
        // Remove existing rigidbody - we'll add a kinematic one for velocity tracking
        var rb = racket.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        
        // Add a kinematic rigidbody for velocity tracking
        rb = racket.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        
        // Set tag for collision detection
        racket.tag = "Racket";
        
        // Ensure there's a collider
        var existingCollider = racket.GetComponent<Collider>();
        if (existingCollider == null)
        {
            // Add a box collider if none exists
            var boxCollider = racket.AddComponent<BoxCollider>();
            boxCollider.size = new Vector3(0.15f, 0.01f, 0.15f); // Flat paddle shape
            boxCollider.isTrigger = false; // Use collision, not trigger
            Debug.Log("[ControllerRacket] Added box collider to racket");
        }
        else
        {
            // Make sure existing collider is not a trigger
            existingCollider.isTrigger = false;
        }
        
        // Also tag all child objects that have colliders
        foreach (var childCollider in racket.GetComponentsInChildren<Collider>())
        {
            childCollider.gameObject.tag = "Racket";
            childCollider.isTrigger = false;
        }
        
        Debug.Log($"[ControllerRacket] Racket setup complete: {racket.name}, tag={racket.tag}");
    }
    
    private void FixedUpdate()
    {
        // Update racket velocities for collision detection
        if (leftRacketRb != null && leftRacket != null && leftRacket.activeSelf)
        {
            Vector3 currentPos = leftRacket.transform.position;
            leftRacketRb.velocity = (currentPos - lastLeftPos) / Time.fixedDeltaTime;
            lastLeftPos = currentPos;
        }
        
        if (rightRacketRb != null && rightRacket != null && rightRacket.activeSelf)
        {
            Vector3 currentPos = rightRacket.transform.position;
            rightRacketRb.velocity = (currentPos - lastRightPos) / Time.fixedDeltaTime;
            lastRightPos = currentPos;
        }
    }
    
    private void Update()
    {
        // Only process input for local player
        if (!isLocalPlayer) return;
        
        // Retry finding racket if not found in Start
        if (racketPrefab == null)
        {
            TryFindRacketTemplate();
            if (racketPrefab != null)
            {
                CreateControllerRackets();
                Debug.Log("[ControllerRacket] Racket found on retry - initialized!");
            }
            return; // Don't process input until we have rackets
        }
        
        if (leftController == null || rightController == null)
        {
            FindControllers();
            return;
        }
        
        // Toggle adjust mode with A button (Button.One on right controller)
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            isAdjustMode = !isAdjustMode;
            Debug.Log($"[ControllerRacket] Adjust mode: {(isAdjustMode ? "ON - Use thumbsticks to adjust racket" : "OFF")}");
            if (isAdjustMode)
            {
                Debug.Log("[ControllerRacket] Left stick: Position (X/Y), Right stick X: Rotation Z, Right stick Y: Rotation X");
            }
        }
        
        // Handle racket adjustment when in adjust mode
        if (isAdjustMode)
        {
            HandleRacketAdjustment();
        }
        
        // Check for toggle on left controller (Y button)
        bool leftPressed = OVRInput.Get(leftActivateButton, OVRInput.Controller.LTouch);
        if (leftPressed && !leftWasPressed)
        {
            leftActive = !leftActive;
            if (leftRacket != null)
            {
                leftRacket.SetActive(leftActive);
                // Keep controller visible if showControllersAlways is true
                if (!showControllersAlways)
                {
                    SetControllerVisualActive(leftControllerVisual, !leftActive);
                }
                Debug.Log($"[RACKET_DEBUG] Left racket: {(leftActive ? "SHOWN" : "HIDDEN")}");
            }
        }
        leftWasPressed = leftPressed;
        
        // Check for toggle on right controller (B button)
        bool rightPressed = OVRInput.Get(rightActivateButton, OVRInput.Controller.RTouch);
        if (rightPressed && !rightWasPressed)
        {
            rightActive = !rightActive;
            if (rightRacket != null)
            {
                rightRacket.SetActive(rightActive);
                // Keep controller visible if showControllersAlways is true
                if (!showControllersAlways)
                {
                    SetControllerVisualActive(rightControllerVisual, !rightActive);
                }
                Debug.Log($"[RACKET_DEBUG] Right racket: {(rightActive ? "SHOWN" : "HIDDEN")}");
            }
        }
        rightWasPressed = rightPressed;
    }
    
    /// <summary>
    /// Handle racket position/rotation adjustment using thumbsticks
    /// </summary>
    private void HandleRacketAdjustment()
    {
        Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
        Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        
        bool changed = false;
        
        // Left stick X: Adjust rotation Y (yaw)
        if (Mathf.Abs(leftStick.x) > 0.1f)
        {
            racketRotation.y += leftStick.x * rotationAdjustSpeed * Time.deltaTime;
            changed = true;
        }
        
        // Right stick X: Adjust rotation Z (roll)
        if (Mathf.Abs(rightStick.x) > 0.1f)
        {
            racketRotation.z += rightStick.x * rotationAdjustSpeed * Time.deltaTime;
            changed = true;
        }
        
        // Apply changes to both rackets
        if (changed)
        {
            if (leftRacket != null)
            {
                leftRacket.transform.localPosition = racketOffset;
                leftRacket.transform.localRotation = Quaternion.Euler(racketRotation);
            }
            if (rightRacket != null)
            {
                rightRacket.transform.localPosition = racketOffset;
                rightRacket.transform.localRotation = Quaternion.Euler(racketRotation);
            }
            
            // Log current values periodically
            if (Time.frameCount % 30 == 0)
            {
                Debug.Log($"[ControllerRacket] Rotation: X={racketRotation.x:F1}, Y={racketRotation.y:F1}, Z={racketRotation.z:F1}");
            }
        }
    }
    
    private void SetControllerVisualActive(GameObject controllerVisual, bool active)
    {
        if (controllerVisual != null)
        {
            // Disable all renderers in the controller visual hierarchy
            var renderers = controllerVisual.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                renderer.enabled = active;
            }
            
            // Also try disabling the OVRControllerHelper component itself
            var controllerHelper = controllerVisual.GetComponent<OVRControllerHelper>();
            if (controllerHelper != null)
            {
                controllerHelper.enabled = active;
            }
            
            Debug.Log($"[RACKET_DEBUG] Controller visual {(active ? "SHOWN" : "HIDDEN")} - disabled {renderers.Length} renderers");
        }
        
        // Disable ray/pointer visuals
        DisableRayVisuals(!active);
    }
    
    private void DisableRayVisuals(bool hide)
    {
        // Find and disable common ray/pointer components
        var cameraRig = FindObjectOfType<OVRCameraRig>(true);
        if (cameraRig == null) return;
        
        // Try to find OVRRayHelper components
        var rayHelpers = cameraRig.GetComponentsInChildren<OVRRayHelper>(true);
        foreach (var rayHelper in rayHelpers)
        {
            rayHelper.enabled = !hide;
        }
        
        // Try to find LineRenderer components (commonly used for rays)
        var lineRenderers = cameraRig.GetComponentsInChildren<LineRenderer>(true);
        foreach (var lr in lineRenderers)
        {
            lr.enabled = !hide;
        }
        
        // Try to find UIRaycastr or similar pointer components by name
        foreach (Transform child in cameraRig.GetComponentsInChildren<Transform>(true))
        {
            string nameLower = child.name.ToLower();
            if (nameLower.Contains("ray") || nameLower.Contains("pointer") || nameLower.Contains("laser"))
            {
                var childRenderers = child.GetComponentsInChildren<Renderer>(true);
                foreach (var r in childRenderers)
                {
                    r.enabled = !hide;
                }
            }
        }
        
        if (hide)
        {
            Debug.Log("[RACKET_DEBUG] Disabled ray/pointer visuals");
        }
    }
    
    /// <summary>
    /// Check if a controller has an active racket (for ball collision)
    /// </summary>
    public bool IsRacketActive(OVRInput.Controller controller)
    {
        if (controller == OVRInput.Controller.LTouch) return leftActive;
        if (controller == OVRInput.Controller.RTouch) return rightActive;
        return false;
    }
    
    /// <summary>
    /// Get the racket GameObject for a controller
    /// </summary>
    public GameObject GetRacket(OVRInput.Controller controller)
    {
        if (controller == OVRInput.Controller.LTouch) return leftRacket;
        if (controller == OVRInput.Controller.RTouch) return rightRacket;
        return null;
    }
    
    /// <summary>
    /// Create visual representations of the remote player's rackets
    /// </summary>
    private System.Collections.IEnumerator CreateRemoteRackets()
    {
        // Wait for racket template to be available
        while (racketPrefab == null)
        {
            TryFindRacketTemplate();
            yield return new WaitForSeconds(0.2f);
        }
        
        // Create remote racket visuals (not parented to controllers)
        remoteRightRacket = Instantiate(racketPrefab);
        remoteRightRacket.name = "RemoteRightRacket";
        remoteRightRacket.transform.localScale = Vector3.one * racketScale;
        remoteRightRacket.SetActive(false);
        CleanupRacketComponents(remoteRightRacket);
        
        remoteLeftRacket = Instantiate(racketPrefab);
        remoteLeftRacket.name = "RemoteLeftRacket";
        remoteLeftRacket.transform.localScale = Vector3.one * racketScale;
        remoteLeftRacket.SetActive(false);
        CleanupRacketComponents(remoteLeftRacket);
        
        Debug.Log("[ControllerRacket] Created remote player racket visuals");
    }
    
    /// <summary>
    /// Sync local racket state to network (called in FixedUpdateNetwork)
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        if (!isLocalPlayer) return;
        
        // Sync right racket
        if (rightRacket != null && rightActive)
        {
            NetworkedRightRacketPos = rightRacket.transform.position;
            NetworkedRightRacketRot = rightRacket.transform.rotation;
            NetworkedRightRacketVisible = true;
        }
        else
        {
            NetworkedRightRacketVisible = false;
        }
        
        // Sync left racket
        if (leftRacket != null && leftActive)
        {
            NetworkedLeftRacketPos = leftRacket.transform.position;
            NetworkedLeftRacketRot = leftRacket.transform.rotation;
            NetworkedLeftRacketVisible = true;
        }
        else
        {
            NetworkedLeftRacketVisible = false;
        }
    }
    
    /// <summary>
    /// Update remote racket positions (called in Render for smooth interpolation)
    /// </summary>
    public override void Render()
    {
        if (isLocalPlayer) return;
        
        // Update remote right racket
        if (remoteRightRacket != null)
        {
            remoteRightRacket.SetActive(NetworkedRightRacketVisible);
            if (NetworkedRightRacketVisible)
            {
                remoteRightRacket.transform.position = Vector3.Lerp(
                    remoteRightRacket.transform.position, 
                    NetworkedRightRacketPos, 
                    Time.deltaTime * 20f);
                remoteRightRacket.transform.rotation = Quaternion.Slerp(
                    remoteRightRacket.transform.rotation, 
                    NetworkedRightRacketRot, 
                    Time.deltaTime * 20f);
            }
        }
        
        // Update remote left racket
        if (remoteLeftRacket != null)
        {
            remoteLeftRacket.SetActive(NetworkedLeftRacketVisible);
            if (NetworkedLeftRacketVisible)
            {
                remoteLeftRacket.transform.position = Vector3.Lerp(
                    remoteLeftRacket.transform.position, 
                    NetworkedLeftRacketPos, 
                    Time.deltaTime * 20f);
                remoteLeftRacket.transform.rotation = Quaternion.Slerp(
                    remoteLeftRacket.transform.rotation, 
                    NetworkedLeftRacketRot, 
                    Time.deltaTime * 20f);
            }
        }
    }
}
