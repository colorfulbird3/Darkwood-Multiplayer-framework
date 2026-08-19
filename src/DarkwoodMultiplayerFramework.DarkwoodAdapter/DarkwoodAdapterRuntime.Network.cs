using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DarkwoodMultiplayerFramework.Actions;
using DarkwoodMultiplayerFramework.Core;
using DarkwoodMultiplayerFramework.Entities;
using DarkwoodMultiplayerFramework.Network;
using DarkwoodMultiplayerFramework.Protocol;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

public sealed partial class DarkwoodAdapterRuntime
{
    private void PollHotkeys()
    {
        var f1 = Input.GetKey(KeyCode.F1); var f2 = Input.GetKey(KeyCode.F2); var f3 = Input.GetKey(KeyCode.F3);
        try
        {
            if (f1 && !f1WasDown) StartHost();
            if (f2 && !f2WasDown) ConnectClient();
            if (f3 && !f3WasDown) { StopNetwork(); log?.LogInfo("联机会话已停止。"); }
        }
        catch (Exception error) { log?.LogError($"Standalone network command failed: {error}"); }
        finally { f1WasDown = f1; f2WasDown = f2; f3WasDown = f3; }
    }

    private void OnHandshakeSucceeded()
    {
        Session.LocalPeerId = clientSession?.PeerId ?? -1;
        Session.SessionId = clientSession?.HostSessionId ?? Guid.Empty;
        Session.IsMultiplayerActive = true;
        log?.LogInfo($"握手成功。玩家 ID：{clientSession?.PeerId}，主机会话：{clientSession?.HostSessionId}。");
        clientSession?.Send(ProtocolMessageType.SaveTransferRequest,ReplicationProtocolCodec.Encode(new SaveTransferRequest(Guid.NewGuid())));
    }
    private void OnHandshakeFailed(string error) => log?.LogError($"握手失败：{error}");
    private void OnPeerAccepted(int connectionId)
    {
        var key = "player";
        if (hostSession != null && hostSession.TryGetPeerGuestKey(connectionId, out var guestKey) && !string.IsNullOrWhiteSpace(guestKey)) key = DarkwoodPlayerService.NormalizeGuestKey(guestKey);
        Players.SetGuestKey(connectionId, key);
        log?.LogInfo($"已接受玩家连接：{connectionId}（身份 {key}）。");
    }
    private void OnPeerRejected(int connectionId, string error) => log?.LogWarning($"已拒绝玩家连接 {connectionId}：{error}");
    private void OnPeerDisconnected(int connectionId)
    {
        Players.PersistGuestProfile(connectionId);
        Combat.OnPeerDisconnected(connectionId); // 血量/倒地/攻击/营救清理归战斗服务
        outgoing.Remove(connectionId);readyPeers.Remove(connectionId);SaveState.OnPeerDisconnected(connectionId);Players.OnPeerDisconnected(connectionId); // 远端状态清理归玩家服务
    }

    private void OnHostMessage(int peer,ProtocolEnvelope envelope)
    {
        try
        {
            if(!DispatchToRouter(new PeerContext(peer,false),envelope))
                log?.LogWarning($"主机收到未注册的消息类型 {envelope.MessageType}（玩家 {peer}），已忽略。");
        }
        catch(Exception error){log?.LogError($"Host protocol handler failed for peer {peer}: {error}");Queue(peer,ProtocolMessageType.Error,ReplicationProtocolCodec.Encode(new ProtocolErrorMessage("HOST_HANDLER_FAILED",error.Message)));}
    }

    private void OnClientMessage(ProtocolEnvelope envelope)
    {
        try
        {
            if(!DispatchToRouter(new PeerContext(0,true),envelope))
                log?.LogWarning($"客户端收到未注册的消息类型 {envelope.MessageType}，已忽略。");
        }
        catch(Exception error){FailClient("CLIENT_PROTOCOL_FAILED",error);}
    }

