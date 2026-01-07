using UnityEngine;
using TMPro;

/// <summary>
/// Attach this to ANY TextMeshPro or TextMesh object to debug visibility issues.
/// Will log all relevant settings and try to fix common problems.
/// </summary>
public class TextDebugHelper : MonoBehaviour
{
    [Header("Test Settings")]
    [SerializeField] private string testText = "VISIBLE TEST";
    [SerializeField] private bool autoFix = true;
    
    private void Start()
    {
        Debug.Log($"[TextDebug] ========== Checking: {gameObject.name} ==========");
        
        // Check for TextMeshPro (3D)
        var tmp = GetComponent<TextMeshPro>();
        if (tmp != null)
        {
            DebugTMP(tmp);
            return;
        }
        
        // Check for TextMeshProUGUI (Canvas)
        var tmpUI = GetComponent<TextMeshProUGUI>();
        if (tmpUI != null)
        {
            DebugTMPUI(tmpUI);
            return;
        }
        
        // Check for legacy TextMesh
        var tm = GetComponent<TextMesh>();
        if (tm != null)
        {
            DebugTextMesh(tm);
            return;
        }
        
        Debug.LogError($"[TextDebug] No text component found on {gameObject.name}!");
    }
    
    private void DebugTMP(TextMeshPro tmp)
    {
        Debug.Log($"[TextDebug] Found TextMeshPro (3D)");
        Debug.Log($"[TextDebug] - Text: '{tmp.text}'");
        Debug.Log($"[TextDebug] - Font: {(tmp.font != null ? tmp.font.name : "NULL!")}");
        Debug.Log($"[TextDebug] - Font Size: {tmp.fontSize}");
        Debug.Log($"[TextDebug] - Color: {tmp.color} (alpha={tmp.color.a})");
        Debug.Log($"[TextDebug] - Enabled: {tmp.enabled}");
        Debug.Log($"[TextDebug] - GameObject Active: {gameObject.activeInHierarchy}");
        Debug.Log($"[TextDebug] - LocalScale: {transform.localScale}");
        Debug.Log($"[TextDebug] - LossyScale (world): {transform.lossyScale}");
        Debug.Log($"[TextDebug] - Position: {transform.position}");
        
        // Check parent scales
        Transform current = transform.parent;
        int depth = 0;
        while (current != null && depth < 5)
        {
            Debug.Log($"[TextDebug] - Parent[{depth}] '{current.name}' scale: {current.localScale}");
            if (current.localScale.x == 0 || current.localScale.y == 0 || current.localScale.z == 0)
            {
                Debug.LogError($"[TextDebug] PROBLEM! Parent '{current.name}' has ZERO scale component!");
            }
            current = current.parent;
            depth++;
        }
        
        var renderer = tmp.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            Debug.Log($"[TextDebug] - MeshRenderer enabled: {renderer.enabled}");
            Debug.Log($"[TextDebug] - Material: {(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "NULL!")}");
            Debug.Log($"[TextDebug] - Render Queue: {(renderer.sharedMaterial != null ? renderer.sharedMaterial.renderQueue.ToString() : "N/A")}");
        }
        else
        {
            Debug.LogWarning($"[TextDebug] - No MeshRenderer found!");
        }
        
