using System.Globalization;
using FluidDock.Menu.Rows;

namespace FluidDock.Menu;

/// <summary>
/// Everything the settings panel is allowed to reach outside itself.
///
/// One object rather than a growing list of parameters, so that giving the panel a new capability
/// is a property here and a line in <see cref="MenuDefinition.Build"/> - not a change to the
/// signature that every caller and every test has to be dragged through.
/// </summary>
internal sealed class MenuContext
{
    public required SettingsStore Store { get; init; }

    /// <summary>The dock's item list, and everything that can be done to it.</summary>
    public required DockItems Items { get; init; }

    /// <summary>Which GPU the icon rasteriser landed on. Diagnostic; see SurfaceFactory.</summary>
    public required Func<string> Adapter { get; init; }

    /// <summary>
    /// Whether the dock is on screen. Live state rather than a saved setting, deliberately: this
    /// is the same switch as the tray menu's, and the two would disagree the moment one of them
    /// remembered something the other did not.
    /// </summary>
    public required Func<bool> DockVisible { get; init; }

    public required Action<bool> SetDockVisible { get; init; }

    /// <summary>
    /// Whether Windows launches the dock at sign-in. Live state again, and for a stronger reason
    /// than the switch above: this one lives in the registry, where Task Manager can turn it off
    /// behind our back.
    /// </summary>
    public required Func<bool> AutoStart { get; init; }

    public required Action<bool> SetAutoStart { get; init; }

    /// <summary>
    /// Changes what the panel is made of, which means building it again. The one setting here
    /// that the panel cannot apply by writing a number and letting something else notice.
    /// </summary>
    public required Action<PanelTheme> SetTheme { get; init; }

    public required Action OpenConfig { get; init; }

    public required Action Quit { get; init; }

    /// <summary>The in-app update. Owns its own state; the row only reads it. See <see cref="Updater"/>.</summary>
    public required Updater Update { get; init; }
}

/// <summary>
/// What the settings panel contains.
///
/// **This is the file to edit to add a setting.** One line in one list, and it appears - laid
/// out, hit-tested, animated, saved and applied, with nothing else touched. Everything under
/// Menu/ exists so that this stays true as the panel grows.
///
/// The row types cover most of what a setting can be:
///
///   ToggleRow    on or off
///   SliderRow    a number in a range
///   SegmentRow   one of a few named choices
///   ButtonRow    an action, optionally destructive, optionally closing the panel
///   LabelRow     something to read
///   DockItemRow  one entry in the dock, with its icon and its own actions
///
/// Another is a new file in Menu/Rows implementing <see cref="MenuRow"/>; nothing here or in the
/// panel needs to know it exists.
/// </summary>
internal static class MenuDefinition
{
    /// <summary>
    /// Shown under the title. Read from the assembly, which gets it from <c>&lt;Version&gt;</c> in
    /// the project file - the one place the number lives. It has to be a real number now rather
    /// than a hand-typed string, because the updater compares it against the release tag, and a
    /// build that called itself 0.5 while tagged 0.6 would offer to update to itself forever.
    /// </summary>
    public static readonly string Version = $"正式版 {Updater.Label(Updater.Current)}";

    /// <summary>Layer names, in the order <see cref="DockLayer"/> declares them.</summary>
    private static readonly string[] LayerNames = ["桌面", "普通", "置顶"];

    /// <summary>Theme names, in the order <see cref="PanelTheme"/> declares them.</summary>
    private static readonly string[] ThemeNames = ["深色", "液态玻璃"];

