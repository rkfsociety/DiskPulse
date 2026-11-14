using System.ComponentModel;
using System.Diagnostics;

namespace Cleaner;

public static class DesktopIntegrationService
{
    public static bool CanOpenSystemAppsSettings => OperatingSystem.IsWindows();

    public static string SystemAppsSettingsLabel =>
        OperatingSystem.IsWindows() ? "Окно Windows" : "Менеджер приложений";

    public static bool OpenInFileManager(string path, bool selectFile)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (selectFile && File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    return true;
                }

                var dir = File.Exists(path) ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                    return true;
                }

                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                var target = Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
                {
                    return false;
                }

                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                psi.ArgumentList.Add(target);
                Process.Start(psi);
                return true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            AppLogService.Write($"Не удалось открыть расположение «{path}»", ex);
        }

        return false;
    }

    public static bool OpenSystemAppsSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            AppLogService.Write("Не удалось открыть параметры приложений Windows", ex);
            return false;
        }
    }

    public static string[] GetRunningBrowserNames()
    {
        var running = new List<string>();
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "chrome", "msedge", "firefox", "brave", "vivaldi", "opera" }
            : new[] { "chrome", "chromium", "chromium-browser", "msedge", "firefox", "firefox-esr", "brave", "brave-browser", "vivaldi", "vivaldi-bin", "opera", "yandex_browser" };

        foreach (var name in candidates)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    running.Add(name);
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var browserProcesses = Process.GetProcessesByName("browser");
            try
            {
                foreach (var process in browserProcesses)
                {
                    try
                    {
                        if (IsYandexBrowserExecutablePath(process.MainModule?.FileName))
                        {
                            running.Add("YandexBrowser");
                            break;
                        }
                    }
                    catch (Win32Exception) { }
                    catch (InvalidOperationException) { }
                    catch (NotSupportedException) { }
                }
            }
            finally
            {
                foreach (var process in browserProcesses)
                {
                    process.Dispose();
                }
            }
        }

        return running.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static bool IsYandexBrowserExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        return executablePath.Contains(@"\Yandex\YandexBrowser\", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains("/yandex/browser", StringComparison.OrdinalIgnoreCase) ||
            executablePath.Contains("/yandex-browser", StringComparison.OrdinalIgnoreCase);
    }
}
