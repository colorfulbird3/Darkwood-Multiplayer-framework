using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework;

internal sealed class WorldStateSync : MonoBehaviour
{
	private sealed class ServerEntity
	{
		public uint NetworkId;

		public WorldEntityRecord Record;

		public WorldEntityState Last;

		public uint Revision;

		public uint InventoryRevision;

		public ushort DirtyMask;

		public bool HasState;

		public InventorySlotWire[] LastInventory;
	}

	private sealed class ServerPeer
	{
		public NetworkConnectionToClient Connection;

		public bool Ready;

		public uint Epoch;

		public string Scene;

		public readonly HashSet<uint> Tracked = new HashSet<uint>();

		public readonly HashSet<uint> SpawnQueued = new HashSet<uint>();

		public readonly List<uint> SpawnOrder = new List<uint>();

		public readonly List<EntityDespawnWire> DespawnQueue = new List<EntityDespawnWire>();

		public readonly Dictionary<uint, uint> SentRevisions = new Dictionary<uint, uint>();

		public readonly List<uint> KeyframeOrder = new List<uint>();

		public int KeyframeIndex;

		public float NextKeyframe;
	}

	private sealed class ClientEntity
	{
		public uint NetworkId;

		public ulong PersistentId;

		public WorldEntityRecord Record;

		public uint Revision;

		public uint InventoryRevision;

		public WorldEntityState State;

		public bool HasState;

		public bool HasTarget;

		public Vector3 TargetPosition;

		public Quaternion TargetRotation;
	}

	private sealed class FrozenEnemy
	{
		public bool CharacterEnabled;

		public bool HasPath;

		public bool PathEnabled;
	}

	private const int ReplicationProtocol = 2;

	private const float TickInterval = 0.05f;

	private const float InterestInterval = 0.5f;

	private const float KeyframeInterval = 2f;

	private const float DigestInterval = 5f;

	private const int SpawnBatchSize = 16;

	private const int MaxBatchesPerTick = 2;

	private const float DespawnHysteresis = 10f;

	private static readonly MethodInfo DoorPathOpen = AccessTools.Method(typeof(Door), "setPathfindingOpen", (Type[])null, (Type[])null);

	private static readonly MethodInfo DoorPathClosed = AccessTools.Method(typeof(Door), "setPathfindingClosed", (Type[])null, (Type[])null);

	private WorldEntityRegistry registry;

	private bool serverHandlers;

	private bool clientHandlers;

	private bool helloSent;

	private bool readySent;

	private bool wasClientActive;

	private uint epoch = 1u;

	private uint serverTick;

	private uint nextNetworkId = 1u;

	private uint nextActionSequence;

	private uint nextOperationId;

	private uint clientEpoch;

	private uint lastDigestTick;

	private float nextRegistry;

	private float nextServerTick;

	private float nextInterest;

	private float nextDigest;

	private float nextInventoryScan;

	private float lastWelcome;

	private string clientScene;

	private readonly Dictionary<ulong, ServerEntity> serverByPersistent = new Dictionary<ulong, ServerEntity>();

	private readonly Dictionary<uint, ServerEntity> serverByNetwork = new Dictionary<uint, ServerEntity>();

	private readonly Dictionary<int, ServerPeer> peers = new Dictionary<int, ServerPeer>();

	private readonly Dictionary<uint, ClientEntity> clientEntities = new Dictionary<uint, ClientEntity>();

	private readonly HashSet<ulong> unmatched = new HashSet<ulong>();

	private readonly Dictionary<Character, FrozenEnemy> frozen = new Dictionary<Character, FrozenEnemy>();

	private readonly Dictionary<ulong, float> mutationTimes = new Dictionary<ulong, float>();

	private readonly Dictionary<int, uint> actionSequences = new Dictionary<int, uint>();

	public static WorldStateSync Instance { get; private set; }

	public static bool ApplyingRemote { get; private set; }

	private void Awake()
	{
		Instance = this;
		WorldNetworkSerializers.Install();
		ReplicationSerializers.Install();
		RebuildRegistry();
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		ClearClientEntities();
		RestoreSimulation();
		peers.Clear();
		if (Instance == this)
		{
			Instance = null;
		}
	}

	private void Update()
	{
		RegisterHandlers();
		if (Time.unscaledTime >= nextRegistry)
		{
			nextRegistry = Time.unscaledTime + 5f;
			RebuildRegistry();
		}
		int num;
		if (NetworkClient.active && NetworkClient.isConnected)
		{
			num = ((!NetworkServer.active) ? 1 : 0);
			if (num != 0)
			{
				if (!helloSent)
				{
					helloSent = true;
					NetworkClient.Send(new ReplicationHelloMessage
					{
						Protocol = 2,
						Version = "0.7.0"
					});
					Plugin.Log.LogInfo((object)"Replication hello sent.");
				}
				if (clientEpoch != 0 && SaveTransferRuntime.CanUseWorldSync && !readySent && !string.IsNullOrEmpty(clientScene))
				{
					readySent = true;
					NetworkClient.Send(new ReplicationReadyMessage
					{
						Epoch = clientEpoch,
						Scene = clientScene,
						RegistryDigest = ((registry != null) ? registry.Digest : 0)
					});
					Plugin.Log.LogInfo((object)"Replication ready sent after save load.");
				}
				TickClientInterpolation();
			}
		}
		else
		{
			num = 0;
		}
		if (num == 0 && wasClientActive)
		{
			HandleTransportDisconnected();
		}
		wasClientActive = NetworkClient.active;
		if (NetworkServer.active)
		{
			if (Time.unscaledTime >= nextServerTick)
			{
				nextServerTick = Time.unscaledTime + 0.05f;
				serverTick++;
				ServerTick();
			}
			if (Time.unscaledTime >= nextDigest)
			{
				nextDigest = Time.unscaledTime + 5f;
				BroadcastDigest();
			}
		}
	}

