using DarkwoodMultiplayerFramework.Network;

namespace DarkwoodMultiplayerFramework.Bootstrap;

public sealed class FrameworkRuntime
{
    public FrameworkRuntime(ITransport transport) { Transport = transport; Lifecycle = new ConnectionLifecycle(); }
    public ITransport Transport { get; }
    public ConnectionLifecycle Lifecycle { get; }
    public bool CanSendLiveState => Transport.IsConnected && Lifecycle.CanReplicate;
}
