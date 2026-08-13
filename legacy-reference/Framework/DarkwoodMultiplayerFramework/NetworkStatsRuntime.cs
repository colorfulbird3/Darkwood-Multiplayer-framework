using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

internal sealed class NetworkStatsRuntime : MonoBehaviour
{
	private const float PingInterval = 1f;

	private const float PingTimeout = 3.2f;

	private const int SampleWindow = 60;

	private const float PanelWidth = 184f;

	private const float PanelHeight = 56f;

	private readonly Dictionary<uint, float> pending = new Dictionary<uint, float>();

	private readonly Queue<bool> outcomes = new Queue<bool>();

	private GUIStyle labelStyle;

	private GUIStyle titleStyle;

	private Texture2D panelTexture;

	private uint nextSequence;

	private float nextPing;

	private float lastRtt;

	private float fpsAccumulator;

	private int fpsFrames;

	private float fpsWindow;

	private bool serverHandlers;

	private bool clientHandlers;

	private bool wasConnected;

	public static NetworkStatsRuntime Instance { get; private set; }

	public float RttMs { get; private set; } = -1f;


	public float JitterMs { get; private set; } = -1f;


	public float LossPercent { get; private set; } = -1f;


	public float Fps { get; private set; }

	private void Awake()
	{
		Instance = this;
		ReplicationSerializers.Install();
		panelTexture = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
		panelTexture.SetPixel(0, 0, Color.white);
		panelTexture.Apply();
	}

	private void OnDestroy()
	{
		if (panelTexture != null)
		{
			Object.Destroy(panelTexture);
		}
		if (Instance == this)
		{
			Instance = null;
		}
	}

	private void Update()
	{
		RegisterHandlers();
		UpdateFps();
		bool flag = NetworkClient.active && NetworkClient.isConnected && !NetworkServer.active;
		if (flag)
		{
			if (!wasConnected)
			{
				pending.Clear();
				outcomes.Clear();
				RttMs = -1f;
				JitterMs = -1f;
				LossPercent = -1f;
				nextPing = 0f;
			}
			if (Time.unscaledTime >= nextPing)
			{
				nextPing = Time.unscaledTime + 1f;
				SendPing();
			}
			ExpirePings();
		}
		else if (wasConnected)
		{
			ResetNetworkSamples();
		}
		wasConnected = flag;
	}

	private void RegisterHandlers()
	{
		if (NetworkServer.active && !serverHandlers)
		{
			NetworkServer.ReplaceHandler<NetworkStatsPingMessage>(OnServerPing, requireAuthentication: false);
			serverHandlers = true;
		}
		if (!NetworkServer.active)
		{
			serverHandlers = false;
		}
		if (NetworkClient.active && !clientHandlers)
		{
			NetworkClient.ReplaceHandler<NetworkStatsPongMessage>(OnClientPong, requireAuthentication: false);
			clientHandlers = true;
		}
		if (!NetworkClient.active)
		{
			clientHandlers = false;
		}
	}

	private void SendPing()
	{
		if (NetworkClient.connection != null)
		{
			uint num = ++nextSequence;
			pending[num] = Time.realtimeSinceStartup;
			NetworkStatsPingMessage message = default(NetworkStatsPingMessage);
			message.Sequence = num;
			NetworkClient.Send(message);
		}
	}

	private void OnServerPing(NetworkConnectionToClient connection, NetworkStatsPingMessage message)
	{
		connection?.Send(new NetworkStatsPongMessage
		{
			Sequence = message.Sequence
		});
	}

	private void OnClientPong(NetworkStatsPongMessage message)
	{
		if (!pending.TryGetValue(message.Sequence, out var value))
		{
			return;
		}
		pending.Remove(message.Sequence);
		float num = Mathf.Max(0f, (Time.realtimeSinceStartup - value) * 1000f);
		if (RttMs < 0f)
		{
			RttMs = num;
		}
		else
		{
			RttMs = Mathf.Lerp(RttMs, num, 0.25f);
		}
		if (lastRtt > 0f)
		{
			float num2 = Mathf.Abs(num - lastRtt);
			if (JitterMs < 0f)
			{
				JitterMs = num2;
			}
			else
			{
				JitterMs = Mathf.Lerp(JitterMs, num2, 0.25f);
			}
		}
		lastRtt = num;
		AddOutcome(success: true);
	}

