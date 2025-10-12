using UnityEngine;

/// <summary>
/// Minimal gaze provider interface used by the task/logger/reticle.
/// Any component (simulator, Quest Pro, etc.) can implement this.
/// </summary>
public interface IGazeSource
{
    /// <returns>True if this frame is a real eye gaze ray; false if it’s a fallback.</returns>
    bool TryGetGazeRay(out Ray ray);
}
