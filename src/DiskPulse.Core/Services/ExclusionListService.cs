using System.Text.Json;

namespace Cleaner;

/// <summary>
/// Хранит постоянный белый список исключённых файлов и папок.
/// </summary>
public sealed class ExclusionListService
{
    private readonly string _exclusionsPath;

    public ExclusionListService()
        : this(Path.Combine(CleanerPaths.GetDefaultStandaloneDataDirectory(), "exclusions.json"))
    {
    }

    internal ExclusionListService(string exclusionsPath)
    {
        _exclusionsPath = exclusionsPath;
    }

    public static ExclusionListService FromDataDirectory(string dataDirectory) =>
        new(Path.Combine(dataDirectory, "exclusions.json"));

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(_exclusionsPath))
            {
                return [];
            }

            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_exclusionsPath));
            return Normalize(items ?? []);
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public bool Save(IEnumerable<string> paths)
    {
        try
        {
            var directory = Path.GetDirectoryName(_exclusionsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var normalized = Normalize(paths);
            var json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_exclusionsPath, json);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsExcluded(string filePath, IReadOnlyList<string> exclusions)
    {
        if (exclusions.Count == 0 || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string fullFilePath;
        try
        {
            fullFilePath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var raw in exclusions)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalized = CleanerPaths.TrimTrailingSeparator(raw);
            if (string.Equals(fullFilePath, normalized, CleanerPaths.PathComparison))
            {
                return true;
            }

            var prefix = normalized.EndsWith(Path.DirectorySeparatorChar)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
            if (fullFilePath.StartsWith(prefix, CleanerPaths.PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    public static string? NormalizePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(raw.Trim());
            return CleanerPaths.TrimTrailingSeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(CleanerPaths.PathComparer);
        foreach (var raw in paths)
        {
            var normalized = NormalizePath(raw);
            if (normalized is not null && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
