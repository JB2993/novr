using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace NOVR.VrUi;

[DefaultExecutionOrder(-1000)]
public class VrUiCursor: NOVRBehaviour
{
    public static VrUiCursor? Instance { get; private set; }
    public static VrUiCursor? I => Instance;

    public bool IsActive => _cursor != null && _cursor.activeSelf;
    public Vector3 CursorPosition => _cursor != null ? _cursor.transform.position : Vector3.zero;

    protected override void Awake()
    {
        base.Awake();
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
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
    private static readonly bool _showDebugOverlay = true;
    private Ray _lastProbeRay;
    private Vector3 _lastCursorTargetPos;
    private string _lastCanvasName = "";
    private Texture2D? _debugDotTexture;
    private Text? _debugText;
    private readonly List<LineRenderer> _debugBorders = new();
    private readonly List<LineRenderer> _debugCanvasBorders = new();
    private LineRenderer? _debugProbeRay;
    private LineRenderer? _debugHitCross;
    private LineRenderer? _debugAxisX;
    private LineRenderer? _debugAxisY;
    private LineRenderer? _debugAxisZ;
    private Material? _debugLineMaterial;
    
    // Paired-diagnostic snapshots for VirtualMouse feed-vs-consume debugging
    private int _feedFrame;
    private Vector2 _feedScreenPoint;
    private Vector3 _feedCameraPos;
    private Quaternion _feedCameraRot;
    private float _feedProjM00, _feedProjM11, _feedProjM02, _feedProjM12;
    private Vector3 _feedCursorWorldPos;

    // Direct pointer event state
    private PointerEventData? _pointerEventData;
    private GameObject? _hovered;
    private GameObject? _pointerPress;
    private bool _wasLeftDown;

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
    }

    private void Update()
    {
        if (!Application.isFocused)
        {
            if (_cursor != null && _cursor.activeSelf)
            {
                _cursor.SetActive(false);
            }
            return;
        }

        if (!IsRealCursorVisible()) // This means we don't have to manually show and hide it every game update
        {
            if (_cursor != null)
            {
                _cursor.SetActive(false);
            }
            return;
        }
        
        if (_realMouse == null)
        {
            _realMouse = Mouse.current ?? throw new System.InvalidOperationException(
                $"[{nameof(VrUiCursor)}] Unity InputSystem could not find an active hardware Mouse device during initialization.");
        }

        if (Time.frameCount < 120)
        {
            DisableStandardUIModule();
        }
        if (_texture == null) return;
        UpdateCursorAngles();
        
        var realMouse = _realMouse;
        if (realMouse == null) return;

        Vector2 screenPoint = GetScreenPoint();
        if (!_isOffscreen)
        {
            // Snapshot A — stamp feed-time state for the paired diagnostic
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

            // Direct pointer events via GraphicRaycaster + ExecuteEvents
            FirePointerEvents(screenPoint, realMouse.leftButton.isPressed);
        }

        UpdateCursorAnimation(realMouse);

        if (realMouse.leftButton.wasPressedThisFrame)
        {
            LogRaycastAtCursor();
        }

        if (_showDebugOverlay)
            UpdateDebugOverlay();
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

        var stdModule = FindObjectOfType<StandaloneInputModule>();
        if (stdModule != null)
        {
            Debug.Log($"[NOVR] Disabling {stdModule.GetType().Name} so VR cursor drives UI exclusively.");
            stdModule.enabled = false;
            foundAny = true;
        }

        var inputSystemModule = FindObjectOfType<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        if (inputSystemModule != null)
        {
            Debug.Log($"[NOVR] Disabling {inputSystemModule.GetType().Name} so VR cursor drives UI exclusively.");
            inputSystemModule.enabled = false;
            foundAny = true;
        }

        if (!foundAny)
        {
            Debug.LogWarning("[NOVR] No UI InputModule found in scene yet, retrying next frame...");
        }
        return foundAny;
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

            _cursor.transform.position = hit.WorldPoint;
            var rt = hit.Canvas.GetComponent<RectTransform>();
            _cursor.transform.rotation = Quaternion.LookRotation(rt.forward, rt.up);
        }
        else
        {
            _hasActiveCanvas = false;
            _activeCanvas = null;
            _lastCanvasName = "(none)";
            _cursor.SetActive(false);
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

    
    private void UpdateCursorAnimation(Mouse realMouse)
    {
        if (_cursor == null || _cursorImage == null) return;

        if (realMouse.leftButton.wasPressedThisFrame)
        {
            _lastCursorClickTime = Time.unscaledTime;
        }

        var isPressed = realMouse.leftButton.isPressed;
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
    
    private LineRenderer? CreateDebugLine(float width, Color color, string name, int positionCount = 2)
    {
        if (_cursorCanvas == null) return null;
        var go = new GameObject(name);
        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());
        var lr = go.AddComponent<LineRenderer>();
        lr.positionCount = positionCount;
        lr.startWidth = width;
        lr.endWidth = width;
        lr.useWorldSpace = true;
        lr.loop = false;
        lr.startColor = color;
        lr.endColor = color;
        if (_debugLineMaterial != null) lr.sharedMaterial = _debugLineMaterial;
        return lr;
    }

    private void EnsureDebugObjects()
    {
        if (_debugText != null || _cursorCanvas == null) return;

        var textGo = new GameObject("CursorDebugText");
        textGo.transform.SetParent(_cursorCanvas.transform, false);

        _debugText = textGo.AddComponent<Text>();
        _debugText.fontSize = 24;
        _debugText.color = new Color(0.2f, 1f, 0.2f);
        _debugText.alignment = TextAnchor.UpperLeft;
        _debugText.horizontalOverflow = HorizontalWrapMode.Wrap;
        _debugText.verticalOverflow = VerticalWrapMode.Overflow;
        _debugText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        var rt = textGo.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(700, 300);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchorMin = new Vector2(1f, 0.5f);
        rt.anchorMax = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(20f, 0f);

        textGo.GetComponent<Graphic>().raycastTarget = false;

        // Probe ray (thick, yellow/red depending on hit)
        _debugProbeRay = CreateDebugLine(0.02f, Color.yellow, "DebugProbeRay");

        // Hit cross (thick, white) — 3 segments: horizontal, vertical
        _debugHitCross = CreateDebugLine(0.02f, Color.white, "DebugHitCross", 3);

        // Axis: X (red), Y (green), Z (blue) at probe origin — one line each
        _debugAxisX = CreateDebugLine(0.008f, Color.red, "DebugAxisX");
        _debugAxisY = CreateDebugLine(0.008f, Color.green, "DebugAxisY");
        _debugAxisZ = CreateDebugLine(0.008f, Color.blue, "DebugAxisZ");
    }

    private void EnsureDebugLineMaterial()
    {
        if (_debugLineMaterial != null) return;
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            _debugLineMaterial = new Material(shader);
            _debugLineMaterial.color = Color.white;
        }
    }

