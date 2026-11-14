using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cleaner;
using PCPulse.Contracts;

namespace DiskPulse.PulseModule.Views;

public partial class DiskPulseRootView : UserControl
{
    private static readonly TimeSpan ScanResultLifetime = TimeSpan.FromMinutes(5);

    private readonly IPulseHostContext _hostContext;
    private readonly bool _isStandaloneHost;
    private readonly IStorageVolumeService _storageService = PlatformServices.CreateStorageVolumeService();
    private readonly CleanerScanService _scanService = new();
    private readonly DiskUsageService _diskUsageService;
    private readonly CleanerSettingsService _settingsService;
    private readonly CleanupPolicyService _policyService;
    private readonly CleanupHistoryService _historyService;
    private readonly ExclusionListService _exclusionsService;
    private readonly DispatcherTimer _ringAnimationTimer;

    private IReadOnlyList<string> _selectedDrives = [];
    private IReadOnlyList<string> _permanentExclusions = [];
    private readonly HashSet<string> _excludedPaths = new(CleanerPaths.PathComparer);
    private int _minimumFileAgeHours;
    private ScanResult? _lastScanResult;
    private DiskUsageSnapshot? _lastDiskUsage;
    private CancellationTokenSource? _activeOperationCts;
    private CancellationTokenSource? _usageCts;
    private ProgramsView? _programsView;
    private bool _isBusy;
    private DateTimeOffset _ringAnimStart;
    private IReadOnlyList<DiskUsageSegment>? _pendingSegments;

    public event Action? ScanOrCleanupCompleted;

    public DiskPulseRootView() : this(new FallbackDesignHostContext(), isStandaloneHost: true)
    {
    }

    public DiskPulseRootView(IPulseHostContext hostContext, bool isStandaloneHost)
    {
        InitializeComponent();
        _hostContext = hostContext;
        _isStandaloneHost = isStandaloneHost;

        var dataDir = hostContext.ModuleDataDirectory;
        AppLogService.Configure(
            dataDir,
            isStandaloneHost
                ? null
                : (msg, ex) =>
                {
                    if (ex is not null)
                    {
                        hostContext.Logger.Error(msg, ex);
                    }
                    else
                    {
                        hostContext.Logger.Info(msg);
                    }
                });

        _diskUsageService = new DiskUsageService(_storageService, dataDir);
        _settingsService = CleanerSettingsService.FromDataDirectory(dataDir);
        _policyService = CleanupPolicyService.FromDataDirectory(dataDir);
        _historyService = CleanupHistoryService.FromDataDirectory(dataDir);
        _exclusionsService = ExclusionListService.FromDataDirectory(dataDir);

        _ringAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _ringAnimationTimer.Tick += RingAnimationTimer_Tick;

        InitializeHeaderAndState();
        KeyDown += RootView_KeyDown;
    }

    public void DisposeResources()
    {
        _ringAnimationTimer.Stop();
        _ringAnimationTimer.Tick -= RingAnimationTimer_Tick;
        _activeOperationCts?.Cancel();
        _activeOperationCts?.Dispose();
        _activeOperationCts = null;
        _usageCts?.Cancel();
        _usageCts?.Dispose();
        _usageCts = null;
        _programsView?.DisposeResources();
        _programsView = null;
    }

