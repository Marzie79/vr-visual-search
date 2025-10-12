using UnityEngine;

/// Attach to each spawned object (or grid cell proxy) so we can log AOIs.
public class AoiTag : MonoBehaviour
{
    [Tooltip("Human-readable AOI id, e.g., r1_c2 or item_5")]
    public string aoiId;

    [Tooltip("Index into the current trial's slot list (0..setSize-1).")]
    public int slotIndex = -1;

    [Tooltip("Optional label for color/shape/category.")]
    public string label;
}
