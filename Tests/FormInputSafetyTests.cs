using System.Diagnostics;
using System.IO;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class FormInputSafetyTests
{
    [Fact]
    public async Task UrlContextIsDeferredBeforeAnyInputWhileOrdinaryFormsStillSubmit()
    {
        using var temp = new TestFolder();
        var work = Environment.GetEnvironmentVariable("SCOPEPILOT_TEST_ZAP_ARTIFACTS") is { Length: > 0 } artifacts
            ? Path.Combine(artifacts, "form-input-" + Guid.NewGuid().ToString("N")) : temp.Path;
        Directory.CreateDirectory(work);
        var info = new ProcessStartInfo(EnvironmentProbe.FindNode()!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "form-input-integration.cjs"));
        info.ArgumentList.Add(AppContext.BaseDirectory);
        info.ArgumentList.Add(work);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    [Fact]
    public async Task AiMustResolveUrlMeaningAndRecoveryInsteadOfUsingGenericText()
    {
        using var temp = new TestFolder();
        var project = temp.Project(); project.EnableAiInteractions = true;
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        var prompt = await File.ReadAllTextAsync(Path.Combine(run, "prompt.md"));
        Assert.Contains("type=textでも画像URL", prompt);
        Assert.Contains("contextual-url-input", prompt);
        Assert.Contains("既存URLをそのまま再送して安全とは判断しません", prompt);
        Assert.Contains("適切な値や復元方法を確認できなければ", prompt);
    }
}
