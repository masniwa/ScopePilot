using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class AiInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractionModePersistsAndSelectsToolsAndPrompt(bool enabled)
    {
        using var temp = new TestFolder();
        var project = temp.Project();
        project.EnableAiInteractions = enabled;
        await new ProjectStore().SaveAsync(project);
        Assert.Equal(enabled, (await new ProjectStore().LoadAsync(project.ProjectFilePath)).EnableAiInteractions);
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "engagement.json")));
        Assert.Equal(enabled, contract.RootElement.GetProperty("aiInteractions").GetBoolean());
        var prompt = await File.ReadAllTextAsync(Path.Combine(run, "prompt.md"));
        Assert.Equal(enabled, prompt.Contains("browser_click"));
        Assert.Equal(!enabled, prompt.Contains("フォーム送信、クリックによる更新"));
        Assert.Equal(enabled, prompt.Contains("operationCoverage"));
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run,"result-schema.json")));
        Assert.Equal(enabled, schema.RootElement.GetProperty("properties").TryGetProperty("operationCoverage",out _));
        var tools = McpSetupService.PlaywrightTools(enabled);
        Assert.Equal(enabled, tools.Contains("browser_fill_form"));
        Assert.Equal(enabled, tools.Contains("browser_click"));
        Assert.DoesNotContain("browser_run_code", tools);
        Assert.DoesNotContain("browser_evaluate", tools);
        Assert.Equal(enabled, tools.Contains("browser_file_upload"));
        Assert.Equal(enabled, tools.Contains("browser_hover"));
        Assert.Equal(enabled, tools.Contains("browser_drag"));
        Assert.Equal(enabled, tools.Contains("browser_mouse_drag_xy"));
        Assert.Equal(enabled, tools.Contains("browser_mouse_down"));
        Assert.Equal(enabled, tools.Contains("browser_mouse_up"));
    }

    [Fact]
    public async Task PendingUiOperationsContinueAndMergeWithoutDroppingPriorEvidence()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        var report = Path.Combine(run, "exploration-summary.json");
        await File.WriteAllTextAsync(report, """{"status":"partial","operationCoverage":[{"page":"Items","operation":"Create","outcome":"observed"},{"page":"Items","operation":"Edit","outcome":"not_attempted"}]}""");
        Assert.NotNull(await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None));
        await File.WriteAllTextAsync(report, """{"status":"completed","operationCoverage":[{"page":"Items","operation":"Edit","outcome":"observed"}]}""");
        await ExplorationCoverage.MergeAsync(run, 1, CancellationToken.None);
        using var merged = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.Equal(2, merged.RootElement.GetProperty("operationCoverage").GetArrayLength());
        Assert.All(merged.RootElement.GetProperty("operationCoverage").EnumerateArray(), item => Assert.Equal("observed", item.GetProperty("outcome").GetString()));
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, 2, CancellationToken.None));
        await File.WriteAllTextAsync(report, """{"status":"partial","operationCoverage":[{"page":"Items","operation":"Edit","outcome":"not_attempted"}]}""");
        await File.WriteAllTextAsync(Path.Combine(run, "fast-crawl-summary.json"), JsonSerializer.Serialize(new { startedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReadOnlyModeNeverSchedulesMutationContinuation()
    {
        using var temp = new TestFolder();
        var run = await new ExplorationPackageBuilder().BuildAsync(temp.Project());
        await File.WriteAllTextAsync(Path.Combine(run, "exploration-summary.json"), """{"status":"partial","operationCoverage":[{"page":"Items","operation":"Edit","outcome":"not_attempted"}]}""");
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None));
        Assert.Null(await ExplorationCoverage.ExecutionAsync(run, CancellationToken.None));
    }

    [Fact]
    public async Task SpecialOperationsAreListedForHumansWithoutAutomaticRetry()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(run, "exploration-summary.json"), """{"status":"partial","operationCoverage":[{"page":"Profile","operation":"MFA setup","outcome":"manual_required","reason":"Open the MFA panel and follow the authenticator instructions manually."}]}""");
        var review = await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None);
        Assert.Contains("未実行キュー:\n[]", review);
        Assert.EndsWith(ExplorationPhase.ReviewInstruction, review);
        Assert.Equal(1, await ExplorationCoverage.WriteManualOperationsAsync(run, CancellationToken.None));
        Assert.Contains("MFA setup", await File.ReadAllTextAsync(Path.Combine(run, "manual-operations.txt")));
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "result-schema.json")));
        Assert.Contains("manual_required", schema.RootElement.GetProperty("properties").GetProperty("operationCoverage").GetProperty("items").GetProperty("properties").GetProperty("outcome").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task IndependentInventoryFollowsTheNarrowPreviousQueueAndOrdersGlobalOperations()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(run, "exploration-summary.json"), """{"status":"partial","operationCoverage":[{"page":"Trash","operation":"Empty trash","outcome":"blocked","reason":"依存データが残っている"}]}""");
        var review = (await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None))!;
        Assert.True(review.LastIndexOf(ExplorationPhase.ReviewInstruction, StringComparison.Ordinal) > review.LastIndexOf("未実行キュー:", StringComparison.Ordinal));
        Assert.Contains("未実行キューは、棚卸し対象の上限ではありません", review);
        Assert.Contains("前回報告にない候補も1操作1行", review);
        Assert.Contains("永久禁止ではなく操作順の条件", review);
        Assert.Contains("件数・ページ送り等の依存する確認を終え", review);
        Assert.Contains("許可範囲に収まらない場合は送信せず", review);
        Assert.Contains("特殊操作はmanual_required", review);
    }

    [Fact]
    public async Task LogoutRequiresIndependentReviewAndNoPendingOrdinaryOperation()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        var report = Path.Combine(run, "exploration-summary.json");
        var complete = """{"status":"partial","operationCoverage":[{"page":"Items","operation":"Edit","outcome":"observed"},{"page":"Profile","operation":"MFA","outcome":"manual_required"}]}""";
        await File.WriteAllTextAsync(report, complete);
        Assert.Null(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        var review = await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None);
        Assert.Contains("独立した棚卸し", review);
        await File.WriteAllTextAsync(report, """{"status":"partial","operationCoverage":[{"page":"Items","operation":"Delete individually","outcome":"not_attempted"}]}""");
        await ExplorationCoverage.MergeAsync(run, 1, CancellationToken.None);
        Assert.Null(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, ExplorationPhase.FileName)))) Assert.False(state.RootElement.GetProperty("allowLogout").GetBoolean());
        Assert.NotNull(await ExplorationCoverage.ContinuationAsync(run, 2, CancellationToken.None));
        await File.WriteAllTextAsync(report, """{"status":"completed","operationCoverage":[{"page":"Items","operation":"Delete individually","outcome":"observed"}]}""");
        await ExplorationCoverage.MergeAsync(run, 2, CancellationToken.None);
        Assert.NotNull(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        using var final = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, ExplorationPhase.FileName)));
        Assert.True(final.RootElement.GetProperty("allowLogout").GetBoolean());
        Assert.Equal("finalization", final.RootElement.GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("権限パネルへ進めず、現在は未認証", true)]
    [InlineData("テストデータを削除済みで再作成が必要", true)]
    [InlineData("候補取得がHTTP403 Forbidden", false)]
    [InlineData("候補取得で403を再確認", false)]
    public void AuthenticationAndMissingFixturesRemainPending(string reason, bool expected)
    {
        using var value = JsonDocument.Parse(JsonSerializer.Serialize(new { operation = "Edit", outcome = "blocked", reason }));
        Assert.Equal(expected, ExplorationCoverage.IsPending(value.RootElement));
    }

    [Fact]
    public async Task TemporarilyUnavailablePaginationStillPreventsFinalLogout()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(run, "coverage-review-completed.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(run, "exploration-summary.json"), """{"status":"partial","operationCoverage":[{"page":"Items","operation":"Next page","outcome":"unavailable","reason":"確認時は最大2件、最小表示件数25で次へが無効。ページ2の通信は未取得。"}]}""");
        Assert.Null(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        Assert.NotNull(await ExplorationCoverage.ContinuationAsync(run, 2, CancellationToken.None));
        using var phase = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, ExplorationPhase.FileName)));
        Assert.False(phase.RootElement.GetProperty("allowLogout").GetBoolean());
    }

    [Theory]
    [InlineData("Only 2 items: next page is disabled.", true)]
    [InlineData("件数不足で次へをクリックできない", true)]
    [InlineData("共有メニューは空。権限がありません。", false)]
    public void TemporaryUnavailableIsDifferentFromRoleUnavailable(string reason, bool expected)
    {
        using var value = JsonDocument.Parse(JsonSerializer.Serialize(new { operation = "Operation", outcome = "unavailable", reason }));
        Assert.Equal(expected, ExplorationCoverage.IsPending(value.RootElement));
    }

    [Fact]
    public async Task AbsoluteUiUrlAndRelativeUrlMergeTheSameOperation()
    {
        using var temp = new TestFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "exploration-round-1.json"), """{"status":"partial","operationCoverage":[{"page":"/items/1/edit","operation":"Update","outcome":"failed"}]}""");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "exploration-summary.json"), """{"status":"completed","operationCoverage":[{"page":"https://example.test/items/2/edit?tab=a","operation":"Update","outcome":"observed"}]}""");
        await ExplorationCoverage.MergeAsync(temp.Path, 1, CancellationToken.None);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "exploration-summary.json")));
        Assert.Equal("observed", Assert.Single(result.RootElement.GetProperty("operationCoverage").EnumerateArray()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task RecreatedFixtureUpdatesTheSameUiOperation()
    {
        using var temp = new TestFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "exploration-round-1.json"), """{"status":"partial","operationCoverage":[{"page":"/documents/1/details","operation":"Edit","outcome":"failed"}]}""");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "exploration-summary.json"), """{"status":"completed","operationCoverage":[{"page":"/documents/2/details","operation":"Edit","outcome":"observed"}]}""");
        await ExplorationCoverage.MergeAsync(temp.Path, 1, CancellationToken.None);
        using var merged = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "exploration-summary.json")));
        var item = Assert.Single(merged.RootElement.GetProperty("operationCoverage").EnumerateArray());
        Assert.Equal("observed", item.GetProperty("outcome").GetString());
        Assert.Equal("/documents/2/details", item.GetProperty("page").GetString());
    }

    [Fact]
    public async Task GeneratedPdfHasTwoDistinctPagesForUiSplitAndReorder()
    {
        using var temp = new TestFolder();
        await ExplorationUploadFixture.CreateAsync(temp.Path);
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "upload-fixture.json")));
        Assert.Equal(2, fixture.RootElement.GetProperty("pageCount").GetInt32());
        var pdf = await File.ReadAllTextAsync(fixture.RootElement.GetProperty("path").GetString()!);
        Assert.Contains("/Count 2", pdf); Assert.Contains("Page 1 of 2", pdf); Assert.Contains("Page 2 of 2", pdf);
    }

    [Fact]
    public async Task HarAfterAuthenticationChangeDoesNotReuseVerifiedRole()
    {
        using var temp = new TestFolder();
        var stamp = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "authentication-navigation.json"), JsonSerializer.Serialize(new {
            status="authenticated", loginFormVisible=false, authenticatedIndicatorVisible=true,
            actualUrl="https://example.test/home", activeRole="User", checkedAt=stamp.AddMinutes(-1)
        }));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "authentication-changes.jsonl"), JsonSerializer.Serialize(new {observedAt=stamp.AddSeconds(-1)}));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "fast-capture.har"), JsonSerializer.Serialize(new { log=new { entries=new[] {new {
            startedDateTime=stamp, request=new {method="GET",url="https://example.test/home"}, response=new {status=200,content=new {mimeType="text/html"}}
        }}}}));
        await new RunObservationCollector().CollectAsync(temp.Path,"https://example.test","User");
        var requests = await new RequestImporter().ImportAsync(Path.Combine(temp.Path,"observed-requests.jsonl"));
        Assert.Equal("認証状態未確認", Assert.Single(requests).Role);
    }

    [Fact]
    public async Task WireCapturePreservesQueuedRequestAndResponseOwnership()
    {
        using var temp = new TestFolder();
        var info = new ProcessStartInfo(EnvironmentProbe.FindNode()!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "wire-capture-regression.cjs"));
        info.ArgumentList.Add(AppContext.BaseDirectory);
        info.ArgumentList.Add(temp.Path);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Fact]
    public async Task RealBrowserAndMcpRecordNonGetAndEnforceBoundaries()
    {
        using var temp = new TestFolder();
        var info = new ProcessStartInfo(EnvironmentProbe.FindNode()!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ai-interaction-integration.cjs"));
        info.ArgumentList.Add(AppContext.BaseDirectory);
        info.ArgumentList.Add(temp.Path);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
    }
}
