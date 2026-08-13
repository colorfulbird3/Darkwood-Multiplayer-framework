using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework;

internal sealed class SaveTransferRuntime : MonoBehaviour
{
	private sealed class Outgoing
	{
		public NetworkConnectionToClient Connection;

		public string Id;

		public byte[][] Chunks;

		public int Next;
	}

	private sealed class Incoming
	{
		public SaveTransferManifest Manifest;

		public byte[][] Chunks;

		public int Received;

		public int Bytes;
	}

	private const int Protocol = 2;

	private const int ChunkSize = 131072;

	private const int MaxBundleBytes = 134217728;

	private const int MaxFiles = 24;

	private const int BundleMagic = 1146572112;

	private static readonly string[] SaveFileNames = new string[6] { "sav.dat", "sav.dat_bak", "savs.dat", "savs.dat_bak", "savch.dat", "savch.dat_bak" };

	private readonly Dictionary<int, Outgoing> outgoing = new Dictionary<int, Outgoing>();

	private Incoming incoming;

	private bool serverHandler;

	private bool clientHandlers;

	private bool requestSent;

	private bool loadStarted;

	private bool wasClientConnected;

	private bool disconnectHandled;

	private float loadDeadline;

	private float nextStabilityProbe;

	private int lastRegistryCount;

	private ulong lastRegistryDigest;

	private int stableRegistrySamples;

	private int incomingProfileId;

	public static SaveTransferRuntime Instance { get; private set; }

	public static string ActiveClientSaveDirectory { get; private set; }

	public static bool ClientReadyForWorld { get; private set; }

	public static string StatusText { get; private set; } = "";


	public static bool CanUseWorldSync
	{
		get
		{
			if (!NetworkServer.active && !(Instance == null))
			{
				return ClientReadyForWorld;
			}
			return true;
		}
	}

	private void Awake()
	{
		Instance = this;
		SaveTransferSerializers.Install();
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnSceneLoaded;
		try
		{
			if (Singleton<SaveManager>.Instance != null)
			{
				SaveManager instance = Singleton<SaveManager>.Instance;
				instance.onFinishedLoading = (saveDelegate)Delegate.Remove(instance.onFinishedLoading, new saveDelegate(OnDownloadedSaveFinished));
			}
		}
		catch
		{
		}
		if (Instance == this)
		{
			Instance = null;
		}
	}

	private void Update()
	{
		RegisterHandlers();
		bool flag = NetworkClient.active && NetworkClient.isConnected && !NetworkServer.active;
		if (flag)
		{
			disconnectHandled = false;
		}
		if (flag && !requestSent)
		{
			requestSent = true;
			ClientReadyForWorld = false;
			StatusText = "正在请求主机存档…";
			SaveTransferRequest message = default(SaveTransferRequest);
			message.Protocol = 2;
			NetworkClient.Send(message);
			Plugin.Log.LogInfo((object)"Requested host save snapshot.");
		}
		if (wasClientConnected && !flag && !NetworkServer.active)
		{
			HandleTransportDisconnected();
		}
		wasClientConnected = flag;
		if (!NetworkClient.active && !NetworkServer.active)
		{
			requestSent = false;
			incoming = null;
		}
		PumpOutgoing();
		if (!loadStarted || ClientReadyForWorld)
		{
			return;
		}
		if (!Core.mainMenu && Player.Instance != null && Time.unscaledTime >= nextStabilityProbe)
		{
			nextStabilityProbe = Time.unscaledTime + 4f;
			int count = 0;
			ulong digest = 0uL;
			if (WorldStateSync.Instance != null)
			{
				WorldStateSync.Instance.RefreshAfterDownloadedSave(out count, out digest);
			}
			if (count >= 100 && count == lastRegistryCount && digest == lastRegistryDigest)
			{
				stableRegistrySamples++;
			}
			else
			{
				stableRegistrySamples = 0;
			}
			lastRegistryCount = count;
			lastRegistryDigest = digest;
			StatusText = $"正在等待世界实体加载：{count}（稳定 {stableRegistrySamples}/2）";
			if (stableRegistrySamples >= 2)
			{
				ClientReadyForWorld = true;
				StatusText = "主机存档已加载，世界同步已启用";
				Plugin.Log.LogInfo((object)"Downloaded host save is active; world synchronization enabled.");
			}
		}
		else if (Time.unscaledTime > loadDeadline)
		{
			loadStarted = false;
			StatusText = "存档加载超时，请查看日志";
			Plugin.Log.LogError((object)"Downloaded save load timed out.");
		}
	}

