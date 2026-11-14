namespace Cleaner;

public static class CleanerPaths
{
    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string GetDefaultStandaloneDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "Cleaner");
            }
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return Path.Combine(xdgDataHome, "diskpulse");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.Combine(home, ".local", "share", "diskpulse");
        }

        return Path.Combine(Path.GetTempPath(), "diskpulse-data");
    }

    public static string TrimTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (OperatingSystem.IsWindows())
        {
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 2 && trimmed[1] == ':')
            {
                return trimmed + Path.DirectorySeparatorChar;
            }

            return trimmed;
        }

        return path == "/" ? "/" : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static string EnsureTrailingSeparator(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
    }

    public static string FormatVolumeLabel(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows() && root == "/")
        {
            return "/";
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
