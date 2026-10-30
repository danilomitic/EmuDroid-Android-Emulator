using System.Windows;
using System.Windows.Input;
using EmuDroid.App.ViewModels;

namespace EmuDroid.App.Views;

public partial class InstanceEditorWindow : Window
{
    private InstanceEditorWindow(InstanceEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Owner = Application.Current.MainWindow;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static bool? Show(InstanceEditorViewModel vm) => new InstanceEditorWindow(vm).ShowDialog();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var vm = (InstanceEditorViewModel)DataContext;
        if (string.IsNullOrWhiteSpace(vm.Name) || vm.SelectedVersion is null)
        {
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