	private void RegisterHandlers()
	{
		if (NetworkServer.active && !serverHandlers)
		{
			NetworkServer.ReplaceHandler<ReplicationHelloMessage>(OnServerHello, requireAuthentication: false);
			NetworkServer.ReplaceHandler<ReplicationReadyMessage>(OnServerReady, requireAuthentication: false);
			NetworkServer.ReplaceHandler<EntityActionCommand>(OnServerAction, requireAuthentication: false);
			NetworkServer.ReplaceHandler<InventoryTransactionRequest>(OnServerInventoryTransaction, requireAuthentication: false);
			serverHandlers = true;
			Plugin.Log.LogInfo((object)"0.7 replication server handlers registered.");
		}
		if (!NetworkServer.active)
		{
			serverHandlers = false;
			peers.Clear();
			serverByPersistent.Clear();
			serverByNetwork.Clear();
			actionSequences.Clear();
		}
		if (NetworkClient.active && !clientHandlers)
		{
			NetworkClient.ReplaceHandler<ReplicationWelcomeMessage>(OnClientWelcome, requireAuthentication: false);
			NetworkClient.ReplaceHandler<EntitySpawnBatchMessage>(OnClientSpawn, requireAuthentication: false);
			NetworkClient.ReplaceHandler<EntityDeltaBatchMessage>(OnClientDelta, requireAuthentication: false);
			NetworkClient.ReplaceHandler<EntityKeyframeBatchMessage>(OnClientKeyframe, requireAuthentication: false);
			NetworkClient.ReplaceHandler<EntityDespawnBatchMessage>(OnClientDespawn, requireAuthentication: false);
			NetworkClient.ReplaceHandler<InventoryStateMessage>(OnClientInventory, requireAuthentication: false);
			NetworkClient.ReplaceHandler<ReplicationDigestMessage>(OnClientDigest, requireAuthentication: false);
			clientHandlers = true;
			Plugin.Log.LogInfo((object)"0.7 replication client handlers registered.");
		}
		if (!NetworkClient.active)
		{
			clientHandlers = false;
			helloSent = false;
			readySent = false;
			clientEpoch = 0u;
			clientScene = null;
		}
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		RestoreSimulation();
		ClearClientEntities();
		RebuildRegistry();
		readySent = false;
		clientEpoch = (NetworkServer.active ? epoch : 0u);
		clientScene = scene.name;
		if (NetworkServer.active)
		{
			epoch++;
			BuildServerEntities();
			{
				foreach (ServerPeer value in peers.Values)
				{
					ResetPeerForEpoch(value);
				}
				return;
			}
		}
		if (NetworkClient.active)
		{
			helloSent = false;
			clientEpoch = 0u;
			Plugin.Log.LogInfo((object)("Scene loaded; restarting replication handshake for " + scene.name + "."));
		}
	}

	private void RebuildRegistry()
	{
		registry = WorldEntityRegistry.Rebuild();
		unmatched.Clear();
		if (NetworkServer.active)
		{
			BuildServerEntities();
		}
	}

	private void BuildServerEntities()
	{
		if (registry == null)
		{
			return;
		}
		Dictionary<ulong, ServerEntity> dictionary = new Dictionary<ulong, ServerEntity>(serverByPersistent);
		Dictionary<uint, ServerEntity> dictionary2 = new Dictionary<uint, ServerEntity>(serverByNetwork);
		serverByPersistent.Clear();
		serverByNetwork.Clear();
		List<ulong> list = new List<ulong>(registry.ById.Keys);
		list.Sort();
		foreach (ulong item in list)
		{
			WorldEntityRecord worldEntityRecord = registry.ById[item];
			if (!dictionary.TryGetValue(item, out var value) || value.Record.Kind != worldEntityRecord.Kind)
			{
				value = new ServerEntity
				{
					NetworkId = nextNetworkId++,
					Record = worldEntityRecord,
					Revision = 1u,
					InventoryRevision = 1u
				};
			}
			else
			{
				value.Record = worldEntityRecord;
			}
			serverByPersistent[item] = value;
			serverByNetwork[value.NetworkId] = value;
		}
		foreach (ServerPeer value2 in peers.Values)
		{
			List<uint> list2 = new List<uint>();
			foreach (uint item2 in value2.Tracked)
			{
				if (!serverByNetwork.ContainsKey(item2))
				{
					list2.Add(item2);
				}
			}
			foreach (uint item3 in list2)
			{
				value2.Tracked.Remove(item3);
				value2.SentRevisions.Remove(item3);
				value2.SpawnQueued.Remove(item3);
				value2.SpawnOrder.Remove(item3);
				value2.DespawnQueue.Add(new EntityDespawnWire
				{
					NetworkId = item3,
					Revision = (dictionary2.ContainsKey(item3) ? dictionary2[item3].Revision : 0u),
					Reason = 2
				});
			}
		}
	}

	private void ResetPeerForEpoch(ServerPeer peer)
	{
		peer.Ready = false;
		peer.Epoch = epoch;
		peer.Scene = ((registry != null) ? registry.SceneName : string.Empty);
		peer.Tracked.Clear();
		peer.SpawnQueued.Clear();
		peer.SpawnOrder.Clear();
		peer.SentRevisions.Clear();
		peer.DespawnQueue.Clear();
		peer.KeyframeOrder.Clear();
		peer.KeyframeIndex = 0;
		peer.NextKeyframe = Time.unscaledTime + 2f;
	}

	private void OnServerHello(NetworkConnectionToClient connection, ReplicationHelloMessage message)
	{
		if (connection == null)
		{
			return;
		}
		if (message.Protocol != 2)
		{
			connection.Send(new ReplicationWelcomeMessage
			{
				Protocol = 2,
				Epoch = epoch,
				Scene = ((registry != null) ? registry.SceneName : string.Empty),
				Error = "Replication protocol mismatch; install 0.7.0 on both sides."
			});
			ManualLogSource log = Plugin.Log;
			string[] obj = new string[5] { "Rejected replication hello from ", null, null, null, null };
			int connectionId = connection.connectionId;
			obj[1] = connectionId.ToString();
			obj[2] = " due to protocol ";
			obj[3] = message.Protocol.ToString();
			obj[4] = ".";
			log.LogWarning((object)string.Concat(obj));
		}
		else
		{
			if (!peers.TryGetValue(connection.connectionId, out var value))
			{
				value = new ServerPeer
				{
					Connection = connection
				};
				peers[connection.connectionId] = value;
			}
			value.Connection = connection;
			value.Epoch = epoch;
			value.Scene = ((registry != null) ? registry.SceneName : SceneManager.GetActiveScene().name);
			connection.Send(new ReplicationWelcomeMessage
			{
				Protocol = 2,
				Epoch = epoch,
				ServerTick = serverTick,
				Scene = value.Scene,
				Error = string.Empty
			});
			ManualLogSource log2 = Plugin.Log;
			int connectionId = connection.connectionId;
			log2.LogInfo((object)("Replication welcome sent to connection " + connectionId + "."));
		}
	}

