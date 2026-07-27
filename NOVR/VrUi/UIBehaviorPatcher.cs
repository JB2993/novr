using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace NOVR.VrUi;

public class UIBehaviorPatcher : NOVRBehaviour
{

    private static Dictionary<Type, Type> _patchMap = new()
    {
        { typeof(FlightHud), typeof(NOVRFlightHudBehavior) },
        { typeof(GameplayUI), typeof(NOVRGameplayUIBehaviour) },
        { typeof(MessageUI), typeof(NOVRGameplayUIBehaviour) },
        { typeof(StatusDisplay), typeof(NOVRStatusDisplayBehavior) },
        { typeof(global::DynamicMap), typeof(NOVRDynamicMapBehavior) },
        { typeof(AircraftSelectionMenu), typeof(NOVRAircraftSelectionMenuBehavior) },
        { typeof(LoadoutSelector), typeof(NOVRLoadoutSelectorBehavior) },
        { typeof(WeaponSelector), typeof(NOVRWeaponSelectorBehavior) },
    };

    private static Dictionary<string, List<Type>> _sceneLoadPatchMap = new() // We patch gameobjects by name the first time a scene is loaded (yes we iterate the tree recursively)
    {
        { "MainCanvas", new() { typeof(NOVRMainMenuBehavior) } },
        { "MenuCanvas", new() { typeof(NOVRGameplayUIBehaviour) } },
        { "BlackoutCanvas", new() { typeof(NOVRBlackoutCanvasBehavior) } }, //Template.ForCanvas("BlackoutCanvas", UiTranslationSpace.ScreenSpace, GameUiRegion.Absolute),
        { "SceneEssentials", new() { typeof(PositionZeroBehavior) } },
        { "Canvas", new() { typeof(PositionZeroBehavior) } },
    };


    private static Dictionary<Component, Type> _toPatch_component = new();
    private static Dictionary<string, List<Type>> _toPatch_name = new();
    private static List<GameObject> _toReactivate = new();


    static UIBehaviorPatcher()
    {
        SceneManager.sceneLoaded += SceneLoaded;
    }

    private static void SceneLoaded(Scene arg0, LoadSceneMode arg1)
    {
        Debug.Log("UIBehaviorPatcher: Scene loaded");
        foreach (var kvp in _sceneLoadPatchMap) _toPatch_name[kvp.Key] = new List<Type>(kvp.Value);
    }


    public static void DoPatching()
    {
        var harmony = new Harmony("novr.vrui.behavioradder");

        var postfixMethod = typeof(UIBehaviorPatcher).GetMethod(nameof(UniversalPostfix), BindingFlags.Static | BindingFlags.NonPublic);

        foreach (var entry in _patchMap)
        {
            Type targetType = entry.Key;

            ConstructorInfo ctor = targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

            if (ctor != null)
            {
                harmony.Patch(ctor, postfix: new HarmonyMethod(postfixMethod));
            }
            else
            {
                // Previously a silent skip, which made a missing behaviour impossible to
                // diagnose from the log. If this fires the type needs a different attach point.
                Debug.LogWarning(
                    $"UIBehaviorPatcher: no parameterless ctor found for {targetType.FullName} - " +
                    $"SKIPPED, {entry.Value.Name} will never be attached via the ctor hook");
            }
        }
    }

    static void UniversalPostfix(object __instance)
    {
        if (__instance is Component comp)
        {
            EnqueueForAddition(comp);
        }
    }

    // Adding components directly from the constructor scope is risky so we defer it till the next update
    static void EnqueueForAddition(Component comp)
    {
        // Was an exact-type indexer lookup, which throws KeyNotFoundException from inside a
        // Harmony constructor postfix if the game ever subclasses a patched type (the base ctor
        // still runs, but GetType() returns the derived type). Walk the hierarchy instead.
        for (var type = comp.GetType(); type != null; type = type.BaseType)
        {
            if (!_patchMap.TryGetValue(type, out var toAddComp)) continue;

            _toPatch_component[comp] = toAddComp;
            return;
        }

        Debug.LogWarning($"UIBehaviorPatcher: no _patchMap entry for {comp.GetType().FullName}");
    }


    private void Start()
    {
        foreach (var kvp in _sceneLoadPatchMap) _toPatch_name[kvp.Key] = new List<Type>(kvp.Value);
    }

    private void FixedUpdate()
    {
        if (_toReactivate.Count > 0)
        {
            foreach (var go in _toReactivate.Where(go => go != null))
            {
                Debug.Log($"UIBehaviorPatcher: Reactivating {go.name}");
                go.SetActive(true);
            }

            _toReactivate.Clear();
        }

        // Previously this loop `return`ed on the first component with an unresolved name, which
        // abandoned every remaining entry for that frame. It happened to recover because the
        // dictionary was only cleared after a full pass, but any component whose name never
        // resolved would permanently starve everything queued behind it. Defer the unresolved
        // ones individually and requeue them instead.
        var deferred = new List<KeyValuePair<Component, Type>>();

        foreach (var kvp in _toPatch_component)
        {
            var comp = kvp.Key;
            var toAdd = kvp.Value;

            if (comp == null) continue; // destroyed before we got to it

            if (string.IsNullOrEmpty(comp.name))
            {
                Debug.LogWarning($"UIBehaviorPatcher: {toAdd.Name} target not loaded fully, retrying next frame");
                deferred.Add(kvp);
                continue;
            }

            Debug.Log($"UIBehaviorPatcher: Adding {toAdd.Name} to {comp.name} (component patch)");
            if (!comp.gameObject.TryGetComponent(toAdd, out Component _))
            {
                AddAndBounceIfActive(comp.gameObject, toAdd);
            }
        }

        _toPatch_component.Clear();
        foreach (var kvp in deferred) _toPatch_component[kvp.Key] = kvp.Value;


        if (_toPatch_name.Count > 0)
        {
            foreach (var go in Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[])
            {

                var name = go.name;
                if (_toPatch_name.TryGetValue(name, out var toAddList))
                {
                    foreach (var toAdd in toAddList)
                    {
                        Debug.Log($"UIBehaviorPatcher: Adding {toAdd.Name} to {name} (name patch)");
                        if (!go.TryGetComponent(toAdd, out Component _)) AddAndBounceIfActive(go, toAdd);
                    }
                }
            }
            _toPatch_name.Clear();
        }
    }
    
    private static void AddAndBounceIfActive(GameObject go, Type toAdd)
    {
        var wasActive = go.activeInHierarchy;
        go.AddComponent(toAdd);

        if (!wasActive) return;
        
        Debug.Log($"UIBehaviorPatcher: Deactivating {go.name} for one frame to force lifecycle callbacks");
        go.SetActive(false);
        _toReactivate.Add(go);
    }
}
