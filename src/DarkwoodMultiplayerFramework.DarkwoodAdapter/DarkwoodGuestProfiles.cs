using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>One starter-kit line parsed from config (item type + amount).</summary>
public sealed class GuestStarterEntry
{
    public GuestStarterEntry(string type, int amount) { Type = type ?? string.Empty; Amount = Math.Max(1, amount); }
    public string Type { get; }
    public int Amount { get; }
}

/// <summary>
/// Host-side persistence for hot-join guest identities. Profiles are keyed by
/// (host save token, guest key) and live beside the BepInEx root (NOT inside the
/// game save directory, so they never leak into transferred save bundles).
/// </summary>
public sealed class DarkwoodGuestProfiles
{
    private const string ProfilesRootName = "DarkwoodMPGuestProfiles";
    private readonly string root;
    private readonly List<GuestStarterEntry> tier1 = new List<GuestStarterEntry>();
    private readonly List<GuestStarterEntry> tier2 = new List<GuestStarterEntry>();
    private readonly List<GuestStarterEntry> tier3 = new List<GuestStarterEntry>();
    private readonly int tier2Day;
    private readonly int tier3Day;
    private readonly ManualLogSource? log;

    public DarkwoodGuestProfiles(ManualLogSource? log, int tier2Day, int tier3Day, string tier1Spec, string tier2Spec, string tier3Spec)
    {
        this.log = log;
        this.tier2Day = Math.Max(2, tier2Day);
        this.tier3Day = Math.Max(this.tier2Day + 1, tier3Day);
        root = Path.Combine(Paths.BepInExRootPath, ProfilesRootName);
        ParseKit(tier1Spec, tier1, "第一档");
        ParseKit(tier2Spec, tier2, "第二档");
        ParseKit(tier3Spec, tier3, "第三档");
    }

    public IReadOnlyList<GuestStarterEntry> KitForDay(int day)
    {
        if (day >= tier3Day) return tier3;
        if (day >= tier2Day) return tier2;
        return tier1;
    }

    /// <summary>Loads the persisted record or creates a fresh one (JoinCount=1). The record's JoinCount is returned incremented for a rejoin.</summary>
    public GuestProfileRecord Resolve(string saveToken, string guestKey, int day, Vector3 hostFallbackPosition, out Vector3 spawn)
    {
        var loaded = Load(saveToken, guestKey);
        if (loaded != null)
        {
            var record = loaded.Value;
            var rejoined = new GuestProfileRecord(record.GuestKey, day, record.JoinCount + 1, record.X, record.Y, record.Z, record.Backpack, record.Hotbar, DateTime.UtcNow.Ticks);
            spawn = DecideSpawn(rejoined, hostFallbackPosition);
            return rejoined;
        }
        var fresh = new GuestProfileRecord(guestKey, day, 1, 0f, 0f, 0f, Array.Empty<InventorySlotWire>(), Array.Empty<InventorySlotWire>(), DateTime.UtcNow.Ticks);
        spawn = DecideSpawn(fresh, hostFallbackPosition);
        return fresh;
    }

    /// <summary>Rejoins resume their last position; first joins spawn next to the host with a per-player angular offset.</summary>
    public Vector3 DecideSpawn(GuestProfileRecord record, Vector3 hostFallbackPosition)
    {
        if (record.HasPosition) return new Vector3(record.X, record.Y, record.Z);
        var angle = (record.JoinCount % 8) * (Mathf.PI / 4f);
        return hostFallbackPosition + new Vector3(Mathf.Cos(angle) * 2f, 0f, Mathf.Sin(angle) * 2f);
    }

    public void Save(string saveToken, GuestProfileRecord record)
    {
        try
        {
            var directory = Path.Combine(root, HashToken(saveToken));
            Directory.CreateDirectory(directory);
            var bytes = ReplicationProtocolCodec.Encode(record);
            var path = Path.Combine(directory, HashToken(record.GuestKey) + ".profile");
            var staging = path + ".tmp";
            File.WriteAllBytes(staging, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(staging, path);
        }
        catch (Exception error) { log?.LogWarning("保存访客档案失败：" + error.Message); }
    }

    private GuestProfileRecord? Load(string saveToken, string guestKey)
    {
        try
        {
            var path = Path.Combine(root, HashToken(saveToken), HashToken(guestKey) + ".profile");
            if (!File.Exists(path)) return null;
            return ReplicationProtocolCodec.DecodeGuestProfileRecord(File.ReadAllBytes(path));
        }
        catch (Exception error)
        {
            log?.LogWarning($"读取访客档案失败（{guestKey}），按新玩家处理：" + error.Message);
            return null;
        }
    }

    private void ParseKit(string spec, List<GuestStarterEntry> destination, string tierLabel)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        foreach (var rawEntry in spec.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.Trim();
            if (entry.Length == 0) continue;
            var separator = entry.IndexOf(':');
            if (separator < 0) separator = entry.IndexOf(',');
            string type; int amount = 1;
            if (separator > 0)
            {
                type = entry.Substring(0, separator).Trim();
                if (!int.TryParse(entry.Substring(separator + 1).Trim(), out var parsed) || parsed < 1)
                {
                    log?.LogWarning($"访客初始装备{tierLabel}条目无效，已跳过：{entry}");
                    continue;
                }
                amount = parsed;
            }
            else type = entry;
            if (type.Length == 0) continue;
            destination.Add(new GuestStarterEntry(type, amount));
        }
    }

    private static string HashToken(string value)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BitConverter.ToString(bytes, 0, 8).Replace("-", "");
    }
}
