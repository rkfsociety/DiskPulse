using System.ComponentModel;
using System.Diagnostics;

namespace Cleaner;

internal static class HelperProgram
{
    public static int Main(string[] args)
    {
        return Run(args, HelperSecurityValidator.GetAllowedSystemCleanupRoots());
    }

    internal static int Run(string[] args, IReadOnlyList<string> allowedCleanupRoots)
    {
        if (args.Length == 0)
        {
            return 2;
        }

        var mode = args[0];
        var options = ParseOptions(args.Skip(1).ToArray());
        if (options is null)
        {
            return 2;
        }

        return mode switch
        {
            "--cleanup" => HandleCleanup(options, allowedCleanupRoots),
            "--cleanup-residuals" => HandleCleanupResiduals(options),
            "--uninstall-package" => HandleUninstallPackage(options),
            _ => 2
        };
    }

    private static Dictionary<string, string>? ParseOptions(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return null;
            }

            dict[key] = args[++i];
        }

        return dict;
    }

    private static int HandleCleanup(Dictionary<string, string> options, IReadOnlyList<string> allowedCleanupRoots)
    {
        if (!options.TryGetValue("--input", out var rawInput) ||
            !options.TryGetValue("--output", out var rawOutput) ||
            !HelperSecurityValidator.TryValidateDataExchangePath(rawInput, mustExist: true, out var inputPath) ||
            !HelperSecurityValidator.TryValidateDataExchangePath(rawOutput, mustExist: false, out var outputPath))
        {
            return 2;
        }

        var request = HelperProtocol.TryReadJson<HelperCleanupRequest>(inputPath);
        if (request is null)
        {
            HelperProtocol.WriteJson(outputPath, new HelperCleanupResponse(false, 0, 0, 0, Message: "Не удалось прочитать запрос очистки."));
            return 2;
        }

        var response = ExecuteCleanup(request, allowedCleanupRoots);
        HelperProtocol.WriteJson(outputPath, response);
        return response.Success ? 0 : 1;
    }

    internal static HelperCleanupResponse ExecuteCleanup(HelperCleanupRequest request, IReadOnlyList<string> allowedRoots)
    {
        var safeDelete = PlatformServices.CreateSafeDeleteService();
        var fullRoots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(CleanerPaths.EnsureTrailingSeparator)
            .Distinct(CleanerPaths.PathComparer)
            .ToArray();

        DateTime? thresholdUtc = request.MinimumAgeHours > 0
            ? DateTime.UtcNow.AddHours(-Math.Clamp(request.MinimumAgeHours, 0, 168))
            : null;

        var deleted = 0;
        var skipped = 0;
        long reclaimed = 0;
        var skipReasons = new Dictionary<CleanupSkipReason, int>();
        var touchedDirectories = new HashSet<string>(CleanerPaths.PathComparer);

        void RecordSkip(CleanupSkipReason reason)
        {
            skipped++;
            skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
        }

        foreach (var file in request.Files)
        {
            if (!HelperSecurityValidator.TryResolveAllowedCleanupTarget(file.Path, fullRoots, out var fullPath, out var matchedRoot))
            {
                RecordSkip(CleanupSkipReason.AccessDenied);
                continue;
            }

            if (thresholdUtc is { } threshold && file.LastWriteTimeUtc is { } lastWrite && lastWrite > threshold)
            {
                RecordSkip(CleanupSkipReason.TooNew);
                continue;
            }

            var attempt = safeDelete.TryDelete(fullPath, matchedRoot, thresholdUtc);
            if (!attempt.Succeeded)
            {
                var reason = attempt.Reason == CleanupSkipReason.Changed && thresholdUtc is not null
                    ? CleanupSkipReason.TooNew
                    : attempt.Reason;
                RecordSkip(reason);
                continue;
            }

            deleted++;
            reclaimed += Math.Max(0, file.Bytes);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
            {
                touchedDirectories.Add(parent);
            }
        }

        CleanerScanService.RemoveEmptySubdirectories(touchedDirectories, fullRoots);

        if (request.EmptyRecycleBin)
        {
            var recycleBin = PlatformServices.CreateRecycleBinService();
            var drives = request.Drives is { Count: > 0 } ? CleanerScanService.NormalizeDrives(request.Drives) : [PlatformServices.CreateStorageVolumeService().GetSystemDriveRoot()];
            var before = recycleBin.GetInfoPerRoot(drives);
            foreach (var root in drives)
            {
                var info = before.TryGetValue(root, out var v) ? v : new RecycleBinInfo(0, 0);
                if (recycleBin.Empty(root))
                {
                    deleted += info.Items;
                    reclaimed += info.Bytes;
                }
                else if (info.Items > 0)
                {
                    skipped += info.Items;
                    skipReasons[CleanupSkipReason.AccessDenied] = skipReasons.GetValueOrDefault(CleanupSkipReason.AccessDenied) + info.Items;
                }
            }
        }

        return new HelperCleanupResponse(
            Success: true,
            DeletedFiles: deleted,
            SkippedFiles: skipped,
            ReclaimedBytes: reclaimed,
            SkipReasons: HelperProtocol.ConvertSkipReasons(skipReasons));
    }

    private static int HandleCleanupResiduals(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--input", out var rawInput) ||
            !options.TryGetValue("--output", out var rawOutput) ||
            !HelperSecurityValidator.TryValidateDataExchangePath(rawInput, mustExist: true, out var inputPath) ||
            !HelperSecurityValidator.TryValidateDataExchangePath(rawOutput, mustExist: false, out var outputPath))
        {
            return 2;
        }

        var request = HelperProtocol.TryReadJson<HelperResidualCleanupRequest>(inputPath);
        if (request is null)
        {
            HelperProtocol.WriteJson(outputPath, new HelperCleanupResponse(false, 0, 0, 0, Message: "Не удалось прочитать запрос удаления остатков."));
            return 2;
        }

        var response = ExecuteCleanupResiduals(request);
        HelperProtocol.WriteJson(outputPath, response);
        return response.Success ? 0 : 1;
    }

    internal static HelperCleanupResponse ExecuteCleanupResiduals(HelperResidualCleanupRequest request)
    {
        var safeDelete = PlatformServices.CreateSafeDeleteService();
        var deleted = 0;
        var skipped = 0;
        long reclaimed = 0;
        var skipReasons = new Dictionary<CleanupSkipReason, int>();
        var validDeletedFiles = new List<ResidualFile>();

        foreach (var file in request.Files)
        {
            if (!HelperSecurityValidator.TryValidateResidualFile(file.Path, file.Root, out var fullPath, out var fullRoot))
            {
                skipped++;
                skipReasons[CleanupSkipReason.AccessDenied] = skipReasons.GetValueOrDefault(CleanupSkipReason.AccessDenied) + 1;
                continue;
            }

            var attempt = safeDelete.TryDelete(fullPath, fullRoot);
            if (attempt.Succeeded)
            {
                deleted++;
                reclaimed += Math.Max(0, file.Size);
                validDeletedFiles.Add(new ResidualFile(fullPath, file.Size, fullRoot));
            }
            else
            {
                skipped++;
                skipReasons[attempt.Reason] = skipReasons.GetValueOrDefault(attempt.Reason) + 1;
            }
        }

        ResidualCleanupService.RemoveEmptyDirectories(validDeletedFiles);

        return new HelperCleanupResponse(
            Success: true,
            DeletedFiles: deleted,
            SkippedFiles: skipped,
            ReclaimedBytes: reclaimed,
            SkipReasons: HelperProtocol.ConvertSkipReasons(skipReasons));
    }

    private static int HandleUninstallPackage(Dictionary<string, string> options)
    {
        if (OperatingSystem.IsWindows())
        {
            return 2;
        }

        if (!options.TryGetValue("--manager", out var manager) ||
            !options.TryGetValue("--package", out var packageId) ||
            !options.TryGetValue("--output", out var rawOutput) ||
            !HelperSecurityValidator.IsValidPackageManager(manager) ||
            !HelperSecurityValidator.IsValidPackageId(packageId) ||
            !HelperSecurityValidator.TryValidateDataExchangePath(rawOutput, mustExist: false, out var outputPath))
        {
            return 2;
        }

        var normalizedManager = manager.Trim().ToLowerInvariant();
        var normalizedPackage = packageId.Trim();

        (string FileName, string[] Args) command = normalizedManager switch
        {
            "apt" => ("apt-get", ["remove", "-y", "--", normalizedPackage]),
            "dpkg" => ("dpkg", ["--remove", "--", normalizedPackage]),
            "dnf" => ("dnf", ["remove", "-y", "--", normalizedPackage]),
            "rpm" => File.Exists("/usr/bin/dnf")
                ? ("dnf", ["remove", "-y", "--", normalizedPackage])
                : ("rpm", ["-e", "--", normalizedPackage]),
            "pacman" => ("pacman", ["-R", "--noconfirm", "--", normalizedPackage]),
            "flatpak" => ("flatpak", ["uninstall", "-y", "--noninteractive", "--", normalizedPackage]),
            _ => (string.Empty, [])
        };

        if (string.IsNullOrEmpty(command.FileName))
        {
            return 2;
        }

        try
        {
            var psi = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            psi.Environment["LC_ALL"] = "C";
            foreach (var arg in command.Args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                HelperProtocol.WriteJson(outputPath, new HelperCleanupResponse(false, 0, 0, 0, Message: $"Не удалось запустить {command.FileName}."));
                return 1;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            var ok = process.ExitCode == 0;
            var msg = ok
                ? $"Пакет {normalizedPackage} удалён."
                : $"Ошибка {command.FileName} (код {process.ExitCode}): {stderr.Trim()}";
            HelperProtocol.WriteJson(outputPath, new HelperCleanupResponse(ok, ok ? 1 : 0, 0, 0, Message: msg));
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            HelperProtocol.WriteJson(outputPath, new HelperCleanupResponse(false, 0, 0, 0, Message: $"Не удалось выполнить удаление через {command.FileName}: {ex.Message}"));
            return 1;
        }
    }
}
