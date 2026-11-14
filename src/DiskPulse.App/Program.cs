using Avalonia;
using Cleaner;
using DiskPulse.PulseModule;
using PCPulse.Contracts;

namespace Cleaner.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--startup-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            return RunStartupSmokeTest();
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogService.Write("Критическая ошибка запуска DiskPulse", ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static int RunStartupSmokeTest()
    {
        try
        {
            var version = CleanerVersion.Current;
            var dialogs = new SmokeDialogService();
            var context = new StandaloneHostContext(dialogs);
            var module = new DiskPulseModule(isStandaloneHost: true);

            if (module.Descriptor.Id != "diskpulse" || module.Descriptor.ContractVersion != PulseContractVersion.Current)
            {
                return 2;
            }

            module.InitializeAsync(context).GetAwaiter().GetResult();
            var summary = module.GetSummaryAsync().GetAwaiter().GetResult();
            if (summary is null || string.IsNullOrWhiteSpace(summary.Headline))
            {
                return 3;
            }

            var hasDisplay = OperatingSystem.IsWindows() ||
                             !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                             !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

            if (hasDisplay)
            {
                BuildAvaloniaApp().SetupWithoutStarting();
                var view1 = module.CreateView();
                var view2 = module.CreateView();
                if (!ReferenceEquals(view1, view2))
                {
                    return 4;
                }
            }

            module.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"DiskPulse startup smoke test OK ({version}, platform={PulseContractVersion.GetCurrentPlatform()})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Startup smoke test failed: {ex}");
            return 1;
        }
    }

    private sealed class SmokeDialogService : IPulseDialogService
    {
        public Task ShowInfoAsync(string title, string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена", bool isDestructive = false, CancellationToken ct = default) => Task.FromResult(false);
    }
}
