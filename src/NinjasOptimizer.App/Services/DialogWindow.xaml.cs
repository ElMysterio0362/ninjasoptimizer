using System.Windows;
using System.Windows.Media;

namespace NinjasOptimizer.App.Services;

public enum DialogKind { Info, Confirm, Danger, Success }

public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, IReadOnlyList<string>? details, DialogKind kind, string confirmText, string? cancelText)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (cancelText is null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancelText;

        if (details is { Count: > 0 })
        {
            DetailsList.ItemsSource = details;
            DetailsBox.Visibility = Visibility.Visible;
        }

        (IconGlyph.Text, var brushKey) = kind switch
        {
            DialogKind.Danger => ("", "DangerBrush"),
            DialogKind.Success => ("", "SuccessBrush"),
            DialogKind.Confirm => ("", "AccentTextBrush"),
            _ => ("", "AccentTextBrush"),
        };
        IconGlyph.Foreground = (Brush)FindResource(brushKey);
        if (kind == DialogKind.Danger)
        {
            IconBg.Background = (Brush)FindResource("DangerSoftBrush");
            ConfirmButton.Style = (Style)FindResource("SecondaryButton");
            ConfirmButton.Foreground = (Brush)FindResource("DangerBrush");
        }

        MouseLeftButtonDown += (_, _) => DragMove();
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class DialogService
{
    public bool Confirm(string title, string message, IReadOnlyList<string>? details = null,
        DialogKind kind = DialogKind.Confirm, string confirmText = "Confirmar", string cancelText = "Cancelar") =>
        Show(title, message, details, kind, confirmText, cancelText);

    public void Info(string title, string message, IReadOnlyList<string>? details = null, DialogKind kind = DialogKind.Info) =>
        Show(title, message, details, kind, "OK", null);

    private static bool Show(string title, string message, IReadOnlyList<string>? details, DialogKind kind, string confirm, string? cancel)
    {
        var app = Application.Current;
        if (app is null) return false;
        return app.Dispatcher.Invoke(() =>
        {
            var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
            var dlg = new DialogWindow(title, message, details, kind, confirm, cancel);
            if (owner is not null && owner.IsVisible) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dlg.ShowDialog() == true;
        });
    }
}
