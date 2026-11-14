namespace Cleaner;

public static class AppLogService
{
    private static readonly object SyncRoot = new();
    private const long MaxLogBytes = 1_048_576;
    private static Action<string, Exception?>? _sink;
    private static string _logDirectory = CleanerPaths.GetDefaultStandaloneDataDirectory();
    [ThreadStatic]
    private static bool _inSink;

    public static void Configure(string dataDirectory, Action<string, Exception?>? externalSink = null)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _logDirectory = dataDirectory;
        }

        _sink = externalSink;
    }

    public static void Write(string message, Exception exception)
    {
        if (!_inSink && _sink is { } sink)
        {
            _inSink = true;
            try
            {
                sink(message, exception);
                return;
            }
            catch
            {
                // Fallback to local file if external logger throws.
            }
            finally
            {
                _inSink = false;
            }
        }

        Append($"{message}: {exception}");
    }

    public static void Write(string message)
    {
        if (!_inSink && _sink is { } sink)
        {
            _inSink = true;
            try
            {
                sink(message, null);
                return;
            }
            catch
            {
                // Fallback to local file if external logger throws.
            }
            finally
            {
                _inSink = false;
            }
        }

        Append(message);
    }

    private static void Append(string text)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}";
            var path = Path.Combine(_logDirectory, "app.log");
            lock (SyncRoot)
            {
                File.AppendAllText(path, line);
                RollIfNeeded(path);
            }
        }
        catch
        {
            // Ошибка записи журнала не должна завершать приложение.
        }
    }

    private static void RollIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxLogBytes)
        {
            return;
        }

        var previous = path + ".1";
        if (File.Exists(previous))
        {
            File.Delete(previous);
        }

        File.Move(path, previous);
    }
}
