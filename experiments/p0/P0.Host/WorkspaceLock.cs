using System.Diagnostics;

namespace P0.Host;

// FileShare.None is advisory on Unix and some shared filesystems silently accept
// competing locks. Verify this workspace with a separate process before using it.
public sealed class WorkspaceLock : IDisposable
{
    public const string ProbeArgument = "--probe-workspace-lock";
    private const int Contended = 73;
    private readonly FileStream handle;

    public WorkspaceLock(string path)
    {
        handle = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Cannot locate Host for workspace lock validation.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add(ProbeArgument);
            start.ArgumentList.Add(path);
            using var probe = Process.Start(start) ?? throw new IOException("Cannot start workspace lock validation.");
            if (!probe.WaitForExit(5000))
            {
                probe.Kill(entireProcessTree: true);
                probe.WaitForExit();
                throw new IOException("workspace_lock_probe_timed_out");
            }
            if (probe.ExitCode != Contended)
                throw new IOException("workspace_lock_not_enforced: use a local filesystem or Docker local volume with working process locks.");
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static int Probe(string path)
    {
        try
        {
            using var competing = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return 0;
        }
        catch (IOException error) when (OperatingSystem.IsWindows()
            ? (error.HResult & 0xffff) is 32 or 33
            : error.HResult == (OperatingSystem.IsMacOS() ? 35 : 11))
        {
            // EAGAIN/EWOULDBLOCK on Unix, sharing/lock violation on Windows.
            return Contended;
        }
        catch (Exception)
        {
            return 74; // Other errors do not prove exclusive locking works.
        }
    }

    public void Dispose() => handle.Dispose();
}
