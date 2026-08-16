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
    public Action<string>? Logger {get;set;}
    public void Apply(PlayerPoseMessage pose,int localId)
    {
        if(pose.PlayerId==localId||pose.Scene!=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)return;
        var player=Player.Instance;
        if(player==null)return;
        if(!avatars.TryGetValue(pose.PlayerId,out var avatar))
        {
            // 0.8.8-beta.4：创建失败时打日志且不登记，下一帧重试；避免异常路径下的重复创建。
            try { avatar = Create(pose.PlayerId, player); avatars[pose.PlayerId] = avatar; }
            catch (Exception error) { Logger?.Invoke($"远端模型创建失败：玩家 {pose.PlayerId}：{error.Message}"); return; }
        }
        if(pose.Sequence<=avatar.Sequence)return;avatar.Sequence=pose.Sequence;avatar.Target=new Vector3(pose.X,pose.Y,pose.Z);avatar.Rotation=new Quaternion(pose.Qx,pose.Qy,pose.Qz,pose.Qw);avatar.LastSeen=Time.unscaledTime;avatar.Root.SetActive(true);if((pose.Flags&PlayerPoseFlags.Downed)==0){ApplyClip(avatar.Torso,pose.TorsoClip,pose.TorsoFrame);ApplyClip(avatar.Legs,pose.LegsClip,(pose.Flags&3)==0?0:pose.LegsFrame);}
    }
    public bool TryGetPosition(int playerId,out Vector3 position)
    {
        if(avatars.TryGetValue(playerId,out var avatar)){position=avatar.Root.transform.position;return true;}
        position=Vector3.zero;return false;
    }
    public void Tick(){var remove=new List<int>();foreach(var pair in avatars){var a=pair.Value;var t=1f-Mathf.Exp(-14f*Time.unscaledDeltaTime);a.Root.transform.position=Vector3.Lerp(a.Root.transform.position,a.Target,t);a.Root.transform.rotation=Quaternion.Slerp(a.Root.transform.rotation,a.Rotation,t);if(Time.unscaledTime-a.LastSeen>10f)remove.Add(pair.Key);}foreach(var id in remove){UnityEngine.Object.Destroy(avatars[id].Root);avatars.Remove(id);}}
    public void Clear(){foreach(var a in avatars.Values)if(a.Root!=null)UnityEngine.Object.Destroy(a.Root);avatars.Clear();}
    public void Remove(int playerId){if(!avatars.TryGetValue(playerId,out var avatar))return;if(avatar.Root!=null)UnityEngine.Object.Destroy(avatar.Root);avatars.Remove(playerId);}
    private Avatar Create(int id,Player player)
    {
        var root=new GameObject("RemotePlayer_"+id);UnityEngine.Object.DontDestroyOnLoad(root);root.transform.position=player.transform.position;root.transform.rotation=player.transform.rotation;root.layer=player.torsoAnimator!=null?player.torsoAnimator.gameObject.layer:player.gameObject.layer;var avatar=new Avatar{Root=root,Target=root.transform.position,Rotation=root.transform.rotation,LastSeen=Time.unscaledTime};if(player.torsoAnimator!=null){var torso=CloneVisual(player.torsoAnimator.gameObject,player.transform,root.transform,"Torso");avatar.Torso=torso.GetComponent<tk2dSpriteAnimator>()??torso.GetComponentInChildren<tk2dSpriteAnimator>(true);}if(player.legs!=null){var legs=CloneVisual(player.legs,player.transform,root.transform,"Legs");avatar.Legs=legs.GetComponent<tk2dSpriteAnimator>()??legs.GetComponentInChildren<tk2dSpriteAnimator>(true);}Logger?.Invoke($"远端模型已创建：玩家 {id}，渲染器 {root.GetComponentsInChildren<Renderer>(true).Length}，动画器 {root.GetComponentsInChildren<tk2dSpriteAnimator>(true).Length}，层级 {root.layer}。");return avatar;
    }
    private static GameObject CloneVisual(GameObject source,Transform playerRoot,Transform remoteRoot,string name){var clone=UnityEngine.Object.Instantiate(source);clone.name=name;clone.transform.SetParent(remoteRoot,false);clone.transform.localPosition=playerRoot.InverseTransformPoint(source.transform.position);clone.transform.localRotation=Quaternion.Inverse(playerRoot.rotation)*source.transform.rotation;clone.transform.localScale=DivideScale(source.transform.lossyScale,remoteRoot.lossyScale);SetLayerRecursive(clone,source.layer);Sanitize(clone);CopyRendererState(source,clone);clone.SetActive(true);return clone;}
    private static void Sanitize(GameObject go){foreach(var c in go.GetComponentsInChildren<Collider>(true))c.enabled=false;foreach(var r in go.GetComponentsInChildren<Rigidbody>(true)){r.isKinematic=true;r.detectCollisions=false;}foreach(var a in go.GetComponentsInChildren<AudioSource>(true))a.enabled=false;foreach(var l in go.GetComponentsInChildren<Light>(true))l.enabled=false;foreach(var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))behaviour.enabled=behaviour.GetType().Name.StartsWith("tk2d",StringComparison.Ordinal);foreach(var renderer in go.GetComponentsInChildren<Renderer>(true))renderer.enabled=!IsExcludedVisual(renderer.gameObject);}
    private static void CopyRendererState(GameObject source,GameObject clone){var from=source.GetComponentsInChildren<Renderer>(true);var to=clone.GetComponentsInChildren<Renderer>(true);for(var i=0;i<Math.Min(from.Length,to.Length);i++){if(IsExcludedVisual(from[i].gameObject)||IsExcludedVisual(to[i].gameObject)){to[i].enabled=false;continue;}to[i].sharedMaterials=from[i].sharedMaterials;to[i].sortingLayerID=from[i].sortingLayerID;to[i].sortingOrder=from[i].sortingOrder;var properties=new MaterialPropertyBlock();from[i].GetPropertyBlock(properties);to[i].SetPropertyBlock(properties);to[i].enabled=from[i].enabled;}}
    private static bool IsExcludedVisual(GameObject go){var value=go.name.ToLowerInvariant();return value.Contains("shadow")||value.Contains("mask")||value.Contains("dark")||value.Contains("light")||value.Contains("fog")||value.Contains("effect")||value.Contains("glow")||value.Contains("flash")||value.Contains("aim");}
    private static void ApplyClip(tk2dSpriteAnimator? animator,string clip,int frame){if(animator==null||string.IsNullOrEmpty(clip))return;try{frame=Math.Max(0,frame);if(animator.CurrentClip==null||animator.CurrentClip.name!=clip)animator.PlayFromFrame(clip,frame);animator.SetFrame(frame,false);animator.Stop();}catch{}}
    private static void SetLayerRecursive(GameObject go,int layer){go.layer=layer;foreach(Transform child in go.transform)SetLayerRecursive(child.gameObject,layer);}
    private static Vector3 DivideScale(Vector3 value,Vector3 divisor)=>new Vector3(Mathf.Abs(divisor.x)>.0001f?value.x/divisor.x:value.x,Mathf.Abs(divisor.y)>.0001f?value.y/divisor.y:value.y,Mathf.Abs(divisor.z)>.0001f?value.z/divisor.z:value.z);
}