	internal void HandleTransportDisconnected()
	{
		if (NetworkServer.active || disconnectHandled)
		{
			return;
		}
		disconnectHandled = true;
		wasClientConnected = false;
		requestSent = false;
		incoming = null;
		loadStarted = false;
		ClientReadyForWorld = false;
		StatusText = "与主机的连接已断开，请返回主菜单后重新连接。";
		try
		{
			SaveManager instance = Singleton<SaveManager>.Instance;
			if (instance != null)
			{
				instance.onFinishedLoading = (saveDelegate)Delegate.Remove(instance.onFinishedLoading, new saveDelegate(OnDownloadedSaveFinished));
			}
		}
		catch
		{
		}
		Plugin.Log.LogWarning((object)"Multiplayer client state cleared after transport disconnect.");
	}

	private void RegisterHandlers()
	{
		if (NetworkServer.active && !serverHandler)
		{
			NetworkServer.ReplaceHandler<SaveTransferRequest>(OnServerRequest, requireAuthentication: false);
			serverHandler = true;
			Plugin.Log.LogInfo((object)"Save-transfer server handler registered.");
		}
		if (!NetworkServer.active)
		{
			serverHandler = false;
			outgoing.Clear();
		}
		if (NetworkClient.active && !clientHandlers)
		{
			NetworkClient.ReplaceHandler<SaveTransferManifest>(OnClientManifest, requireAuthentication: false);
			NetworkClient.ReplaceHandler<SaveTransferChunk>(OnClientChunk, requireAuthentication: false);
			clientHandlers = true;
			Plugin.Log.LogInfo((object)"Save-transfer client handlers registered.");
		}
		if (!NetworkClient.active)
		{
			clientHandlers = false;
		}
	}

	private void OnServerRequest(NetworkConnectionToClient connection, SaveTransferRequest request)
	{
		if (request.Protocol != 2)
		{
			SendError(connection, "联机存档协议版本不一致。双方必须安装相同版本框架。");
			return;
		}
		try
		{
			int profileId;
			string description;
			byte[] array = BuildHostBundle(out profileId, out description);
			if (array.Length == 0 || array.Length > 134217728)
			{
				throw new InvalidDataException("存档包大小超出限制。");
			}
			string text = Guid.NewGuid().ToString("N");
			byte[][] array2 = Split(array, 131072);
			Outgoing value = new Outgoing
			{
				Connection = connection,
				Id = text,
				Chunks = array2
			};
			outgoing[connection.connectionId] = value;
			connection.Send(new SaveTransferManifest
			{
				TransferId = text,
				ProfileId = profileId,
				ProfileDescription = description,
				TotalBytes = array.Length,
				ChunkCount = array2.Length,
				Sha256 = Sha256(array),
				Error = ""
			});
			Plugin.Log.LogInfo((object)$"Prepared save snapshot for connection {connection.connectionId}: profile {profileId}, {array.Length} bytes, {array2.Length} chunks.");
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError((object)("Failed preparing host save: " + ex));
			SendError(connection, "主机无法读取当前存档。请让主机先加载一个有效存档再按 F1。");
		}
	}

	private void SendError(NetworkConnectionToClient connection, string error)
	{
		connection.Send(new SaveTransferManifest
		{
			Error = error,
			TransferId = ""
		});
	}

	private void PumpOutgoing()
	{
		if (!NetworkServer.active || outgoing.Count == 0)
		{
			return;
		}
		List<int> list = new List<int>();
		foreach (KeyValuePair<int, Outgoing> item in outgoing)
		{
			Outgoing value = item.Value;
			if (value.Connection == null || (!value.Connection.isReady && !NetworkServer.connections.ContainsKey(item.Key)))
			{
				list.Add(item.Key);
				continue;
			}
			int num = 0;
			while (value.Next < value.Chunks.Length && num < 2)
			{
				value.Connection.Send(new SaveTransferChunk
				{
					TransferId = value.Id,
					Index = value.Next,
					Data = value.Chunks[value.Next]
				});
				value.Next++;
				num++;
			}
			if (value.Next >= value.Chunks.Length)
			{
				Plugin.Log.LogInfo((object)$"Finished sending save snapshot to connection {item.Key}.");
				list.Add(item.Key);
			}
		}
		foreach (int item2 in list)
		{
			outgoing.Remove(item2);
		}
	}

