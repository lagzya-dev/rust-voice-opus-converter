using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpusConverter.Gui;

/// <summary>
/// Switches between Palette.Light.xaml and Palette.Dark.xaml by replacing the one merged into the app's resources
/// (see App.xaml). Every color in Theme.xaml is looked up with DynamicResource, so this re-themes every open window
/// immediately - no window needs to be recreated or reloaded.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightSource = new("Palette.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkSource = new("Palette.Dark.xaml", UriKind.Relative);

    public static bool IsDark { get; private set; } = true;

    public static void Apply(bool dark)
    {
        IsDark = dark;

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        int index = dictionaries.ToList().FindIndex(d => d.Source == LightSource || d.Source == DarkSource);
        var palette = new ResourceDictionary { Source = dark ? DarkSource : LightSource };

        if (index >= 0)
        {
            dictionaries[index] = palette;
        }
        else
        {
            dictionaries.Insert(0, palette);
        }

        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window, dark);
        }
    }

    /// <summary>Colors the native title bar/border to match (Windows 10 2004+ / 11); silently does nothing on older Windows.</summary>
    public static void ApplyTitleBar(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985) || PresentationSource.FromVisual(window) is null)
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int useDarkMode = dark ? 1 : 0;
        // Attribute 20 (DWMWA_USE_IMMERSIVE_DARK_MODE) on the SDKs this targets; 19 is the pre-20H1 fallback.
        if (DwmSetWindowAttribute(handle, 20, ref useDarkMode, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref useDarkMode, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
