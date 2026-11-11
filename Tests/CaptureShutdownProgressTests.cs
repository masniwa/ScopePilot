using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScopePilot.Services;
using Xunit;

namespace ScopePilot.Tests;

public sealed class CaptureShutdownProgressTests
{
    private static async Task<Process> StartAsync(string file,bool heartbeat,bool finite)
    {
        var info=new ProcessStartInfo(EnvironmentProbe.FindNode()!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add("const fs=require('fs');const t=setInterval(()=>{"+(heartbeat?"fs.writeFileSync("+JsonSerializer.Serialize(file)+",String(Date.now()));":"")+"},50);console.log('ready');"+(finite?"setTimeout(()=>{clearInterval(t);process.exit(0)},1800);":""));
        var process=Process.Start(info)!;
        Assert.Equal("ready",await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        return process;
    }
    private static async Task StopAsync(Process process){if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();process.Dispose();}

    [Fact]
    public async Task ActiveCaptureMayOutlastTheInactivityWindow()
    {
        using var temp=new TestFolder();var file=Path.Combine(temp.Path,"progress.json");var process=await StartAsync(file,true,true);
        try{Assert.True(await FastCrawlerRunner.WaitForCaptureExitAsync(process,file,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(8),TimeSpan.FromMilliseconds(20)));Assert.Equal(0,process.ExitCode);}
        finally{await StopAsync(process);}
    }
    [Fact]
    public async Task StalledCaptureStillHasABoundedWait()
    {
        using var temp=new TestFolder();var file=Path.Combine(temp.Path,"progress.json");var process=await StartAsync(file,false,false);
        try{Assert.False(await FastCrawlerRunner.WaitForCaptureExitAsync(process,file,TimeSpan.FromMilliseconds(250),TimeSpan.FromSeconds(3),TimeSpan.FromMilliseconds(20)));Assert.False(process.HasExited);}
        finally{await StopAsync(process);}
    }
    [Fact]
    public async Task RepeatedHeartbeatsDoNotBypassTheAbsoluteLimit()
    {
        using var temp=new TestFolder();var file=Path.Combine(temp.Path,"progress.json");var process=await StartAsync(file,true,false);
        try{Assert.False(await FastCrawlerRunner.WaitForCaptureExitAsync(process,file,TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(350),TimeSpan.FromMilliseconds(20)));Assert.False(process.HasExited);}
        finally{await StopAsync(process);}
    }
}