	private void OnServerReady(NetworkConnectionToClient connection, ReplicationReadyMessage message)
	{
		if (connection != null && peers.TryGetValue(connection.connectionId, out var value))
		{
			if (message.Epoch != epoch || registry == null || message.Scene != registry.SceneName)
			{
				connection.Send(new ReplicationWelcomeMessage
				{
					Protocol = 2,
					Epoch = epoch,
					ServerTick = serverTick,
					Scene = ((registry != null) ? registry.SceneName : string.Empty),
					Error = "Scene or epoch changed; retrying replication handshake."
				});
				return;
			}
			value.Ready = true;
			value.Epoch = epoch;
			value.Scene = message.Scene;
			UpdateInterest(value, force: true);
			ManualLogSource log = Plugin.Log;
			string[] obj = new string[5] { "Replication peer ", null, null, null, null };
			int connectionId = connection.connectionId;
			obj[1] = connectionId.ToString();
			obj[2] = " ready; client digest ";
			obj[3] = message.RegistryDigest.ToString("X16");
			obj[4] = ".";
			log.LogInfo((object)string.Concat(obj));
		}
	}

	private void ServerTick()
	{
		if (registry == null || registry.SceneName != SceneManager.GetActiveScene().name)
		{
			RebuildRegistry();
		}
		CaptureServerStateChanges();
		List<int> list = new List<int>();
		foreach (KeyValuePair<int, ServerPeer> peer in peers)
		{
			ServerPeer value = peer.Value;
			if (value.Connection == null || !NetworkServer.connections.ContainsKey(peer.Key))
			{
				list.Add(peer.Key);
			}
			else if (value.Connection != null && value.Connection.isReady && value.Ready)
			{
				if (Time.unscaledTime >= nextInterest)
				{
					UpdateInterest(value, force: false);
				}
				PumpPeer(value);
			}
		}
		foreach (int item in list)
		{
			peers.Remove(item);
		}
		if (Time.unscaledTime >= nextInterest)
		{
			nextInterest = Time.unscaledTime + 0.5f;
		}
		if (Time.unscaledTime >= nextInventoryScan)
		{
			nextInventoryScan = Time.unscaledTime + 0.5f;
			BroadcastInventoryChanges();
		}
	}

	private void CaptureServerStateChanges()
	{
		foreach (ServerEntity value in serverByPersistent.Values)
		{
			value.DirtyMask = 0;
			if (value.Record != null && !(value.Record.Component == null) && value.Record.Kind != WorldEntityKind.Inventory)
			{
				WorldEntityState worldEntityState;
				try
				{
					worldEntityState = Capture(value.Record);
				}
				catch (Exception ex)
				{
					Plugin.Log.LogWarning((object)("Skipped server entity capture " + value.Record.Id.ToString("X16") + ": " + ex.Message));
					continue;
				}
				ushort num = DirtyMask(value.Last, worldEntityState, value.HasState);
				value.Last = worldEntityState;
				value.HasState = true;
				value.DirtyMask = num;
				if (num != 0)
				{
					value.Revision++;
				}
			}
		}
	}

	private void UpdateInterest(ServerPeer peer, bool force)
	{
		if (!force && Time.unscaledTime < nextInterest)
		{
			return;
		}
		Vector3 position = Vector3.zero;
		bool flag = SyncRuntime.Instance != null && SyncRuntime.Instance.TryGetServerPlayerPosition(peer.Connection.connectionId, out position);
		HashSet<uint> hashSet = new HashSet<uint>();
		foreach (ServerEntity value in serverByPersistent.Values)
		{
			if (value.Record != null && !(value.Record.Component == null))
			{
				float num = InterestRadius(value.Record.Kind);
				if (peer.Tracked.Contains(value.NetworkId))
				{
					num += 10f;
				}
				if (!flag || Vector3.Distance(position, value.Record.Component.transform.position) <= num)
				{
					hashSet.Add(value.NetworkId);
				}
			}
		}
		foreach (uint item in hashSet)
		{
			if (!peer.Tracked.Contains(item) && peer.SpawnQueued.Add(item))
			{
				peer.SpawnOrder.Add(item);
			}
		}
		for (int num2 = peer.SpawnOrder.Count - 1; num2 >= 0; num2--)
		{
			if (!hashSet.Contains(peer.SpawnOrder[num2]))
			{
				peer.SpawnQueued.Remove(peer.SpawnOrder[num2]);
				peer.SpawnOrder.RemoveAt(num2);
			}
		}
		List<uint> list = new List<uint>();
		foreach (uint item2 in peer.Tracked)
		{
			if (!hashSet.Contains(item2))
			{
				list.Add(item2);
			}
		}
		foreach (uint item3 in list)
		{
			peer.Tracked.Remove(item3);
			peer.SentRevisions.Remove(item3);
			peer.DespawnQueue.Add(new EntityDespawnWire
			{
				NetworkId = item3,
				Revision = 0u,
				Reason = 1
			});
		}
	}

	private static float InterestRadius(WorldEntityKind kind)
	{
		switch (kind)
		{
		case WorldEntityKind.Enemy:
			return 70f;
		case WorldEntityKind.Door:
		case WorldEntityKind.Window:
		case WorldEntityKind.Inventory:
			return 50f;
		case WorldEntityKind.Item:
			return 35f;
		default:
			return 50f;
		}
	}

