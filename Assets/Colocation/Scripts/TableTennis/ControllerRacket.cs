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
    [SerializeField] private Vector3 racketRotation = new Vector3(-51f, 189.0f, 77.4f); // Default rotation for natural grip
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
    
    // Public getter for racket velocity (used for ball hit detection)
    public Vector3 GetRacketVelocity(GameObject racketObject)
    {
        if (racketObject == leftRacket && leftRacketRb != null)
            return leftRacketRb.velocity;
        if (racketObject == rightRacket && rightRacketRb != null)
            return rightRacketRb.velocity;
        // Fallback: try to find rigidbody on the object
        var rb = racketObject.GetComponent<Rigidbody>();
        if (rb != null)
            return rb.velocity;
        return Vector3.zero;
    }
    
    // Remote player racket representations
    private GameObject remoteLeftRacket;
    private GameObject remoteRightRacket;
    
    // Rigidbodies for remote rackets (for velocity tracking)
    private Rigidbody remoteLeftRacketRb;
    private Rigidbody remoteRightRacketRb;
    private Vector3 lastRemoteLeftPos;
    private Vector3 lastRemoteRightPos;
    
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
    
    // Table reference for coordinate conversion (needed for proper sync after alignment)
    private Transform tableTransform;
    
    /// <summary>
    /// Called when spawned via Fusion network
    /// </summary>
    public override void Spawned()
    {
        base.Spawned();
        isNetworked = true;
        isLocalPlayer = Object.HasInputAuthority;
        Debug.Log($"[ControllerRacket][SPAWN] Spawned via Fusion - IsLocalPlayer: {isLocalPlayer}, HasStateAuthority: {Object.HasStateAuthority}, HasInputAuthority: {Object.HasInputAuthority}, InputAuthority: {Object.InputAuthority}, LocalPlayer: {Runner.LocalPlayer}");
        
        if (isLocalPlayer)
        {
            // Request State Authority so we can modify networked properties
            // Each player needs state authority over their own racket to sync position/visibility
            if (!Object.HasStateAuthority)
            {
                Object.RequestStateAuthority();
                Debug.Log("[ControllerRacket][SPAWN] Requested State Authority for local player's racket");
            }
            
            // Local player - setup controller tracking
            InitializeLocalPlayer();
        }
        else
        {
            // Remote player - create visual representations
            Debug.Log("[ControllerRacket][SPAWN] This is REMOTE player's racket - creating remote visuals...");
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
        FindTableReference();
        
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
    
    private void FindTableReference()
    {
        if (tableTransform != null) return;
        
        var table = GameObject.Find("PingPongTable") ?? GameObject.Find("pingpongtable") 
                    ?? GameObject.Find("pingpong") ?? GameObject.Find("PingPong");
        if (table != null)
        {
            tableTransform = table.transform;
            Debug.Log($"[ControllerRacket] Found table reference: {table.name} at {tableTransform.position}");
        }
        else
        {
            Debug.LogWarning("[ControllerRacket] Could not find table for coordinate conversion!");
        }
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
        
        // Hide the original racket template only
        racketPrefab.SetActive(false);
        
        // Find and hide ONLY the original scene template rackets (under pingpong parent)
        // Do NOT hide any rackets that were dynamically instantiated (controller/remote)
        var pingPongParent = GameObject.Find("pingpong") ?? GameObject.Find("PingPong") ?? GameObject.Find("PingPongTable");
        if (pingPongParent != null)
        {
            foreach (Transform child in pingPongParent.GetComponentsInChildren<Transform>(true))
            {
                string nameLower = child.name.ToLower();
                if (nameLower.Contains("racket") || nameLower.Contains("paddle"))
                {
                    // Only hide if it's a scene object (not instantiated by us)
                    // Our instantiated rackets have specific names
                    if (!nameLower.Contains("controller") && !nameLower.Contains("remote") &&
                        child.gameObject != leftRacket && child.gameObject != rightRacket)
                    {
                        child.gameObject.SetActive(false);
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
        // Remove existing rigidbody - we'll add one for velocity tracking
        var rb = racket.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        
        // Add a rigidbody for velocity tracking and collision detection
        rb = racket.AddComponent<Rigidbody>();
        // For local rackets: kinematic (we control position)
        // For remote rackets: non-kinematic (needs to collide with ball)
        // We'll set this based on racket name after creation
        rb.isKinematic = true; // Default, will be changed for remote rackets
        rb.useGravity = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // Better collision detection
        rb.interpolation = RigidbodyInterpolation.Interpolate; // Smooth movement
        
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
            // If we just found controllers and have racketPrefab but no rackets, create them
            if (leftController != null && rightController != null && racketPrefab != null)
            {
                if (leftRacket == null || rightRacket == null)
                {
                    Debug.Log("[ControllerRacket] Controllers found - creating rackets now");
                    CreateRacketOnController();
                }
            }
            return;
        }
        
        // Ensure rackets are created (they might not exist if controllers were found late)
        if ((leftRacket == null || rightRacket == null) && racketPrefab != null)
        {
            Debug.Log("[ControllerRacket] Rackets missing but have prefab - creating now");
            CreateRacketOnController();
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
        
        // Y button (left controller) = toggle LEFT racket (deactivates right)
        // B button (right controller) = toggle RIGHT racket (deactivates left)
        // Only ONE racket can be active at a time
        bool leftPressed = OVRInput.Get(leftActivateButton, OVRInput.Controller.LTouch);
        bool rightPressed = OVRInput.Get(rightActivateButton, OVRInput.Controller.RTouch);
        
        // Toggle LEFT racket with Y button (and deactivate right)
        if (leftPressed && !leftWasPressed)
        {
            Debug.Log($"[RACKET_DEBUG] Y button pressed! leftRacket={leftRacket}, leftController={leftController}");
            
            leftActive = !leftActive;
            
            // If activating left, deactivate right
            if (leftActive)
            {
                rightActive = false;
                if (rightRacket != null)
                {
                    rightRacket.SetActive(false);
                    if (!showControllersAlways)
                    {
                        SetControllerVisualActive(rightControllerVisual, true, rightController);
                    }
                }
            }
            
            if (leftRacket != null)
            {
                leftRacket.SetActive(leftActive);
                if (!showControllersAlways)
                {
                    SetControllerVisualActive(leftControllerVisual, !leftActive, leftController);
                }
                Debug.Log($"[RACKET_DEBUG] Left racket: {(leftActive ? "SHOWN" : "HIDDEN")}, position={leftRacket.transform.position}");
            }
            else
            {
                Debug.LogError($"[RACKET_DEBUG] leftRacket is NULL! Trying to create now...");
                // Try to create rackets if they weren't created before
                if (racketPrefab != null && leftController != null)
                {
                    CreateRacketOnController();
                    if (leftRacket != null)
                    {
                        leftRacket.SetActive(leftActive);
                        Debug.Log($"[RACKET_DEBUG] Left racket created on demand and set to: {leftActive}");
                    }
                }
                else
                {
                    Debug.LogError($"[RACKET_DEBUG] Cannot create: racketPrefab={racketPrefab}, leftController={leftController}");
                }
            }
        }
        
        // Toggle RIGHT racket with B button (and deactivate left)
        if (rightPressed && !rightWasPressed)
        {
            Debug.Log($"[RACKET_DEBUG] B button pressed! rightRacket={rightRacket}, rightController={rightController}");
            
            rightActive = !rightActive;
            
            // If activating right, deactivate left
            if (rightActive)
            {
                leftActive = false;
                if (leftRacket != null)
                {
                    leftRacket.SetActive(false);
                    if (!showControllersAlways)
                    {
                        SetControllerVisualActive(leftControllerVisual, true, leftController);
                    }
                }
            }
            
            if (rightRacket != null)
            {
                rightRacket.SetActive(rightActive);
                if (!showControllersAlways)
                {
                    SetControllerVisualActive(rightControllerVisual, !rightActive, rightController);
                }
                Debug.Log($"[RACKET_DEBUG] Right racket: {(rightActive ? "SHOWN" : "HIDDEN")}, position={rightRacket.transform.position}");
            }
            else
            {
                Debug.LogError($"[RACKET_DEBUG] rightRacket is NULL! Trying to create now...");
                // Try to create rackets if they weren't created before
                if (racketPrefab != null && rightController != null)
                {
                    CreateRacketOnController();
                    if (rightRacket != null)
                    {
                        rightRacket.SetActive(rightActive);
                        Debug.Log($"[RACKET_DEBUG] Right racket created on demand and set to: {rightActive}");
                    }
                }
                else
                {
                    Debug.LogError($"[RACKET_DEBUG] Cannot create: racketPrefab={racketPrefab}, rightController={rightController}");
                }
            }
        }
        
        leftWasPressed = leftPressed;
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
    
    private void SetControllerVisualActive(GameObject controllerVisual, bool active, Transform controllerAnchor = null)
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
        
        // Disable ray/pointer visuals for this specific controller
        if (controllerAnchor != null)
        {
            SetRayVisuals(controllerAnchor, active);
        }
    }
    
    private void SetRayVisuals(Transform controllerAnchor, bool show)
    {
        if (controllerAnchor == null) return;
        
        // Disable ray/pointer components on this specific controller
        var rayHelpers = controllerAnchor.GetComponentsInChildren<OVRRayHelper>(true);
        foreach (var rayHelper in rayHelpers)
        {
            rayHelper.enabled = show;
            Debug.Log($"[RACKET_DEBUG] OVRRayHelper on {controllerAnchor.name}: {(show ? "enabled" : "disabled")}");
        }
        
        // Disable LineRenderer components (commonly used for rays)
        var lineRenderers = controllerAnchor.GetComponentsInChildren<LineRenderer>(true);
        foreach (var lr in lineRenderers)
        {
            lr.enabled = show;
            Debug.Log($"[RACKET_DEBUG] LineRenderer on {controllerAnchor.name}: {(show ? "enabled" : "disabled")}");
        }
        
        // Find ray/pointer objects by name and disable their renderers
        foreach (Transform child in controllerAnchor.GetComponentsInChildren<Transform>(true))
        {
            string nameLower = child.name.ToLower();
            if (nameLower.Contains("ray") || nameLower.Contains("pointer") || nameLower.Contains("laser") || nameLower.Contains("beam"))
            {
                // Disable the gameobject itself
                child.gameObject.SetActive(show);
                Debug.Log($"[RACKET_DEBUG] Ray object '{child.name}': {(show ? "enabled" : "disabled")}");
            }
        }
        
        Debug.Log($"[RACKET_DEBUG] Ray visuals for {controllerAnchor.name}: {(show ? "SHOWN" : "HIDDEN")}");
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
        Debug.Log("[ControllerRacket][REMOTE] Starting to create remote rackets...");
        
        int attempts = 0;
        // Wait for racket template to be available
        while (racketPrefab == null && attempts < 50)
        {
            TryFindRacketTemplate();
            attempts++;
            yield return new WaitForSeconds(0.2f);
        }
        
        if (racketPrefab == null)
        {
            Debug.LogError("[ControllerRacket][REMOTE] Failed to find racket template after 50 attempts!");
            yield break;
        }
        
        Debug.Log($"[ControllerRacket][REMOTE] Found racket template: {racketPrefab.name}, creating remote racket visuals...");
        
        // Create remote racket visuals (not parented to controllers)
        remoteRightRacket = Instantiate(racketPrefab);
        remoteRightRacket.name = "RemoteRightRacket";
        remoteRightRacket.transform.localScale = Vector3.one * racketScale;
        remoteRightRacket.SetActive(false);
        CleanupRacketComponents(remoteRightRacket);
        remoteRightRacketRb = remoteRightRacket.GetComponent<Rigidbody>();
        // Make non-kinematic so it can collide with ball (ball physics runs on host)
        if (remoteRightRacketRb != null)
        {
            remoteRightRacketRb.isKinematic = false;
            Debug.Log("[ControllerRacket][REMOTE] Remote right racket rigidbody set to non-kinematic for collisions");
        }
        
        remoteLeftRacket = Instantiate(racketPrefab);
        remoteLeftRacket.name = "RemoteLeftRacket";
        remoteLeftRacket.transform.localScale = Vector3.one * racketScale;
        remoteLeftRacket.SetActive(false);
        CleanupRacketComponents(remoteLeftRacket);
        remoteLeftRacketRb = remoteLeftRacket.GetComponent<Rigidbody>();
        // Make non-kinematic so it can collide with ball (ball physics runs on host)
        if (remoteLeftRacketRb != null)
        {
            remoteLeftRacketRb.isKinematic = false;
            Debug.Log("[ControllerRacket][REMOTE] Remote left racket rigidbody set to non-kinematic for collisions");
        }
        
        Debug.Log("[ControllerRacket][REMOTE] Created remote player racket visuals successfully!");
    }
    
    /// <summary>
    /// Sync local racket state to network (called in FixedUpdateNetwork)
    /// </summary>
    public override void FixedUpdateNetwork()
    {
        if (!isLocalPlayer) return;
        
        // Need State Authority to modify networked properties
        if (!Object.HasStateAuthority)
        {
            // Keep requesting until we get it
            if (Time.frameCount % 100 == 0) // Log occasionally
            {
                Debug.Log("[ControllerRacket][SYNC] Still waiting for State Authority...");
            }
            Object.RequestStateAuthority();
            return;
        }
        
        // Find table if not found yet
        if (tableTransform == null)
        {
            FindTableReference();
        }
        
        // Sync right racket - use table-relative coordinates for proper alignment
        if (rightRacket != null && rightActive)
        {
            // Convert world position to table-relative for sync
            if (tableTransform != null)
            {
                NetworkedRightRacketPos = tableTransform.InverseTransformPoint(rightRacket.transform.position);
                // Store rotation relative to table
                NetworkedRightRacketRot = Quaternion.Inverse(tableTransform.rotation) * rightRacket.transform.rotation;
            }
            else
            {
                NetworkedRightRacketPos = rightRacket.transform.position;
                NetworkedRightRacketRot = rightRacket.transform.rotation;
            }
            NetworkedRightRacketVisible = true;
            
            // Log occasionally
            if (Time.frameCount % 300 == 0)
            {
                Debug.Log($"[ControllerRacket][SYNC] Syncing RIGHT racket: tableRelPos={NetworkedRightRacketPos}, worldPos={rightRacket.transform.position}");
            }
        }
        else
        {
            if (NetworkedRightRacketVisible) // Log when visibility changes
            {
                Debug.Log("[ControllerRacket][SYNC] RIGHT racket now hidden");
            }
            NetworkedRightRacketVisible = false;
        }
        
        // Sync left racket - use table-relative coordinates for proper alignment
        if (leftRacket != null && leftActive)
        {
            // Convert world position to table-relative for sync
            if (tableTransform != null)
            {
                NetworkedLeftRacketPos = tableTransform.InverseTransformPoint(leftRacket.transform.position);
                // Store rotation relative to table
                NetworkedLeftRacketRot = Quaternion.Inverse(tableTransform.rotation) * leftRacket.transform.rotation;
            }
            else
            {
                NetworkedLeftRacketPos = leftRacket.transform.position;
                NetworkedLeftRacketRot = leftRacket.transform.rotation;
            }
            NetworkedLeftRacketVisible = true;
            
            // Log occasionally
            if (Time.frameCount % 300 == 0)
            {
                Debug.Log($"[ControllerRacket][SYNC] Syncing LEFT racket: tableRelPos={NetworkedLeftRacketPos}, worldPos={leftRacket.transform.position}");
            }
        }
        else
        {
            if (NetworkedLeftRacketVisible) // Log when visibility changes
            {
                Debug.Log("[ControllerRacket][SYNC] LEFT racket now hidden");
            }
            NetworkedLeftRacketVisible = false;
        }
    }
    
    /// <summary>
    /// Update remote racket positions (called in Render for smooth interpolation)
    /// </summary>
    public override void Render()
    {
        if (isLocalPlayer) return;
        
        // Find table if not found yet
        if (tableTransform == null)
        {
            FindTableReference();
        }
        
        // Debug log every few seconds to see what's happening
        if (Time.frameCount % 180 == 0)
        {
            Debug.Log($"[ControllerRacket][REMOTE_RENDER] Right: visible={NetworkedRightRacketVisible}, tableRelPos={NetworkedRightRacketPos}, remoteRacket={remoteRightRacket != null}, table={tableTransform != null}");
            Debug.Log($"[ControllerRacket][REMOTE_RENDER] Left: visible={NetworkedLeftRacketVisible}, tableRelPos={NetworkedLeftRacketPos}, remoteRacket={remoteLeftRacket != null}");
        }
        
        // Update remote right racket
        if (remoteRightRacket != null)
        {
            bool shouldBeVisible = NetworkedRightRacketVisible;
            if (remoteRightRacket.activeSelf != shouldBeVisible)
            {
                remoteRightRacket.SetActive(shouldBeVisible);
                Debug.Log($"[ControllerRacket][REMOTE] Right racket visibility changed to: {shouldBeVisible}");
            }
            
            if (shouldBeVisible)
            {
                // Convert table-relative position back to world position
                Vector3 targetPos;
                Quaternion targetRot;
                if (tableTransform != null)
                {
                    targetPos = tableTransform.TransformPoint(NetworkedRightRacketPos);
                    targetRot = tableTransform.rotation * NetworkedRightRacketRot;
                }
                else
                {
                    targetPos = NetworkedRightRacketPos;
                    targetRot = NetworkedRightRacketRot;
                }
                
                // Use Rigidbody.MovePosition for physics-based movement (allows collisions)
                if (remoteRightRacketRb != null)
                {
                    remoteRightRacketRb.MovePosition(Vector3.Lerp(
                        remoteRightRacket.transform.position, 
                        targetPos, 
                        Time.deltaTime * 20f));
                    remoteRightRacketRb.MoveRotation(Quaternion.Slerp(
                        remoteRightRacket.transform.rotation, 
                        targetRot, 
                        Time.deltaTime * 20f));
                    
                    // Manually calculate velocity for collision detection
                    remoteRightRacketRb.velocity = (targetPos - lastRemoteRightPos) / Time.deltaTime;
                    lastRemoteRightPos = remoteRightRacket.transform.position;
                }
                else
                {
                    // Fallback if no rigidbody
                    remoteRightRacket.transform.position = Vector3.Lerp(
                        remoteRightRacket.transform.position, 
                        targetPos, 
                        Time.deltaTime * 20f);
                    remoteRightRacket.transform.rotation = Quaternion.Slerp(
                        remoteRightRacket.transform.rotation, 
                        targetRot, 
                        Time.deltaTime * 20f);
                }
            }
        }
        
        // Update remote left racket
        if (remoteLeftRacket != null)
        {
            bool shouldBeVisible = NetworkedLeftRacketVisible;
            if (remoteLeftRacket.activeSelf != shouldBeVisible)
            {
                remoteLeftRacket.SetActive(shouldBeVisible);
                Debug.Log($"[ControllerRacket][REMOTE] Left racket visibility changed to: {shouldBeVisible}");
            }
            
            if (shouldBeVisible)
            {
                // Convert table-relative position back to world position
                Vector3 targetPos;
                Quaternion targetRot;
                if (tableTransform != null)
                {
                    targetPos = tableTransform.TransformPoint(NetworkedLeftRacketPos);
                    targetRot = tableTransform.rotation * NetworkedLeftRacketRot;
                }
                else
                {
                    targetPos = NetworkedLeftRacketPos;
                    targetRot = NetworkedLeftRacketRot;
                }
                
                // Use Rigidbody.MovePosition for physics-based movement (allows collisions)
                if (remoteLeftRacketRb != null)
                {
                    remoteLeftRacketRb.MovePosition(Vector3.Lerp(
                        remoteLeftRacket.transform.position, 
                        targetPos, 
                        Time.deltaTime * 20f));
                    remoteLeftRacketRb.MoveRotation(Quaternion.Slerp(
                        remoteLeftRacket.transform.rotation, 
                        targetRot, 
                        Time.deltaTime * 20f));
                    
                    // Manually calculate velocity for collision detection
                    remoteLeftRacketRb.velocity = (targetPos - lastRemoteLeftPos) / Time.deltaTime;
                    lastRemoteLeftPos = remoteLeftRacket.transform.position;
                }
                else
                {
                    // Fallback if no rigidbody
                    remoteLeftRacket.transform.position = Vector3.Lerp(
                        remoteLeftRacket.transform.position, 
                        targetPos, 
                        Time.deltaTime * 20f);
                    remoteLeftRacket.transform.rotation = Quaternion.Slerp(
                        remoteLeftRacket.transform.rotation, 
                        targetRot, 
                        Time.deltaTime * 20f);
                }
            }
        }
    }
}
