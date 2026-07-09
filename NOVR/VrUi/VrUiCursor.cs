using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;

namespace NOVR.VrUi;

[DefaultExecutionOrder(-1000)]
public class VrUiCursor: NOVRBehaviour
{
    public static VrUiCursor? Instance { get; private set; }
    public static VrUiCursor? I => Instance;
    private static int _instanceCount;
    private int _instanceId;

    public bool IsActive => _cursor != null && _cursor.activeSelf;
    public Vector3 CursorPosition => _cursor != null ? _cursor.transform.position : Vector3.zero;

    protected override void Awake()
    {
        base.Awake();
        _instanceId = ++_instanceCount;
        if (Instance != null && Instance != this)
            if (NOVRPlugin.LogSource != null)
                NOVRPlugin.LogSource.LogMessage($"[VrUiCursor] WARNING: Instance already set (id={Instance._instanceId}), overwriting with new instance id={_instanceId}");
        Instance = this;
        if (NOVRPlugin.LogSource != null)
            NOVRPlugin.LogSource.LogMessage($"[VrUiCursor] Awake id={_instanceId} name={name} parent={(transform.parent != null ? transform.parent.name : "<none>")}");
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            if (NOVRPlugin.LogSource != null)
                NOVRPlugin.LogSource.LogMessage($"[VrUiCursor] OnDestroy id={_instanceId}");
        }
    }

    private Texture2D? _texture;
    private const float DefaultProjectionDistance = 5;
    private const float CursorCanvasScale = 0.001f;
    private const int CursorTextureSize = 64;
    private const float CursorRingRadius = 12f;
    private const float CursorRingThickness = 4f;
    private const float CursorIdlePulseScale = 0.035f;
    private const float CursorIdlePulseSpeed = 5.5f;
    private const float CursorHoverScale = 1.18f;
    private const float CursorPressedScale = 0.84f;
    private const float CursorClickPulseScale = 0.22f;
    private const float CursorClickPulseDuration = 0.18f;
    private const float CursorAnimationLerpSpeed = 24f;
    private static readonly Color CursorNormalColor = new Color32(100, 200, 100, 255);
    private static readonly Color CursorHoverColor = new Color32(155, 255, 175, 255);
    private static readonly Color CursorPressedColor = new Color32(255, 224, 92, 255);
    private static readonly Color CursorMouseNormalColor = new Color32(110, 180, 240, 255);
    private static readonly Color CursorMouseHoverColor = new Color32(170, 215, 255, 255);
    private static readonly Color CursorMousePressedColor = new Color32(255, 180, 220, 255);
    private GameObject? _cursor;
    private RectTransform? _cursorRectTransform;
    private Canvas? _cursorCanvas;
    private RawImage? _cursorImage;
    private bool _cursorOverInteractive;
    private float _lastCursorClickTime = -100f;
    private bool _hasProjectionReferenceOverride;
    private Quaternion _projectionReferenceRotation = Quaternion.identity;

    private Mouse? _realMouse;
    private bool _isOffscreen;
    private Canvas? _activeCanvas;
    private bool _hasActiveCanvas;
    private Ray _lastProbeRay;
    private Vector3 _lastCursorTargetPos;
    private string _lastCanvasName = "";
    
    // Paired-diagnostic snapshots for VirtualMouse feed-vs-consume debugging
    private int _feedFrame;
    private Vector2 _feedScreenPoint;
    private Vector3 _feedCameraPos;
    private Quaternion _feedCameraRot;
    private float _feedProjM00, _feedProjM11, _feedProjM02, _feedProjM12;
    private Vector3 _feedCursorWorldPos;

    // Controller input mode
    private bool _controllerModeActive;
    private bool _triggerIsPressed;
    private bool _triggerWasPressed;
    private Vector3 _controllerOrigin;
    private Quaternion _controllerRotation;

    // Throttled diagnostic logging
    private float _lastDiagLogTime = -100f;
    private const float DiagLogInterval = 1f;
    private static int _diagFrameCounter;

    // Runtime input mode override — set by CheckModeToggleRequests() in response
    // to a mouse left-click (→ Mouse) or controller trigger press (→ Controller).
    // When _runtimeMode == Auto the config-driven default is used.
    public enum RuntimeInputMode { Auto, Mouse, Controller }
    private RuntimeInputMode _runtimeMode = RuntimeInputMode.Auto;

    // Angular dead-zone for controller ray — suppresses cursor movement when the
    // ray direction changes by less than this threshold, killing idle shimmer.
    private const float ControllerDeadZoneDegrees = 0.3f;
    private Vector3 _lastControllerRayLocalDir;
    private bool _hasLastControllerRay;

    // Direct pointer event state
    private PointerEventData? _pointerEventData;
    private GameObject? _hovered;
    private GameObject? _pointerPress;
    private bool _wasLeftDown;

    // Standard UI input module references — disabled normally, re-enabled when the
    // original game's control mapper (non-VR screen) is open so mouse clicks work.
    private StandaloneInputModule? _standaloneInputModule;
    private UnityEngine.InputSystem.UI.InputSystemUIInputModule? _inputSystemUIInputModule;

    public Camera? UiCamera
    {
        get
        {
            return APIBus.CockpitHudCamera;
        }
    }
    
    
    public Vector2 GetScreenPoint()
    {
        var camera = UiCamera;
        if (_cursor == null || camera == null)
        {
            _isOffscreen = true;
            return Vector2.zero;
        }

        Vector3 screenPoint = camera.WorldToScreenPoint(
            _cursor.transform.position);

        if (screenPoint.z <= 0f)
        {
            _isOffscreen = true;
            return Vector2.zero;
        }

        _isOffscreen = false;
        return new Vector2(
            Mathf.Clamp(screenPoint.x, 0f, camera.pixelWidth),
            Mathf.Clamp(screenPoint.y, 0f, camera.pixelHeight));
    }

    public bool IsControllerModeActive => _controllerModeActive;

    public RuntimeInputMode RuntimeMode => _runtimeMode;

    public void SetRuntimeMode(RuntimeInputMode mode)
    {
        _runtimeMode = mode;
    }

    public void SetProjectionReferenceRotation(Quaternion referenceRotation)
    {
        _projectionReferenceRotation = referenceRotation;
        _hasProjectionReferenceOverride = true;
    }

    public void ClearProjectionReferenceRotation()
    {
        _hasProjectionReferenceOverride = false;
    }
    
    
    private void Start()
    {
        _texture = CreateCursorTexture();
        if (NOVRPlugin.LogSource != null)
            NOVRPlugin.LogSource.LogMessage($"[VrUiCursor] Start id={_instanceId}");
    }

    private void Update()
    {
        if (!Application.isFocused)
        {
            if (_cursor != null && _cursor.activeSelf)
                _cursor.SetActive(false);
            return;
        }

        if (_realMouse == null)
        {
            _realMouse = Mouse.current ?? throw new System.InvalidOperationException(
                $"[{nameof(VrUiCursor)}] Unity InputSystem could not find an active hardware Mouse device during initialization.");
        }

        if (Time.frameCount < 120 || Time.frameCount % 120 == 0)
            DisableStandardUIModule();

        UpdateStandardUIModuleState();
        if (_texture == null) return;

        CheckModeToggleRequests();

        // Determine input mode
        string modeSetting = ModConfiguration.Instance.CursorInputMode.Value;
        bool controllerAvailable = VrControllerInput.TryGetDominantHand(
            out _controllerOrigin, out _controllerRotation, out _triggerIsPressed);

        bool useController;
        if (_runtimeMode == RuntimeInputMode.Controller)
        {
            useController = true;
        }
        else if (_runtimeMode == RuntimeInputMode.Mouse)
        {
            useController = false;
        }
        else
        {
            useController = modeSetting == "Controller" ||
                            (modeSetting == "Auto" && controllerAvailable);
        }

        // Throttled diagnostic — show current mode + pose state once per second
        float diagNow = Time.unscaledTime;
        if (diagNow - _lastDiagLogTime > DiagLogInterval)
        {
            _lastDiagLogTime = diagNow;
            string branch = (useController && controllerAvailable) ? "CONTROLLER" : "MOUSE";
            string cursorPosStr = (_cursor != null) ? _cursor.transform.position.ToString() : "<null>";
            string cursorActiveStr = (_cursor != null) ? _cursor.activeSelf.ToString() : "<null>";
            string msg = $"[VrUiCursor] mode='{modeSetting}' runtime={_runtimeMode} ctrlAvail={controllerAvailable} branch={branch} ctrlPos={_controllerOrigin} cursorPos={cursorPosStr} cursorActive={cursorActiveStr} trigger={_triggerIsPressed} _hasActiveCanvas={_hasActiveCanvas}";
            if (NOVRPlugin.LogSource != null) NOVRPlugin.LogSource.LogMessage(msg);
            else Debug.Log(msg);
        }

        if (useController && controllerAvailable)
        {
            _controllerModeActive = true;
            _triggerWasPressed = _triggerIsPressed && !_triggerWasPressed;

            // Use trigger was-pressed tracking for animation
            bool triggerDownThisFrame = VrControllerInput.GetTriggerWasPressedThisFrame(
                XRNode.RightHand) || VrControllerInput.GetTriggerWasPressedThisFrame(
                XRNode.LeftHand);

            UpdateCursorAnglesFromController();

            Vector2 screenPoint = GetScreenPoint();
            if (!_isOffscreen)
            {
                _feedFrame = Time.frameCount;
                _feedScreenPoint = screenPoint;
                _feedCursorWorldPos = _cursor != null ? _cursor.transform.position : Vector3.zero;
                var snapCam = UiCamera;
                if (snapCam != null)
                {
                    _feedCameraPos = snapCam.transform.position;
                    _feedCameraRot = snapCam.transform.rotation;
                    var p = snapCam.projectionMatrix;
                    _feedProjM00 = p.m00; _feedProjM11 = p.m11;
                    _feedProjM02 = p.m02; _feedProjM12 = p.m12;
                }

                FirePointerEvents(screenPoint, _triggerIsPressed);
            }

            UpdateCursorAnimation(triggerDownThisFrame, _triggerIsPressed);

            if (triggerDownThisFrame)
            {
                LogRaycastAtCursor();
                ForwardMapClickIfNeeded();
            }

            _triggerWasPressed = _triggerIsPressed;
        }
        else
        {
            _controllerModeActive = false;
            if (!IsRealCursorVisible())
            {
                if (_cursor != null)
                    _cursor.SetActive(false);
                return;
            }

            UpdateCursorAngles();
            var realMouse = _realMouse;
            if (realMouse == null) return;

            Vector2 screenPoint = GetScreenPoint();
            if (!_isOffscreen)
            {
                _feedFrame = Time.frameCount;
                _feedScreenPoint = screenPoint;
                _feedCursorWorldPos = _cursor != null ? _cursor.transform.position : Vector3.zero;
                var snapCam = UiCamera;
                if (snapCam != null)
                {
                    _feedCameraPos = snapCam.transform.position;
                    _feedCameraRot = snapCam.transform.rotation;
                    var p = snapCam.projectionMatrix;
                    _feedProjM00 = p.m00; _feedProjM11 = p.m11;
                    _feedProjM02 = p.m02; _feedProjM12 = p.m12;
                }
                FirePointerEvents(screenPoint, realMouse.leftButton.isPressed);
            }

            UpdateCursorAnimation(realMouse.leftButton.wasPressedThisFrame, realMouse.leftButton.isPressed);

            if (realMouse.leftButton.wasPressedThisFrame)
            {
                LogRaycastAtCursor();
                ForwardMapClickIfNeeded();
            }
        }
    }

    private void CheckModeToggleRequests()
    {
        if (_realMouse != null && _realMouse.leftButton.wasPressedThisFrame
            && _runtimeMode != RuntimeInputMode.Mouse)
        {
            _runtimeMode = RuntimeInputMode.Mouse;
            return;
        }

        bool triggerPressedThisFrame =
            VrControllerInput.GetTriggerWasPressedThisFrame(XRNode.RightHand) ||
            VrControllerInput.GetTriggerWasPressedThisFrame(XRNode.LeftHand);
        if (triggerPressedThisFrame && _runtimeMode != RuntimeInputMode.Controller)
        {
            _runtimeMode = RuntimeInputMode.Controller;
        }
    }

    private void LateUpdate()
    {
    }

    private void FirePointerEvents(Vector2 screenPoint, bool isLeftDown)
    {
        var es = EventSystem.current;
        if (es == null) return;

        if (_activeCanvas == null || !_hasActiveCanvas) return;

        var raycaster = _activeCanvas.GetComponent<GraphicRaycaster>();
        if (raycaster == null) return;

        var ped = _pointerEventData;
        if (ped == null)
        {
            ped = new PointerEventData(es);
            _pointerEventData = ped;
        }

        ped.position = screenPoint;
        ped.delta = Vector2.zero;
        ped.button = PointerEventData.InputButton.Left;

        var results = new List<RaycastResult>();
        raycaster.Raycast(ped, results);

        // Get the event root (the ancestor that has Selectable or IPointerClickHandler)
        GameObject? current = null;
        if (results.Count > 0)
        {
            current = GetEventRoot(results[0].gameObject);
            ped.pointerCurrentRaycast = results[0];
        }

        // Hover enter / exit — use hierarchy-walking version
        if (current != _hovered)
        {
            // Exit old hierarchy
            if (_hovered != null)
            {
                ExecuteEvents.ExecuteHierarchy(_hovered, ped, ExecuteEvents.pointerExitHandler);
            }
            _hovered = current;
            // Enter new hierarchy
            if (current != null)
            {
                ExecuteEvents.ExecuteHierarchy(current, ped, ExecuteEvents.pointerEnterHandler);
            }
        }

        _cursorOverInteractive = false;
        if (current != null)
        {
            var selectable = current.GetComponent<Selectable>();
            if (selectable != null)
                _cursorOverInteractive = true;
        }

        // Click handling
        if (isLeftDown)
        {
            if (!_wasLeftDown)
            {
                _pointerPress = current;
                ped.pressPosition = screenPoint;
                ped.pointerPress = current;
                ped.clickTime = Time.unscaledTime;
                ped.clickCount = 1;
                if (current != null)
                {
                    ExecuteEvents.ExecuteHierarchy(current, ped, ExecuteEvents.pointerDownHandler);
                }
            }
            else
            {
                if (_pointerPress != null && _pointerPress == current)
                {
                    ExecuteEvents.ExecuteHierarchy(_pointerPress, ped, ExecuteEvents.dragHandler);
                }
            }
        }
        else if (_wasLeftDown)
        {
            if (_pointerPress != null)
            {
                ExecuteEvents.ExecuteHierarchy(_pointerPress, ped, ExecuteEvents.pointerUpHandler);
                if (_pointerPress == current)
                {
                    ExecuteEvents.ExecuteHierarchy(_pointerPress, ped, ExecuteEvents.pointerClickHandler);
                    ped.clickCount++;
                }
                else
                {
                    ExecuteEvents.ExecuteHierarchy(_pointerPress, ped, ExecuteEvents.initializePotentialDrag);
                }
            }
            _pointerPress = null;
        }

        _wasLeftDown = isLeftDown;
    }

    private static GameObject? GetEventRoot(GameObject? obj)
    {
        if (obj == null) return null;
        // Walk up to find the first ancestor with IPointerClickHandler (a button root)
        Transform t = obj.transform;
        while (t != null)
        {
            if (t.GetComponent<IPointerClickHandler>() != null)
                return t.gameObject;
            t = t.parent;
        }
        return obj;
    }

    private bool DisableStandardUIModule()
    {
        bool foundAny = false;

        _standaloneInputModule = FindObjectOfType<StandaloneInputModule>();
        if (_standaloneInputModule != null)
        {
            if (NOVRPlugin.LogSource != null)
                NOVRPlugin.LogSource.LogMessage($"[VrUiCursor] Disabling StandaloneInputModule (enabled={_standaloneInputModule.enabled}) on {_standaloneInputModule.gameObject.name}");
            _standaloneInputModule.enabled = false;
            foundAny = true;
        }

        _inputSystemUIInputModule = FindObjectOfType<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        if (_inputSystemUIInputModule != null)
        {
            if (NOVRPlugin.LogSource != null)
                NOVRPlugin.LogSource.LogMessage($"[VrUiCursor] Disabling InputSystemUIInputModule (enabled={_inputSystemUIInputModule.enabled}) on {_inputSystemUIInputModule.gameObject.name}");
            _inputSystemUIInputModule.enabled = false;
            foundAny = true;
        }

        if (!foundAny)
        {
            if (NOVRPlugin.LogSource != null)
                NOVRPlugin.LogSource.LogMessage("[VrUiCursor] No UI InputModule found in scene, retrying...");
        }
        return foundAny;
    }

    private void UpdateStandardUIModuleState()
    {
        if (_standaloneInputModule == null && _inputSystemUIInputModule == null)
            return;

        var controlMapperOpen = GameManager.controlMapper != null && GameManager.controlMapper.isOpen;

        if (_standaloneInputModule != null)
            _standaloneInputModule.enabled = controlMapperOpen;
        if (_inputSystemUIInputModule != null)
            _inputSystemUIInputModule.enabled = controlMapperOpen;
    }

    private void UpdateCursorAngles()
    {
        var camera = UiCamera;
        if (camera == null) return;

        EnsureCursorCanvas(camera);

        if (_cursor == null || _cursorRectTransform == null)
            return;

        if (!_cursor.activeSelf)
            _cursor.SetActive(true);

        var mouse = _realMouse;
        if (mouse == null) return;

        var mousePos = mouse.position.ReadValue();

        Transform anchor = GetAnchorTransform();
        Vector3 probeOrigin = anchor != null ? anchor.position : camera.transform.position;
        Quaternion referenceRotation = GetProjectionReferenceRotation();

        // Compute mouse-driven world direction
        float cursorPitch = Mathf.Lerp(-45f, 45f, Mathf.Clamp01(mousePos.y / Screen.height));
        float cursorYaw = Mathf.Lerp(-65f, 65f, Mathf.Clamp01(mousePos.x / Screen.width));
        Vector3 localDir = Quaternion.Euler(-cursorPitch, cursorYaw, 0f) * Vector3.forward;
        Vector3 worldDir = referenceRotation * localDir;

        Ray probeRay = new Ray(probeOrigin, worldDir);
        _lastProbeRay = probeRay;

        // Find the first canvas plane the ray intersects (no rect clamping)
        if (VrCanvasHitTester.RaycastCanvasPlanes(probeRay, out var hit))
        {
            _activeCanvas = hit.Canvas;
            _hasActiveCanvas = true;
            _lastCanvasName = hit.Canvas.name;
            _lastCursorTargetPos = hit.WorldPoint;
            VrCanvasHitTester.LastActiveCanvas = hit.Canvas;

            _cursor.transform.position = hit.WorldPoint;
            var rt = hit.Canvas.GetComponent<RectTransform>();
            _cursor.transform.rotation = Quaternion.LookRotation(rt.forward, rt.up);
        }
        else
        {
            _hasActiveCanvas = false;
            _activeCanvas = null;
            VrCanvasHitTester.LastActiveCanvas = null;
            _lastCanvasName = "(none)";
            _cursor.SetActive(false);
        }
    }

    private void UpdateCursorAnglesFromController()
    {
        var camera = UiCamera;
        if (camera == null) return;

        EnsureCursorCanvas(camera);
        if (_cursor == null || _cursorRectTransform == null)
            return;

        if (!_cursor.activeSelf)
        {
            _cursor.SetActive(true);
            _hasLastControllerRay = false;
        }

        Vector3 localDir = _controllerRotation * Vector3.forward;
        Ray probeRay = new Ray(_controllerOrigin, localDir);
        _lastProbeRay = probeRay;

        if (VrCanvasHitTester.RaycastCanvasPlanes(probeRay, out var hit))
        {
            // Angular dead-zone: suppress cursor update when the ray direction
            // hasn't moved enough, preventing idle jitter from shifting the cursor.
            if (_hasLastControllerRay)
            {
                float angleDeg = Vector3.Angle(_lastControllerRayLocalDir, localDir);
                if (angleDeg < ControllerDeadZoneDegrees)
                {
                    // Keep previous cursor position and canvas, but still update
                    // the probe ray for the laser visual.
                    _lastControllerRayLocalDir = localDir;
                    // return; // DISABLED: dead-zone + OneEuro filter kept deltas < 0.3 deg/frame, pinning cursor
                }
            }

            _hasLastControllerRay = true;
            _lastControllerRayLocalDir = localDir;

            _activeCanvas = hit.Canvas;
            _hasActiveCanvas = true;
            _lastCanvasName = hit.Canvas.name;
            _lastCursorTargetPos = hit.WorldPoint;
            VrCanvasHitTester.LastActiveCanvas = hit.Canvas;

            _cursor.transform.position = hit.WorldPoint;
            var rt = hit.Canvas.GetComponent<RectTransform>();
            _cursor.transform.rotation = Quaternion.LookRotation(rt.forward, rt.up);
        }
        else
        {
            _hasActiveCanvas = false;
            _activeCanvas = null;
            VrCanvasHitTester.LastActiveCanvas = null;
            _lastCanvasName = "(none)";
            _cursor.SetActive(false);
            _hasLastControllerRay = false;
        }
    }

    private Quaternion GetProjectionReferenceRotation()
    {
        if (_hasProjectionReferenceOverride)
        {
            return _projectionReferenceRotation;
        }

        var camera = UiCamera;
        return camera != null ? camera.transform.rotation : Quaternion.identity;
    }

    private Transform GetAnchorTransform()
    {
        if (_hasProjectionReferenceOverride)
        {
            return APIBus.CockpitHudReference?.transform;
        }
        return null;
    }
    
    private void EnsureCursorCanvas(Camera uiCaptureCamera)
    {
        if (_cursor != null)
        {
            if (_cursorImage != null)
            {
                _cursorImage.texture = _texture;
            }
            return;
        }

        _cursor = new GameObject("VrUiCursorCanvas");
        _cursor.transform.localScale = Vector3.one * CursorCanvasScale;
        _cursorCanvas = _cursor.AddComponent<Canvas>();
        _cursorCanvas.renderMode = RenderMode.WorldSpace;
        _cursorCanvas.planeDistance = Mathf.Max(uiCaptureCamera.nearClipPlane + 0.01f, 0.11f);
        _cursorCanvas.overrideSorting = true;
        _cursorCanvas.sortingOrder = short.MaxValue;
        _cursorCanvas.pixelPerfect = true;

        _cursorRectTransform = _cursor.GetComponent<RectTransform>();
        _cursorRectTransform.sizeDelta = new Vector2(CursorTextureSize, CursorTextureSize);
        _cursorImage = _cursor.AddComponent<RawImage>();
        _cursorImage.raycastTarget = false;
        _cursorImage.texture = _texture;
        _cursorImage.color = CursorNormalColor;
        LayerHelper.SetLayerRecursive(_cursor.transform, LayerHelper.GetVrUiLayer());
    }

    
    private void UpdateCursorAnimation(bool wasPressed, bool isPressed)
    {
        if (_cursor == null || _cursorImage == null) return;

        if (wasPressed)
        {
            _lastCursorClickTime = Time.unscaledTime;
        }

        var idlePulse = Mathf.Sin(Time.unscaledTime * CursorIdlePulseSpeed) * CursorIdlePulseScale;
        var clickProgress = Mathf.Clamp01((Time.unscaledTime - _lastCursorClickTime) / CursorClickPulseDuration);
        var clickPulse = clickProgress < 1f
            ? Mathf.Sin((1f - clickProgress) * Mathf.PI) * CursorClickPulseScale
            : 0f;

        var targetVisualScale = 1f + idlePulse + clickPulse;
        if (_cursorOverInteractive)
        {
            targetVisualScale *= CursorHoverScale;
        }
        if (isPressed)
        {
            targetVisualScale *= CursorPressedScale;
        }

        var targetScale = Vector3.one * (CursorCanvasScale * targetVisualScale);
        _cursor.transform.localScale = Vector3.Lerp(_cursor.transform.localScale, targetScale, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);

        var targetColor = CursorNormalColor;
        if (_cursorOverInteractive)
        {
            targetColor = CursorHoverColor;
        }
        if (isPressed)
        {
            targetColor = CursorPressedColor;
        }
        if (_runtimeMode == RuntimeInputMode.Mouse)
        {
            if (isPressed)
                targetColor = CursorMousePressedColor;
            else if (_cursorOverInteractive)
                targetColor = CursorMouseHoverColor;
            else
                targetColor = CursorMouseNormalColor;
        }

        _cursorImage.color = Color.Lerp(_cursorImage.color, targetColor, Time.unscaledDeltaTime * CursorAnimationLerpSpeed);
    }


    private static bool IsRealCursorVisible() => Cursor.visible && Cursor.lockState != CursorLockMode.Locked;

    private static Texture2D CreateCursorTexture()
    {
        var texture = new Texture2D(CursorTextureSize, CursorTextureSize, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var colors = new Color32[CursorTextureSize * CursorTextureSize];
        var center = new Vector2((CursorTextureSize - 1) * 0.5f, (CursorTextureSize - 1) * 0.5f);
        var innerRadius = CursorRingRadius - CursorRingThickness * 0.5f;
        var outerRadius = CursorRingRadius + CursorRingThickness * 0.5f;
        var transparent = new Color32(0, 0, 0, 0);

        for (var y = 0; y < CursorTextureSize; y++)
        {
            for (var x = 0; x < CursorTextureSize; x++)
            {
                var distanceFromCenter = Vector2.Distance(new Vector2(x, y), center);
                var isRing = distanceFromCenter >= innerRadius && distanceFromCenter <= outerRadius;
                colors[y * CursorTextureSize + x] = isRing ? Color.white : transparent;
            }
        }

        texture.SetPixels32(colors);
        texture.Apply();
        return texture;
    }
    
    private void LogRaycastAtCursor()
    {
        return;
        var camera = UiCamera;
        if (camera == null) return;

        var lines = new System.Collections.Generic.List<string>();
        lines.Add($"=== VR UI Diagnostics at {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        lines.Add("");

        var mouse = _realMouse;
        Vector2 mousePos = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
        lines.Add($"Mouse: screen=({mousePos.x:F0},{mousePos.y:F0}) norm=({mousePos.x / Screen.width:F4},{mousePos.y / Screen.height:F4})");
        lines.Add($"Screen: {Screen.width}x{Screen.height}");
        lines.Add("");

        lines.Add($"ProbeRay: origin={_lastProbeRay.origin:F3} dir={_lastProbeRay.direction:F3}");
        lines.Add($"Hit: {_hasActiveCanvas}");
        lines.Add($"ActiveCanvas: {_lastCanvasName}");
        lines.Add($"CursorTarget: {_lastCursorTargetPos:F3}");

        // Compare screen-space positions from different paths
        if (_cursor != null)
        {
            var cam = UiCamera;
            if (cam != null)
            {
                Vector3 fromCursor = cam.WorldToScreenPoint(_cursor.transform.position);
                Vector3 fromTarget = cam.WorldToScreenPoint(_lastCursorTargetPos);
                lines.Add($"ScreenPos (cursor): ({fromCursor.x:F1},{fromCursor.y:F1}) z={fromCursor.z:F3}");
                lines.Add($"ScreenPos (target): ({fromTarget.x:F1},{fromTarget.y:F1}) z={fromTarget.z:F3}");
        lines.Add($"cam.pixel: {cam.pixelWidth}x{cam.pixelHeight}  Screen: {Screen.width}x{Screen.height}");
            }

            // == Paired diagnostic: Snapshot A (feed time) vs Snapshot B (now, consume time) ==
            lines.Add("");
            lines.Add($"--- Snapshot A: VirtualMouse feed (frame {_feedFrame}) ---");
            lines.Add($"A_screenPoint: ({_feedScreenPoint.x:F4}, {_feedScreenPoint.y:F4})");
            lines.Add($"A_camPos: {_feedCameraPos:F4}  A_camRot: {_feedCameraRot:F4}");
            lines.Add($"A_proj[m00={_feedProjM00:F4} m11={_feedProjM11:F4} m02={_feedProjM02:F4} m12={_feedProjM12:F4}]");
            lines.Add($"A_cursorWorldPos: {_feedCursorWorldPos:F4}");

            int consumeFrame = Time.frameCount;
            lines.Add($"--- Snapshot B: diagnostic capture (frame {consumeFrame}) ---");
            var bCam = UiCamera;
            if (bCam != null)
            {
                Vector3 bCamPos = bCam.transform.position;
                Quaternion bCamRot = bCam.transform.rotation;
                var bProj = bCam.projectionMatrix;
                Vector3 bFreshScreen = bCam.WorldToScreenPoint(
                    _cursor != null ? _cursor.transform.position : Vector3.zero);
                lines.Add($"B_camPos: {bCamPos:F4}  B_camRot: {bCamRot:F4}");
                lines.Add($"B_proj[m00={bProj.m00:F4} m11={bProj.m11:F4} m02={bProj.m02:F4} m12={bProj.m12:F4}]");
                lines.Add($"B_freshScreenPoint: ({bFreshScreen.x:F4}, {bFreshScreen.y:F4}) z={bFreshScreen.z:F4}");
                lines.Add($"B_cursorWorldPos: {(_cursor != null ? _cursor.transform.position.ToString("F4") : "null")}");
            }
            lines.Add($"A_frame==B_frame: {_feedFrame == consumeFrame}");

            var bRealMouse = _realMouse;
            if (bRealMouse != null)
            {
                Vector2 bMousePos = bRealMouse.position.ReadValue();
                lines.Add($"B_realMousePos: ({bMousePos.x:F0},{bMousePos.y:F0})");
            }
            lines.Add("");
        }

        // Dump exactly what GraphicRaycaster sees at the fed screen point
        if (_activeCanvas != null && _hasActiveCanvas)
        {
            var raycaster = _activeCanvas.GetComponent<GraphicRaycaster>();
            var grCam = _activeCanvas.worldCamera;
            if (raycaster != null && grCam != null)
            {
                lines.Add($"--- GraphicRaycaster hit-test at fed screen point ({_feedScreenPoint.x:F1}, {_feedScreenPoint.y:F1}) ---");
                Ray screenRay = grCam.ScreenPointToRay(_feedScreenPoint);
                lines.Add($"ScreenPointToRay: origin={screenRay.origin:F4} dir={screenRay.direction:F4}");

                var ped = new PointerEventData(EventSystem.current)
                {
                    position = _feedScreenPoint
                };
                var results = new List<RaycastResult>();
                raycaster.Raycast(ped, results);
                if (results.Count > 0)
                {
                    lines.Add($"Hits: {results.Count}");
                    for (int i = 0; i < results.Count; i++)
                    {
                        var r = results[i];
                        lines.Add($"  [{i}] {r.gameObject?.name ?? "(null)"}" +
                                   $" screenPos=({r.screenPosition.x:F1},{r.screenPosition.y:F1})" +
                                   $" worldPos={r.worldPosition:F4}" +
                                   $" worldNorm={r.worldNormal:F4}" +
                                   $" distance={r.distance:F4}" +
                                   $" sort={r.sortingOrder}" +
                                   $" depth={r.depth}");
                    }
                }
                else
                {
                    lines.Add("Hits: 0 (no graphic at fed screen point)");
                }
                lines.Add("");
            }
        }

        // Input state
        lines.Add($"--- Input state ---");
        lines.Add($"RealMouse.current.pos: {Mouse.current?.position.ReadValue().ToString("F1") ?? "null"}");
        lines.Add($"Hovered: {(_hovered != null ? _hovered.name + " (" + (_hovered.transform.parent?.name ?? "(no parent)") + ")" : "(null)")}");
        lines.Add($"EventRoot: {(_hovered != null ? GetEventRoot(_hovered)?.name ?? "(null)" : "(null)")}");
        lines.Add($"PointerPress: {(_pointerPress != null ? _pointerPress.name : "(null)")}");
        lines.Add($"WasLeftDown: {_wasLeftDown}");
        if (_controllerModeActive)
        {
            lines.Add("--- Controller Input ---");
            lines.Add($"ControllerOrigin: {_controllerOrigin:F3}");
            lines.Add($"ControllerRotation: {_controllerRotation.eulerAngles:F3}");
            lines.Add($"ControllerDirection: {(_controllerRotation * Vector3.forward):F3}");
            lines.Add($"TriggerPressed: {_triggerIsPressed}");
        }
        var stdM = FindObjectOfType<StandaloneInputModule>();
        var ism = FindObjectOfType<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        lines.Add($"StandaloneInputModule: {(stdM != null ? (stdM.enabled ? "enabled" : "disabled") : "not found")}");
        lines.Add($"InputSystemUIInputModule: {(ism != null ? (ism.enabled ? "enabled" : "disabled") : "not found")}");
        lines.Add("");

        Quaternion refRot = GetProjectionReferenceRotation();
        lines.Add($"ReferenceRotation: euler={refRot.eulerAngles:F1} forward={refRot * Vector3.forward:F3}");
        lines.Add($"HasOverride: {_hasProjectionReferenceOverride}");
        lines.Add("");

        Transform anchor = GetAnchorTransform();
        lines.Add($"Anchor: {(anchor != null ? $"{anchor.name} pos={anchor.position:F3}" : "null (using camera)")}");
        lines.Add("");

        int idx = 0;
        string activeFwd = "";
        string activePos = "";
        string activeName = "";
        foreach (var c in VrCanvasHitTester.GetRegisteredCanvases())
        {
            if (c == null) continue;
            bool isActive = c == _activeCanvas;
            string diag = VrCanvasHitTester.DebugRaycast(c, _lastProbeRay);
            var rt = c.GetComponent<RectTransform>();
            string sz = rt != null ? rt.rect.size.ToString("F3") : "?";
            string pos = rt != null ? rt.position.ToString("F3") : "?";
            string fwd = rt != null ? rt.forward.ToString("F3") : "?";
            string wc = c.worldCamera?.name ?? "null";

            // Forward-orientation check: Dot(canvas.forward, canvasPos -> headPos)
            // uGUI convention: forward points AWAY from viewer → orientDot should be NEGATIVE.
            string orientCheck = "";
            if (rt != null)
            {
                Vector3 toViewer = _lastProbeRay.origin - rt.position;
                float orientDot = Vector3.Dot(rt.forward, toViewer.normalized);
                orientCheck = $" orientDot={orientDot:F3}";
                if (orientDot > 0f)
                    orientCheck += " *** FACING WRONG (positive = forward points toward viewer, UI is mirrored) ***";
                if (isActive)
                {
                    activeFwd = fwd;
                    activePos = pos;
                    activeName = c.name;
                }
            }

            // Negative-lossyScale warning (mirrored canvas flips rendered handedness)
            string scaleWarn = "";
            if (rt != null && rt.lossyScale.x < 0f != rt.lossyScale.y < 0f)
                scaleWarn = " *** NEGATIVE LOSSY SCALE DETECTED ***";

            lines.Add($"C{idx}: {c.name}{(isActive ? " <<< ACTIVE (green)" : "")}");
            lines.Add($"   Rect: sz={sz} pos={pos} fwd={fwd}{orientCheck}{scaleWarn}");
            lines.Add($"   worldCamera: {wc}");
            lines.Add($"   active: {c.gameObject.activeInHierarchy} raycast: {diag}");

            if (isActive)
            {
                activeFwd = fwd;
                activePos = pos;
                activeName = c.name;
            }

            // Show forward-dot vs other canvases when there are multiple
            foreach (var other in VrCanvasHitTester.GetRegisteredCanvases())
            {
                if (other == null || other == c) continue;
                var ort = other.GetComponent<RectTransform>();
                if (ort == null) continue;
                float fDot = Vector3.Dot(rt.forward, ort.forward);
                Vector3 pDelta = ort.position - rt.position;
                lines.Add($"   vs \"{other.name}\": forwardDot={fDot:F3} posDelta=({pDelta.x:F2},{pDelta.y:F2},{pDelta.z:F2})");
            }

            idx++;
        }

        // === DynamicMap diagnostics ===
        try
        {
            var dynamicMap = Object.FindObjectOfType<global::DynamicMap>();
            if (dynamicMap != null)
            {
                lines.Add("");
                lines.Add("--- DynamicMap ---");
                var registeredCanvases = VrCanvasHitTester.GetRegisteredCanvases();
                if (dynamicMap.mapImage != null)
                {
                    var mapImg = dynamicMap.mapImage.GetComponent<Image>();
                    if (mapImg != null)
                    {
                        var owningCanvas = mapImg.canvas;
                        bool isRegistered = owningCanvas != null && System.Linq.Enumerable.Contains(registeredCanvases, owningCanvas);
                        lines.Add($"mapImage.raycastTarget: {mapImg.raycastTarget}");
                        lines.Add($"mapImage.canvas.name: \"{owningCanvas?.name ?? "null"}\"");
                        lines.Add($"mapImage.canvas.hasGraphicRaycaster: {owningCanvas?.GetComponent<GraphicRaycaster>() != null}");
                        lines.Add($"mapImage.canvas.isRegistered: {isRegistered}");
                        lines.Add($"mapImage.canvas.renderMode: {owningCanvas?.renderMode}");
                        lines.Add($"mapImage.canvas.worldCamera: {owningCanvas?.worldCamera?.name ?? "null"}");
                    }
                }
                if (dynamicMap.mapBackground != null)
                {
                    var bgImg = dynamicMap.mapBackground.GetComponent<Image>();
                    if (bgImg != null)
                        lines.Add($"mapBackground.raycastTarget: {bgImg.raycastTarget}");
                }
                var dynCanvas = dynamicMap.GetComponent<Canvas>();
                if (dynCanvas != null)
                {
                    bool dynIsRegistered = System.Linq.Enumerable.Contains(registeredCanvases, dynCanvas);
                    lines.Add($"DynamicMap own Canvas: {dynCanvas.name} registered={dynIsRegistered} worldCamera={dynCanvas.worldCamera?.name ?? "null"}");
                }
                var dynCanvasInParent = dynamicMap.GetComponentInParent<Canvas>();
                if (dynCanvasInParent != null && dynCanvasInParent != dynCanvas)
                {
                    bool parentIsRegistered = System.Linq.Enumerable.Contains(registeredCanvases, dynCanvasInParent);
                    lines.Add($"DynamicMap parent Canvas: {dynCanvasInParent.name} registered={parentIsRegistered}");
                }
            }
            else
            {
                lines.Add("DynamicMap: not found");
            }
        }
        catch (System.Exception ex)
        {
            lines.Add($"DynamicMap diagnostics error: {ex.GetType().Name}: {ex.Message}");
        }

        lines.Add("");
        lines.Add("========================================");

        string output = string.Join("\n", lines);
        string filePath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop),
            $"vr-ui-diagnostics-{System.DateTime.Now:HHmmss}.txt");
        try
        {
            System.IO.File.WriteAllText(filePath, output);
            Debug.Log($"[VrUiCursor] Diagnostics written to {filePath}");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[VrUiCursor] Failed to write diagnostics: {ex}");
        }
    }

    private void ForwardMapClickIfNeeded()
    {
        if (_activeCanvas == null || !_hasActiveCanvas) return;
        if (_activeCanvas.name != "MapCanvas") return;

        var dynamicMap = Object.FindObjectOfType<global::DynamicMap>();
        if (dynamicMap == null) return;

        var mapImage = dynamicMap.mapImage;
        if (mapImage == null) return;
        var mapImageRect = mapImage.GetComponent<RectTransform>();
        if (mapImageRect == null) return;

        var rectSize = mapImageRect.rect.size;
        if (rectSize.x < 1f || rectSize.y < 1f) return;

        var camera = APIBus.CockpitHudCamera;
        if (camera == null) return;

        var cursorScreenPoint = GetScreenPoint();
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                mapImageRect, cursorScreenPoint, camera, out var cursorLocal))
        {
            return;
        }

        var cursorNorm = new Vector2(cursorLocal.x / rectSize.x, cursorLocal.y / rectSize.y);
        float maxRadius = ModConfiguration.Instance != null
            ? ModConfiguration.Instance.MapClickMaxRadius.Value
            : 0.05f;
        float maxRadiusSqr = maxRadius * maxRadius;

        var icons = UnityEngine.Object.FindObjectsOfType<global::MapIcon>();
        global::MapIcon? closest = null;
        float closestSqr = float.MaxValue;

        foreach (var icon in icons)
        {
            if (icon == null || !icon.gameObject.activeInHierarchy) continue;

            Vector2 iconScreenPoint = camera.WorldToScreenPoint(icon.transform.position);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    mapImageRect, iconScreenPoint, camera, out var iconLocal))
            {
                continue;
            }

            var iconNorm = new Vector2(iconLocal.x / rectSize.x, iconLocal.y / rectSize.y);
            float sqr = (iconNorm - cursorNorm).sqrMagnitude;

            if (sqr < closestSqr)
            {
                closestSqr = sqr;
                closest = icon;
            }
        }

        if (closest != null && closestSqr <= maxRadiusSqr)
            closest.ClickIcon(global::MapIcon.ClickSource.Mouse);
    }
}
