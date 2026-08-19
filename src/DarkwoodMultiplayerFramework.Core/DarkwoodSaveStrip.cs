using System;
using System.Text.RegularExpressions;

namespace DarkwoodMultiplayerFramework.Core;

/// <summary>
/// 客户端存档打包用：剥离 savs.dat 静态存档中的 A* 导航图字段。
/// 纯文本变换，无 IO/JSON 依赖，便于在 SelfTests 中直接验证。
/// </summary>
public static class DarkwoodSaveStrip
{
    // 匹配 "graph":"..." 的值（允许值内含转义引号/反斜杠）。
    private static readonly Regex GraphField = new Regex("(\"graph\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Compiled);

    /// <summary>
    /// 尝试剥离 graph 字段。返回 null 表示不应剥离（字段缺失或出现次数不是 1），
    /// 调用方必须回退为原样传输——客户端侧的图反序列化跳过补丁仍会兜底。
    /// 剥离值必须用 null 而不是 ""：StaticSave.graph 是 byte[]，客户端
    /// JsonConvert.DeserializeObject<byte[]>("") 会抛异常导致整份存档加载失败
    /// （真机 ERROR WHEN LOADING DYNAMIC AND STATIC SAVE → 角色全缺 → 绑定 158 缺失）。
    /// </summary>
    public static string? TryStrip(string json)
    {
        var matches = GraphField.Matches(json);
        if (matches.Count != 1) return null;
        return GraphField.Replace(json, "$1null");
    }
}
