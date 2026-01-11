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
    
    // Toggle states
    private bool leftActive = false;
    private bool rightActive = false;
    private bool leftWasPressed = false;
    private bool rightWasPressed = false;
    
    private void Start()
    {
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
        }
        
        Debug.Log("[ControllerRacket] Created racket visuals on controllers (press B/Y to show)");
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
                Debug.Log("[ControllerRacket] Racket found on retry - initialized!");
            }
            return; // Don't process input until we have rackets
        }
        
        if (leftController == null || rightController == null)
        {
            FindControllers();
            return;
        }
        
        // Check for toggle on left controller (Y button)
        bool leftPressed = OVRInput.Get(leftActivateButton, OVRInput.Controller.LTouch);
        if (leftPressed && !leftWasPressed)
        {
            leftActive = !leftActive;
            if (leftRacket != null)
            {
                leftRacket.SetActive(leftActive);
                SetControllerVisualActive(leftControllerVisual, !leftActive);
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
                SetControllerVisualActive(rightControllerVisual, !rightActive);
                Debug.Log($"[RACKET_DEBUG] Right racket: {(rightActive ? "SHOWN" : "HIDDEN")}");
            }
        }
        rightWasPressed = rightPressed;
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
    /// Get the velocity of a racket for physics calculations (e.g., ball collision)
    /// </summary>
    public Vector3 GetRacketVelocity(GameObject racketObject)
    {
        // Try to get velocity from Rigidbody
        var rb = racketObject.GetComponent<Rigidbody>();
        if (rb != null)
        {
            return rb.velocity;
        }
        
        // Try from parent
        rb = racketObject.GetComponentInParent<Rigidbody>();
        if (rb != null)
        {
            return rb.velocity;
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
        
        return Vector3.zero;
    }
}
