using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluidDock;

internal sealed class DockItemConfig
{
    /// <summary>Executable, shortcut, folder or URL. Launched through the shell.</summary>
    public string Path { get; set; } = string.Empty;

    public string? Args { get; set; }
    public string? Label { get; set; }

    /// <summary>PNG to use instead of the extracted shell icon. Relative paths resolve against the app folder.</summary>
    public string? Icon { get; set; }
}

internal sealed class DockMetricsConfig
{
    public float IconSize { get; set; } = 48f;
    public float IconGap { get; set; } = 14f;
    public float MaxScale { get; set; } = 2.0f;
    public float InfluenceCells { get; set; } = 2.5f;
    public float PaddingX { get; set; } = 18f;
    public float PaddingY { get; set; } = 10f;
    public float CornerRadius { get; set; } = 20f;
    public float ScreenMargin { get; set; } = 12f;
    public float BounceHeight { get; set; } = 30f;

    /// <summary>
    /// Off by default: the pill holds a constant width and icons spread inside it.
    /// Turn it on to get the macOS behaviour where the pill widens with the icons.
    /// </summary>
    public bool PillGrowsWithIcons { get; set; }

    public DockMetrics ToMetrics() => new()
    {
        IconSize = IconSize,
        IconGap = IconGap,
        MaxScale = MaxScale,
        InfluenceCells = InfluenceCells,
        PaddingX = PaddingX,
        PaddingY = PaddingY,
        CornerRadius = CornerRadius,
        ScreenMargin = ScreenMargin,
        BounceHeight = BounceHeight,
        PillGrowsWithIcons = PillGrowsWithIcons,
    };
}

internal sealed class DockAppearanceConfig
{
    /// <summary>
    /// Whether to draw the rounded panel behind the icons. Off: the icons float directly on the
    /// desktop and every setting below this one is inert.
    ///
    /// Kept as a switch rather than deleted because it costs one branch, and the pill is a
    /// hundred-odd lines of texture work that would be tedious to reconstruct from scratch.
    /// </summary>
    public bool ShowPill { get; set; }

    /// <summary>
    /// Pill fill, #AARRGGBB. Without a real backdrop blur the alpha has to do more work than
    /// it would on macOS - too low and the dock dissolves into a dark wallpaper.
    /// </summary>
    public string Tint { get; set; } = "#B22A2A2E";

    /// <summary>Strength of the film grain that keeps the pill from reading as flat plastic. 0 disables.</summary>
    public float Noise { get; set; } = 0.05f;

    /// <summary>Opacity of the 1px highlight along the top edge.</summary>
    public float TopHighlight { get; set; } = 0.30f;

    /// <summary>Opacity of the hairline border.</summary>
    public float Border { get; set; } = 0.16f;

    public float ShadowOpacity { get; set; } = 0.55f;
    public float ShadowBlur { get; set; } = 30f;
    public float ShadowOffsetY { get; set; } = 8f;
}

internal enum DockLayer
{
    /// <summary>
    /// Reparented into the desktop. Applications cover it and Win+D leaves it alone, but it
    /// becomes a sibling of the desktop's icon view - and that view has WS_CLIPSIBLINGS, so it
    /// refuses to paint inside our rectangle. A rubber-band selection dragged across the dock
    /// comes out with a hole in it.
    /// </summary>
    Desktop,

    /// <summary>
    /// An ordinary top-level window, not topmost. Applications still cover it, and because DWM
    /// composites it over the desktop rather than sharing the desktop's paint surface, the
    /// desktop draws through it correctly.
    /// </summary>
    Normal,

    /// <summary>Floats above everything.</summary>
    Top,
}

internal sealed class DockConfig
{
    /// <summary>Where the dock sits in the window stack. See <see cref="DockLayer"/>.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DockLayer Layer { get; set; } = DockLayer.Desktop;

    public DockMetricsConfig Metrics { get; set; } = new();
    public DockAppearanceConfig Appearance { get; set; } = new();
    public List<DockItemConfig> Items { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "config", "dock.json");

    public static DockConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            DockConfig seeded = Seed();
            Save(path, seeded);
            return seeded;
        }

        DockConfig? loaded = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(path), Options);
        return loaded ?? Seed();
    }

    public static void Save(string path, DockConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }

    /// <summary>
    /// A first-run config built from whatever is actually installed, so the dock is never
    /// empty and never shows a broken icon on first launch.
    /// </summary>
    private static DockConfig Seed()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        (string Path, string Label)[] candidates =
        [
            (System.IO.Path.Combine(windows, "explorer.exe"), "File Explorer"),
            (System.IO.Path.Combine(programFilesX86, @"Microsoft\Edge\Application\msedge.exe"), "Edge"),
            (System.IO.Path.Combine(programFiles, @"Google\Chrome\Application\chrome.exe"), "Chrome"),
            (System.IO.Path.Combine(localAppData, @"Programs\Microsoft VS Code\Code.exe"), "VS Code"),
            (System.IO.Path.Combine(system32, "notepad.exe"), "Notepad"),
            (System.IO.Path.Combine(system32, "mspaint.exe"), "Paint"),
            (System.IO.Path.Combine(system32, "cmd.exe"), "Command Prompt"),
        ];

        var config = new DockConfig();
        foreach ((string path, string label) in candidates)
        {
            if (File.Exists(path))
                config.Items.Add(new DockItemConfig { Path = path, Label = label });
        }

        return config;
    }
}
