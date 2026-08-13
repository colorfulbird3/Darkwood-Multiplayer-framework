using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework;

internal sealed class SyncRuntime : MonoBehaviour
{
	private sealed class ServerPose
	{
		public Vector3 Position;

		public float Time;

		public NetworkConnectionToClient Connection;
	}

	private readonly Dictionary<int, RemoteAvatar> avatars = new Dictionary<int, RemoteAvatar>();

	private readonly Dictionary<int, ServerPose> serverPoses = new Dictionary<int, ServerPose>();

	private readonly Dictionary<int, float> attackTimes = new Dictionary<int, float>();

	private bool serverHandlers;

	private bool clientHandlers;

	private bool wasClientActive;

	private float nextPose;

	private float nextScene;

	private uint poseSequence;

	private uint attackSequence;

	private uint sceneRevision;

	private float lastLocalAttack;

	private string lastHostScene;

	private int localPlayerId = int.MinValue;

	public static SyncRuntime Instance { get; private set; }

	private void Awake()
	{
		Instance = this;
		NetworkSerializers.Install();
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		ClearAvatars();
		if ((Object)(object)Instance == (Object)(object)this)
		{
			Instance = null;
		}
	}

	private void Update()
	{
		UpdateRegistrations();
		if (NetworkClient.active && NetworkClient.isConnected && SaveTransferRuntime.CanUseWorldSync && Time.unscaledTime >= nextPose)
		{
			nextPose = Time.unscaledTime + 1f / 15f;
			SendPose();
		}
		if (NetworkServer.active && Time.unscaledTime >= nextScene)
		{
			nextScene = Time.unscaledTime + 1f;
			BroadcastScene();
		}
		foreach (RemoteAvatar value in avatars.Values)
		{
			value.Tick();
		}
		CleanupAvatars();
	}

	private void UpdateRegistrations()
	{
		if (NetworkServer.active && !serverHandlers)
		{
			NetworkServer.ReplaceHandler<PlayerPoseMessage>(OnServerPose, requireAuthentication: false);
			NetworkServer.ReplaceHandler<PlayerAttackMessage>(OnServerAttack, requireAuthentication: false);
			serverHandlers = true;
			Plugin.Log.LogInfo((object)"Server sync handlers registered.");
		}
		if (1 == 0)
		{
			serverHandlers = false;
			serverPoses.Clear();
			attackTimes.Clear();
		}
		if (NetworkClient.active && !clientHandlers)
		{
			NetworkClient.ReplaceHandler<PlayerPoseMessage>(OnClientPose, requireAuthentication: false);
			NetworkClient.ReplaceHandler<PlayerAttackMessage>(OnClientAttack, requireAuthentication: false);
			NetworkClient.ReplaceHandler<HostSceneMessage>(OnClientScene, requireAuthentication: false);
			NetworkClient.ReplaceHandler<ClientIdentityMessage>(OnClientIdentity, requireAuthentication: false);
			clientHandlers = true;
			Plugin.Log.LogInfo((object)"Client sync handlers registered.");
		}
		if (1 == 0 && wasClientActive)
		{
			clientHandlers = false;
			localPlayerId = int.MinValue;
			ClearAvatars();
		}
		wasClientActive = NetworkClient.active;
	}

	private void SendPose()
	{
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		Player instance = Player.Instance;
		if (!((Object)(object)instance == (Object)null) && NetworkClient.connection != null)
		{
			tk2dSpriteAnimator a = (((Object)(object)instance.legs != (Object)null) ? instance.legs.GetComponent<tk2dSpriteAnimator>() : null);
			byte b = 0;
			if (instance.walking)
			{
				b = (byte)(b | 1u);
			}
			if (instance.running)
			{
				b = (byte)(b | 2u);
			}
			if (instance.aiming)
			{
				b = (byte)(b | 4u);
			}
			if (instance.attacking)
			{
				b = (byte)(b | 8u);
			}
			PlayerPoseMessage message = default(PlayerPoseMessage);
			message.PlayerId = -1;
			message.Sequence = ++poseSequence;
			message.Position = ((Component)instance).transform.position;
			message.Rotation = ((Component)instance).transform.rotation;
			message.Flags = b;
			Scene activeScene = SceneManager.GetActiveScene();
			message.Scene = ((Scene)(ref activeScene)).name;
			message.TorsoClip = ClipName(instance.torsoAnimator);
			message.TorsoFrame = Frame(instance.torsoAnimator);
			message.LegsClip = ClipName(a);
			message.LegsFrame = Frame(a);
			NetworkClient.Send(message, 1);
		}
	}