    public PulseStatusSummary? BuildStatusSummary()
    {
        if (_lastScanResult is not null)
        {
            var includedBytes = GetIncludedBytes(_lastScanResult);
            var includedCount = GetIncludedCount(_lastScanResult);
            if (includedBytes > 0 || includedCount > 0)
            {
                return new PulseStatusSummary(
                    Headline: $"Можно освободить {ByteSizeFormatter.Format(includedBytes)}",
                    Details: $"Найдено объектов: {includedCount:N0} (проверка от {_lastScanResult.ScannedAt.LocalDateTime:HH:mm}).",
                    Severity: includedBytes >= 512L * 1024 * 1024 ? PulseStatusSeverity.Warning : PulseStatusSeverity.Info);
            }

            return new PulseStatusSummary(
                Headline: "Система чиста",
                Details: $"Последняя проверка ({_lastScanResult.ScannedAt.LocalDateTime:HH:mm}) не выявила мусора.",
                Severity: PulseStatusSeverity.Normal);
        }

        if (_lastDiskUsage is not null)
        {
            var freeSeg = _lastDiskUsage.Segments.FirstOrDefault(s => s.Name == "Свободно");
            var freeBytes = freeSeg?.Bytes ?? 0;
            var freePercent = _lastDiskUsage.TotalBytes > 0
                ? (double)freeBytes / _lastDiskUsage.TotalBytes * 100d
                : 0d;

            var severity = freePercent is > 0 and < 10d
                ? PulseStatusSeverity.Attention
                : PulseStatusSeverity.Normal;

            return new PulseStatusSummary(
                Headline: $"Свободно {ByteSizeFormatter.Format(freeBytes)} ({freePercent:0}%)",
                Details: $"Всего на выбранных томах: {ByteSizeFormatter.Format(_lastDiskUsage.TotalBytes)}.",
                Severity: severity);
        }

        return null;
    }

    private void InitializeHeaderAndState()
    {
        var user = string.IsNullOrWhiteSpace(Environment.UserName) ? "Пользователь" : Environment.UserName;
        WelcomeText.Text = $"Привет, {user}";
        UserInitialText.Text = user[..1].ToUpper(CultureInfo.CurrentCulture);

        var available = _storageService.GetAvailableVolumes().Select(v => v.Root).ToArray();
        var defaultDrive = _storageService.GetSystemDriveRoot();
        _selectedDrives = _settingsService.LoadSelectedDrives(available, defaultDrive);
        _minimumFileAgeHours = _policyService.LoadMinimumAgeHours();
        _permanentExclusions = _exclusionsService.Load();

        UpdateSidebarDriveSummary();
        LoadLatestHistory();
        _ = RefreshDiskUsageAsync();
    }

    private void UpdateSidebarDriveSummary()
    {
        var labels = _selectedDrives.Select(CleanerPaths.FormatVolumeLabel);
        SidebarDriveSubtext.Text = _selectedDrives.Count == 1
            ? $"Том: {labels.First()}"
            : $"Выбрано томов: {_selectedDrives.Count} ({string.Join(", ", labels)})";
    }

    private void LoadLatestHistory()
    {
        var latest = _historyService.LoadLatest();
        if (latest is not null && _lastScanResult is null)
        {
            ActivityText.Text = $"Последняя очистка {latest.Timestamp.LocalDateTime:dd.MM HH:mm}";
            ActivityResultText.Text = $"Освобождено {ByteSizeFormatter.Format(latest.ReclaimedBytes)}";
            ActivitySizeText.Text = $"Категории: {latest.Scope}. Удалено: {latest.DeletedFiles:N0}.";
        }
        else if (latest is null && _lastScanResult is null)
        {
            ActivityText.Text = "Проверка ещё не выполнялась";
            ActivityResultText.Text = "Запустите сканирование";
            ActivitySizeText.Text = "Данные не удаляются без подтверждения";
        }
    }

    private async Task RefreshDiskUsageAsync(bool forceRefresh = false)
    {
        _usageCts?.Cancel();
        _usageCts?.Dispose();
        _usageCts = new CancellationTokenSource();
        var token = _usageCts.Token;

        try
        {
            var snapshot = await _diskUsageService.ReadAsync(_selectedDrives, token, forceRefresh);
            if (snapshot is null || token.IsCancellationRequested)
            {
                return;
            }

            _lastDiskUsage = snapshot;
            ApplyDiskUsageSnapshot(snapshot);
            ScanOrCleanupCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _hostContext.Logger.Warn($"Не удалось обновить диаграмму использования диска: {ex.Message}");
        }
    }

