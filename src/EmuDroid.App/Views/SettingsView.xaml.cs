using System.Windows;
using System.Windows.Controls;
using EmuDroid.App.ViewModels;

namespace EmuDroid.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is SettingsViewModel vm)
            await vm.LoadAsync();
    }
}