	private void OnClientManifest(SaveTransferManifest manifest)
	{
		if (!NetworkServer.active)
		{
			if (!string.IsNullOrEmpty(manifest.Error))
			{
				StatusText = manifest.Error;
				Plugin.Log.LogError((object)("Host save transfer refused: " + manifest.Error));
				return;
			}
			if (string.IsNullOrEmpty(manifest.TransferId) || manifest.TotalBytes <= 0 || manifest.TotalBytes > 134217728 || manifest.ChunkCount <= 0 || manifest.ChunkCount > 1026)
			{
				StatusText = "主机发送了无效的存档清单";
				Plugin.Log.LogError((object)"Invalid save transfer manifest.");
				return;
			}
			incoming = new Incoming
			{
				Manifest = manifest,
				Chunks = new byte[manifest.ChunkCount][]
			};
			StatusText = $"正在下载主机存档：0/{manifest.ChunkCount}";
			Plugin.Log.LogInfo((object)$"Receiving host save profile {manifest.ProfileId}: {manifest.TotalBytes} bytes in {manifest.ChunkCount} chunks.");
		}
	}

	private void OnClientChunk(SaveTransferChunk chunk)
	{
		if (NetworkServer.active || incoming == null || chunk.TransferId != incoming.Manifest.TransferId)
		{
			return;
		}
		if (chunk.Index < 0 || chunk.Index >= incoming.Chunks.Length || chunk.Data == null || chunk.Data.Length > 131072)
		{
			FailIncoming("收到无效的存档分块。");
		}
		else if (incoming.Chunks[chunk.Index] == null)
		{
			incoming.Chunks[chunk.Index] = chunk.Data;
			incoming.Received++;
			incoming.Bytes += chunk.Data.Length;
			StatusText = $"正在下载主机存档：{incoming.Received}/{incoming.Chunks.Length}";
			if (incoming.Bytes > incoming.Manifest.TotalBytes || incoming.Bytes > 134217728)
			{
				FailIncoming("存档下载大小超出清单。");
			}
			else if (incoming.Received == incoming.Chunks.Length)
			{
				CompleteIncoming();
			}
		}
	}

