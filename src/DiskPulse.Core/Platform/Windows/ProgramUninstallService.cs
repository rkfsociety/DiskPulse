using System.ComponentModel;
using System.Diagnostics;

namespace Cleaner;

/// <summary>
/// Запускает штатный деинсталлятор программы (Windows) или удаление пакета (Linux).
/// </summary>
public sealed class ProgramUninstallService
{
    private readonly IInstalledProgramsService _programsService;
    private readonly IResidualCleanupService _residuals;

    public ProgramUninstallService()
        : this(PlatformServices.CreateInstalledProgramsService(), PlatformServices.CreateResidualCleanupService())
    {
    }

    public ProgramUninstallService(IInstalledProgramsService programsService, IResidualCleanupService? residuals = null)
    {
        _programsService = programsService;
        _residuals = residuals ?? PlatformServices.CreateResidualCleanupService();
    }

    public ProgramUninstallService(InstalledProgramsService programsService, ResidualCleanupService? residuals = null)
        : this((IInstalledProgramsService)programsService, residuals)
    {
    }

    public static UninstallCommand? Parse(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
        {
            return null;
        }

        var text = uninstallString.Trim();
        string fileName;
        string arguments;

        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing <= 1)
            {
                return null;
            }

            fileName = text[1..closing];
            arguments = text[(closing + 1)..].Trim();
        }
        else
        {
            var executableEnd = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                fileName = text[..(executableEnd + 4)];
                arguments = text[(executableEnd + 4)..].Trim();
            }
            else
            {
                var space = text.IndexOf(' ');
                fileName = space > 0 ? text[..space] : text;
                arguments = space > 0 ? text[(space + 1)..].Trim() : string.Empty;
            }
        }

        fileName = fileName.Trim();
        if (fileName.Length == 0)
        {
            return null;
        }

        if (IsMsiExec(fileName))
        {
            arguments = ToMsiUninstallArguments(arguments);
        }

        return new UninstallCommand(fileName, arguments);
    }

    private static bool IsMsiExec(string fileName)
    {
        try
        {
            return string.Equals(Path.GetFileNameWithoutExtension(fileName), "msiexec", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static string ToMsiUninstallArguments(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length >= 2 && (part[0] == '/' || part[0] == '-') && (part[1] == 'I' || part[1] == 'i'))
            {
                parts[index] = $"/X{part[2..]}";
            }
        }

        return string.Join(' ', parts);
    }

    public async Task<UninstallOutcome> RunAsync(
        InstalledProgram program,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? runElevatedHelperAsync = null,
        string? reportDirectory = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return await RunLinuxUninstallAsync(program, runElevatedHelperAsync, reportDirectory, cancellationToken);
        }

        var command = Parse(program.UninstallString);
        if (command is null)
        {
            return new UninstallOutcome(false, null, true, "В реестре нет команды удаления для этой программы.", _residuals.Find(program), _residuals.FindRegistryEntries(program));
        }

        try
        {
            var startInfo = new ProcessStartInfo(command.FileName)
            {
                Arguments = command.Arguments,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
            {
                startInfo.WorkingDirectory = program.InstallLocation;
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return BuildOutcome(program, false, null, "Не удалось запустить деинсталлятор.");
            }

            await process.WaitForExitAsync(cancellationToken);
            var exitCode = process.ExitCode;
            var stillInstalled = _programsService.IsStillInstalled(program);
            return BuildOutcome(program, true, exitCode, DescribeOutcome(exitCode, stillInstalled));
        }
        catch (Win32Exception exception)
        {
            AppLogService.Write($"Не удалось запустить деинсталлятор: {command.Display}", exception);
            return BuildOutcome(program, false, null, "Система не разрешила запуск деинсталлятора или он не найден.");
        }
        catch (FileNotFoundException exception)
        {
            AppLogService.Write($"Деинсталлятор не найден: {command.Display}", exception);
            return BuildOutcome(program, false, null, "Файл деинсталлятора не найден. Возможно, программа уже удалена частично.");
        }
        catch (InvalidOperationException exception)
        {
            AppLogService.Write($"Ошибка запуска деинсталлятора: {command.Display}", exception);
            return BuildOutcome(program, false, null, "Не удалось запустить деинсталлятор.");
        }
    }

    private async Task<UninstallOutcome> RunLinuxUninstallAsync(
        InstalledProgram program,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? runElevatedHelperAsync,
        string? reportDirectory,
        CancellationToken cancellationToken)
    {
        var manager = program.PackageManager?.Trim().ToLowerInvariant();
        var packageId = program.PackageId?.Trim();

        if (string.IsNullOrWhiteSpace(manager) || string.IsNullOrWhiteSpace(packageId))
        {
            if (program.RegistryScope.Contains(".desktop", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(program.RegistryKeyPath) &&
                File.Exists(program.RegistryKeyPath) &&
                program.RegistryScope.Contains("Текущий пользователь", StringComparison.OrdinalIgnoreCase))
            {
                return BuildOutcome(
                    program,
                    false,
                    null,
                    $"Ярлык пользователя можно удалить вручную: {program.RegistryKeyPath}");
            }

            return BuildOutcome(
                program,
                false,
                null,
                string.IsNullOrWhiteSpace(program.UninstallString)
                    ? "Для этого приложения не найдена команда пакетного удаления."
                    : $"Удалите приложение через пакетный менеджер: {program.UninstallString}");
        }

        // Пользовательский Flatpak можно удалить без повышения прав.
        if (manager == "flatpak" && program.RegistryScope.Contains("Текущий пользователь", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var startInfo = new ProcessStartInfo("flatpak")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("uninstall");
                startInfo.ArgumentList.Add("--user");
                startInfo.ArgumentList.Add("-y");
                startInfo.ArgumentList.Add(packageId);

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return BuildOutcome(program, false, null, "Не удалось запустить flatpak для удаления приложения.");
                }

                await process.WaitForExitAsync(cancellationToken);
                var stillInstalled = _programsService.IsStillInstalled(program);
                return BuildOutcome(program, true, process.ExitCode, DescribeOutcome(process.ExitCode, stillInstalled));
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
            {
                AppLogService.Write($"Не удалось удалить Flatpak-пакет {packageId}", ex);
                return BuildOutcome(program, false, null, $"Не удалось запустить flatpak. Команда вручную: {program.UninstallString}");
            }
        }

        if (runElevatedHelperAsync is null)
        {
            return BuildOutcome(program, false, null, $"Для удаления системного пакета требуются права root. Выполните в терминале: sudo {program.UninstallString}");
        }

        if (!HelperSecurityValidator.IsValidPackageManager(manager) || !HelperSecurityValidator.IsValidPackageId(packageId))
        {
            return BuildOutcome(program, false, null, "Некорректный идентификатор системного пакета.");
        }

        var workingDir = string.IsNullOrWhiteSpace(reportDirectory) ? CleanerPaths.GetDefaultStandaloneDataDirectory() : reportDirectory;
        Directory.CreateDirectory(workingDir);
        var responsePath = Path.Combine(workingDir, $"helper-uninstall-{Guid.NewGuid():N}.json");

        try
        {
            var args = new[]
            {
                "--uninstall-package",
                "--manager", manager,
                "--package", packageId,
                "--output", responsePath
            };

            var exitCode = await runElevatedHelperAsync(args, cancellationToken);
            var response = HelperProtocol.TryReadResponse(responsePath);
            var stillInstalled = _programsService.IsStillInstalled(program);
            var msg = !string.IsNullOrWhiteSpace(response?.Message)
                ? response.Message!
                : DescribeOutcome(exitCode, stillInstalled);
            return BuildOutcome(program, true, exitCode, msg);
        }
        finally
        {
            HelperProtocol.TryDeleteFileQuietly(responsePath);
        }
    }

    private UninstallOutcome BuildOutcome(InstalledProgram program, bool started, int? exitCode, string message)
    {
        return new UninstallOutcome(
            started,
            exitCode,
            _programsService.IsStillInstalled(program),
            message,
            _residuals.Find(program),
            _residuals.FindRegistryEntries(program));
    }

    internal static string DescribeOutcome(int? exitCode, bool stillInstalled)
    {
        if (!stillInstalled)
        {
            return "Программа удалена и больше не числится установленной.";
        }

        return exitCode switch
        {
            0 => "Деинсталлятор завершился без ошибок, но запись о программе ещё осталась. Проверьте список повторно.",
            126 or 127 => "Повышение прав отменено пользователем или утилита удаления недоступна.",
            1602 => "Удаление отменено пользователем.",
            1641 or 3010 => "Программа удалена, требуется перезагрузка компьютера.",
            null => "Деинсталлятор завершился, состояние программы уточните повторной проверкой.",
            _ => $"Деинсталлятор завершился с кодом {exitCode}. Программа осталась в списке установленных."
        };
    }
}
