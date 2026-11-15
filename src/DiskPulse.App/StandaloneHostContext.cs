using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Cleaner;
using PCPulse.Contracts;

namespace Cleaner.App;

internal sealed class StandaloneHostContext : IPulseHostContext
{
    public StandaloneHostContext(IPulseDialogService dialogs)
    {
        ModuleDataDirectory = CleanerPaths.GetDefaultStandaloneDataDirectory();
        Directory.CreateDirectory(ModuleDataDirectory);
        AppLogService.Configure(ModuleDataDirectory);

        Dialogs = dialogs;
        Elevation = new StandaloneElevationService();
        Logger = new StandaloneLogger();
        Settings = new StandaloneSettingsStore(Path.Combine(ModuleDataDirectory, "module-settings"));
    }

    public string ModuleId => "diskpulse";
    public string ModuleDataDirectory { get; }
    public string ModuleInstallDirectory => AppContext.BaseDirectory;
    public Version HostVersion => new(CleanerVersion.Current.Major, CleanerVersion.Current.Minor, CleanerVersion.Current.Patch);
    public string ContractVersion => PulseContractVersion.Current;
    public IPulseDialogService Dialogs { get; }
    public IPulseElevationService Elevation { get; }
    public IPulseLogger Logger { get; }
    public IPulseSettingsStore Settings { get; }

    public event Action? SummaryRefreshRequested;

    public void RequestSummaryRefresh() => SummaryRefreshRequested?.Invoke();

    private sealed class StandaloneLogger : IPulseLogger
    {
        public void Info(string message) => AppLogService.Write($"[INFO] {message}");
        public void Warn(string message) => AppLogService.Write($"[WARN] {message}");
        public void Error(string message, Exception? exception = null)
        {
            if (exception is null)
            {
                AppLogService.Write($"[ERROR] {message}");
            }
            else
            {
                AppLogService.Write($"[ERROR] {message}", exception);
            }
        }
    }

    private sealed class StandaloneSettingsStore(string settingsDir) : IPulseSettingsStore
    {
        private readonly string _settingsDir = settingsDir;

        public Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveKeyPath(key);
            if (!File.Exists(path))
            {
                return Task.FromResult<T?>(null);
            }

            try
            {
                var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path));
                return Task.FromResult(value);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<T?>(null);
            }
        }

        public Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_settingsDir);
            var path = ResolveKeyPath(key);
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveKeyPath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }

        private string ResolveKeyPath(string key)
        {
            var safe = string.Concat(key.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
            return Path.Combine(_settingsDir, $"{safe}.json");
        }
    }

    private sealed class StandaloneElevationService : IPulseElevationService
    {
        public bool IsElevated => Environment.IsPrivilegedProcess;

        public async Task<int> RunElevatedHelperAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
        {
            var helperName = OperatingSystem.IsWindows() ? "DiskPulse.Helper.exe" : "DiskPulse.Helper";
            var processDir = Path.GetDirectoryName(Environment.ProcessPath);
            var candidateDirs = new[] { processDir, AppContext.BaseDirectory }
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(CleanerPaths.PathComparer);

            var helperPath = candidateDirs
                .Select(d => Path.Combine(d!, helperName))
                .FirstOrDefault(File.Exists);

            if (string.IsNullOrEmpty(helperPath))
            {
                AppLogService.Write($"Не найден {helperName} рядом с приложением.");
                return -1;
            }

            try
            {
                var startInfo = OperatingSystem.IsWindows()
                    ? CreateWindowsStartInfo(helperPath, args)
                    : CreateLinuxStartInfo(helperPath, args);

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return -1;
                }

                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                AppLogService.Write("Пользователь отменил запрос UAC для DiskPulse.Helper.");
                return 1223;
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
            {
                AppLogService.Write("Ошибка запуска повышенного DiskPulse.Helper", ex);
                return -1;
            }
        }

        private static ProcessStartInfo CreateWindowsStartInfo(string helperPath, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? AppContext.BaseDirectory
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }

        private static ProcessStartInfo CreateLinuxStartInfo(string helperPath, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo("pkexec")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(helperPath);
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }
    }
}
