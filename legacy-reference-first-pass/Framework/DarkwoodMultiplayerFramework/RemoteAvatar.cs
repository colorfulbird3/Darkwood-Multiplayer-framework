using System;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

internal sealed class RemoteAvatar
{
	public readonly int PlayerId;

	public readonly GameObject Root;

	public float LastSeen;

	private Vector3 targetPosition;

	private Quaternion targetRotation;

	private Vector3 baseScale = Vector3.one;

	private tk2dSpriteAnimator torso;

	private tk2dSpriteAnimator legs;

	private float attackUntil;

	private Vector3 attackDirection;

	public RemoteAvatar(int id, Player local)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Expected O, but got Unknown
		//IL_0050: Unknown result type (might be due to invalid IL or missing references)
		//IL_006b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0081: Unknown result type (might be due to invalid IL or missing references)
		//IL_0086: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		PlayerId = id;
		Root = new GameObject("RemotePlayer_" + id);
		Object.DontDestroyOnLoad((Object)(object)Root);
		Root.transform.position = ((Component)local).transform.position;
		Root.transform.rotation = ((Component)local).transform.rotation;
		targetPosition = Root.transform.position;
		targetRotation = Root.transform.rotation;
		CloneVisuals(local);
		CreateFallback(id);
		RemoteAvatarRenderFix.Configure(id, Root, local);
		CloneVisuals(local);
		baseScale = Root.transform.localScale;
	}

	private bool CloneVisuals(Player player)
	{
		//Discarded unreachable code: IL_00a0
		tk2dSpriteAnimator[] componentsInChildren = Root.GetComponentsInChildren<tk2dSpriteAnimator>(true);
		if (componentsInChildren.Length != 0)
		{
			torso = componentsInChildren[0];
		}
		if (componentsInChildren.Length > 1)
		{
			legs = componentsInChildren[1];
		}
		return Root.GetComponentsInChildren<Renderer>(true).Length != 0;
	}

	private static void Sanitize(GameObject go)
	{
		Collider[] componentsInChildren = go.GetComponentsInChildren<Collider>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = false;
		}
		Rigidbody[] componentsInChildren2 = go.GetComponentsInChildren<Rigidbody>(true);
		foreach (Rigidbody obj in componentsInChildren2)
		{
			obj.isKinematic = true;
			obj.detectCollisions = false;
		}
		AudioSource[] componentsInChildren3 = go.GetComponentsInChildren<AudioSource>(true);
		for (int i = 0; i < componentsInChildren3.Length; i++)
		{
			((Behaviour)componentsInChildren3[i]).enabled = false;
		}
		Light[] componentsInChildren4 = go.GetComponentsInChildren<Light>(true);
		for (int i = 0; i < componentsInChildren4.Length; i++)
		{
			((Behaviour)componentsInChildren4[i]).enabled = false;
		}
		MonoBehaviour[] componentsInChildren5 = go.GetComponentsInChildren<MonoBehaviour>(true);
		foreach (MonoBehaviour val in componentsInChildren5)
		{
			if (!((object)val).GetType().Name.StartsWith("tk2dSprite", StringComparison.Ordinal))
			{
				((Behaviour)val).enabled = false;
			}
			else
			{
				((Behaviour)val).enabled = true;
			}
		}
	}

	private void CreateFallback(int id)
	{
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		GameObject obj = GameObject.CreatePrimitive((PrimitiveType)1);
		((Object)obj).name = "RemotePlayerFallback";
		obj.transform.SetParent(Root.transform, false);
		obj.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
		Collider component = obj.GetComponent<Collider>();
		if ((Object)(object)component != (Object)null)
		{
			Object.Destroy((Object)(object)component);
		}
		Renderer component2 = obj.GetComponent<Renderer>();
		if ((Object)(object)component2 != (Object)null)
		{
			component2.material.color = Color.HSVToRGB((float)id * 0.173f % 1f, 0.65f, 1f);
		}
	}

	public void Apply(PlayerPoseMessage pose, bool visible)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		LastSeen = Time.unscaledTime;
		targetPosition = pose.Position;
		targetRotation = pose.Rotation;
		Root.SetActive(true);
		if (true)
		{
			ApplyClip(torso, pose.TorsoClip, pose.TorsoFrame);
			ApplyClip(legs, pose.LegsClip, pose.LegsFrame);
		}
	}

	private static void ApplyClip(tk2dSpriteAnimator animator, string clip, int frame)
	{
		if ((Object)(object)animator == (Object)null || string.IsNullOrEmpty(clip))
		{
			return;
		}
		try
		{
			if (animator.CurrentClip == null || animator.CurrentClip.name != clip)
			{
				animator.PlayFromFrame(clip, Math.Max(0, frame));
			}
		}
		catch (Exception)
		{
		}
	}

	public void Attack(Vector3 direction)
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		attackDirection = ((((Vector3)(ref direction)).sqrMagnitude > 0.01f) ? ((Vector3)(ref direction)).normalized : Root.transform.forward);
		attackUntil = Time.unscaledTime + 0.18f;
	}

	public void Tick()
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		float num = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
		Vector3 val = Vector3.zero;
		if (Time.unscaledTime < attackUntil)
		{
			float num2 = 1f - (attackUntil - Time.unscaledTime) / 0.18f;
			val = attackDirection * Mathf.Sin(num2 * (float)Math.PI) * 0.22f;
			Root.transform.localScale = baseScale * (1f + 0.12f * Mathf.Sin(num2 * (float)Math.PI));
		}
		else
		{
			Root.transform.localScale = baseScale;
		}
		Root.transform.position = Vector3.Lerp(Root.transform.position, targetPosition + val, num);
		Root.transform.rotation = Quaternion.Slerp(Root.transform.rotation, targetRotation, num);
	}

	public void Dispose()
	{
		if ((Object)(object)Root != (Object)null)
		{
			Object.Destroy((Object)(object)Root);
		}
	}
}
