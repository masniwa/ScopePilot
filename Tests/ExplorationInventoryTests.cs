using System.IO;
using System.Text.Json;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class ExplorationInventoryTests
{
    private static async Task<string> RunAsync(TestFolder temp)
    {
        var project = temp.Project(); project.EnableAiInteractions = true;
        return await new ExplorationPackageBuilder().BuildAsync(project);
    }

    [Fact]
    public async Task CandidateWithoutOutcomeSurvivesOmissionAndPreventsLogout()
    {
        using var temp = new TestFolder(); var run = await RunAsync(temp);
        var path = Path.Combine(run, "exploration-summary.json");
        await File.WriteAllTextAsync(path, """{"status":"completed","operationCandidates":[{"page":"/items","operation":"Selected items / Delete","reason":"Visible after selecting two rows"}],"operationCoverage":[{"page":"/items","operation":"Create","outcome":"observed","requests":["request 12 POST /api/items → 201"],"reason":"Created"}]}""");
        await ExplorationCoverage.SynchronizeInventoryAsync(run, CancellationToken.None);
        await File.WriteAllTextAsync(path, """{"status":"completed","operationCandidates":[],"operationCoverage":[]}""");
        await ExplorationCoverage.SynchronizeInventoryAsync(run, CancellationToken.None);
        using (var report = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            var rows = report.RootElement.GetProperty("operationCoverage").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal("not_attempted", rows.Single(ExplorationCoverage.IsPending).GetProperty("outcome").GetString());
            Assert.Equal("partial", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(2, report.RootElement.GetProperty("operationCandidates").GetArrayLength());
        }
        await File.WriteAllTextAsync(Path.Combine(run, "coverage-review-completed.json"), "{}");
        Assert.Null(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        Assert.Equal(0, await ExplorationCoverage.WriteManualOperationsAsync(run, CancellationToken.None));
        Assert.Contains("Selected items / Delete", await File.ReadAllTextAsync(Path.Combine(run, "pending-operations.txt")));
        Assert.Contains(ExplorationCoverage.InventoryFileName, await CodexExplorationRunner.BuildPromptAsync(run, CancellationToken.None));
    }

    [Fact]
    public async Task CandidateBecomesObservedWithoutLosingPriorRequestOrManualOperation()
    {
        using var temp = new TestFolder(); var run = await RunAsync(temp);
        var path = Path.Combine(run, "exploration-summary.json");
        await File.WriteAllTextAsync(path, """{"status":"partial","operationCandidates":[{"page":"/items/1","operation":"Edit / General tab","reason":"Save button"}],"operationCoverage":[{"page":"/profile","operation":"MFA","outcome":"manual_required","requests":[],"reason":"Manual authenticator"}]}""");
        await ExplorationCoverage.SynchronizeInventoryAsync(run, CancellationToken.None);
        Assert.NotNull(await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None));
        await File.WriteAllTextAsync(path, """{"status":"completed","operationCandidates":[],"operationCoverage":[{"page":"https://example.test/items/2","operation":"Edit / General tab","outcome":"observed","requests":["request 23 PATCH /api/items/2 → 200"],"reason":"Saved and checked UI"}]}""");
        await ExplorationCoverage.SynchronizeInventoryAsync(run, CancellationToken.None);
        await ExplorationCoverage.MergeAsync(run, 1, CancellationToken.None);
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, 2, CancellationToken.None));
        Assert.NotNull(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        Assert.Equal(1, await ExplorationCoverage.WriteManualOperationsAsync(run, CancellationToken.None));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var edit = report.RootElement.GetProperty("operationCoverage").EnumerateArray().Single(row => row.GetProperty("operation").GetString() == "Edit / General tab");
        Assert.Equal("observed", edit.GetProperty("outcome").GetString());
        Assert.Contains("request 23", edit.GetProperty("requests")[0].GetString());
    }

    [Fact]
    public async Task ProgressPermitsMoreThanTwoReviewsButTwoStalledReviewsStop()
    {
        using var temp = new TestFolder(); var run = await RunAsync(temp);
        var path = Path.Combine(run, "exploration-summary.json");
        const string initial = """{"status":"partial","operationCoverage":[{"page":"/items","operation":"Create","outcome":"not_attempted"}]}""";
        await File.WriteAllTextAsync(path, initial);
        for (var round = 1; round <= 3; round++)
        {
            Assert.NotNull(await ExplorationCoverage.ContinuationAsync(run, round, CancellationToken.None));
            if (round == 1)
                await File.WriteAllTextAsync(path, """{"status":"partial","operationCoverage":[{"page":"/items","operation":"Create","outcome":"observed"},{"page":"/items","operation":"Edit","outcome":"not_attempted"}]}""");
            // Rounds 2/3 make no progress. The newly found operation after round
            // 1 must still get an opportunity beyond the former fixed two passes.
            await ExplorationCoverage.MergeAsync(run, round, CancellationToken.None);
        }
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, 4, CancellationToken.None));
        Assert.Contains("no_progress", await File.ReadAllTextAsync(Path.Combine(run, "coverage-stop.json")));
        Assert.Null(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
    }

    [Fact]
    public async Task RoundAndTimeLimitsLeavePendingItemsUnresolved()
    {
        using var temp = new TestFolder(); var run = await RunAsync(temp);
        await File.WriteAllTextAsync(Path.Combine(run, "exploration-summary.json"), """{"status":"completed","operationCoverage":[{"page":"/items","operation":"Create","outcome":"not_attempted"}]}""");
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, ExplorationCoverage.MaxContinuationRounds + 1, CancellationToken.None));
        Assert.Contains("round_limit", await File.ReadAllTextAsync(Path.Combine(run, "coverage-stop.json")));
        await File.WriteAllTextAsync(Path.Combine(run, "fast-crawl-summary.json"), JsonSerializer.Serialize(new { startedAt = DateTimeOffset.UtcNow.AddDays(-1) }));
        Assert.Null(await ExplorationCoverage.ContinuationAsync(run, 1, CancellationToken.None));
        Assert.Contains("time_limit", await File.ReadAllTextAsync(Path.Combine(run, "coverage-stop.json")));
        await ExplorationCoverage.WriteManualOperationsAsync(run, CancellationToken.None);
        Assert.Contains("not_attempted", await File.ReadAllTextAsync(Path.Combine(run, "pending-operations.json")));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "exploration-summary.json")));
        Assert.Equal("partial", report.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Not tried yet", true)]
    [InlineData("Menu not visible in current selection", true)]
    [InlineData("この機能は未導入。設定画面で無効と確認", false)]
    [InlineData("機能が存在しないか、前提データ不足か未確認", true)]
    [InlineData("機能が存在しないか未確認", true)]
    [InlineData("保存画面は未認証\n以前の観測（blocked）: HTTP403 Forbidden", true)]
    public void UnavailableNeedsAnActualTerminalBoundary(string reason, bool pending)
    {
        using var row = JsonDocument.Parse(JsonSerializer.Serialize(new { operation = "Edit", outcome = "unavailable", reason }));
        Assert.Equal(pending, ExplorationCoverage.IsPending(row.RootElement));
    }

    [Fact]
    public void ChangedErrorWordingDoesNotCountAsProgress()
    {
        Assert.False(ExplorationCoverage.MadeProgress(
            """{"operationCoverage":[{"page":"/items/1","operation":"Edit","outcome":"failed","reason":"Timeout"}]}""",
            """{"operationCoverage":[{"page":"/items/2","operation":"Edit","outcome":"failed","reason":"Different wording","requests":["more requests"]}]}"""));
    }

    [Fact]
    public async Task ReadOnlyContractDoesNotCreateAnInventory()
    {
        using var temp = new TestFolder(); var run = await new ExplorationPackageBuilder().BuildAsync(temp.Project());
        const string text = """{"status":"completed","operationCandidates":[{"page":"/items","operation":"Save","reason":"Candidate"}]}""";
        var path = Path.Combine(run, "exploration-summary.json"); await File.WriteAllTextAsync(path, text);
        await ExplorationCoverage.SynchronizeInventoryAsync(run, CancellationToken.None);
        Assert.Equal(text, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(run, ExplorationCoverage.InventoryFileName)));
    }
}
