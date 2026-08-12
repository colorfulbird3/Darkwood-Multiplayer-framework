using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;

namespace DarkwoodMultiplayerFramework.Bootstrap;

public sealed class FrameworkRuntime
{
    public FrameworkRuntime(ITransport transport, ProtocolIdentity identity) { Transport = transport; Session = new NetworkSession(identity); }
    public ITransport Transport { get; }
    public NetworkSession Session { get; }
    public ConnectionLifecycle Lifecycle => Session.Lifecycle;
    public bool CanSendLiveState => Transport.IsConnected && Lifecycle.CanReplicate && Session.RegistryMatches;
}
