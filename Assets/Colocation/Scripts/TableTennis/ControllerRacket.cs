using UnityEngine;

/// <summary>
/// Attaches a racket visual to the controller when grip is pressed.
/// Since controllers are already visible and synced via colocation alignment,
/// this just swaps the controller visual with a racket.
/// </summary>
public class ControllerRacket : MonoBehaviour
{
    [Header("Racket Prefab (optional - will auto-find if not set)")]
    [SerializeField] private GameObject racketPrefab; // Racket model to show on controller
    
    [Header("Settings")]
    [SerializeField] private OVRInput.Button rightActivateButton = OVRInput.Button.Two; // B button for right controller (Button.Two = B when using RTouch)
    [SerializeField] private OVRInput.Button leftActivateButton = OVRInput.Button.Two; // Y button for left controller (Button.Two = Y when using LTouch)
    [SerializeField] private Vector3 racketOffset = new Vector3(0f, 0.03f, 0.04f); // Position offset from controller
    [SerializeField] private Vector3 racketRotation = new Vector3(-51f, 240f, 43f); // Rotation to align handle with controller grip
    [SerializeField] private float racketScale = 10f; // 10x scale for visibility
    
    // Controller references
    private Transform leftController;
    private Transform rightController;
    
    // Controller visual components (to hide when racket is shown)
    private GameObject leftControllerVisual;
    private GameObject rightControllerVisual;
    
    // Racket instances attached to controllers
    private GameObject leftRacket;
    private GameObject rightRacket;
    
    // Toggle states - separate for each hand
    private bool leftRacketActive = false;
    private bool rightRacketActive = false;
    
    // Velocity tracking for ball collision
    private Vector3 lastRacketPosition;
    private Vector3 racketVelocity;
    
    // Rigidbody for collision detection
    private Rigidbody activeRb;
    
    private void Start()
    {
        Debug.Log($"[ControllerRacket Start] Start - Rotation: {racketRotation}, Offset: {racketOffset}, Scale: {racketScale}");
        TryFindRacketTemplate();
        
        // Create rackets attached to controllers (hidden initially)
        if (racketPrefab != null)
        {
            CreateControllerRackets();
            Debug.Log("[ControllerRacket Start] Initialized - press B/Y to toggle rackets on controllers");
        }
        else
        {
            Debug.LogWarning("[ControllerRacket Start] Racket not found yet - will retry...");
        }
    }

