using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScopePilot.Domain;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class ForbiddenActionsTests
{
    [Fact]
    public async Task OnlyUntouchedLegacyDefaultsAreUpgradedWithoutWritingTheCaseFile()
    {
        using var temp=new TestFolder();var project=temp.Project();
        foreach(var value in new[]{EngagementProject.LegacyDefaultForbiddenActions,"購入・決済","削除",""})
        {
            project.ForbiddenActions=value;await new ProjectStore().SaveAsync(project);
            var before=await File.ReadAllTextAsync(project.ProjectFilePath);
            var loaded=await new ProjectStore().LoadAsync(project.ProjectFilePath);
            Assert.Equal(value==EngagementProject.LegacyDefaultForbiddenActions?EngagementProject.DefaultForbiddenActions:value,loaded.ForbiddenActions);
            Assert.Equal(before,await File.ReadAllTextAsync(project.ProjectFilePath));
        }
    }

    [Fact]
    public async Task EveryCrawlModeEnforcesExplicitProhibitionsWithoutBlockingAllowedOperations()
    {
        using var temp=new TestFolder();
        var work=Environment.GetEnvironmentVariable("SCOPEPILOT_TEST_ZAP_ARTIFACTS") is {Length:>0} artifacts
            ? Path.Combine(artifacts,"forbidden-actions-"+Guid.NewGuid().ToString("N")) : temp.Path;
        Directory.CreateDirectory(work);
        var info=new ProcessStartInfo(EnvironmentProbe.FindNode()!) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"forbidden-actions-integration.cjs"));
        info.ArgumentList.Add(AppContext.BaseDirectory);info.ArgumentList.Add(work);
        using var process=Process.Start(info)!;
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(180));
        try {await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill(true);throw;}
        Assert.True(process.ExitCode==0,await output+await error);
    }

    [Fact]
    public async Task SettingsPersistLiterallyAndReachBothAiProvidersAndReports()
    {
        using var temp=new TestFolder();var project=temp.Project();
        Assert.Contains("Delete",project.ForbiddenActions.Split('\n'));
        Assert.Contains("購入",project.ForbiddenActions.Split('\n'));Assert.Contains("決済",project.ForbiddenActions.Split('\n'));
        project.ForbiddenActions="Publish\n/api/private\n購入・決済";project.EnableAiInteractions=true;
        await new ProjectStore().SaveAsync(project);project=await new ProjectStore().LoadAsync(project.ProjectFilePath);
        Assert.Equal("Publish\n/api/private\n購入・決済",project.ForbiddenActions);
        foreach(var provider in new[]{DocumentAiProvider.Codex,DocumentAiProvider.Glm})
        {
            project.ExplorationAi.Provider=provider;
            var run=await new ExplorationPackageBuilder().BuildAsync(project);
            using var contract=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run,"engagement.json")));
            Assert.Equal(new[]{"Publish","/api/private","購入・決済"},contract.RootElement.GetProperty("forbiddenActions").EnumerateArray().Select(x=>x.GetString()).ToArray());
            var prompt=await CodexExplorationRunner.BuildPromptAsync(run,CancellationToken.None);
            Assert.Contains("/api/private",prompt);Assert.Contains("禁止",prompt);
        }
        var report=await new FindingReportService().ExportAsync(project);
        using var exported=JsonDocument.Parse(await File.ReadAllTextAsync(report.JsonPath));
        Assert.Equal(new[]{"Publish","/api/private","購入・決済"},exported.RootElement.GetProperty("forbiddenActions").EnumerateArray().Select(x=>x.GetString()).ToArray());
        Assert.Contains("Publish",await File.ReadAllTextAsync(report.HtmlPath));
        project.ForbiddenActions="";var empty=await new ExplorationPackageBuilder().BuildAsync(project);
        using var unrestricted=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(empty,"engagement.json")));
        Assert.Empty(unrestricted.RootElement.GetProperty("forbiddenActions").EnumerateArray());
    }
}