        if (autoFix)
        {
            // Fix THIS object's local scale if it has zeros
            Vector3 localScale = transform.localScale;
            if (Mathf.Abs(localScale.x) < 0.001f || Mathf.Abs(localScale.y) < 0.001f || Mathf.Abs(localScale.z) < 0.001f)
            {
                Debug.LogWarning($"[TextDebug] AUTO-FIX: LocalScale has zero! Was: {localScale}");
                transform.localScale = new Vector3(1f, 1f, 1f);
                Debug.Log($"[TextDebug] AUTO-FIX: Set localScale to (1, 1, 1)");
            }
            
            // Check world scale after fix
            Vector3 worldScale = transform.lossyScale;
            if (Mathf.Abs(worldScale.x) < 0.0001f || Mathf.Abs(worldScale.y) < 0.0001f || Mathf.Abs(worldScale.z) < 0.0001f)
            {
                Debug.LogError($"[TextDebug] CRITICAL: World scale still zero! A PARENT has zero scale!");
                Debug.LogError($"[TextDebug] Check parents and fix their scales manually!");
            }
            
            if (tmp.font == null)
            {
                Debug.LogWarning("[TextDebug] AUTO-FIX: No font assigned! Trying to load default...");
                tmp.font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
                if (tmp.font == null)
                {
                    // Try alternative paths
                    var fonts = Resources.LoadAll<TMP_FontAsset>("");
                    if (fonts.Length > 0)
                    {
                        tmp.font = fonts[0];
                        Debug.Log($"[TextDebug] AUTO-FIX: Found font: {tmp.font.name}");
                    }
                }
            }
            
            if (tmp.color.a < 0.1f)
            {
                Debug.LogWarning("[TextDebug] AUTO-FIX: Alpha too low, setting to 1");
                tmp.color = new Color(tmp.color.r, tmp.color.g, tmp.color.b, 1f);
            }
            
            if (tmp.fontSize < 1f)
            {
                Debug.LogWarning("[TextDebug] AUTO-FIX: Font size too small, setting to 36");
                tmp.fontSize = 36;
            }
            
            // Check if position is too low (below typical eye level in VR)
            if (transform.position.y < 1.0f)
            {
                Debug.LogWarning($"[TextDebug] Position Y={transform.position.y} is very low for VR viewing");
                Debug.LogWarning("[TextDebug] Consider positioning around Y=1.5 for eye level");
            }
            
            // Set test text
            tmp.text = testText;
            Debug.Log($"[TextDebug] Set test text: '{testText}'");
            
            // Force mesh update
            tmp.ForceMeshUpdate();
            Debug.Log("[TextDebug] Forced mesh update");
        }
    }
    
    private void DebugTMPUI(TextMeshProUGUI tmpUI)
    {
        Debug.Log($"[TextDebug] Found TextMeshProUGUI (Canvas)");
        Debug.Log($"[TextDebug] - Text: '{tmpUI.text}'");
        Debug.Log($"[TextDebug] - Font: {(tmpUI.font != null ? tmpUI.font.name : "NULL!")}");
        Debug.Log($"[TextDebug] - Font Size: {tmpUI.fontSize}");
        Debug.Log($"[TextDebug] - Color: {tmpUI.color} (alpha={tmpUI.color.a})");
        Debug.Log($"[TextDebug] - Enabled: {tmpUI.enabled}");
        Debug.Log($"[TextDebug] - GameObject Active: {gameObject.activeInHierarchy}");
        Debug.Log($"[TextDebug] - RectTransform Size: {tmpUI.rectTransform.sizeDelta}");
        Debug.Log($"[TextDebug] - Scale: {transform.lossyScale}");
        
        // Check Canvas
        var canvas = GetComponentInParent<Canvas>();
        if (canvas != null)
        {
            Debug.Log($"[TextDebug] - Canvas Render Mode: {canvas.renderMode}");
            Debug.Log($"[TextDebug] - Canvas Scale: {canvas.transform.lossyScale}");
            Debug.Log($"[TextDebug] - Canvas Sorting Order: {canvas.sortingOrder}");
            
            if (canvas.renderMode == RenderMode.WorldSpace)
            {
                Debug.Log($"[TextDebug] - Canvas is World Space (correct for VR)");
            }
            else
            {
                Debug.LogWarning($"[TextDebug] - Canvas is NOT World Space! For VR, use World Space");
            }
        }
        else
        {
            Debug.LogError("[TextDebug] - No Canvas found! TextMeshProUGUI needs a Canvas parent!");
        }
        
        if (autoFix)
        {
            if (tmpUI.color.a < 0.1f)
            {
                tmpUI.color = new Color(tmpUI.color.r, tmpUI.color.g, tmpUI.color.b, 1f);
            }
            tmpUI.text = testText;
            Debug.Log($"[TextDebug] Set test text: '{testText}'");
        }
    }
    
    private void DebugTextMesh(TextMesh tm)
    {
        Debug.Log($"[TextDebug] Found Legacy TextMesh");
        Debug.Log($"[TextDebug] - Text: '{tm.text}'");
        Debug.Log($"[TextDebug] - Font: {(tm.font != null ? tm.font.name : "NULL!")}");
        Debug.Log($"[TextDebug] - Font Size: {tm.fontSize}");
        Debug.Log($"[TextDebug] - Character Size: {tm.characterSize}");
        Debug.Log($"[TextDebug] - Color: {tm.color}");
        Debug.Log($"[TextDebug] - Scale: {transform.lossyScale}");
        
        var renderer = GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            Debug.Log($"[TextDebug] - MeshRenderer enabled: {renderer.enabled}");
            Debug.Log($"[TextDebug] - Material: {(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "NULL!")}");
        }
        
        if (autoFix)
        {
            tm.text = testText;
            Debug.Log($"[TextDebug] Set test text: '{testText}'");
        }
    }
    
    // Also provide a button in the inspector to test
    [ContextMenu("Run Debug Check")]
    private void RunDebugCheck()
    {
        Start();
    }
}
