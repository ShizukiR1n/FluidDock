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

    public required Action OpenConfig { get; init; }

    public required Action Quit { get; init; }
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
    /// Shown under the title. Bumped by hand alongside the release tag - there is one string and
    /// this is it, so a version on screen that disagrees with the tag is a typo rather than a
    /// build-configuration mystery.
    /// </summary>
    public const string Version = "Beta Ver A1.5";

    /// <summary>Layer names, in the order <see cref="DockLayer"/> declares them.</summary>
    private static readonly string[] LayerNames = ["桌面", "普通", "置顶"];

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

                new MenuSection("外观")
                {
                    Rows =
                    {
                        new ToggleRow("显示 Dock",
                            context.DockVisible,
                            context.SetDockVisible),
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
