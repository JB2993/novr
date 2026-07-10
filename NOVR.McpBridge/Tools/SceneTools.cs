using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NOVR.McpBridge.Tools;

public static class SceneTools
{
    [McpTool("get_scene_hierarchy", "Returns the full GameObject tree of all loaded scenes.")]
    public static string GetSceneHierarchy(
        [McpParam(Name = "maxDepth", Description = "Maximum hierarchy depth to traverse (default: 0 = unlimited)")]
        int maxDepth = 0)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            sb.AppendLine($"Scene: {scene.name} (index={scene.buildIndex}, path={scene.path})");
            foreach (var root in scene.GetRootGameObjects())
            {
                AppendGameObject(sb, root, 1, maxDepth);
            }
        }

        return sb.ToString();
    }

    private static void AppendGameObject(StringBuilder sb, GameObject go, int depth, int maxDepth)
    {
        if (maxDepth > 0 && depth > maxDepth) return;

        var indent = new string(' ', depth * 2);
        var components = string.Join(", ", go.GetComponents<Component>()
            .Where(c => c != null)
            .Select(c => c.GetType().Name));

        var active = go.activeInHierarchy ? "" : " (inactive)";
        sb.AppendLine($"{indent}- {go.name}{active} [{components}]");

        for (var i = 0; i < go.transform.childCount; i++)
        {
            AppendGameObject(sb, go.transform.GetChild(i).gameObject, depth + 1, maxDepth);
        }
    }

    [McpTool("inspect_gameobject", "Returns detailed component data for a GameObject by name path.")]
    public static string InspectGameObject(
        [McpParam(Name = "name", Description = "GameObject name (substring match)")]
        string name,
        [McpParam(Name = "includeComponents", Description = "Include component field values", Required = false)]
        bool includeComponents = false)
    {
        var go = GameObject.Find(name) ?? Resources.FindObjectsOfTypeAll(typeof(GameObject))
            .Cast<GameObject>()
            .FirstOrDefault(o => o.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);

        if (go == null)
            return $"GameObject '{name}' not found.";

        var sb = new StringBuilder();
        sb.AppendLine($"GameObject: {go.name}");
        sb.AppendLine($"  Tag: {go.tag}");
        sb.AppendLine($"  Layer: {go.layer} ({LayerMask.LayerToName(go.layer)})");
        sb.AppendLine($"  Active: {go.activeInHierarchy}");
        sb.AppendLine($"  Scene: {go.scene.name}");
        sb.AppendLine($"  Position: {go.transform.position}");
        sb.AppendLine($"  Rotation: {go.transform.rotation.eulerAngles}");
        sb.AppendLine($"  Scale: {go.transform.localScale}");
        sb.AppendLine($"  Children: {go.transform.childCount}");

        if (!includeComponents) return sb.ToString();

        foreach (var component in go.GetComponents<Component>())
        {
            if (component == null) continue;
            sb.AppendLine($"  [{component.GetType().Name}]");
            var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(component);
                    sb.AppendLine($"    {field.Name} = {value ?? "null"}");
                }
                catch { sb.AppendLine($"    {field.Name} = <error>"); }
            }
        }

        return sb.ToString();
    }

    [McpTool("find_objects_by_type", "Finds all GameObject paths for a given Unity component type.")]
    public static string FindObjectsByType(
        [McpParam(Name = "typeName", Description = "Type name (e.g. Camera, MeshRenderer, Rigidbody)")]
        string typeName)
    {
        var sb = new StringBuilder();
        var count = 0;

        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(typeName, false, true))
            .FirstOrDefault(t => t != null);

        if (type == null) return $"Type '{typeName}' not found in any loaded assembly.";

        foreach (var obj in Resources.FindObjectsOfTypeAll(type))
        {
            if (obj is not Component component) continue;
            if (!component.gameObject.scene.isLoaded) continue;

            sb.AppendLine($"- {component.GetFullPath()}");
            count++;
        }

        return $"Found {count} object(s) of type '{typeName}':\n{sb}";
    }

    private static string GetFullPath(this Component component)
    {
        var sb = new StringBuilder(component.gameObject.name);
        var parent = component.transform.parent;
        while (parent != null)
        {
            sb.Insert(0, "/");
            sb.Insert(0, parent.name);
            parent = parent.parent;
        }
        return sb.ToString();
    }
}