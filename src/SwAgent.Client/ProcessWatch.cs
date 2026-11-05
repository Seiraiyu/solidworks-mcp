using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SwAgent.Client;

/// <summary>Tells when a SOLIDWORKS process exits, and how.</summary>
public interface IProcessWatch
{
    /// <summary>
    /// Starts watching process <paramref name="processId"/> now (so its exit code stays readable) and completes when it
    /// exits, with its exit code when Windows reports one; at once when it is already gone.
    /// </summary>
    Task<int?> WatchAsync(int processId, CancellationToken cancellationToken);
}

public sealed class SystemProcessWatch : IProcessWatch
{
    public static readonly SystemProcessWatch Instance = new SystemProcessWatch();

    public async Task<int?> WatchAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            // Opened now: the handle keeps the exit code readable after the process is gone.
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;
        }
        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return process.ExitCode;
            }
            catch (Exception e) when (e is InvalidOperationException || e is Win32Exception || e is NotSupportedException)
            {
                return null;
            }
        }
    }
}
