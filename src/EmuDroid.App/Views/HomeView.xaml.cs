using System.Windows;
using System.Windows.Controls;
using EmuDroid.App.ViewModels;

namespace EmuDroid.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && Vm is { } vm)
            await vm.InstallApksAsync(files, target: null);
    }

    private async void OnCardDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && Vm is { } vm
            && (sender as FrameworkElement)?.DataContext is InstanceViewModel card)
            await vm.InstallApksAsync(files, card);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.DataContext = button.DataContext;
            menu.IsOpen = true;
        }
    }
}
