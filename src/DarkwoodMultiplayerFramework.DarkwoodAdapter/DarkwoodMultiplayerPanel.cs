using DarkwoodMultiplayerFramework.Core;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>Small IMGUI control surface for the standalone host/client runtime.</summary>
public sealed class DarkwoodMultiplayerPanel : MonoBehaviour
{
    private Rect windowRect = new Rect(40f, 20f, 430f, 520f);
    private Vector2 scrollPosition;
    private bool visible;
    private bool f6WasDown;
    private bool initialized;
    private string address = "127.0.0.1";
    private string port = "17777";
    private string playerName = string.Empty;
    private string notice = string.Empty;
    private CursorLockMode previousLockMode;
    private bool previousCursorVisible;
    private GUIStyle? titleStyle;
    private GUIStyle? statusStyle;
    private GUIStyle? mutedStyle;
    private Font? uiFont;

    private void Update()
    {
        var f6 = Input.GetKey(KeyCode.F6);
        if (f6 && !f6WasDown) SetVisible(!visible);
        f6WasDown = f6;
    }

    private void SetVisible(bool next)
    {
        visible = next;
        DarkwoodAdapterRuntime.LogMessage(next ? "联机面板已显示（F6）。" : "联机面板已隐藏（F6）。");
        if (visible)
        {
            var runtime = DarkwoodAdapterRuntime.Instance;
            if (runtime != null) { address = runtime.ConfiguredAddress; port = runtime.ConfiguredPort.ToString(); playerName = runtime.ConfiguredPlayerName; }
            previousLockMode = UnityEngine.Cursor.lockState;
            previousCursorVisible = UnityEngine.Cursor.visible;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
        }
        else
        {
            UnityEngine.Cursor.lockState = previousLockMode;
            UnityEngine.Cursor.visible = previousCursorVisible;
        }
    }

    private void OnDestroy()
    {
        if (visible) { UnityEngine.Cursor.lockState = previousLockMode; UnityEngine.Cursor.visible = previousCursorVisible; }
    }

