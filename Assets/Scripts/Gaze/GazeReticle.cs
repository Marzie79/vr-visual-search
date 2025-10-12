// Assets/Scripts/Gaze/GazeReticle.cs
using UnityEngine;

public class GazeReticle : MonoBehaviour {
    public MonoBehaviour gazeSourceComponent;   // drag a component that implements IGazeSource
    public float maxDistance = 10f;
    public Transform marker;                    // small sphere/cross to place at hit

    IGazeSource _gaze;

    void Awake(){ _gaze = gazeSourceComponent as IGazeSource; }

    void Update(){
        if (_gaze != null && _gaze.TryGetGazeRay(out var ray)){
            if (Physics.Raycast(ray, out var hit, maxDistance)){
                if (marker){ marker.gameObject.SetActive(true); marker.position = hit.point + hit.normal * 0.01f; }
            } else if (marker){ marker.gameObject.SetActive(false); }
        }
    }
}
