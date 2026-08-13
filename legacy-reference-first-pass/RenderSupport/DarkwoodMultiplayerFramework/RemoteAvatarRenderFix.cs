using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public static class RemoteAvatarRenderFix
{
	public static void Configure(int playerId, GameObject root, Player localPlayer)
	{
		//IL_016a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0175: Unknown result type (might be due to invalid IL or missing references)
		//IL_018f: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Expected O, but got Unknown
		//IL_0118: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)root == (Object)null || (Object)(object)localPlayer == (Object)null)
		{
			return;
		}
		int layer = ((Component)localPlayer).gameObject.layer;
		if ((Object)(object)localPlayer.torsoAnimator != (Object)null)
		{
			layer = ((Component)localPlayer.torsoAnimator).gameObject.layer;
		}
		SetLayerRecursive(root, layer);
		root.SetActive(true);
		ConfigureVisualClone(root, localPlayer, ((Object)(object)localPlayer.torsoAnimator != (Object)null) ? ((Component)localPlayer.torsoAnimator).gameObject : null);
		ConfigureVisualClone(root, localPlayer, localPlayer.legs);
		if (!HasModelRenderer(root))
		{
			ClonePlayerRenderers(root, localPlayer);
		}
		bool flag = false;
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(true);
		foreach (Renderer val in componentsInChildren)
		{
			val.enabled = true;
			if (((Object)((Component)val).gameObject).name == "RemotePlayerFallback")
			{
				val.sortingOrder = 32760;
				Shader val2 = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
				if ((Object)(object)val2 != (Object)null)
				{
					Material val3 = new Material(val2);
					val3.color = Color.HSVToRGB((float)playerId * 0.173f % 1f, 0.72f, 1f);
					val.material = val3;
				}
			}
			else
			{
				flag = true;
			}
		}
		Transform val4 = root.transform.Find("RemotePlayerFallback");
		if ((Object)(object)val4 != (Object)null)
		{
			val4.localPosition = new Vector3(0f, 0.9f, 0f);
			val4.localRotation = Quaternion.identity;
			val4.localScale = new Vector3(0.9f, 1.8f, 0.9f);
			((Component)val4).gameObject.SetActive(!flag);
		}
		RemoteAvatarProbe remoteAvatarProbe = root.GetComponent<RemoteAvatarProbe>();
		if ((Object)(object)remoteAvatarProbe == (Object)null)
		{
			remoteAvatarProbe = root.AddComponent<RemoteAvatarProbe>();
		}
		remoteAvatarProbe.Initialize(playerId, layer, localPlayer);
		Log("Configured remote model " + playerId + ": renderers=" + root.GetComponentsInChildren<Renderer>(true).Length + ", model=" + HasModelRenderer(root) + ", layer=" + layer);
	}

	private static bool HasModelRenderer(GameObject root)
	{
		Renderer[] componentsInChildren = root.GetComponentsInChildren<Renderer>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			if (((Object)((Component)componentsInChildren[i]).gameObject).name != "RemotePlayerFallback")
			{
				return true;
			}
		}
		return false;
	}

	private static void ClonePlayerRenderers(GameObject root, Player localPlayer)
	{
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00eb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		Transform transform = ((Component)localPlayer).transform;
		HashSet<GameObject> hashSet = new HashSet<GameObject>();
		int num = 0;
		Renderer[] componentsInChildren = ((Component)localPlayer).gameObject.GetComponentsInChildren<Renderer>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			GameObject gameObject = ((Component)componentsInChildren[i]).gameObject;
			if (!((Object)(object)gameObject == (Object)(object)((Component)localPlayer).gameObject) && !IsExcludedVisual(gameObject) && !((Object)(object)gameObject.GetComponent<tk2dBaseSprite>() == (Object)null) && !HasRendererAncestor(gameObject.transform, transform) && hashSet.Add(gameObject))
			{
				GameObject val = Object.Instantiate<GameObject>(gameObject);
				((Object)val).name = "RemoteModel_" + num++ + "_" + ((Object)gameObject).name;
				val.transform.SetParent(root.transform, false);
				val.transform.localPosition = transform.InverseTransformPoint(gameObject.transform.position);
				val.transform.localRotation = Quaternion.Inverse(transform.rotation) * gameObject.transform.rotation;
				val.transform.localScale = DivideScale(gameObject.transform.lossyScale, root.transform.lossyScale);
				SanitizeVisual(val);
				CopyRendererState(gameObject, val);
				val.SetActive(true);
			}
		}
	}

	private static bool IsExcludedVisual(GameObject gameObject)
	{
		string text = ((Object)gameObject).name.ToLowerInvariant();
		if (!text.Contains("shadow") && !text.Contains("mask") && !text.Contains("dark") && !text.Contains("light") && !text.Contains("fog") && !text.Contains("effect") && !text.Contains("glow") && !text.Contains("flash"))
		{
			return text.Contains("aim");
		}
		return true;
	}

	private static void CopyRendererState(GameObject source, GameObject clone)
	{
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Expected O, but got Unknown
		Renderer[] componentsInChildren = source.GetComponentsInChildren<Renderer>(true);
		Renderer[] componentsInChildren2 = clone.GetComponentsInChildren<Renderer>(true);
		int num = Math.Min(componentsInChildren.Length, componentsInChildren2.Length);
		for (int i = 0; i < num; i++)
		{
			Renderer val = componentsInChildren[i];
			Renderer val2 = componentsInChildren2[i];
			if (IsExcludedVisual(((Component)val).gameObject))
			{
				val2.enabled = false;
				continue;
			}
			val2.sharedMaterials = val.sharedMaterials;
			val2.sortingLayerID = val.sortingLayerID;
			val2.sortingOrder = val.sortingOrder;
			MaterialPropertyBlock val3 = new MaterialPropertyBlock();
			val.GetPropertyBlock(val3);
			val2.SetPropertyBlock(val3);
			val2.enabled = true;
		}
	}

	private static bool HasRendererAncestor(Transform transform, Transform playerRoot)
	{
		Transform parent = transform.parent;
		while ((Object)(object)parent != (Object)null && (Object)(object)parent != (Object)(object)playerRoot)
		{
			if ((Object)(object)((Component)parent).GetComponent<Renderer>() != (Object)null)
			{
				return true;
			}
			parent = parent.parent;
		}
		return false;
	}

	private static void SanitizeVisual(GameObject visual)
	{
		Collider[] componentsInChildren = visual.GetComponentsInChildren<Collider>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = false;
		}
		Rigidbody[] componentsInChildren2 = visual.GetComponentsInChildren<Rigidbody>(true);
		foreach (Rigidbody obj in componentsInChildren2)
		{
			obj.isKinematic = true;
			obj.detectCollisions = false;
		}
		AudioSource[] componentsInChildren3 = visual.GetComponentsInChildren<AudioSource>(true);
		for (int i = 0; i < componentsInChildren3.Length; i++)
		{
			((Behaviour)componentsInChildren3[i]).enabled = false;
		}
		Light[] componentsInChildren4 = visual.GetComponentsInChildren<Light>(true);
		for (int i = 0; i < componentsInChildren4.Length; i++)
		{
			((Behaviour)componentsInChildren4[i]).enabled = false;
		}
		MonoBehaviour[] componentsInChildren5 = visual.GetComponentsInChildren<MonoBehaviour>(true);
		foreach (MonoBehaviour obj2 in componentsInChildren5)
		{
			((Behaviour)obj2).enabled = ((object)obj2).GetType().Name.StartsWith("tk2d", StringComparison.Ordinal);
		}
		Renderer[] componentsInChildren6 = visual.GetComponentsInChildren<Renderer>(true);
		foreach (Renderer obj3 in componentsInChildren6)
		{
			obj3.enabled = !IsExcludedVisual(((Component)obj3).gameObject);
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
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0063: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		if ((Object)(object)source == (Object)null || (Object)(object)source == (Object)(object)((Component)localPlayer).gameObject)
		{
			return;
		}
		Transform val = FindDirectChild(root.transform, "RemoteVisual_" + ((Object)source).name);
		if (!((Object)(object)val == (Object)null))
		{
			Transform transform = ((Component)localPlayer).transform;
			val.localPosition = transform.InverseTransformPoint(source.transform.position);
			val.localRotation = Quaternion.Inverse(transform.rotation) * source.transform.rotation;
			val.localScale = DivideScale(source.transform.lossyScale, root.transform.lossyScale);
			((Component)val).gameObject.SetActive(true);
			Renderer[] componentsInChildren = source.GetComponentsInChildren<Renderer>(true);
			Renderer[] componentsInChildren2 = ((Component)val).GetComponentsInChildren<Renderer>(true);
			int num = Math.Min(componentsInChildren.Length, componentsInChildren2.Length);
			for (int i = 0; i < num; i++)
			{
				componentsInChildren2[i].enabled = true;
				componentsInChildren2[i].sortingLayerID = componentsInChildren[i].sortingLayerID;
				componentsInChildren2[i].sortingOrder = componentsInChildren[i].sortingOrder;
				componentsInChildren2[i].sharedMaterials = componentsInChildren[i].sharedMaterials;
			}
			tk2dSpriteAnimator[] componentsInChildren3 = ((Component)val).GetComponentsInChildren<tk2dSpriteAnimator>(true);
			for (int j = 0; j < componentsInChildren3.Length; j++)
			{
				((Behaviour)componentsInChildren3[j]).enabled = true;
			}
		}
	}

	private static Transform FindDirectChild(Transform parent, string name)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Expected O, but got Unknown
		foreach (Transform item in parent)
		{
			Transform val = item;
			if (((Object)val).name == name)
			{
				return val;
			}
		}
		return null;
	}

	private static Vector3 DivideScale(Vector3 value, Vector3 divisor)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0068: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		return new Vector3((Mathf.Abs(divisor.x) > 0.0001f) ? (value.x / divisor.x) : value.x, (Mathf.Abs(divisor.y) > 0.0001f) ? (value.y / divisor.y) : value.y, (Mathf.Abs(divisor.z) > 0.0001f) ? (value.z / divisor.z) : value.z);
	}

	private static void SetLayerRecursive(GameObject root, int layer)
	{
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		root.layer = layer;
		foreach (Transform item in root.transform)
		{
			SetLayerRecursive(((Component)item).gameObject, layer);
		}
	}
}