    private void ApplyDiskUsageSnapshot(DiskUsageSnapshot snapshot)
    {
        var freeBytes = snapshot.Segments.FirstOrDefault(s => s.Name == "Свободно")?.Bytes ?? 0;
        var freePercent = snapshot.TotalBytes > 0 ? Math.Clamp(freeBytes * 100d / snapshot.TotalBytes, 0, 100) : 0;
        var usedPercent = 100d - freePercent;

        FreeSpaceText.Text = $"{freePercent:0}%";
        FreeSpaceProgress.Value = usedPercent;

        if (_lastScanResult is null && !_isBusy)
        {
            StatusDetails.Text = $"Всего {ByteSizeFormatter.Format(snapshot.TotalBytes)}, свободно {ByteSizeFormatter.Format(freeBytes)}. Нажмите «Начать проверку».";
        }

        BuildLegend(snapshot.Segments);
        _pendingSegments = snapshot.Segments;
        _ringAnimStart = DateTimeOffset.UtcNow;
        _ringAnimationTimer.Start();
    }

    private void BuildLegend(IReadOnlyList<DiskUsageSegment> segments)
    {
        DiskLegendPanel.Children.Clear();
        foreach (var segment in segments.Where(s => s.Bytes > 0).Take(6))
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Margin = new Thickness(0, 3)
            };

            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = Brush.Parse(segment.Color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var name = new TextBlock
            {
                Text = segment.Name,
                Foreground = FindBrush("Muted", "#8E9BB0"),
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var value = new TextBlock
            {
                Text = ByteSizeFormatter.Format(segment.Bytes),
                Foreground = FindBrush("Ink", "#F4F6FB"),
                FontSize = 11.5,
                FontWeight = FontWeight.Medium,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(value);
            DiskLegendPanel.Children.Add(row);
        }
    }

    private void RingAnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingSegments is null || _lastDiskUsage is null)
        {
            _ringAnimationTimer.Stop();
            return;
        }

