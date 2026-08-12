using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Entities;

public sealed class EntityRegistry<T> where T : class
{
    private readonly Dictionary<EntityId, T> entries = new Dictionary<EntityId, T>();
    private ulong nextRuntimeId = 1;
    public int Count => entries.Count;
    public EntityId AllocateRuntimeId() => new EntityId(nextRuntimeId++, false);
    public void Register(EntityId id, T entity)
    {
        if (entries.ContainsKey(id)) throw new InvalidOperationException($"Duplicate entity id {id}.");
        entries.Add(id, entity ?? throw new ArgumentNullException(nameof(entity)));
    }
    public bool Remove(EntityId id) => entries.Remove(id);
    public bool TryGet(EntityId id, out T? entity) => entries.TryGetValue(id, out entity);
    public string ComputeDigest()
    {
        var canonical = string.Join("\n", entries.Keys.OrderBy(x => x.IsPersistent ? 0 : 1).ThenBy(x => x.Value).Select(x => x.ToString()));
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", string.Empty).Substring(0, 16);
    }
}