	private void CompleteIncoming()
	{
		try
		{
			StatusText = "正在校验主机存档…";
			byte[] array = Combine(incoming.Chunks, incoming.Manifest.TotalBytes);
			if (!Sha256(array).Equals(incoming.Manifest.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("SHA256 校验失败。");
			}
			ActiveClientSaveDirectory = InstallClientBundle(array, incoming.Manifest.ProfileId);
			incoming = null;
			StatusText = "下载完成，正在加载主机存档…";
			StartCoroutine(LoadDownloadedSave());
		}
		catch (Exception ex)
		{
			Plugin.Log.LogError((object)("Failed installing downloaded save: " + ex));
			FailIncoming("主机存档校验或安装失败，请查看日志。");
		}
	}

	private IEnumerator LoadDownloadedSave()
	{
		yield return null;
		if (!Core.mainMenu)
		{
			StatusText = "存档已下载；客户端需停留在主菜单连接";
			Plugin.Log.LogError((object)"Automatic save loading is only allowed from the main menu.");
			yield break;
		}
		SaveManager instance = Singleton<SaveManager>.Instance;
		MainMenu.SaveState saveState = instance.loadGameProfiles();
		if (saveState == null || saveState.profiles == null)
		{
			throw new InvalidDataException("Downloaded profiles file could not be loaded.");
		}
		GameProfile gameProfile = saveState.profiles.FirstOrDefault((GameProfile p) => p != null && p.id == incomingProfileId);
		if (gameProfile == null || !gameProfile.Active)
		{
			throw new InvalidDataException("Downloaded profile was not found in profiles metadata.");
		}
		Core.profiles = saveState.profiles;
		Core.currentProfile = gameProfile;
		instance.updateFilePaths();
		loadStarted = true;
		nextStabilityProbe = 0f;
		lastRegistryCount = 0;
		lastRegistryDigest = 0uL;
		stableRegistrySamples = 0;
		loadDeadline = Time.unscaledTime + 150f;
		instance.onFinishedLoading = (saveDelegate)Delegate.Remove(instance.onFinishedLoading, new saveDelegate(OnDownloadedSaveFinished));
		instance.onFinishedLoading = (saveDelegate)Delegate.Combine(instance.onFinishedLoading, new saveDelegate(OnDownloadedSaveFinished));
		Plugin.Log.LogInfo((object)$"Loading downloaded multiplayer profile {gameProfile.id}: day {gameProfile.day}, chapter {gameProfile.chapter}.");
		UI instance2 = Singleton<UI>.Instance;
		instance2.StartCoroutine(instance2.initLoadGame());
	}

	private void OnDownloadedSaveFinished()
	{
		try
		{
			SaveManager instance = Singleton<SaveManager>.Instance;
			if (instance != null)
			{
				instance.onFinishedLoading = (saveDelegate)Delegate.Remove(instance.onFinishedLoading, new saveDelegate(OnDownloadedSaveFinished));
			}
		}
		catch
		{
		}
		Plugin.Log.LogInfo((object)"Darkwood reported downloaded save loading finished.");
	}

	private string InstallClientBundle(byte[] compressed, int expectedProfileId)
	{
		string path = HostKey();
		string path2 = Path.Combine(Paths.BepInExRootPath, "DarkwoodMPClientSaves", path);
		string text = Path.Combine(path2, "1_4Save");
		string text2 = Path.Combine(path2, ".incoming-" + Guid.NewGuid().ToString("N"));
		string text3 = Path.Combine(path2, ".previous");
		Directory.CreateDirectory(text2);
		int num = ExtractBundle(compressed, text2);
		if (num != expectedProfileId)
		{
			throw new InvalidDataException("Profile id mismatch.");
		}
		incomingProfileId = num;
		if (Directory.Exists(text3))
		{
			Directory.Delete(text3, recursive: true);
		}
		if (Directory.Exists(text))
		{
			Directory.Move(text, text3);
		}
		Directory.Move(text2, text);
		Plugin.Log.LogInfo((object)("Installed downloaded save into isolated cache: " + text));
		return text;
	}

	private static byte[] BuildHostBundle(out int profileId, out string description)
	{
		if (Core.currentProfile == null || !Core.currentProfile.Active)
		{
			throw new InvalidOperationException("No active host profile.");
		}
		profileId = Core.currentProfile.id;
		if (profileId < 1 || profileId > 5)
		{
			throw new InvalidDataException("Invalid profile id.");
		}
		description = $"第 {Core.currentProfile.day} 天 / 第 {Core.currentProfile.chapter} 章 / {Core.currentProfile.timeSaved}";
		string baseSaveDirectory = Singleton<SaveManager>.Instance.baseSaveDirectory;
		string directory = Path.Combine(baseSaveDirectory, "prof" + profileId);
		List<KeyValuePair<string, byte[]>> list = new List<KeyValuePair<string, byte[]>>();
		AddRequired(list, baseSaveDirectory, "profs.dat");
		AddOptional(list, baseSaveDirectory, "profs.dat_bak");
		AddOptional(list, baseSaveDirectory, "playerState.dat");
		string[] saveFileNames = SaveFileNames;
		foreach (string text in saveFileNames)
		{
			AddOptional(list, directory, Path.Combine("prof" + profileId, text), text);
		}
		if (!list.Any((KeyValuePair<string, byte[]> f) => f.Key.EndsWith("sav.dat", StringComparison.OrdinalIgnoreCase)) || !list.Any((KeyValuePair<string, byte[]> f) => f.Key.EndsWith("savs.dat", StringComparison.OrdinalIgnoreCase)))
		{
			throw new FileNotFoundException("Active profile save files are missing.");
		}
		using MemoryStream memoryStream = new MemoryStream();
		using (GZipStream output = new GZipStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true))
		{
			using BinaryWriter binaryWriter = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
			binaryWriter.Write(1146572112);
			binaryWriter.Write(2);
			binaryWriter.Write(profileId);
			binaryWriter.Write(list.Count);
			foreach (KeyValuePair<string, byte[]> item in list)
			{
				binaryWriter.Write(item.Key.Replace('\\', '/'));
				binaryWriter.Write(item.Value.Length);
				binaryWriter.Write(item.Value);
			}
		}
		return memoryStream.ToArray();
	}

	private static void AddRequired(List<KeyValuePair<string, byte[]>> files, string directory, string name)
	{
		string text = Path.Combine(directory, name);
		if (!File.Exists(text))
		{
			throw new FileNotFoundException("Required save file missing.", text);
		}
		files.Add(new KeyValuePair<string, byte[]>(name, ReadStable(text)));
	}

	private static void AddOptional(List<KeyValuePair<string, byte[]>> files, string directory, string relative, string fileName = null)
	{
		string path = Path.Combine(directory, fileName ?? relative);
		if (File.Exists(path))
		{
			files.Add(new KeyValuePair<string, byte[]>(relative, ReadStable(path)));
		}
	}

