using System.Collections.Generic;
using System.Globalization;
using System.Text;
using DarkwoodMultiplayerFramework.Core;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodEntityScanner
{
    public IEnumerable<Component> ScanScene()
    {
        var seen = new HashSet<int>();
        foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name.StartsWith("RemotePlayer_", System.StringComparison.Ordinal) ||
                root.name.StartsWith("DarkwoodMultiplayer", System.StringComparison.Ordinal))
                continue;

            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!(component is Character || component is Door || component is Window || component is Item || component is Inventory))
                    continue;
                if (component is Inventory && component.GetComponentInParent<Player>() != null)
                    continue;
                if (seen.Add(component.GetInstanceID()))
                    yield return component;
            }
        }
    }
    public EntityId ToPersistentId(Component component)
    {
        var saveable = component.GetComponent<SaveableObject>() ?? component.GetComponentInParent<SaveableObject>();
        string signature;
        if (!global::Core.mainMenu && global::Core.currentProfile != null && saveable != null && saveable.uniqueId > 0)
            signature = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "|" + component.GetType().FullName + "|uid:" + saveable.uniqueId.ToString(CultureInfo.InvariantCulture) + "|" + RelativePath(saveable.transform, component.transform);
        else
        {
            var p = component.transform.position;
            var enemy = component as Character;
            if (enemy != null && enemy.spawnPoint.sqrMagnitude > 0.001f) p = enemy.spawnPoint;
            signature = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "|" + component.GetType().FullName + "|" + HierarchyPath(component.transform) + "|" + Q(p.x) + "," + Q(p.y) + "," + Q(p.z);
        }
        return new EntityId(Fnv1a(signature), true);
    }
    private static string RelativePath(Transform root, Transform target) { var parts = new List<string>(); for (Transform? t = target; t != null && t != root; t = t.parent) parts.Add(t.name); parts.Reverse(); return parts.Count == 0 ? "." : string.Join("/", parts.ToArray()); }
    private static string HierarchyPath(Transform target) { var parts = new List<string>(); for (Transform? t = target; t != null; t = t.parent) parts.Add(t.name); parts.Reverse(); return string.Join("/", parts.ToArray()); }
    private static string Q(float value) => Mathf.RoundToInt(value * 10f).ToString(CultureInfo.InvariantCulture);
    private static ulong Fnv1a(string value) { ulong hash = 14695981039346656037UL; foreach (var b in Encoding.UTF8.GetBytes(value)) { hash ^= b; hash *= 1099511628211UL; } return hash == 0 ? 1UL : hash; }
}
