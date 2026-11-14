using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Linq;

namespace Cleaner;

/// <summary>
/// Читает установленные программы на Linux из dpkg/rpm/pacman/flatpak и .desktop-файлов.
/// </summary>
public sealed class LinuxInstalledProgramsService : IInstalledProgramsService
{
    internal sealed record LinuxPackageInfo(
        string Manager,
        string PackageId,
        string Name,
        string Version,
        long EstimatedBytes,
        string Publisher,
        string Architecture,
        string Scope,
        DateTimeOffset? InstalledAt,
        string InstallLocation = "",
        string UninstallCommand = "");

    internal sealed record DesktopAppEntry(
        string Name,
        string ExecBinary,
        string ExecPath,
        string DesktopFilePath,
        string Scope,
        string? FlatpakAppId,
        DateTimeOffset? ModifiedAt);

    public Task<IReadOnlyList<InstalledProgram>> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledProgram>>(() => Load(cancellationToken), cancellationToken);
    }

    public IReadOnlyList<InstalledProgram> Load(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        var packages = LoadInstalledPackages(cancellationToken);
        var desktopEntries = LoadDesktopEntries(cancellationToken);
        var recentUsage = ReadRecentlyUsedTimestamps();
        var programs = MergeDesktopAndPackages(desktopEntries, packages, recentUsage);

        return InstalledProgramsService.Sort(programs, ProgramSortMode.LeastRecentlyUsed);
    }

    public bool IsStillInstalled(InstalledProgram program)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var manager = program.PackageManager?.Trim().ToLowerInvariant();
        var packageId = program.PackageId?.Trim();

        if (!string.IsNullOrWhiteSpace(manager) && !string.IsNullOrWhiteSpace(packageId))
        {
            return manager switch
            {
                "dpkg" => RunCommandExitZero("dpkg-query", ["-W", "-f=${Status}", packageId], out var stdout) &&
                          stdout.Contains("install ok installed", StringComparison.OrdinalIgnoreCase),
                "rpm" => RunCommandExitZero("rpm", ["-q", packageId], out _),
                "pacman" => RunCommandExitZero("pacman", ["-Q", packageId], out _),
                "flatpak" => RunCommandExitZero("flatpak", ["info", packageId], out _),
                _ => false
            };
        }

        return !string.IsNullOrWhiteSpace(program.RegistryKeyPath) && File.Exists(program.RegistryKeyPath);
    }

    internal static IReadOnlyList<InstalledProgram> MergeDesktopAndPackages(
        IReadOnlyList<DesktopAppEntry> desktopEntries,
        IReadOnlyList<LinuxPackageInfo> packages,
        IReadOnlyDictionary<string, DateTimeOffset> recentUsage)
    {
        var programs = new List<InstalledProgram>();
        var matchedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var packageByName = new Dictionary<string, LinuxPackageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packages)
        {
            packageByName.TryAdd(pkg.PackageId, pkg);
            packageByName.TryAdd(pkg.Name, pkg);
        }

        foreach (var desktop in desktopEntries)
        {
            var pkg = FindMatchingPackage(desktop, packageByName, packages);
            if (pkg is not null)
            {
                matchedPackages.Add($"{pkg.Manager}:{pkg.PackageId}");
            }

            var key = $"{desktop.Name}|{pkg?.PackageId ?? desktop.ExecBinary}";
            if (!seenApps.Add(key))
            {
                continue;
            }

            var (lastUsed, source) = ResolveLastUsed(desktop, pkg, recentUsage);
            var installLocation = ResolveInstallLocation(desktop, pkg);
            var scope = pkg?.Scope ?? desktop.Scope;
            if (!string.IsNullOrWhiteSpace(pkg?.Architecture) && !scope.Contains(pkg.Architecture, StringComparison.OrdinalIgnoreCase))
            {
                scope = $"{scope} ({pkg.Architecture})";
            }

            programs.Add(new InstalledProgram(
                Name: desktop.Name,
                Publisher: pkg?.Publisher ?? string.Empty,
                Version: pkg?.Version ?? string.Empty,
                EstimatedBytes: pkg?.EstimatedBytes ?? 0,
                InstalledAt: pkg?.InstalledAt ?? desktop.ModifiedAt,
                LastUsedAt: lastUsed,
                LastUsedSource: source,
                InstallLocation: installLocation,
                RegistryScope: scope,
                UninstallString: pkg?.UninstallCommand ?? string.Empty,
                RegistryKeyPath: desktop.DesktopFilePath,
                PackageManager: pkg?.Manager,
                PackageId: pkg?.PackageId));
        }

        // Добавляем Flatpak GUI-приложения, даже если .desktop не попал в стандартный каталог.
        foreach (var pkg in packages.Where(p => p.Manager == "flatpak" && !matchedPackages.Contains($"{p.Manager}:{p.PackageId}")))
        {
            var key = $"{pkg.Name}|{pkg.PackageId}";
            if (!seenApps.Add(key))
            {
                continue;
            }

            var (lastUsed, source) = ResolveLastUsed(null, pkg, recentUsage);
            programs.Add(new InstalledProgram(
                Name: pkg.Name,
                Publisher: pkg.Publisher,
                Version: pkg.Version,
                EstimatedBytes: pkg.EstimatedBytes,
                InstalledAt: pkg.InstalledAt,
                LastUsedAt: lastUsed,
                LastUsedSource: source,
                InstallLocation: pkg.InstallLocation,
                RegistryScope: pkg.Scope,
                UninstallString: pkg.UninstallCommand,
                RegistryKeyPath: $"flatpak:{pkg.PackageId}",
                PackageManager: pkg.Manager,
                PackageId: pkg.PackageId));
        }

        return programs;
    }

    private static LinuxPackageInfo? FindMatchingPackage(
        DesktopAppEntry desktop,
        Dictionary<string, LinuxPackageInfo> packageByName,
        IReadOnlyList<LinuxPackageInfo> packages)
    {
        if (!string.IsNullOrWhiteSpace(desktop.FlatpakAppId) &&
            packageByName.TryGetValue(desktop.FlatpakAppId, out var flatpakPkg))
        {
            return flatpakPkg;
        }

        if (!string.IsNullOrWhiteSpace(desktop.ExecBinary))
        {
            if (packageByName.TryGetValue(desktop.ExecBinary, out var direct))
            {
                return direct;
            }

            var withoutDesktop = Path.GetFileNameWithoutExtension(desktop.DesktopFilePath);
            if (!string.IsNullOrWhiteSpace(withoutDesktop) && packageByName.TryGetValue(withoutDesktop, out var byDesktopName))
            {
                return byDesktopName;
            }
        }

        var normalizedAppName = desktop.Name.Replace(" ", "-", StringComparison.Ordinal);
        if (packageByName.TryGetValue(normalizedAppName, out var byTitle))
        {
            return byTitle;
        }

        return packages.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(desktop.ExecBinary) &&
            p.PackageId.Contains(desktop.ExecBinary, StringComparison.OrdinalIgnoreCase) &&
            p.PackageId.Length <= desktop.ExecBinary.Length + 12);
    }

    private static (DateTimeOffset? LastUsed, string Source) ResolveLastUsed(
        DesktopAppEntry? desktop,
        LinuxPackageInfo? pkg,
        IReadOnlyDictionary<string, DateTimeOffset> recentUsage)
    {
        if (desktop is not null && !string.IsNullOrWhiteSpace(desktop.ExecBinary) &&
            recentUsage.TryGetValue(desktop.ExecBinary, out var usedAt))
        {
            return (usedAt, "журнал файлов");
        }

        if (pkg is not null && recentUsage.TryGetValue(pkg.PackageId, out var pkgUsedAt))
        {
            return (pkgUsedAt, "журнал файлов");
        }

        if (pkg?.InstalledAt is { } installedAt)
        {
            return (installedAt, "дата установки");
        }

        if (desktop?.ModifiedAt is { } modifiedAt)
        {
            return (modifiedAt, "ярлык .desktop");
        }

        return (null, "нет данных");
    }

    private static string ResolveInstallLocation(DesktopAppEntry desktop, LinuxPackageInfo? pkg)
    {
        if (!string.IsNullOrWhiteSpace(pkg?.InstallLocation) && Directory.Exists(pkg.InstallLocation))
        {
            return pkg.InstallLocation;
        }

        if (!string.IsNullOrWhiteSpace(desktop.ExecPath))
        {
            try
            {
                var dir = Path.GetDirectoryName(desktop.ExecPath);
                if (!string.IsNullOrWhiteSpace(dir) && dir != "/usr/bin" && dir != "/bin" && Directory.Exists(dir))
                {
                    return dir;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        try
        {
            return Path.GetDirectoryName(desktop.DesktopFilePath) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<DesktopAppEntry> LoadDesktopEntries(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var localShare = !string.IsNullOrWhiteSpace(xdgDataHome) ? xdgDataHome : Path.Combine(home, ".local", "share");

        var searchDirs = new (string Path, string Scope)[]
        {
            (Path.Combine(localShare, "applications"), "Текущий пользователь (.desktop)"),
            (Path.Combine(localShare, "flatpak", "exports", "share", "applications"), "Текущий пользователь (Flatpak)"),
            ("/var/lib/flatpak/exports/share/applications", "Все пользователи (Flatpak)"),
            ("/usr/local/share/applications", "Все пользователи"),
            ("/usr/share/applications", "Все пользователи"),
            ("/var/lib/snapd/desktop/applications", "Все пользователи (Snap)")
        };

        var entries = new List<DesktopAppEntry>();
        foreach (var (dir, scope) in searchDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var lines = File.ReadAllLines(file);
                        var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                        var entry = ParseDesktopFile(lines, file, scope, modified);
                        if (entry is not null)
                        {
                            entries.Add(entry);
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return entries;
    }

    internal static DesktopAppEntry? ParseDesktopFile(IEnumerable<string> lines, string filePath, string scope, DateTimeOffset? modifiedAt = null)
    {
        var inDesktopEntry = false;
        string? name = null;
        string? type = null;
        string? exec = null;
        string? flatpakId = null;
        var noDisplay = false;
        var hidden = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (inDesktopEntry)
                {
                    break;
                }

                inDesktopEntry = string.Equals(line, "[Desktop Entry]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inDesktopEntry)
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (key.Equals("Name", StringComparison.Ordinal))
            {
                name = val;
            }
            else if (key.Equals("Type", StringComparison.Ordinal))
            {
                type = val;
            }
            else if (key.Equals("Exec", StringComparison.Ordinal))
            {
                exec = val;
            }
            else if (key.Equals("X-Flatpak", StringComparison.Ordinal))
            {
                flatpakId = val;
            }
            else if (key.Equals("NoDisplay", StringComparison.Ordinal))
            {
                noDisplay = val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key.Equals("Hidden", StringComparison.Ordinal))
            {
                hidden = val.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (noDisplay || hidden || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(type) && !type.Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var (execPath, execBinary) = ExtractExecCommand(exec);
        if (string.IsNullOrWhiteSpace(flatpakId) && filePath.Contains("flatpak", StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (fileName.Contains('.'))
            {
                flatpakId = fileName;
            }
        }

        return new DesktopAppEntry(name, execBinary, execPath, filePath, scope, flatpakId, modifiedAt);
    }

    internal static (string ExecPath, string ExecBinary) ExtractExecCommand(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec))
        {
            return (string.Empty, string.Empty);
        }

        var trimmed = exec.Trim();
        string cmd;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            cmd = end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }
        else
        {
            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            cmd = tokens.FirstOrDefault(t => !t.Contains('=') && t != "env") ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(cmd))
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            return (cmd, Path.GetFileName(cmd));
        }
        catch (ArgumentException)
        {
            return (cmd, cmd);
        }
    }

    private static IReadOnlyList<LinuxPackageInfo> LoadInstalledPackages(CancellationToken cancellationToken)
    {
        var results = new List<LinuxPackageInfo>();

        cancellationToken.ThrowIfCancellationRequested();
        if (RunCommandExitZero("dpkg-query", ["-W", "-f=${Package}\t${Version}\t${Installed-Size}\t${Maintainer}\t${Architecture}\t${Status}\n"], out var dpkgOut))
        {
            results.AddRange(ParseDpkgOutput(dpkgOut));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RunCommandExitZero("rpm", ["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{SIZE}\t%{VENDOR}\t%{INSTALLTIME}\t%{ARCH}\n"], out var rpmOut))
        {
            results.AddRange(ParseRpmOutput(rpmOut));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RunCommandExitZero("pacman", ["-Q"], out var pacmanOut))
        {
            results.AddRange(ParsePacmanOutput(pacmanOut));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (RunCommandExitZero("flatpak", ["list", "--app", "--columns=application,name,version,size,installation"], out var flatpakOut))
        {
            results.AddRange(ParseFlatpakOutput(flatpakOut));
        }

        return results;
    }

    internal static IReadOnlyList<LinuxPackageInfo> ParseDpkgOutput(string output)
    {
        var list = new List<LinuxPackageInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length < 6)
            {
                continue;
            }

            if (!cols[5].Contains("installed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var packageId = cols[0].Trim();
            var version = cols[1].Trim();
            long.TryParse(cols[2].Trim(), out var sizeKb);
            var maintainer = NormalizePublisher(cols[3]);
            var arch = cols[4].Trim();

            list.Add(new LinuxPackageInfo(
                Manager: "dpkg",
                PackageId: packageId,
                Name: packageId,
                Version: version,
                EstimatedBytes: Math.Max(0, sizeKb) * 1024L,
                Publisher: maintainer,
                Architecture: arch,
                Scope: $"APT / dpkg ({arch})",
                InstalledAt: null,
                UninstallCommand: $"apt remove {packageId}"));
        }

        return list;
    }

    internal static IReadOnlyList<LinuxPackageInfo> ParseRpmOutput(string output)
    {
        var list = new List<LinuxPackageInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length < 6)
            {
                continue;
            }

            var packageId = cols[0].Trim();
            var version = cols[1].Trim();
            long.TryParse(cols[2].Trim(), out var sizeBytes);
            var vendor = NormalizePublisher(cols[3]);
            DateTimeOffset? installedAt = long.TryParse(cols[4].Trim(), out var unix) && unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : null;
            var arch = cols[5].Trim();

            list.Add(new LinuxPackageInfo(
                Manager: "rpm",
                PackageId: packageId,
                Name: packageId,
                Version: version,
                EstimatedBytes: Math.Max(0, sizeBytes),
                Publisher: vendor,
                Architecture: arch,
                Scope: $"RPM ({arch})",
                InstalledAt: installedAt,
                UninstallCommand: $"dnf remove {packageId}"));
        }

        return list;
    }

    internal static IReadOnlyList<LinuxPackageInfo> ParsePacmanOutput(string output)
    {
        var list = new List<LinuxPackageInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var packageId = parts[0].Trim();
            var version = parts[1].Trim();

            list.Add(new LinuxPackageInfo(
                Manager: "pacman",
                PackageId: packageId,
                Name: packageId,
                Version: version,
                EstimatedBytes: 0,
                Publisher: string.Empty,
                Architecture: "x64",
                Scope: "Pacman",
                InstalledAt: null,
                UninstallCommand: $"pacman -R {packageId}"));
        }

        return list;
    }

    internal static IReadOnlyList<LinuxPackageInfo> ParseFlatpakOutput(string output)
    {
        var list = new List<LinuxPackageInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length < 4)
            {
                continue;
            }

            var appId = cols[0].Trim();
            var name = string.IsNullOrWhiteSpace(cols[1]) ? appId : cols[1].Trim();
            var version = cols[2].Trim();
            var sizeBytes = ParseHumanSize(cols[3]);
            var installation = cols.Length > 4 ? cols[4].Trim() : "system";
            var isUser = installation.Equals("user", StringComparison.OrdinalIgnoreCase);

            list.Add(new LinuxPackageInfo(
                Manager: "flatpak",
                PackageId: appId,
                Name: name,
                Version: version,
                EstimatedBytes: sizeBytes,
                Publisher: appId.Contains('.') ? appId[..appId.LastIndexOf('.')] : string.Empty,
                Architecture: "x64",
                Scope: isUser ? "Текущий пользователь (Flatpak)" : "Все пользователи (Flatpak)",
                InstalledAt: null,
                UninstallCommand: isUser ? $"flatpak uninstall --user {appId}" : $"flatpak uninstall {appId}"));
        }

        return list;
    }

    internal static long ParseHumanSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var cleaned = text.Replace('\u00A0', ' ').Trim();
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return 0;
        }

        var numStr = parts[0].Replace(',', '.');
        if (!double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "B";
        var multiplier = unit switch
        {
            "KB" or "KIB" or "КБ" => 1024d,
            "MB" or "MIB" or "МБ" => 1024d * 1024d,
            "GB" or "GIB" or "ГБ" => 1024d * 1024d * 1024d,
            _ => 1d
        };

        return Math.Max(0, (long)(value * multiplier));
    }

    internal static IReadOnlyDictionary<string, DateTimeOffset> ReadRecentlyUsedTimestamps(string? customXbelPath = null)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xbelPath = customXbelPath ?? (!string.IsNullOrWhiteSpace(xdgData)
            ? Path.Combine(xdgData, "recently-used.xbel")
            : Path.Combine(home, ".local", "share", "recently-used.xbel"));

        if (!File.Exists(xbelPath))
        {
            return result;
        }

        try
        {
            var doc = XDocument.Load(xbelPath);
            foreach (var app in doc.Descendants().Where(e => e.Name.LocalName == "application"))
            {
                var name = app.Attribute("name")?.Value?.Trim();
                var modifiedStr = app.Attribute("modified")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(modifiedStr))
                {
                    continue;
                }

                if (DateTimeOffset.TryParse(modifiedStr, out var timestamp))
                {
                    if (!result.TryGetValue(name, out var existing) || timestamp > existing)
                    {
                        result[name] = timestamp;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    private static string NormalizePublisher(string raw)
    {
        var trimmed = raw.Trim();
        var angle = trimmed.IndexOf('<');
        return angle > 0 ? trimmed[..angle].Trim() : trimmed;
    }

    private static bool RunCommandExitZero(string fileName, IReadOnlyList<string> args, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment["LC_ALL"] = "C";
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
