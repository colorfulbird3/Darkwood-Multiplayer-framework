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
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_0097: Unknown result type (might be due to invalid IL or missing references)
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		Renderer[] componentsInChildren = ((Component)this).GetComponentsInChildren<Renderer>(true);
		for (int i = 0; i < componentsInChildren.Length; i++)
		{
			componentsInChildren[i].enabled = true;
		}
		if (!(Time.unscaledTime < nextLog))
		{
			nextLog = Time.unscaledTime + 5f;
			Camera val = FindCamera();
			float num = (((Object)(object)localPlayer != (Object)null) ? Vector3.Distance(((Component)localPlayer).transform.position, ((Component)this).transform.position) : (-1f));
			Vector3 val2 = (((Object)(object)val != (Object)null) ? val.WorldToViewportPoint(((Component)this).transform.position) : Vector3.zero);
			Debug.Log((object)string.Format("[DMF AvatarProbe] id={0} pos={1} distance={2:F2} active={3} layer={4} renderers={5} camera={6} viewport={7}", playerId, ((Component)this).transform.position, num, ((Component)this).gameObject.activeInHierarchy, playerLayer, ((Component)this).GetComponentsInChildren<Renderer>(true).Length, ((Object)(object)val != (Object)null) ? ((Object)val).name : "NONE", val2));
		}
	}

	private Camera FindCamera()
	{
		Camera main = Camera.main;
		if ((Object)(object)main != (Object)null && ((Behaviour)main).enabled && (main.cullingMask & (1 << playerLayer)) != 0)
		{
			return main;
		}
		Camera[] allCameras = Camera.allCameras;
		foreach (Camera val in allCameras)
		{
			if ((Object)(object)val != (Object)null && ((Behaviour)val).enabled && (val.cullingMask & (1 << playerLayer)) != 0)
			{
				return val;
			}
		}
		return main;
	}

	private void OnGUI()
	{
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_0079: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a4: Expected O, but got Unknown
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		Renderer[] componentsInChildren = ((Component)this).GetComponentsInChildren<Renderer>(true);
		foreach (Renderer val in componentsInChildren)
		{
			if (((Object)((Component)val).gameObject).name != "RemotePlayerFallback" && ((Component)val).gameObject.activeInHierarchy)
			{
				return;
			}
		}
		Camera val2 = FindCamera();
		if ((Object)(object)val2 == (Object)null)
		{
			return;
		}
		Vector3 val3 = val2.WorldToScreenPoint(((Component)this).transform.position + Vector3.up * 1.8f);
		if (!(val3.z <= 0f))
		{
			if (style == null)
			{
				style = new GUIStyle(GUI.skin.label);
				style.alignment = (TextAnchor)4;
				style.fontSize = 12;
				style.normal.textColor = new Color(0.72f, 0.9f, 1f, 0.72f);
			}
			GUI.Label(new Rect(val3.x - 30f, (float)Screen.height - val3.y - 12f, 60f, 24f), "P" + playerId, style);
		}
	}

	private void OnDestroy()
	{
		Debug.Log((object)("[DMF AvatarProbe] destroyed id=" + playerId));
	}
}
