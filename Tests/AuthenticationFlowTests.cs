using System.IO;
using System.Text.Json;
using ScopePilot.Domain;
using ScopePilot.Services;
using ScopePilot.ViewModels;
using Xunit;

namespace ScopePilot.Tests;

public sealed class AuthenticationFlowTests
{
    [Theory]
    [InlineData("表示したGitHubリンクは許可Origin外へ転送するため未クリック。", false)]
    [InlineData("GitHubも許可Origin http://example.test:42000 外への遷移であり送信不可。", false)]
    [InlineData("転送先Originが許可Origin https://example.test と一致しないため未クリック。", false)]
    [InlineData("The observed link is outside allowed origin.", false)]
    [InlineData("URLは許可Origin外ではありません。ボタンはdisabledで未確認。", true)]
    [InlineData("URLは許可Origin外ではなく、件数不足で未確認。", true)]
    [InlineData("現在のメニューに入口がなく未確認。", true)]
    public void ExplicitExternalDestinationDoesNotKeepOrdinaryQueueOpen(string reason, bool pending)
    {
        using var item = JsonDocument.Parse(JsonSerializer.Serialize(new { operation = "Link navigation", outcome = "blocked", reason }));
        Assert.Equal(pending, ExplorationCoverage.IsPending(item.RootElement));
    }
    [Theory]
    [InlineData("http://192.168.0.27/login.php", "", false, false, true)]
    [InlineData("https://example.test/signin", "", false, false, true)]
    [InlineData("https://example.test/#/login", "Application", false, false, true)]
    [InlineData("https://example.test/#!/signin?return=home", "Application", false, false, true)]
    [InlineData("https://example.test/session", "", true, true, true)]
    [InlineData("https://example.test/auth", "サインイン", false, false, true)]
    [InlineData("https://example.test/change-password", "設定", true, false, false)]
    [InlineData("https://example.test/article", "account settings", false, false, false)]
    public void LoginHintsUsePathTitleAndInputTypesWithoutCredentials(string url, string title,
        bool password, bool user, bool expected) =>
        Assert.Equal(expected, AuthenticationFlow.LooksLikeLogin(url, title, password, user));

