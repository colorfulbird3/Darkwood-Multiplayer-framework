using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Inventory;

public sealed class AuthoritativeContainer
{
    private readonly Dictionary<string, int> items = new Dictionary<string, int>(StringComparer.Ordinal);
    public StateVersion Version { get; private set; }
    public IReadOnlyDictionary<string, int> Items => items;
    public bool TryTransfer(string itemId, int delta, StateVersion expected, out string error)
    {
        if (expected != Version) { error = "STALE_VERSION"; return false; }
        items.TryGetValue(itemId, out var current);
        var next = current + delta;
        if (next < 0) { error = "INSUFFICIENT_ITEMS"; return false; }
        if (next == 0) items.Remove(itemId); else items[itemId] = next;
        Version = new StateVersion(Version.Value + 1); error = string.Empty; return true;
    }
}
