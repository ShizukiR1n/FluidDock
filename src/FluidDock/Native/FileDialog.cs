using System.Runtime.InteropServices;

namespace FluidDock.Native;

[Flags]
internal enum FOS : uint
{
    OverwritePrompt = 0x00000002,
    NoChangeDir = 0x00000008,

    /// <summary>Turns the open dialog into a folder picker. The modern replacement for SHBrowseForFolder.</summary>
    PickFolders = 0x00000020,

    /// <summary>Refuse anything that has no path on disk - a printer, say, or Recycle Bin.</summary>
    ForceFileSystem = 0x00000040,

    AllowMultiSelect = 0x00000200,
    PathMustExist = 0x00000800,
    FileMustExist = 0x00001000,
    DontAddToRecent = 0x02000000,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct COMDLG_FILTERSPEC
{
    [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
    [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
}

/// <summary>
/// The vtable of IFileOpenDialog, in order.
///
/// Every slot has to be declared even when it is never called - a COM interface is an array of
/// function pointers and the runtime finds a method by counting, not by name. The ones this app
/// has no use for are declared as bare <c>void Slot()</c>: the position is what matters, and a
/// signature that is never invoked cannot be wrong in a way that shows.
/// </summary>
[ComImport]
[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOpenDialog
{
    // ---- IModalWindow ----
    // PreserveSig, because cancelling is not an error worth an exception: the dialog returns
    // HRESULT_FROM_WIN32(ERROR_CANCELLED) and the caller is expected to read it as "no".
    [PreserveSig] int Show(IntPtr parent);

    // ---- IFileDialog ----
    void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] filters);
    void SetFileTypeIndex(uint index);
    void GetFileTypeIndex(out uint index);
    void Advise();
    void Unadvise();
    void SetOptions(FOS options);
    void GetOptions(out FOS options);
    void SetDefaultFolder();
    void SetFolder();
    void GetFolder();
    void GetCurrentSelection();
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
    void GetFileName();
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
    void GetResult([MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    void AddPlace();
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
    void Close(int result);
    void SetClientGuid(in Guid guid);
    void ClearClientData();
    void SetFilter();

    // ---- IFileOpenDialog ----
    void GetResults([MarshalAs(UnmanagedType.Interface)] out IShellItemArray items);
    void GetSelectedItems([MarshalAs(UnmanagedType.Interface)] out IShellItemArray items);
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    void BindToHandler();
    void GetParent();
    void GetDisplayName(uint sigdn, out IntPtr name);
    void GetAttributes();
    void Compare();
}

[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemArray
{
    void BindToHandler();
    void GetPropertyStore();
    void GetPropertyDescriptionList();
    void GetAttributes();
    void GetCount(out uint count);
    void GetItemAt(uint index, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    void EnumItems();
}

/// <summary>
/// The system's own open dialog, for picking what goes in the dock.
///
/// IFileOpenDialog rather than GetOpenFileName, for one reason that matters: the same interface
/// picks folders. A dock that could take an .exe but needed a different, older and uglier dialog
/// to take a folder would be telling the user about our plumbing.
///
/// Shortcuts are deliberately left dereferenced - selecting a .lnk yields what it points at.
/// Storing the .lnk instead would preserve any arguments baked into it, but the shell draws
/// shortcut icons with the little arrow overlay composited in, and a dock full of arrows is a
/// worse trade than losing arguments a user can type into dock.json.
/// </summary>
internal static class FileDialog
{
    private static readonly Guid CLSID_FileOpenDialog = new("dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7");
    private static readonly Guid IID_IFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");

    /// <summary>The full filesystem path, in the form the shell parses back.</summary>
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    private const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    private const uint CLSCTX_INPROC_SERVER = 1;

    /// <summary>Executables, shortcuts and anything else the shell can launch.</summary>
    public static string[] PickPrograms(IntPtr owner) => Pick(
        owner, "添加到 Dock", "添加", FOS.AllowMultiSelect,
        [
            new COMDLG_FILTERSPEC { pszName = "程序和快捷方式", pszSpec = "*.exe;*.lnk;*.url;*.bat;*.cmd;*.msc;*.appref-ms" },
            new COMDLG_FILTERSPEC { pszName = "所有文件", pszSpec = "*.*" },
        ]);

    /// <summary>One folder, or nothing. An array so that every picker here answers the same way.</summary>
    public static string[] PickFolder(IntPtr owner) =>
        Pick(owner, "添加文件夹到 Dock", "添加", FOS.PickFolders, null);

    /// <summary>
    /// An image to use in place of the extracted icon. Executables are offered too: pointing at
    /// one means "borrow that program's icon", which is the easiest way to fix the handful of
    /// apps whose own icon is the ugly one.
    /// </summary>
    public static string[] PickIcon(IntPtr owner) => Pick(
        owner, "选择图标", "选择", 0,
        [
            new COMDLG_FILTERSPEC { pszName = "图片和图标", pszSpec = "*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.dll" },
            new COMDLG_FILTERSPEC { pszName = "所有文件", pszSpec = "*.*" },
        ]);

    /// <summary>
    /// Runs the dialog and returns whatever was chosen, or an empty array if the user said no.
    ///
    /// Every failure is a cancel. There is nothing useful a dock can do about a shell that will
    /// not open its own file dialog except carry on with the list the user already had.
    ///
    /// Called on an STA thread of its own rather than on the UI thread - see MenuWindow.Dialog for
    /// why - so it blocks for as long as the dialog is up, and nothing here may touch the panel.
    /// </summary>
    private static string[] Pick(IntPtr owner, string title, string okLabel, FOS extra, COMDLG_FILTERSPEC[]? filters)
    {
        object? created = null;

        try
        {
            if (CoCreateInstance(
                    in CLSID_FileOpenDialog, IntPtr.Zero, CLSCTX_INPROC_SERVER,
                    in IID_IFileOpenDialog, out created) != 0)
            {
                return [];
            }

            var dialog = (IFileOpenDialog)created;

            // Read-modify-write rather than assign: the shell puts defaults in here that are not
            // ours to discard.
            dialog.GetOptions(out FOS options);
            dialog.SetOptions(options | extra | FOS.ForceFileSystem | FOS.FileMustExist
                | FOS.PathMustExist | FOS.NoChangeDir | FOS.DontAddToRecent);

            if (filters is not null) dialog.SetFileTypes((uint)filters.Length, filters);
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel(okLabel);

            int shown = dialog.Show(owner);
            if (shown == ERROR_CANCELLED || shown != 0) return [];

            dialog.GetResults(out IShellItemArray results);

            try
            {
                results.GetCount(out uint count);
                var paths = new List<string>((int)count);

                for (uint i = 0; i < count; i++)
                {
                    results.GetItemAt(i, out IShellItem item);
                    try
                    {
                        string? path = PathOf(item);
                        if (path is not null) paths.Add(path);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }

                return [.. paths];
            }
            finally
            {
                Marshal.ReleaseComObject(results);
            }
        }
        catch (Exception)
        {
            return [];
        }
        finally
        {
            if (created is not null) Marshal.ReleaseComObject(created);
        }
    }

    private static string? PathOf(IShellItem item)
    {
        IntPtr buffer = IntPtr.Zero;

        try
        {
            item.GetDisplayName(SIGDN_FILESYSPATH, out buffer);
            return buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            // The shell allocated this with CoTaskMemAlloc and expects it back, whatever happened.
            if (buffer != IntPtr.Zero) CoTaskMemFree(buffer);
        }
    }
}
