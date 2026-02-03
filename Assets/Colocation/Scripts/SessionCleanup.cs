using UnityEngine;
using Fusion;
using System.Collections;

/// <summary>
/// Handles proper cleanup of Photon Fusion session when quitting the game.
/// Attach this to any persistent GameObject in your scene.
/// </summary>
public class SessionCleanup : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Time to wait for graceful shutdown before forcing quit")]
    [SerializeField] private float shutdownTimeout = 3f;
    
    private bool isQuitting = false;
    private static SessionCleanup instance;
    
    private void Awake()
    {
        // Singleton pattern - persist across scenes
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }
    
    private void OnApplicationQuit()
    {
        if (isQuitting) return;
        isQuitting = true;
        
        ShutdownAllRunners();
    }
    
    private void OnDestroy()
    {
        if (isQuitting) return;
        
        // Also cleanup if this object is destroyed
        ShutdownAllRunners();
    }
    
    /// <summary>
    /// Call this to manually quit the game with proper cleanup
    /// </summary>
    public static void QuitGame()
    {
        
        if (instance != null)
        {
            instance.StartCoroutine(instance.QuitWithCleanup());
        }
        else
        {
            // No instance, just shutdown runners directly
            ShutdownAllRunners();
            Application.Quit();
        }
    }
    
    private IEnumerator QuitWithCleanup()
    {
        isQuitting = true;
        
        // Shutdown all runners
        ShutdownAllRunners();
        
        // Wait a moment for graceful shutdown
        float elapsed = 0f;
        while (elapsed < shutdownTimeout)
        {
            // Check if all runners are shut down
            bool allShutdown = true;
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.State != NetworkRunner.States.Shutdown)
                {
                    allShutdown = false;
                    break;
                }
            }
            
            if (allShutdown)
            {
                break;
            }
            
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
        
        Application.Quit();
        
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #endif
    }
    
    private static void ShutdownAllRunners()
    {
        // Get all active NetworkRunner instances and shut them down
        foreach (var runner in NetworkRunner.Instances)
        {
            if (runner != null && runner.State != NetworkRunner.States.Shutdown)
            {
                runner.Shutdown();
            }
        }
    }
    
    /// <summary>
    /// Call this to leave the current session without quitting the app
    /// </summary>
    public static void LeaveSession()
    {
        ShutdownAllRunners();
    }
}