    private void OnGUI()
    {
        if (!visible) return;
        InitializeStyles();
        windowRect.height = Mathf.Clamp(Screen.height - 40f, 300f, 600f);
        windowRect.width = Mathf.Min(430f, Screen.width - 20f);
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Screen.width - windowRect.width));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Screen.height - windowRect.height));
        var previousFont = GUI.skin.font;
        if (uiFont != null) GUI.skin.font = uiFont;
        windowRect = GUI.Window(7087, windowRect, DrawWindow, "");
        GUI.skin.font = previousFont;
    }

    private void InitializeStyles()
    {
        if (initialized) return;
        initialized = true;
        uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" }, 14);
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        titleStyle.font = uiFont;
        titleStyle.normal.textColor = new Color(.88f, .9f, .92f, 1f);
        statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        statusStyle.font = uiFont;
        statusStyle.normal.textColor = new Color(.72f, .86f, .78f, 1f);
        mutedStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        mutedStyle.font = uiFont;
        mutedStyle.normal.textColor = new Color(.68f, .68f, .68f, 1f);
    }

    private void DrawWindow(int id)
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        GUILayout.BeginVertical();
        GUILayout.Label("DARKWOOD 联机框架", titleStyle!);
        GUILayout.Label(Plugin.DisplayVersion + "  |  F6 关闭", mutedStyle!);
        GUILayout.Space(8f);

        scrollPosition = GUILayout.BeginScrollView(scrollPosition, false, true, GUILayout.ExpandHeight(true));

        GUILayout.Label("主机地址");
        address = GUILayout.TextField(address, 128);
        GUILayout.Label("TCP 端口");
        port = GUILayout.TextField(port, 5);
        GUILayout.Label("玩家名称（热加入身份，主机据此保存你的物品）");
        playerName = GUILayout.TextField(playerName, 64);

        GUILayout.Label("联机人数（含主机；新生成的柜子物品倍率）");
        GUILayout.BeginHorizontal();
        for (var players = 1; players <= 4; players++)
        {
            var oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && runtime != null && !runtime.IsHost && !runtime.IsClient;
            if (GUILayout.Button(runtime != null && runtime.ConfiguredPlayerCount == players ? "[" + players + "]" : players.ToString(), GUILayout.Height(26f)))
                runtime?.SetPlayerCount(players);
            GUI.enabled = oldEnabled;
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("保存", GUILayout.Height(30f))) Save(runtime);
        if (GUILayout.Button("创建", GUILayout.Height(30f))) { if (Save(runtime)) Run(runtime, true); }
        // A second Join while the first session is handshaking or loading a
        // save opens another transport and aborts the previous one. Keep it
        // disabled for the entire active session; Stop is the explicit way
        // to return to the disconnected state.
        var joinEnabled = runtime == null || (!runtime.IsHost && !runtime.IsClient && runtime.State == ConnectionState.Disconnected);
        var oldGuiEnabled = GUI.enabled;
        GUI.enabled = oldGuiEnabled && joinEnabled;
        if (GUILayout.Button("加入", GUILayout.Height(30f))) { if (Save(runtime)) Run(runtime, false); }
        GUI.enabled = oldGuiEnabled;
        if (GUILayout.Button("停止", GUILayout.Height(30f))) { runtime?.StopNetwork(); notice = "联机会话已停止。"; }
        GUILayout.EndHorizontal();

        GUILayout.Space(12f);
        GUILayout.Label("连接状态", statusStyle!);
        if (runtime == null) GUILayout.Label("联机运行时不可用");
        else
        {
            GUILayout.Label("角色        " + (runtime.IsHost ? "主机" : runtime.IsClient ? "客户端" : "离线"));
            GUILayout.Label("状态        " + runtime.StateDisplay);
            if (!string.IsNullOrEmpty(runtime.TransferProgress)) GUILayout.Label("进度        " + runtime.TransferProgress, mutedStyle!);
            GUILayout.Label("连接地址    " + runtime.ConfiguredAddress + ":" + runtime.ConfiguredPort);
            GUILayout.Label("玩家 ID     " + runtime.LocalPeerId);
            GUILayout.Label("就绪玩家    " + runtime.ReadyPeerCount);
            GUILayout.Label("场景        " + runtime.CurrentScene);
            GUILayout.Label("实体注册表  " + runtime.RegistryCount + (runtime.RegistryDigest.Length > 10 ? "  " + runtime.RegistryDigest.Substring(0, 10) + "..." : string.Empty));
            GUILayout.Label("握手完成    " + (runtime.HandshakeComplete ? "是" : "否"));
            GUILayout.Space(8f);
            GUILayout.Label("动作统计", statusStyle!);
            GUILayout.Label("已接受 " + runtime.AcceptedActionCount + "   已拒绝 " + runtime.RejectedActionCount + "   重复 " + runtime.DuplicateActionCount);
            var error = runtime.SessionError;
            if (!string.IsNullOrEmpty(error)) GUILayout.Label("最近错误：" + error, mutedStyle!);
        }
        var statusNotice = GetStatusNotice(runtime);
        if (!string.IsNullOrEmpty(statusNotice)) { GUILayout.Space(6f); GUILayout.Label(statusNotice, mutedStyle!); }
        GUILayout.Space(8f);
        GUILayout.Label("F1 创建主机  |  F2 加入  |  F3 停止  |  F6 面板", mutedStyle!);
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 42f));
    }

    private string GetStatusNotice(DarkwoodAdapterRuntime? runtime)
    {
        if (runtime == null) return notice;
        if (runtime.IsHost && runtime.State == ConnectionState.Ready)
            return "\u4e3b\u673a\u5df2\u5c31\u7eea\uff0c\u7b49\u5f85\u5ba2\u6237\u7aef\u52a0\u5165\u3002";
        if (runtime.State == ConnectionState.Failed)
            return "\u8054\u673a\u5931\u8d25\uff1a" + runtime.SessionError;
        if (runtime.IsClient && runtime.State == ConnectionState.Ready)
            return "\u5df2\u8054\u673a\u5c31\u7eea\u3002";
        return notice;
    }

    private bool Save(DarkwoodAdapterRuntime? runtime)
    {
        if (runtime == null) { notice = "联机运行时不可用。"; return false; }
        if (!runtime.ApplyNetworkConfiguration(address, port, out var error)) { notice = error; return false; }
        if (!runtime.ApplyPlayerName(playerName, out var nameError)) { notice = nameError; return false; }
        notice = "网络配置已保存。";
        return true;
    }

    private void Run(DarkwoodAdapterRuntime? runtime, bool host)
    {
        try { if (host) runtime?.StartHost(); else runtime?.ConnectClient(); notice = host ? "正在创建主机……" : "正在连接……"; }
        catch (System.Exception error) { notice = error.Message; }
    }
}