	private void ExpirePings()
	{
		if (pending.Count == 0)
		{
			return;
		}
		float realtimeSinceStartup = Time.realtimeSinceStartup;
		List<uint> list = new List<uint>();
		foreach (KeyValuePair<uint, float> item in pending)
		{
			if (realtimeSinceStartup - item.Value >= 3.2f)
			{
				list.Add(item.Key);
			}
		}
		foreach (uint item2 in list)
		{
			pending.Remove(item2);
			AddOutcome(success: false);
		}
	}

	private void AddOutcome(bool success)
	{
		outcomes.Enqueue(success);
		while (outcomes.Count > 60)
		{
			outcomes.Dequeue();
		}
		int num = 0;
		foreach (bool outcome in outcomes)
		{
			if (!outcome)
			{
				num++;
			}
		}
		LossPercent = ((outcomes.Count == 0) ? (-1f) : ((float)num * 100f / (float)outcomes.Count));
	}

	private void ResetNetworkSamples()
	{
		pending.Clear();
		outcomes.Clear();
		RttMs = -1f;
		JitterMs = -1f;
		LossPercent = -1f;
		lastRtt = 0f;
	}

	private void UpdateFps()
	{
		float unscaledDeltaTime = Time.unscaledDeltaTime;
		if (!(unscaledDeltaTime <= 0f))
		{
			fpsAccumulator += 1f / unscaledDeltaTime;
			fpsFrames++;
			fpsWindow += unscaledDeltaTime;
			if (fpsWindow >= 0.5f)
			{
				float num = fpsAccumulator / (float)Mathf.Max(1, fpsFrames);
				Fps = ((Fps <= 0f) ? num : Mathf.Lerp(Fps, num, 0.45f));
				fpsAccumulator = 0f;
				fpsFrames = 0;
				fpsWindow = 0f;
			}
		}
	}

	private void CreateStyles()
	{
		titleStyle = new GUIStyle(GUI.skin.label)
		{
			fontSize = 11
		};
		labelStyle = new GUIStyle(GUI.skin.label)
		{
			fontSize = 11
		};
	}

	private void OnGUI()
	{
		if (Plugin.ShowNetworkStats != null && Plugin.ShowNetworkStats.Value)
		{
			GUI.depth = -10000;
			if (titleStyle == null)
			{
				CreateStyles();
			}
			if (!(panelTexture == null))
			{
				Rect position = new Rect((float)Screen.width - 184f - 16f, 14f, 184f, 56f);
				Color color = GUI.color;
				GUI.color = new Color(0.025f, 0.03f, 0.035f, 0.48f);
				GUI.DrawTexture(position, panelTexture);
				GUI.color = new Color(0.86f, 0.89f, 0.92f, 0.84f);
				GUI.Label(new Rect(position.x + 10f, position.y + 4f, position.width - 20f, 16f), "NETWORK", titleStyle);
				GUI.color = MetricColor(RttMs, LossPercent);
				GUI.Label(new Rect(position.x + 10f, position.y + 20f, position.width - 20f, 16f), "FPS " + FormatFps() + "    RTT " + FormatMetric(RttMs, "ms"), labelStyle);
				GUI.color = new Color(0.78f, 0.82f, 0.86f, 0.76f);
				GUI.Label(new Rect(position.x + 10f, position.y + 36f, position.width - 20f, 16f), "LOSS " + FormatMetric(LossPercent, "%") + "    JITTER " + FormatMetric(JitterMs, "ms"), labelStyle);
				GUI.color = color;
			}
		}
	}

	private string FormatFps()
	{
		if (!(Fps <= 0f))
		{
			return Mathf.RoundToInt(Fps).ToString();
		}
		return "--";
	}

	private static string FormatMetric(float value, string suffix)
	{
		if (!(value < 0f))
		{
			return ((suffix == "%") ? value.ToString("0.0") : Mathf.RoundToInt(value).ToString()) + suffix;
		}
		return "False";
	}

	private static Color MetricColor(float rtt, float loss)
	{
		if (loss >= 5f || rtt >= 180f)
		{
			return new Color(0.95f, 0.58f, 0.48f, 0.84f);
		}
		if (loss >= 1f || rtt >= 90f)
		{
			return new Color(0.95f, 0.82f, 0.52f, 0.84f);
		}
		return new Color(0.86f, 0.89f, 0.92f, 0.84f);
	}
}
