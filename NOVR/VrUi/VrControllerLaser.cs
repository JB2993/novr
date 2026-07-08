using UnityEngine;

namespace NOVR.VrUi
{
    [DefaultExecutionOrder(-900)]
    public class VrControllerLaser : NOVRBehaviour
    {
        private LineRenderer? _lineRenderer;
        private static readonly Color LaserColor = new Color32(0, 255, 200, 180);
        private const float LaserWidth = 0.002f;
        private const float LaserMaxDistance = 50f;

        protected override void Awake()
        {
            base.Awake();
            CreateLaser();
        }

        private void CreateLaser()
        {
            var go = new GameObject("VrControllerLaser");
            go.transform.SetParent(transform, false);
            LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());
            _lineRenderer = go.AddComponent<LineRenderer>();

            _lineRenderer.positionCount = 2;
            _lineRenderer.startWidth = LaserWidth;
            _lineRenderer.endWidth = LaserWidth * 0.5f;
            _lineRenderer.startColor = LaserColor;
            _lineRenderer.endColor = new Color(LaserColor.r / 255f, LaserColor.g / 255f, LaserColor.b / 255f, 0f);
            _lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
            _lineRenderer.sortingOrder = short.MaxValue;
            _lineRenderer.enabled = false;
        }

        private int _lastLaserLogFrame;
        private float _nextSpaceDebugTime;

        private void Update()
        {
            var cursor = VrUiCursor.I;
            if (cursor == null || !cursor.IsActive || !cursor.IsControllerModeActive)
            {
                if (_lineRenderer != null && _lineRenderer.enabled)
                {
                    _lineRenderer.enabled = false;
                    Debug.Log("[VrControllerLaser] Laser disabled: cursor null/inactive/mouse mode");
                }
                return;
            }

            var cam = APIBus.CockpitHudCamera;
            if (cam == null)
            {
                if (_lineRenderer != null && _lineRenderer.enabled)
                {
                    _lineRenderer.enabled = false;
                    Debug.Log("[VrControllerLaser] Laser disabled: CockpitHudCamera null");
                }
                return;
            }

            var controllerPos = cam.transform.position; // fallback
            var cursorPos = cursor.CursorPosition;

            // Read current frame controller origin for the line start
            bool gotHand = VrControllerInput.TryGetDominantHand(out var handPos, out var handRot, out _);
            if (gotHand)
                controllerPos = handPos;

            float distance = Vector3.Distance(controllerPos, cursorPos);

            bool wasEnabled = _lineRenderer.enabled;
            if (distance > LaserMaxDistance || distance < 0.01f)
            {
                if (wasEnabled)
                    Debug.Log($"[VrControllerLaser] Laser disabled: distance={distance:F3} outside valid range");
                _lineRenderer.enabled = false;
                return;
            }

            _lineRenderer.enabled = true;
            _lineRenderer.SetPosition(0, controllerPos);
            _lineRenderer.SetPosition(1, cursorPos);

            if (_lastLaserLogFrame != Time.frameCount)
            {
                Debug.Log($"[VrControllerLaser] Laser enabled: ctrlPos={controllerPos:F3} cursorPos={cursorPos:F3} dist={distance:F3} gotHand={gotHand}");
                _lastLaserLogFrame = Time.frameCount;
            }

            // Space-frame debug: log controller pose + head/camera pose once per second
            if (Time.unscaledTime >= _nextSpaceDebugTime)
            {
                _nextSpaceDebugTime = Time.unscaledTime + 1f;
                Vector3 headPos = NOVRHeadsetData.Translation;
                Quaternion headRot = NOVRHeadsetData.Rotation;
                Vector3 camWorldPos = cam.transform.position;
                Vector3 delta = controllerPos - camWorldPos;
                Debug.Log(
                    $"[VrControllerLaser SpaceDebug] " +
                    $"ctrl(InputSys)={controllerPos:F3} " +
                    $"head(NOVRData)={headPos:F3} " +
                    $"camWorld={camWorldPos:F3} " +
                    $"ctrl-cam={delta:F3} " +
                    $"headRotEuler={headRot.eulerAngles:F1} " +
                    $"ctrlRotEuler={handRot.eulerAngles:F1}");
            }
        }
    }
}
