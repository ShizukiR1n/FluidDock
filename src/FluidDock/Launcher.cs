using System.Diagnostics;

namespace FluidDock;

/// <summary>
/// Starts a dock entry and reports when its window shows up, so the bounce knows when to stop.
/// </summary>
internal static class Launcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Launches the item. <paramref name="onWindowReady"/> fires on a background thread once
    /// the process has a window, or when we give up waiting - either way the bounce ends.
    /// </summary>
    public static bool Launch(DockItemConfig item, Action onWindowReady)
    {
        Process? process;

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = item.Path,
                // Shell execution so shortcuts, folders and URLs all work, not just executables.
                UseShellExecute = true,
                WorkingDirectory = SafeWorkingDirectory(item.Path),
            };

            if (!string.IsNullOrWhiteSpace(item.Args))
                info.Arguments = item.Args;

            process = Process.Start(info);
        }
        catch (Exception)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WaitForWindowAsync(process).ConfigureAwait(false);
            }
            finally
            {
                onWindowReady();
                process?.Dispose();
            }
        });

        return true;
    }

    private static async Task WaitForWindowAsync(Process? process)
    {
        // Shell execution does not always hand back the process that ends up owning the window
        // (explorer.exe hands off to the running instance, launchers re-exec, and so on). When
        // there is nothing to watch, fall back to a fixed bounce rather than bouncing forever.
        if (process is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1400)).ConfigureAwait(false);
            return;
        }

        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited) return;

                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero) return;
            }
            catch (Exception)
            {
                return;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    private static string SafeWorkingDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }
}
