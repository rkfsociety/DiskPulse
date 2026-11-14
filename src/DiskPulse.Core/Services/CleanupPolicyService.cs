using System.Text.Json;

namespace Cleaner;

public sealed class CleanupPolicyService
{
    private readonly string _policyPath;

    public CleanupPolicyService()
        : this(Path.Combine(CleanerPaths.GetDefaultStandaloneDataDirectory(), "policy.json"))
    {
    }

    internal CleanupPolicyService(string policyPath)
    {
        _policyPath = policyPath;
    }

    public static CleanupPolicyService FromDataDirectory(string dataDirectory) =>
        new(Path.Combine(dataDirectory, "policy.json"));

    public int LoadMinimumAgeHours()
    {
        try
        {
            if (!File.Exists(_policyPath))
            {
                return 0;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_policyPath));
            return Math.Clamp(document.RootElement.GetProperty("minimumAgeHours").GetInt32(), 0, 168);
        }
        catch (JsonException) { return 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        catch (KeyNotFoundException) { return 0; }
    }

    public bool SaveMinimumAgeHours(int hours)
    {
        try
        {
            var directory = Path.GetDirectoryName(_policyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var value = new { minimumAgeHours = Math.Clamp(hours, 0, 168) };
            File.WriteAllText(_policyPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
