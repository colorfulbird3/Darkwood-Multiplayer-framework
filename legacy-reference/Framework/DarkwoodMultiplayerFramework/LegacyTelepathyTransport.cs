using System;
using System.Diagnostics;
using System.Threading;
using BepInEx.Logging;
using Mirror;
using Telepathy;
using UnityEngine;

namespace DarkwoodMultiplayerFramework;

internal sealed class LegacyTelepathyTransport : Transport
{
	public ushort port = 7777;

	public int maxMessageSize = 16777216;

	private Client client;

	private Server server;

	public override bool Available()
	{
		return Application.platform != RuntimePlatform.WebGLPlayer;
	}

	private void EnsureClient()
	{
		if (client == null)
		{
			client = new Client();
			client.MaxMessageSize = maxMessageSize;
			client.NoDelay = true;
		}
	}

	private void EnsureServer()
	{
		if (server == null)
		{
			server = new Server();
			server.MaxMessageSize = maxMessageSize;
			server.NoDelay = true;
		}
	}

	public override bool ClientConnected()
	{
		if (client != null)
		{
			return client.Connected;
		}
		return false;
	}

	public override void ClientConnect(string address)
	{
		EnsureClient();
		client.Connect(address, port);
	}

	public override void ClientSend(ArraySegment<byte> segment, int channelId)
	{
		if (client == null || !client.Send(Copy(segment)))
		{
			OnClientError?.Invoke(TransportError.Unexpected, "Telepathy client send failed");
		}
	}

	public override void ClientDisconnect()
	{
		if (client != null)
		{
			client.Disconnect();
		}
	}

	public override Uri ServerUri()
	{
		return new Uri("tcp://localhost:" + port);
	}

	public override bool ServerActive()
	{
		if (server != null)
		{
			return server.Active;
		}
		return false;
	}

	public override void ServerStart()
	{
		EnsureServer();
		if (!server.Start(port))
		{
			throw new InvalidOperationException("Telepathy failed to start on port " + port);
		}
		int num = Environment.TickCount + 2000;
		while (!IsListenerBound() && Environment.TickCount < num)
		{
			Thread.Sleep(5);
		}
		if (!IsListenerBound())
		{
			throw new InvalidOperationException("Telepathy listener did not bind port " + port);
		}
		Plugin.Log.LogInfo((object)("Telepathy listener bound on port " + port));
	}

	private bool IsListenerBound()
	{
		try
		{
			return server != null && server.listener != null && server.listener.Server != null && server.listener.Server.IsBound;
		}
		catch
		{
			return false;
		}
	}

	public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId)
	{
		if (server == null || !server.Send(connectionId, Copy(segment)))
		{
			OnServerError?.Invoke(connectionId, TransportError.Unexpected, "Telepathy server send failed");
		}
	}

	public override void ServerDisconnect(int connectionId)
	{
		if (server != null)
		{
			Plugin.Log.LogWarning((object)$"Mirror requested transport disconnect for connection {connectionId}. Call stack: {new StackTrace(1, fNeedFileInfo: false)}");
			server.Disconnect(connectionId);
		}
	}

	public override string ServerGetClientAddress(int connectionId)
	{
		if (server == null)
		{
			return string.Empty;
		}
		return server.GetClientAddress(connectionId);
	}

	public override void ServerStop()
	{
		if (server != null)
		{
			server.Stop();
		}
	}

	public override int GetMaxPacketSize(int channelId)
	{
		return maxMessageSize;
	}

	public override void Shutdown()
	{
		ClientDisconnect();
		ServerStop();
	}

	public override void ClientEarlyUpdate()
	{
		if (client == null)
		{
			return;
		}
		Message message;
		while (client.GetNextMessage(out message))
		{
			if (message.eventType == Telepathy.EventType.Connected)
			{
				Plugin.Log.LogInfo((object)"Transport client connected.");
				OnClientConnected?.Invoke();
				Plugin.Log.LogInfo((object)$"Mirror client state after connect: active={NetworkClient.active}, connected={NetworkClient.isConnected}, connection={NetworkClient.connection != null}.");
				break;
			}
			if (message.eventType == Telepathy.EventType.Data)
			{
				OnClientDataReceived?.Invoke(new ArraySegment<byte>(message.data), 0);
			}
			else if (message.eventType == Telepathy.EventType.Disconnected)
			{
				Plugin.Log.LogWarning((object)"Transport client disconnected.");
				if (SaveTransferRuntime.Instance != null)
				{
					SaveTransferRuntime.Instance.HandleTransportDisconnected();
				}
				if (WorldStateSync.Instance != null)
				{
					WorldStateSync.Instance.HandleTransportDisconnected();
				}
				if (SyncRuntime.Instance != null)
				{
					SyncRuntime.Instance.HandleTransportDisconnected();
				}
				OnClientDisconnected?.Invoke();
			}
		}
	}

	public override void ServerEarlyUpdate()
	{
		if (server == null)
		{
			return;
		}
		Message message;
		while (server.GetNextMessage(out message))
		{
			if (message.eventType == Telepathy.EventType.Connected)
			{
				ManualLogSource log = Plugin.Log;
				int connectionId = message.connectionId;
				log.LogInfo((object)("Transport server accepted connection " + connectionId + "."));
				OnServerConnectedWithAddress?.Invoke(message.connectionId, ServerGetClientAddress(message.connectionId));
				Plugin.Log.LogInfo((object)$"Mirror server connection registered={NetworkServer.connections.ContainsKey(message.connectionId)}, count={NetworkServer.connections.Count}.");
			}
			else if (message.eventType == Telepathy.EventType.Data)
			{
				OnServerDataReceived?.Invoke(message.connectionId, new ArraySegment<byte>(message.data), 0);
			}
			else if (message.eventType == Telepathy.EventType.Disconnected)
			{
				ManualLogSource log2 = Plugin.Log;
				int connectionId = message.connectionId;
				log2.LogWarning((object)("Transport server disconnected connection " + connectionId + "."));
				OnServerDisconnected?.Invoke(message.connectionId);
			}
		}
	}

	private static byte[] Copy(ArraySegment<byte> segment)
	{
		if (segment.Count == 0)
		{
			return new byte[0];
		}
		byte[] array = new byte[segment.Count];
		Buffer.BlockCopy(segment.Array, segment.Offset, array, 0, segment.Count);
		return array;
	}
}
