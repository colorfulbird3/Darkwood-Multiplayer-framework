using System;
using System.Collections.Generic;
using DarkwoodMultiplayerFramework.Core;

namespace DarkwoodMultiplayerFramework.Actions;

/// <summary>Bounded request-id cache used by a host to guarantee apply-once semantics.</summary>
public sealed class ActionIdempotencyCache
{
    private readonly int capacity;
    private readonly Dictionary<Guid,NetworkActionResult> results=new Dictionary<Guid,NetworkActionResult>();
    private readonly Queue<Guid> order=new Queue<Guid>();
    public ActionIdempotencyCache(int capacity=2048){if(capacity<1)throw new ArgumentOutOfRangeException(nameof(capacity));this.capacity=capacity;}
    public int Count=>results.Count;
    public bool TryGet(Guid requestId,out NetworkActionResult result)=>results.TryGetValue(requestId,out result);
    /// <summary>Stores the first result and returns an evicted request id, if any.</summary>
    public Guid Store(NetworkActionResult result){if(result.RequestId==Guid.Empty)throw new ArgumentException("Request id must not be empty.");if(results.ContainsKey(result.RequestId))return Guid.Empty;results[result.RequestId]=result;order.Enqueue(result.RequestId);var evicted=Guid.Empty;while(order.Count>capacity){evicted=order.Dequeue();results.Remove(evicted);}return evicted;}
    public void Clear(){results.Clear();order.Clear();}
}

public static class ActionValidation
{
    public static bool RevisionMatches(ulong currentRevision,ulong expectedRevision)=>currentRevision==expectedRevision;
    public static bool WithinDistance(float ax,float ay,float az,float bx,float by,float bz,float maxDistance)
    {if(maxDistance<=0)throw new ArgumentOutOfRangeException(nameof(maxDistance));var x=ax-bx;var y=ay-by;var z=az-bz;return x*x+y*y+z*z<=maxDistance*maxDistance;}
}
