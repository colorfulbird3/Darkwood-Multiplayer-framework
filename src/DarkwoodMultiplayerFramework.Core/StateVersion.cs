namespace DarkwoodMultiplayerFramework.Core;

public readonly struct StateVersion : System.IComparable<StateVersion>, System.IEquatable<StateVersion>
{
    public StateVersion(ulong value) => Value = value;
    public ulong Value { get; }
    public int CompareTo(StateVersion other) => Value.CompareTo(other.Value);
    public bool Equals(StateVersion other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is StateVersion other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator >(StateVersion a, StateVersion b) => a.Value > b.Value;
    public static bool operator <(StateVersion a, StateVersion b) => a.Value < b.Value;
    public static bool operator ==(StateVersion a, StateVersion b) => a.Equals(b);
    public static bool operator !=(StateVersion a, StateVersion b) => !a.Equals(b);
}
