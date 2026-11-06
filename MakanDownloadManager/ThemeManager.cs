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
    public static string Current { get; private set; } = "platinum-blue";
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
        var dark = ThemePalette.IsDarkTheme(resolved);
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
        ApplyGlass(resources, p, dark, resolved);
        foreach (Window window in Application.Current.Windows) ApplyToWindow(window);
    }

    static Color ColorOf(string hex) { var (a, r, g, b) = ThemePalette.ParseArgb(hex); return Color.FromArgb(a, r, g, b); }
    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>
    /// The "frosted glass" look: panels and cards become translucent over a soft aurora background (blurred colour glows from the
    /// theme's own accents) and get a bright glass edge. Done for every theme from the palette, so all themes stay consistent.
    /// </summary>
    static void ApplyGlass(ResourceDictionary resources, IReadOnlyDictionary<string, string> p, bool dark, string theme)
    {
        try
        {
            void Tint(string key, byte alpha) { var brush = new SolidColorBrush(WithAlpha(ColorOf(p[key]), alpha)); brush.Freeze(); resources[key] = brush; }
            if (theme != "crystal-noir")   // crystal-noir already uses the real Windows acrylic backdrop
            {
                Tint("Surface", dark ? (byte)0xB4 : (byte)0x9C);
                Tint("Panel", dark ? (byte)0x9A : (byte)0x74);
                Tint("Panel2", dark ? (byte)0x8C : (byte)0x66);
                Tint("HeaderBg", dark ? (byte)0x8A : (byte)0x6A);
                Tint("Track", dark ? (byte)0xA0 : (byte)0xA0);
                Tint("Input", dark ? (byte)0xB8 : (byte)0xD6);
            }
            Tint("Hover", dark ? (byte)0xB0 : (byte)0xC0);
            Tint("Selected", dark ? (byte)0xC0 : (byte)0xD0);

            var bg = ColorOf(p["Bg"]); var accent = ColorOf(p["Accent"]); var teal = ColorOf(p["IconTeal"]); var purple = ColorOf(p["IconPurple"]);
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(bg), null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
            void Glow(Color c, byte alpha, double cx, double cy, double radius)
            {
                var g = new RadialGradientBrush { Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = radius, RadiusY = radius };
                g.GradientStops.Add(new GradientStop(WithAlpha(c, alpha), 0));
                g.GradientStops.Add(new GradientStop(WithAlpha(c, 0), 1));
                group.Children.Add(new GeometryDrawing(g, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
            }
            if (theme == "crystal-noir") { var t = new SolidColorBrush(WithAlpha(bg, 0x30)); t.Freeze(); resources["Aurora"] = t; }
            else
            {
                {
                    try
                    {
                        var file = theme switch { "mint-glass" => "bg-mint-glass", "champagne-minimal" => "bg-sunrise-sand", "sapphire-noir" => "bg-midnight-blue", "graphite-copper" => "bg-graphite-cyan", "royal-amethyst" => "bg-aurora-violet", _ => "bg-glass-light" };
                        var img = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/Assets/{file}.png", UriKind.Absolute));
                        img.Freeze();
                        group.Children.Add(new ImageDrawing(img, new Rect(0, 0, 100, 100)));
                    }
                    catch (Exception) { /* the plain colour background stays */ }
                }
                if (theme == "platinum-blue")
                {
                    // Arctic Blue is the main theme: its look stays exactly as it was approved
                    Glow(teal, (byte)0x30, 0.06, 0.0, 0.75);
                    Glow(purple, (byte)0x28, 0.98, 1.0, 0.80);
                    Glow(accent, (byte)0x28, 0.95, 0.0, 0.55);
                    Glow(teal, (byte)0x28, 0.0, 1.0, 0.55);
                }
                else
                {
                    // the other themes glow only in their OWN accent (teal + purple glows on every theme made them muddy)
                    var accentDark = ColorOf(p["AccentDark"]);
                    Glow(accent, dark ? (byte)0x34 : (byte)0x1C, 0.04, 0.0, 0.70);
                    Glow(accentDark, dark ? (byte)0x30 : (byte)0x16, 0.98, 1.0, 0.75);
                }
                group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 100, 100));   // nothing may stick out and change the stretch
                group.Freeze();
                var brush = new DrawingBrush(group) { Stretch = Stretch.Fill }; brush.Freeze();
                resources["Aurora"] = brush;
            }

            var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            if (dark)
            {
                edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 255, 255, 255), 0));
                edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 255, 255, 255), 0.45));
                edge.GradientStops.Add(new GradientStop(WithAlpha(teal, 0x50), 1));
            }
            else
            {
                edge.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 255, 255, 255), 0));
                edge.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 255, 255, 255), 0.35));
                edge.GradientStops.Add(new GradientStop(WithAlpha(ColorOf(p["Border"]), 0x80), 0.7));
                edge.GradientStops.Add(new GradientStop(WithAlpha(ColorOf(p["Border"]), 0xC0), 1));
            }
            edge.Freeze();
            resources["GlassEdge"] = edge;
            var track = new SolidColorBrush(dark ? Color.FromArgb(0x30, 255, 255, 255) : Color.FromArgb(0x30, 0x30, 0x58, 0x90)); track.Freeze();
            resources["BarTrack"] = track;
        }
        catch (Exception) { /* cosmetic only: the plain theme colours stay in place */ }
    }

    static void Override(ResourceDictionary resources, object key, string hex) => resources[key] = Brush(hex);

    static SolidColorBrush Brush(string hex)
    {
        var (a, r, g, b) = ThemePalette.ParseArgb(hex);
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
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
            // Windows 11 acrylic for Crystal Noir. Other themes explicitly restore the normal backdrop.
            var backdrop = Current == "crystal-noir" ? 3 : 0; // DWMSBT_TRANSIENTWINDOW / DWMSBT_AUTO
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        }
        catch (Exception) { /* cosmetic only */ }
    }
}
