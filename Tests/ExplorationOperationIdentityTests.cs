using System.IO;
using System.Text.Json;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class ExplorationOperationIdentityTests
{
    [Theory]
    [InlineData("/tasks → Document link", "https://example.test/tasks", "Automatic acknowledgement")]
    [InlineData("Settings → General", "https://example.test/settings/general", "UI-101 General settings save")]
    public async Task SameOperationKeepsHistoricalFailureAndLatestSuccessfulCondition(string oldPage, string newPage, string operation)
    {
        using var temp = new TestFolder();
        object Row(string page, string outcome, string request, string reason) => new { page, operation, outcome, requests = new[] { request }, reason };
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "exploration-round-1.json"), JsonSerializer.Serialize(new { status="partial", operationCoverage=new[] { Row(oldPage,"failed","POST https://example.test/api/action/ → ERR_ABORTED","Same-tab navigation cancelled the request") } }));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "exploration-summary.json"), JsonSerializer.Serialize(new { status="partial", operationCoverage=new[] { Row(newPage,"observed","POST https://example.test/api/action/ → 200","New-tab operation confirmed") } }));
        await ExplorationCoverage.MergeAsync(temp.Path, 1, CancellationToken.None);
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path,"exploration-summary.json")));
        var row=Assert.Single(report.RootElement.GetProperty("operationCoverage").EnumerateArray());
        Assert.Equal("observed",row.GetProperty("outcome").GetString());
        Assert.Equal(2,row.GetProperty("requests").GetArrayLength());
        Assert.Contains("Same-tab",row.GetProperty("reason").GetString());
        Assert.Contains("New-tab",row.GetProperty("reason").GetString());
        Assert.False(ExplorationCoverage.IsPending(row));
    }

    [Fact]
    public async Task UnrelatedGenericSaveOperationsRemainSeparate()
    {
        using var temp=new TestFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path,"exploration-round-1.json"),"""{"operationCoverage":[{"page":"/settings/general","operation":"Save","outcome":"observed"}]}""");
        await File.WriteAllTextAsync(Path.Combine(temp.Path,"exploration-summary.json"),"""{"operationCoverage":[{"page":"/settings/permissions","operation":"Save","outcome":"failed"}]}""");
        await ExplorationCoverage.MergeAsync(temp.Path,1,CancellationToken.None);
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path,"exploration-summary.json")));
        var rows=report.RootElement.GetProperty("operationCoverage").EnumerateArray().ToArray();
        Assert.Equal(2,rows.Length);Assert.Contains(rows,ExplorationCoverage.IsPending);
    }

    [Fact]
    public async Task NewFailureIsNotHiddenByPreviousSuccess()
    {
        using var temp=new TestFolder();
        await File.WriteAllTextAsync(Path.Combine(temp.Path,"exploration-round-1.json"),"""{"operationCoverage":[{"page":"/items/1","operation":"Edit","outcome":"observed","reason":"Previous success","requests":["PATCH /api/items/1 → 200"]}]}""");
        await File.WriteAllTextAsync(Path.Combine(temp.Path,"exploration-summary.json"),"""{"operationCoverage":[{"page":"/items/2","operation":"Edit","outcome":"failed","reason":"Current save failed","requests":["PATCH /api/items/2 → 500"]}]}""");
        await ExplorationCoverage.MergeAsync(temp.Path,1,CancellationToken.None);
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path,"exploration-summary.json")));
        var row=Assert.Single(report.RootElement.GetProperty("operationCoverage").EnumerateArray());
        Assert.True(ExplorationCoverage.IsPending(row));Assert.Equal(2,row.GetProperty("requests").GetArrayLength());
    }

    [Fact]
    public async Task LegacyDuplicateReportCanFinalizeWithoutLosingManualOrFailureEvidence()
    {
        using var temp=new TestFolder();var project=temp.Project();project.EnableAiInteractions=true;
        var run=await new ExplorationPackageBuilder().BuildAsync(project);
        const string before="""{"status":"partial","operationCoverage":[{"page":"/tasks → Document link","operation":"Automatic acknowledgement","outcome":"failed","requests":["POST /api/tasks/acknowledge/ → ERR_ABORTED"],"reason":"Same-tab cancellation remains"},{"page":"https://example.test/tasks","operation":"Automatic acknowledgement","outcome":"observed","requests":["POST /api/tasks/acknowledge/ → 200"],"reason":"New-tab success"},{"page":"/profile","operation":"MFA","outcome":"manual_required","requests":[],"reason":"Manual input required"}]}""";
        await File.WriteAllTextAsync(Path.Combine(run,"exploration-summary.json"),before);
        await File.WriteAllTextAsync(Path.Combine(run,"coverage-review-completed.json"),"{}");
        Assert.NotNull(await ExplorationPhase.FinalizationAsync(run,CancellationToken.None));
        Assert.Equal(before,await File.ReadAllTextAsync(Path.Combine(run,"operation-report-before-reconciliation.json")));
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run,"exploration-summary.json")));
        var rows=report.RootElement.GetProperty("operationCoverage").EnumerateArray().ToArray();
        Assert.Equal(2,rows.Length);Assert.Contains(rows,r=>r.GetProperty("outcome").GetString()=="manual_required");
        Assert.Contains("Same-tab cancellation remains",rows[0].GetProperty("reason").GetString());
    }
}