	private void PumpPeer(ServerPeer peer)
	{
		int num = 0;
		if (peer.DespawnQueue.Count > 0)
		{
			int count = Math.Min(64, peer.DespawnQueue.Count);
			EntityDespawnWire[] entities = peer.DespawnQueue.GetRange(0, count).ToArray();
			peer.DespawnQueue.RemoveRange(0, count);
			peer.Connection.Send(new EntityDespawnBatchMessage
			{
				Epoch = epoch,
				ServerTick = serverTick,
				Scene = registry.SceneName,
				Entities = entities
			});
			num++;
		}
		while (peer.SpawnOrder.Count > 0 && num < 2)
		{
			List<EntitySpawnWire> list = new List<EntitySpawnWire>();
			while (peer.SpawnOrder.Count > 0 && list.Count < 16)
			{
				uint num2 = peer.SpawnOrder[0];
				peer.SpawnOrder.RemoveAt(0);
				peer.SpawnQueued.Remove(num2);
				if (serverByNetwork.TryGetValue(num2, out var value) && !(value.Record.Component == null))
				{
					WorldEntityState last = (value.HasState ? value.Last : Capture(value.Record));
					WorldEntityState state = (value.Last = last);
					value.HasState = true;
					InventorySlotWire[] array = ((value.Record.Kind == WorldEntityKind.Inventory) ? CaptureSlots((Inventory)value.Record.Component) : null);
					if (value.Record.Kind == WorldEntityKind.Inventory && value.LastInventory == null)
					{
						value.LastInventory = array;
					}
					list.Add(new EntitySpawnWire
					{
						NetworkId = value.NetworkId,
						PersistentId = value.Record.Id,
						Kind = (byte)value.Record.Kind,
						StateRevision = value.Revision,
						InventoryRevision = value.InventoryRevision,
						State = state,
						Inventory = array
					});
					peer.Tracked.Add(num2);
					peer.SentRevisions[num2] = value.Revision;
				}
			}
			if (list.Count > 0)
			{
				peer.Connection.Send(new EntitySpawnBatchMessage
				{
					Epoch = epoch,
					ServerTick = serverTick,
					Scene = registry.SceneName,
					Entities = list.ToArray()
				});
				num++;
			}
		}
		List<EntityDeltaWire> list2 = new List<EntityDeltaWire>();
		foreach (uint item in peer.Tracked)
		{
			if (!serverByNetwork.TryGetValue(item, out var value2) || value2.Record.Component == null || value2.Record.Kind == WorldEntityKind.Inventory)
			{
				continue;
			}
			peer.SentRevisions.TryGetValue(item, out var value3);
			if (value3 < value2.Revision)
			{
				ushort dirtyMask = (ushort)((value3 + 1 == value2.Revision && value2.DirtyMask != 0) ? value2.DirtyMask : 15);
				list2.Add(new EntityDeltaWire
				{
					NetworkId = item,
					Revision = value2.Revision,
					DirtyMask = dirtyMask,
					State = value2.Last
				});
				if (list2.Count >= 64)
				{
					break;
				}
			}
		}
		if (list2.Count > 0 && num < 2)
		{
			peer.Connection.Send(new EntityDeltaBatchMessage
			{
				Epoch = epoch,
				ServerTick = serverTick,
				Scene = registry.SceneName,
				Entities = list2.ToArray()
			}, 1);
			foreach (EntityDeltaWire item2 in list2)
			{
				peer.SentRevisions[item2.NetworkId] = item2.Revision;
			}
			num++;
		}
		if (peer.KeyframeIndex >= peer.KeyframeOrder.Count && Time.unscaledTime >= peer.NextKeyframe)
		{
			peer.KeyframeOrder.Clear();
			peer.KeyframeOrder.AddRange(peer.Tracked);
			peer.KeyframeIndex = 0;
			peer.NextKeyframe = Time.unscaledTime + 2f;
		}
		while (peer.KeyframeIndex < peer.KeyframeOrder.Count && num < 2)
		{
			List<EntityDeltaWire> list3 = new List<EntityDeltaWire>();
			while (peer.KeyframeIndex < peer.KeyframeOrder.Count && list3.Count < 64)
			{
				uint num3 = peer.KeyframeOrder[peer.KeyframeIndex++];
				if (serverByNetwork.TryGetValue(num3, out var value4) && value4.Record.Kind != WorldEntityKind.Inventory && value4.Record.Component != null)
				{
					list3.Add(new EntityDeltaWire
					{
						NetworkId = num3,
						Revision = value4.Revision,
						DirtyMask = 15,
						State = value4.Last
					});
				}
			}
			if (list3.Count > 0)
			{
				peer.Connection.Send(new EntityKeyframeBatchMessage
				{
					Epoch = epoch,
					ServerTick = serverTick,
					Scene = registry.SceneName,
					Entities = list3.ToArray()
				}, 1);
				num++;
			}
		}
	}

	private static ushort DirtyMask(WorldEntityState oldState, WorldEntityState state, bool hasOld)
	{
		if (!hasOld)
		{
			return 15;
		}
		EntityDirtyMask entityDirtyMask = EntityDirtyMask.None;
		if ((oldState.Position - state.Position).sqrMagnitude > 0.0001f || Quaternion.Angle(oldState.Rotation, state.Rotation) > 0.2f)
		{
			entityDirtyMask |= EntityDirtyMask.Transform;
		}
		if (Mathf.Abs(oldState.Health - state.Health) > 0.01f)
		{
			entityDirtyMask |= EntityDirtyMask.Vitals;
		}
		if (oldState.StateA != state.StateA || oldState.StateB != state.StateB || oldState.Flags != state.Flags)
		{
			entityDirtyMask |= EntityDirtyMask.State;
		}
		if (oldState.Frame != state.Frame || oldState.Animation != state.Animation)
		{
			entityDirtyMask |= EntityDirtyMask.Animation;
		}
		return (ushort)entityDirtyMask;
	}

	private void BroadcastInventoryChanges()
	{
		foreach (ServerEntity value in serverByPersistent.Values)
		{
			if (value.Record == null || value.Record.Kind != WorldEntityKind.Inventory || value.Record.Component == null)
			{
				continue;
			}
			InventorySlotWire[] array;
			try
			{
				array = CaptureSlots((Inventory)value.Record.Component);
			}
			catch
			{
				continue;
			}
			if (SlotsEqual(value.LastInventory, array))
			{
				continue;
			}
			value.LastInventory = array;
			value.InventoryRevision++;
			InventoryStateMessage inventoryStateMessage = default(InventoryStateMessage);
			inventoryStateMessage.Epoch = epoch;
			inventoryStateMessage.ServerTick = serverTick;
			inventoryStateMessage.NetworkId = value.NetworkId;
			inventoryStateMessage.Revision = value.InventoryRevision;
			inventoryStateMessage.OperationId = 0u;
			inventoryStateMessage.Accepted = true;
			inventoryStateMessage.Slots = array;
			InventoryStateMessage message = inventoryStateMessage;
			foreach (ServerPeer value2 in peers.Values)
			{
				if (value2.Ready && value2.Tracked.Contains(value.NetworkId) && value2.Connection != null)
				{
					value2.Connection.Send(message);
				}
			}
		}
	}