    public static MenuPage Build(MenuContext context)
    {
        DockMetricsConfig Metrics() => context.Store.Config.Metrics;

        return new MenuPage("FluidDock", Version)
        {
            Sections =
            {
                // First, and by a distance the most used. Everything below it is a number that
                // gets set once; this is the list the user actually came here to edit.
                Apps(context),
                AddButtons(context),

                // Both of these are switches on the program rather than on how it looks, and both
                // read their state from somewhere outside dock.json - which is the thing they
                // have in common and the reason they are not in 外观 with the sliders.
                new MenuSection("常规")
                {
                    Rows =
                    {
                        new ToggleRow("显示 Dock",
                            context.DockVisible,
                            context.SetDockVisible),
                        new ToggleRow("开机自动启动",
                            context.AutoStart,
                            context.SetAutoStart),
                    },
                },

                new MenuSection("外观")
                {
                    Rows =
                    {
                        // Rebuilds the panel rather than merely saving, because what a row is
                        // made of is decided when it is built. Deferred inside Restyle for the
                        // usual reason: this arrives from a pointer event, and the panel it would
                        // destroy is the one dispatching it.
                        new SegmentRow("主题", ThemeNames,
                            () => (int)context.Store.Config.Theme,
                            value => context.SetTheme((PanelTheme)value)),
                        new SliderRow("图标大小", 24f, 96f, 2f,
                            () => Metrics().IconSize,
                            value => Metrics().IconSize = value,
                            value => $"{value:0} px"),
                        new SliderRow("图标间距", 0f, 40f, 1f,
                            () => Metrics().IconGap,
                            value => Metrics().IconGap = value,
                            value => $"{value:0} px"),
                    },
                },

                new MenuSection("放大")
                {
                    Rows =
                    {
                        new SliderRow("放大倍数", 1f, 3f, 0.05f,
                            () => Metrics().MaxScale,
                            value => Metrics().MaxScale = value,
                            value => value.ToString("0.00×", CultureInfo.InvariantCulture)),
                        new SliderRow("影响范围", 0.5f, 5f, 0.25f,
                            () => Metrics().InfluenceCells,
                            value => Metrics().InfluenceCells = value,
                            value => $"{value.ToString("0.##", CultureInfo.InvariantCulture)} 格"),
                        new SliderRow("弹跳高度", 0f, 60f, 2f,
                            () => Metrics().BounceHeight,
                            value => Metrics().BounceHeight = value,
                            value => $"{value:0} px"),
                    },
                },

                new MenuSection("位置")
                {
                    Rows =
                    {
                        new SegmentRow("层级", LayerNames,
                            () => (int)context.Store.Config.Layer,
                            value => context.Store.Config.Layer = (DockLayer)value),
                        new SliderRow("屏幕边距", 0f, 60f, 2f,
                            () => Metrics().ScreenMargin,
                            value => Metrics().ScreenMargin = value,
                            value => $"{value:0} px"),
                    },
                },

                // One row that walks itself from "check" to "update" to "restarting", so a fix
                // is a click here rather than a download and a rebuilt icon list. The version
                // under the panel's title is what it compares against.
                Updates(context),

                new MenuSection("关于")
                {
                    Rows =
                    {
                        new LabelRow("渲染显卡", context.Adapter),
                        new ButtonRow("打开配置文件", context.OpenConfig, closesPanel: true),
                        new ButtonRow("退出 FluidDock", context.Quit, danger: true, closesPanel: true),
                    },
                },
            },
        };
    }

    /// <summary>
    /// The dock's contents, in dock order, draggable into a different one.
    ///
    /// <see cref="MenuSection.Reorder"/> is the whole of it: the panel takes care of the gesture,
    /// the lift, the neighbours moving aside and the settle, and hands back two indices into this
    /// section's rows - which are the same two indices into the config's item list, because the
    /// rows were built from it in order.
    /// </summary>
    private static MenuSection Apps(MenuContext context)
    {
        var section = new MenuSection("应用");

        foreach (DockItemConfig item in context.Items.All)
            section.Rows.Add(new DockItemRow(item, context.Items));

        if (section.Rows.Count == 0)
        {
            // An empty card would be a rounded rectangle of zero height - invisible, and
            // indistinguishable from the panel having failed to draw the list.
            section.Rows.Add(new LabelRow("Dock 是空的", () => "用下面的按钮添加"));
            return section;
        }

        section.Reorder = context.Items.Move;
        return section;
    }

    /// <summary>
    /// The update row, and under it - only once a check has found a newer version - what that
    /// version says it changed. The notes are a row of their own rather than part of the update
    /// row because they change the panel's height, and height is settled when the panel is
    /// built; the updater raises LayoutChanged and the panel is built again, through here.
    /// </summary>
    private static MenuSection Updates(MenuContext context)
    {
        var section = new MenuSection("更新");
        section.Rows.Add(new UpdateRow(context.Update));

        if (context.Update.Notes is { } notes)
            section.Rows.Add(new NotesRow(context.Update.NotesHeading, notes));

        return section;
    }

    /// <summary>
    /// Two ways in, because the shell's open dialog picks files or folders but not both, and one
    /// button that opened the wrong one half the time would be worse than two that say which.
    /// </summary>
    private static MenuSection AddButtons(MenuContext context) => new()
    {
        Rows =
        {
            new ButtonRow("添加应用或文件…", context.Items.AddPrograms),
            new ButtonRow("添加文件夹…", context.Items.AddFolder),
        },
    };
}