	private static string ClipName(tk2dSpriteAnimator a)
	{
		if (!((Object)(object)a != (Object)null) || a.CurrentClip == null)
		{
			return string.Empty;
		}
		return a.CurrentClip.name;
	}

	private static int Frame(tk2dSpriteAnimator a)
	{
		if (!((Object)(object)a != (Object)null) || a.CurrentClip == null)
		{
			return 0;
		}
		return a.CurrentFrame;
	}

	private void OnServerPose(NetworkConnectionToClient connection, PlayerPoseMessage msg)
	{
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_008c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		float unscaledTime = Time.unscaledTime;
		int num;
		if (serverPoses.TryGetValue(connection.connectionId, out var value))
		{
			num = 1;
			if (num != 0)
			{
				float num2 = 3f + 12f * Mathf.Max(0.02f, unscaledTime - value.Time);
				if (Vector3.Distance(value.Position, msg.Position) > num2)
				{
					msg.Position = Vector3.MoveTowards(value.Position, msg.Position, num2);
				}
			}
		}
		else
		{
			num = 0;
		}
		bool num3 = num == 0;
		serverPoses[connection.connectionId] = new ServerPose
		{
			Position = msg.Position,
			Time = unscaledTime,
			Connection = connection
		};
		if (num3)
		{
			connection.Send(new ClientIdentityMessage
			{
				PlayerId = connection.connectionId
			});
		}
		msg.PlayerId = connection.connectionId;
		Scene activeScene = SceneManager.GetActiveScene();
		msg.Scene = ((Scene)(ref activeScene)).name;
		NetworkServer.SendToAll(msg, 1);
	}

