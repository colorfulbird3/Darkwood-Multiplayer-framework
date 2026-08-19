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
                if (component is Character && component.GetComponentInParent<Player>() != null)
                    continue;
                if (component is Inventory && component.GetComponentInParent<Player>() != null)
                    continue;
                if (seen.Add(component.GetInstanceID()))
                    yield return component;
            }
        }
    }
    /// <summary>客户端本地候选描述符（无网络身份）。匹配由 EntityBindingMatcher 完成。
    /// components 与返回数组按同一顺序对齐（供绑定使用）。</summary>
    public DarkwoodMultiplayerFramework.Protocol.LocalEntityCandidate[] BuildLocalCandidates(out Component[] components)
    {
        var list = new System.Collections.Generic.List<DarkwoodMultiplayerFramework.Protocol.LocalEntityCandidate>();
        var comps = new System.Collections.Generic.List<Component>();
        foreach (var c in ScanScene())
        {
            var saveable = c.GetComponent<SaveableObject>() ?? c.GetComponentInParent<SaveableObject>();
            var uid = saveable != null && saveable.uniqueId > 0 ? saveable.uniqueId : 0L;
            var path = saveable != null ? RelativePath(saveable.transform, c.transform) : string.Empty;
            var p = c.transform.position;
            list.Add(new DarkwoodMultiplayerFramework.Protocol.LocalEntityCandidate(c.GetType().Name, uid, path, c.name, p.x, p.y, p.z));
            comps.Add(c);
        }
        components = comps.ToArray();
        return list.ToArray();
    }

    /// <summary>主机权威描述符（构建 BindingManifest 用）。</summary>
    public DarkwoodMultiplayerFramework.Protocol.EntityBindingEntryWire[] BuildAuthoritativeDescriptors(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<DarkwoodMultiplayerFramework.Core.EntityId, Component>> entities)
    {
        var list = new System.Collections.Generic.List<DarkwoodMultiplayerFramework.Protocol.EntityBindingEntryWire>();
        foreach (var pair in entities)
        {
            var component = pair.Value;
            if (component == null || component.gameObject == null) continue;
            var saveable = component.GetComponent<SaveableObject>() ?? component.GetComponentInParent<SaveableObject>();
            var uid = saveable != null && saveable.uniqueId > 0 ? saveable.uniqueId : 0L;
            var path = saveable != null ? RelativePath(saveable.transform, component.transform) : string.Empty;
            var p = component.transform.position;
            list.Add(new DarkwoodMultiplayerFramework.Protocol.EntityBindingEntryWire(pair.Key.Value, DarkwoodEntityStateAdapter.Kind(component), component.GetType().Name, uid, path, component.name, p.x, p.y, p.z));
        }
        return list.ToArray();
    }

    public EntityId ToPersistentId(Component component)
    {
        var saveable = component.GetComponent<SaveableObject>() ?? component.GetComponentInParent<SaveableObject>();
        var componentOrdinal = ComponentOrdinal(component);
        string signature;
        if (!global::Core.mainMenu && global::Core.currentProfile != null && saveable != null && saveable.uniqueId > 0)
            signature = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "|" + component.GetType().FullName + "|uid:" + saveable.uniqueId.ToString(CultureInfo.InvariantCulture) + "|" + RelativePath(saveable.transform, component.transform) + "|component:" + componentOrdinal.ToString(CultureInfo.InvariantCulture);
        else
        {
            var p = component.transform.position;
            var enemy = component as Character;
            if (enemy != null && enemy.spawnPoint.sqrMagnitude > 0.001f) p = enemy.spawnPoint;
            signature = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "|" + component.GetType().FullName + "|" + HierarchyPath(component.transform) + "|component:" + componentOrdinal.ToString(CultureInfo.InvariantCulture) + "|" + Q(p.x) + "," + Q(p.y) + "," + Q(p.z);
        }
        return new EntityId(Fnv1a(signature), true);
    }
    private static string RelativePath(Transform root, Transform target) { var parts = new List<string>(); for (Transform? t = target; t != null && t != root; t = t.parent) parts.Add(IndexedName(t)); parts.Reverse(); return parts.Count == 0 ? "." : string.Join("/", parts.ToArray()); }
    private static string HierarchyPath(Transform target) { var parts = new List<string>(); for (Transform? t = target; t != null; t = t.parent) parts.Add(IndexedName(t)); parts.Reverse(); return string.Join("/", parts.ToArray()); }
    private static string IndexedName(Transform target)
    {
        var ordinal=0;var parent=target.parent;
        if(parent!=null)for(var i=0;i<target.GetSiblingIndex();i++)if(parent.GetChild(i).name==target.name)ordinal++;
        else foreach(var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects()){if(root.transform==target)break;if(root.name==target.name)ordinal++;}
        return target.name+"#"+ordinal.ToString(CultureInfo.InvariantCulture);
    }
    private static int ComponentOrdinal(Component component)
    {
        var ordinal=0;foreach(var candidate in component.gameObject.GetComponents(component.GetType())){if(ReferenceEquals(candidate,component))return ordinal;ordinal++;}return ordinal;
    }
    private static string Q(float value) => Mathf.RoundToInt(value * 10f).ToString(CultureInfo.InvariantCulture);
    private static ulong Fnv1a(string value) { ulong hash = 14695981039346656037UL; foreach (var b in Encoding.UTF8.GetBytes(value)) { hash ^= b; hash *= 1099511628211UL; } return hash == 0 ? 1UL : hash; }
}
