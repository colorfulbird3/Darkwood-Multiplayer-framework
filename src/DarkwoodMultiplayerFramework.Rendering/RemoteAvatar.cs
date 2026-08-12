namespace DarkwoodMultiplayerFramework.Rendering;

public readonly struct RemotePlayerPose
{
    public RemotePlayerPose(float x, float y, float rotation, bool moving, bool running, bool aiming, bool attacking)
    { X=x; Y=y; Rotation=rotation; Moving=moving; Running=running; Aiming=aiming; Attacking=attacking; }
    public float X { get; } public float Y { get; } public float Rotation { get; }
    public bool Moving { get; } public bool Running { get; } public bool Aiming { get; } public bool Attacking { get; }
}
public interface IRemoteAvatar
{
    int PlayerId { get; }
    void Apply(RemotePlayerPose previous, RemotePlayerPose next, float interpolation);
    void SetVisible(bool visible);
}
