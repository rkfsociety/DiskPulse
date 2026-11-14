using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cleaner;

public sealed record HelperCleanupFileItem(string Path, long Bytes, DateTime? LastWriteTimeUtc = null);

public sealed record HelperCleanupRequest(
    IReadOnlyList<HelperCleanupFileItem> Files,
    IReadOnlyList<string> Drives,
    int MinimumAgeHours,
    bool EmptyRecycleBin = false);

public sealed record HelperResidualCleanupRequest(IReadOnlyList<ResidualFile> Files);

public sealed record HelperCleanupResponse(
    bool Success,
    int DeletedFiles,
    int SkippedFiles,
    long ReclaimedBytes,
    Dictionary<string, int>? SkipReasons = null,
    string? Message = null);

public static class HelperProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void WriteJson<T>(string path, T payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static T? TryReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static HelperCleanupResponse? TryReadResponse(string path) =>
        TryReadJson<HelperCleanupResponse>(path);

    public static CleanupResult ToCleanupResult(HelperCleanupResponse? response)
    {
        if (response is null)
        {
            return new CleanupResult(0, 0, 0, new Dictionary<CleanupSkipReason, int>());
        }

        var reasons = new Dictionary<CleanupSkipReason, int>();
        if (response.SkipReasons is not null)
        {
            foreach (var (key, count) in response.SkipReasons)
            {
                if (Enum.TryParse<CleanupSkipReason>(key, ignoreCase: true, out var reason) && count > 0)
                {
                    reasons[reason] = count;
                }
            }
        }

        return new CleanupResult(response.DeletedFiles, response.SkippedFiles, response.ReclaimedBytes, reasons);
    }

    public static Dictionary<string, int> ConvertSkipReasons(IReadOnlyDictionary<CleanupSkipReason, int>? reasons)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        if (reasons is null)
        {
            return dict;
        }

        foreach (var (reason, count) in reasons)
        {
            if (count > 0 && reason != CleanupSkipReason.None)
            {
                dict[reason.ToString()] = count;
            }
        }

        return dict;
    }

    public static void TryDeleteFileQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class HelperSecurityValidator
{
    private static readonly Regex PackageIdRegex = new(@"^[a-zA-Z0-9][a-zA-Z0-9.+_:~-]{0,127}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AllowedPackageManagers = new(StringComparer.Ordinal)
    {
        "apt", "dpkg", "dnf", "rpm", "pacman", "flatpak"
    };

    public static bool IsValidPackageManager(string? manager) =>
        !string.IsNullOrWhiteSpace(manager) && AllowedPackageManagers.Contains(manager.Trim());

    public static bool IsValidPackageId(string? packageId) =>
        !string.IsNullOrWhiteSpace(packageId) &&
        PackageIdRegex.IsMatch(packageId) &&
        !packageId.StartsWith("-", StringComparison.Ordinal) &&
        !packageId.Contains("..", StringComparison.Ordinal);

    public static bool HasTraversalOrControlCharacters(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.Any(char.IsControl))
        {
            return true;
        }

        var segments = rawPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment is "." or "..");
    }

    public static bool TryValidateDataExchangePath(string? rawPath, bool mustExist, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawPath) || HasTraversalOrControlCharacters(rawPath) || !Path.IsPathFullyQualified(rawPath))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(rawPath);
            if (!fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (HasAnyReparsePointInExistingAncestors(fullPath))
            {
                return false;
            }

            if (mustExist)
            {
                if (!File.Exists(fullPath) || IsReparsePointOrSymlink(fullPath))
                {
                    return false;
                }
            }
            else if (File.Exists(fullPath) && IsReparsePointOrSymlink(fullPath))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> GetAllowedSystemCleanupRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var candidates = new[]
            {
                Path.Combine(windowsFolder, "Temp"),
                Path.Combine(windowsFolder, "Minidump"),
                Path.Combine(windowsFolder, "SoftwareDistribution", "Download"),
                Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
                Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(commonAppData, "Microsoft", "Windows", "WER", "Temp")
            };

            return candidates
                .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
                .Select(CleanerPaths.EnsureTrailingSeparator)
                .Distinct(CleanerPaths.PathComparer)
                .ToArray();
        }

        return new[]
        {
            "/var/tmp",
            "/var/crash",
            "/var/cache/apt/archives",
            "/var/cache/pacman/pkg",
            "/var/cache/dnf"
        }
        .Select(CleanerPaths.EnsureTrailingSeparator)
        .ToArray();
    }

    public static bool TryResolveAllowedCleanupTarget(
        string? rawPath,
        IReadOnlyList<string> allowedRoots,
        out string fullPath,
        out string matchedRoot)
    {
        fullPath = string.Empty;
        matchedRoot = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath) || HasTraversalOrControlCharacters(rawPath) || !Path.IsPathFullyQualified(rawPath))
        {
            return false;
        }

        string resolvedFullPath;
        try
        {
            resolvedFullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var normalizedRoots = allowedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(CleanerPaths.EnsureTrailingSeparator)
            .ToArray();

        matchedRoot = normalizedRoots.FirstOrDefault(r => resolvedFullPath.StartsWith(r, CleanerPaths.PathComparison)) ?? string.Empty;
        fullPath = resolvedFullPath;
        if (string.IsNullOrEmpty(matchedRoot))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            var trimmedRoot = CleanerPaths.TrimTrailingSeparator(matchedRoot);
            if (trimmedRoot == "/var/tmp")
            {
                var rel = Path.GetRelativePath(trimmedRoot, fullPath);
                if (LinuxScanRootsProvider.IsProtectedTempRelativePath(rel))
                {
                    return false;
                }
            }

            if (trimmedRoot == "/var/cache/apt/archives" &&
                string.Equals(Path.GetFileName(fullPath), "lock", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return !HasReparsePointBetween(fullPath, matchedRoot);
    }

    public static bool IsSafeResidualRoot(string? rawRoot)
    {
        if (string.IsNullOrWhiteSpace(rawRoot) || HasTraversalOrControlCharacters(rawRoot) || !Path.IsPathFullyQualified(rawRoot))
        {
            return false;
        }

        try
        {
            var fullRoot = CleanerPaths.TrimTrailingSeparator(Path.GetFullPath(rawRoot));
            var pathRoot = CleanerPaths.TrimTrailingSeparator(Path.GetPathRoot(fullRoot) ?? string.Empty);
            if (string.IsNullOrEmpty(fullRoot) || string.Equals(fullRoot, pathRoot, CleanerPaths.PathComparison))
            {
                return false;
            }

            var relative = Path.GetRelativePath(pathRoot.EndsWith(Path.DirectorySeparatorChar) ? pathRoot : pathRoot + Path.DirectorySeparatorChar, fullRoot);
            var depth = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Length;
            if (depth < 2)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                var windows = CleanerPaths.TrimTrailingSeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (!string.IsNullOrEmpty(windows) &&
                    (string.Equals(fullRoot, windows, StringComparison.OrdinalIgnoreCase) ||
                     fullRoot.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }
            }
            else
            {
                foreach (var protectedDir in new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64", "/etc", "/boot", "/dev", "/proc", "/sys" })
                {
                    if (string.Equals(fullRoot, protectedDir, StringComparison.Ordinal) ||
                        fullRoot.StartsWith(protectedDir + "/", StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return !HasAnyReparsePointInExistingAncestors(fullRoot) && !IsReparsePointOrSymlink(fullRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool TryValidateResidualFile(string? rawFilePath, string? rawRoot, out string fullPath, out string fullRoot)
    {
        fullPath = string.Empty;
        fullRoot = string.Empty;

        if (!IsSafeResidualRoot(rawRoot) || string.IsNullOrWhiteSpace(rawFilePath) || HasTraversalOrControlCharacters(rawFilePath) || !Path.IsPathFullyQualified(rawFilePath))
        {
            return false;
        }

        try
        {
            fullRoot = CleanerPaths.EnsureTrailingSeparator(Path.GetFullPath(rawRoot!));
            fullPath = Path.GetFullPath(rawFilePath!);
            if (!fullPath.StartsWith(fullRoot, CleanerPaths.PathComparison))
            {
                return false;
            }

            return !HasReparsePointBetween(fullPath, fullRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasReparsePointBetween(string fullPath, string fullRootWithSeparator)
    {
        try
        {
            var trimmedRoot = CleanerPaths.TrimTrailingSeparator(fullRootWithSeparator);
            var current = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && IsReparsePointOrSymlink(current))
                {
                    return true;
                }

                if (string.Equals(CleanerPaths.TrimTrailingSeparator(current), trimmedRoot, CleanerPaths.PathComparison))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }

            if (File.Exists(fullPath) && IsReparsePointOrSymlink(fullPath))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool HasAnyReparsePointInExistingAncestors(string fullPath)
    {
        try
        {
            var current = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && IsReparsePointOrSymlink(current))
                {
                    return true;
                }

                current = Path.GetDirectoryName(current);
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsReparsePointOrSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists && !Directory.Exists(path))
            {
                return false;
            }

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                return dir.LinkTarget is not null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint);
            }

            return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