        var elapsed = (DateTimeOffset.UtcNow - _ringAnimStart).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / 380d, 0d, 1d);
        var eased = 1d - Math.Pow(1d - progress, 3);

        DrawRing(_pendingSegments, _lastDiskUsage.TotalBytes, eased);

        if (progress >= 1d)
        {
            _ringAnimationTimer.Stop();
        }
    }

    private void DrawRing(IReadOnlyList<DiskUsageSegment> segments, long totalBytes, double animationProgress)
    {
        while (DiskUsageRing.Children.Count > 1)
        {
            DiskUsageRing.Children.RemoveAt(DiskUsageRing.Children.Count - 1);
        }

        if (totalBytes <= 0)
        {
            return;
        }

        const double size = 145d;
        const double stroke = 12d;
        const double radius = (size - stroke) / 2d;
        var center = new Point(size / 2d, size / 2d);
        var startAngle = -90d;

        foreach (var segment in segments)
        {
            if (segment.Bytes <= 0)
            {
                continue;
            }

            var fullSweep = (double)segment.Bytes / totalBytes * 360d;
            var sweep = Math.Min(359.6d, fullSweep * animationProgress);
            if (sweep < 1d)
            {
                startAngle += fullSweep;
                continue;
            }

            var endAngle = startAngle + sweep;
            var startRad = startAngle * Math.PI / 180d;
            var endRad = endAngle * Math.PI / 180d;

            var startPoint = new Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad));
            var endPoint = new Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad));

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(startPoint, isFilled: false);
                ctx.ArcTo(endPoint, new Size(radius, radius), 0, sweep > 180d, SweepDirection.Clockwise);
                ctx.EndFigure(isClosed: false);
            }

            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = geometry,
                Stroke = Brush.Parse(segment.Color),
                StrokeThickness = stroke,
                StrokeLineCap = PenLineCap.Flat
            };

            DiskUsageRing.Children.Add(path);
            startAngle += fullSweep;
        }
    }

    private void RootView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && HomeView.IsVisible && !_isBusy)
        {
            e.Handled = true;
            _ = RunScanAsync();
        }
    }

    private void HomeNavigation_Click(object? sender, RoutedEventArgs e) => ShowHomeView();

    private void ProgramsNavigation_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveNavButton(ProgramsNavButton);
        _programsView ??= new ProgramsView(_hostContext);
        ShowSubView(_programsView);
    }

    private void HistoryNavigation_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveNavButton(HistoryNavButton);
        ShowSubView(new HistoryView(_historyService, _hostContext.Dialogs, LoadLatestHistory, _hostContext.Logger));
    }

    private void SettingsNavigation_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveNavButton(SettingsNavButton);
        ShowSubView(new SettingsView(
            _minimumFileAgeHours,
            _selectedDrives.Count,
            _permanentExclusions,
            _storageService,
            _hostContext,
            _isStandaloneHost,
            hours =>
            {
                _minimumFileAgeHours = hours;
                _policyService.SaveMinimumAgeHours(hours);
            },
            exclusions =>
            {
                _permanentExclusions = exclusions;
                _exclusionsService.Save(exclusions);
            },
            OpenDriveSelectionView));
    }

    private void HelpNavigation_Click(object? sender, RoutedEventArgs e)
    {
        SetActiveNavButton(HelpNavButton);
        ShowSubView(new HelpView(_hostContext.ModuleDataDirectory));
    }

    private void DriveButton_Click(object? sender, RoutedEventArgs e) => OpenDriveSelectionView();

    private void OpenDriveSelectionView()
    {
        SetActiveNavButton(HomeNavButton);
        ShowSubView(new DriveSelectionView(
            _selectedDrives,
            _storageService,
            drives =>
            {
                _selectedDrives = drives;
                _settingsService.SaveSelectedDrives(drives);
                _lastScanResult = null;
                UpdateSidebarDriveSummary();
                ShowHomeView();
                _ = RefreshDiskUsageAsync(forceRefresh: true);
            },
            ShowHomeView,
            _hostContext.Dialogs));
    }

    private void DetailsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastScanResult is null)
        {
            return;
        }

        ShowSubView(new ScanDetailsView(
            _lastScanResult,
            _excludedPaths,
            UpdateScanSummaryUI,
            ShowHomeView,
            _hostContext.Dialogs,
            _hostContext.Logger));
    }

    private void ShowHomeView()
    {
        SetActiveNavButton(HomeNavButton);
        PageHost.Content = null;
        PageHost.IsVisible = false;
        HomeView.IsVisible = true;
        if (_lastScanResult is not null)
        {
            UpdateScanSummaryUI();
        }
    }

    private void ShowSubView(Control view)
    {
        HomeView.IsVisible = false;
        PageHost.Content = view;
        PageHost.IsVisible = true;
    }

    private void SetActiveNavButton(Button active)
    {
        foreach (var btn in new[] { HomeNavButton, ProgramsNavButton, HistoryNavButton, SettingsNavButton, HelpNavButton })
        {
            btn.Classes.Set("active", ReferenceEquals(btn, active));
        }
    }

    private void ScanButton_Click(object? sender, RoutedEventArgs e) => _ = RunScanAsync();

    private async Task RunScanAsync()
    {
        if (_isBusy)
        {
            return;
        }

        SetBusyState(true, "Сканируем временные файлы и кэши...");
        _excludedPaths.Clear();
        _activeOperationCts?.Dispose();
        _activeOperationCts = new CancellationTokenSource();
        var token = _activeOperationCts.Token;

        var progress = new Progress<ScanProgress>(p =>
        {
            StatusDetails.Text = $"{p.Stage}: найдено {p.FilesFound:N0} файлов ({ByteSizeFormatter.Format(p.BytesFound)})...";
        });

        try
        {
            _lastScanResult = await _scanService.ScanAsync(_selectedDrives, token, progress, _permanentExclusions);
            _hostContext.Logger.Info($"Сканирование завершено: найдено {_lastScanResult.TotalFiles} объектов ({ByteSizeFormatter.Format(_lastScanResult.TotalBytes)}).");
            UpdateScanSummaryUI();
            ScanOrCleanupCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Проверка отменена";
        }
        catch (Exception ex)
        {
            _hostContext.Logger.Error("Ошибка сканирования", ex);
            await _hostContext.Dialogs.ShowErrorAsync("Ошибка проверки", ex.Message);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private void UpdateScanSummaryUI()
    {
        if (_lastScanResult is null)
        {
            return;
        }

        var userFiles = _lastScanResult.UserTempFiles.Where(f => !_excludedPaths.Contains(f.Path)).ToArray();
        var sysFiles = _lastScanResult.WindowsTempFiles.Where(f => !_excludedPaths.Contains(f.Path)).ToArray();
        var devFiles = _lastScanResult.DevCacheFilesOrEmpty.Where(f => !_excludedPaths.Contains(f.Path)).ToArray();

        var userBytes = userFiles.Sum(f => f.Bytes);
        var sysBytes = sysFiles.Sum(f => f.Bytes);
        var devBytes = devFiles.Sum(f => f.Bytes);
        var binBytes = _lastScanResult.RecycleBin.Bytes;
        var totalBytes = userBytes + sysBytes + devBytes + binBytes;
        var totalCount = userFiles.Length + sysFiles.Length + devFiles.Length + _lastScanResult.RecycleBin.Items;

        UserTempValue.Text = ByteSizeFormatter.Format(userBytes);
        UserTempSubtitle.Text = $"{userFiles.Length:N0} файлов" + (devFiles.Length > 0 ? $" (+{devFiles.Length:N0} dev)" : "");

        WindowsTempValue.Text = ByteSizeFormatter.Format(sysBytes);
        WindowsTempSubtitle.Text = sysFiles.Length > 0
            ? $"{sysFiles.Length:N0} файлов" + (_hostContext.Elevation.IsElevated ? "" : " (потребует права)")
            : "0 файлов";

        RecycleBinValue.Text = ByteSizeFormatter.Format(binBytes);
        RecycleBinSubtitle.Text = $"{_lastScanResult.RecycleBin.Items:N0} объектов";

        if (totalBytes == 0 && totalCount == 0)
        {
            StatusText.Text = "Всё чисто";
            StatusDetails.Text = $"Проверка завершена в {_lastScanResult.ScannedAt.LocalDateTime:HH:mm}: ненужных файлов не найдено.";
        }
        else
        {
            StatusText.Text = $"Можно очистить {ByteSizeFormatter.Format(totalBytes)}";
            StatusDetails.Text = $"Найдено объектов: {totalCount:N0}. Выберите нужные категории ниже и нажмите «Очистить выбранное».";
        }

        ActivityText.Text = $"Проверено в {_lastScanResult.ScannedAt.LocalDateTime:HH:mm}";
        ActivityResultText.Text = totalCount > 0 ? $"К очистке: {totalCount:N0}" : "Система чиста";
        ActivitySizeText.Text = totalCount > 0
            ? $"Объём: {ByteSizeFormatter.Format(totalBytes)}. Перед удалением будет запрос подтверждения."
            : "Мусорных файлов не обнаружено.";

        CleanButton.IsEnabled = totalCount > 0;
        DetailsButton.IsEnabled = _lastScanResult.UserTempFiles.Count + _lastScanResult.WindowsTempFiles.Count + _lastScanResult.DevCacheFilesOrEmpty.Count > 0;
    }

    private long GetIncludedBytes(ScanResult result) =>
        result.UserTempFiles.Where(f => !_excludedPaths.Contains(f.Path)).Sum(f => f.Bytes) +
        result.WindowsTempFiles.Where(f => !_excludedPaths.Contains(f.Path)).Sum(f => f.Bytes) +
        result.DevCacheFilesOrEmpty.Where(f => !_excludedPaths.Contains(f.Path)).Sum(f => f.Bytes) +
        result.RecycleBin.Bytes;

    private int GetIncludedCount(ScanResult result) =>
        result.UserTempFiles.Count(f => !_excludedPaths.Contains(f.Path)) +
        result.WindowsTempFiles.Count(f => !_excludedPaths.Contains(f.Path)) +
        result.DevCacheFilesOrEmpty.Count(f => !_excludedPaths.Contains(f.Path)) +
        result.RecycleBin.Items;

    private void CleanButton_Click(object? sender, RoutedEventArgs e) => _ = RunCleanAsync();

    private async Task RunCleanAsync()
    {
        if (_isBusy || _lastScanResult is null)
        {
            return;
        }

        var delUser = UserTempCheckBox.IsChecked == true;
        var delSystem = WindowsTempCheckBox.IsChecked == true;
        var delBin = RecycleBinCheckBox.IsChecked == true;
        var delDev = DevCachesCheckBox.IsChecked == true;

        if (!delUser && !delSystem && !delBin && !delDev)
        {
            await _hostContext.Dialogs.ShowInfoAsync("Ничего не выбрано", "Отметьте хотя бы одну категорию для очистки.");
            return;
        }

        if (!_lastScanResult.IsFresh(ScanResultLifetime, DateTimeOffset.Now))
        {
            var rescan = await _hostContext.Dialogs.ConfirmAsync(
                "Результаты проверки устарели",
                "С момента последнего сканирования прошло больше 5 минут. Выполнить быструю перепроверку перед удалением?",
                confirmText: "Перепроверить",
                cancelText: "Продолжить так");
            if (rescan)
            {
                await RunScanAsync();
                if (_lastScanResult is null)
                {
                    return;
                }
            }
        }

        var scopeParts = new List<string>();
        if (delUser) scopeParts.Add("Пользовательские кэши");
        if (delSystem) scopeParts.Add("Системный Temp");
        if (delBin) scopeParts.Add("Корзина");
        if (delDev) scopeParts.Add("Кэши разработки");
        var scopeLabel = string.Join(", ", scopeParts);

        var needsHelperForSystem = delSystem &&
                                   !_hostContext.Elevation.IsElevated &&
                                   _lastScanResult.WindowsTempFiles.Any(f => !_excludedPaths.Contains(f.Path));

        var confirmMsg = $"Будут очищены: {scopeLabel}.\n" +
                         (needsHelperForSystem
                             ? "Для системных файлов будут запрошены права администратора (через DiskPulse.Helper).\n"
                             : string.Empty) +
                         "Продолжить?";

        var confirmed = await _hostContext.Dialogs.ConfirmAsync(
            "Подтверждение очистки",
            confirmMsg,
            confirmText: "Очистить",
            cancelText: "Отмена",
            isDestructive: true);
        if (!confirmed)
        {
            return;
        }

        SetBusyState(true, "Удаляем выбранные файлы...");
        _activeOperationCts?.Dispose();
        _activeOperationCts = new CancellationTokenSource();
        var token = _activeOperationCts.Token;

        try
        {
            var progress = new Progress<CleanupProgress>(p =>
            {
                StatusDetails.Text = $"{p.Stage}: удалено {p.DeletedFiles:N0} ({ByteSizeFormatter.Format(p.ReclaimedBytes)})...";
            });

            // Шаг 1: без повышения прав удаляем пользовательские файлы, dev-кэши, корзину (и системные, только если уже Elevated).
            var localDeleteSystem = delSystem && _hostContext.Elevation.IsElevated;
            var localResult = await _scanService.DeleteAsync(
                _lastScanResult,
                _selectedDrives,
                delUser,
                localDeleteSystem,
                delBin,
                _minimumFileAgeHours,
                token,
                progress,
                _excludedPaths,
                delDev,
                _permanentExclusions);

            var deletedTotal = localResult.DeletedFiles;
            var skippedTotal = localResult.SkippedFiles;
            var reclaimedTotal = localResult.ReclaimedBytes;
            var mergedReasons = new Dictionary<CleanupSkipReason, int>();
            CleanerScanService.MergeSkipReasons(mergedReasons, localResult.SkipReasons);

            string? helperWarning = null;

            // Шаг 2: если отмечены системные временные файлы и прав недостаточно — делегируем в DiskPulse.Helper.
            if (needsHelperForSystem)
            {
                StatusDetails.Text = "Запускаем повышенную очистку системных файлов (DiskPulse.Helper)...";
                var helperResult = await RunElevatedSystemCleanupAsync(_lastScanResult.WindowsTempFiles, token);
                if (helperResult is not null)
                {
                    deletedTotal += helperResult.DeletedFiles;
                    skippedTotal += helperResult.SkippedFiles;
                    reclaimedTotal += helperResult.ReclaimedBytes;
                    CleanerScanService.MergeSkipReasons(mergedReasons, helperResult.SkipReasons);
                }
                else
                {
                    helperWarning = "Повышение прав отменено или DiskPulse.Helper не вернул отчёт — системные файлы пропущены.";
                }
            }

            var entry = new CleanupHistoryEntry(DateTimeOffset.Now, scopeLabel, deletedTotal, reclaimedTotal);
            _historyService.Append(entry);
            _hostContext.Logger.Info($"Очистка ({scopeLabel}): удалено {deletedTotal}, освобождено {ByteSizeFormatter.Format(reclaimedTotal)}.");

            var skipDetails = CleanerScanService.FormatSkipReasons(mergedReasons, _minimumFileAgeHours);
            var report = $"Удалено объектов: {deletedTotal:N0}\nОсвобождено места: {ByteSizeFormatter.Format(reclaimedTotal)}" +
                         (skippedTotal > 0 ? $"\nПропущено: {skippedTotal:N0}" + (string.IsNullOrEmpty(skipDetails) ? "" : $" ({skipDetails})") : string.Empty) +
                         (string.IsNullOrEmpty(helperWarning) ? string.Empty : $"\n\n{helperWarning}");

            await _hostContext.Dialogs.ShowInfoAsync("Очистка завершена", report);

            _lastScanResult = null;
            UserTempValue.Text = "—";
            WindowsTempValue.Text = "—";
            RecycleBinValue.Text = "—";
            UserTempSubtitle.Text = "Очищено";
            WindowsTempSubtitle.Text = "Очищено";
            RecycleBinSubtitle.Text = "Очищено";
            StatusText.Text = "Очистка выполнена";
            StatusDetails.Text = $"Освобождено {ByteSizeFormatter.Format(reclaimedTotal)}. Нажмите «Начать проверку» для повторного анализа.";
            CleanButton.IsEnabled = false;
            DetailsButton.IsEnabled = false;

            LoadLatestHistory();
            await RefreshDiskUsageAsync(forceRefresh: true);
            ScanOrCleanupCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Очистка отменена";
        }
        catch (Exception ex)
        {
            _hostContext.Logger.Error("Ошибка при очистке", ex);
            await _hostContext.Dialogs.ShowErrorAsync("Ошибка очистки", ex.Message);
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private async Task<CleanupResult?> RunElevatedSystemCleanupAsync(IReadOnlyList<ScanFile> systemFiles, CancellationToken cancellationToken)
    {
        var filesToClean = systemFiles
            .Where(f => !_excludedPaths.Contains(f.Path) && !ExclusionListService.IsExcluded(f.Path, _permanentExclusions))
            .Select(f => new HelperCleanupFileItem(f.Path, f.Bytes, f.LastWriteTimeUtc))
            .ToArray();

        if (filesToClean.Length == 0)
        {
            return new CleanupResult(0, 0, 0, new Dictionary<CleanupSkipReason, int>());
        }

        Directory.CreateDirectory(_hostContext.ModuleDataDirectory);
        var id = Guid.NewGuid().ToString("N");
        var reqPath = System.IO.Path.Combine(_hostContext.ModuleDataDirectory, $"helper-clean-req-{id}.json");
        var respPath = System.IO.Path.Combine(_hostContext.ModuleDataDirectory, $"helper-clean-resp-{id}.json");

        try
        {
            var request = new HelperCleanupRequest(filesToClean, _selectedDrives, _minimumFileAgeHours);
            HelperProtocol.WriteJson(reqPath, request);

            await _hostContext.Elevation.RunElevatedHelperAsync(
                ["--cleanup", "--input", reqPath, "--output", respPath],
                cancellationToken);

            var response = HelperProtocol.TryReadResponse(respPath);
            return response is null ? null : HelperProtocol.ToCleanupResult(response);
        }
        finally
        {
            HelperProtocol.TryDeleteFileQuietly(reqPath);
            HelperProtocol.TryDeleteFileQuietly(respPath);
        }
    }

    private void SetBusyState(bool busy, string? statusText = null)
    {
        _isBusy = busy;
        ScanProgressBar.IsVisible = busy;
        ScanButton.IsEnabled = !busy;
        DriveButton.IsEnabled = !busy;
        if (busy)
        {
            CleanButton.IsEnabled = false;
            StatusText.Text = statusText ?? "Выполняется...";
        }
    }

    private IBrush FindBrush(string key, string fallbackHex)
    {
        if (this.TryFindResource(key, out var value) && value is IBrush brush)
        {
            return brush;
        }

        return Brush.Parse(fallbackHex);
    }

    private sealed class FallbackDesignHostContext : IPulseHostContext
    {
        public string ModuleId => "diskpulse";
        public string ModuleDataDirectory => CleanerPaths.GetDefaultStandaloneDataDirectory();
        public string ModuleInstallDirectory => AppContext.BaseDirectory;
        public Version HostVersion => new(0, 1, 0);
        public string ContractVersion => PulseContractVersion.Current;
        public IPulseDialogService Dialogs { get; } = new DesignDialogService();
        public IPulseElevationService Elevation { get; } = new DesignElevationService();
        public IPulseLogger Logger { get; } = new DesignLogger();
        public IPulseSettingsStore Settings { get; } = new DesignSettingsStore();
        public void RequestSummaryRefresh() { }

        private sealed class DesignDialogService : IPulseDialogService
        {
            public Task ShowInfoAsync(string title, string message, CancellationToken ct = default) => Task.CompletedTask;
            public Task ShowErrorAsync(string title, string message, CancellationToken ct = default) => Task.CompletedTask;
            public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена", bool isDestructive = false, CancellationToken ct = default) => Task.FromResult(false);
        }

        private sealed class DesignElevationService : IPulseElevationService
        {
            public bool IsElevated => false;
            public Task<int> RunElevatedHelperAsync(IReadOnlyList<string> args, CancellationToken ct = default) => Task.FromResult(1);
        }

        private sealed class DesignLogger : IPulseLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? exception = null) { }
        }

        private sealed class DesignSettingsStore : IPulseSettingsStore
        {
            public Task<T?> LoadAsync<T>(string key, CancellationToken ct = default) where T : class => Task.FromResult<T?>(null);
            public Task SaveAsync<T>(string key, T value, CancellationToken ct = default) where T : class => Task.CompletedTask;
            public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        }
    }
}
