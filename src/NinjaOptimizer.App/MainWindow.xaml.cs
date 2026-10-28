using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using NinjaOptimizer.App.ViewModels;

namespace NinjaOptimizer.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;
        // As duas listas do menu (principal e rodapé) refletem sempre a página atual,
        // inclusive quando a navegação vem do código (ex.: botão "Ver o que mudou").
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Current)) SyncNav(); };
        Loaded += (_, _) => SyncNav();
        // Com WindowChrome, a janela maximizada "vaza" alguns pixels para fora da tela.
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private readonly MainViewModel _vm;
    private bool _syncing;

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ListBox { SelectedItem: NavItem item }) return;
        _vm.Current = item;
    }

    /// <summary>Seleciona a página atual na lista que a contém e limpa a outra.</summary>
    private void SyncNav()
    {
        _syncing = true;
        try
        {
            foreach (var list in new[] { MainList, FooterList })
                list.SelectedItem = list.Items.Contains(_vm.Current) ? _vm.Current : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
