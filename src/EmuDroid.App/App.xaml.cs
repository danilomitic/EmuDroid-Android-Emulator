using System.Windows;
using System.Windows.Threading;
using EmuDroid.App.Services;
using EmuDroid.App.ViewModels;
using EmuDroid.Core;

namespace EmuDroid.App;

public partial class App : Application
{
    private AppServices? _services;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\EmuDroid.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("EmuDroid is already open.", "EmuDroid");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        if (ScreenshotMode.TryParse(e.Args, out var shot))
        {
            ScreenshotMode.Run(this, shot);
            return;
        }

        _services = new AppServices();
        Log.Info($"EmuDroid {AppServices.Version} starting");
        var window = new MainWindow { DataContext = new MainViewModel(_services) };
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        Views.DialogWindow.Info("Something went wrong", Format.Error(e.Exception) + "\n\nDetails were saved to the log (Settings → Open logs).");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
