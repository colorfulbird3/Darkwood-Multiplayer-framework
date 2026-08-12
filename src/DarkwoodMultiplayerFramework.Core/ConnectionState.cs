namespace DarkwoodMultiplayerFramework.Core;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    VersionChecking,
    SaveTransfer,
    LoadingSave,
    BuildingRegistry,
    ApplyingSnapshot,
    Ready,
    Stopping,
    Failed
}
