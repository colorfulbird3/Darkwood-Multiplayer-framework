using UnityEngine;

namespace DarkwoodMultiplayerFramework;

public sealed class RemoteAvatarProbe : MonoBehaviour
{
	private int playerId;

	private int playerLayer;

	private Player localPlayer;

	private float nextLog;

	private GUIStyle style;

	public void Initialize(int id, int layer, Player local)
	{
		playerId = id;
		playerLayer = layer;
		localPlayer = local;
		nextLog = 0f;
	}

	private void LateUpdate()
	{
		Renderer[] componentsInChildren = GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = true;
		}
		if (!(Time.unscaledTime < nextLog))
		{
			nextLog = Time.unscaledTime + 5f;
			Camera camera = FindCamera();
			float num = ((localPlayer != null) ? Vector3.Distance(localPlayer.transform.position, base.transform.position) : (-1f));
			Vector3 vector = ((camera != null) ? camera.WorldToViewportPoint(base.transform.position) : Vector3.zero);
			Debug.Log(string.Format("[DMF AvatarProbe] id={0} pos={1} distance={2:F2} active={3} layer={4} renderers={5} camera={6} viewport={7}", playerId, base.transform.position, num, base.gameObject.activeInHierarchy, playerLayer, GetComponentsInChildren<Renderer>(includeInactive: true).Length, (camera != null) ? camera.name : "NONE", vector));
		}
	}

	private Camera FindCamera()
	{
		Camera main = Camera.main;
		if (main != null && main.enabled && (main.cullingMask & (1 << playerLayer)) != 0)
		{
			return main;
		}
		Camera[] allCameras = Camera.allCameras;
		foreach (Camera camera in allCameras)
		{
			if (camera != null && camera.enabled && (camera.cullingMask & (1 << playerLayer)) != 0)
			{
				return camera;
			}
		}
		return main;
	}

	private void OnGUI()
	{
		Renderer[] componentsInChildren = GetComponentsInChildren<Renderer>(includeInactive: true);
		foreach (Renderer renderer in componentsInChildren)
		{
			if (renderer.gameObject.name != "RemotePlayerFallback" && renderer.gameObject.activeInHierarchy)
			{
				return;
			}
		}
		Camera camera = FindCamera();
		if (camera == null)
		{
			return;
		}
		Vector3 vector = camera.WorldToScreenPoint(base.transform.position + Vector3.up * 1.8f);
		if (!(vector.z <= 0f))
		{
			if (style == null)
			{
				style = new GUIStyle(GUI.skin.label);
				style.alignment = TextAnchor.MiddleCenter;
				style.fontSize = 12;
				style.normal.textColor = new Color(0.72f, 0.9f, 1f, 0.72f);
			}
			GUI.Label(new Rect(vector.x - 30f, (float)Screen.height - vector.y - 12f, 60f, 24f), "P" + playerId, style);
		}
	}

	private void OnDestroy()
	{
		Debug.Log("[DMF AvatarProbe] destroyed id=" + playerId);
	}
}
