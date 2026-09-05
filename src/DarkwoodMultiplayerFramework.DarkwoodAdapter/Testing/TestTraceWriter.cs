using System;
using System.IO;
using System.Threading;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>
/// v0.9.2 TestHarness：每个进程独立写自己的 trace（host.trace.log / client.trace.log），
/// 不依赖 BepInEx 共享 LogOutput.log（两进程可能覆盖）。
/// TestMode 才激活（DMF_TEST_ENABLED=1）。
/// </summary>
public sealed class TestTraceWriter : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object lockObj = new object();
    public string TracePath { get; }
    public string Role { get; }
    public int Pid { get; }
    public string RunId { get; }

    public TestTraceWriter(string traceDir, string role, string runId)
    {
        Role = role ?? "Unknown";
        RunId = runId ?? "norunid";
        Pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        Directory.CreateDirectory(traceDir);
        TracePath = Path.Combine(traceDir, $"{Role.ToLowerInvariant()}.trace.log");
        writer = new StreamWriter(new FileStream(TracePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = false,
        };
        WriteHeader();
    }

    private void WriteHeader()
    {
        lock (lockObj)
        {
            writer.WriteLine($"# DMF TestHarness trace");
            writer.WriteLine($"# role={Role} pid={Pid} runId={RunId}");
            writer.WriteLine($"# started={DateTime.UtcNow:O}");
            writer.WriteLine($"# tracePath={TracePath}");
            writer.Flush();
        }
    }

    public void Log(string tag, string message)
    {
        lock (lockObj)
        {
            writer.WriteLine($"{DateTime.UtcNow:O}\t{Role}\tPID={Pid}\t{tag}\t{message}");
            writer.Flush();
        }
    }

    /// <summary>把 BepInEx logger 的输出 mirror 写一份到本进程 trace（避免共享 LogOutput.log）</summary>
    public void MirrorBepInEx(string bepInExLine)
    {
        if (string.IsNullOrEmpty(bepInExLine)) return;
        lock (lockObj)
        {
            writer.WriteLine($"{DateTime.UtcNow:O}\t{Role}\tPID={Pid}\t[BEP-INEX]\t{bepInExLine}");
            writer.Flush();
        }
    }

    public void Dispose()
    {
        try { lock (lockObj) { writer.WriteLine($"# ended={DateTime.UtcNow:O}"); writer.Flush(); writer.Dispose(); } } catch { }
    }
}
