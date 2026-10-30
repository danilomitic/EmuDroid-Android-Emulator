using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmuDroid.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar(this);
    }

    /// <summary>Asks Windows 10/11 to draw a dark title bar to match the app.</summary>
    public static void UseDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int on = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ and Windows 11)
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