    private void UpdateDebugOverlay()
    {
        if (_cursorCanvas == null) return;
        EnsureDebugLineMaterial();
        EnsureDebugObjects();
        if (_debugText == null) return;

        var mouse = _realMouse;
        Vector2 mousePos = mouse != null ? mouse.position.ReadValue() : Vector2.zero;

        // Collect canvas metrics + raycast diagnostic per canvas
        string canvasInfo = "";
        int idx = 0;
        foreach (var c in VrCanvasHitTester.GetRegisteredCanvases())
        {
            if (c == null) continue;
            string diag = VrCanvasHitTester.DebugRaycast(c, _lastProbeRay);
            var crt = c.GetComponent<RectTransform>();
            string sz = crt != null ? crt.rect.size.ToString("F3") : "?";
            canvasInfo += $"\nC{idx}:{c.name} sz:{sz} -> {diag}";
            idx++;
        }

        _debugText.text =
            $"Act:{_lastCanvasName}  Hit:{_hasActiveCanvas}  Cvs:{VrCanvasHitTester.GetRegisteredCanvasCount()}\n" +
            $"M:({mousePos.x:F0},{mousePos.y:F0})  N:({mousePos.x / Screen.width:F3},{mousePos.y / Screen.height:F3})\n" +
            $"W:{_lastCursorTargetPos:F2}\n" +
            $"R:{GetProjectionReferenceRotation().eulerAngles:F1}  A:{_hasProjectionReferenceOverride}" +
            canvasInfo;

        _debugText.color = _hasActiveCanvas
            ? new Color(0.2f, 1f, 0.2f)
            : new Color(1f, 0.3f, 0.3f);

        DrawClickableBorders();
        DrawCanvasBorders();
        DrawProbeRay();
        DrawHitCross();
        DrawProbeOriginAxis();
    }

    private static LineRenderer? SetLine(LineRenderer? lr, Vector3[] positions, Color color)
    {
        if (lr == null) return null;
        lr.gameObject.SetActive(true);
        lr.startColor = color;
        lr.endColor = color;
        lr.positionCount = positions.Length;
        lr.SetPositions(positions);
        return lr;
    }

