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
		PlayerId = id;
		Root = new GameObject("RemotePlayer_" + id);
		UnityEngine.Object.DontDestroyOnLoad(Root);
		Root.transform.position = local.transform.position;
		Root.transform.rotation = local.transform.rotation;
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
		tk2dSpriteAnimator[] componentsInChildren = Root.GetComponentsInChildren<tk2dSpriteAnimator>(includeInactive: true);
		if (componentsInChildren.Length != 0)
		{
			torso = componentsInChildren[0];
		}
		if (componentsInChildren.Length > 1)
		{
			legs = componentsInChildren[1];
		}
		return Root.GetComponentsInChildren<Renderer>(includeInactive: true).Length != 0;
	}

	private static void Sanitize(GameObject go)
	{
		Collider[] componentsInChildren = go.GetComponentsInChildren<Collider>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = false;
		}
		Rigidbody[] componentsInChildren2 = go.GetComponentsInChildren<Rigidbody>(includeInactive: true);
		foreach (Rigidbody obj in componentsInChildren2)
		{
			obj.isKinematic = true;
			obj.detectCollisions = false;
		}
		AudioSource[] componentsInChildren3 = go.GetComponentsInChildren<AudioSource>(includeInactive: true);
		for (int i = 0; i < componentsInChildren3.Length; i++)
		{
			componentsInChildren3[i].enabled = false;
		}
		Light[] componentsInChildren4 = go.GetComponentsInChildren<Light>(includeInactive: true);
		for (int i = 0; i < componentsInChildren4.Length; i++)
		{
			componentsInChildren4[i].enabled = false;
		}
		MonoBehaviour[] componentsInChildren5 = go.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
		foreach (MonoBehaviour monoBehaviour in componentsInChildren5)
		{
			if (!monoBehaviour.GetType().Name.StartsWith("tk2dSprite", StringComparison.Ordinal))
			{
				monoBehaviour.enabled = false;
			}
			else
			{
				monoBehaviour.enabled = true;
			}
		}
	}

	private void CreateFallback(int id)
	{
		GameObject gameObject = GameObject.CreatePrimitive(PrimitiveType.Capsule);
		gameObject.name = "RemotePlayerFallback";
		gameObject.transform.SetParent(Root.transform, worldPositionStays: false);
		gameObject.transform.localScale = new Vector3(0.55f, 0.9f, 0.55f);
		Collider component = gameObject.GetComponent<Collider>();
		if (component != null)
		{
			UnityEngine.Object.Destroy(component);
		}
		Renderer component2 = gameObject.GetComponent<Renderer>();
		if (component2 != null)
		{
			component2.material.color = Color.HSVToRGB((float)id * 0.173f % 1f, 0.65f, 1f);
		}
	}

	public void Apply(PlayerPoseMessage pose, bool visible)
	{
		LastSeen = Time.unscaledTime;
		targetPosition = pose.Position;
		targetRotation = pose.Rotation;
		Root.SetActive(value: true);
		if (true)
		{
			ApplyClip(torso, pose.TorsoClip, pose.TorsoFrame);
			ApplyClip(legs, pose.LegsClip, pose.LegsFrame);
		}
	}

	private static void ApplyClip(tk2dSpriteAnimator animator, string clip, int frame)
	{
		if (animator == null || string.IsNullOrEmpty(clip))
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
		attackDirection = ((direction.sqrMagnitude > 0.01f) ? direction.normalized : Root.transform.forward);
		attackUntil = Time.unscaledTime + 0.18f;
	}

	public void Tick()
	{
		float t = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
		Vector3 vector = Vector3.zero;
		if (Time.unscaledTime < attackUntil)
		{
			float num = 1f - (attackUntil - Time.unscaledTime) / 0.18f;
			vector = attackDirection * Mathf.Sin(num * Mathf.PI) * 0.22f;
			Root.transform.localScale = baseScale * (1f + 0.12f * Mathf.Sin(num * Mathf.PI));
		}
		else
		{
			Root.transform.localScale = baseScale;
		}
		Root.transform.position = Vector3.Lerp(Root.transform.position, targetPosition + vector, t);
		Root.transform.rotation = Quaternion.Slerp(Root.transform.rotation, targetRotation, t);
	}

	public void Dispose()
	{
		if (Root != null)
		{
			UnityEngine.Object.Destroy(Root);
		}
	}
}
