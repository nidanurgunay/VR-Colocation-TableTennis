using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Displays cube spawning instructions popup when cube is spawned.
/// Follows the user's head (screen space style) until dismissed.
/// Dismisses on button click or controller input.
/// </summary>
public class CubeInstructionsPopup : MonoBehaviour
{
    [Header("UI References (Auto-created if not assigned)")]
    [SerializeField] private Canvas popupCanvas;
    [SerializeField] private GameObject popupPanel;
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI messageText;
    [SerializeField] private Button okButton;

    [Header("Settings")]
    [SerializeField] private float distanceFromCamera = 1.5f; // Distance in front of camera
    [SerializeField] private float followSpeed = 5f; // How fast the popup follows head movement
    [SerializeField] private float verticalOffset = -0.1f; // Slight offset below eye level

    private bool isShowing = false;
    private Transform cameraTransform;
    private Vector3 targetPosition;
    private Quaternion targetRotation;

    private void Start()
    {
        // Find main camera
        cameraTransform = Camera.main?.transform;
    }

    private void Update()
    {
        if (!isShowing) return;

        // Follow the camera (head tracking)
        FollowCamera();

        // Dismiss with any button press (A, B, X, Y, or triggers)
        if (OVRInput.GetDown(OVRInput.Button.One) ||      // A
            OVRInput.GetDown(OVRInput.Button.Two) ||      // B
            OVRInput.GetDown(OVRInput.Button.Three) ||    // X
            OVRInput.GetDown(OVRInput.Button.Four) ||     // Y
            OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger) ||
            OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger))
        {
            HidePopup();
        }
    }

    private void FollowCamera()
    {
        if (cameraTransform == null || popupCanvas == null) return;

        // Calculate target position in front of camera
        Vector3 forward = cameraTransform.forward;
        Vector3 up = cameraTransform.up;
        targetPosition = cameraTransform.position + forward * distanceFromCamera + up * verticalOffset;
        targetRotation = Quaternion.LookRotation(targetPosition - cameraTransform.position);

        // Smoothly move to target position
        popupCanvas.transform.position = Vector3.Lerp(popupCanvas.transform.position, targetPosition, Time.deltaTime * followSpeed);
        popupCanvas.transform.rotation = Quaternion.Slerp(popupCanvas.transform.rotation, targetRotation, Time.deltaTime * followSpeed);
    }

    public void ShowPopup()
    {
        // Ensure we have camera reference
        if (cameraTransform == null)
        {
            cameraTransform = Camera.main?.transform;
        }

        // Create UI if not assigned
        if (popupCanvas == null)
        {
            CreatePopupUI();
        }

        // Snap to position in front of camera immediately
        if (cameraTransform != null && popupCanvas != null)
        {
            Vector3 forward = cameraTransform.forward;
            Vector3 up = cameraTransform.up;
            popupCanvas.transform.position = cameraTransform.position + forward * distanceFromCamera + up * verticalOffset;
            popupCanvas.transform.rotation = Quaternion.LookRotation(popupCanvas.transform.position - cameraTransform.position);
        }

        // Update message
        if (messageText != null)
        {
            messageText.text = GetCubeInstructionsMessage();
        }

        popupCanvas.gameObject.SetActive(true);
        if (popupPanel != null) popupPanel.SetActive(true);
        isShowing = true;

        Debug.Log("[CubeInstructionsPopup] Showing cube instructions popup (follows head)");
    }

    public void HidePopup()
    {
        if (popupCanvas != null)
        {
            popupCanvas.gameObject.SetActive(false);
        }
        if (popupPanel != null)
        {
            popupPanel.SetActive(false);
        }
        isShowing = false;

        Debug.Log("[CubeInstructionsPopup] Cube instructions popup dismissed");
    }

    private void CreatePopupUI()
    {
        // Create Canvas (matching WelcomePopup style)
        GameObject canvasObj = new GameObject("CubeInstructionsPopupCanvas");
        popupCanvas = canvasObj.AddComponent<Canvas>();
        popupCanvas.renderMode = RenderMode.WorldSpace;

        // Add Canvas Scaler
        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 100f;

        // Add Graphic Raycaster for button interaction
        canvasObj.AddComponent<GraphicRaycaster>();

        // Initial position in front of camera
        if (cameraTransform != null)
        {
            canvasObj.transform.position = cameraTransform.position + cameraTransform.forward * distanceFromCamera;
            canvasObj.transform.rotation = Quaternion.LookRotation(canvasObj.transform.position - cameraTransform.position);
        }
        else
        {
            canvasObj.transform.position = new Vector3(0, 1.5f, 1.5f);
            canvasObj.transform.rotation = Quaternion.identity;
        }

        // Scale canvas for VR (matching WelcomePopup)
        canvasObj.transform.localScale = Vector3.one * 0.0015f;

        // Create Panel (background - matching WelcomePopup #001F41)
        popupPanel = new GameObject("Panel");
        popupPanel.transform.SetParent(canvasObj.transform, false);
        RectTransform panelRect = popupPanel.AddComponent<RectTransform>();
        panelRect.sizeDelta = new Vector2(800, 750);

        Image panelImage = popupPanel.AddComponent<Image>();
        ColorUtility.TryParseHtmlString("#001F41", out Color bgColor);
        panelImage.color = new Color(bgColor.r, bgColor.g, bgColor.b, 0.95f);

        // Add outline (matching WelcomePopup #00938A)
        Outline outline = popupPanel.AddComponent<Outline>();
        ColorUtility.TryParseHtmlString("#00938A", out Color outlineColor);
        outline.effectColor = outlineColor;
        outline.effectDistance = new Vector2(3, 3);

        // Create Title
        GameObject titleObj = new GameObject("Title");
        titleObj.transform.SetParent(popupPanel.transform, false);
        RectTransform titleRect = titleObj.AddComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0.5f, 1f);
        titleRect.anchorMax = new Vector2(0.5f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.anchoredPosition = new Vector2(0, -30);
        titleRect.sizeDelta = new Vector2(750, 60);

        titleText = titleObj.AddComponent<TextMeshProUGUI>();
        titleText.text = "Cube Spawned!";
        titleText.fontSize = 42;
        titleText.fontStyle = FontStyles.Bold;
        ColorUtility.TryParseHtmlString("#01B4A9", out Color titleColor);
        titleText.color = titleColor;
        titleText.alignment = TextAlignmentOptions.Center;

        // Create Message
        GameObject messageObj = new GameObject("Message");
        messageObj.transform.SetParent(popupPanel.transform, false);
        RectTransform messageRect = messageObj.AddComponent<RectTransform>();
        messageRect.anchorMin = new Vector2(0.5f, 0.5f);
        messageRect.anchorMax = new Vector2(0.5f, 0.5f);
        messageRect.pivot = new Vector2(0.5f, 0.5f);
        messageRect.anchoredPosition = new Vector2(0, 30);
        messageRect.sizeDelta = new Vector2(720, 450);

        messageText = messageObj.AddComponent<TextMeshProUGUI>();
        messageText.text = GetCubeInstructionsMessage();
        messageText.fontSize = 28;
        messageText.color = Color.white;
        messageText.alignment = TextAlignmentOptions.Left;
        messageText.lineSpacing = 5f;

        // Create OK Button (matching WelcomePopup #00938A)
        GameObject buttonObj = new GameObject("OKButton");
        buttonObj.transform.SetParent(popupPanel.transform, false);
        RectTransform buttonRect = buttonObj.AddComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0f);
        buttonRect.anchorMax = new Vector2(0.5f, 0f);
        buttonRect.pivot = new Vector2(0.5f, 0f);
        buttonRect.anchoredPosition = new Vector2(0, 30);
        buttonRect.sizeDelta = new Vector2(200, 60);

        Image buttonImage = buttonObj.AddComponent<Image>();
        ColorUtility.TryParseHtmlString("#00938A", out Color buttonColor);
        buttonImage.color = buttonColor;

        okButton = buttonObj.AddComponent<Button>();
        okButton.targetGraphic = buttonImage;
        okButton.onClick.AddListener(HidePopup);

        // Button hover colors
        ColorBlock colors = okButton.colors;
        colors.normalColor = buttonColor;
        ColorUtility.TryParseHtmlString("#12FFE8", out Color highlightColor);
        colors.highlightedColor = highlightColor;
        ColorUtility.TryParseHtmlString("#01B4A9", out Color pressedColor);
        colors.pressedColor = pressedColor;
        okButton.colors = colors;

        // Button Text
        GameObject buttonTextObj = new GameObject("Text");
        buttonTextObj.transform.SetParent(buttonObj.transform, false);
        RectTransform buttonTextRect = buttonTextObj.AddComponent<RectTransform>();
        buttonTextRect.anchorMin = Vector2.zero;
        buttonTextRect.anchorMax = Vector2.one;
        buttonTextRect.sizeDelta = Vector2.zero;

        TextMeshProUGUI buttonText = buttonTextObj.AddComponent<TextMeshProUGUI>();
        buttonText.text = "Got It!";
        buttonText.fontSize = 32;
        buttonText.fontStyle = FontStyles.Bold;
        buttonText.color = Color.white;
        buttonText.alignment = TextAlignmentOptions.Center;

        Debug.Log("[CubeInstructionsPopup] Created popup UI programmatically");
    }

    private string GetCubeInstructionsMessage()
    {
        return @"<b>Cube Alignment Testing Procedure:</b>

You now have a <color=#FFD700>grabbable networked cube</color> for testing spatial alignment accuracy.

<color=#01B4A9>•</color> <b>Step 1:</b> Place the cube in the <b>first square</b> on the physical grid
<color=#01B4A9>•</color> <b>Step 2:</b> The <b>other user grabs the cube</b> and measures positioning error on their headset
<color=#01B4A9>•</color> <b>Step 3:</b> Record the measured error for square #1
<color=#01B4A9>•</color> <b>Step 4:</b> Repeat for all <b>6 squares</b> (3 rows × 2 columns)

<color=#FF6B6B>Important:</color> Alternate who grabs the cube each time to test both perspectives.";
    }
}