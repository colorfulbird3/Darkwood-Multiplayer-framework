using System;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Actions;

public enum NetworkActionKind { Pickup, Drop, TransferItem, Attack, Open, Craft, Use, Interact }
public readonly struct NetworkActionRequest
{
    public NetworkActionRequest(Guid requestId, int playerId, NetworkActionKind kind, EntityId target, StateVersion expectedVersion, byte[] payload)
    { RequestId = requestId; PlayerId = playerId; Kind = kind; Target = target; ExpectedVersion = expectedVersion; Payload = payload ?? Array.Empty<byte>(); }
    public Guid RequestId { get; } public int PlayerId { get; } public NetworkActionKind Kind { get; }
    public EntityId Target { get; } public StateVersion ExpectedVersion { get; } public byte[] Payload { get; }
}
public readonly struct NetworkActionResult
{
    public NetworkActionResult(Guid requestId, bool accepted, StateVersion version, string errorCode)
    { RequestId = requestId; Accepted = accepted; Version = version; ErrorCode = errorCode; }
    public Guid RequestId { get; } public bool Accepted { get; } public StateVersion Version { get; } public string ErrorCode { get; }
}
public interface IActionHandler
{
    bool CanHandle(NetworkActionKind kind);
    NetworkActionResult ValidateAndApply(NetworkActionRequest request);
}