	private void OnClientPose(PlayerPoseMessage msg)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_005a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		if (!SaveTransferRuntime.CanUseWorldSync)
		{
			return;
		}
		int num = localPlayerId;
		Scene activeScene = SceneManager.GetActiveScene();
		string name = ((Scene)(ref activeScene)).name;
		if (msg.PlayerId == num)
		{
			if (!NetworkServer.active && msg.Scene == name)
			{
				Player instance = Player.Instance;
				if ((Object)(object)instance != (Object)null && Vector3.Distance(((Component)instance).transform.position, msg.Position) > 3f)
				{
					((Component)instance).transform.position = msg.Position;
				}
			}
			return;
		}
		Player instance2 = Player.Instance;
		if (!((Object)(object)instance2 == (Object)null))
		{
			if (!avatars.TryGetValue(msg.PlayerId, out var value))
			{
				value = new RemoteAvatar(msg.PlayerId, instance2);
				avatars.Add(msg.PlayerId, value);
				Plugin.Log.LogInfo((object)$"Created remote avatar {msg.PlayerId}.");
			}
			value.Apply(msg, msg.Scene == name);
		}
	}

	public void SendLocalAttack(byte kind)
	{
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		if (Time.unscaledTime - lastLocalAttack < 0.07f)
		{
			return;
		}
		lastLocalAttack = Time.unscaledTime;
		if (NetworkClient.active && NetworkClient.isConnected && NetworkClient.connection != null)
		{
			Player instance = Player.Instance;
			if (!((Object)(object)instance == (Object)null))
			{
				PlayerAttackMessage message = default(PlayerAttackMessage);
				message.PlayerId = -1;
				message.Sequence = ++attackSequence;
				message.Kind = kind;
				message.Position = ((Component)instance).transform.position;
				message.Direction = ((Component)instance).transform.forward;
				Scene activeScene = SceneManager.GetActiveScene();
				message.Scene = ((Scene)(ref activeScene)).name;
				NetworkClient.Send(message);
			}
		}
	}

	private void OnServerAttack(NetworkConnectionToClient connection, PlayerAttackMessage msg)
	{
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		float unscaledTime = Time.unscaledTime;
		if (!attackTimes.TryGetValue(connection.connectionId, out var value) || !(unscaledTime - value < 0.07f))
		{
			attackTimes[connection.connectionId] = unscaledTime;
			msg.PlayerId = connection.connectionId;
			Scene activeScene = SceneManager.GetActiveScene();
			msg.Scene = ((Scene)(ref activeScene)).name;
			NetworkServer.SendToAll(msg);
		}
	}

	private void OnClientAttack(PlayerAttackMessage msg)
	{
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		if (!SaveTransferRuntime.CanUseWorldSync)
		{
			return;
		}
		int num = localPlayerId;
		if (msg.PlayerId != num && avatars.TryGetValue(msg.PlayerId, out var value))
		{
			string scene = msg.Scene;
			Scene activeScene = SceneManager.GetActiveScene();
			if (scene == ((Scene)(ref activeScene)).name)
			{
				value.Attack(msg.Direction);
			}
		}
	}

	private void BroadcastScene()
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		Scene activeScene = SceneManager.GetActiveScene();
		if (((Scene)(ref activeScene)).name != lastHostScene)
		{
			lastHostScene = ((Scene)(ref activeScene)).name;
			sceneRevision++;
		}
		HostSceneMessage message = default(HostSceneMessage);
		message.Revision = sceneRevision;
		message.Scene = ((Scene)(ref activeScene)).name;
		message.BuildIndex = ((Scene)(ref activeScene)).buildIndex;
		NetworkServer.SendToAll(message);
	}

	public bool TryGetServerPlayerPosition(int connectionId, out Vector3 position)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		if (serverPoses.TryGetValue(connectionId, out var value))
		{
			position = value.Position;
			return true;
		}
		position = Vector3.zero;
		return false;
	}

	private void OnClientIdentity(ClientIdentityMessage msg)
	{
		if (localPlayerId != msg.PlayerId)
		{
			localPlayerId = msg.PlayerId;
			if (avatars.TryGetValue(localPlayerId, out var value))
			{
				value.Dispose();
				avatars.Remove(localPlayerId);
			}
			Plugin.Log.LogInfo((object)$"Assigned network player id {localPlayerId}.");
		}
	}

	private void OnClientScene(HostSceneMessage msg)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002d: Unknown result type (might be due to invalid IL or missing references)
		if (!NetworkServer.active && SaveTransferRuntime.CanUseWorldSync && Plugin.HostSceneAuthority.Value && !string.IsNullOrEmpty(msg.Scene))
		{
			Scene activeScene = SceneManager.GetActiveScene();
			if (!(((Scene)(ref activeScene)).name == msg.Scene))
			{
				Plugin.Log.LogInfo((object)("Following host scene: " + ((Scene)(ref activeScene)).name + " -> " + msg.Scene));
				ClearAvatars();
				SceneManager.LoadScene(msg.Scene);
			}
		}
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Invalid comparison between Unknown and I4
		if ((int)mode == 0)
		{
			nextPose = 0f;
		}
	}

	internal void HandleTransportDisconnected()
	{
		localPlayerId = int.MinValue;
		serverPoses.Clear();
		attackTimes.Clear();
		ClearAvatars();
	}

	private void CleanupAvatars()
	{
		if (avatars.Count == 0)
		{
			return;
		}
		float unscaledTime = Time.unscaledTime;
		List<int> list = new List<int>();
		foreach (KeyValuePair<int, RemoteAvatar> avatar in avatars)
		{
			if (unscaledTime - avatar.Value.LastSeen > 8f)
			{
				list.Add(avatar.Key);
			}
		}
		foreach (int item in list)
		{
			avatars[item].Dispose();
			avatars.Remove(item);
			Plugin.Log.LogInfo((object)$"Removed stale remote avatar {item}.");
		}
	}

	private void ClearAvatars()
	{
	}//Discarded unreachable code: IL_0011

}
