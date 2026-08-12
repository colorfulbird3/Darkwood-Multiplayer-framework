using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodPlayerPose
{
    public Vector3 Position = Vector3.zero;
    public Quaternion Rotation = Quaternion.identity;
    public byte Flags;
    public string Scene = string.Empty;
    public string TorsoClip = string.Empty;
    public int TorsoFrame;
    public string LegsClip = string.Empty;
    public int LegsFrame;
}

public static class DarkwoodPlayerAdapter
{
    public static bool TryCapture(out DarkwoodPlayerPose pose)
    {
        pose = new DarkwoodPlayerPose(); var player = Player.Instance; if (player == null) return false;
        pose.Position = player.transform.position; pose.Rotation = player.transform.rotation;
        if (player.walking) pose.Flags |= 1; if (player.running) pose.Flags |= 2; if (player.aiming) pose.Flags |= 4; if (player.attacking) pose.Flags |= 8;
        pose.Scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        CaptureAnimator(player.torsoAnimator, out pose.TorsoClip, out pose.TorsoFrame);
        CaptureAnimator(player.legs != null ? player.legs.GetComponent<tk2dSpriteAnimator>() : null, out pose.LegsClip, out pose.LegsFrame);
        return true;
    }
    private static void CaptureAnimator(tk2dSpriteAnimator? animator, out string clip, out int frame) { clip = animator != null && animator.CurrentClip != null ? animator.CurrentClip.name : string.Empty; frame = animator != null && animator.CurrentClip != null ? animator.CurrentFrame : 0; }
}
