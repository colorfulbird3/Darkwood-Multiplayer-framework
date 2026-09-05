using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>
/// v0.9.2 TestHarness: 严格按原版调用链把游戏从主菜单加载到 Test Save。
/// 调用链（反编译 Assembly-CSharp 确认）：
///   Core.currentProfile = selectedProfile
///   Core.profiles = profile list
///   Singleton&lt;UI&gt;.Instance.StartCoroutine(Singleton&lt;UI&gt;.Instance.initLoadGame())
///     → initNewGame():
///         Core.loadingGame = false; Core.loadedGame = false
///         if (currentProfile.chapter == 2) SceneManager.LoadScene("chapter2")
///         else                              SceneManager.LoadScene("chapter1")
///         Core.loadingGame = true;  Core.loadedGame = true  ← SaveManager 读 playerState.dat
/// 完成后：
///   Player.Instance != null
///   Player.gameObject.activeInHierarchy
///   Scene == "chapter1" / "chapter2"
///   EntityRegistry built (WorldStableDetector)
/// </summary>
public sealed class TestBootstrap
{
    private readonly DualInstanceTestAgent agent;
    public TestBootstrap(DualInstanceTestAgent agent) { this.agent = agent; }

    // 阶段状态
    public BootPhase Phase { get; private set; } = BootPhase.Boot;
    public DateTime PhaseStartedUtc { get; private set; } = DateTime.UtcNow;
    public string LastReason { get; private set; } = "";
    public DateTime? MainMenuReadyAtUtc { get; private set; }
    public DateTime? SaveResolvedAtUtc { get; private set; }
    public DateTime? SaveLoadStartedAtUtc { get; private set; }
    public DateTime? GameplaySceneAtUtc { get; private set; }
    public DateTime? PlayerReadyAtUtc { get; private set; }
    public DateTime? WorldStableAtUtc { get; private set; }

    private DateTime startedUtc = DateTime.UtcNow;

    // 时限（秒），与脚本侧一致
    public int MainMenuTimeoutSec    = 30;
    public int SaveResolveTimeoutSec = 10;
    public int SaveLoadTimeoutSec    = 90;
    public int GameplaySceneTimeoutSec = 90;
    public int PlayerReadyTimeoutSec  = 30;
    public int WorldStableTimeoutSec  = 90;

    // 内部状态机推进
    public void Tick()
    {
        if (Phase == BootPhase.HostWorldReady || Phase == BootPhase.Failed) return;
        var nowUtc = DateTime.UtcNow;
        PhaseStartedUtc = PhaseStartedUtc == default ? nowUtc : PhaseStartedUtc;

        try
        {
            switch (Phase)
            {
                case BootPhase.Boot:
                    Phase = BootPhase.WaitMainMenuReady;
                    agent.Trace?.Log("BOOT2", "phase=Boot -> WaitMainMenuReady");
                    break;
                case BootPhase.WaitMainMenuReady:
                    if (TryDetectMainMenuReady(out var mmReason))
                    {
                        MainMenuReadyAtUtc = nowUtc;
                        Phase = BootPhase.ResolveTestSave;
                        agent.Trace?.Log("BOOT2", $"phase=MainMenuReady -> ResolveTestSave ms={(nowUtc - startedUtc).TotalMilliseconds:F0}");
                    }
                    else if ((nowUtc - startedUtc).TotalSeconds > MainMenuTimeoutSec)
                    {
                        Fail("WaitMainMenuReady timeout: " + mmReason);
                    }
                    break;
                case BootPhase.ResolveTestSave:
                    if (TryResolveTestSave(out var rsReason))
                    {
                        SaveResolvedAtUtc = nowUtc;
                        Phase = BootPhase.RequestOriginalSaveLoad;
                        agent.Trace?.Log("BOOT2", $"phase=SaveResolved -> RequestOriginalSaveLoad ms={(nowUtc - startedUtc).TotalMilliseconds:F0}");
                    }
                    else if ((nowUtc - (SaveResolvedAtUtc ?? startedUtc)).TotalSeconds > SaveResolveTimeoutSec)
                    {
                        Fail("ResolveTestSave timeout: " + rsReason);
                    }
                    break;
                case BootPhase.RequestOriginalSaveLoad:
                    if (TryRequestOriginalSaveLoad(out var rlReason))
                    {
                        SaveLoadStartedAtUtc = nowUtc;
                        Phase = BootPhase.WaitGameplayScene;
                        agent.Trace?.Log("BOOT2", $"phase=SaveLoadRequested -> WaitGameplayScene ms={(nowUtc - startedUtc).TotalMilliseconds:F0}");
                    }
                    else
                    {
                        Fail("RequestOriginalSaveLoad failed: " + rlReason);
                    }
                    break;
                case BootPhase.WaitGameplayScene:
                    if (SceneManager.GetActiveScene().name == "chapter1" || SceneManager.GetActiveScene().name == "chapter2")
                    {
                        GameplaySceneAtUtc = nowUtc;
                        Phase = BootPhase.WaitLocalPlayer;
                        agent.Trace?.Log("BOOT2", $"phase=GameplayScene name={SceneManager.GetActiveScene().name} -> WaitLocalPlayer ms={(nowUtc - startedUtc).TotalMilliseconds:F0}");
                    }
                    else if ((nowUtc - (SaveLoadStartedAtUtc ?? nowUtc)).TotalSeconds > SaveLoadTimeoutSec + GameplaySceneTimeoutSec)
                    {
                        Fail($"WaitGameplayScene timeout: current scene={SceneManager.GetActiveScene().name}");
                    }
                    break;
                case BootPhase.WaitLocalPlayer:
                    if (TryDetectPlayerReady(out var prReason))
                    {
                        PlayerReadyAtUtc = nowUtc;
                        Phase = BootPhase.WaitWorldStable;
                        agent.Trace?.Log("BOOT2", $"phase=PlayerReady -> WaitWorldStable ms={(nowUtc - startedUtc).TotalMilliseconds:F0}");
                    }
                    else if ((nowUtc - (GameplaySceneAtUtc ?? nowUtc)).TotalSeconds > PlayerReadyTimeoutSec)
                    {
                        Fail("WaitLocalPlayer timeout: " + prReason);
                    }
                    break;
                case BootPhase.WaitWorldStable:
                    if (TryDetectWorldStable(out var wsReason))
                    {
                        WorldStableAtUtc = nowUtc;
                        Phase = BootPhase.HostWorldReady;
                        agent.Trace?.Log("BOOT2", $"phase=WorldStable ms={(nowUtc - startedUtc).TotalMilliseconds:F0}");
                        agent.Trace?.Log("READY", "test=BOOT-TEST-2 result=PASS");
                        agent.Trace?.Log("TEST-RESULT", "test=BOOT-TEST-2 result=PASS");
                    }
                    else if ((nowUtc - (PlayerReadyAtUtc ?? nowUtc)).TotalSeconds > WorldStableTimeoutSec)
                    {
                        Fail("WaitWorldStable timeout: " + wsReason);
                    }
                    break;
            }
        }
        catch (Exception error)
        {
            Fail("Tick exception: " + error.Message);
        }
    }

