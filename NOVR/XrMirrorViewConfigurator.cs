using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;

namespace NOVR;

internal static class XrMirrorViewConfigurator
{
    private static bool _attempted;

    public static void Apply()
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        var disabledAny = false;
        disabledAny |= TrySetXrSettingsShowDeviceView(false);
        disabledAny |= TrySetOpenXrSkipPresent(true);

        Debug.Log(disabledAny
            ? "[NOVR] Requested XR mirror/native flatscreen output suppression."
            : "[NOVR] Could not find a runtime API for suppressing XR mirror/native flatscreen output.");
    }

    private static bool TrySetXrSettingsShowDeviceView(bool value)
    {
        try
        {
            var property = typeof(XRSettings).GetProperty("showDeviceView", BindingFlags.Public | BindingFlags.Static);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            property.SetValue(null, value, null);
            Debug.Log($"[NOVR] Set XRSettings.showDeviceView={value}.");
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[NOVR] Failed to set XRSettings.showDeviceView: {exception.Message}");
            return false;
        }
    }

    private static bool TrySetOpenXrSkipPresent(bool value)
    {
        try
        {
            var settings = OpenXRSettings.Instance;
            if (settings == null)
            {
                return false;
            }

            return TrySetBooleanMember(settings, "skipPresentToMainScreen", value) ||
                   TrySetBooleanMember(settings, "skipPresentToMainScreenEnabled", value) ||
                   TrySetBooleanMember(settings, "disableMirrorView", value) ||
                   TrySetBooleanMember(settings, "m_skipPresentToMainScreen", value) ||
                   TrySetBooleanMember(settings, "m_disableMirrorView", value);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[NOVR] Failed to configure OpenXR mirror/native flatscreen output: {exception.Message}");
            return false;
        }
    }

    private static bool TrySetBooleanMember(object target, string memberName, bool value)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var type = target.GetType();

        var property = type.GetProperty(memberName, flags);
        if (property != null && property.PropertyType == typeof(bool) && property.CanWrite)
        {
            property.SetValue(target, value, null);
            Debug.Log($"[NOVR] Set OpenXRSettings.{memberName}={value}.");
            return true;
        }

        var field = type.GetField(memberName, flags);
        if (field != null && field.FieldType == typeof(bool))
        {
            field.SetValue(target, value);
            Debug.Log($"[NOVR] Set OpenXRSettings.{memberName}={value}.");
            return true;
        }

        return false;
    }
}
