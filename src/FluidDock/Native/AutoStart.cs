using Microsoft.Win32;

namespace FluidDock.Native;

/// <summary>
/// "Start with Windows", which is one string value under HKCU's Run key.
///
/// Deliberately not the Startup folder and deliberately not a scheduled task. The Run key is the
/// one of the three that the user can see and undo from Task Manager's Startup tab without
/// knowing this program exists - and a launcher that installs itself somewhere the operating
/// system's own uninstall-my-autostart UI cannot reach it is the kind of thing that gets a
/// program uninstalled.
///
/// HKCU rather than HKLM for the same reason the single-instance mutex is session-scoped: the
/// dock belongs to whoever turned it on, and writing to HKLM would need elevation to do something
/// nobody asked for.
///
/// State is read back from the registry every time rather than mirrored into dock.json. The
/// registry is the thing Windows actually obeys, so a copy in the config would be a second
/// opinion - and the moment the user disables the entry from Task Manager, the wrong one.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Where Task Manager records that the user switched an entry off.
    ///
    /// The Run value stays exactly as it was; only this appears alongside it. Ignoring it means a
    /// switch that reads "on" for an entry Windows is no longer honouring.
    /// </summary>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "FluidDock";

    /// <summary>
    /// True only when the entry exists, points at this exe, and has not been vetoed.
    ///
    /// The path comparison is the part worth keeping. An entry left behind by a copy that has
    /// since been moved or deleted launches nothing, and a switch that says "on" for it is
    /// telling the user something that is not true - whereas reading "off" is both honest and
    /// repairable by the obvious gesture, since turning it on rewrites the path.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            try
            {
                using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(ValueName) is not string command) return false;
                if (!SamePath(Unquote(command), ExePath())) return false;

                return !Vetoed();
            }
            catch (Exception)
            {
                // Registry access can fail for reasons that have nothing to do with us - policy,
                // a corrupted hive, a locked-down account. None of them are worth failing to open
                // the settings panel over, and "off" is the safe answer to show.
                return false;
            }
        }

        set
        {
            try
            {
                using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

                if (!value)
                {
                    run.DeleteValue(ValueName, throwOnMissingValue: false);
                    return;
                }

                // Quoted, because the path has a space in it on any machine where the exe lives
                // under Program Files - and an unquoted Run entry is split on spaces, so
                // "C:\Program Files\FluidDock\FluidDock.exe" is read as an attempt to run
                // "C:\Program.exe" with two arguments.
                run.SetValue(ValueName, $"\"{ExePath()}\"", RegistryValueKind.String);

                Unveto();
            }
            catch (Exception)
            {
                // Same reasoning as the getter. The switch will read back "off" on the next open,
                // which is at least an accurate report of what happened.
            }
        }
    }

    /// <summary>
    /// Whether Task Manager has the entry switched off.
    ///
    /// The value is a twelve-byte blob: a state word followed by the FILETIME of the change. Only
    /// the low bit of the first byte matters - 02 and 06 are enabled, 03 and 07 disabled - so the
    /// test is on that bit rather than on any particular one of the four values, which is what
    /// keeps this working if a future build invents a fifth.
    /// </summary>
    private static bool Vetoed()
    {
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) != 0;
    }

    /// <summary>
    /// Clears the veto, by deleting it rather than by writing an "enabled" blob.
    ///
    /// A missing value is what Windows sees for an entry it has never been asked about, and it
    /// honours those. Writing our own approval record instead would mean guessing at a format
    /// that belongs to Task Manager, for no gain over having no opinion at all.
    /// </summary>
    private static void Unveto()
    {
        using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// This executable, as Windows would have to spell it to launch us.
    ///
    /// <see cref="Environment.ProcessPath"/> rather than the entry assembly's location: under
    /// single-file publishing the managed assembly has no path on disk at all, and the one thing
    /// that does is the apphost - which is exactly what a Run entry has to name.
    /// </summary>
    private static string ExePath() =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FluidDock.exe");

    private static string Unquote(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // GetFullPath throws on a stored value that is not a path at all.
            return false;
        }
    }
}
