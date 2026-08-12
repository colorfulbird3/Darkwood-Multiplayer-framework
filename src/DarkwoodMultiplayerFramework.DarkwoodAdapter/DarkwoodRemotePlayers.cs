using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed class DarkwoodRemotePlayers
{
    private sealed class Avatar
    {
        public GameObject Root=null!; public Vector3 Target; public Quaternion Rotation; public tk2dSpriteAnimator? Torso; public tk2dSpriteAnimator? Legs; public uint Sequence; public float LastSeen;
    }
    private readonly Dictionary<int,Avatar> avatars=new Dictionary<int,Avatar>();
    public void Apply(PlayerPoseMessage pose,int localId)
    {
        if(pose.PlayerId==localId||pose.Scene!=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)return;var player=Player.Instance;if(player==null)return;if(!avatars.TryGetValue(pose.PlayerId,out var avatar)){avatar=Create(pose.PlayerId,player);avatars[pose.PlayerId]=avatar;}if(pose.Sequence<=avatar.Sequence)return;avatar.Sequence=pose.Sequence;avatar.Target=new Vector3(pose.X,pose.Y,pose.Z);avatar.Rotation=new Quaternion(pose.Qx,pose.Qy,pose.Qz,pose.Qw);avatar.LastSeen=Time.unscaledTime;avatar.Root.SetActive(true);ApplyClip(avatar.Torso,pose.TorsoClip,pose.TorsoFrame);ApplyClip(avatar.Legs,pose.LegsClip,pose.LegsFrame);
    }
    public void Tick(){var remove=new List<int>();foreach(var pair in avatars){var a=pair.Value;var t=1f-Mathf.Exp(-14f*Time.unscaledDeltaTime);a.Root.transform.position=Vector3.Lerp(a.Root.transform.position,a.Target,t);a.Root.transform.rotation=Quaternion.Slerp(a.Root.transform.rotation,a.Rotation,t);if(Time.unscaledTime-a.LastSeen>10f)remove.Add(pair.Key);}foreach(var id in remove){UnityEngine.Object.Destroy(avatars[id].Root);avatars.Remove(id);}}
    public void Clear(){foreach(var a in avatars.Values)if(a.Root!=null)UnityEngine.Object.Destroy(a.Root);avatars.Clear();}
    public void Remove(int playerId){if(!avatars.TryGetValue(playerId,out var avatar))return;if(avatar.Root!=null)UnityEngine.Object.Destroy(avatar.Root);avatars.Remove(playerId);}
    private static Avatar Create(int id,Player player)
    {
        var root=new GameObject("RemotePlayer_"+id);UnityEngine.Object.DontDestroyOnLoad(root);root.transform.position=player.transform.position;root.transform.rotation=player.transform.rotation;var avatar=new Avatar{Root=root,Target=root.transform.position,Rotation=root.transform.rotation,LastSeen=Time.unscaledTime};if(player.torsoAnimator!=null){var torso=UnityEngine.Object.Instantiate(player.torsoAnimator.gameObject,root.transform);torso.name="Torso";Sanitize(torso);avatar.Torso=torso.GetComponent<tk2dSpriteAnimator>();}if(player.legs!=null){var legs=UnityEngine.Object.Instantiate(player.legs,root.transform);legs.name="Legs";Sanitize(legs);avatar.Legs=legs.GetComponent<tk2dSpriteAnimator>();}return avatar;
    }
    private static void Sanitize(GameObject go){foreach(var c in go.GetComponentsInChildren<Collider>(true))c.enabled=false;foreach(var r in go.GetComponentsInChildren<Rigidbody>(true)){r.isKinematic=true;r.detectCollisions=false;}foreach(var a in go.GetComponentsInChildren<AudioSource>(true))a.enabled=false;foreach(var l in go.GetComponentsInChildren<Light>(true))l.enabled=false;foreach(var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))behaviour.enabled=behaviour.GetType().Name.StartsWith("tk2dSprite",StringComparison.Ordinal);}
    private static void ApplyClip(tk2dSpriteAnimator? animator,string clip,int frame){if(animator==null||string.IsNullOrEmpty(clip))return;try{if(animator.CurrentClip==null||animator.CurrentClip.name!=clip)animator.PlayFromFrame(clip,Math.Max(0,frame));}catch{}}
}
