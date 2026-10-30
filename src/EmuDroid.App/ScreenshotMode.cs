using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmuDroid.App.Services;
using EmuDroid.App.ViewModels;
using EmuDroid.Core.Avd;

namespace EmuDroid.App;

/// <summary>
/// Developer tool: renders a screen to PNG and exits. Uses a throw-away data folder with sample instances,
/// so it never touches the user's real EmuDroid data. Used for README screenshots and UI checks.
///   EmuDroid.App.exe --screenshot out.png --view home|settings|wizard [--step SystemCheck]
/// </summary>
public static class ScreenshotMode
{
    public sealed record Options(string Output, string View, string? Step);

    public static bool TryParse(string[] args, out Options options)
    {
        options = new Options("", "home", null);
        int i = Array.IndexOf(args, "--screenshot");
        if (i < 0 || i + 1 >= args.Length) return false;
        string Get(string name, string fallback)
        {
            int j = Array.IndexOf(args, name);
            return j >= 0 && j + 1 < args.Length ? args[j + 1] : fallback;
        }
        options = new Options(Path.GetFullPath(args[i + 1]), Get("--view", "home"), Get("--step", "") is { Length: > 0 } s ? s : null);
        return true;
    }

    public static async void Run(Application app, Options o)
    {
        string root = Path.Combine(Path.GetTempPath(), "emudroid-screenshot");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var services = new AppServices(root);
        services.Settings.FirstRunCompleted = o.View != "wizard";

        if (o.View == "home")
        {
            services.Avds.Create("Main phone", 34, "system-images;android-34;google_apis_playstore;x86_64");
            services.Avds.Create("Games", 35, "system-images;android-35;google_apis_playstore;x86_64",
                c => c with { RamMb = 6144, CpuCores = 6, Width = 1920, Height = 1080, Dpi = 280 });
            services.Avds.Create("Work", 33, "system-images;android-33;google_apis_playstore;x86_64",
                c => c with { RamMb = 3072, CpuCores = 2 });
        }

        var main = new MainViewModel(services);
        if (o.View == "settings") main.Page = Page.Settings;
        if (o.View == "home" && main.Home is { } home)
        {
            // Show each card state once.
            home.Instances[0].State = InstanceState.Running;
            home.Instances[1].State = InstanceState.Booting;
        }

        var window = new MainWindow { DataContext = main, Width = 1180, Height = 760, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        app.MainWindow = window;
        window.Show();

        if (main.Current is WizardViewModel wizard && o.Step is not null && Enum.TryParse<WizardStep>(o.Step, out var target))
        {
            while (wizard.Step < target && wizard.Step != WizardStep.Download)
            {
                if (wizard.Step == WizardStep.License) wizard.LicenseAccepted = true;
                if (!wizard.NextCommand.CanExecute(null)) break;
                await wizard.NextCommand.ExecuteAsync(null);
            }
            if (target == WizardStep.Done) wizard.Step = WizardStep.Done;
        }

        await Task.Delay(TimeSpan.FromSeconds(o.View == "settings" ? 8 : 2));
        Save(window, o.Output);
        app.Shutdown();
    }

    private static void Save(Window window, string file)
    {
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle((Brush)Application.Current.Resources["InkBrush"], null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bmp.Render(background);
        bmp.Render(content); // 1:1, no stretching
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
