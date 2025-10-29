using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR;
using UnityEngine.XR.Management;

public class ForceFloorEyeHeight : MonoBehaviour
{
    public XROrigin xrOrigin;                 // drag your XR Origin (VR)
    [Tooltip("Target eye height above the floor (meters). 1.70–1.80 is typical standing.")]
    public float eyeHeightMeters = 1.70f;
    public bool logDetails = true;

    void Awake(){ if (!xrOrigin) xrOrigin = FindObjectOfType<XROrigin>(); }

    void OnEnable() { StartCoroutine(ApplyWhenXRReady()); }

    IEnumerator ApplyWhenXRReady()
    {
        // Wait for XR loader to initialize on device
        while (XRGeneralSettings.Instance == null ||
               XRGeneralSettings.Instance.Manager == null ||
               XRGeneralSettings.Instance.Manager.activeLoader == null)
            yield return null;

        // Ensure subsystems exist
        var subs = new List<XRInputSubsystem>();
        SubsystemManager.GetInstances(subs);

        // Force FLOOR origin, then set CameraYOffset = desired eye height
        bool any = subs.Count > 0;
        bool floorSet = false;
        foreach (var s in subs)
            floorSet |= s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);

        xrOrigin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
        xrOrigin.CameraYOffset = eyeHeightMeters;   // <- this gives you the standing height
        foreach (var s in subs) s.TryRecenter();

        if (logDetails)
            Debug.Log($"[ForceFloorEyeHeight] floorSet={floorSet}, eyeHeight={eyeHeightMeters:F2}m, camLocalY={xrOrigin.CameraInOriginSpacePos.y:F2}");
    }
}
