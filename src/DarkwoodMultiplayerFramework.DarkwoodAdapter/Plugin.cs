using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;
using DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing.Scenarios;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// Standalone 0.8 entry point. F1 starts Host, F2 starts Client, F3 stops the session.
/// TestMode (--dmf-test or DMF_TEST_ENABLED=1): auto-starts scenario via DualInstanceTestAgent.
/// </summary>
[BepInPlugin(Guid, Name, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.darkwood.multiplayer.framework.rebuilt.adapter";
    public const string Name = "Darkwood Multiplayer Framework - Darkwood Adapter";
    // BepInEx 5 parses this value as System.Version while scanning plugins.
    public const string PluginVersion = "0.8.9.3";
    public const string DisplayVersion = "0.8.9.3-pre.1";
    public const string Version = DisplayVersion;

    private GameObject? runtimeObject;
    private Harmony? harmony;

    private void Awake()
    {
        harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        runtimeObject = new GameObject("DarkwoodMultiplayerRebuiltAdapter");
        DontDestroyOnLoad(runtimeObject);
        var runtime = runtimeObject.AddComponent<DarkwoodAdapterRuntime>();
        runtime.Initialize(Logger);
        runtime.Configure(Config);
        runtimeObject.AddComponent<DarkwoodMultiplayerPanel>();
        runtimeObject.AddComponent<DarkwoodRescueOverlay>();
        var selfTest = runtimeObject.AddComponent<DarkwoodSelfTestClient>();
        if (runtime.AutoSelfTest) selfTest.AutoStart();
        // v0.9.2 P0-BUILD-IDENTITY
        Logger.LogInfo($"[BUILD] version={DarkwoodMultiplayerFramework.Core.BuildIdentity.Version} commit={(DarkwoodMultiplayerFramework.Core.BuildIdentity.GitCommit.Length >= 12 ? DarkwoodMultiplayerFramework.Core.BuildIdentity.GitCommit.Substring(0, 12) : DarkwoodMultiplayerFramework.Core.BuildIdentity.GitCommit)} dirty={DarkwoodMultiplayerFramework.Core.BuildIdentity.GitDirty} source={DarkwoodMultiplayerFramework.Core.BuildIdentity.SourceFingerprint} protocol={DarkwoodMultiplayerFramework.Core.BuildIdentity.ProtocolVersion} buildUtc={DarkwoodMultiplayerFramework.Core.BuildIdentity.BuildUtc}");
        Logger.LogInfo($"Darkwood adapter {DisplayVersion} loaded; host-authoritative entity ids + binding manifest + real world-stable registry gate; loopback self-test (SelfTestAuto).");

        // v0.9.2 TestHarness: only enabled if --dmf-test or DMF_TEST_ENABLED=1 is present.
        // Normal users see no behavior change.
        // Role Probe [TEST-BOOT] prints immediately regardless of TestMode result, so we can
        // diagnose BOTH "is the flag passed" AND "what role did we resolve to".
        try { EmitRoleProbe(); } catch (Exception error) { Logger.LogError($"[TEST-BOOT] probe exception: {error.Message}"); }

        if (!TestConfig.Enabled) return;

        try
        {
            var agent = new DualInstanceTestAgent(runtime);
            runtime.TestAgent = agent;
            runtime.TestCoordinator = new DualInstanceTestCoordinator(agent);
            switch ((TestConfig.Scenario ?? string.Empty).ToLowerInvariant())
            {
                case "doorsync": agent.SetScenario(new DoorSyncScenario(agent)); break;
                case "containersync": agent.SetScenario(new ContainerSyncScenario(agent)); break;
                case "droproundtrip": agent.SetScenario(new DropRoundTripScenario(agent)); break;
                default:
                    // Fail fast instead of silently continuing: an unknown/empty scenario means the
                    // harness launcher and the implemented scenario set drifted (e.g. old whitelist
                    // entries BearTrapSync/GuestIsolation that have no scenario class yet).
                    agent.Trace?.Log("SCENARIO-ERR", $"unknown scenario: '{TestConfig.Scenario}' (known: doorsync, containersync, droproundtrip)");
                    Logger.LogError($"[TESTHARNESS] unknown scenario '{TestConfig.Scenario}' - aborting harness init (known: doorsync/containersync/droproundtrip)");
                    return;
            }
            agent.Trace?.Log("AGENT-INIT", $"role={TestConfig.Role} runId={TestConfig.RunId} scenario={agent.Scenario} trace={agent.Trace.TracePath}");
            Logger.LogInfo($"[TESTHARNESS] enabled role={TestConfig.Role} runId={TestConfig.RunId} scenario={agent.Scenario}");
            // BOOT-TEST-1 minimum: just print role, then exit.
            if (TestConfig.IsHost)
            {
                runtime.StartHost();
                Logger.LogInfo("[TESTHARNESS] Host auto-started (TestMode)");
                Logger.LogInfo("[TEST-HOST-LISTENING] role=Host"); // Run-DualInstance.ps1 BOOT-TEST-3 轮询标记
            }
            else if (TestConfig.IsClient && !string.IsNullOrEmpty(TestConfig.Address))
            {
                runtime.StartCoroutine(AutoConnectClient(runtime, TestConfig.Address, TestConfig.Port));
            }
        }
        catch (Exception error) { Logger.LogError($"[TESTHARNESS] init failed: {error.Message}"); }
    }

    private static string Sha256OfFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "missing";
            using (var s = System.IO.File.OpenRead(path))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var b = sha.ComputeHash(s);
                return System.Convert.ToBase64String(b);
            }
        }
        catch { return "error"; }
    }

    private void EmitRoleProbe()
    {
        var cmdRole = ReadArg("--dmf-test-role");
        var envRole = System.Environment.GetEnvironmentVariable("DMF_TEST_ROLE") ?? string.Empty;
        var enabled = TestConfig.Enabled;
        var role = TestConfig.Role ?? "(none)";
        var runId = TestConfig.RunId ?? "(none)";
        var scenario = TestConfig.Scenario ?? "(none)";
        var port = TestConfig.Port.ToString();
        var addr = TestConfig.Address ?? "(none)";
        var saveSlot = TestConfig.SaveSlot ?? "(none)";
        var traceDir = TestConfig.TraceDir ?? "(none)";
        // DLL location of THIS assembly + SHA256
        var dllPath = TestConfig.AssemblyPath;
        var dllSha = Sha256OfFile(dllPath);
        // CommandLine args
        var cmd = TestConfig.CommandLine;
        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[TEST-BOOT] pid={pid}");
        sb.AppendLine($"[TEST-BOOT] exe={System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName}");
        sb.AppendLine($"[TEST-BOOT] dataPath={Application.dataPath}");
        sb.AppendLine($"[TEST-BOOT] pluginVersion={PluginVersion}");
        sb.AppendLine($"[TEST-BOOT] assemblyPath={dllPath}");
        sb.AppendLine($"[TEST-BOOT] assemblySha256={dllSha}");
        sb.AppendLine($"[TEST-BOOT] commandLine={cmd}");
        sb.AppendLine($"[TEST-BOOT] enabled={enabled}");
        sb.AppendLine($"[TEST-BOOT] envRole=\"{envRole}\"");
        sb.AppendLine($"[TEST-BOOT] cmdRole=\"{cmdRole}\"");
        sb.AppendLine($"[TEST-BOOT] effectiveRole={role}");
        sb.AppendLine($"[TEST-BOOT] runId={runId}");
        sb.AppendLine($"[TEST-BOOT] scenario={scenario}");
        sb.AppendLine($"[TEST-BOOT] address={addr} port={port}");
        sb.AppendLine($"[TEST-BOOT] saveSlot={saveSlot}");
        sb.AppendLine($"[TEST-BOOT] traceDir={traceDir}");
        // Echo each line individually so BepInEx log preserves them
        foreach (var line in sb.ToString().Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (t.Length > 0) Logger.LogInfo(t);
        }
    }

    private static string ReadArg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        if (args == null) return string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return args[i].Substring(name.Length + 1);
        }
        return string.Empty;
    }

    private System.Collections.IEnumerator AutoConnectClient(DarkwoodAdapterRuntime runtime, string address, ushort port)
    {
        yield return new WaitForSeconds(5f);
        runtime.ConnectClient();
        Logger.LogInfo($"[TESTHARNESS] Client auto-connected to {address}:{port}");
        yield break;
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
        harmony = null;
        if (runtimeObject != null)
        {
            Destroy(runtimeObject);
            runtimeObject = null;
        }
    }
}