    private void PrepareSave(int peer)
    {
        var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager is unavailable.");
        var profile=global::Core.currentProfile;
        if(profile==null||!profile.Active)
        {
            // 自测：quickLoadGame 等路径不会激活档案——从档案列表恢复（主机侧通用健壮性）。
            var state=manager.loadGameProfiles();
            if(state?.profiles!=null){global::Core.profiles=state.profiles;profile=state.profiles.FirstOrDefault(p=>p!=null&&p.Active);if(profile!=null){global::Core.currentProfile=profile;manager.updateFilePaths();log?.LogWarning("主机档案未激活，已从档案列表恢复当前档案。");}}
        }
        if(profile==null||!profile.Active)throw new InvalidOperationException("Host has no active save profile.");
        manager.Save(false,true,true,false,false,true,false);manager.saveProfilesFile();var bundle=DarkwoodSaveBundle.BuildForClient(manager.baseSaveDirectory,profile.id);var id=Guid.NewGuid();SaveState.SetSentSave(peer,id);var chunks=ChunkTransferAssembler.Split(bundle,128*1024);Queue(peer,ProtocolMessageType.SaveTransferManifest,ReplicationProtocolCodec.Encode(new SaveTransferManifest(id,profile.id,bundle.LongLength,chunks.Length,ChunkTransferAssembler.Hash(bundle),$"Day {profile.day}, chapter {profile.chapter}")));for(var i=0;i<chunks.Length;i++)Queue(peer,ProtocolMessageType.SaveTransferChunk,ReplicationProtocolCodec.Encode(new SaveTransferChunk(id,i,chunks.Length,chunks[i],ChunkTransferAssembler.Hash(chunks[i]))),"存档",i,chunks.Length);log?.LogInfo($"已为玩家 {peer} 准备实时存档：传输 {id}，{bundle.Length} 字节，{chunks.Length} 个数据块。");
    }

    private void ReceiveSaveChunk(SaveTransferChunk chunk)
    {
        if(!SaveState.AcceptSaveChunk(chunk))return;
        var data=SaveState.FinishSaveReceive();
        var manifest=SaveState.PendingSaveManifest;
        InstallDownloadedSave(data,manifest.ProfileId);
        clientSession?.Send(ProtocolMessageType.SaveTransferApplied,ReplicationProtocolCodec.Encode(new SaveTransferApplied(manifest.TransferId,manifest.ProfileId,"isolated-client-save")));
        StartCoroutine(LoadDownloadedSave(manifest.ProfileId));
    }