    [Fact]
    public async Task AuthenticationPageIsPassedToAiEvenWhenExcludedFromCandidateReview()
    {
        using var temp = new TestFolder();
        var project = temp.Project();
        project.DeferredCandidatePatterns.Add("GET https://example.test/login.php");
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(run, "crawl-pages.jsonl"),
            "{\"url\":\"https://example.test/login.php\",\"title\":\"ログイン\",\"forms\":[{\"method\":\"POST\",\"action\":\"https://example.test/login.php\",\"fields\":[\"username\",\"password\"],\"hasPasswordField\":true,\"hasUserField\":true}]}\n");
        var input = await new AiInputBuilder().BuildAsync(run);
        Assert.True(input.RequiresAi);
        Assert.Equal(0, input.FormPatternCount);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(input.Path));
        Assert.Equal("https://example.test/login.php", Assert.Single(json.RootElement.GetProperty("authenticationPages").EnumerateArray()).GetProperty("url").GetString());
    }

    [Fact]
    public async Task BrowserStartPreservesSpaRouteWhileApiRequestsRejectFragmentsAndScopeRemainsStrict()
    {
        using var temp = new TestFolder();
        var project = temp.Project();
        project.StartUrl = "https://example.test/#/login";
        project.AfterAuthenticationUrl = "https://example.test/#/dashboard";
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "engagement.json")));
        Assert.Equal(project.StartUrl, contract.RootElement.GetProperty("StartUrl").GetString());
        Assert.Throws<InvalidDataException>(() => ApiRequestFormatter.ValidateUrl(project.StartUrl));
        foreach (var invalid in new[] { "https://outside.test/#/login", "https://user:secret@example.test/#/login", "javascript:alert(1)", "https://example.test/#/login\n" })
        {
            project.StartUrl = invalid;
            Assert.Throws<InvalidDataException>(() => AuthenticationFlow.ValidateTarget(project));
        }
        project.StartUrl = "https://example.test/#/login";
        Assert.Contains("未到達", AuthenticationFlow.FormatNavigation("""{"status":"authenticated","actualUrl":"https://example.test/#/other","loginFormVisible":false,"authenticatedIndicatorVisible":true}""", project));
        Assert.Contains("認証後の到達先:", AuthenticationFlow.FormatNavigation("""{"status":"authenticated","actualUrl":"https://example.test/#/dashboard","loginFormVisible":false,"authenticatedIndicatorVisible":true}""", project));
        Assert.Equal("https://example.test/#/dashboard", AuthenticationFlow.DisplayUrl("https://example.test/?token=SECRET#/dashboard?token=SECRET"));
        Assert.DoesNotContain("SECRET", AuthenticationFlow.DisplayUrl("https://example.test/#access_token=SECRET"));
    }

    [Fact]
    public async Task ObservationCollectionAcceptsScopedSpaLandingAndKeepsHttpRequestIdentity()
    {
        using var temp = new TestFolder();
        var at = DateTimeOffset.UtcNow;
        var navigation = Path.Combine(temp.Path, "authentication-navigation.json");
        await File.WriteAllTextAsync(navigation, JsonSerializer.Serialize(new { status = "authenticated", actualUrl = "https://example.test/#/dashboard", loginFormVisible = false, authenticatedIndicatorVisible = true, activeRole = "User", checkedAt = at }));
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "fast-capture.har"), JsonSerializer.Serialize(new { log = new { entries = new[] { new {
            startedDateTime = at.AddSeconds(1), request = new { method = "GET", url = "https://example.test/api/items" }, response = new { status = 200, content = new { mimeType = "application/json" } }
        } } } }));
        await new RunObservationCollector().CollectAsync(temp.Path, "https://example.test", "User");
        var item = Assert.Single(await new RequestImporter().ImportAsync(Path.Combine(temp.Path, "observed-requests.jsonl")));
        Assert.Equal("User", item.Role);
        Assert.Equal("https://example.test/api/items", item.Url);
        await File.WriteAllTextAsync(navigation, JsonSerializer.Serialize(new { status = "authenticated", actualUrl = "https://outside.test/#/dashboard", loginFormVisible = false, authenticatedIndicatorVisible = true, activeRole = "User", checkedAt = at }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new RunObservationCollector().CollectAsync(temp.Path, "https://example.test", "User"));
    }

    [Fact]
    public async Task AuthenticationPreparationKeepsPublicUiLinksWithoutLeakingOpaqueNavigationState()
    {
        using var temp = new TestFolder();
        var project = temp.Project();
        project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(run, "crawl-pages.jsonl"), JsonSerializer.Serialize(new {
            url = "https://example.test/dialog", title = "Application", forms = Array.Empty<object>(), hasPasswordField = true, hasUserField = true,
            observedLinks = new object[] {
                new {url="https://example.test/#/register",label="Register"},
                new {url="https://example.test/#/register",label="Duplicate"},
                new {url="https://example.test/reset?token=SECRET",label="Reset password"},
                new {url="https://example.test/#access_token=SECRET",label="Opaque state"},
                new {url=(string?)null,label="Captured opaque link",requiresUiLookup=true},
                new {url="https://outside.test/register",label="External"},
                new {url="https://example.test:444/register",label="Other port"},
                new {url="https://user:SECRET@example.test/register",label="Credentials"}
            }
        }) + "\n");
        var built = await new AiInputBuilder().BuildAsync(run);
        var input = await File.ReadAllTextAsync(built.Path);
        Assert.DoesNotContain("SECRET", input);
        Assert.DoesNotContain("outside.test", input);
        using var json = JsonDocument.Parse(input);
        var page = Assert.Single(json.RootElement.GetProperty("authenticationPages").EnumerateArray());
        Assert.True(page.GetProperty("hasPasswordField").GetBoolean());
        var links = page.GetProperty("observedLinks").EnumerateArray().ToArray();
        Assert.Equal(4, links.Length);
        Assert.Equal("https://example.test/#/register", links[0].GetProperty("url").GetString());
        Assert.False(links[0].GetProperty("requiresUiLookup").GetBoolean());
        Assert.All(links.Skip(1), link => { Assert.Equal(JsonValueKind.Null, link.GetProperty("url").ValueKind); Assert.True(link.GetProperty("requiresUiLookup").GetBoolean()); });
        var prompt = await File.ReadAllTextAsync(Path.Combine(run, "prompt.md"));
        Assert.Contains("authenticationPages.observedLinks", prompt);
        Assert.Contains("認証をやり直す目的", prompt);
        Assert.Contains("正常系の成功と区別", prompt);
    }

    [Fact]
    public async Task PromptIncludesAnonymousLoginAndPostLoginNavigationForBothProxies()
    {
        using var temp = new TestFolder();
        var project = temp.Project();
        project.AfterAuthenticationUrl = "https://example.test/dashboard";
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        await ExplorationPackageBuilder.ConfigureProxyPromptAsync(run, false);
        var prompt = await File.ReadAllTextAsync(Path.Combine(run, "prompt.md"));
        Assert.Contains("未認証を選択している場合も", prompt);
        Assert.Contains("ロールに関係なくユーザーに認証情報の入力を依頼", prompt);
        Assert.Contains("ユーザー名・パスワードを直接入力", prompt);
        Assert.Contains("再開ボタンはユーザーの操作完了の通知", prompt);
        // Semantic prohibitions move post-login links from the automatic
        // crawler to AI review; do not promise they were already requested.
        Assert.Contains("認証後のリンクも自動で送信済みとは限りません", prompt);
        Assert.Contains("semanticPolicyHandoff", prompt);
        Assert.Contains("認証後にブラウザを再起動したり", prompt);
        Assert.Contains("authentication-navigation.json", prompt);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "engagement.json")));
        Assert.Equal(project.AfterAuthenticationUrl, contract.RootElement.GetProperty("afterAuthenticationUrl").GetString());
        project.AfterAuthenticationUrl = "https://outside.test/dashboard";
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExplorationPackageBuilder().BuildAsync(project));
    }

    [Fact]
    public void PartialAndRepeatedRequestsAreNotLostOrRepeatedButNewLoginFailureIsReported()
    {
        var inbox = new InterventionInbox();
        Assert.Null(inbox.Observe("{\"reason\":"));
        var first = Request("first");
        Assert.NotNull(inbox.Observe(first));
        Assert.Null(inbox.Observe(first));
        Assert.NotNull(inbox.Observe(Request("second", "ログインできませんでした")));
        inbox.Observe(null);
        Assert.NotNull(inbox.Observe(first));
    }

    [Fact]
    public void ResumeIsBoundToRequestAndSelectedAuthenticatedRoleWithoutCredentials()
    {
        using var temp = new TestFolder();
        var pending = Path.Combine(temp.Path, "human-intervention.json");
        File.WriteAllText(pending, Request("first"));
        Assert.Throws<InvalidDataException>(() => AuthenticationFlow.WriteResume(temp.Path, "未認証"));
        Assert.False(File.Exists(Path.Combine(temp.Path, "resume.signal")));
        AuthenticationFlow.WriteResume(temp.Path, "一般ユーザー");
        using var signal = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "resume.signal")));
        Assert.Equal("first", signal.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("一般ユーザー", signal.RootElement.GetProperty("activeRole").GetString());
        Assert.True(signal.RootElement.GetProperty("verifyNavigation").GetBoolean());
        Assert.False(signal.RootElement.TryGetProperty("password", out _));
        File.WriteAllText(pending, Request("second"));
        AuthenticationFlow.WriteResume(temp.Path, "管理者");
        Assert.Contains("second", File.ReadAllText(Path.Combine(temp.Path, "resume.signal")));
    }

    [Theory]
    [InlineData("waiting", "https://example.test/login.php", true, "未確認")]
    [InlineData("authenticated", "https://example.test/login.php", true, "認証画面が残っています")]
    [InlineData("authenticated", "https://outside.test/dashboard", false, "許可Origin外")]
    [InlineData("authenticated", "https://example.test/profile", false, "未到達")]
    [InlineData("authenticated", "https://example.test/dashboard?token=SECRET", false, "認証後の到達先:")]
    public void ArrivalDisplaySeparatesPendingLoginOutOfScopeAndActualLanding(string status, string url, bool login, string expected)
    {
        var project = new EngagementProject { AllowedOrigins = "https://example.test", AfterAuthenticationUrl = "https://example.test/dashboard" };
        var text = AuthenticationFlow.FormatNavigation(JsonSerializer.Serialize(new { status, actualUrl = url, loginFormVisible = login, authenticatedIndicatorVisible = true }), project);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("SECRET", text);
    }

    [Fact]
    public void ButtonConfirmationAndHiddenLoginFormWithoutPositiveIndicatorDoNotConfirmAuthentication()
    {
        var project = new EngagementProject { AllowedOrigins = "https://example.test" };
        Assert.Contains("未確認", AuthenticationFlow.FormatNavigation(
            "{\"status\":\"authenticated\",\"actualUrl\":\"https://example.test/login.php\",\"loginFormVisible\":false}", project));
    }

    [Fact]
    public void TargetPathCaseAndExplicitQueryMustMatchAndDisplayOmitsUrlCredentials()
    {
        var project = new EngagementProject { AllowedOrigins = "https://example.test", AfterAuthenticationUrl = "https://example.test/Dashboard?tab=users" };
        var record = new { status = "authenticated", actualUrl = "https://example.test/dashboard?tab=reports", loginFormVisible = false, authenticatedIndicatorVisible = true };
        Assert.Contains("未到達", AuthenticationFlow.FormatNavigation(JsonSerializer.Serialize(record), project));
        Assert.Equal("https://example.test/login.php", AuthenticationFlow.DisplayUrl("https://user:password@example.test/login.php?token=SECRET"));
    }

    [Fact]
    public void AuthenticationPromptAsksForDirectBrowserInputAndOffersAnAuthenticatedRole()
    {
        var vm = new MainViewModel();
        vm.UpdateRoleOptions();
        var shown = new List<string>();
        vm.ShowAuthenticationPrompt = shown.Add;
        vm.PrepareAuthenticationIntervention(Request("first"));
        Assert.True(vm.AuthenticationWaiting);
        Assert.False(AuthenticationFlow.IsAnonymous(vm.AuthenticationRole));
        Assert.Contains("探索ブラウザにユーザー名・パスワードを直接入力", Assert.Single(shown));
        Assert.Equal("ログイン後のページを確認して再開", vm.ResumeActionText);
    }

    private static string Request(string id, string reason = "認証が必要です") =>
        JsonSerializer.Serialize(new { requestId = id, kind = "authentication", reason, page = "https://example.test/login.php", instruction = "探索ブラウザへ入力してください。" });
}
