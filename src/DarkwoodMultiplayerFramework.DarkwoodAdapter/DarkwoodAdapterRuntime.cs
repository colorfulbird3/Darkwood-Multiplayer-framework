using System;
using BepInEx.Logging;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Darkwood-specific boundary. It owns scene/player discovery while protocol logic stays in src modules.</summary>
public sealed class DarkwoodAdapterRuntime : MonoBehaviour
{
    public static DarkwoodAdapterRuntime? Instance { get; private set; }
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string CurrentScene => SceneManager.GetActiveScene().name;
    public Player? LocalPlayer => Player.Instance;
    public int RegistryCount => registry?.Count ?? 0;
    public string RegistryDigest { get; private set; } = string.Empty;
    public string LegacyStatus => legacy.StatusText;
    public event Action<string>? SceneChanged;
    public event Action<ConnectionState>? StateChanged;

    private readonly LegacyRuntimeBridge legacy = new LegacyRuntimeBridge();
    private readonly DarkwoodEntityScanner scanner = new DarkwoodEntityScanner();
    private EntityRegistry<Component>? registry;
    private ManualLogSource? log;
    private string lastScene = string.Empty;
    private bool registryDirty = true;
    private float nextLegacyProbe;

    public void Initialize(ManualLogSource logger)
    {
        log = logger;
        legacy.Refresh();
        log.LogInfo(legacy.IsAvailable
            ? "Legacy 0.7 runtime detected; compatibility bridge enabled."
            : "Legacy 0.7 runtime not detected; adapter remains passive.");
    }

    public void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        lastScene = CurrentScene;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    public void Update()
    {
        if (Time.unscaledTime >= nextLegacyProbe)
        {
            nextLegacyProbe = Time.unscaledTime + 2f;
            if (!legacy.IsAvailable) legacy.Refresh();
        }

        var scene = CurrentScene;
        if (!string.Equals(scene, lastScene, StringComparison.Ordinal)) MarkSceneChanged(scene);

        SetState(DetectState());
        if (registryDirty && IsNetworkConnected() && Player.Instance != null)
        {
            RebuildRegistry();
            SetState(DetectState());
        }
    }

    public void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        if (ReferenceEquals(Instance, this)) Instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => MarkSceneChanged(scene.name);

    private void MarkSceneChanged(string scene)
    {
        lastScene = scene;
        registryDirty = true;
        registry = null;
        RegistryDigest = string.Empty;
        SceneChanged?.Invoke(scene);
    }

    private void RebuildRegistry()
    {
        var next = new EntityRegistry<Component>();
        var collisions = 0;
        foreach (var component in scanner.ScanScene())
        {
            var id = scanner.ToPersistentId(component);
            try { next.Register(id, component); }
            catch (InvalidOperationException)
            {
                collisions++;
                log?.LogWarning($"Duplicate Darkwood entity id {id} for {component.GetType().Name} at {component.transform.name}.");
            }
        }
        registry = next;
        RegistryDigest = next.ComputeDigest();
        registryDirty = false;
        log?.LogInfo($"Darkwood adapter registry ready: {next.Count} entities, {collisions} collisions, digest {RegistryDigest}, scene {CurrentScene}.");
    }

    private void SetState(ConnectionState next)
    {
        if (State == next) return;
        State = next;
        log?.LogInfo($"Darkwood adapter state: {next}.");
        StateChanged?.Invoke(next);
    }

    private ConnectionState DetectState()
    {
        if (!NetworkClient.active && !NetworkServer.active) return ConnectionState.Disconnected;
        if (NetworkClient.active && !NetworkClient.isConnected) return ConnectionState.Connecting;
        if (!NetworkServer.active && legacy.IsAvailable && !legacy.CanUseWorldSync) return ConnectionState.SaveTransfer;
        if (Player.Instance == null) return ConnectionState.LoadingSave;
        if (registryDirty) return ConnectionState.BuildingRegistry;
        return ConnectionState.Ready;
    }

    private static bool IsNetworkConnected()
        => NetworkServer.active || (NetworkClient.active && NetworkClient.isConnected);
}