	private static bool SlotsEqual(InventorySlotWire[] a, InventorySlotWire[] b)
	{
		if (a == b)
		{
			return true;
		}
		if (a == null || b == null || a.Length != b.Length)
		{
			return false;
		}
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i].Type != b[i].Type || a[i].Amount != b[i].Amount || Mathf.Abs(a[i].Durability - b[i].Durability) > 0.001f || a[i].Quality != b[i].Quality || a[i].Recipe != b[i].Recipe)
			{
				return false;
			}
		}
		return true;
	}

	private void BroadcastDigest()
	{
		if (registry != null)
		{
			lastDigestTick = serverTick;
			ReplicationDigestMessage message = default(ReplicationDigestMessage);
			message.Epoch = epoch;
			message.ServerTick = serverTick;
			message.Scene = registry.SceneName;
			message.Count = registry.ById.Count;
			message.Digest = registry.Digest;
			NetworkServer.SendToAll(message);
		}
	}

	private void OnClientWelcome(ReplicationWelcomeMessage message)
	{
		if (NetworkServer.active)
		{
			return;
		}
		if (message.Protocol != 2)
		{
			Plugin.Log.LogError((object)("Replication handshake rejected: " + message.Error));
			return;
		}
		if (!string.IsNullOrEmpty(message.Error))
		{
			Plugin.Log.LogWarning((object)("Replication handshake refresh: " + message.Error));
		}
		clientEpoch = message.Epoch;
		clientScene = message.Scene;
		lastWelcome = Time.unscaledTime;
		readySent = false;
		Plugin.Log.LogInfo((object)("Replication welcome: epoch " + clientEpoch + ", scene " + clientScene + "."));
	}

	private bool ValidClientHeader(uint messageEpoch, string scene)
	{
		if (!NetworkServer.active && SaveTransferRuntime.CanUseWorldSync && messageEpoch == clientEpoch)
		{
			return scene == SceneManager.GetActiveScene().name;
		}
		return false;
	}

	private void OnClientSpawn(EntitySpawnBatchMessage message)
	{
		if (!ValidClientHeader(message.Epoch, message.Scene))
		{
			return;
		}
		if (registry == null || registry.SceneName != message.Scene)
		{
			RebuildRegistry();
		}
		if (message.Entities == null)
		{
			return;
		}
		EntitySpawnWire[] entities = message.Entities;
		for (int i = 0; i < entities.Length; i++)
		{
			EntitySpawnWire entitySpawnWire = entities[i];
			if (!registry.TryGet(entitySpawnWire.PersistentId, out var record) || (uint)record.Kind != entitySpawnWire.Kind)
			{
				if (unmatched.Add(entitySpawnWire.PersistentId))
				{
					ManualLogSource log = Plugin.Log;
					ulong persistentId = entitySpawnWire.PersistentId;
					log.LogWarning((object)("Unmatched host entity persistent id " + persistentId.ToString("X16") + "."));
				}
				continue;
			}
			if (!clientEntities.TryGetValue(entitySpawnWire.NetworkId, out var value))
			{
				value = new ClientEntity
				{
					NetworkId = entitySpawnWire.NetworkId,
					PersistentId = entitySpawnWire.PersistentId,
					Record = record
				};
				clientEntities[entitySpawnWire.NetworkId] = value;
			}
			value.Record = record;
			value.Revision = entitySpawnWire.StateRevision;
			value.InventoryRevision = entitySpawnWire.InventoryRevision;
			value.State = entitySpawnWire.State;
			value.HasState = true;
			value.HasTarget = false;
			Apply(record, entitySpawnWire.State, 15, immediate: true);
			if (record.Kind == WorldEntityKind.Inventory && entitySpawnWire.Inventory != null)
			{
				ApplyInventory((Inventory)record.Component, entitySpawnWire.Inventory);
			}
		}
	}

	private void OnClientDelta(EntityDeltaBatchMessage message)
	{
		ApplyDelta(message.Epoch, message.ServerTick, message.Scene, message.Entities, keyframe: false);
	}

	private void OnClientKeyframe(EntityKeyframeBatchMessage message)
	{
		ApplyDelta(message.Epoch, message.ServerTick, message.Scene, message.Entities, keyframe: true);
	}

	private void ApplyDelta(uint messageEpoch, uint tick, string scene, EntityDeltaWire[] deltas, bool keyframe)
	{
		if (!ValidClientHeader(messageEpoch, scene) || deltas == null)
		{
			return;
		}
		for (int i = 0; i < deltas.Length; i++)
		{
			EntityDeltaWire entityDeltaWire = deltas[i];
			if (clientEntities.TryGetValue(entityDeltaWire.NetworkId, out var value) && !(keyframe ? (entityDeltaWire.Revision < value.Revision) : (entityDeltaWire.Revision <= value.Revision)))
			{
				value.State = MergeState(value.State, entityDeltaWire.State, entityDeltaWire.DirtyMask);
				value.Revision = entityDeltaWire.Revision;
				if (((uint)entityDeltaWire.DirtyMask & (true ? 1u : 0u)) != 0 && value.Record != null && (value.Record.Kind == WorldEntityKind.Enemy || value.Record.Kind == WorldEntityKind.Item) && !keyframe)
				{
					value.TargetPosition = value.State.Position;
					value.TargetRotation = value.State.Rotation;
					value.HasTarget = true;
					Apply(value.Record, value.State, (ushort)(entityDeltaWire.DirtyMask & 0xFFFFFFFEu), immediate: false);
				}
				else
				{
					Apply(value.Record, value.State, entityDeltaWire.DirtyMask, keyframe);
				}
			}
		}
	}

	private static WorldEntityState MergeState(WorldEntityState oldState, WorldEntityState incoming, ushort mask)
	{
		if (((uint)mask & (true ? 1u : 0u)) != 0)
		{
			oldState.Position = incoming.Position;
			oldState.Rotation = incoming.Rotation;
		}
		if ((mask & 2u) != 0)
		{
			oldState.Health = incoming.Health;
		}
		if ((mask & 4u) != 0)
		{
			oldState.StateA = incoming.StateA;
			oldState.StateB = incoming.StateB;
			oldState.Flags = incoming.Flags;
		}
		if ((mask & 8u) != 0)
		{
			oldState.Animation = incoming.Animation;
			oldState.Frame = incoming.Frame;
		}
		return oldState;
	}

	private void OnClientDespawn(EntityDespawnBatchMessage message)
	{
		if (!ValidClientHeader(message.Epoch, message.Scene) || message.Entities == null)
		{
			return;
		}
		EntityDespawnWire[] entities = message.Entities;
		for (int i = 0; i < entities.Length; i++)
		{
			EntityDespawnWire entityDespawnWire = entities[i];
			if (clientEntities.TryGetValue(entityDespawnWire.NetworkId, out var value))
			{
				if (value.Record != null && value.Record.Component is Character)
				{
					Unfreeze((Character)value.Record.Component);
				}
				clientEntities.Remove(entityDespawnWire.NetworkId);
			}
		}
	}

	private void TickClientInterpolation()
	{
		foreach (ClientEntity value in clientEntities.Values)
		{
			if (value.HasTarget && value.Record != null && !(value.Record.Component == null))
			{
				Transform obj = value.Record.Component.transform;
				obj.position = Vector3.Lerp(obj.position, value.TargetPosition, 0.35f);
				obj.rotation = Quaternion.Slerp(obj.rotation, value.TargetRotation, 0.35f);
				if (Vector3.Distance(obj.position, value.TargetPosition) < 0.02f)
				{
					value.HasTarget = false;
				}
			}
		}
	}

	private void OnClientInventory(InventoryStateMessage message)
	{
		if (ValidClientHeader(message.Epoch, SceneManager.GetActiveScene().name) && clientEntities.TryGetValue(message.NetworkId, out var value) && value.Record != null && value.Record.Kind == WorldEntityKind.Inventory && message.Revision >= value.InventoryRevision && (message.Revision != value.InventoryRevision || !message.Accepted))
		{
			value.InventoryRevision = message.Revision;
			ApplyInventory((Inventory)value.Record.Component, message.Slots);
		}
	}

	private void OnClientDigest(ReplicationDigestMessage message)
	{
		if (ValidClientHeader(message.Epoch, message.Scene) && message.ServerTick != lastDigestTick)
		{
			lastDigestTick = message.ServerTick;
			if (registry == null || registry.SceneName != message.Scene)
			{
				RebuildRegistry();
			}
			if (registry.ById.Count == message.Count && registry.Digest == message.Digest)
			{
				Plugin.Log.LogInfo((object)("Entity registry MATCH: " + message.Count + " entities, digest " + message.Digest.ToString("X16") + "."));
			}
			else
			{
				Plugin.Log.LogWarning((object)("Entity registry MISMATCH: host " + message.Count + "/" + message.Digest.ToString("X16") + ", local " + registry.ById.Count + "/" + registry.Digest.ToString("X16") + "."));
			}
		}
	}

	public void RefreshAfterDownloadedSave()
	{
		RefreshAfterDownloadedSave(out var _, out var _);
	}

	public void RefreshAfterDownloadedSave(out int count, out ulong digest)
	{
		RestoreSimulation();
		ClearClientEntities();
		RebuildRegistry();
		nextRegistry = Time.unscaledTime + 5f;
		count = ((registry != null) ? registry.ById.Count : 0);
		digest = ((registry != null) ? registry.Digest : 0);
		Plugin.Log.LogInfo((object)("World entity registry refreshed after downloaded save load: " + count + "/" + digest.ToString("X16") + "."));
	}

	internal void HandleTransportDisconnected()
	{
		helloSent = false;
		readySent = false;
		clientEpoch = 0u;
		clientScene = null;
		ClearClientEntities();
		RestoreSimulation();
		if (SyncRuntime.Instance != null)
		{
			SyncRuntime.Instance.HandleTransportDisconnected();
		}
	}

	public void RequestAction(Component component, byte action, float amount, bool boolValue, Vector3 direction)
	{
		if (ApplyingRemote || NetworkServer.active || !NetworkClient.active || !NetworkClient.isConnected || registry == null || !registry.TryGetId(component, out var id))
		{
			return;
		}
		if (!serverByPersistent.TryGetValue(id, out var value))
		{
			uint num = FindClientNetworkId(id);
			if (num != 0)
			{
				EntityActionCommand message = default(EntityActionCommand);
				message.Epoch = clientEpoch;
				message.NetworkId = num;
				message.Sequence = ++nextActionSequence;
				message.ClientTick = 0u;
				message.Action = action;
				message.Amount = amount;
				message.BoolValue = boolValue;
				message.Direction = direction;
				NetworkClient.Send(message);
			}
		}
		else
		{
			EntityActionCommand message = default(EntityActionCommand);
			message.Epoch = clientEpoch;
			message.NetworkId = value.NetworkId;
			message.Sequence = ++nextActionSequence;
			message.ClientTick = 0u;
			message.Action = action;
			message.Amount = amount;
			message.BoolValue = boolValue;
			message.Direction = direction;
			NetworkClient.Send(message);
		}
	}

	private uint FindClientNetworkId(ulong persistentId)
	{
		foreach (ClientEntity value in clientEntities.Values)
		{
			if (value.PersistentId == persistentId)
			{
				return value.NetworkId;
			}
		}
		return 0u;
	}

	private void OnServerAction(NetworkConnectionToClient connection, EntityActionCommand command)
	{
		if (connection == null || !peers.TryGetValue(connection.connectionId, out var value) || !value.Ready || command.Epoch != epoch || !serverByNetwork.TryGetValue(command.NetworkId, out var value2) || !value.Tracked.Contains(command.NetworkId) || (actionSequences.TryGetValue(connection.connectionId, out var value3) && command.Sequence <= value3))
		{
			return;
		}
		actionSequences[connection.connectionId] = command.Sequence;
		if (!ValidateDistance(connection, value2.Record.Component.transform.position, 15f))
		{
			return;
		}
		ApplyingRemote = true;
		try
		{
			Transform transform = ((Player.Instance != null) ? Player.Instance.transform : null);
			if (command.Action == 1)
			{
				int damage = Mathf.Clamp(Mathf.CeilToInt(command.Amount), 1, 10000);
				Character character = value2.Record.Component as Character;
				Door door = value2.Record.Component as Door;
				Window window = value2.Record.Component as Window;
				Item item = value2.Record.Component as Item;
				if (character != null)
				{
					character.getHit(Mathf.Clamp(command.Amount, 1f, 10000f));
				}
				else if (door != null)
				{
					door.getHit(damage, transform, normalHit: true, canDamageMetal: true);
				}
				else if (window != null)
				{
					window.getHit(damage, transform);
				}
				else if (item != null)
				{
					item.getHit(damage, transform);
				}
			}
			else if (command.Action == 2)
			{
				Door door2 = value2.Record.Component as Door;
				Item item2 = value2.Record.Component as Item;
				if (door2 != null && door2.opened != command.BoolValue)
				{
					door2.openClose(transform);
				}
				else if (item2 != null && item2.switchable && item2.isOn != command.BoolValue)
				{
					item2.activate();
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning((object)("Host action failed: " + ex.Message));
		}
		finally
		{
			ApplyingRemote = false;
		}
	}

	private bool ValidateDistance(NetworkConnectionToClient connection, Vector3 entityPosition, float maxDistance)
	{
		if (SyncRuntime.Instance == null || !SyncRuntime.Instance.TryGetServerPlayerPosition(connection.connectionId, out var position))
		{
			return true;
		}
		return Vector3.Distance(position, entityPosition) <= maxDistance;
	}

	public void NotifyInventoryChanged(Inventory inventory)
	{
		BroadcastInventoryChanges();
		if (!ApplyingRemote && !NetworkServer.active && NetworkClient.active && NetworkClient.isConnected && !(inventory == null) && registry != null && registry.TryGetId(inventory, out var id))
		{
			uint num = FindClientNetworkId(id);
			if (num != 0 && (!mutationTimes.TryGetValue(id, out var value) || !(Time.unscaledTime - value < 0.1f)))
			{
				mutationTimes[id] = Time.unscaledTime;
				ClientEntity value2;
				uint expectedRevision = (clientEntities.TryGetValue(num, out value2) ? value2.InventoryRevision : 0u);
				InventoryTransactionRequest message = default(InventoryTransactionRequest);
				message.Epoch = clientEpoch;
				message.NetworkId = num;
				message.ExpectedRevision = expectedRevision;
				message.OperationId = ++nextOperationId;
				message.Slots = CaptureSlots(inventory);
				NetworkClient.Send(message);
			}
		}
	}

	private void OnServerInventoryTransaction(NetworkConnectionToClient connection, InventoryTransactionRequest request)
	{
		if (connection == null || !peers.TryGetValue(connection.connectionId, out var value) || !value.Ready || request.Epoch != epoch || !serverByNetwork.TryGetValue(request.NetworkId, out var value2) || value2.Record.Kind != WorldEntityKind.Inventory || !value.Tracked.Contains(request.NetworkId))
		{
			return;
		}
		bool flag = request.ExpectedRevision == value2.InventoryRevision && ValidateDistance(connection, value2.Record.Component.transform.position, 15f);
		Inventory inventory = (Inventory)value2.Record.Component;
		if (flag)
		{
			ApplyInventory(inventory, request.Slots);
			value2.InventoryRevision++;
			value2.LastInventory = CaptureSlots(inventory);
		}
		InventoryStateMessage inventoryStateMessage = default(InventoryStateMessage);
		inventoryStateMessage.Epoch = epoch;
		inventoryStateMessage.ServerTick = serverTick;
		inventoryStateMessage.NetworkId = value2.NetworkId;
		inventoryStateMessage.Revision = value2.InventoryRevision;
		inventoryStateMessage.OperationId = request.OperationId;
		inventoryStateMessage.Accepted = flag;
		inventoryStateMessage.Slots = CaptureSlots(inventory);
		InventoryStateMessage message = inventoryStateMessage;
		foreach (ServerPeer value3 in peers.Values)
		{
			if (value3.Ready && value3.Connection != null)
			{
				value3.Connection.Send(message);
			}
		}
	}

	internal static InventorySlotWire[] CaptureSlots(Inventory inventory)
	{
		if (inventory == null || inventory.slots == null)
		{
			return new InventorySlotWire[0];
		}
		int num = Math.Min(128, inventory.slots.Count);
		InventorySlotWire[] array = new InventorySlotWire[num];
		for (int i = 0; i < num; i++)
		{
			InvItemClass invItemClass = ((inventory.slots[i] != null) ? inventory.slots[i].invItem : null);
			if (invItemClass != null)
			{
				array[i] = new InventorySlotWire
				{
					Type = invItemClass.type,
					Amount = invItemClass.amount,
					Durability = invItemClass.durability,
					Quality = (int)invItemClass.modifierQuality,
					Recipe = invItemClass.isRecipe
				};
			}
		}
		return array;
	}

	private static void ApplyInventory(Inventory inventory, InventorySlotWire[] slots)
	{
		if (inventory == null || inventory.slots == null || slots == null)
		{
			return;
		}
		ApplyingRemote = true;
		try
		{
			while (inventory.slots.Count < slots.Length)
			{
				inventory.addSlot();
			}
			for (int i = 0; i < inventory.slots.Count; i++)
			{
				InventorySlotWire inventorySlotWire = ((i < slots.Length) ? slots[i] : default(InventorySlotWire));
				InvSlot invSlot = inventory.slots[i];
				if (invSlot == null)
				{
					continue;
				}
				if (string.IsNullOrEmpty(inventorySlotWire.Type))
				{
					if (invSlot.invItem != null)
					{
						invSlot.removeItem();
					}
				}
				else if (invSlot.invItem == null || invSlot.invItem.type != inventorySlotWire.Type)
				{
					invSlot.createItem(inventorySlotWire.Type, Math.Max(1, inventorySlotWire.Amount), inventorySlotWire.Durability, (InvItem.ModifierQuality)inventorySlotWire.Quality, inventorySlotWire.Recipe);
				}
				else
				{
					invSlot.invItem.amount = Math.Max(1, inventorySlotWire.Amount);
					invSlot.invItem.durability = inventorySlotWire.Durability;
					invSlot.invItem.modifierQuality = (InvItem.ModifierQuality)inventorySlotWire.Quality;
					invSlot.invItem.isRecipe = inventorySlotWire.Recipe;
					invSlot.invItem.refresh();
				}
			}
			inventory.refreshItems();
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning((object)("Failed applying inventory state: " + ex.Message));
		}
		finally
		{
			ApplyingRemote = false;
		}
	}

	private WorldEntityState Capture(WorldEntityRecord record)
	{
		WorldEntityState worldEntityState = default(WorldEntityState);
		worldEntityState.Id = record.Id;
		worldEntityState.Kind = (byte)record.Kind;
		worldEntityState.Position = record.Component.transform.position;
		worldEntityState.Rotation = record.Component.transform.rotation;
		worldEntityState.Animation = string.Empty;
		WorldEntityState result = worldEntityState;
		Character character = record.Component as Character;
		if (record.Kind == WorldEntityKind.Enemy && character != null)
		{
			result.Health = character.health;
			result.Flags = Flags(character.alive, character.gameObject.activeSelf, character.attacking, character.walking, character.running);
			if (character.animator != null)
			{
				result.Animation = ((character.animator.CurrentClip != null) ? character.animator.CurrentClip.name : string.Empty);
				result.Frame = character.animator.CurrentFrame;
			}
		}
		else if (record.Kind == WorldEntityKind.Door)
		{
			Door door = (Door)record.Component;
			result.Health = door.health;
			result.StateA = door.barricadeHealth;
			result.StateB = door.barricadeState;
			result.Flags = Flags(door.opened, door.barricaded, door.destroyed, door.blocked, door.gameObject.activeSelf);
			if (door.body != null)
			{
				result.Position = door.body.position;
				result.Rotation = door.body.rotation;
			}
		}
		else if (record.Kind == WorldEntityKind.Window)
		{
			Window window = (Window)record.Component;
			result.Health = window.barricadeHealth;
			result.StateA = window.barricadeState;
			result.Flags = Flags(window.barricaded, window.blocked, window.gameObject.activeSelf, d: false);
		}
		else if (record.Kind == WorldEntityKind.Item)
		{
			Item item = (Item)record.Component;
			result.Health = item.health;
			result.StateA = item.invItemAmount;
			result.Flags = Flags(item.destroyed, item.isOn, item.hasPower, item.searched, item.gameObject.activeSelf);
		}
		return result;
	}

	private void Apply(WorldEntityRecord record, WorldEntityState state, ushort mask, bool immediate)
	{
		if (record == null || record.Component == null)
		{
			return;
		}
		ApplyingRemote = true;
		try
		{
			if (record.Kind == WorldEntityKind.Enemy)
			{
				ApplyEnemy((Character)record.Component, state, immediate);
			}
			else if (record.Kind == WorldEntityKind.Door)
			{
				ApplyDoor((Door)record.Component, state);
			}
			else if (record.Kind == WorldEntityKind.Window)
			{
				ApplyWindow((Window)record.Component, state);
			}
			else if (record.Kind == WorldEntityKind.Item)
			{
				ApplyItem((Item)record.Component, state, immediate);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log.LogWarning((object)("Failed applying entity " + record.Id.ToString("X16") + ": " + ex.Message));
		}
		finally
		{
			ApplyingRemote = false;
		}
	}

	private void ApplyEnemy(Character c, WorldEntityState state, bool immediate)
	{
		Freeze(c);
		bool flag = Flag(state.Flags, 0);
		if (!flag && c.alive && c.health > 0f)
		{
			c.getHit(Mathf.Max(c.health + 1f, 999999f));
		}
		c.health = state.Health;
		c.Health = state.Health;
		c.alive = flag;
		c.gameObject.SetActive(Flag(state.Flags, 1));
		c.walking = Flag(state.Flags, 3);
		c.running = Flag(state.Flags, 4);
		if (immediate)
		{
			c.transform.position = state.Position;
			c.transform.rotation = state.Rotation;
		}
		if (!(c.animator != null) || string.IsNullOrEmpty(state.Animation))
		{
			return;
		}
		try
		{
			if (c.animator.CurrentClip == null || c.animator.CurrentClip.name != state.Animation)
			{
				c.animator.PlayFromFrame(state.Animation, Math.Max(0, state.Frame));
			}
		}
		catch
		{
		}
	}

	private void ApplyDoor(Door d, WorldEntityState state)
	{
		bool flag = Flag(state.Flags, 0);
		bool flag2 = Flag(state.Flags, 1);
		bool flag3 = Flag(state.Flags, 2);
		if (d.destroyed != flag3)
		{
			if (flag3)
			{
				d.destroyDoor();
			}
			else
			{
				d.unDestroy();
			}
		}
		if (d.barricaded && !flag2)
		{
			d.destroyBarricade();
		}
		d.opened = flag;
		d.barricaded = flag2;
		d.destroyed = flag3;
		d.blocked = Flag(state.Flags, 3);
		d.health = Mathf.RoundToInt(state.Health);
		d.barricadeHealth = state.StateA;
		d.barricadeState = state.StateB;
		if (d.body != null)
		{
			d.body.position = state.Position;
			d.body.rotation = state.Rotation;
		}
		if (flag && DoorPathOpen != null)
		{
			DoorPathOpen.Invoke(d, null);
		}
		else if (DoorPathClosed != null)
		{
			DoorPathClosed.Invoke(d, null);
		}
		d.gameObject.SetActive(Flag(state.Flags, 4));
	}

	private void ApplyWindow(Window w, WorldEntityState state)
	{
		bool flag = Flag(state.Flags, 0);
		if (w.barricaded && !flag)
		{
			w.destroyBarricade();
		}
		w.barricaded = flag;
		w.blocked = Flag(state.Flags, 1);
		w.barricadeHealth = Mathf.RoundToInt(state.Health);
		w.barricadeState = state.StateA;
		w.gameObject.SetActive(Flag(state.Flags, 2));
	}

	private void ApplyItem(Item item, WorldEntityState state, bool immediate)
	{
		bool flag = Flag(state.Flags, 0);
		if (flag && !item.destroyed)
		{
			item.getHit(Mathf.Max(item.health + 1, 999999), null, normalHit: false);
		}
		item.destroyed = flag;
		item.health = Mathf.RoundToInt(state.Health);
		item.invItemAmount = state.StateA;
		item.isOn = Flag(state.Flags, 1);
		item.hasPower = Flag(state.Flags, 2);
		item.searched = Flag(state.Flags, 3);
		if (immediate)
		{
			item.transform.position = state.Position;
			item.transform.rotation = state.Rotation;
		}
		item.gameObject.SetActive(Flag(state.Flags, 4));
	}

	private static byte Flags(bool a, bool b, bool c, bool d, bool e = false)
	{
		byte b2 = 0;
		if (a)
		{
			b2 = (byte)(b2 | 1u);
		}
		if (b)
		{
			b2 = (byte)(b2 | 2u);
		}
		if (c)
		{
			b2 = (byte)(b2 | 4u);
		}
		if (d)
		{
			b2 = (byte)(b2 | 8u);
		}
		if (e)
		{
			b2 = (byte)(b2 | 0x10u);
		}
		return b2;
	}

	private static bool Flag(byte flags, int bit)
	{
		return (flags & (1 << bit)) != 0;
	}

	private void Freeze(Character c)
	{
		if (!frozen.ContainsKey(c))
		{
			FrozenEnemy value = new FrozenEnemy
			{
				CharacterEnabled = c.enabled,
				HasPath = (c.AIpath != null),
				PathEnabled = (c.AIpath != null && c.AIpath.enabled)
			};
			frozen[c] = value;
			c.enabled = false;
			if (c.AIpath != null)
			{
				c.AIpath.enabled = false;
			}
		}
	}

	private void Unfreeze(Character c)
	{
		if (frozen.TryGetValue(c, out var value))
		{
			c.enabled = value.CharacterEnabled;
			if (value.HasPath && c.AIpath != null)
			{
				c.AIpath.enabled = value.PathEnabled;
			}
			frozen.Remove(c);
		}
	}

	private void RestoreSimulation()
	{
		foreach (Character item in new List<Character>(frozen.Keys))
		{
			if (item != null)
			{
				Unfreeze(item);
			}
		}
	}

	private void ClearClientEntities()
	{
		foreach (ClientEntity value in clientEntities.Values)
		{
			if (value.Record != null && value.Record.Component is Character)
			{
				Unfreeze((Character)value.Record.Component);
			}
		}
		clientEntities.Clear();
	}
}
