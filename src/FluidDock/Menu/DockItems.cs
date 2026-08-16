using FluidDock.Native;

namespace FluidDock.Menu;

/// <summary>
/// Everything that can be done to the dock's list of entries, in one place.
///
/// The rows below call these; nothing else does. That is what keeps "add an app" one line in
/// <see cref="MenuDefinition"/> rather than a file dialog, a path-to-label rule, a save and a
/// panel rebuild spread across three row types that each got it slightly different.
///
/// Two kinds of change, and the difference matters:
///
///   <see cref="Changed"/>           a value moved. Save, and the dock's watcher rebuilds it.
///   <see cref="StructureChanged"/>  the list got longer or shorter, so the panel is now the
///                                   wrong height and has to be built again.
///
/// Reordering raises only the first, which is why a drag can settle with a spring instead of the
/// list blinking out and back.
///
/// Every mutation except the reorder is deferred, and that is not a detail. These are called from
/// a row's release handler, which the panel is in the middle of - so an add that opened a dialog
/// would do it while the panel still held the mouse capture, and a remove that rebuilt the panel
/// would free the tree out from under the call that asked for it. Deferring is enforced here
/// rather than at the call sites so that a row written later cannot forget.
/// </summary>
internal sealed class DockItems
{
    private readonly SettingsStore _store;
    private readonly Func<IntPtr> _owner;
    private readonly Action<Action> _defer;

    public DockItems(SettingsStore store, Func<IntPtr> owner, Action<Action> defer)
    {
        _store = store;
        _owner = owner;
        _defer = defer;
    }

    public List<DockItemConfig> All => _store.Config.Items;

    public event Action? Changed;

    public event Action? StructureChanged;

    /// <summary>Programs, shortcuts, documents - whatever the shell will open.</summary>
    public void AddPrograms() => _defer(() => Add(FileDialog.PickPrograms(_owner())));

    public void AddFolder() => _defer(() =>
    {
        string? folder = FileDialog.PickFolder(_owner());
        if (folder is not null) Add([folder]);
    });

    public void Remove(DockItemConfig item) => _defer(() => RemoveNow(item));

    /// <summary>Picks a replacement image. Cancelling leaves whatever was there.</summary>
    public void ChooseIcon(DockItemConfig item) => _defer(() => ChooseIconNow(item));

    /// <summary>Goes back to whatever the shell has for the entry's own path.</summary>
    public void ResetIcon(DockItemConfig item) => _defer(() => ResetIconNow(item));

    private void Add(string[] paths)
    {
        if (paths.Length == 0) return;

        foreach (string path in paths)
            All.Add(new DockItemConfig { Path = path, Label = LabelFor(path) });

        Changed?.Invoke();
        StructureChanged?.Invoke();
    }

    private void RemoveNow(DockItemConfig item)
    {
        if (!All.Remove(item)) return;

        Changed?.Invoke();
        StructureChanged?.Invoke();
    }

    /// <summary>
    /// Moves one entry, without disturbing the panel.
    ///
    /// The list is reordered in place and saved; the rows themselves are already sitting where
    /// the drag left them, so nothing is rebuilt and nothing jumps.
    /// </summary>
    public void Move(int from, int to)
    {
        if (from == to || from < 0 || from >= All.Count || to < 0 || to >= All.Count) return;

        DockItemConfig item = All[from];
        All.RemoveAt(from);
        All.Insert(to, item);

        Changed?.Invoke();
    }

    private void ChooseIconNow(DockItemConfig item)
    {
        string? icon = FileDialog.PickIcon(_owner());
        if (icon is null) return;

        item.Icon = Relative(icon);

        Changed?.Invoke();

        // The thumbnail is a GPU surface baked when the row was built, so a new picture means a
        // new row. Cheap, and it is the one moment the user is looking straight at that thumbnail
        // waiting to see whether it worked.
        StructureChanged?.Invoke();
    }

    private void ResetIconNow(DockItemConfig item)
    {
        if (item.Icon is null) return;

        item.Icon = null;
        Changed?.Invoke();
        StructureChanged?.Invoke();
    }

    /// <summary>
    /// Stores an icon that lives inside the app folder as a relative path, so a dock carried to
    /// another machine on a USB stick still finds its own artwork. Anything else stays absolute,
    /// because a relative path out of the app folder would be worse than useless.
    /// </summary>
    private static string Relative(string path)
    {
        try
        {
            string root = System.IO.Path.GetFullPath(AppContext.BaseDirectory);
            string full = System.IO.Path.GetFullPath(path);

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return full;
            return System.IO.Path.GetRelativePath(root, full);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// A first guess at what to call an entry. The file name without its extension is what the
    /// shell shows and what the user recognises; only when there is no name to take - a drive
    /// root - does the path itself have to do.
    /// </summary>
    private static string LabelFor(string path)
    {
        try
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(name)) return name;

            name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch (Exception)
        {
            return path;
        }
    }
}
