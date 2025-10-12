using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// One component that implements IGazeSource for BOTH cases:
/// - In Editor / no eye device: uses mouse/HMD forward (simulator behavior)
/// - On Quest Pro (OpenXR Eye Gaze): uses real eye gaze ray / fixation
/// Assign this to your _Gaze object and to Logger/Reticle as the gazeSourceComponent.
/// </summary>
public class AutoGazeSource : MonoBehaviour, IGazeSource
{
    [Header("Simulator (Editor/No eye device)")]
    public Camera cam;                 // auto-fills with Camera.main
    public bool useMouse = true;       // when no eye device is present
    public float mouseDepthMeters = 3f;

    // XR eye-tracking device
    private InputDevice eyeDevice;
    private readonly List<InputDevice> _devices = new List<InputDevice>();
    private int _queryFrame = -999;

    void Awake()
    {
        if (!cam) cam = Camera.main;
    }

    /// <summary>Try to refresh the eye device list (throttled).</summary>
    private void RefreshEyeDevice()
    {
        if (Time.frameCount == _queryFrame) return;
        _queryFrame = Time.frameCount;

        _devices.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.EyeTracking, _devices);
        eyeDevice = (_devices.Count > 0) ? _devices[0] : default;
    }

    public bool TryGetGazeRay(out Ray ray)
    {
        ray = default;

        // 1) Try XR eye tracking (Quest Pro via OpenXR)
        if (!eyeDevice.isValid) RefreshEyeDevice();

        if (eyeDevice.isValid && eyeDevice.TryGetFeatureValue(CommonUsages.eyesData, out Eyes eyes))
        {
            // Prefer fixation point if available
            if (eyes.TryGetFixationPoint(out Vector3 fixation) && cam != null)
            {
                Vector3 origin = cam.transform.position;
                Vector3 dir = (fixation - origin).normalized;
                ray = new Ray(origin, dir);
                return true; // real eye data
            }

            // Fallback to center-eye pose
            var center = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (center.isValid &&
                center.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos) &&
                center.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
            {
                ray = new Ray(pos, rot * Vector3.forward);
                return true; // still driven by XR pose
            }
        }

        // 2) Simulator behavior (no eye device present)
        if (cam == null) cam = Camera.main;

        if (useMouse && cam != null)
        {
            // Mouse position → ray from camera → point at a fixed depth
            Vector3 mp = Input.mousePosition;
            mp.z = Mathf.Clamp(mouseDepthMeters, 0.1f, 10f);
            Vector3 world = cam.ScreenToWorldPoint(mp);
            Vector3 dir = (world - cam.transform.position).normalized;
            ray = new Ray(cam.transform.position, dir);
        }
        else if (cam != null)
        {
            // HMD/camera forward
            ray = new Ray(cam.transform.position, cam.transform.forward);
        }
        else
        {
            ray = new Ray(Vector3.zero, Vector3.forward);
        }

        return false; // simulated / fallback (not real eye data)
    }
}