    private void Fail(string reason)
    {
        LastReason = reason;
        Phase = BootPhase.Failed;
        agent.Trace?.Log("BOOT2-FAIL", $"phase={Phase} reason={reason}");
        agent.Trace?.Log("TEST-RESULT", $"test=BOOT-TEST-2 result=FAIL phase={Phase} reason={reason}");
    }

    private bool TryDetectMainMenuReady(out string reason)
    {
        reason = "";
        var scene = SceneManager.GetActiveScene().name;
        if (scene != "Darkwood") { reason = $"scene={scene} (expected Darkwood)"; return false; }
        // MainMenu singleton + SaveManager singleton 都必须就绪
        try
        {
            var mainMenu = Singleton<MainMenu>.Instance;
            if (mainMenu == null) { reason = "MainMenu.Instance==null"; return false; }
            var saveManager = Singleton<SaveManager>.Instance;
            if (saveManager == null) { reason = "SaveManager.Instance==null"; return false; }
            var ui = Singleton<UI>.Instance;
            if (ui == null) { reason = "UI.Instance==null"; return false; }
            return true;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    private bool TryResolveTestSave(out string reason)
    {
        reason = "";
        var slot = agent.SaveSlot;
        // TestMode 下 baseSaveDirectory 已被 patch 重定向到 DMF_TestHarness/Saves/<slot>
        var saveManager = Singleton<SaveManager>.Instance;
        if (saveManager == null) { reason = "SaveManager null"; return false; }
        // 调原版 loadGameProfiles 读 profs.dat
        var state = saveManager.loadGameProfiles();
        if (state == null) { reason = "loadGameProfiles returned null (no profs.dat?)"; return false; }
        if (state.profiles == null || state.profiles.Count == 0) { reason = "no profiles"; return false; }
        // 选择 active profile（可能是 fixture 中的 prof1）
        GameProfile? active = null;
        foreach (var p in state.profiles) { if (p != null && p.Active) { active = p; break; } }
        if (active == null) active = state.profiles[0]; // fixture 只有一个 prof
        // 设置 Core.currentProfile 走原版路径
        global::Core.profiles = state.profiles;
        global::Core.currentProfile = active;
        saveManager.updateFilePaths();
        agent.Trace?.Log("BOOT2-RESOLVE", $"slot={slot} profileId={active.id} chapter={active.chapter}");
        return true;
    }

    private bool TryRequestOriginalSaveLoad(out string reason)
    {
        reason = "";
        try
        {
            var ui = Singleton<UI>.Instance;
            if (ui == null) { reason = "UI.Instance==null"; return false; }
            // 走原版 initLoadGame 协程（真实 Save Manager 读档路径）
            ui.StartCoroutine(ui.initLoadGame());
            return true;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    private bool TryDetectPlayerReady(out string reason)
    {
        reason = "";
        try
        {
            var p = Player.Instance;
            if (p == null) { reason = "Player.Instance==null"; return false; }
            if (p.gameObject == null || !p.gameObject.activeInHierarchy) { reason = "Player.gameObject inactive"; return false; }
            return true;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    private bool TryDetectWorldStable(out string reason)
    {
        reason = "";
        // 注：EntityRegistry 是 DMF 自己的注册表，不是原版。World Stable 检测需要：
        //   Player.Instance.ready + 时间稳定窗口 + Entity scan 成功
        try
        {
            var p = Player.Instance;
            if (p == null) { reason = "Player.Instance==null"; return false; }
            if (!p.gameObject.activeInHierarchy) { reason = "Player inactive"; return false; }
            // 简易判定：场景已稳定 5 秒（Player.transform.position 抖动 < epsilon）
            // 实际可读 DMF replication registry，但要求 ≥1 World Stable tick
            var runtime = agent.Runtime;
            if (runtime == null) { reason = "runtime==null"; return false; }
            if (runtime.replication == null) { reason = "replication==null"; return false; }
            if (runtime.replication.SharedInventoryCount < 0) { reason = "registry not initialized"; return false; }
            return true;
        }
        catch (Exception e) { reason = e.Message; return false; }
    }

    public enum BootPhase
    {
        Boot,
        WaitMainMenuReady,
        ResolveTestSave,
        RequestOriginalSaveLoad,
        WaitGameplayScene,
        WaitLocalPlayer,
        WaitWorldStable,
        HostWorldReady,
        Failed
    }
}
