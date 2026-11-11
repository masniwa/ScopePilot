using System.IO;
using System.Text;
using System.Text.Json;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class ExplorationAuthenticationEvidenceTests
{
    private static async Task<string> PrepareAsync(TestFolder temp, string path = "/login", int status = 302, bool oldRequest = false, bool oldNavigation = false)
    {
        var project = temp.Project(); project.EnableAiInteractions = true;
        project.StartUrl = "https://example.test/"; project.AllowedOrigins = "https://example.test";
        project.Roles = "未認証\nUser"; project.ActiveRole = "User";
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "engagement.json")));
        var started = contract.RootElement.GetProperty("runStartedAt").GetDateTimeOffset();
        var requestAt = oldRequest ? started.AddDays(-1) : started.AddSeconds(1);
        var checkedAt = oldNavigation ? started.AddDays(-1) : started.AddSeconds(3);
        await File.WriteAllTextAsync(Path.Combine(run, "authentication-navigation.json"), JsonSerializer.Serialize(new {
            status="authenticated", actualUrl="https://example.test/home", loginFormVisible=false,
            authenticatedIndicatorVisible=true, activeRole="User", checkedAt
        }));
        await File.WriteAllTextAsync(Path.Combine(run, "fast-crawl-summary.json"), JsonSerializer.Serialize(new {
            startedAt=started, pages=new[] {new { forms=new[] {new {method="POST",action="https://example.test/login",hasPasswordField=true}}}}
        }));
        var request = JsonSerializer.Serialize(new {kind="request",id=1,at=requestAt,method="POST",url="https://example.test"+path+"?next=DO_NOT_EXPOSE_QUERY",
            postDataBase64=Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"password\":\"DO_NOT_EXPOSE_PASSWORD\"}"))});
        var response = JsonSerializer.Serialize(new {kind="response",id=1,at=requestAt.AddMilliseconds(50),status});
        await File.WriteAllTextAsync(Path.Combine(run,"http-transactions.jsonl"),request+"\n"+(status>0?response+"\n":""));
        await File.WriteAllTextAsync(Path.Combine(run,"coverage-review-completed.json"),"{}");
        await WriteReportAsync(run, "observed");
        return run;
    }

    private static Task WriteReportAsync(string run, string editOutcome) => File.WriteAllTextAsync(Path.Combine(run,"exploration-summary.json"), JsonSerializer.Serialize(new {
        status="partial", operationCoverage=new[] {
            new {page="/home",operation="UI-001 ログイン送信",outcome="not_attempted",requests=Array.Empty<string>(),reason="AIの前に認証済みで、現在フォームがない"},
            new {page="/items",operation="Edit",outcome=editOutcome,requests=Array.Empty<string>(),reason="通常操作"},
            new {page="/profile",operation="MFA",outcome="manual_required",requests=Array.Empty<string>(),reason="手動"},
            new {page="/home",operation="ログアウト送信",outcome="not_attempted",requests=Array.Empty<string>(),reason="終了処理まで保留"}
        }
    }));

    [Theory]
    [InlineData("/login")]
    [InlineData("/api/session")]
    public async Task CurrentRunAuthenticationPreparationCanReleaseFinalizationWithoutCredentialDisclosure(string path)
    {
        using var temp = new TestFolder(); var run = await PrepareAsync(temp, path);
        Assert.NotNull(await ExplorationPhase.FinalizationAsync(run,CancellationToken.None));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run,"exploration-summary.json")));
        var login = report.RootElement.GetProperty("operationCoverage")[0];
        Assert.Equal("observed",login.GetProperty("outcome").GetString());
        Assert.Equal("POST https://example.test"+path,login.GetProperty("requests")[0].GetString());
        Assert.Equal("manual_required",report.RootElement.GetProperty("operationCoverage")[2].GetProperty("outcome").GetString());
        var evidence = await File.ReadAllTextAsync(Path.Combine(run,ExplorationAuthenticationEvidence.FileName));
        Assert.DoesNotContain("DO_NOT_EXPOSE_PASSWORD",evidence);
        Assert.DoesNotContain("DO_NOT_EXPOSE_QUERY",evidence);
        Assert.True(File.Exists(Path.Combine(run,"authentication-report-before-reconciliation.json")));
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task AnotherRunAuthenticationCannotSatisfyTheCurrentRun(bool oldRequest,bool oldNavigation)
    {
        using var temp = new TestFolder(); var run = await PrepareAsync(temp,oldRequest:oldRequest,oldNavigation:oldNavigation);
        Assert.Null(await ExplorationPhase.FinalizationAsync(run,CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(run,ExplorationAuthenticationEvidence.FileName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task MissingOrRejectedSubmissionDoesNotBecomeObserved(int status)
    {
        using var temp = new TestFolder(); var run = await PrepareAsync(temp,status:status);
        Assert.Null(await ExplorationPhase.FinalizationAsync(run,CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(run,ExplorationAuthenticationEvidence.FileName)));
    }

    [Fact]
    public async Task AuthenticationEvidenceDoesNotBypassOtherPendingUiWork()
    {
        using var temp = new TestFolder(); var run = await PrepareAsync(temp);
        await WriteReportAsync(run,"not_attempted");
        Assert.Null(await ExplorationPhase.FinalizationAsync(run,CancellationToken.None));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run,"exploration-summary.json")));
        Assert.Equal("observed",report.RootElement.GetProperty("operationCoverage")[0].GetProperty("outcome").GetString());
        Assert.Equal("not_attempted",report.RootElement.GetProperty("operationCoverage")[1].GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("UI-001 ログイン送信",true)]
    [InlineData("Login request",true)]
    [InlineData("ログアウト送信",false)]
    [InlineData("ログイン後のユーザー更新",false)]
    [InlineData("APIトークン再生成",false)]
    public void OnlyTheLoginOperationIsReconciled(string name,bool expected) => Assert.Equal(expected,ExplorationAuthenticationEvidence.IsLoginOperation(name));
}
