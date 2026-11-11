using System.Diagnostics;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class SpaAuthenticationTests
{
    [Fact]
    public async Task VisibleStandaloneCredentialsAndLogoutControlsAreDetectedWithoutReadingValues()
    {
        var info = new ProcessStartInfo(EnvironmentProbe.FindNode()!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "ui-state-regression.cjs"));
        info.ArgumentList.Add(AppContext.BaseDirectory);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await output + "\n" + await error);
    }
}
