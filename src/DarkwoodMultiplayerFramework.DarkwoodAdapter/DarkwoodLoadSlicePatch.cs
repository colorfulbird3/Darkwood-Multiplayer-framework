using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>
/// FIX-015：客户端联机存档加载切片化。
///
/// 游戏原版 SaveManager.loadObjs 每 500 个对象才 yield 一次，中间全部是同步
/// Instantiate（Core.AddPrefab）——大世界（3000+ 对象）在弱机上单段风暴可达
/// 数十秒：主线程冻结、进度条纹丝不动地停在 0%（percentLoaded 只在切片边界
/// 更新）、DMF 周期逻辑停摆。用户看到的"卡在加载存档 0%"实为进度不可见。
///
/// 本补丁仅在"客户端加载联机下载存档"期间把 loadObjs 替换为切片枚举器：
/// - 每帧最多 25 个对象（进度条按帧平滑前进，弱机也能看到进度）；
/// - 保持原版 unloadTextures 节奏（每 500 个对象一次，32 位进程内存保护）；
/// - 单对象异常容错跳过（不因一个损坏对象卡死整个加载链）；
/// - 进度语义与原版一致：objsLeftToLoad 按已载数量递减，percentLoaded
///   = (total - objsLeft) / (total * 2) * 100。
/// 单机/主机流程不受影响（原版枚举器原样执行）。
/// </summary>
[HarmonyPatch(typeof(SaveManager), "loadObjs")]
internal static class DarkwoodLoadSlicePatch
{
    private const int SliceSize = 25;
    private const int UnloadInterval = 500;

    private static bool Prefix(SaveManager __instance, List<SaveManager.SavedObj> savedObjs, bool Dynamic, ref IEnumerator __result)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null || !runtime.IsClient || !runtime.ClientSaveLoadPending) return true;
        if (savedObjs == null || savedObjs.Count == 0) return true;
        DarkwoodAdapterRuntime.LogMessage($"客户端加载切片生效：{savedObjs.Count} 个对象，每帧 {SliceSize} 个（FIX-015）。");
        __result = new SlicedLoadEnumerator(__instance, savedObjs, Dynamic);
        return false;
    }

    private sealed class SlicedLoadEnumerator : IEnumerator
    {
        private static readonly MethodInfo LoadObjMethod =
            AccessTools.Method(typeof(SaveManager), "loadObj", new[] { typeof(SaveManager.SavedObj), typeof(bool) })
            ?? throw new MissingMethodException(typeof(SaveManager).FullName, "loadObj");
        private static readonly FieldInfo ObjectsLeftField =
            AccessTools.Field(typeof(SaveManager), "objsLeftToLoad")
            ?? throw new MissingFieldException(typeof(SaveManager).FullName, "objsLeftToLoad");
        private static readonly FieldInfo TotalObjectsField =
            AccessTools.Field(typeof(SaveManager), "totalObjsToLoad")
            ?? throw new MissingFieldException(typeof(SaveManager).FullName, "totalObjsToLoad");
        private static readonly MethodInfo UnloadTexturesMethod =
            AccessTools.Method(typeof(Controller), "unloadTextures");

        private readonly SaveManager manager;
        private readonly List<SaveManager.SavedObj> list;
        private readonly bool isDynamic;
        private int index;
        private int processedSinceUnload;
        private bool finished;

        public SlicedLoadEnumerator(SaveManager manager, List<SaveManager.SavedObj> list, bool isDynamic)
        {
            this.manager = manager;
            this.list = list;
            this.isDynamic = isDynamic;
            index = list.Count - 1;
        }

        public object Current => null!;
        public void Reset() { }

        public bool MoveNext()
        {
            if (finished) return false;
            try
            {
                var slice = 0;
                while (index >= 0 && slice < SliceSize)
                {
                    var saved = list[index];
                    index--;
                    slice++;
                    processedSinceUnload++;
                    if (saved == null) continue;
                    try
                    {
                        LoadObjMethod.Invoke(manager, new object[] { saved, isDynamic });
                    }
                    catch (Exception error)
                    {
                        DarkwoodAdapterRuntime.LogMessage($"跳过损坏存档对象：{saved.Name}（{error.Message}）。");
                    }
                    if (processedSinceUnload >= UnloadInterval)
                    {
                        processedSinceUnload = 0;
                        InvokeUnloadTextures();
                    }
                }
                if (index < 0)
                {
                    InvokeUnloadTextures();
                    finished = true;
                }
                UpdateProgress(slice);
                return !finished;
            }
            catch (Exception error)
            {
                DarkwoodAdapterRuntime.LogMessage($"客户端加载切片中断：{error.Message}。");
                finished = true;
                return false;
            }
        }

        private void UpdateProgress(int slice)
        {
            try
            {
                var objsLeft = (int)(ObjectsLeftField.GetValue(manager) ?? 0);
                var total = (int)(TotalObjectsField.GetValue(manager) ?? 0);
                ObjectsLeftField.SetValue(manager, objsLeft - slice);
                var worldGen = Singleton<WorldGenerator>.Instance;
                if (worldGen != null && total > 0)
                {
                    var loaded = total - Math.Max(0, objsLeft - slice);
                    worldGen.percentLoaded = loaded / (total * 2f) * 100f;
                }
            }
            catch
            {
                // 进度是辅助信息，失败不影响加载本身。
            }
        }

        private void InvokeUnloadTextures()
        {
            if (UnloadTexturesMethod == null) return;
            try
            {
                var controller = Singleton<Controller>.Instance;
                if (controller != null) UnloadTexturesMethod.Invoke(controller, null);
            }
            catch
            {
                // 内存整理失败不阻断加载。
            }
        }
    }
}
