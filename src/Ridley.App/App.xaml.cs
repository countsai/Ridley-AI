using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ridley.App.Services;
using Ridley.App.ViewModels;
using Ridley.Core;
using Ridley.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Ridley.App;

public partial class App : Application
{
    private const string SingleInstanceName = "Ridley.SingleInstance.6c1a";

    private Mutex? _singleInstance;
    private IHost? _host;
    private TrayIconService? _tray;
    private readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Information);
    private Serilog.ILogger _bootLog = Serilog.Core.Logger.None;

    internal bool ExitRequested { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show("Ridley is already running. Look for it in the system tray.", "Ridley");
            Shutdown();
            return;
        }

#if DEBUG
        DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
#endif

        try
        {
            _host = BuildHost();
            RegisterGlobalExceptionHandlers();
            await _host.StartAsync();

            var vm = _host.Services.GetRequiredService<MainViewModel>();
            await vm.Settings.LoadAsync(CancellationToken.None);
            _levelSwitch.MinimumLevel = ParseLevel(vm.Settings.Current.LogLevel);
            vm.Settings.Applied += (_, s) => _levelSwitch.MinimumLevel = ParseLevel(s.LogLevel);

            var window = new MainWindow(vm, this);
            MainWindow = window;
            _tray = new TrayIconService(window.RestoreFromTray, RequestExit);
            window.Show();
            _bootLog.Information("Ridley started");
        }
        catch (Exception ex)
        {
            _bootLog.Fatal(ex, "Startup failed");
            System.Windows.MessageBox.Show($"Ridley could not start:\n\n{ex.Message}\n\nSee the log in %LOCALAPPDATA%\\Ridley\\logs.", "Ridley", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestExit();
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables(prefix: "RIDLEY__");

        var logDirectory = builder.Configuration["Logging:Directory"];
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ridley", "logs");
        }

        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .WriteTo.File(
                Path.Combine(logDirectory, "ridley-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: false)
            .CreateLogger();
        _bootLog = logger;

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);

        var databasePath = builder.Configuration["Storage:DatabasePath"];
        builder.Services.AddRidleyStorage(string.IsNullOrWhiteSpace(databasePath) ? null : databasePath);

        builder.Services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Dispatcher));
        builder.Services.AddSingleton<CaptureIndicatorService>();
        builder.Services.AddSingleton<ICaptureIndicator>(sp => sp.GetRequiredService<CaptureIndicatorService>());
        builder.Services.AddSingleton<IConfirmationPrompt, MessageBoxConfirmationPrompt>();
        builder.Services.AddSingleton<ITurnHandler, UnavailableTurnHandler>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        return builder.Build();
    }

    private static LogEventLevel ParseLevel(string level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _bootLog.Error(args.Exception, "Unhandled UI exception");
            System.Windows.MessageBox.Show("Something unexpected went wrong. Details were written to the log.", "Ridley", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true; // stay alive; the user's work is not tied to this process
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _bootLog.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    internal void RequestExit()
    {
        ExitRequested = true;
        _tray?.Dispose();
        _tray = null;
        MainWindow?.Close();
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            _bootLog.Warning(ex, "Host shutdown was not clean");
        }

        _singleInstance?.Dispose();
        Shutdown();
    }
}
