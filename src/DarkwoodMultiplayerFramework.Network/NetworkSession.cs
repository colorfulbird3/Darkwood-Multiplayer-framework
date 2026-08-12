using System;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.Network;

public sealed class NetworkSession
{
    public NetworkSession(ProtocolIdentity identity) { Identity=identity; Lifecycle=new ConnectionLifecycle(); SessionId=Guid.NewGuid(); }
    public Guid SessionId { get; }
    public ProtocolIdentity Identity { get; }
    public ConnectionLifecycle Lifecycle { get; }
    public string LocalRegistryDigest { get; private set; } = string.Empty;
    public string RemoteRegistryDigest { get; private set; } = string.Empty;
    public bool RegistryMatches => LocalRegistryDigest.Length > 0 && LocalRegistryDigest == RemoteRegistryDigest;
    public HandshakeResult Accept(ProtocolIdentity remote) => HandshakeValidator.Validate(Identity, remote);
    public void SetRegistryDigests(string local, string remote) { LocalRegistryDigest=local ?? string.Empty; RemoteRegistryDigest=remote ?? string.Empty; }
    public void RequireMatchingRegistry()
    {
        if (!RegistryMatches) throw new InvalidOperationException("REGISTRY_DIGEST_MISMATCH");
    }
}
