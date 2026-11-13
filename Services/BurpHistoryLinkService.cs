using System.Diagnostics;
using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record BurpHistoryLinkResult(string Status, int Matched, string Message);
public sealed class BurpHistoryLinkService
{
    public async Task<BurpHistoryLinkResult> LinkAsync(EngagementProject project, CancellationToken token = default)
    {
        foreach (var request in project.Requests) { request.BurpProxyRequestId = null; request.BurpCaptureId = ""; request.BurpRequestTime = null; }
        var fingerprints = project.Requests.Select(x => x.RequestFingerprint).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (fingerprints.Length == 0) return new("NO_CAPTURE_FINGERPRINTS", 0, "要求内容の照合情報がありません。保存済みHARを取り込んで再計算してください。");
        var node = EnvironmentProbe.FindNode();
        var java = AppSettingsService.Load().JavaPath;
        if (!File.Exists(java)) java = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BurpSuite", "jre", "bin", "java.exe");
        if (!File.Exists(java)) java = Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", "bin", "java.exe");
        if (!File.Exists(java)) java = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(x => Path.Combine(x.Trim('"'), "java.exe")).FirstOrDefault(File.Exists) ?? "";
        var jar = BurpProxyLocation.Resolve();
        var script = Path.Combine(AppContext.BaseDirectory, "tools", "burp-proxy-history.js");
        if (!File.Exists(node) || !File.Exists(java) || !File.Exists(jar) || !File.Exists(script))
            return new("MCP_NOT_CONFIGURED", 0, "Burp履歴の読取接続に必要なNode・Java・MCPプロキシを確認できません。IDは未照合です。");
        var directory = ProjectPaths.Resolve(project, "temp/burp-history/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "lookup.json");
        var origins = project.AllowedOrigins.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https").Select(x => new Uri(x).GetLeftPart(UriPartial.Authority)).Distinct().ToArray();
        var observations = project.Requests.Where(x => !string.IsNullOrEmpty(x.RequestFingerprint))
            .Select(x => new { id = x.Id, fingerprint = x.RequestFingerprint, observedAt = x.ObservedAt }).ToArray();
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { command = java, args = new[] { "-jar", jar, "--sse-url", "http://127.0.0.1:9876" }, origins, fingerprints, observations }), token);
        var info = new ProcessStartInfo(node!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(script); info.ArgumentList.Add(configPath);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var json = await output; await error;
            using var result = JsonDocument.Parse(json);
            var status = result.RootElement.GetProperty("status").GetString() ?? "HISTORY_LOOKUP_FAILED";
            var linked = 0;
            if (status == "MATCHED")
                foreach (var entry in result.RootElement.GetProperty("matches").EnumerateArray())
                {
                    var fingerprint = entry.GetProperty("fingerprint").GetString();
                    var id = entry.GetProperty("requestId").GetInt32();
                    if (id < 0) continue;
                    var observationId = entry.TryGetProperty("observationId", out var observation) && observation.ValueKind == JsonValueKind.String ? observation.GetString() : null;
                    foreach (var request in project.Requests.Where(x => x.RequestFingerprint == fingerprint && x.Id == observationId))
                    {
                        request.BurpProxyRequestId = id;
                        request.BurpCaptureId = entry.GetProperty("captureId").GetString() ?? "";
                        request.BurpRequestTime = entry.TryGetProperty("requestTime", out var time) && time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var stamp) ? stamp : null;
                        linked++;
                    }
                }
            var message = status switch
            {
                "MATCHED" => $"Burp Proxy historyの要求内容と照合し、{linked:N0}件にネイティブRequest IDを対応付けました。同じ内容の要求は観測時刻に最も近い履歴を選びます。",
                "UNSUPPORTED_REQUEST_IDS" => "使用中のMCPはProxy Request ID付き履歴一覧に未対応です。対応版への更新が必要です。IDは推測していません。",
                _ => "Burpの履歴参照または照合を完了できませんでした。未照合のIDは推測しません。"
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { status, linked, message }), token);
            return new(status, linked, message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("TIME_LIMIT", 0, "Burp履歴照合の時間上限に達しました。未照合のIDは生成しません。"); }
        catch (Exception ex) when (ex is JsonException or IOException)
        { return new("HISTORY_LOOKUP_FAILED", 0, "Burp履歴の読取結果を処理できません。IDは未照合です。"); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
