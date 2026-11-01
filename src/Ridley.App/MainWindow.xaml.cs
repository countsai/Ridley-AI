using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using Ridley.App.ViewModels;

namespace Ridley.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly App _app;

    public MainWindow(MainViewModel vm, App app)
    {
        InitializeComponent();
        _vm = vm;
        _app = app;
        DataContext = vm;
        vm.Messages.CollectionChanged += OnMessagesChanged;
        Loaded += (_, _) => InputBox.Focus();
    }

    internal void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        InputBox.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.ExitRequested && _vm.MinimiseToTray)
        {
            e.Cancel = true; // hide to tray instead of quitting
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => MessageScroller.ScrollToEnd());
}

