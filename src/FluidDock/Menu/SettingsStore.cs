namespace FluidDock.Menu;

/// <summary>
/// The panel's copy of the config, and the only thing that writes it back.
///
/// The panel does not talk to the dock. It edits this, saves, and the dock's existing file
/// watcher notices and rebuilds - the same path a hand-edited config already takes. That is
/// worth more than a direct call would be: there is one way for settings to reach the dock, so a
/// setting that works when typed into the file works when moved by a slider, and neither can
/// quietly grow a code path the other does not have.
///
/// Held behind a property rather than handed out, because <see cref="Reload"/> replaces the
/// whole object. The rows read through <c>store.Config.Something</c> on every access, so they
/// see the new one; a row that had captured the old instance would silently go on editing a
/// config nothing was going to save.
/// </summary>
internal sealed class SettingsStore
{
    private readonly string _path;

    /// <summary>The file as it was when <see cref="Config"/> was built from it. See Reload.</summary>
    private string _asRead = string.Empty;

    public SettingsStore(string path)
    {
        _path = path;
        Config = DockConfig.Load(path);
        _asRead = Text();
    }

    public DockConfig Config { get; private set; }

    /// <summary>
    /// Re-reads the file, and says whether that produced a new <see cref="Config"/>.
    ///
    /// The comparison is not an optimisation. Rows that edit a single value read through
    /// <c>store.Config.X</c> and do not care that the object changed - but the dock's item list is
    /// edited by rows that hold a <see cref="DockItemConfig"/> each, and those references point
    /// into the list that was loaded when the panel was built. Replacing the config
    /// unconditionally would leave every one of them editing a list nothing was going to save.
    ///
    /// So the file is only re-parsed when it actually differs, and a caller that is told it was
    /// gets to rebuild whatever was holding the old objects.
    /// </summary>
    public bool Reload()
    {
        try
        {
            string text = Text();
            if (text == _asRead) return false;

            Config = DockConfig.Load(_path);
            _asRead = text;
            return true;
        }
        catch (Exception)
        {
            // A malformed file is the user's business, not a reason to fail to open the panel.
            // They keep whatever was loaded last, which is at least valid.
            return false;
        }
    }

    public void Save()
    {
        try
        {
            DockConfig.Save(_path, Config);

            // Our own write is not an external change. Without this the next open would see the
            // file differing from what was last read and rebuild the panel for no reason.
            _asRead = Text();
        }
        catch (Exception)
        {
            // Losing one write is survivable - the next change will try again - and there is
            // nowhere to report it from inside a window procedure that would not be worse.
        }
    }

    private string Text()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public string Path => _path;
}
