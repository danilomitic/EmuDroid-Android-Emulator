using System.Windows;
using System.Windows.Input;

namespace EmuDroid.App.Views;

/// <summary>Themed replacement for MessageBox: confirm, prompt and info dialogs.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow(string title, string message, string okText, bool showCancel)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow is { IsVisible: true } w && w != this ? w : null;
        if (Owner is null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = okText;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    public static bool Confirm(string title, string message, string okText, bool danger = false)
    {
        var d = new DialogWindow(title, message, okText, showCancel: true);
        if (danger) d.OkButton.Style = (Style)Application.Current.Resources["DangerButton"];
        return d.ShowDialog() == true;
    }

    public static string? Prompt(string title, string message, string initial, string okText)
    {
        var d = new DialogWindow(title, message, okText, showCancel: true);
        d.Input.Visibility = Visibility.Visible;
        d.Input.Text = initial;
        d.Loaded += (_, _) => { d.Input.Focus(); d.Input.SelectAll(); };
        return d.ShowDialog() == true && !string.IsNullOrWhiteSpace(d.Input.Text) ? d.Input.Text.Trim() : null;
    }

    public static void Info(string title, string message) =>
        new DialogWindow(title, message, "OK", showCancel: false).ShowDialog();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
