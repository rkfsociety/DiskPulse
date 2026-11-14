using System.Text.Json;

namespace Cleaner;

public sealed class CleanerSettingsService
{
    private readonly string _settingsPath;

    public CleanerSettingsService()
        : this(Path.Combine(CleanerPaths.GetDefaultStandaloneDataDirectory(), "settings.json"))
    {
    }

    internal CleanerSettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public static CleanerSettingsService FromDataDirectory(string dataDirectory) =>
        new(Path.Combine(dataDirectory, "settings.json"));

    public IReadOnlyList<string> LoadSelectedDrives(IReadOnlyList<string> availableDrives)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return availableDrives;
            }

            var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_settingsPath));
            var selected = availableDrives.Where(drive => saved?.Contains(drive, CleanerPaths.PathComparer) == true).ToArray();
            return selected.Length > 0 ? selected : availableDrives;
        }
        catch (JsonException) { return availableDrives; }
        catch (IOException) { return availableDrives; }
        catch (UnauthorizedAccessException) { return availableDrives; }
    }

    public IReadOnlyList<string> LoadSelectedDrives(IReadOnlyList<string> availableDrives, string defaultDrive)
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return availableDrives.Contains(defaultDrive, CleanerPaths.PathComparer)
                    ? [defaultDrive]
                    : availableDrives;
            }

            var saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_settingsPath));
            var selected = availableDrives.Where(drive => saved?.Contains(drive, CleanerPaths.PathComparer) == true).ToArray();
            return selected.Length > 0 ? selected : [defaultDrive];
        }
        catch (JsonException) { return [defaultDrive]; }
        catch (IOException) { return [defaultDrive]; }
        catch (UnauthorizedAccessException) { return [defaultDrive]; }
    }

    public bool SaveSelectedDrives(IEnumerable<string> drives)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(drives.Distinct(CleanerPaths.PathComparer).ToArray()));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
