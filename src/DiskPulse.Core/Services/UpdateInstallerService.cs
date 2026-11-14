using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Cleaner;

public sealed class UpdateInstallerService
{
    private const string InstallDirectory = "DiskPulse";
    private const string ExecutableFileName = "DiskPulse.exe";
    private const string InstallerFileName = "DiskPulse-Setup.exe";
    private const string ShortcutFileName = "DiskPulse.lnk";
    private const string InstallerResourceName = "Cleaner.ApplyUpdate.ps1";

    private static string DataRoot => CleanerPaths.GetDefaultStandaloneDataDirectory();

    public string TargetExecutablePath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", InstallDirectory, ExecutableFileName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "diskpulse", "DiskPulse");

    public string CurrentExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException($"Не удалось определить путь запущенного {ExecutableFileName}.");

    public bool IsInnoInstalled => OperatingSystem.IsWindows() && IsInnoInstalledAt(CurrentExecutablePath);

    public bool IsInstalled => OperatingSystem.IsWindows() && (IsInnoInstalled || PathsEqual(CurrentExecutablePath, TargetExecutablePath));

    internal static bool IsInnoInstalledAt(string executablePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            return !string.IsNullOrEmpty(directory) &&
                File.Exists(Path.Combine(directory, "unins000.exe")) &&
                File.Exists(Path.Combine(directory, "unins000.dat"));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool CanInstallCurrentExecutable =>
        OperatingSystem.IsWindows() &&
        !IsInstalled &&
        File.Exists(CurrentExecutablePath) &&
        string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    public string InstallFailureMarkerPath => Path.Combine(DataRoot, "update-install-error.txt");

    public string StageCurrentExecutable()
    {
        if (!CanInstallCurrentExecutable)
        {
            throw new InvalidOperationException($"Установить можно только автономный {ExecutableFileName}, запущенный не из папки установки.");
        }

        var stagingDirectory = Path.Combine(DataRoot, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagedPath = Path.Combine(stagingDirectory, ExecutableFileName);
        File.Copy(CurrentExecutablePath, stagedPath);
        return stagedPath;
    }

    public void StartInstall(string stagedExecutablePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Автоматическое самообновление exe поддерживается только на Windows.");
        }

        var targetPath = Path.GetFullPath(TargetExecutablePath);
        var packagePath = Path.GetFullPath(stagedExecutablePath);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException($"Не найден подготовленный файл {ExecutableFileName}.", packagePath);
        }

        var stagingRoot = Path.GetFullPath(Path.Combine(DataRoot, "updates"));
        if (!packagePath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Файл установки находится вне временной папки обновлений DiskPulse.");
        }

        var localData = Path.GetFullPath(DataRoot);
        var helperDirectory = Path.Combine(localData, "installer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(helperDirectory);
        var helperPath = Path.Combine(helperDirectory, "apply-update.ps1");
        using (var resource = typeof(UpdateInstallerService).Assembly.GetManifestResourceStream(InstallerResourceName)
            ?? throw new InvalidOperationException("Не найден встроенный помощник установки DiskPulse."))
        using (var reader = new StreamReader(resource, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            File.WriteAllText(helperPath, reader.ReadToEnd(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-STA", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", helperPath,
            "-ProcessId", Environment.ProcessId.ToString(),
            "-PackagePath", packagePath,
            "-TargetPath", targetPath,
            "-ShortcutPath", GetStartMenuShortcutPath(),
            "-FallbackPath", CurrentExecutablePath,
            "-FailureMarkerPath", InstallFailureMarkerPath,
            "-LogPath", Path.Combine(localData, "app.log")
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Не удалось запустить помощник установки DiskPulse.");
            if (process.WaitForExit(500))
            {
                throw new InvalidOperationException($"Помощник установки DiskPulse завершился до начала обновления (код {process.ExitCode}).");
            }
        }
        catch
        {
            DeleteHelperDirectory(helperDirectory);
            throw;
        }
    }

    public void StartInstallerUpdate(string stagedInstallerPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Запуск установщика exe поддерживается только на Windows.");
        }

        var installerPath = Path.GetFullPath(stagedInstallerPath);
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException($"Не найден подготовленный установщик {InstallerFileName}.", installerPath);
        }

        var stagingRoot = Path.GetFullPath(Path.Combine(DataRoot, "updates"));
        if (!installerPath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Файл установщика находится вне временной папки обновлений DiskPulse.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        };
        foreach (var argument in new[] { "/SP-", "/SILENT", "/SUPPRESSMSGBOXES", "/CLOSEAPPLICATIONS", "/relaunch=1" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось запустить установщик обновления DiskPulse.");
    }

    public void CleanupStaleUpdates()
    {
        try
        {
            var updatesRoot = Path.Combine(DataRoot, "updates");
            if (!Directory.Exists(updatesRoot))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var directory in Directory.GetDirectories(updatesRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void DeleteStagedExecutable(string stagedExecutablePath)
    {
        try
        {
            var stagingRoot = Path.GetFullPath(Path.Combine(DataRoot, "updates"));
            var packagePath = Path.GetFullPath(stagedExecutablePath);
            if (!packagePath.StartsWith(stagingRoot + Path.DirectorySeparatorChar, CleanerPaths.PathComparison))
            {
                return;
            }

            var directory = Path.GetDirectoryName(packagePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException exception) { AppLogService.Write("Не удалось удалить подготовленные файлы установки", exception); }
        catch (UnauthorizedAccessException exception) { AppLogService.Write("Нет доступа для удаления подготовленных файлов установки", exception); }
        catch (ArgumentException exception) { AppLogService.Write("Некорректный путь подготовленных файлов установки", exception); }
    }

    public string? ConsumeInstallFailure()
    {
        if (!File.Exists(InstallFailureMarkerPath))
        {
            return null;
        }

        try
        {
            var message = File.ReadAllText(InstallFailureMarkerPath, Encoding.UTF8).Trim();
            File.Delete(InstallFailureMarkerPath);
            return string.IsNullOrWhiteSpace(message) ? "DiskPulse не удалось установить. Запущена предыдущая версия." : message;
        }
        catch (IOException)
        {
            return "DiskPulse не удалось установить. Запущена предыдущая версия.";
        }
        catch (UnauthorizedAccessException)
        {
            return "DiskPulse не удалось установить. Запущена предыдущая версия.";
        }
    }

    private static string GetStartMenuShortcutPath()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return Path.Combine(programs, ShortcutFileName);
    }

    private static void DeleteHelperDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException exception) { AppLogService.Write("Не удалось удалить временный скрипт установки", exception); }
        catch (UnauthorizedAccessException exception) { AppLogService.Write("Нет доступа для удаления временного скрипта установки", exception); }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), CleanerPaths.PathComparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
