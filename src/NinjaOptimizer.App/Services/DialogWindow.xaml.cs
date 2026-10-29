using System.Windows;
using System.Windows.Media;

namespace NinjaOptimizer.App.Services;

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

        var (icon, fg, bg, border) = kind switch
        {
            DialogKind.Danger => ("Icon.Error", "DangerBrush", "DangerSoftBrush", "DangerBorderBrush"),
            DialogKind.Success => ("Icon.CheckCircle", "SuccessBrush", "SuccessSoftBrush", "SuccessBorderBrush"),
            DialogKind.Confirm => ("Icon.Shield", "AccentTextBrush", "AccentSoftBrush", "AccentSoftBorderBrush"),
            _ => ("Icon.Info", "AccentTextBrush", "AccentSoftBrush", "AccentSoftBorderBrush"),
        };
        IconGlyph.Data = (Geometry)FindResource(icon);
        IconGlyph.Foreground = (Brush)FindResource(fg);
        IconBg.Background = (Brush)FindResource(bg);
        IconBg.BorderBrush = (Brush)FindResource(border);

        // Ação destrutiva: botão secundário com texto em vermelho (nunca o roxo da marca).
        if (kind == DialogKind.Danger && cancelText is not null)
        {
            ConfirmButton.Style = (Style)FindResource("SecondaryButton");
            ConfirmButton.Foreground = (Brush)FindResource("DangerBrush");
        }

        MouseLeftButtonDown += (_, _) => DragMove();
    }

    private List<System.Windows.Controls.CheckBox>? _choices;

    /// <summary>Troca a lista de detalhes por caixas de seleção (todas marcadas).</summary>
    public void UseChoices(IReadOnlyList<string> items)
    {
        _choices = items.Select(i => new System.Windows.Controls.CheckBox
        {
            Content = i, IsChecked = true, Margin = new Thickness(0, 4, 0, 4),
            Foreground = (Brush)FindResource("TextBrush"),
        }).ToList();
        DetailsList.ItemTemplate = null;
        DetailsList.ItemsSource = _choices;
        DetailsBox.Visibility = Visibility.Visible;
    }

    public IReadOnlyList<string> Chosen => _choices?.Where(c => c.IsChecked == true).Select(c => (string)c.Content).ToList() ?? [];

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class DialogService
{
    public bool Confirm(string title, string message, IReadOnlyList<string>? details = null,
        DialogKind kind = DialogKind.Confirm, string confirmText = "Confirmar", string cancelText = "Cancelar") =>
        Show(title, message, details, kind, confirmText, cancelText);

    public void Info(string title, string message, IReadOnlyList<string>? details = null, DialogKind kind = DialogKind.Info) =>
        Show(title, message, details, kind, "Fechar", null);

    /// <summary>Caixas de seleção (todas marcadas). null = cancelado.</summary>
    public IReadOnlyList<string>? Choose(string title, string message, IReadOnlyList<string> items, string confirmText)
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Dispatcher.Invoke(() =>
        {
            var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
            var dlg = new DialogWindow(title, message, null, DialogKind.Danger, confirmText, "Cancelar");
            dlg.UseChoices(items);
            if (owner is not null && owner.IsVisible) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return dlg.ShowDialog() == true ? dlg.Chosen : null;
        });
    }

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

/// <summary>Perguntas das tarefas de manutenção no meio da execução, pelas janelas do app.</summary>
public sealed class MaintenanceUi(DialogService dialogs) : NinjaOptimizer.Core.Maintenance.IMaintenanceUi
{
    public Task<IReadOnlyList<string>?> ChooseAsync(string title, string message, IReadOnlyList<string> items, string confirmText) =>
        Task.FromResult(dialogs.Choose(title, message, items, confirmText));

    public Task<bool> ConfirmAsync(string title, string message, IReadOnlyList<string> details, string confirmText) =>
        Task.FromResult(dialogs.Confirm(title, message, details, DialogKind.Danger, confirmText));
}
