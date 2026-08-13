using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework;

internal sealed class WorldEntityRegistry
{
	private static readonly Dictionary<Component, string> StableSignatures = new Dictionary<Component, string>();

	public readonly Dictionary<ulong, WorldEntityRecord> ById = new Dictionary<ulong, WorldEntityRecord>();

	private readonly Dictionary<int, ulong> byInstance = new Dictionary<int, ulong>();

	public static WorldEntityRegistry Current { get; private set; }

	public string SceneName { get; private set; }

	public int CollisionCount { get; private set; }

	public ulong Digest { get; private set; }

	public static WorldEntityRegistry Rebuild()
	{
		WorldEntityRegistry worldEntityRegistry = new WorldEntityRegistry();
		worldEntityRegistry.Scan();
		Current = worldEntityRegistry;
		return worldEntityRegistry;
	}

	private void Scan()
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		Scene activeScene = SceneManager.GetActiveScene();
		SceneName = ((Scene)(ref activeScene)).name;
		GameObject[] rootGameObjects = ((Scene)(ref activeScene)).GetRootGameObjects();
		foreach (GameObject val in rootGameObjects)
		{
			if (((Object)val).name.StartsWith("RemotePlayer_", StringComparison.Ordinal) || ((Object)val).name.StartsWith("DarkwoodMultiplayer", StringComparison.Ordinal))
			{
				continue;
			}
			RegisterMany<Character>(val.GetComponentsInChildren<Character>(true), WorldEntityKind.Enemy);
			RegisterMany<Door>(val.GetComponentsInChildren<Door>(true), WorldEntityKind.Door);
			RegisterMany<Window>(val.GetComponentsInChildren<Window>(true), WorldEntityKind.Window);
			RegisterMany<Item>(val.GetComponentsInChildren<Item>(true), WorldEntityKind.Item);
			Inventory[] componentsInChildren = val.GetComponentsInChildren<Inventory>(true);
			foreach (Inventory val2 in componentsInChildren)
			{
				if ((Object)(object)((Component)val2).GetComponentInParent<Player>() == (Object)null)
				{
					Register((Component)(object)val2, WorldEntityKind.Inventory);
				}
			}
		}
		List<ulong> list = new List<ulong>(ById.Keys);
		list.Sort();
		ulong num = 14695981039346656037uL;
		foreach (ulong item in list)
		{
			num ^= item;
			num *= 1099511628211L;
		}
		Digest = num;
		Plugin.Log.LogInfo((object)$"Entity registry: {ById.Count} entities, {CollisionCount} collisions, digest {Digest:X16}, scene {SceneName}.");
	}

	private void RegisterMany<T>(T[] components, WorldEntityKind kind) where T : Component
	{
		foreach (T val in components)
		{
			if ((Object)(object)val != (Object)null)
			{
				Register((Component)(object)val, kind);
			}
		}
	}

	private void Register(Component component, WorldEntityKind kind)
	{
		string text = BuildSignature(component, kind);
		ulong num = Fnv1a(text);
		int num2 = 0;
		while (ById.ContainsKey(num) && (Object)(object)ById[num].Component != (Object)(object)component)
		{
			CollisionCount++;
			int num3 = ++num2;
			num = Fnv1a(text + "#" + num3.ToString(CultureInfo.InvariantCulture));
		}
		WorldEntityRecord value = new WorldEntityRecord
		{
			Id = num,
			Kind = kind,
			Component = component,
			Signature = text
		};
		ById[num] = value;
		byInstance[((Object)component).GetInstanceID()] = num;
	}

	public bool TryGetId(Component component, out ulong id)
	{
		id = 0uL;
		if ((Object)(object)component == (Object)null)
		{
			return false;
		}
		if (byInstance.TryGetValue(((Object)component).GetInstanceID(), out id))
		{
			return true;
		}
		Component componentInParent = (Component)(object)component.GetComponentInParent<Inventory>();
		if ((Object)(object)componentInParent != (Object)null && byInstance.TryGetValue(((Object)componentInParent).GetInstanceID(), out id))
		{
			return true;
		}
		return false;
	}

	public bool TryGet(ulong id, out WorldEntityRecord record)
	{
		if (ById.TryGetValue(id, out record))
		{
			return (Object)(object)record.Component != (Object)null;
		}
		return false;
	}

	private static string BuildSignature(Component component, WorldEntityKind kind)
	{
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ea: Unknown result type (might be due to invalid IL or missing references)
		//IL_010e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_013e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0143: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d7: Unknown result type (might be due to invalid IL or missing references)
		SaveableObject val = component.GetComponent<SaveableObject>() ?? component.GetComponentInParent<SaveableObject>();
		Scene activeScene;
		byte b;
		if (!Core.mainMenu && Core.currentProfile != null && (Object)(object)val != (Object)null && val.uniqueId > 0)
		{
			string[] array = new string[9];
			activeScene = SceneManager.GetActiveScene();
			array[0] = ((Scene)(ref activeScene)).name;
			array[1] = "|";
			b = (byte)kind;
			array[2] = b.ToString();
			array[3] = "|";
			array[4] = ((object)component).GetType().FullName;
			array[5] = "|uid:";
			array[6] = val.uniqueId.ToString(CultureInfo.InvariantCulture);
			array[7] = "|";
			array[8] = RelativePath(((Component)val).transform, component.transform);
			string text = string.Concat(array);
			StableSignatures[component] = text;
			return text;
		}
		if (StableSignatures.TryGetValue(component, out var value))
		{
			return value;
		}
		Vector3 val2 = component.transform.position;
		Character val3 = (Character)(object)((component is Character) ? component : null);
		if ((Object)(object)val3 != (Object)null && ((Vector3)(ref val3.spawnPoint)).sqrMagnitude > 0.001f)
		{
			val2 = val3.spawnPoint;
		}
		string text2 = (((Object)(object)val != (Object)null) ? (val.prefabName ?? string.Empty) : string.Empty);
		string[] array2 = new string[15];
		activeScene = SceneManager.GetActiveScene();
		array2[0] = ((Scene)(ref activeScene)).name;
		array2[1] = "|";
		b = (byte)kind;
		array2[2] = b.ToString();
		array2[3] = "|";
		array2[4] = ((object)component).GetType().FullName;
		array2[5] = "|";
		array2[6] = text2;
		array2[7] = "|";
		array2[8] = HierarchyPath(component.transform);
		array2[9] = "|";
		array2[10] = Q(val2.x);
		array2[11] = ",";
		array2[12] = Q(val2.y);
		array2[13] = ",";
		array2[14] = Q(val2.z);
		string text3 = string.Concat(array2);
		StableSignatures[component] = text3;
		return text3;
	}

	private static string RelativePath(Transform root, Transform target)
	{
		if ((Object)(object)root == (Object)(object)target)
		{
			return ".";
		}
		List<string> list = new List<string>();
		Transform val = target;
		while ((Object)(object)val != (Object)null && (Object)(object)val != (Object)(object)root)
		{
			list.Add(((Object)val).name);
			val = val.parent;
		}
		list.Reverse();
		return string.Join("/", list.ToArray());
	}

	private static string HierarchyPath(Transform transform)
	{
		List<string> list = new List<string>();
		Transform val = transform;
		while ((Object)(object)val != (Object)null)
		{
			list.Add(((Object)val).name);
			val = val.parent;
		}
		list.Reverse();
		return string.Join("/", list.ToArray());
	}

	private static string Q(float value)
	{
		return Mathf.RoundToInt(value * 10f).ToString(CultureInfo.InvariantCulture);
	}

	private static ulong Fnv1a(string value)
	{
		ulong num = 14695981039346656037uL;
		byte[] bytes = Encoding.UTF8.GetBytes(value);
		foreach (byte b in bytes)
		{
			num ^= b;
			num *= 1099511628211L;
		}
		if (num != 0L)
		{
			return num;
		}
		return 1uL;
	}
}
