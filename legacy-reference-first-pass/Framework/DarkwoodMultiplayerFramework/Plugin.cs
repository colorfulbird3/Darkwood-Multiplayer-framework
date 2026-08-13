using System;
using System.Collections;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

[BepInPlugin("com.darkwood.multiplayer.framework.v2", "Darkwood Multiplayer Framework", "0.7.0")]
public sealed class Plugin : BaseUnityPlugin
{
	public const string Guid = "com.darkwood.multiplayer.framework.v2";

	public const string Name = "Darkwood Multiplayer Framework";

	public const string Version = "0.7.0";

	public static ManualLogSource Log;

	public static ConfigEntry<int> Players;

	public static ConfigEntry<bool> ScaleLoot;

	public static ConfigEntry<string> Address;

	public static ConfigEntry<int> Port;

	public static ConfigEntry<bool> HostSceneAuthority;

	public static ConfigEntry<bool> ShowNetworkStats;

	private NetworkManager manager;

	private LegacyTelepathyTransport transport;

	private bool show;

	private Rect window = new Rect(24f, 24f, 460f, 390f);

	private string address;

	private string port;

	private void Awake()
	{
		Log = ((BaseUnityPlugin)this).Logger;
		Players = ((BaseUnityPlugin)this).Config.Bind<int>("Gameplay", "PlayerCount", 2, "Target players and random-loot multiplier.");
		ScaleLoot = ((BaseUnityPlugin)this).Config.Bind<bool>("Gameplay", "ScaleRandomLoot", true, "Scale newly generated random-container loot.");
		Address = ((BaseUnityPlugin)this).Config.Bind<string>("Network", "Address", "127.0.0.1", "Client destination.");
		Port = ((BaseUnityPlugin)this).Config.Bind<int>("Network", "Port", 7777, "TCP port.");
		HostSceneAuthority = ((BaseUnityPlugin)this).Config.Bind<bool>("Network", "HostSceneAuthority", true, "Clients follow the host Unity scene.");
		ShowNetworkStats = ((BaseUnityPlugin)this).Config.Bind<bool>("Interface", "ShowNetworkStats", true, "Show the subtle FPS/RTT/loss overlay.");
		Players.Value = Mathf.Clamp(Players.Value, 1, 8);
		Port.Value = Mathf.Clamp(Port.Value, 1, 65535);
		address = Address.Value;
		port = Port.Value.ToString();
		InitNetwork();
		Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, "com.darkwood.multiplayer.framework.v2");
		ParseArgs();
		Log.LogInfo((object)"Darkwood Multiplayer Framework 0.7.0 loaded; F6 opens settings.");
	}

	private void InitNetwork()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Expected O, but got Unknown
		GameObject val = new GameObject("DarkwoodMultiplayerNetworkV2");
		Object.DontDestroyOnLoad((Object)(object)val);
		transport = val.AddComponent<LegacyTelepathyTransport>();
		transport.port = (ushort)Port.Value;
		manager = val.AddComponent<NetworkManager>();
		manager.transport = transport;
		manager.maxConnections = Players.Value;
		manager.autoCreatePlayer = false;
		manager.networkAddress = Address.Value;
		val.AddComponent<MirrorRuntimePump>();
		val.AddComponent<SaveTransferRuntime>();
		val.AddComponent<SyncRuntime>();
		val.AddComponent<WorldStateSync>();
		val.AddComponent<NetworkStatsRuntime>();
		Log.LogInfo((object)$"Telepathy ready on {transport.port}.");
	}

	private void Update()
	{
		if (Input.GetKeyDown((KeyCode)287))
		{
			show = !show;
		}
		if (Input.GetKeyDown((KeyCode)282))
		{
			StartHost();
		}
		if (Input.GetKeyDown((KeyCode)283))
		{
			StartClient();
		}
		if (Input.GetKeyDown((KeyCode)284))
		{
			Stop();
		}
	}

	private void OnGUI()
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Expected O, but got Unknown
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		if (show)
		{
			window = GUILayout.Window(((Object)this).GetInstanceID(), window, new WindowFunction(Draw), "Darkwood 联机框架设置", Array.Empty<GUILayoutOption>());
		}
	}

	private void Draw(int id)
	{
		//IL_0289: Unknown result type (might be due to invalid IL or missing references)
		GUILayout.Label("联机人数（新随机战利品按此倍率生成）", Array.Empty<GUILayoutOption>());
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		for (int i = 1; i <= 8; i++)
		{
			if (GUILayout.Button((i == Players.Value) ? $"[{i}]" : i.ToString(), (GUILayoutOption[])(object)new GUILayoutOption[1] { GUILayout.Width(43f) }))
			{
				SetPlayers(i);
			}
		}
		GUILayout.EndHorizontal();
		bool flag = GUILayout.Toggle(ScaleLoot.Value, "按人数缩放随机容器战利品", Array.Empty<GUILayoutOption>());
		if (flag != ScaleLoot.Value)
		{
			ScaleLoot.Value = flag;
			((BaseUnityPlugin)this).Config.Save();
		}
		bool flag2 = GUILayout.Toggle(HostSceneAuthority.Value, "客户端跟随主机场景", Array.Empty<GUILayoutOption>());
		if (flag2 != HostSceneAuthority.Value)
		{
			HostSceneAuthority.Value = flag2;
			((BaseUnityPlugin)this).Config.Save();
		}
		bool flag3 = GUILayout.Toggle(ShowNetworkStats.Value, "显示右上角 FPS/延迟/丢包", Array.Empty<GUILayoutOption>());
		if (flag3 != ShowNetworkStats.Value)
		{
			ShowNetworkStats.Value = flag3;
			((BaseUnityPlugin)this).Config.Save();
		}
		GUILayout.Label("主机地址", Array.Empty<GUILayoutOption>());
		address = GUILayout.TextField(address ?? "127.0.0.1", Array.Empty<GUILayoutOption>());
		GUILayout.Label("端口", Array.Empty<GUILayoutOption>());
		port = GUILayout.TextField(port ?? "7777", Array.Empty<GUILayoutOption>());
		GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
		GUI.enabled = !NetworkClient.active && !NetworkServer.active;
		if (GUILayout.Button("启动主机 (F1)", Array.Empty<GUILayoutOption>()))
		{
			StartHost();
		}
		if (GUILayout.Button("连接主机 (F2)", Array.Empty<GUILayoutOption>()))
		{
			StartClient();
		}
		GUI.enabled = NetworkClient.active || NetworkServer.active;
		if (GUILayout.Button("停止 (F3)", Array.Empty<GUILayoutOption>()))
		{
			Stop();
		}
		GUI.enabled = true;
		GUILayout.EndHorizontal();
		GUILayout.Label("状态：" + Status(), Array.Empty<GUILayoutOption>());
		if (!string.IsNullOrEmpty(SaveTransferRuntime.StatusText))
		{
			GUILayout.Label("存档：" + SaveTransferRuntime.StatusText, Array.Empty<GUILayoutOption>());
		}
		GUILayout.Label("客户端应停留在主菜单连接；连接后会自动下载并加载主机当前存档。", Array.Empty<GUILayoutOption>());
		GUILayout.Label("下载存档保存在 BepInEx/DarkwoodMPClientSaves，不覆盖本地槽位。", Array.Empty<GUILayoutOption>());
		GUILayout.Label("F6 关闭", Array.Empty<GUILayoutOption>());
		GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
	}

	private void SetPlayers(int n)
	{
		Players.Value = Mathf.Clamp(n, 1, 8);
		if ((Object)(object)manager != (Object)null)
		{
			manager.maxConnections = Players.Value;
		}
		((BaseUnityPlugin)this).Config.Save();
		Log.LogInfo((object)$"Players/loot multiplier: {Players.Value}.");
	}

	private bool Apply()
	{
		if (!ushort.TryParse(port, out var result) || result == 0)
		{
			Log.LogWarning((object)("Invalid port: " + port));
			return false;
		}
		string text = (string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim());
		Address.Value = text;
		Port.Value = result;
		manager.networkAddress = text;
		manager.maxConnections = Players.Value;
		transport.port = result;
		((BaseUnityPlugin)this).Config.Save();
		return true;
	}

	private void StartHost()
	{
		if ((Object)(object)manager == (Object)null || NetworkClient.active || NetworkServer.active || !Apply())
		{
			return;
		}
		try
		{
			NetworkServer.listen = true;
			manager.StartHost();
			Log.LogInfo((object)$"Host started on {transport.port}.");
		}
		catch (Exception arg)
		{
			Log.LogError((object)$"Host start failed: {arg}");
			Stop();
		}
	}

	private void StartClient()
	{
		if ((Object)(object)manager == (Object)null || NetworkClient.active || NetworkServer.active || !Apply())
		{
			return;
		}
		try
		{
			manager.StartClient();
			Log.LogInfo((object)$"Connecting to {Address.Value}:{transport.port}.");
		}
		catch (Exception arg)
		{
			Log.LogError((object)$"Client start failed: {arg}");
			Stop();
		}
	}

	private void Stop()
	{
		if ((Object)(object)manager == (Object)null)
		{
			return;
		}
		try
		{
			if (manager.mode == NetworkManagerMode.Host)
			{
				manager.StopHost();
			}
			else if (NetworkClient.active)
			{
				manager.StopClient();
			}
			else if (NetworkServer.active)
			{
				manager.StopServer();
			}
			Log.LogInfo((object)"Network stopped.");
		}
		catch (Exception arg)
		{
			Log.LogError((object)$"Stop failed: {arg}");
		}
	}

	private string Status()
	{
		if ((Object)(object)manager == (Object)null)
		{
			return "未初始化";
		}
		if (manager.mode == NetworkManagerMode.Host)
		{
			return $"主机运行中（{NetworkServer.connections.Count}/{Players.Value}）";
		}
		if (NetworkClient.isConnected)
		{
			return $"已连接 {Address.Value}:{Port.Value}";
		}
		if (NetworkClient.active)
		{
			return "正在连接…";
		}
		if (NetworkServer.active)
		{
			return "服务器运行中";
		}
		return "未连接";
	}

	private void ParseArgs()
	{
		bool flag = false;
		bool flag2 = false;
		int result = 0;
		string[] commandLineArgs = Environment.GetCommandLineArgs();
		foreach (string text in commandLineArgs)
		{
			if (text.Equals("-host", StringComparison.OrdinalIgnoreCase))
			{
				flag = true;
			}
			else if (text.Equals("-client", StringComparison.OrdinalIgnoreCase))
			{
				flag2 = true;
			}
			else if (text.StartsWith("-address=", StringComparison.OrdinalIgnoreCase))
			{
				address = text.Substring(9);
			}
			else if (text.StartsWith("-port=", StringComparison.OrdinalIgnoreCase))
			{
				port = text.Substring(6);
			}
			else if (text.StartsWith("-players=", StringComparison.OrdinalIgnoreCase))
			{
				if (int.TryParse(text.Substring(9), out var result2))
				{
					SetPlayers(result2);
				}
			}
			else if (text.StartsWith("-profile=", StringComparison.OrdinalIgnoreCase))
			{
				int.TryParse(text.Substring(9), out result);
			}
		}
		if (flag && result >= 1 && result <= 5)
		{
			((MonoBehaviour)this).StartCoroutine(StartHostProfile(result));
		}
		else if (flag)
		{
			StartHost();
		}
		else if (flag2)
		{
			StartClient();
		}
	}

	private IEnumerator StartHostProfile(int profileId)
	{
		float deadline = Time.realtimeSinceStartup + 90f;
		while (((Object)(object)Singleton<SaveManager>.Instance == (Object)null || (Object)(object)Singleton<UI>.Instance == (Object)null) && Time.realtimeSinceStartup < deadline)
		{
			yield return null;
		}
		if ((Object)(object)Singleton<SaveManager>.Instance == (Object)null || (Object)(object)Singleton<UI>.Instance == (Object)null)
		{
			Log.LogError((object)"Profile test launch timed out waiting for game singletons.");
			yield break;
		}
		SaveState val = Singleton<SaveManager>.Instance.loadGameProfiles();
		GameProfile val2 = ((val != null && val.profiles != null) ? val.profiles.FirstOrDefault((GameProfile p) => p != null && p.id == profileId && p.Active) : null);
		if (val2 == null)
		{
			Log.LogError((object)$"Profile {profileId} is not an active save.");
			yield break;
		}
		Core.profiles = val.profiles;
		Core.currentProfile = val2;
		Log.LogInfo((object)$"Command-line loading profile {profileId} before hosting.");
		UI instance = Singleton<UI>.Instance;
		((MonoBehaviour)instance).StartCoroutine(instance.initLoadGame());
		while ((Core.mainMenu || (Object)(object)Player.Instance == (Object)null) && Time.realtimeSinceStartup < deadline)
		{
			yield return null;
		}
		if (Core.mainMenu || (Object)(object)Player.Instance == (Object)null)
		{
			Log.LogError((object)"Profile test launch timed out loading save.");
		}
		else
		{
			StartHost();
		}
	}
}
