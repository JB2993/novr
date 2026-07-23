using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace NOVR;

public sealed class FramePacingDiagnostics : MonoBehaviour
{
    private const float LogIntervalSeconds = 1f;
    private const int FrameTimeBucketCount = 64;

    private readonly float[] _frameTimesMs = new float[FrameTimeBucketCount];
    private int _frameTimeIndex;
    private int _frameTimeCount;
    private int _frameCount;
    private int _beforeRenderCount;
    private int _beginFrameRenderingCount;
    private int _endFrameRenderingCount;
    private float _intervalStartRealtime;
    private float _lastRealtime;

    private static readonly FrameTiming[] FrameTimings = new FrameTiming[4];

    private void OnEnable()
    {
        Application.onBeforeRender += OnBeforeRender;
        RenderPipelineManager.beginFrameRendering += OnBeginFrameRendering;
        RenderPipelineManager.endFrameRendering += OnEndFrameRendering;
        ResetInterval();
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= OnBeforeRender;
        RenderPipelineManager.beginFrameRendering -= OnBeginFrameRendering;
        RenderPipelineManager.endFrameRendering -= OnEndFrameRendering;
    }

    private void Update()
    {
        var now = Time.realtimeSinceStartup;
        if (_lastRealtime > 0f)
        {
            RecordFrameTime((now - _lastRealtime) * 1000f);
        }

        _lastRealtime = now;
        _frameCount++;

        if (now - _intervalStartRealtime >= LogIntervalSeconds)
        {
            LogInterval(now);
            ResetInterval();
        }
    }

    private void OnBeforeRender()
    {
        _beforeRenderCount++;
    }

    private void OnBeginFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        _beginFrameRenderingCount++;
    }

    private void OnEndFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        _endFrameRenderingCount++;
    }

    private void RecordFrameTime(float frameTimeMs)
    {
        _frameTimesMs[_frameTimeIndex] = frameTimeMs;
        _frameTimeIndex = (_frameTimeIndex + 1) % _frameTimesMs.Length;
        if (_frameTimeCount < _frameTimesMs.Length)
        {
            _frameTimeCount++;
        }
    }

    private void LogInterval(float now)
    {
        var elapsed = Mathf.Max(0.0001f, now - _intervalStartRealtime);
        var fps = _frameCount / elapsed;
        var avgMs = AverageFrameTime();
        var maxMs = MaxFrameTime();
        var cameraSummary = BuildCameraSummary();
        var xrSummary = BuildXrSummary();
        var frameTimingSummary = BuildFrameTimingSummary();

        Debug.Log(
            $"[NOVR] Frame pacing diagnostics: " +
            $"fps={fps:0.0}, avgFrame={avgMs:0.00}ms, maxFrame={maxMs:0.00}ms, " +
            $"frames={_frameCount}, beforeRender={_beforeRenderCount}, " +
            $"beginFrameRendering={_beginFrameRenderingCount}, endFrameRendering={_endFrameRenderingCount}, " +
            $"{xrSummary}, {cameraSummary}, {frameTimingSummary}");
    }

    private float AverageFrameTime()
    {
        if (_frameTimeCount == 0) return 0f;

        var total = 0f;
        for (var index = 0; index < _frameTimeCount; index++)
        {
            total += _frameTimesMs[index];
        }

        return total / _frameTimeCount;
    }

    private float MaxFrameTime()
    {
        var max = 0f;
        for (var index = 0; index < _frameTimeCount; index++)
        {
            if (_frameTimesMs[index] > max)
            {
                max = _frameTimesMs[index];
            }
        }

        return max;
    }

    private static string BuildXrSummary()
    {
        return
            $"xrEnabled={SafeValue(() => XRSettings.enabled.ToString())}, " +
            $"xrActive={SafeValue(() => XRSettings.isDeviceActive.ToString())}, " +
            $"xrDevice='{SafeValue(() => XRSettings.loadedDeviceName)}', " +
            $"eyeTexture={SafeValue(() => $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}")}, " +
            $"viewportScale={SafeValue(() => XRSettings.renderViewportScale.ToString("0.###"))}, " +
            $"targetFps={Application.targetFrameRate}, " +
            $"vSync={QualitySettings.vSyncCount}, " +
            $"fixedDt={Time.fixedDeltaTime:0.0000}";
    }

    private static string BuildCameraSummary()
    {
        var cameras = new Camera[Camera.allCamerasCount];
        Camera.GetAllCameras(cameras);

        var enabledCount = 0;
        var stereoBothCount = 0;
        var stackishCount = 0;
        var builder = new StringBuilder();

        for (var index = 0; index < cameras.Length; index++)
        {
            var camera = cameras[index];
            if (camera == null || !camera.enabled) continue;

            enabledCount++;
            if (camera.stereoTargetEye == StereoTargetEyeMask.Both)
            {
                stereoBothCount++;
            }

            if (camera.depth > 0f || camera.clearFlags == CameraClearFlags.Depth)
            {
                stackishCount++;
            }

            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder
                .Append(camera.name)
                .Append(":depth=")
                .Append(camera.depth.ToString("0.#"))
                .Append(",eye=")
                .Append(camera.stereoTargetEye)
                .Append(",clear=")
                .Append(camera.clearFlags)
                .Append(",mask=0x")
                .Append(camera.cullingMask.ToString("X"));
        }

        return $"cameras={enabledCount}/{cameras.Length}, stereoBoth={stereoBothCount}, stackish={stackishCount}, cameraList=[{builder}]";
    }

    private static string BuildFrameTimingSummary()
    {
        var capturedCount = 0u;
        try
        {
            FrameTimingManager.CaptureFrameTimings();
            capturedCount = FrameTimingManager.GetLatestTimings((uint)FrameTimings.Length, FrameTimings);
        }
        catch
        {
            return "frameTiming=unavailable";
        }

        if (capturedCount == 0)
        {
            return "frameTiming=none";
        }

        var timing = FrameTimings[0];
        return
            $"frameTiming=cpuFrame={timing.cpuFrameTime:0.00}ms, " +
            $"cpuMain={timing.cpuMainThreadFrameTime:0.00}ms, " +
            $"cpuRender={timing.cpuRenderThreadFrameTime:0.00}ms, " +
            $"gpu={timing.gpuFrameTime:0.00}ms";
    }

    private static string SafeValue(System.Func<string> getValue)
    {
        try
        {
            return getValue();
        }
        catch
        {
            return "unavailable";
        }
    }

    private void ResetInterval()
    {
        _intervalStartRealtime = Time.realtimeSinceStartup;
        _lastRealtime = _intervalStartRealtime;
        _frameCount = 0;
        _beforeRenderCount = 0;
        _beginFrameRenderingCount = 0;
        _endFrameRenderingCount = 0;
        _frameTimeIndex = 0;
        _frameTimeCount = 0;
    }
}
