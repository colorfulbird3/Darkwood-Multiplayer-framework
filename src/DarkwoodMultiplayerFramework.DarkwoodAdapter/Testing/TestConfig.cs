using System;
using System.Collections.Generic;
using System.Reflection;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>
/// v0.9.2 TestHarness 测试模式配置。优先级：
///   1) Command line args (--dmf-test / --dmf-test-role= / --dmf-test-run-id= / ...)
///   2) Environment variables (DMF_TEST_*)
///   3) default (Disabled = false)
/// 只有 --dmf-test 或 DMF_TEST_ENABLED=1 才启用 TestMode。普通用户行为完全不变。
/// </summary>
public static class TestConfig
{
    public static bool Enabled { get; }
    public static string Role { get; }
    public static string RunId { get; }
    public static string Scenario { get; }
    public static ushort Port { get; }
    public static string Address { get; }
    public static string PlayerName { get; }
    public static string TraceDir { get; }
    public static string SaveSlot { get; }
    public static int ProcessReadyTimeoutSeconds { get; }
    public static string CommandLine { get; }
    public static string AssemblyPath { get; }

    static TestConfig()
    {
        // 收集所有来源（CLI > env > default）
        string[] args = Environment.GetCommandLineArgs() ?? Array.Empty<string>();
        CommandLine = string.Join(" ", args);
        AssemblyPath = Assembly.GetExecutingAssembly()?.Location ?? "(unknown)";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 1) Env (lower priority)
        TryAddEnv(map, "DMF_TEST_ENABLED");
        TryAddEnv(map, "DMF_TEST_ROLE");
        TryAddEnv(map, "DMF_TEST_RUN_ID");
        TryAddEnv(map, "DMF_TEST_SCENARIO");
        TryAddEnv(map, "DMF_TEST_PORT");
        TryAddEnv(map, "DMF_TEST_ADDRESS");
        TryAddEnv(map, "DMF_TEST_PLAYER_NAME");
        TryAddEnv(map, "DMF_TEST_TRACE_DIR");
        TryAddEnv(map, "DMF_TEST_SAVE_SLOT");
        TryAddEnv(map, "DMF_TEST_READY_TIMEOUT");
        // 2) CLI args (higher priority) — --dmf-test-X=value 或 --dmf-test-X value
        foreach (var a in args) ParseArg(a, map);

        // Enabled gate
        string enabledRaw;
        Enabled = map.TryGetValue("--dmf-test-enabled", out enabledRaw) && (enabledRaw == "1" || string.Equals(enabledRaw, "true", StringComparison.OrdinalIgnoreCase))
                  || map.ContainsKey("--dmf-test"); // --dmf-test 单独（无值）即启用
        if (!Enabled) { Role = null; RunId = null; Scenario = null; Address = null; PlayerName = null; TraceDir = null; SaveSlot = null; return; }

        Role = GetOr(map, "--dmf-test-role", "Unknown");
        RunId = GetOr(map, "--dmf-test-run-id", Guid.NewGuid().ToString("N"));
        Scenario = GetOr(map, "--dmf-test-scenario", string.Empty);
        Address = GetOr(map, "--dmf-test-address", "127.0.0.1");
        PlayerName = GetOr(map, "--dmf-test-player-name", "DMFTest");
        TraceDir = GetOr(map, "--dmf-test-trace-dir", System.IO.Path.Combine("TestHarness", "output", RunId));
        SaveSlot = GetOr(map, "--dmf-test-save", "TestSaveA");

        ushort port;
        Port = (ushort.TryParse(GetOr(map, "--dmf-test-port", "17777"), out port) ? port : (ushort)17777);

        int t;
        ProcessReadyTimeoutSeconds = (int.TryParse(GetOr(map, "--dmf-test-ready-timeout", "120"), out t) && t > 0) ? t : 120;
    }

    private static void TryAddEnv(Dictionary<string, string> map, string envName)
    {
        var v = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(v)) map[("--dmf-test-" + envName.Substring("DMF_TEST_".Length)).ToLowerInvariant()] = v;
    }

    private static void ParseArg(string a, Dictionary<string, string> map)
    {
        if (string.IsNullOrEmpty(a)) return;
        if (a == "--dmf-test") { map["--dmf-test-enabled"] = "1"; return; }
        if (a.StartsWith("--dmf-test-", StringComparison.OrdinalIgnoreCase))
        {
            var eq = a.IndexOf('=');
            string key, val;
            if (eq > 0) { key = a.Substring(0, eq).ToLowerInvariant(); val = a.Substring(eq + 1); }
            else { key = a.ToLowerInvariant(); val = "1"; }
            map[key] = val;
        }
    }

    private static string GetOr(Dictionary<string, string> map, string key, string def)
    {
        string v;
        return map.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : def;
    }

    public static bool IsHost => string.Equals(Role, "Host", StringComparison.OrdinalIgnoreCase);
    public static bool IsClient => string.Equals(Role, "Client", StringComparison.OrdinalIgnoreCase);
}
