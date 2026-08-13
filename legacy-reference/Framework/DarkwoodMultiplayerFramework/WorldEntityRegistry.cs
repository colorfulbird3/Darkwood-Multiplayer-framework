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
		Scene activeScene = SceneManager.GetActiveScene();
		SceneName = activeScene.name;
		GameObject[] rootGameObjects = activeScene.GetRootGameObjects();
		foreach (GameObject gameObject in rootGameObjects)
		{
			if (gameObject.name.StartsWith("RemotePlayer_", StringComparison.Ordinal) || gameObject.name.StartsWith("DarkwoodMultiplayer", StringComparison.Ordinal))
			{
				continue;
			}
			RegisterMany(gameObject.GetComponentsInChildren<Character>(includeInactive: true), WorldEntityKind.Enemy);
			RegisterMany(gameObject.GetComponentsInChildren<Door>(includeInactive: true), WorldEntityKind.Door);
			RegisterMany(gameObject.GetComponentsInChildren<Window>(includeInactive: true), WorldEntityKind.Window);
			RegisterMany(gameObject.GetComponentsInChildren<Item>(includeInactive: true), WorldEntityKind.Item);
			Inventory[] componentsInChildren = gameObject.GetComponentsInChildren<Inventory>(includeInactive: true);
			foreach (Inventory inventory in componentsInChildren)
			{
				if (inventory.GetComponentInParent<Player>() == null)
				{
					Register(inventory, WorldEntityKind.Inventory);
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
			if (val != null)
			{
				Register(val, kind);
			}
		}
	}

	private void Register(Component component, WorldEntityKind kind)
	{
		string text = BuildSignature(component, kind);
		ulong num = Fnv1a(text);
		int num2 = 0;
		while (ById.ContainsKey(num) && ById[num].Component != component)
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
		byInstance[component.GetInstanceID()] = num;
	}

	public bool TryGetId(Component component, out ulong id)
	{
		id = 0uL;
		if (component == null)
		{
			return false;
		}
		if (byInstance.TryGetValue(component.GetInstanceID(), out id))
		{
			return true;
		}
		Component componentInParent = component.GetComponentInParent<Inventory>();
		if (componentInParent != null && byInstance.TryGetValue(componentInParent.GetInstanceID(), out id))
		{
			return true;
		}
		return false;
	}

	public bool TryGet(ulong id, out WorldEntityRecord record)
	{
		if (ById.TryGetValue(id, out record))
		{
			return record.Component != null;
		}
		return false;
	}

	private static string BuildSignature(Component component, WorldEntityKind kind)
	{
		SaveableObject saveableObject = component.GetComponent<SaveableObject>() ?? component.GetComponentInParent<SaveableObject>();
		byte b;
		if (!Core.mainMenu && Core.currentProfile != null && saveableObject != null && saveableObject.uniqueId > 0)
		{
			string[] obj = new string[9]
			{
				SceneManager.GetActiveScene().name,
				"|",
				null,
				null,
				null,
				null,
				null,
				null,
				null
			};
			b = (byte)kind;
			obj[2] = b.ToString();
			obj[3] = "|";
			obj[4] = component.GetType().FullName;
			obj[5] = "|uid:";
			obj[6] = saveableObject.uniqueId.ToString(CultureInfo.InvariantCulture);
			obj[7] = "|";
			obj[8] = RelativePath(saveableObject.transform, component.transform);
			string text = string.Concat(obj);
			StableSignatures[component] = text;
			return text;
		}
		if (StableSignatures.TryGetValue(component, out var value))
		{
			return value;
		}
		Vector3 vector = component.transform.position;
		Character character = component as Character;
		if (character != null && character.spawnPoint.sqrMagnitude > 0.001f)
		{
			vector = character.spawnPoint;
		}
		string text2 = ((saveableObject != null) ? (saveableObject.prefabName ?? string.Empty) : string.Empty);
		string[] obj2 = new string[15]
		{
			SceneManager.GetActiveScene().name,
			"|",
			null,
			null,
			null,
			null,
			null,
			null,
			null,
			null,
			null,
			null,
			null,
			null,
			null
		};
		b = (byte)kind;
		obj2[2] = b.ToString();
		obj2[3] = "|";
		obj2[4] = component.GetType().FullName;
		obj2[5] = "|";
		obj2[6] = text2;
		obj2[7] = "|";
		obj2[8] = HierarchyPath(component.transform);
		obj2[9] = "|";
		obj2[10] = Q(vector.x);
		obj2[11] = ",";
		obj2[12] = Q(vector.y);
		obj2[13] = ",";
		obj2[14] = Q(vector.z);
		string text3 = string.Concat(obj2);
		StableSignatures[component] = text3;
		return text3;
	}

	private static string RelativePath(Transform root, Transform target)
	{
		if (root == target)
		{
			return ".";
		}
		List<string> list = new List<string>();
		Transform transform = target;
		while (transform != null && transform != root)
		{
			list.Add(transform.name);
			transform = transform.parent;
		}
		list.Reverse();
		return string.Join("/", list.ToArray());
	}

	private static string HierarchyPath(Transform transform)
	{
		List<string> list = new List<string>();
		Transform transform2 = transform;
		while (transform2 != null)
		{
			list.Add(transform2.name);
			transform2 = transform2.parent;
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
