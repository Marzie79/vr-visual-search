using System.Collections.Generic;
using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR;

public class TrackingOriginEnforcer : MonoBehaviour
{
    public XROrigin xrOrigin;
    public float fallbackEyeHeight = 1.6f;

    void Awake()
    {
        if (!xrOrigin) xrOrigin = FindObjectOfType<XROrigin>();

        var subs = new List<XRInputSubsystem>();
        SubsystemManager.GetInstances(subs);

        bool deviceSupported = false, switched = false;
        foreach (var s in subs)
        {
            var supported = s.GetSupportedTrackingOriginModes();
            deviceSupported |= (supported & TrackingOriginModeFlags.Device) != 0;
            if ((supported & TrackingOriginModeFlags.Device) != 0)
                switched |= s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
        }

        if (switched)
        {
            xrOrigin.CameraYOffset = 0f;
            Debug.Log("[OriginEnforcer] Using Device/Eye tracking origin");
        }
        else
        {
            xrOrigin.CameraYOffset = fallbackEyeHeight;
            Debug.LogWarning("[OriginEnforcer] Using Floor + fallback offset");
        }

        foreach (var s in subs) s.TryRecenter();
    }
}
