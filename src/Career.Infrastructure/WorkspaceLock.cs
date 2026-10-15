using System.Diagnostics;

namespace Career.Infrastructure;

public sealed class WorkspaceLock : IDisposable
{
    public const string ProbeArgument = "--probe-workspace-lock";
    private readonly FileStream _handle;
    public WorkspaceLock(string path,bool verifyProcessLock=true)
    {
        _handle=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(!verifyProcessLock)return;
        try
        {
            var executable=Environment.ProcessPath ?? throw new IOException("无法验证工作区锁。");
            var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add(ProbeArgument);start.ArgumentList.Add(path);
            using var probe=Process.Start(start) ?? throw new IOException("无法启动工作区锁检查。");
            if(!probe.WaitForExit(5000)){probe.Kill(true);probe.WaitForExit();throw new IOException("工作区锁检查超时。");}
            if(probe.ExitCode!=73)throw new IOException("这个文件系统无法可靠锁定工作区，请选择本机文件系统或本地卷。");
        }
        catch{_handle.Dispose();throw;}
    }
    public static int Probe(string path)
    {
        try{using var handle=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);return 0;}
        catch(IOException e) when(OperatingSystem.IsWindows()?(e.HResult&0xffff)is 32 or 33:e.HResult==(OperatingSystem.IsMacOS()?35:11)){return 73;}
        catch{return 74;}
    }
    public void Dispose()=>_handle.Dispose();
}
