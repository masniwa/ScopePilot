using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public class ExplorationAvailabilityTests
{
    [Fact]
    public async Task ResultSchemaKeepsEveryObjectPropertyRequiredForCodexStructuredOutput()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "result-schema.json")));
        void Check(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("properties", out var properties))
                {
                    Assert.False(node.GetProperty("additionalProperties").GetBoolean());
                    var required = node.GetProperty("required").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x).ToArray();
                    Assert.Equal(properties.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray(), required);
                }
                foreach (var property in node.EnumerateObject()) Check(property.Value);
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Check(child);
        }
        Check(schema.RootElement);
    }

    [Theory]
    [InlineData("unsupported_in_version", "unavailable", "UI: format disabled after valid input", "Pinned source: format is hard disabled", false)]
    [InlineData("not_applicable_to_current_subject", "unavailable", "UI: already a member; no join button", "Pinned source: join shown only for nonmembers", false)]
    [InlineData("unsupported_in_version", "unavailable", "UI: disabled", "", true)]
    [InlineData("unsupported_in_version", "unavailable", "", "Source: disabled", true)]
    [InlineData("temporary", "unavailable", "UI: only two rows", "Source: pagination", true)]
    [InlineData("unsupported_in_version", "not_attempted", "UI: disabled", "Source: disabled", true)]
    [InlineData("unsupported_in_version", "failed", "UI: disabled", "Source: disabled", true)]
    public void TerminalDecisionRequiresUnavailableAndBothEvidenceKinds(string classification, string outcome, string uiEvidence, string sourceEvidence, bool pending)
    {
        using var row = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            operation = "Export", outcome, reason = "disabled; current prerequisite cannot be changed in this subject",
            unavailability = new { classification, uiEvidence, sourceEvidence }
        }));
        Assert.Equal(pending, ExplorationCoverage.IsPending(row.RootElement));
    }

    [Fact]
    public async Task ConfirmedUnavailablePermitsFinalizationWithoutBecomingObservedOrLosingEvidence()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        var report = new JsonObject
        {
            ["status"] = "partial",
            ["operationCoverage"] = new JsonArray(
                new JsonObject
                {
                    ["page"] = "/export", ["operation"] = "UI-001 Export", ["outcome"] = "unavailable",
                    ["requests"] = new JsonArray(), ["reason"] = "Disabled after valid input",
                    ["unavailability"] = new JsonObject
                    {
                        ["classification"] = "unsupported_in_version", ["uiEvidence"] = "snapshot: option remains disabled",
                        ["sourceEvidence"] = "version-pinned template: hard disabled option"
                    }
                },
                new JsonObject { ["page"] = "/profile", ["operation"] = "MFA", ["outcome"] = "manual_required", ["reason"] = "Authenticator input required" })
        };
        await File.WriteAllTextAsync(Path.Combine(run, "exploration-summary.json"), report.ToJsonString());
        Assert.Null(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        await File.WriteAllTextAsync(Path.Combine(run, "coverage-review-completed.json"), "{}");
        Assert.NotNull(await ExplorationPhase.FinalizationAsync(run, CancellationToken.None));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "exploration-summary.json")));
        Assert.Equal("partial", saved.RootElement.GetProperty("status").GetString());
        var rows = saved.RootElement.GetProperty("operationCoverage").EnumerateArray().ToArray();
        Assert.Equal("unavailable", rows[0].GetProperty("outcome").GetString());
        Assert.True(rows[0].TryGetProperty("unavailability", out _));
        Assert.Equal("manual_required", rows[1].GetProperty("outcome").GetString());
        await ExplorationCoverage.WriteManualOperationsAsync(run, CancellationToken.None);
        using var pending = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "pending-operations.json")));
        Assert.Equal(0, pending.RootElement.GetArrayLength());
    }
}