    private void FindControllers()
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>(true);
        if (cameraRig != null)
        {
            leftController = cameraRig.leftControllerAnchor;
            rightController = cameraRig.rightControllerAnchor;
            Debug.Log($"[ControllerRacket FindControllers] Found controllers - Left: {leftController != null}, Right: {rightController != null}");
            
            if (leftController == null)
            {
                Debug.LogWarning("[ControllerRacket FindControllers] leftControllerAnchor is null - creating dummy");
                leftController = new GameObject("LeftControllerDummy").transform;
            }
            if (rightController == null)
            {
                Debug.LogWarning("[ControllerRacket FindControllers] rightControllerAnchor is null - creating dummy");
                rightController = new GameObject("RightControllerDummy").transform;
            }
            
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
            Debug.LogWarning("[ControllerRacket FindControllers] OVRCameraRig not found!");
        }
    }
    
    private void TryFindRacketTemplate()
    {
        if (racketPrefab != null)
        {
            Debug.Log("[ControllerRacket TryFindRacketTemplate] Racket prefab already assigned.");
            return; // Already found
        } 
        
        // Method 1: Search by tag (active objects only)
        var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var r in taggedRackets)
        {
            // Skip our created copies
            if (r.name.Contains("Controller") || r.name.Contains("Remote")) continue;
            racketPrefab = r;
            Debug.Log($"[ControllerRacket TryFindRacketTemplate] Found racket by tag: {racketPrefab.name}");
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
                Debug.Log($"[ControllerRacket TryFindRacketTemplate] Found racket by Resources search: {racketPrefab.name}");
                return;
            }
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
        // If no racket prefab found, create a placeholder
        if (racketPrefab == null)
        {
            Debug.LogWarning("[ControllerRacket CreateControllerRackets] No racket prefab found - creating placeholder rackets");
            CreatePlaceholderRackets();
            return;
        }
                FindControllers();
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
                        Debug.Log($"[ControllerRacket CreateControllerRackets] Hiding scene racket: {child.name}");
                    }
                }
            }
        }
        
        // Now create the controller rackets (only once, after hiding scene rackets)
        CreateRacketOnController();
    }
    
    private void CreateRacketOnController()
    {
        Debug.Log($"[ControllerRacket CreateRacketOnController] Prefab original rotation: {racketPrefab.transform.eulerAngles}");
        
        // Create left controller racket
        if (leftController != null && leftRacket == null)
        {
            Debug.Log("[ControllerRacket CreateRacketOnController] Entering left if");
            // Instantiate without parent first to avoid inheriting rotations
            leftRacket = Instantiate(racketPrefab);
            Debug.Log($"[ControllerRacket CreateRacketOnController] leftRacket after Instantiate: {leftRacket}");
            leftRacket.name = "LeftControllerRacket";
            
            // Reset to identity, then set parent
            leftRacket.transform.SetParent(leftController, false);
            leftRacket.transform.localPosition = racketOffset;
            leftRacket.transform.localRotation = Quaternion.identity; // Reset first
            leftRacket.transform.localRotation = Quaternion.Euler(racketRotation); // Then apply our rotation
            leftRacket.transform.localScale = Vector3.one * racketScale;
            leftRacket.SetActive(false); // Hidden until activated
            
            Debug.Log($"[ControllerRacket CreateRacketOnController] Left racket created with rotation: {leftRacket.transform.localEulerAngles}");
            
            // Remove any physics/grab components
            CleanupRacketComponents(leftRacket);
        }
        else
        {
            Debug.Log($"[ControllerRacket CreateRacketOnController] Skipping left: leftController={leftController}, leftRacket={leftRacket}");
        }
        
        // Create right controller racket
        Debug.Log($"[ControllerRacket CreateRacketOnController] Checking right: rightController={rightController}, rightRacket={rightRacket}");
        if (rightController != null && rightRacket == null)
        {
            Debug.Log("[ControllerRacket CreateRacketOnController] Entering right if");
            // Instantiate without parent first to avoid inheriting rotations
            rightRacket = Instantiate(racketPrefab);
            Debug.Log($"[ControllerRacket CreateRacketOnController] rightRacket after Instantiate: {rightRacket}");
            if (rightRacket != null)
            {
                rightRacket.name = "RightControllerRacket";
                
                // Reset to identity, then set parent
                rightRacket.transform.SetParent(rightController, false);
                Debug.Log("[ControllerRacket CreateRacketOnController] Right SetParent done");
                rightRacket.transform.localPosition = racketOffset;
                rightRacket.transform.localRotation = Quaternion.identity; // Reset first
                rightRacket.transform.localRotation = Quaternion.Euler(racketRotation); // Then apply our rotation
                rightRacket.transform.localScale = Vector3.one * racketScale;
                rightRacket.SetActive(false); // Will be activated by UpdateRacketVisibility
                
                Debug.Log($"[ControllerRacket CreateRacketOnController] Right racket created with rotation: {rightRacket.transform.localEulerAngles}");
                
                // Remove any physics/grab components
                CleanupRacketComponents(rightRacket);
            }
            else
            {
                Debug.LogError("[ControllerRacket CreateRacketOnController] Instantiate returned null for right racket!");
            }
        }
        else
        {
            Debug.Log($"[ControllerRacket CreateRacketOnController] Skipping right: rightController={rightController}, rightRacket={rightRacket}");
        }
        
        // Start with no rackets active
        UpdateRacketVisibility(false, false);
        
        Debug.Log("[ControllerRacket CreateRacketOnController] Created racket on controller - press B/Y to toggle rackets on controllers");
    }
    
    private void CleanupRacketComponents(GameObject racket)
    {
        // Remove components that shouldn't be on the controller-attached version
        var rb = racket.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        
        // Keep collider for ball hits
        
        // Deleted code from current branch (for consistency with main):
        // // Add kinematic rigidbody for collision detection
        // // Kinematic = follows transform exactly, no physics forces
        // var rb = racket.AddComponent<Rigidbody>();
        // rb.isKinematic = true;
        // rb.useGravity = false;
        // rb.interpolation = RigidbodyInterpolation.Interpolate;
        // rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        // 
        // // Ensure proper tag for ball collision detection
        // racket.tag = "Racket";
        // 
        // // Ensure collider exists for ball collision
        // var colliders = racket.GetComponentsInChildren<Collider>();
        // if (colliders.Length == 0)
        // {
        //     var boxCol = racket.AddComponent<BoxCollider>();
        //     boxCol.isTrigger = false;
        //     boxCol.size = new Vector3(0.15f, 0.02f, 0.18f);
        //     boxCol.center = new Vector3(0, 0, 0.1f);
        //     Debug.Log($"[ControllerRacket] Added BoxCollider to {racket.name}");
        // }
        // else
        // {
        //     foreach (var col in colliders)
        //     {
        //         col.isTrigger = false;
        //         col.gameObject.tag = "Racket";
        //     }
        // }
        // 
        // Debug.Log($"[ControllerRacket CleanupRacketComponents] Cleaned up {racket.name} - no physics, direct controller attachment");
    }
    
    private void Update()
    {
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
        
        FindControllers();
        
        // Update dummy transforms to follow controller positions
        if (leftController != null && leftController.gameObject.name == "LeftControllerDummy")
        {
            leftController.position = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
            leftController.rotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);
        }
        if (rightController != null && rightController.gameObject.name == "RightControllerDummy")
        {
            rightController.position = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
            rightController.rotation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        }
        
        // B/Y button press toggles racket on/off for that hand
        bool bPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch);
        bool yPressed = OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch);
        
        if (bPressed)
        {
            rightRacketActive = true;
            leftRacketActive = false;
            Debug.Log($"[ControllerRacket Update] Activated right racket, deactivated left");
        }
        if (yPressed)
        {
            leftRacketActive = true;
            rightRacketActive = false;
            Debug.Log($"[ControllerRacket Update] Activated left racket, deactivated right");
        }
        
        UpdateRacketVisibility(leftRacketActive, rightRacketActive);
    }

    /// Update which racket is visible based on active states
    private void UpdateRacketVisibility(bool leftActive, bool rightActive)
    {
        Debug.Log($"[ControllerRacket UpdateRacketVisibility] Left: {leftActive}, Right: {rightActive}");

        if (leftRacket != null)
        {
            leftRacket.SetActive(leftActive);
            SetControllerVisualActive(leftControllerVisual, !leftActive); // Show controller when racket hidden
            Debug.Log($"[ControllerRacket UpdateRacketVisibility] Left racket SetActive({leftActive}), position: {leftRacket.transform.position}, rotation: {leftRacket.transform.rotation.eulerAngles}");
        }
        else
        {
            Debug.LogWarning("[ControllerRacket UpdateRacketVisibility] Left racket is null!");
        }

        if (rightRacket != null)
        {
            rightRacket.SetActive(rightActive);
            SetControllerVisualActive(rightControllerVisual, !rightActive); // Show controller when racket hidden
            Debug.Log($"[ControllerRacket UpdateRacketVisibility] Right racket SetActive({rightActive}), position: {rightRacket.transform.position}, rotation: {rightRacket.transform.rotation.eulerAngles}");
        }
        else
        {
            Debug.LogWarning("[ControllerRacket UpdateRacketVisibility] Right racket is null!");
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
            
            Debug.Log($"[ControllerRacket SetControllerVisualActive] Controller visual {(active ? "SHOWN" : "HIDDEN")} - disabled {renderers.Length} renderers");
        }
        
        // Disable ray/pointer visuals
        DisableRayVisuals(!active);
    }
    
    private void DisableRayVisuals(bool hide)
    {
        // Find and disable common ray/pointer components
        var cameraRig = FindObjectOfType<OVRCameraRig>(true);
        if (cameraRig == null) return;

        int disabledCount = 0;

        // 1. Disable OVRRayHelper components
        var rayHelpers = cameraRig.GetComponentsInChildren<OVRRayHelper>(true);
        foreach (var rayHelper in rayHelpers)
        {
            rayHelper.enabled = !hide;
            disabledCount++;
        }

        // 2. Disable LineRenderer components (commonly used for rays)
        var lineRenderers = cameraRig.GetComponentsInChildren<LineRenderer>(true);
        foreach (var lr in lineRenderers)
        {
            lr.enabled = !hide;
            disabledCount++;
        }

        // 3. Disable Unity XR Interaction Toolkit ray interactors
        // Search for components with "Ray" or "Interactor" in type name
        var allMonoBehaviours = cameraRig.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (var mb in allMonoBehaviours)
        {
            if (mb == null) continue;

            string typeName = mb.GetType().Name.ToLower();
            // Check for common ray interactor types
            if (typeName.Contains("rayinteractor") ||
                typeName.Contains("xrrayinteractor") ||
                (typeName.Contains("ray") && typeName.Contains("interactor")))
            {
                mb.enabled = !hide;
                disabledCount++;
                Debug.Log($"[ControllerRacket DisableRayVisuals] {(hide ? "Disabled" : "Enabled")} ray interactor: {mb.GetType().Name}");
            }
        }

        // 4. Disable ray/pointer visuals by GameObject name
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
            Debug.Log($"[ControllerRacket DisableRayVisuals] Disabled {disabledCount} ray/pointer components");
        }
        else
        {
            Debug.Log($"[ControllerRacket DisableRayVisuals] Enabled {disabledCount} ray/pointer components");
        }
    }

    /// Check if a controller has an active racket (for ball collision)
    public bool IsRacketActive(OVRInput.Controller controller)
    {
        if (controller == OVRInput.Controller.LTouch) return leftRacket != null && leftRacket.activeInHierarchy;
        if (controller == OVRInput.Controller.RTouch) return rightRacket != null && rightRacket.activeInHierarchy;
        return false;
    }

    /// Get the velocity of a racket for physics calculations (e.g., ball collision)
    public Vector3 GetRacketVelocity(GameObject racketObject)
    {
        // Use tracked velocity if this is our active racket
        if (racketObject == leftRacket || racketObject == rightRacket)
        {
            if (racketVelocity.magnitude > 0.1f)
            {
                return racketVelocity;
            }
        }
        
        // Fallback: use controller velocity from OVRInput
        if (racketObject == leftRacket)
        {
            return OVRInput.GetLocalControllerVelocity(OVRInput.Controller.LTouch);
        }
        if (racketObject == rightRacket)
        {
            return OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch);
        }
        
        return OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch);
    }
    
    /// <summary>
    /// Check if racket is on right hand
    /// </summary>
    public bool IsRacketOnRight => rightRacket != null && rightRacket.activeInHierarchy;
    

    /// Set the racket prefab externally (for passthrough mode)
    public void SetRacketPrefab(GameObject prefab)
    {
        if (prefab != null && racketPrefab == null)
        {
            racketPrefab = prefab;
            Debug.Log($"[ControllerRacket] Racket prefab set externally: {prefab.name}");
            
            // If we already have controllers, create the rackets now
            if (leftController != null && rightController != null && leftRacket == null && rightRacket == null)
            {
                CreateControllerRackets();
            }
        }
    }
    
    /// Create placeholder rackets when no prefab is available
    private void CreatePlaceholderRackets()
    {
        Debug.Log("[ControllerRacket CreatePlaceholderRackets] Creating placeholder rackets...");
        
        // Create left racket
        if (leftController != null && leftRacket == null)
        {
            leftRacket = CreateSinglePlaceholderRacket("LeftControllerRacket");
            leftRacket.transform.SetParent(leftController, false);
            leftRacket.transform.localPosition = racketOffset;
            leftRacket.transform.localRotation = Quaternion.Euler(racketRotation);
            leftRacket.transform.localScale = Vector3.one * 0.15f; // Smaller scale for placeholder
            leftRacket.SetActive(false);
            CleanupRacketComponents(leftRacket);
            Debug.Log("[ControllerRacket CreatePlaceholderRackets] Created placeholder left racket");
        }
        
        // Create right racket
        if (rightController != null && rightRacket == null)
        {
            rightRacket = CreateSinglePlaceholderRacket("RightControllerRacket");
            rightRacket.transform.SetParent(rightController, false);
            rightRacket.transform.localPosition = racketOffset;
            rightRacket.transform.localRotation = Quaternion.Euler(racketRotation);
            rightRacket.transform.localScale = Vector3.one * 0.15f; // Smaller scale for placeholder
            rightRacket.SetActive(false);
            CleanupRacketComponents(rightRacket);
            Debug.Log("[ControllerRacket CreatePlaceholderRackets] Created placeholder right racket");
        }
        
        // Start with no rackets active
        UpdateRacketVisibility(false, false);
        
        Debug.Log("[ControllerRacket CreatePlaceholderRackets] Placeholder rackets created - press B/Y to toggle rackets on controllers");
    }
    

    /// Create a single placeholder racket (handle + paddle)
    private GameObject CreateSinglePlaceholderRacket(string name)
    {
        var racket = new GameObject(name);
        racket.tag = "Racket";
        // Set to default layer (0) or "Racket" layer if you have one
        racket.layer = LayerMask.NameToLayer("Racket") >= 0 ? LayerMask.NameToLayer("Racket") : 0;

        // Handle (cylinder)
        var handle = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        handle.name = "Handle";
        handle.transform.SetParent(racket.transform);
        handle.transform.localPosition = new Vector3(0, -0.08f, 0);
        handle.transform.localScale = new Vector3(0.03f, 0.08f, 0.03f);
        handle.GetComponent<Renderer>().material.color = new Color(0.5f, 0.3f, 0.1f); // Brown wood
        var handleCol = handle.GetComponent<Collider>();
        if (handleCol != null) Object.Destroy(handleCol); // Remove handle collider
        handle.layer = racket.layer;

        // Paddle head (flattened sphere for reliable collision)
        var paddle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        paddle.name = "Paddle";
        paddle.transform.SetParent(racket.transform);
        paddle.transform.localPosition = new Vector3(0, 0.04f, 0);
        paddle.transform.localScale = new Vector3(0.12f, 0.01f, 0.14f);
        paddle.GetComponent<Renderer>().material.color = Color.red;
        paddle.tag = "Racket";
        paddle.layer = racket.layer;

        // Add Rigidbody directly to paddle for collision detection
        var rb = paddle.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // Ensure collider is not a trigger
        var paddleCol = paddle.GetComponent<Collider>();
        if (paddleCol != null)
        {
            paddleCol.isTrigger = false;
        }

        // Add debug collision logger to paddle
        paddle.AddComponent<DebugRacketCollisionLogger>();

        return racket;
    }
}
