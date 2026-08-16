using DarkwoodMultiplayerFramework.Protocol;
using UnityEngine;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter;

/// <summary>World-space rescue progress bar drawn above the downed player's head.</summary>
public sealed class DarkwoodRescueOverlay : MonoBehaviour
{
    private static GUIStyle? labelStyle;
    /// <summary>FIX-014：GUI.skin 只能在 OnGUI 上下文内访问；静态构造在 OnGUI 外会抛 ArgumentException（0.8.8-alpha.6 实机崩溃）。改为懒初始化。</summary>
    private static GUIStyle LabelStyle
    {
        get
        {
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold
                };
            }
            return labelStyle;
        }
    }

    private void OnGUI()
    {
        var runtime = DarkwoodAdapterRuntime.Instance;
        if (runtime == null) return;
        var progress = runtime.LastRescueProgress;
        if (!progress.Active || progress.Progress <= 0f || progress.Progress > 1f) return;
        if (!runtime.TryGetKnownPlayerPosition(progress.TargetId, out var position)) return;
        var camera = Camera.main;
        if (camera == null) return;
        var screen = camera.WorldToScreenPoint(position + new Vector3(0f, 2.4f, 0f));
        if (screen.z <= 0f) return;
        screen.y = Screen.height - screen.y;
        const float width = 96f;
        const float height = 10f;
        var rect = new Rect(screen.x - width / 2f, screen.y - 28f, width, height);
        GUI.color = new Color(0f, 0f, 0f, 0.65f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = new Color(0.35f, 0.9f, 0.4f, 0.95f);
        GUI.DrawTexture(new Rect(rect.x + 1f, rect.y + 1f, (rect.width - 2f) * progress.Progress, rect.height - 2f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(screen.x - 90f, screen.y - 46f, 180f, 20f), "营救中 " + Mathf.RoundToInt(progress.Progress * 100f) + "%", LabelStyle);
    }
}
