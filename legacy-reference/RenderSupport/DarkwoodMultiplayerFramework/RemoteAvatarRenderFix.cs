using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public static class RemoteAvatarRenderFix
{
	public static void Configure(int playerId, GameObject root, Player localPlayer)
	{
		if (root == null || localPlayer == null)
		{
			return;
		}
		int layer = localPlayer.gameObject.layer;
		if (localPlayer.torsoAnimator != null)
		{
			layer = localPlayer.torsoAnimator.gameObject.layer;
		}
		SetLayerRecursive(root, layer);
		root.SetActive(value: true);
		ConfigureVisualClone(root, localPlayer, (localPlayer.torsoAnimator != null) ? localPlayer.torsoAnimator.gameObject : null);
		ConfigureVisualClone(root, localPlayer, localPlayer.legs);
		if (!HasModelRenderer(root))
		{
			ClonePlayerRenderers(root, localPlayer);
		}
		bool flag = false;
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		foreach (Renderer renderer in componentsInChildren)
		{
			renderer.enabled = true;
			if (renderer.gameObject.name == "RemotePlayerFallback")
			{
				renderer.sortingOrder = 32760;
				Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				if (shader != null)
				{
					Material material = new Material(shader);
					material.color = Color.HSVToRGB((float)playerId * 0.173f % 1f, 0.72f, 1f);
					renderer.material = material;
				}
			}
			else
			{
				flag = true;
			}
		}
		Transform transform = root.transform.Find("RemotePlayerFallback");
		if (transform != null)
		{
			transform.localPosition = new Vector3(0f, 0.9f, 0f);
			transform.localRotation = Quaternion.identity;
			transform.localScale = new Vector3(0.9f, 1.8f, 0.9f);
			transform.gameObject.SetActive(!flag);
		}
		RemoteAvatarProbe remoteAvatarProbe = root.GetComponent<RemoteAvatarProbe>();
		if (remoteAvatarProbe == null)
		{
			remoteAvatarProbe = root.AddComponent<RemoteAvatarProbe>();
		}
		remoteAvatarProbe.Initialize(playerId, layer, localPlayer);
		Log("Configured remote model " + playerId + ": renderers=" + root.GetComponentsInChildren<Renderer>(includeInactive: true).Length + ", model=" + HasModelRenderer(root) + ", layer=" + layer);
	}

	private static bool HasModelRenderer(GameObject root)
	{
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			if (componentsInChildren[i].gameObject.name != "RemotePlayerFallback")
			{
				return true;
			}
		}
		return false;
	}

	private static void ClonePlayerRenderers(GameObject root, Player localPlayer)
	{
		Transform transform = localPlayer.transform;
		HashSet<GameObject> hashSet = new HashSet<GameObject>();
		int num = 0;
		Renderer[] componentsInChildren = localPlayer.gameObject.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			GameObject gameObject = componentsInChildren[i].gameObject;
			if (!(gameObject == localPlayer.gameObject) && !IsExcludedVisual(gameObject) && !(gameObject.GetComponent<tk2dBaseSprite>() == null) && !HasRendererAncestor(gameObject.transform, transform) && hashSet.Add(gameObject))
			{
				GameObject gameObject2 = UnityEngine.Object.Instantiate(gameObject);
				gameObject2.name = "RemoteModel_" + num++ + "_" + gameObject.name;
				gameObject2.transform.SetParent(root.transform, worldPositionStays: false);
				gameObject2.transform.localPosition = transform.InverseTransformPoint(gameObject.transform.position);
				gameObject2.transform.localRotation = Quaternion.Inverse(transform.rotation) * gameObject.transform.rotation;
				gameObject2.transform.localScale = DivideScale(gameObject.transform.lossyScale, root.transform.lossyScale);
				SanitizeVisual(gameObject2);
				CopyRendererState(gameObject, gameObject2);
				gameObject2.SetActive(value: true);
			}
		}
	}

	private static bool IsExcludedVisual(GameObject gameObject)
	{
		string text = gameObject.name.ToLowerInvariant();
		if (!text.Contains("shadow") && !text.Contains("mask") && !text.Contains("dark") && !text.Contains("light") && !text.Contains("fog") && !text.Contains("effect") && !text.Contains("glow") && !text.Contains("flash"))
		{
			return text.Contains("aim");
		}
		return true;
	}

	private static void CopyRendererState(GameObject source, GameObject clone)
	{
		Renderer[] componentsInChildren = source.GetComponentsInChildren<Renderer>(includeInactive: true);
		Renderer[] componentsInChildren2 = clone.GetComponentsInChildren<Renderer>(includeInactive: true);
		int num = Math.Min(componentsInChildren.Length, componentsInChildren2.Length);
		for (int i = 0; i < num; i++)
		{
			Renderer renderer = componentsInChildren[i];
			Renderer renderer2 = componentsInChildren2[i];
			if (IsExcludedVisual(renderer.gameObject))
			{
				renderer2.enabled = false;
				continue;
			}
			renderer2.sharedMaterials = renderer.sharedMaterials;
			renderer2.sortingLayerID = renderer.sortingLayerID;
			renderer2.sortingOrder = renderer.sortingOrder;
			MaterialPropertyBlock materialPropertyBlock = new MaterialPropertyBlock();
			renderer.GetPropertyBlock(materialPropertyBlock);
			renderer2.SetPropertyBlock(materialPropertyBlock);
			renderer2.enabled = true;
		}
	}

	private static bool HasRendererAncestor(Transform transform, Transform playerRoot)
	{
		Transform parent = transform.parent;
		while (parent != null && parent != playerRoot)
		{
			if (parent.GetComponent<Renderer>() != null)
			{
				return true;
			}
			parent = parent.parent;
		}
		return false;
	}

	private static void SanitizeVisual(GameObject visual)
	{
		Collider[] componentsInChildren = visual.GetComponentsInChildren<Collider>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = false;
		}
		Rigidbody[] componentsInChildren2 = visual.GetComponentsInChildren<Rigidbody>(includeInactive: true);
		foreach (Rigidbody obj in componentsInChildren2)
		{
			obj.isKinematic = true;
			obj.detectCollisions = false;
		}
		AudioSource[] componentsInChildren3 = visual.GetComponentsInChildren<AudioSource>(includeInactive: true);
		for (int i = 0; i < componentsInChildren3.Length; i++)
		{
			componentsInChildren3[i].enabled = false;
		}
		Light[] componentsInChildren4 = visual.GetComponentsInChildren<Light>(includeInactive: true);
		for (int i = 0; i < componentsInChildren4.Length; i++)
		{
			componentsInChildren4[i].enabled = false;
		}
		MonoBehaviour[] componentsInChildren5 = visual.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
		foreach (MonoBehaviour obj2 in componentsInChildren5)
		{
			obj2.enabled = obj2.GetType().Name.StartsWith("tk2d", StringComparison.Ordinal);
		}
		Renderer[] componentsInChildren6 = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
		foreach (Renderer obj3 in componentsInChildren6)
		{
			obj3.enabled = !IsExcludedVisual(obj3.gameObject);
		}
	}

	private static void Log(string message)
	{
		try
		{
			Type type = Type.GetType("DarkwoodMultiplayerFramework.Plugin, DarkwoodMultiplayerFramework");
			FieldInfo fieldInfo = ((type != null) ? type.GetField("Log", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null);
			object obj = ((fieldInfo != null) ? fieldInfo.GetValue(null) : null);
			obj?.GetType().GetMethod("LogInfo").Invoke(obj, new object[1] { "[DMF Model] " + message });
		}
		catch
		{
		}
	}

	private static void ConfigureVisualClone(GameObject root, Player localPlayer, GameObject source)
	{
		if (source == null || source == localPlayer.gameObject)
		{
			return;
		}
		Transform transform = FindDirectChild(root.transform, "RemoteVisual_" + source.name);
		if (!(transform == null))
		{
			Transform transform2 = localPlayer.transform;
			transform.localPosition = transform2.InverseTransformPoint(source.transform.position);
			transform.localRotation = Quaternion.Inverse(transform2.rotation) * source.transform.rotation;
			transform.localScale = DivideScale(source.transform.lossyScale, root.transform.lossyScale);
			transform.gameObject.SetActive(value: true);
			Renderer[] componentsInChildren = source.GetComponentsInChildren<Renderer>(includeInactive: true);
			Renderer[] componentsInChildren2 = transform.GetComponentsInChildren<Renderer>(includeInactive: true);
			int num = Math.Min(componentsInChildren.Length, componentsInChildren2.Length);
			for (int i = 0; i < num; i++)
			{
				componentsInChildren2[i].enabled = true;
				componentsInChildren2[i].sortingLayerID = componentsInChildren[i].sortingLayerID;
				componentsInChildren2[i].sortingOrder = componentsInChildren[i].sortingOrder;
				componentsInChildren2[i].sharedMaterials = componentsInChildren[i].sharedMaterials;
			}
			tk2dSpriteAnimator[] componentsInChildren3 = transform.GetComponentsInChildren<tk2dSpriteAnimator>(includeInactive: true);
			for (int j = 0; j < componentsInChildren3.Length; j++)
			{
				componentsInChildren3[j].enabled = true;
			}
		}
	}

	private static Transform FindDirectChild(Transform parent, string name)
	{
		foreach (Transform item in parent)
		{
			if (item.name == name)
			{
				return item;
			}
		}
		return null;
	}

	private static Vector3 DivideScale(Vector3 value, Vector3 divisor)
	{
		return new Vector3((Mathf.Abs(divisor.x) > 0.0001f) ? (value.x / divisor.x) : value.x, (Mathf.Abs(divisor.y) > 0.0001f) ? (value.y / divisor.y) : value.y, (Mathf.Abs(divisor.z) > 0.0001f) ? (value.z / divisor.z) : value.z);
	}

	private static void SetLayerRecursive(GameObject root, int layer)
	{
		root.layer = layer;
		foreach (Transform item in root.transform)
		{
			SetLayerRecursive(item.gameObject, layer);
		}
	}
}