    private void InstallDownloadedSave(byte[] data,int profile)
    {
        var key=HostKey();var root=Path.Combine(Paths.BepInExRootPath,"DarkwoodMPClientSaves",key);var target=Path.Combine(root,"1_4Save");var staging=Path.Combine(root,".incoming-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);try{var extracted=DarkwoodSaveBundle.Extract(data,staging);if(extracted!=profile)throw new InvalidDataException("下载的存档档案 ID 不一致。");Directory.CreateDirectory(root);if(Directory.Exists(target))Directory.Move(target,Path.Combine(root,"previous-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")));Directory.Move(staging,target);}catch{try{if(Directory.Exists(staging))Directory.Delete(staging,true);}catch{}throw;}ActiveClientSaveDirectory=target;log?.LogInfo("下载的存档已安装到独立目录："+target);
    }

    private IEnumerator LoadDownloadedSave(int profileId)
    {
        yield return null;try{if(clientSession==null||!clientSession.HandshakeComplete||clientSession.Session.Lifecycle.State==ConnectionState.Failed)throw new InvalidOperationException("客户端连接已断开，取消存档加载。");var manager=Singleton<SaveManager>.Instance;if(manager==null)throw new InvalidOperationException("SaveManager 不可用。");var state=manager.loadGameProfiles();if(state?.profiles==null)throw new InvalidDataException("下载的存档档案信息不可用。");var profile=state.profiles.FirstOrDefault(p=>p!=null&&p.id==profileId&&p.Active);if(profile==null)throw new InvalidDataException("下载的存档档案信息不可用。");global::Core.profiles=state.profiles;global::Core.currentProfile=profile;manager.updateFilePaths();if(clientSession.Session.Lifecycle.State==ConnectionState.SaveTransfer)clientSession.Session.Lifecycle.MoveTo(ConnectionState.LoadingSave);
        // FIX-006：不在此处挂 onFinishedLoading——SaveManager 是场景内单例（非 DontDestroyOnLoad），
        // 此处挂到的是主菜单场景的实例，LoadScene 后随场景销毁；Load() 跑在 chapter1 场景的
        // 新实例上，回调永远不触发（实测：加载卡 92%、timeScale 无人恢复、界面永不隐藏）。
        // 改由 DarkwoodLoadFinishedPatch 在 SaveManager.Load 入口挂到 __instance。
        SaveState.SetProgress("正在加载存档");SaveState.MarkLoadStarted(Time.unscaledTime);
        // FIX-002：initLoadGame() 内部先跑 initNewGame()，会把 Core.loadingGame 重置为 false，
        // WorldGenerator.Start 因此走“生成新世界”分支（教学梦境，约 8 个实体），且
        // SaveManager.onFinishedLoading 不触发（客户端永远卡在加载界面）。
        // 正确路径：保持 loadingGame=true 直接加载章节场景，WorldGenerator.Start
        // 会走 SaveManager.Load() 恢复主机存档世界，完成后回调 onFinishedLoading。
        global::Core.loadingGame=true;global::Core.loadedGame=true;global::Core.forbidInputs=true;var controller=Singleton<Controller>.Instance;if(controller!=null)controller.buttonsDisabled=true;SaveState.ClientSaveLoadPending=true;LogMessage($"正在切换到章节场景 {(profile.chapter>=2?"chapter2":"chapter1")} 并启动存档恢复（约 2 秒后 WorldGenerator.Start 调度 SaveManager.Load）。");UnityEngine.SceneManagement.SceneManager.LoadScene(profile.chapter>=2?"chapter2":"chapter1");global::Core.mainMenu=false;}catch(Exception error){FailClient("SAVE_LOAD_FAILED",error);}
    }

    private void OnDownloadedSaveFinished()
    {
        SaveState.ClientSaveLoadPending=false;LogMessage($"存档加载完成回调已触发（用时 {(SaveState.LoadStartedAt>0f?Time.unscaledTime-SaveState.LoadStartedAt:0f):F1} 秒）。");
        if(Time.timeScale<=0.01f){Time.timeScale=1f;LogMessage("已强制恢复 timeScale=1（加载期间曾被冻结）。");}
        var manager=Singleton<SaveManager>.Instance;if(manager!=null)manager.onFinishedLoading=(saveDelegate)Delegate.Remove(manager.onFinishedLoading,new saveDelegate(OnDownloadedSaveFinished));if(clientSession==null||!clientSession.HandshakeComplete||clientSession.Session.Lifecycle.State==ConnectionState.Failed){log?.LogWarning("客户端连接已断开，忽略已完成的存档加载回调。");return;}if(clientSession.Session.Lifecycle.State==ConnectionState.LoadingSave)clientSession.Session.Lifecycle.MoveTo(ConnectionState.BuildingRegistry);registryDirty=true;StartCoroutine(WaitForRegistryThenReady());
    }

    /// <summary>FIX-006：把完成回调幂等挂到“真正执行 Load 的 SaveManager 实例”上。
    /// SaveManager 是场景内单例（无 DontDestroyOnLoad），主菜单场景的实例会在
    /// LoadScene 后销毁；只有当前场景实例上挂的回调才会被 Load() 触发。</summary>
    internal static void AttachLoadFinishedCallback(SaveManager manager)
    {
        var runtime = Instance;
        if (runtime == null) return;
        manager.onFinishedLoading = (saveDelegate)Delegate.Remove(manager.onFinishedLoading, new saveDelegate(runtime.OnDownloadedSaveFinished));
        manager.onFinishedLoading = (saveDelegate)Delegate.Combine(manager.onFinishedLoading, new saveDelegate(runtime.OnDownloadedSaveFinished));
    }

    private IEnumerator WaitForRegistryThenReady()
    {
        var deadline=Time.realtimeSinceStartup+90f;
        // 世界在存档加载后仍可能流式生成：等待本地候选集合稳定（连续 3 次一致），
        // 之后收到的 BindingManifest 才有完整的本地对象可绑定。
        var previousFingerprint = string.Empty;
        var stableChecks = 0;
        var candidates = Array.Empty<LocalEntityCandidate>();
        while(Time.realtimeSinceStartup<deadline)
        {
            if(Player.Instance==null){yield return null;continue;}
            candidates=scanner.BuildLocalCandidates(out var comps);
            var fingerprint = ScanFingerprint(comps); // 真实对象集合指纹（type+InstanceID），非 count
            if(fingerprint==previousFingerprint)
            {
                stableChecks++;
                if(stableChecks>=3)break;
            }
            else
            {
                stableChecks=0;
                previousFingerprint=fingerprint;
            }
            yield return new WaitForSecondsRealtime(1f);
        }
        try
        {
            if(Player.Instance==null)throw new InvalidOperationException("本地场景在 90 秒内未就绪。");
            registryDirty=false; // 客户端场景稳定：清 dirty 否则握手永远被拦（客户端不再重建注册表）
            clientCandidatesReady=true;
            clientCandidatesCount=candidates.Length;
            clientCandidateDigest=CandidateDigest(candidates);
            if(stableChecks<3)log?.LogWarning($"本地候选在超时前未能稳定（最后 {candidates.Length} 个），继续尝试就绪。");
            log?.LogInfo($"客户端本地候选已稳定：{candidates.Length} 个实体，摘要 {clientCandidateDigest}。");
            SaveState.MarkRegistryStabilized();
            TrySendClientRegistryReady();
        }
        catch(Exception error){FailClient("REGISTRY_BUILD_FAILED",error);}
    }

    /// <summary>客户端候选摘要（诊断用，不再作为就绪门槛与主机比对）。</summary>
    private static string CandidateDigest(LocalEntityCandidate[] candidates)
    {
        var items=new List<string>(candidates.Length);
        foreach(var c in candidates)items.Add(c.ComponentType+":"+c.SaveableUid);
        items.Sort(StringComparer.Ordinal);
        ulong hash=14695981039346656037UL;
        foreach(var s in items)foreach(var b in System.Text.Encoding.UTF8.GetBytes(s)){hash^=b;hash*=1099511628211UL;}
        return hash.ToString("X16").Substring(0,16);
    }

    private void PrepareSnapshot(int peer,ReadyMessage ready)
    {
        var stableGate = EntityBindingGate.SnapshotReady(hostRegistryStable);
        if (stableGate != null)
        {
            SaveState.SetPendingSnapshotRequest(peer, ready);
            SaveState.SetProgress("正在等待主机世界稳定");
            log?.LogInfo($"Peer {peer} ready 到达但主机注册表未稳定（{stableGate}），快照请求挂起。");
            return;
        }
        if(!string.Equals(ready.Scene,CurrentScene,StringComparison.Ordinal)){Queue(peer,ProtocolMessageType.Error,ReplicationProtocolCodec.Encode(new ProtocolErrorMessage("SCENE_MISMATCH",$"host={CurrentScene};client={ready.Scene}")));return;}
        EnsureHostExistingLootScaled();
        if(!hostLootScaleScanComplete)
        {
            SaveState.SetPendingSnapshotRequest(peer,ready);
            SaveState.SetProgress("正在按联机人数准备共享容器");
            log?.LogInfo($"Peer {peer} handshake is valid; delaying world snapshot until shared-container preparation completes.");
            return;
        }
        if(!string.Equals(ready.RegistryDigest,RegistryDigest,StringComparison.Ordinal))log?.LogWarning($"[SYNC] Peer {peer} local candidate digest differs (host={RegistryDigest}, client={ready.RegistryDigest}); sending authoritative binding manifest + snapshot.");
        if(SaveState.TryGetSentSnapshot(peer,out _))return;
        // 1) Entity Binding Manifest（权威描述符，分块）——客户端必须先完成绑定才能应用快照
        if (authoritativeDescriptors.Length == 0) { log?.LogError($"无法为玩家 {peer} 发送绑定清单：主机权威描述符为空（注册表未构建）。"); return; }
        var transferId = bindingTransferId;
        var chunks = ChunkTransferAssembler.Split(bindingManifestBytes, 64 * 1024);
        Queue(peer, ProtocolMessageType.EntityBindingManifest, ReplicationProtocolCodec.Encode(new EntityBindingManifest(transferId, bindingManifestBytes.LongLength, chunks.Length, ChunkTransferAssembler.Hash(bindingManifestBytes), CurrentScene, registryGeneration, authoritativeDescriptors.Length)));
        for (var i = 0; i < chunks.Length; i++) Queue(peer, ProtocolMessageType.EntityBindingChunk, ReplicationProtocolCodec.Encode(new EntityBindingChunk(transferId, i, chunks.Length, chunks[i], ChunkTransferAssembler.Hash(chunks[i]))), "实体绑定清单", i, chunks.Length);
        log?.LogInfo($"已为玩家 {peer} 发送实体绑定清单（第 {registryGeneration} 代）：{authoritativeDescriptors.Length} 个描述符，{bindingManifestBytes.Length} 字节，{chunks.Length} 块。");
        // 2) 世界快照（绑定完成后客户端才会应用）
        var entities=replication.Snapshot();var inventories=replication.CaptureInventorySnapshot();var state=DarkwoodWorldSnapshotCodec.Encode(CurrentScene,RegistryDigest,serverTick,entities,inventories);var id=Guid.NewGuid();SaveState.SetSentSnapshot(peer,id);var snapshotChunks=ChunkTransferAssembler.Split(state,64*1024);Queue(peer,ProtocolMessageType.WorldSnapshotManifest,ReplicationProtocolCodec.Encode(new WorldSnapshotManifest(id,state.LongLength,snapshotChunks.Length,ChunkTransferAssembler.Hash(state),CurrentScene,RegistryDigest,serverTick)));for(var i=0;i<snapshotChunks.Length;i++)Queue(peer,ProtocolMessageType.WorldSnapshotChunk,ReplicationProtocolCodec.Encode(new WorldSnapshotChunk(id,i,snapshotChunks.Length,snapshotChunks[i],ChunkTransferAssembler.Hash(snapshotChunks[i]))),"世界快照",i,snapshotChunks.Length);log?.LogInfo($"已为玩家 {peer} 准备世界快照 {id}：{entities.Length} 个实体，{inventories.Length} 个库存，{state.Length} 字节，注册表 {RegistryDigest}。");
    }

    private void EnsureHostExistingLootScaled()
    {
        if(hostSession==null||hostLootScaleScanStarted||hostLootScaleScanComplete||ConfiguredPlayerCount<=1||registry==null)return;
        hostLootScaleScanStarted=true;
        hostLootScaleCoroutine=StartCoroutine(ScaleExistingLootCoroutine());
    }

    private IEnumerator ScaleExistingLootCoroutine()
    {
        var inventories=new List<Inventory>();
        foreach(var component in scanner.ScanScene())
        {
            if(component is Inventory inventory && (inventory.invType==Inventory.InvType.itemInv||inventory.invType==Inventory.InvType.deathDrop)) inventories.Add(inventory);
            if((inventories.Count%32)==0)yield return null;
        }
        var hostToken=HostSaveToken();var scenePrefix=hostToken+"|"+CurrentScene+"|";var legacyLedger=scaledHostInventoryKeys.Any(value=>value.StartsWith(scenePrefix,StringComparison.Ordinal));var scaled=0;var migrated=0;var processed=0;
        foreach(var inventory in inventories)
        {
            var id=scanner.ToPersistentId(inventory);var key=hostToken+"|"+CurrentScene+"|"+id.Value.ToString("X16");
            if(scaledHostInventoryKeys.Add(key))
            {
                // alpha.8 used the pre-indexed EntityId. If its ledger already
                // contains this save/scene, keep the existing scaled quantities
                // and migrate the entries instead of multiplying them again.
                if(legacyLedger){migrated++;}
                else{DarkwoodLootScalingPatch.ScaleExistingInventory(inventory,ConfiguredPlayerCount);scaled++;}
            }
            processed++;
            if((processed%8)==0){SaveState.SetProgress($"正在准备共享容器：{processed}/{inventories.Count}");yield return null;}
        }
        hostLootScaleScanComplete=true;hostLootScaleScanStarted=false;hostLootScaleCoroutine=null;
        if(scaled>0)SaveLootScaleLedger();
        SaveState.SetProgress(string.Empty);
        log?.LogInfo($"已按 {ConfiguredPlayerCount} 人完成共享柜子准备：扫描 {inventories.Count} 个，扩容 {scaled} 个，迁移旧账本 {migrated} 个。");
        foreach(var request in SaveState.DrainPendingSnapshotRequests())
        {
            if(hostSession!=null)PrepareSnapshot(request.Key,request.Value);
        }
    }

    internal string HostSaveToken()
    {
        // FIX-008：不再用 savs.dat 的文件创建时间——游戏保存会重写该文件，创建时间随之变化，
        // 扩容账本因此全部失效，导致每次开服对共享柜子重复翻倍物品，最终溢出损坏主机存档。
        // 改用稳定身份：存档目录名 + profile id。新档的 uniqueId 会全新分配，账本不会误命中。
        try
        {
            var manager=Singleton<SaveManager>.Instance;
            var dir=manager!=null&&!string.IsNullOrEmpty(manager.staticFile)?Path.GetDirectoryName(manager.staticFile):string.Empty;
            if(!string.IsNullOrEmpty(dir))return Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))+"|"+(global::Core.currentProfile?.id??-1);
        }
        catch{}
        return "profile:"+(global::Core.currentProfile?.id??-1);
    }

    private void LoadLootScaleLedger()
    {
        scaledHostInventoryKeys.Clear();
        try{if(File.Exists(lootScaleLedgerPath))foreach(var line in File.ReadAllLines(lootScaleLedgerPath))if(!string.IsNullOrWhiteSpace(line))scaledHostInventoryKeys.Add(line.Trim());}
        catch(Exception error){log?.LogWarning("读取柜子扩容账本失败："+error.Message);}
    }

    private void SaveLootScaleLedger()
    {
        try{Directory.CreateDirectory(Path.GetDirectoryName(lootScaleLedgerPath));File.WriteAllLines(lootScaleLedgerPath,scaledHostInventoryKeys.OrderBy(value=>value).ToArray());}
        catch(Exception error){log?.LogWarning("保存柜子扩容账本失败："+error.Message);}
    }

    private void ReceiveSnapshotChunk(WorldSnapshotChunk chunk)
    {
        if(!SaveState.AcceptSnapshotChunk(chunk))return;
        var bytes=SaveState.FinishSnapshotReceive();
        var manifest=SaveState.PendingSnapshotManifest;
        var snapshot=DarkwoodWorldSnapshotCodec.Decode(bytes);if(snapshot.Scene!=CurrentScene||snapshot.Scene!=manifest.Scene)throw new InvalidDataException("世界快照场景不一致。");if(snapshot.RegistryDigest!=manifest.RegistryDigest)throw new InvalidDataException($"快照摘要不一致：payload={snapshot.RegistryDigest}，manifest={manifest.RegistryDigest}。");if(snapshot.ServerTick!=manifest.ServerTick)throw new InvalidDataException("世界快照 tick 不一致。");var stats = replication.Apply(snapshot.Entities, true, out _);
        // Ready gate 1/2：硬 critical（Character）unmatched 一律禁止 Ready
        var gate = EntityBindingGate.Evaluate(stats, EntityKindWire.Character);
        if (gate != null)
        {
            var details = string.Join(" | ", stats.MissingDetails.ToArray());
            FailClient("ENTITY_BINDING_INCOMPLETE", new InvalidDataException($"{gate} missing 前 20：{details}"));
            return;
        }
        // Ready gate 2/2：非 Character 关键类别（Door/Window/Item/Shared Inventory）missing 超容差 → 禁止 Ready
        var criticalMissing = EntityBindingGate.CountMissing(stats, EntityKindWire.Door, EntityKindWire.Window, EntityKindWire.Item, EntityKindWire.Inventory);
        if (criticalMissing > 0 && !SnapshotTolerance.Tolerate(criticalMissing, stats.Received))
        {
            var details2 = string.Join(" | ", stats.MissingDetails.ToArray());
            FailClient("ENTITY_BINDING_INCOMPLETE", new InvalidDataException($"关键实体类别（Door/Window/Item/Inventory）绑定缺失 {criticalMissing}/{stats.Received} 个，超过容差，禁止进入就绪。missing 前 20：{details2}"));
            return;
        }
var appliedInventories=0;var failedInventories=0;var loggedFailures=0;var skippedDeathDrop=0;foreach(var inventory in snapshot.Inventories){if(replication.Apply(inventory))appliedInventories++;else if(inventory.InventoryType==(int)Inventory.InvType.deathDrop){skippedDeathDrop++;}else{failedInventories++;missingEntities.Add(new EntityId(inventory.Value,inventory.Persistent));if(loggedFailures<8){loggedFailures++;log?.LogError($"共享容器快照无法绑定：ID={inventory.Value:X16}，名称={inventory.Name}，位置=({inventory.X:F1},{inventory.Y:F1},{inventory.Z:F1})，类型={inventory.InventoryType}。客户端候选：{replication.DescribeNearestInventory(inventory)}");}}}if(failedInventories>0){if(!DarkwoodMultiplayerFramework.Core.SnapshotTolerance.Tolerate(failedInventories,snapshot.Inventories.Length))throw new InvalidDataException($"有 {failedInventories} 个共享容器无法应用主机权威快照（客户端共享容器 {replication.SharedInventoryCount} 个），已阻止客户端误进入就绪状态。");log?.LogWarning($"FIX-007：{failedInventories}/{snapshot.Inventories.Length} 个共享容器在客户端世界中缺失（主机运行时生成物，如乌鸦/动物尸体），已跳过并继续就绪；等待 0.8.8 Spawn 生命周期补发。");}if(skippedDeathDrop>0)log?.LogWarning($"已跳过 {skippedDeathDrop} 个死亡掉落容器（跨端独立生成，ID 不一致属常态，不阻断就绪）。");if (stats.Missing > 0) log?.LogWarning($"[SYNC] 快照 missing 前 20：{string.Join(" | ", stats.MissingDetails.ToArray())}");
        SaveState.RecordSnapshotApplied(new WorldSnapshotApplied(manifest.SnapshotId,snapshot.Scene,snapshot.RegistryDigest,snapshot.ServerTick,stats.Applied));
        SendSnapshotAcknowledgement();
        log?.LogInfo($"世界快照应用完成：received={stats.Received} applied={stats.Applied} missing={stats.Missing} stale={stats.Stale}，共享容器 {appliedInventories}/{snapshot.Inventories.Length}，tick {snapshot.ServerTick}；等待主机确认。");
    }

    private ChunkTransferAssembler? bindingAssembler;
    private EntityBindingManifest pendingBindingManifest;

    internal void BeginBindingReceive(EntityBindingManifest manifest)
    {
        bindingAssembler = new ChunkTransferAssembler(manifest.TransferId, manifest.TotalBytes, manifest.ChunkCount, manifest.Sha256);
        pendingBindingManifest = manifest;
        log?.LogInfo($"开始接收实体绑定清单（第 {manifest.Generation} 代）：{manifest.EntityCount} 个描述符，{manifest.TotalBytes} 字节，{manifest.ChunkCount} 块。");
    }

    internal void ReceiveBindingChunk(EntityBindingChunk chunk)
    {
        if (bindingAssembler == null) return;
        bindingAssembler.Add(chunk.TransferId, chunk.Index, chunk.Total, chunk.Data, chunk.Hash);
        if (!bindingAssembler.IsComplete) return;
        var bytes = bindingAssembler.Build(); // SHA-256 全量校验
        bindingAssembler = null;
        var entries = ReplicationProtocolCodec.DecodeEntityBindingEntries(bytes);
        if (entries.Length != pendingBindingManifest.EntityCount)
        {
            FailClient("BINDING_COUNT_MISMATCH", new InvalidDataException($"绑定清单实体数不一致：manifest={pendingBindingManifest.EntityCount}，实际={entries.Length}。"));
            return;
        }
        CompleteEntityBinding(entries, pendingBindingManifest.Generation);
    }

    private void CompleteEntityBinding(EntityBindingEntryWire[] entries, int generation)
    {
        var candidates = scanner.BuildLocalCandidates(out var localComponents);
        var outcome = new EntityBindingMatcher().Match(entries, candidates);
        if (generation != replication.RegistryGeneration) replication.BeginNewGeneration(generation); // 代际换代：旧映射失效
        replication.BindFromManifest(entries, outcome, localComponents);
        replication.FreezeUnboundCharacters(localComponents); // 未绑定 Character 禁止静默本地模拟
        lastBindingGeneration = generation;
        log?.LogInfo($"[SYNC] binding host={entries.Length} bound={outcome.Bound} missing={outcome.Missing.Count} ambiguous={outcome.Ambiguous.Count} generation={generation}");
        if (outcome.Missing.Count > 0) log?.LogWarning($"[SYNC] 绑定 missing 前 20：{string.Join(" | ", outcome.MissingDetails.ToArray())}");
        if (outcome.Ambiguous.Count > 0) log?.LogWarning($"[SYNC] 绑定 ambiguous 前 20：{string.Join(" | ", outcome.AmbiguousDetails.ToArray())}");
    }

    private void RetrySnapshotAcknowledgement()
    {
        if(!SaveState.ShouldRetrySnapshotAck(Time.realtimeSinceStartup))return;
        SendSnapshotAcknowledgement();
    }

    private void SendSnapshotAcknowledgement()
    {
        if(SaveState.LastSnapshotApplied==null||clientSession==null)return;
        SaveState.RecordSnapshotAckSent(Time.realtimeSinceStartup);
        clientSession.Send(ProtocolMessageType.WorldSnapshotApplied,ReplicationProtocolCodec.Encode(SaveState.LastSnapshotApplied.Value));
        SaveState.SetProgress($"快照已应用，等待主机确认（第 {SaveState.SnapshotAckRetryCount} 次）");
        if(SaveState.SnapshotAckRetryCount>1)log?.LogWarning($"主机 Ready 确认尚未到达，正在重发快照应用确认：第 {SaveState.SnapshotAckRetryCount} 次。");
    }

    public bool TryRequestPickup(Item item)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||item==null)return false;
        if(!replication.TryGetId(item,out var id)||!replication.TryGetState(id,out var state)){log?.LogWarning("Pickup was not sent because the target has no registered EntityId.");return true;}
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.Pickup,id.Value,id.IsPersistent,state.Revision,Array.Empty<byte>());
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Pickup request {request.RequestId} sent for {id} revision {state.Revision}.");
        return true;
    }

    public bool TryRequestMeleeAttack(Player player,bool special)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||player==null)return false;
        if(InvItemClass.isNull(player.currentItem))return false;
        var item=player.currentItem;
        var hotbar=TryFindItemSlot(player.Hotbar,item,out var slotIndex);
        if(!hotbar&&!TryFindItemSlot(player.Inventory,item,out slotIndex)){log?.LogWarning("Melee attack was not sent because the active item has no inventory slot.");return false;}
        // Darkwood fires melee hits along transform.up; send the horizontal aim direction.
        var aim=player.transform.up;var pos=player.transform.position;
        var payload=ReplicationProtocolCodec.Encode(new AttackPayload(special?(byte)2:(byte)1,hotbar,slotIndex,aim.x,aim.z,pos.x,pos.z));
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.Attack,0,false,0,payload);
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Melee attack request {request.RequestId} sent: {(special?"special":"normal")}, {(hotbar?"hotbar":"backpack")} slot {slotIndex}.");
        return true;
    }

    private static bool TryFindItemSlot(Inventory inventory,InvItemClass item,out int slotIndex)
    {
        slotIndex=-1;
        if(inventory?.slots==null||InvItemClass.isNull(item))return false;
        for(var i=0;i<inventory.slots.Count;i++)
        {
            var slot=inventory.slots[i];
            if(slot!=null&&!InvItemClass.isNull(slot.invItem)&&ReferenceEquals(slot.invItem,item)){slotIndex=i;return true;}
        }
        return false;
    }

    public bool TryRequestDoorToggle(Door door)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||door==null)return false;
        if(!replication.TryGetId(door,out var id)){log?.LogWarning("Door toggle was not sent because the door has no registered EntityId.");return false;}
        ulong expectedRevision=0;
        if(replication.TryGetState(id,out var state))expectedRevision=state.Revision;
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.DoorInteract,id.Value,id.IsPersistent,expectedRevision,ReplicationProtocolCodec.Encode(new InteractPayload(0)));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Door interact request {request.RequestId} sent for {id}, revision {expectedRevision}.");
        return true;
    }

    public bool TryRequestWindowBarricade(Window window,int destHealth)
    {
        if(clientSession?.Session.Lifecycle.State!=ConnectionState.Ready||window==null)return false;
        if(!replication.TryGetId(window,out var id)){log?.LogWarning("Window barricade was not sent because the window has no registered EntityId.");return false;}
        ulong expectedRevision=0;
        if(replication.TryGetState(id,out var state))expectedRevision=state.Revision;
        var request=new ActionRequestMessage(Guid.NewGuid(),clientSession.PeerId,ActionKindWire.WindowInteract,id.Value,id.IsPersistent,expectedRevision,ReplicationProtocolCodec.Encode(new InteractPayload(destHealth)));
        pendingActions[request.RequestId]=request;
        clientSession.Send(ProtocolMessageType.ActionRequest,ReplicationProtocolCodec.Encode(request));
        log?.LogInfo($"Window barricade request {request.RequestId} sent for {id}, destHealth {destHealth}, revision {expectedRevision}.");
        return true;
    }
}

