using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using MakanDownloadManager.Services;

namespace MakanDownloadManager;

/// <summary>
/// Light and dark "blue grey" themes. Every colour of the application is a brush in the application resources
/// (App.xaml); switching the theme swaps those brushes, and because all windows use DynamicResource they repaint at once.
/// </summary>
public static class ThemeManager
{
    public static bool IsDark { get; private set; }
    /// <summary>The theme actually in effect right now - what "auto" and Windows' own
    /// setting resolved to, not necessarily what's stored in Settings.Theme (which can be "auto").</summary>
    public static string Current { get; private set; } = "light";
    /// <summary>False when Themes/ControlStyles.xaml could not be loaded; the standard Windows controls are used then.</summary>
    public static bool ControlStylesLoaded { get; private set; }
    public static string? ControlStylesError { get; private set; }

    /// <summary>Loads the flat control styles once (each template is instantiated as a test; a broken file is dropped).</summary>
    public static void Initialize()
    {
        var app = Application.Current;
        ResourceDictionary? dictionary = null;
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Themes/ControlStyles.xaml", UriKind.Absolute));
            using var stream = info.Stream;
            dictionary = (ResourceDictionary)XamlReader.Load(stream);
            app.Resources.MergedDictionaries.Add(dictionary);
            SelfTest();
            ControlStylesLoaded = true;
        }
        catch (Exception ex)
        {
            ControlStylesLoaded = false;
            ControlStylesError = ex.Message;
            if (dictionary != null) app.Resources.MergedDictionaries.Remove(dictionary);
            try { new DiagnosticsService().Error("Themes/ControlStyles.xaml was not used; standard controls are shown instead.", ex); } catch (Exception) { }
        }
    }

    /// <summary>Creates one of every styled control and applies its template, so a mistake in the styles shows up here and not in a user's window.</summary>
    static void SelfTest()
    {
        static void Prepare(FrameworkElement element) { element.ApplyTemplate(); element.Measure(new Size(300, 200)); element.Arrange(new Rect(0, 0, 300, 200)); element.UpdateLayout(); }
        var combo = new System.Windows.Controls.ComboBox(); combo.Items.Add("one"); combo.SelectedIndex = 0; Prepare(combo);
        var item = new System.Windows.Controls.ComboBoxItem { Content = "x" }; Prepare(item);
        var check = new System.Windows.Controls.CheckBox { Content = "x", IsChecked = true }; Prepare(check);
        var radio = new System.Windows.Controls.RadioButton { Content = "x", IsChecked = true }; Prepare(radio);
        var group = new System.Windows.Controls.GroupBox { Header = "x", Content = new System.Windows.Controls.TextBlock { Text = "y" } }; Prepare(group);
        var tabs = new System.Windows.Controls.TabControl(); tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "a", Content = "b" }); tabs.Items.Add(new System.Windows.Controls.TabItem { Header = "c", Content = "d" }); Prepare(tabs);
        foreach (var orientation in new[] { System.Windows.Controls.Orientation.Vertical, System.Windows.Controls.Orientation.Horizontal })
            Prepare(new System.Windows.Controls.Primitives.ScrollBar { Orientation = orientation, Minimum = 0, Maximum = 100, ViewportSize = 20 });
        var menu = new System.Windows.Controls.Menu();
        var top = new System.Windows.Controls.MenuItem { Header = "File" };
        var sub = new System.Windows.Controls.MenuItem { Header = "More" }; sub.Items.Add(new System.Windows.Controls.MenuItem { Header = "Deep", IsCheckable = true, IsChecked = true, InputGestureText = "Ctrl+D" });
        top.Items.Add(new System.Windows.Controls.MenuItem { Header = "Open", InputGestureText = "Ctrl+O" }); top.Items.Add(new System.Windows.Controls.Separator()); top.Items.Add(sub);
        menu.Items.Add(top); menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Plain" });
        Prepare(menu);
        foreach (var m in new[] { top, sub }) Prepare(m);
        var context = new System.Windows.Controls.ContextMenu(); context.Items.Add(new System.Windows.Controls.MenuItem { Header = "x" }); Prepare(context);
        var tip = new System.Windows.Controls.ToolTip { Content = "x" }; Prepare(tip);
        var list = new System.Windows.Controls.ListView(); var grid = new System.Windows.Controls.GridView();
        grid.Columns.Add(new System.Windows.Controls.GridViewColumn { Header = "N.", Width = 40, DisplayMemberBinding = new System.Windows.Data.Binding("Length") });
        list.View = grid; list.Items.Add("row"); Prepare(list);
        var data = new System.Windows.Controls.DataGrid { AutoGenerateColumns = false };
        data.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "A", Binding = new System.Windows.Data.Binding("Length") }); data.Items.Add("row"); Prepare(data);
    }

    // ------------------------------------------------------------------------------------------------ switching

    public static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Applies a saved built-in theme name, or follows Windows when mode is "auto".</summary>
    public static void Apply(string? mode)
    {
        var resolved = ThemePalette.Resolve(mode, WindowsUsesDarkApps());
        var dark = resolved is "makan" or "obsidian" or "nebula" or "lilac" or "dracula" or "uhnohh" or "dark" or "epsilon";
        var resources = Application.Current.Resources;
        var p = ThemePalette.For(resolved);
        foreach (var (name, hex) in p) resources[name] = Brush(hex);

        // The standard controls take their colours from the system colours: give them the current theme's, in every theme
        // (not just dark) - otherwise a native control (e.g. an unstyled dialog) would keep Windows' own blue highlight
        // even under the orange or dark theme.
        Override(resources, SystemColors.HighlightBrushKey, p["Selected"]);
        Override(resources, SystemColors.HighlightTextBrushKey, p["Text"]);
        Override(resources, SystemColors.InactiveSelectionHighlightBrushKey, p["Selected"]);
        Override(resources, SystemColors.InactiveSelectionHighlightTextBrushKey, p["Text"]);
        Override(resources, SystemColors.WindowBrushKey, p["Input"]);
        Override(resources, SystemColors.WindowTextBrushKey, p["Text"]);
        Override(resources, SystemColors.ControlBrushKey, p["Surface"]);
        Override(resources, SystemColors.ControlTextBrushKey, p["Text"]);
        Override(resources, SystemColors.ControlLightBrushKey, p["Panel2"]);
        Override(resources, SystemColors.ControlLightLightBrushKey, p["Surface"]);
        Override(resources, SystemColors.ControlDarkBrushKey, p["InputBorder"]);
        Override(resources, SystemColors.ControlDarkDarkBrushKey, p["Border"]);
        Override(resources, SystemColors.MenuBrushKey, p["Popup"]);
        Override(resources, SystemColors.MenuTextBrushKey, p["Text"]);
        Override(resources, SystemColors.GrayTextBrushKey, p["Disabled"]);

        IsDark = dark;
        Current = resolved;
        foreach (Window window in Application.Current.Windows) ApplyToWindow(window);
    }

    static void Override(ResourceDictionary resources, object key, string hex) => resources[key] = Brush(hex);

    static SolidColorBrush Brush(string hex)
    {
        var (r, g, b) = ThemePalette.Parse(hex);
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------------------------------------ title bar

    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar (Windows 10 1809 and newer) for a window; a no-op where Windows does not support it.</summary>
    public static void ApplyToWindow(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var value = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE (older builds: 19)
                DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
        catch (Exception) { /* cosmetic only */ }
    }
}
