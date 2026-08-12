namespace DarkwoodMultiplayerFramework.Core;

public readonly struct EntityId : System.IEquatable<EntityId>
{
    public EntityId(ulong value, bool persistent) { Value = value; IsPersistent = persistent; }
    public ulong Value { get; }
    public bool IsPersistent { get; }
    public bool Equals(EntityId other) => Value == other.Value && IsPersistent == other.IsPersistent;
    public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
    public override int GetHashCode() => unchecked((Value.GetHashCode() * 397) ^ IsPersistent.GetHashCode());
    public override string ToString() => IsPersistent ? $"world:{Value}" : $"runtime:{Value}";
}
