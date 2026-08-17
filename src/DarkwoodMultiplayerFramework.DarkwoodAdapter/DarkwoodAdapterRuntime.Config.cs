using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed partial class DarkwoodAdapterRuntime
{
    private ConfigEntry<string>? addressConfig;
    private ConfigEntry<int>? portConfig;
    private ConfigEntry<int>? playerCountConfig;
    private ConfigEntry<string>? playerNameConfig;
    private ConfigEntry<string>? starterKitTier1Config;
    private ConfigEntry<string>? starterKitTier2Config;
    private ConfigEntry<string>? starterKitTier3Config;
    private ConfigEntry<int>? starterKitTier2DayConfig;
    private ConfigEntry<int>? starterKitTier3DayConfig;
    private ConfigEntry<bool>? autoSelfTestConfig;
    public void Initialize(ManualLogSource logger)
    {
        log = logger;
        if (Players != null) Players.RemotePlayers.Logger = message => log?.LogInfo(message); // 服务在 Awake 构造（Initialize 可能先于 Awake 执行）
        log.LogInfo("Darkwood 联机适配层已初始化（0.8）。");
    }
    public void Configure(ConfigFile config)
    {
        addressConfig = config.Bind("Network", "Address", "127.0.0.1", "Host address used by F2 client connect.");
        portConfig = config.Bind("Network", "Port", 17777, "TCP port used by the standalone DMF 0.8 transport.");
        playerCountConfig = config.Bind("Gameplay", "PlayerCount", 2, "Target player count and new shared-container loot multiplier.");
        playerCountConfig.Value = Mathf.Clamp(playerCountConfig.Value, 1, 8);
        playerNameConfig = config.Bind("Gameplay", "PlayerName", string.Empty, "访客身份（主机用它跨热加入保存你的物品）。留空自动生成唯一随机名。");
        if (string.IsNullOrWhiteSpace(playerNameConfig.Value)) { playerNameConfig.Value = "Guest" + Guid.NewGuid().ToString("N").Substring(0, 4); playerNameConfig.ConfigFile.Save(); }
        starterKitTier2DayConfig = config.Bind("Gameplay", "GuestStarterKitTier2Day", 3, "从第几天起发放第二档访客初始装备。");
        starterKitTier3DayConfig = config.Bind("Gameplay", "GuestStarterKitTier3Day", 7, "从第几天起发放第三档访客初始装备。");
        starterKitTier1Config = config.Bind("Gameplay", "GuestStarterKitTier1", "", "新访客首次加入的初始装备（分号分隔的 物品类型:数量；留空不发装备）。");
        starterKitTier2Config = config.Bind("Gameplay", "GuestStarterKitTier2", "", "第二档访客初始装备（仅首次加入，按天数选档）。");
        starterKitTier3Config = config.Bind("Gameplay", "GuestStarterKitTier3", "", "第三档访客初始装备（仅首次加入，按天数选档）。");
        autoSelfTestConfig = config.Bind("Gameplay", "SelfTestAuto", false, "启动后自动执行回环自测：自动开主机 → 自动读档 → 主机 READY 后自动连接 127.0.0.1 回环客户端并跑完整协议链（本地验证用，正常联机请保持 false）。");
        Players.AttachGuestProfiles(new DarkwoodGuestProfiles(log, starterKitTier2DayConfig.Value, starterKitTier3DayConfig.Value, starterKitTier1Config.Value, starterKitTier2Config.Value, starterKitTier3Config.Value));
        lootScaleLedgerPath = Path.Combine(Paths.ConfigPath, "DarkwoodMultiplayerFramework.loot-scale-ledger.txt");
        LoadLootScaleLedger();
        telepathyPath = Path.Combine(Paths.PluginPath, "Telepathy.dll");
        log?.LogInfo($"联机传输已配置：{telepathyPath}，TCP 端口 {portConfig.Value}。");
    }
}
