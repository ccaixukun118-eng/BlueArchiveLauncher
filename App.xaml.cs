using System.Windows;
using System.Windows.Threading;
using System.Net.Http;
using BlueArchiveLauncher.Core.Interfaces;
using BlueArchiveLauncher.Core.Services;
using BlueArchiveLauncher.ViewModels;

namespace BlueArchiveLauncher;

public partial class App : Application
{
    private FileLogger? _logger;
    private MainViewModel? _viewModel;
    private HttpClient? _httpClient;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _logger = new FileLogger();
        var stateStore = new JsonStateStore(_logger);
        var cacheStore = new JsonCacheStore(_logger);
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.kivo.wiki/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        var gameKeeAdapter = new GameKeeBackupAdapter(_logger, _httpClient);
        var publicRedeemCodeProvider = new PublicRedeemCodeProvider(_logger, _httpClient);
        var sourceManager = new SourceManager(
            new IDataSource[]
            {
                new KivoDataSource(
                    _logger,
                    _httpClient,
                    gameKeeAdapter,
                    publicRedeemCodeProvider)
            },
            _logger);
        var contextManager = new ServerContextManager();
        var mumuManager = new MuMuManager(_logger);

        _viewModel = new MainViewModel(
            _logger,
            stateStore,
            cacheStore,
            sourceManager,
            contextManager,
            mumuManager);

        var window = new MainWindow
        {
            DataContext = _viewModel
        };

        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("未处理的界面异常", e.Exception);
        _viewModel?.ReportError("刚才的操作没有完成，程序仍在运行。");
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger?.Error("未处理的应用异常", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("未观察到的后台任务异常", e.Exception);
        e.SetObserved();
    }
}
