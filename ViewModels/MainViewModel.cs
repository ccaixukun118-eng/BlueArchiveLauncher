using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BlueArchiveLauncher.Core.Models;
using BlueArchiveLauncher.Core.Services;

namespace BlueArchiveLauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly FileLogger _logger;
    private readonly JsonStateStore _stateStore;
    private readonly JsonCacheStore _cacheStore;
    private readonly SourceManager _sourceManager;
    private readonly ServerContextManager _contextManager;
    private readonly MuMuManager _mumuManager;
    private readonly object _loadGate = new();
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private int _mumuActionRunning;
    private ServerId _selectedServer = ServerId.Global;
    private DashboardSnapshot? _snapshot;
    private MuMuStatus _mumuStatus = new(false, "检测中", "正在检查 MuMu 状态。");
    private string _statusMessage = "正在初始化服务器上下文...";
    private string _currentNavLabel = "总览";

    public MainViewModel(
        FileLogger logger,
        JsonStateStore stateStore,
        JsonCacheStore cacheStore,
        SourceManager sourceManager,
        ServerContextManager contextManager,
        MuMuManager mumuManager)
    {
        _logger = logger;
        _stateStore = stateStore;
        _cacheStore = cacheStore;
        _sourceManager = sourceManager;
        _contextManager = contextManager;
        _mumuManager = mumuManager;
        _mumuManager.InstallProgressChanged += OnMumuInstallProgress;

        Announcements = new ObservableCollection<AnnouncementInfo>();
        RecommendedCharacters = new ObservableCollection<CharacterRecommendation>();
        RaidRecommendations = new ObservableCollection<CharacterRecommendation>();
        RedeemCodes = new ObservableCollection<RedeemCodeInfo>();

        SwitchServerCommand = new RelayCommand(parameter =>
        {
            if (parameter is string code && Enum.TryParse<ServerId>(code, true, out var serverId))
            {
                RunSafely(() => SwitchServerAsync(serverId), "切换服务器");
            }
        });
        RefreshCommand = new RelayCommand(
            _ => RunSafely(() => LoadDashboardAsync(_selectedServer, forceRefresh: true), "刷新数据"),
            _ => !IsLoading);
        LaunchGameCommand = new RelayCommand(
            _ => RunSafely(LaunchGameAsync, "连接 MuMu"),
            _ => CanLaunchGame);
        CopyCodeCommand = new RelayCommand(
            parameter => RunSafely(() =>
            {
                CopyCode(parameter as string);
                return Task.CompletedTask;
            }, "复制兑换码"));
        NavigateCommand = new RelayCommand(
            parameter => RunSafely(() =>
            {
                Navigate(parameter as string);
                return Task.CompletedTask;
            }, "打开页面"));

        RunSafely(InitializeAsync, "初始化应用");
    }

    public ICommand SwitchServerCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand NavigateCommand { get; }

    public ObservableCollection<AnnouncementInfo> Announcements { get; }
    public ObservableCollection<CharacterRecommendation> RecommendedCharacters { get; }
    public ObservableCollection<CharacterRecommendation> RaidRecommendations { get; }
    public ObservableCollection<RedeemCodeInfo> RedeemCodes { get; }

    public bool IsGlobalSelected => _selectedServer == ServerId.Global;
    public bool IsCnSelected => _selectedServer == ServerId.CN;
    public bool IsJpSelected => _selectedServer == ServerId.JP;

    public bool IsLoading { get; private set; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CurrentNavLabel
    {
        get => _currentNavLabel;
        private set => SetProperty(ref _currentNavLabel, value);
    }

    public string SectionTitle => "控制台总览";

    public string SectionSubtitle =>
        $"{ActiveServerLabel} · 数据、缓存和启动实例均按服务器上下文隔离";

    public string ActiveServerLabel => _selectedServer switch
    {
        ServerId.Global => "国际服",
        ServerId.CN => "国服",
        ServerId.JP => "日服",
        _ => _selectedServer.ToString()
    };

    public string HeroEyebrow => "碧蓝档案三服控制台";
    public string HeroTitle => $"{ActiveServerLabel} 准备就绪";
    public string HeroDescription => _snapshot is null
        ? "正在加载当前服务器上下文..."
        : "卡池、总力战和活动信息来自 Kivo Wiki；接口未提供的字段会明确标记为待核验。数据源不可用时自动读取本服最后一次成功缓存。";

    public string LastSyncLabel => _snapshot is null
        ? "尚未同步"
        : _snapshot.RetrievedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");

    public string DataSourceLabel => _snapshot?.SourceName ?? "等待数据源";
    public string CacheStatusLabel => _snapshot is null ? "未读取" : "按服缓存已启用";
    public string MumuStatusLabel => _mumuStatus.Label;
    public string MumuStatusDetail => _mumuStatus.Detail;
    public string MumuShortcutLabel => string.IsNullOrWhiteSpace(_mumuStatus.ShortcutPath)
        ? (_mumuStatus.Label == "未安装" ? "未安装" : "安装目录")
        : Path.GetFileName(_mumuStatus.ShortcutPath);

    public string MumuActionLabel => _mumuStatus.Label == "未安装"
        ? "下载并安装 MuMu"
        : "启动并连接 MuMu";
    public Brush MumuStatusBrush
    {
        get
        {
            if (_mumuStatus.IsAdbConnected)
            {
                return Application.Current.Resources["SuccessBrush"] as Brush ?? Brushes.LightGreen;
            }

            if (_mumuStatus.Label is "未安装" or "下载失败" or "安装失败" or "未完成安装")
            {
                return Application.Current.Resources["DangerBrush"] as Brush ?? Brushes.Salmon;
            }

            return Application.Current.Resources["GoldBrush"] as Brush ?? Brushes.Gold;
        }
    }

    public string BannerName => _snapshot?.Banner.Name ?? "读取中";
    public string BannerInitials => _snapshot?.Banner.Initials ?? "--";
    public string BannerType => _snapshot?.Banner.Type ?? "数据源加载中";
    public string BannerWindow => _snapshot?.Banner.Window ?? "--";
    public string BannerDescription => _snapshot?.Banner.Description ?? "正在读取当前服务器卡池信息。";

    public string RaidName => _snapshot?.Raid.Name ?? "读取中";
    public string RaidWindow => _snapshot?.Raid.Window ?? "--";
    public string RaidArmor => _snapshot?.Raid.Armor ?? "--";
    public string RaidDifficulty => _snapshot?.Raid.Difficulty ?? "--";
    public string RaidHint => _snapshot?.Raid.Hint ?? "正在读取总力战信息。";

    public string CodeName => _snapshot?.RedeemCode.Name ?? "读取中";
    public string CodeValue => _snapshot?.RedeemCodes.Count > 0
        ? $"{_snapshot.RedeemCodes.Count} 条公开兑换码"
        : _snapshot?.RedeemCode.Value ?? "----------";
    public string CodeExpiry => _snapshot?.RedeemCodes.Count > 0
        ? "公开汇总未标注到期日"
        : _snapshot?.RedeemCode.Expiry ?? "--";
    public string CodeStatus => _snapshot?.RedeemCode.Status ?? "状态读取中";

    public string FooterMessage =>
        IsLoading ? "正在切换服务器上下文，旧请求不会回写到当前页面。" : _mumuStatus.Detail;

    private bool CanLaunchGame =>
        !IsLoading && Volatile.Read(ref _mumuActionRunning) == 0;

    public void ReportError(string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            _logger.Error(message, exception);
        }

        StatusMessage = message;
        OnPropertyChanged(nameof(FooterMessage));
    }

    private void RunSafely(Func<Task> operation, string operationName)
    {
        _ = RunSafelyAsync(operation, operationName);
    }

    private async Task RunSafelyAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"已取消操作：{operationName}");
        }
        catch (Exception ex)
        {
            _logger.Error($"{operationName}失败", ex);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ReportError($"{operationName}没有完成，程序仍在运行。");
            });
        }
    }

    private async Task InitializeAsync()
    {
        var savedServer = await _stateStore.ReadSelectedServerAsync(CancellationToken.None);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _selectedServer = savedServer;
            NotifyServerProperties();
        });

        await LoadDashboardAsync(savedServer, forceRefresh: false);
        _mumuStatus = await _mumuManager.DetectAsync(CancellationToken.None);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            NotifyMumuProperties();
            StatusMessage = $"{ActiveServerLabel} 已加载，MuMu 状态：{_mumuStatus.Label}。";
        });
    }

    private async Task SwitchServerAsync(ServerId serverId)
    {
        if (serverId == _selectedServer && _snapshot is not null)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _selectedServer = serverId;
            _snapshot = null;
            Announcements.Clear();
            RecommendedCharacters.Clear();
            RaidRecommendations.Clear();
            NotifyServerProperties();
            StatusMessage = $"正在切换到 {ActiveServerLabel}...";
        });

        await _stateStore.SaveSelectedServerAsync(serverId, CancellationToken.None);
        await LoadDashboardAsync(serverId, forceRefresh: false);
    }

    private async Task LoadDashboardAsync(ServerId serverId, bool forceRefresh)
    {
        CancellationToken token;
        int version;
        lock (_loadGate)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            token = _loadCts.Token;
            version = ++_loadVersion;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsLoading = true;
            StatusMessage = forceRefresh
                ? $"正在刷新 {GetServerLabel(serverId)} 数据..."
                : $"正在加载 {GetServerLabel(serverId)} 数据...";
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(FooterMessage));
            ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
            ((RelayCommand)LaunchGameCommand).RaiseCanExecuteChanged();
        });

        var context = _contextManager.GetContext(serverId);
        try
        {
            DashboardSnapshot? snapshot = null;
            try
            {
                snapshot = await _sourceManager.GetDashboardAsync(context, token);
                await _cacheStore.SaveDashboardAsync(serverId, snapshot, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error($"读取 {serverId} 数据源失败，尝试缓存", ex);
                snapshot = await _cacheStore.ReadDashboardAsync(serverId, token);
                if (snapshot is null)
                {
                    throw;
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (version == _loadVersion && _selectedServer == serverId)
                    {
                        StatusMessage = $"{ActiveServerLabel} 数据源暂时不可用，已回退到最后一次成功缓存。";
                    }
                });
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version != _loadVersion || _selectedServer != serverId || token.IsCancellationRequested)
                {
                    _logger.Warn($"丢弃过期数据回写：{serverId}，版本 {version}");
                    return;
                }

                _snapshot = snapshot;
                Announcements.Clear();
                foreach (var item in snapshot!.Announcements)
                {
                    Announcements.Add(item);
                }

                RecommendedCharacters.Clear();
                foreach (var item in snapshot.RecommendedCharacters)
                {
                    RecommendedCharacters.Add(item);
                }

                RaidRecommendations.Clear();
                foreach (var item in snapshot.Raid.Recommendations)
                {
                    RaidRecommendations.Add(item);
                }

                RedeemCodes.Clear();
                var redeemCodes = snapshot.RedeemCodes.Count > 0
                    ? snapshot.RedeemCodes
                    : IsUsableRedeemCode(snapshot.RedeemCode.Value)
                        ? new[] { snapshot.RedeemCode }
                        : Array.Empty<RedeemCodeInfo>();
                foreach (var item in redeemCodes)
                {
                    RedeemCodes.Add(item);
                }

                StatusMessage = $"{ActiveServerLabel} 数据已更新。";
                NotifySnapshotProperties();
            });
        }
        catch (OperationCanceledException)
        {
            _logger.Info($"取消过期加载请求：{serverId}");
        }
        catch (Exception ex)
        {
            _logger.Error($"加载 {serverId} 数据失败", ex);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version == _loadVersion && _selectedServer == serverId)
                {
                    StatusMessage = $"{ActiveServerLabel} 数据暂时不可用，请检查日志。";
                }
            });
        }
        finally
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (version == _loadVersion)
                {
                    IsLoading = false;
                    OnPropertyChanged(nameof(IsLoading));
                    OnPropertyChanged(nameof(FooterMessage));
                    ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)LaunchGameCommand).RaiseCanExecuteChanged();
                }
            });
        }
    }

    private async Task LaunchGameAsync()
    {
        if (Interlocked.Exchange(ref _mumuActionRunning, 1) != 0)
        {
            return;
        }

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = _mumuStatus.Label == "未安装"
                    ? "没有找到 MuMu，正在下载官方安装器..."
                    : "正在查找并启动 MuMu...";
                OnPropertyChanged(nameof(FooterMessage));
                ((RelayCommand)LaunchGameCommand).RaiseCanExecuteChanged();
            });

            _mumuStatus = await _mumuManager.LaunchAndConnectAsync(CancellationToken.None);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                NotifyMumuProperties();
                StatusMessage = _mumuStatus.IsAvailable
                    ? $"{ActiveServerLabel} 已连接 MuMu，可以继续启动游戏。"
                    : _mumuStatus.Detail;
            });
        }
        finally
        {
            Interlocked.Exchange(ref _mumuActionRunning, 0);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                ((RelayCommand)LaunchGameCommand).RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(FooterMessage));
            });
        }
    }

    private void CopyCode(string? requestedCode = null)
    {
        var code = requestedCode ?? _snapshot?.RedeemCode.Value;
        if (!IsUsableRedeemCode(code))
        {
            StatusMessage = "当前没有可复制的兑换码。";
            OnPropertyChanged(nameof(FooterMessage));
            return;
        }

        Clipboard.SetText(code);
        StatusMessage = $"{ActiveServerLabel} 兑换码已复制。";
        OnPropertyChanged(nameof(FooterMessage));
    }

    private static bool IsUsableRedeemCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !string.Equals(value, "--", StringComparison.Ordinal)
        && !string.Equals(value, "----------", StringComparison.Ordinal)
        && !string.Equals(value, "暂无", StringComparison.Ordinal);

    private void Navigate(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        CurrentNavLabel = target switch
        {
            "Dashboard" => "总览",
            "Characters" => "角色数据库",
            "Raid" => "总力战",
            "Events" => "活动与公告",
            "Codes" => "兑换码",
            "Settings" => "设置",
            "Diagnostics" => "诊断",
            _ => "总览"
        };

        if (target != "Dashboard")
        {
            StatusMessage = $"{CurrentNavLabel} 模块正在准备中，当前首页和服务器隔离链路可用。";
            OnPropertyChanged(nameof(FooterMessage));
        }
    }

    private static string GetServerLabel(ServerId serverId) => serverId switch
    {
        ServerId.Global => "国际服",
        ServerId.CN => "国服",
        ServerId.JP => "日服",
        _ => serverId.ToString()
    };

    private void NotifyServerProperties()
    {
        OnPropertyChanged(nameof(IsGlobalSelected));
        OnPropertyChanged(nameof(IsCnSelected));
        OnPropertyChanged(nameof(IsJpSelected));
        OnPropertyChanged(nameof(ActiveServerLabel));
        OnPropertyChanged(nameof(SectionSubtitle));
        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroDescription));
        OnPropertyChanged(nameof(FooterMessage));
    }

    private void OnMumuInstallProgress(MuMuInstallProgress progress)
    {
        _mumuStatus = _mumuStatus with
        {
            Label = progress.Label,
            Detail = progress.Detail
        };
        Application.Current.Dispatcher.Invoke(() =>
        {
            NotifyMumuProperties();
            StatusMessage = progress.Detail;
        });
    }

    private void NotifyMumuProperties()
    {
        OnPropertyChanged(nameof(MumuStatusLabel));
        OnPropertyChanged(nameof(MumuStatusDetail));
        OnPropertyChanged(nameof(MumuShortcutLabel));
        OnPropertyChanged(nameof(MumuActionLabel));
        OnPropertyChanged(nameof(MumuStatusBrush));
        OnPropertyChanged(nameof(FooterMessage));
    }

    private void NotifySnapshotProperties()
    {
        OnPropertyChanged(nameof(HeroDescription));
        OnPropertyChanged(nameof(LastSyncLabel));
        OnPropertyChanged(nameof(DataSourceLabel));
        OnPropertyChanged(nameof(CacheStatusLabel));
        OnPropertyChanged(nameof(BannerName));
        OnPropertyChanged(nameof(BannerInitials));
        OnPropertyChanged(nameof(BannerType));
        OnPropertyChanged(nameof(BannerWindow));
        OnPropertyChanged(nameof(BannerDescription));
        OnPropertyChanged(nameof(RaidName));
        OnPropertyChanged(nameof(RaidWindow));
        OnPropertyChanged(nameof(RaidArmor));
        OnPropertyChanged(nameof(RaidDifficulty));
        OnPropertyChanged(nameof(RaidHint));
        OnPropertyChanged(nameof(CodeName));
        OnPropertyChanged(nameof(CodeValue));
        OnPropertyChanged(nameof(CodeExpiry));
        OnPropertyChanged(nameof(CodeStatus));
        OnPropertyChanged(nameof(FooterMessage));
    }
}
