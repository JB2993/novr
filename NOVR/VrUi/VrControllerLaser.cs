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

        private static int _instanceCount;
        private int _instanceId;

        protected override void Awake()
        {
            base.Awake();
            _instanceId = ++_instanceCount;
            if (NOVRPlugin.LogSource != null)
                NOVRPlugin.LogSource.LogMessage($"[VrControllerLaser] Awake instance={_instanceId} name={name} parent={(transform.parent != null ? transform.parent.name : "<none>")} active={gameObject.activeInHierarchy}");
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
        private int _updateCounter;

        private void Update()
        {
            _updateCounter++;

            // Periodic instance count diagnostic
            if (_updateCounter % 120 == 0)
            {
                int laserCount = FindObjectsOfType<VrControllerLaser>().Length;
                int cursorCount = FindObjectsOfType<VrUiCursor>().Length;
                int nouiCount = FindObjectsOfType<NOUIManager>().Length;
                int coreCount = FindObjectsOfType<Core>().Length;
                var allLineRenderers = Resources.FindObjectsOfTypeAll<LineRenderer>();
                string lrInfo = "";
                foreach (var lr in allLineRenderers)
                {
                    string parentChain = lr.transform.parent != null ? lr.transform.parent.name : "<none>";
                    string grandparent = (lr.transform.parent != null && lr.transform.parent.parent != null) ? lr.transform.parent.parent.name : "<none>";
                    lrInfo += $" [{lr.gameObject.name} parent={parentChain} gp={grandparent} enabled={lr.enabled} active={lr.gameObject.activeInHierarchy} scene={lr.gameObject.scene.name}]";
                }
                // Also look for GameObjects that might be creating ray visuals
                var allEventSystems = Resources.FindObjectsOfTypeAll<GameObject>();
                string esInfo = "";
                int esCount = 0;
                foreach (var go in allEventSystems)
                {
                    if (go.name.Contains("EventSystem") || go.name.Contains("Laser") || go.name.Contains("Ray") || go.name.Contains("Pointer") || go.name.Contains("Beam"))
                    {
                        esCount++;
                        if (esCount <= 8)
                            esInfo += $" [{go.name} active={go.activeInHierarchy} scene={go.scene.name}]";
                    }
                }
                if (NOVRPlugin.LogSource != null)
                    NOVRPlugin.LogSource.LogMessage($"[VrControllerLaser] Diag: VrControllerLaser={laserCount} VrUiCursor={cursorCount} NOUIManager={nouiCount} Core={coreCount} TotalLineRenderers={allLineRenderers.Length}{lrInfo} ES/Lazer={esCount}{esInfo} instance={_instanceId} update={_updateCounter}");
            }

            var cursor = VrUiCursor.I;
            if (cursor == null || !cursor.IsActive || !cursor.IsControllerModeActive)
            {
                if (_lineRenderer != null && _lineRenderer.enabled)
                {
                    _lineRenderer.enabled = false;
                    Debug.Log($"[VrControllerLaser] Laser disabled instance={_instanceId}: cursor null/inactive/mouse mode");
                }
                return;
            }

            var cam = APIBus.CockpitHudCamera;
            if (cam == null)
            {
                if (_lineRenderer != null && _lineRenderer.enabled)
                {
                    _lineRenderer.enabled = false;
                    Debug.Log($"[VrControllerLaser] Laser disabled instance={_instanceId}: CockpitHudCamera null");
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
                    Debug.Log($"[VrControllerLaser] Laser disabled instance={_instanceId}: distance={distance:F3} outside valid range");
                _lineRenderer.enabled = false;
                return;
            }

            _lineRenderer.enabled = true;
            _lineRenderer.SetPosition(0, controllerPos);
            _lineRenderer.SetPosition(1, cursorPos);

            if (_lastLaserLogFrame != Time.frameCount)
            {
                Debug.Log($"[VrControllerLaser] Laser enabled instance={_instanceId}: ctrlPos={controllerPos:F3} cursorPos={cursorPos:F3} dist={distance:F3} gotHand={gotHand}");
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
                    $"[VrControllerLaser SpaceDebug] instance={_instanceId} " +
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
