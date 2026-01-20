/// <summary>
/// Shared game phase definitions for both VR (TableTennisManager) and AR (PassthroughGameManager).
/// Ensures consistent phase naming and behavior across both game modes.
/// </summary>
public enum GamePhase
{
    /// <summary>
    /// Not in game (AR passthrough only - initial state before game starts)
    /// </summary>
    Idle,

    /// <summary>
    /// Adjusting table position/rotation with thumbsticks
    /// Controls: Left/Right stick for movement, rotation, height adjustment
    /// Exit: Press A/X or GRIP to advance to BallPosition phase
    /// </summary>
    TableAdjust,

    /// <summary>
    /// Ball spawned, can adjust ball position
    /// Controls: GRIP spawns ball, A/X + thumbstick to adjust ball position
    /// Exit: Hit ball with racket to transition to Playing phase
    /// </summary>
    BallPosition,

    /// <summary>
    /// Game in progress - active gameplay
    /// Racket switching is locked during this phase
    /// Exit: Ball hits ground → transitions to BallGrounded phase
    /// </summary>
    Playing,

    /// <summary>
    /// Ball hit ground, round ended
    /// Shows which player gets the next serve
    /// Controls: GRIP to respawn ball and return to BallPosition/Playing
    /// </summary>
    BallGrounded
}

/// <summary>
/// Helper utilities for game phase management
/// </summary>
public static class GamePhaseExtensions
{
    /// <summary>
    /// Get human-readable name for phase
    /// </summary>
    public static string GetDisplayName(this GamePhase phase)
    {
        switch (phase)
        {
            case GamePhase.Idle: return "IDLE";
            case GamePhase.TableAdjust: return "TABLE ADJUST";
            case GamePhase.BallPosition: return "BALL POSITION";
            case GamePhase.Playing: return "PLAYING";
            case GamePhase.BallGrounded: return "ROUND END";
            default: return phase.ToString().ToUpper();
        }
    }

    /// <summary>
    /// Get instruction text for current phase
    /// </summary>
    public static string GetInstructions(this GamePhase phase, bool isBallSpawned = false)
    {
        switch (phase)
        {
            case GamePhase.TableAdjust:
                return "Right Stick X: Rotate | Left Stick Y: Height | A/X: Confirm";

            case GamePhase.BallPosition:
                if (!isBallSpawned)
                    return "GRIP: Spawn Ball";
                return "A/X + Stick: Adjust Ball | Hit ball to start!";

            case GamePhase.Playing:
                return "MENU: Pause";

            case GamePhase.BallGrounded:
                return "GRIP: Respawn Ball | MENU: Pause";

            default:
                return "";
        }
    }

    /// <summary>
    /// Check if phase allows table adjustment
    /// </summary>
    public static bool AllowsTableAdjustment(this GamePhase phase)
    {
        return phase == GamePhase.TableAdjust;
    }

    /// <summary>
    /// Check if phase allows ball adjustment
    /// </summary>
    public static bool AllowsBallAdjustment(this GamePhase phase)
    {
        return phase == GamePhase.BallPosition;
    }

    /// <summary>
    /// Check if phase allows ball spawning
    /// </summary>
    public static bool AllowsBallSpawn(this GamePhase phase)
    {
        return phase == GamePhase.BallPosition;
    }

    /// <summary>
    /// Check if racket switching is locked
    /// </summary>
    public static bool IsRacketSwitchingLocked(this GamePhase phase)
    {
        return phase == GamePhase.Playing;
    }
}
