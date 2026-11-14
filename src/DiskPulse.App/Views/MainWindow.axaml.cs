using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cleaner;
using DiskPulse.PulseModule;

namespace Cleaner.App.Views;

public partial class MainWindow : Window
{
    private DiskPulseModule? _module;
    private StandaloneHostContext? _hostContext;
    private bool _isDisposing;

    public MainWindow()
    {
        InitializeComponent();
        TitleVersionText.Text = CleanerVersion.Current.ToString();
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        Opened -= MainWindow_Opened;
        try
        {
            _hostContext = new StandaloneHostContext(DialogHost);
            _module = new DiskPulseModule(isStandaloneHost: true);
            await _module.InitializeAsync(_hostContext);
            ModuleHost.Content = _module.CreateView();

            var installerService = new UpdateInstallerService();
            installerService.CleanupStaleUpdates();
            var installError = installerService.ConsumeInstallFailure();
            if (!string.IsNullOrWhiteSpace(installError))
            {
                await DialogHost.ShowInfoAsync("Установка обновления", installError);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Write("Ошибка инициализации главного окна DiskPulse", ex);
            await DialogHost.ShowErrorAsync("Ошибка запуска", ex.Message);
        }
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_isDisposing || _module is null)
        {
            return;
        }

        _isDisposing = true;
        await _module.DisposeAsync();
        _module = null;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                BeginMoveDrag(e);
            }
        }
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) =>
        Close();
}