    private void DrawProbeRay()
    {
        if (_debugProbeRay == null) return;
        float rayLen = 10f;
        Vector3 end = _lastProbeRay.origin + _lastProbeRay.direction * rayLen;
        Vector3 hitEnd = _hasActiveCanvas ? _lastCursorTargetPos : end;
        SetLine(_debugProbeRay, new[] { _lastProbeRay.origin, hitEnd },
            _hasActiveCanvas ? Color.green : Color.yellow);
    }

    private void DrawHitCross()
    {
        if (_debugHitCross == null) return;
        float arm = 0.05f;
        Vector3 p = _lastCursorTargetPos;
        SetLine(_debugHitCross, new[]
        {
            p + Vector3.left * arm, p + Vector3.right * arm,
            p + Vector3.up * arm, p + Vector3.down * arm,
        }, Color.white);
    }

    private void DrawProbeOriginAxis()
    {
        Vector3 origin = _lastProbeRay.origin;
        float arm = 0.15f;
        if (_debugAxisX != null)
            SetLine(_debugAxisX, new[] { origin, origin + Vector3.right * arm }, Color.red);
        if (_debugAxisY != null)
            SetLine(_debugAxisY, new[] { origin, origin + Vector3.up * arm }, Color.green);
        if (_debugAxisZ != null)
            SetLine(_debugAxisZ, new[] { origin, origin + Vector3.forward * arm }, Color.blue);
    }

    private void DrawCanvasBorders()
    {
        var canvases = VrCanvasHitTester.GetRegisteredCanvases();
        while (_debugCanvasBorders.Count < canvases.Count)
        {
            var go = new GameObject($"DebugCanvasBorder_{_debugCanvasBorders.Count}");
            LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 5;
            lr.startWidth = 0.01f;
            lr.endWidth = 0.01f;
            lr.useWorldSpace = true;
            lr.loop = false;
            if (_debugLineMaterial != null) lr.sharedMaterial = _debugLineMaterial;
            _debugCanvasBorders.Add(lr);
        }

        for (int i = 0; i < _debugCanvasBorders.Count; i++)
        {
            if (i < canvases.Count)
            {
                var c = canvases[i];
                if (c == null || !c.gameObject.activeInHierarchy)
                {
                    _debugCanvasBorders[i].gameObject.SetActive(false);
                    continue;
                }
                var rt = c.GetComponent<RectTransform>();
                if (rt == null)
                {
                    _debugCanvasBorders[i].gameObject.SetActive(false);
                    continue;
                }
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                bool isActive = c == _activeCanvas;
                SetLine(_debugCanvasBorders[i], new[] { corners[0], corners[1], corners[2], corners[3], corners[0] },
                    isActive ? new Color(0f, 1f, 0f, 0.6f) : new Color(1f, 0f, 1f, 0.4f));
            }
            else
            {
                _debugCanvasBorders[i].gameObject.SetActive(false);
            }
        }
    }

    private void DrawClickableBorders()
    {
        var elements = new List<(RectTransform rt, Canvas canvas)>();
        foreach (var canvas in VrCanvasHitTester.GetRegisteredCanvases())
        {
            if (canvas == null || !canvas.gameObject.activeInHierarchy) continue;
            var selectables = canvas.GetComponentsInChildren<Selectable>(false);
            foreach (var sel in selectables)
            {
                if (sel == null || !sel.gameObject.activeInHierarchy) continue;
                var rt = sel.GetComponent<RectTransform>();
                if (rt != null)
                    elements.Add((rt, canvas));
            }
        }

        while (_debugBorders.Count < elements.Count)
        {
            var go = new GameObject($"DebugBorder_{_debugBorders.Count}");
            LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());
            var lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 5;
            lr.startWidth = 0.003f;
            lr.endWidth = 0.003f;
            lr.useWorldSpace = true;
            lr.loop = false;
            if (_debugLineMaterial != null) lr.sharedMaterial = _debugLineMaterial;
            _debugBorders.Add(lr);
        }

        for (int i = 0; i < _debugBorders.Count; i++)
        {
            if (i < elements.Count)
            {
                var (rt, canvas) = elements[i];
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);

                bool isActive = canvas == _activeCanvas;
                _debugBorders[i].gameObject.SetActive(true);
                _debugBorders[i].SetPositions(new Vector3[]
                {
                    corners[0], corners[1], corners[2], corners[3], corners[0]
                });
                _debugBorders[i].startColor = isActive ? Color.green : Color.cyan;
                _debugBorders[i].endColor = isActive ? Color.green : Color.cyan;
            }
            else
            {
                _debugBorders[i].gameObject.SetActive(false);
            }
        }
    }
}