	private static byte[] ReadStable(string path)
	{
		using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		if (fileStream.Length < 0 || fileStream.Length > 134217728)
		{
			throw new InvalidDataException("Save file too large: " + path);
		}
		byte[] array = new byte[fileStream.Length];
		int num;
		for (int i = 0; i < array.Length; i += num)
		{
			num = fileStream.Read(array, i, array.Length - i);
			if (num <= 0)
			{
				throw new EndOfStreamException(path);
			}
		}
		return array;
	}

	private static int ExtractBundle(byte[] compressed, string destination)
	{
		using MemoryStream stream = new MemoryStream(compressed, writable: false);
		using GZipStream input = new GZipStream(stream, CompressionMode.Decompress);
		using BinaryReader binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
		if (binaryReader.ReadInt32() != 1146572112 || binaryReader.ReadInt32() != 2)
		{
			throw new InvalidDataException("Invalid save bundle header.");
		}
		int num = binaryReader.ReadInt32();
		int num2 = binaryReader.ReadInt32();
		if (num < 1 || num > 5 || num2 <= 0 || num2 > 24)
		{
			throw new InvalidDataException("Invalid save bundle metadata.");
		}
		string text = Path.GetFullPath(destination).TrimEnd(new char[1] { Path.DirectorySeparatorChar });
		char directorySeparatorChar = Path.DirectorySeparatorChar;
		string text2 = text + directorySeparatorChar;
		long num3 = 0L;
		for (int i = 0; i < num2; i++)
		{
			string text3 = binaryReader.ReadString().Replace('/', Path.DirectorySeparatorChar);
			if (!IsAllowedRelativePath(text3, num))
			{
				throw new InvalidDataException("Rejected save path: " + text3);
			}
			int num4 = binaryReader.ReadInt32();
			num3 += num4;
			if (num4 < 0 || num3 > 134217728)
			{
				throw new InvalidDataException("Invalid extracted size.");
			}
			byte[] array = binaryReader.ReadBytes(num4);
			if (array.Length != num4)
			{
				throw new EndOfStreamException(text3);
			}
			string fullPath = Path.GetFullPath(Path.Combine(text2, text3));
			if (!fullPath.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("Path escaped save directory.");
			}
			Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
			File.WriteAllBytes(fullPath, array);
		}
		return num;
	}

	private static bool IsAllowedRelativePath(string relative, int profileId)
	{
		if (relative.Equals("profs.dat", StringComparison.OrdinalIgnoreCase) || relative.Equals("profs.dat_bak", StringComparison.OrdinalIgnoreCase) || relative.Equals("playerState.dat", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		string text = profileId.ToString();
		char directorySeparatorChar = Path.DirectorySeparatorChar;
		string text2 = "prof" + text + directorySeparatorChar;
		if (!relative.StartsWith(text2, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string name = relative.Substring(text2.Length);
		return SaveFileNames.Any((string x) => x.Equals(name, StringComparison.OrdinalIgnoreCase));
	}

	private static byte[][] Split(byte[] data, int size)
	{
		int num = (data.Length + size - 1) / size;
		byte[][] array = new byte[num][];
		for (int i = 0; i < num; i++)
		{
			int num2 = Math.Min(size, data.Length - i * size);
			array[i] = new byte[num2];
			Buffer.BlockCopy(data, i * size, array[i], 0, num2);
		}
		return array;
	}

	private static byte[] Combine(byte[][] chunks, int total)
	{
		byte[] array = new byte[total];
		int num = 0;
		foreach (byte[] array2 in chunks)
		{
			if (array2 == null || num + array2.Length > total)
			{
				throw new InvalidDataException("Missing or oversized chunk.");
			}
			Buffer.BlockCopy(array2, 0, array, num, array2.Length);
			num += array2.Length;
		}
		if (num != total)
		{
			throw new InvalidDataException("Downloaded size mismatch.");
		}
		return array;
	}

	private static string Sha256(byte[] data)
	{
		using SHA256 sHA = SHA256.Create();
		return BitConverter.ToString(sHA.ComputeHash(data)).Replace("-", "");
	}

	private static string HostKey()
	{
		string s = (Plugin.Address?.Value ?? "host") + ":" + (Plugin.Port?.Value ?? 7777);
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		using SHA256 sHA = SHA256.Create();
		return BitConverter.ToString(sHA.ComputeHash(bytes), 0, 8).Replace("-", "");
	}

	private void FailIncoming(string message)
	{
		incoming = null;
		StatusText = message;
		Plugin.Log.LogError((object)message);
	}

	private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (loadStarted && !Core.mainMenu)
		{
			Plugin.Log.LogInfo((object)("Downloaded save scene loaded: " + scene.name));
		}
	}
}
