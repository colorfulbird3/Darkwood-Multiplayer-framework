using System;
using System.Collections.Generic;

namespace DarkwoodMultiplayerFramework.DarkwoodAdapter.Testing;

/// <summary>单次断言结果。</summary>
public readonly struct TestCheck
{
    public string Id { get; }
    public string Phase { get; }
    public bool Pass { get; }
    public string Detail { get; }
    public TestCheck(string id, string phase, bool pass, string detail) { Id=id; Phase=phase; Pass=pass; Detail=detail??string.Empty; }
}

/// <summary>整个 scenario 结果（host / client 两侧均持有，由 PowerShell 汇总到 report.json）。</summary>
public sealed class DualInstanceTestResult
{
    public string RunId { get; set; } = "";
    public string Scenario { get; set; } = "";
    public int HostPid { get; set; }
    public int ClientPid { get; set; }
    public string Result { get; set; } = "FAIL"; // PASS / FAIL / ERROR
    public long DurationMs { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public List<TestCheck> Checks { get; } = new List<TestCheck>();
    public string HostTrace { get; set; } = "";
    public string ClientTrace { get; set; } = "";
    public string FailPhase { get; set; } = "";
    public string FailReason { get; set; } = "";

    public void Add(string id, string phase, bool pass, string detail)
    {
        Checks.Add(new TestCheck(id, phase, pass, detail));
        if (!pass && string.IsNullOrEmpty(FailPhase)) FailPhase = phase;
    }

    public string ToJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{");
        sb.AppendFormat("\"runId\":\"{0}\",", JsonEsc(RunId));
        sb.AppendFormat("\"scenario\":\"{0}\",", JsonEsc(Scenario));
        sb.AppendFormat("\"hostPid\":{0},", HostPid);
        sb.AppendFormat("\"clientPid\":{0},", ClientPid);
        sb.AppendFormat("\"result\":\"{0}\",", JsonEsc(Result));
        sb.AppendFormat("\"durationMs\":{0},", DurationMs);
        sb.AppendFormat("\"startedUtc\":\"{0}\",", JsonEsc(StartedUtc.ToString("O")));
        sb.AppendFormat("\"endedUtc\":\"{0}\",", JsonEsc(EndedUtc.ToString("O")));
        sb.AppendFormat("\"failPhase\":\"{0}\",", JsonEsc(FailPhase));
        sb.AppendFormat("\"failReason\":\"{0}\",", JsonEsc(FailReason));
        sb.AppendFormat("\"hostTrace\":\"{0}\",", JsonEsc(HostTrace));
        sb.AppendFormat("\"clientTrace\":\"{0}\",", JsonEsc(ClientTrace));
        sb.Append("\"checks\":[");
        for (var i = 0; i < Checks.Count; i++)
        {
            var c = Checks[i];
            if (i > 0) sb.Append(",");
            sb.Append("{");
            sb.AppendFormat("\"id\":\"{0}\",", JsonEsc(c.Id));
            sb.AppendFormat("\"phase\":\"{0}\",", JsonEsc(c.Phase));
            sb.AppendFormat("\"pass\":{0},", c.Pass ? "true" : "false");
            sb.AppendFormat("\"detail\":\"{0}\"", JsonEsc(c.Detail));
            sb.Append("}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static string JsonEsc(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}
