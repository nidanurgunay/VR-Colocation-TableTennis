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
    
    // Velocity tracking for ball collision (in WORLD SPACE)
    private Vector3 lastLeftRacketWorldPos;
    private Vector3 lastRightRacketWorldPos;
    private Vector3 leftRacketWorldVelocity;
    private Vector3 rightRacketWorldVelocity;
    
    private void Start()
    {
        TryFindRacketTemplate();
        
        // Create rackets attached to controllers (hidden initially)
        if (racketPrefab != null)
        {
            CreateControllerRackets();
        }
    }

    private void FindControllers()
    {
        var cameraRig = FindObjectOfType<OVRCameraRig>(true);
        if (cameraRig != null)
        {
            leftController = cameraRig.leftControllerAnchor;
            rightController = cameraRig.rightControllerAnchor;
            
            if (leftController == null)
            {
                leftController = new GameObject("LeftControllerDummy").transform;
            }
            if (rightController == null)
            {
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
    }
    
    private void TryFindRacketTemplate()
    {
        if (racketPrefab != null)
        {
            return; // Already found
        } 
        
        // Method 1: Search by tag (active objects only)
        var taggedRackets = GameObject.FindGameObjectsWithTag("Racket");
        foreach (var r in taggedRackets)
        {
            // Skip our created copies
            if (r.name.Contains("Controller") || r.name.Contains("Remote")) continue;
            racketPrefab = r;
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
                    }
                }
            }
        }
        
        // Now create the controller rackets (only once, after hiding scene rackets)
        CreateRacketOnController();
    }
    
    private void CreateRacketOnController()
    {
        
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
            
            
            // Remove any physics/grab components
            CleanupRacketComponents(leftRacket);
        }
        
        // Create right controller racket
        if (rightController != null && rightRacket == null)
        {
            // Instantiate without parent first to avoid inheriting rotations
            rightRacket = Instantiate(racketPrefab);
            if (rightRacket != null)
            {
                rightRacket.name = "RightControllerRacket";
                
                // Reset to identity, then set parent
                rightRacket.transform.SetParent(rightController, false);
                rightRacket.transform.localPosition = racketOffset;
                rightRacket.transform.localRotation = Quaternion.identity; // Reset first
                rightRacket.transform.localRotation = Quaternion.Euler(racketRotation); // Then apply our rotation
                rightRacket.transform.localScale = Vector3.one * racketScale;
                rightRacket.SetActive(false); // Will be activated by UpdateRacketVisibility
                
                
                // Remove any physics/grab components
                CleanupRacketComponents(rightRacket);
            }
            else
            {
                Debug.LogError("[ControllerRacket CreateRacketOnController] Instantiate returned null for right racket!");
            }
        }
        
        // Start with no rackets active
        UpdateRacketVisibility(false, false);
        
    }
    
    private void CleanupRacketComponents(GameObject racket)
    {
        // Remove components that shouldn't be on the controller-attached version
        var rb = racket.GetComponent<Rigidbody>();
        if (rb != null) Destroy(rb);
        
        // Keep collider for ball hits
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
        }
        if (yPressed)
        {
            leftRacketActive = true;
            rightRacketActive = false;
        }
        
        UpdateRacketVisibility(leftRacketActive, rightRacketActive);

        // Track world-space velocity for physics calculations
        UpdateRacketVelocities();
    }

    /// Track racket velocities in world space (for accurate cross-device physics)
    private void UpdateRacketVelocities()
    {
        float dt = Time.deltaTime;
        if (dt < 0.001f) return; // Avoid division by zero

        // Track left racket world velocity
        if (leftRacket != null && leftRacket.activeInHierarchy)
        {
            Vector3 currentPos = leftRacket.transform.position;
            leftRacketWorldVelocity = (currentPos - lastLeftRacketWorldPos) / dt;
            lastLeftRacketWorldPos = currentPos;
        }

        // Track right racket world velocity
        if (rightRacket != null && rightRacket.activeInHierarchy)
        {
            Vector3 currentPos = rightRacket.transform.position;
            rightRacketWorldVelocity = (currentPos - lastRightRacketWorldPos) / dt;
            lastRightRacketWorldPos = currentPos;
        }
    }

    /// Update which racket is visible based on active states
    private void UpdateRacketVisibility(bool leftActive, bool rightActive)
    {

        if (leftRacket != null)
        {
            leftRacket.SetActive(leftActive);
            SetControllerVisualActive(leftControllerVisual, !leftActive); // Show controller when racket hidden
        }

        if (rightRacket != null)
        {
            rightRacket.SetActive(rightActive);
            SetControllerVisualActive(rightControllerVisual, !rightActive); // Show controller when racket hidden
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
    }

    /// Check if a controller has an active racket (for ball collision)
    public bool IsRacketActive(OVRInput.Controller controller)
    {
        if (controller == OVRInput.Controller.LTouch) return leftRacket != null && leftRacket.activeInHierarchy;
        if (controller == OVRInput.Controller.RTouch) return rightRacket != null && rightRacket.activeInHierarchy;
        return false;
    }

    /// Get the velocity of a racket in WORLD SPACE for physics calculations.
    /// Since both VR devices have aligned world coordinates through colocation,
    /// world-space velocity ensures accurate cross-device hit physics.
    public Vector3 GetRacketVelocity(GameObject racketObject)
    {
        // Return tracked world-space velocity for our rackets
        if (racketObject == leftRacket && leftRacketWorldVelocity.magnitude > 0.1f)
        {
            return leftRacketWorldVelocity;
        }
        if (racketObject == rightRacket && rightRacketWorldVelocity.magnitude > 0.1f)
        {
            return rightRacketWorldVelocity;
        }

        // Fallback: Convert local controller velocity to world space
        Transform cameraRig = Camera.main?.transform.parent;
        Vector3 localVelocity;

        if (racketObject == leftRacket)
        {
            localVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.LTouch);
        }
        else
        {
            localVelocity = OVRInput.GetLocalControllerVelocity(OVRInput.Controller.RTouch);
        }

        // Transform to world space if we have camera rig
        if (cameraRig != null)
        {
            return cameraRig.TransformDirection(localVelocity);
        }

        return localVelocity;
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
        }
        
        // Start with no rackets active
        UpdateRacketVisibility(false, false);
        
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

        return racket;
    }
}
