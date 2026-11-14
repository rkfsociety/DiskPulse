using System.IO.Enumeration;

namespace Cleaner;

/// <summary>
/// Работа с корзиной Linux по спецификации FreeDesktop.org Trash ($XDG_DATA_HOME/Trash).
/// </summary>
public sealed class LinuxTrashService : IRecycleBinService
{
    private readonly string? _customTrashDirectory;

    public LinuxTrashService()
    {
    }

    internal LinuxTrashService(string customTrashDirectory)
    {
        _customTrashDirectory = customTrashDirectory;
    }

    public RecycleBinInfo GetInfo(IEnumerable<string>? roots = null)
    {
        var trashDirs = GetTrashDirectories(roots);
        long totalBytes = 0;
        long totalItems = 0;

        foreach (var trashDir in trashDirs)
        {
            var info = ReadTrashDirectoryInfo(trashDir);
            totalBytes += info.Bytes;
            totalItems += info.Items;
        }

        return new RecycleBinInfo(totalBytes, checked((int)Math.Min(totalItems, int.MaxValue)));
    }

    public IReadOnlyDictionary<string, RecycleBinInfo> GetInfoPerRoot(IEnumerable<string>? roots = null)
    {
        var results = new Dictionary<string, RecycleBinInfo>(CleanerPaths.PathComparer);
        var rootList = roots?.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray() ?? ["/"];
        if (rootList.Length == 0)
        {
            rootList = ["/"];
        }

        // Основная корзина пользователя привязана к домашнему/системному разделу;
        // для первого/корневого раздела возвращаем общий размер, чтобы не дублировать подсчёт.
        var total = GetInfo(rootList);
        for (var i = 0; i < rootList.Length; i++)
        {
            results[rootList[i]] = i == 0 ? total : new RecycleBinInfo(0, 0);
        }

        return results;
    }

    public bool Empty(string? root)
    {
        var trashDirs = GetTrashDirectories(root is null ? null : [root]);
        var allSucceeded = true;

        foreach (var trashDir in trashDirs)
        {
            if (!EmptyTrashDirectory(trashDir))
            {
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    internal IReadOnlyList<string> GetTrashDirectories(IEnumerable<string>? roots)
    {
        if (!string.IsNullOrWhiteSpace(_customTrashDirectory))
        {
            return Directory.Exists(_customTrashDirectory) ? [_customTrashDirectory] : [];
        }

        var dirs = new List<string>();
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var homeTrash = !string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(xdgDataHome, "Trash")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Trash");

        if (Directory.Exists(homeTrash))
        {
            dirs.Add(homeTrash);
        }

        if (!OperatingSystem.IsWindows() && roots is not null)
        {
            var uid = Environment.GetEnvironmentVariable("UID");
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(uid))
                {
                    var volumeTrash = Path.Combine(root, $".Trash-{uid}");
                    if (Directory.Exists(volumeTrash))
                    {
                        dirs.Add(volumeTrash);
                    }
                }
            }
        }

        return dirs.Distinct(CleanerPaths.PathComparer).ToArray();
    }

    internal static RecycleBinInfo ReadTrashDirectoryInfo(string trashDir)
    {
        var filesDir = Path.Combine(trashDir, "files");
        if (!Directory.Exists(filesDir) || IsSymlink(filesDir))
        {
            return new RecycleBinInfo(0, 0);
        }

        var itemCount = 0;
        long totalBytes = 0;

        try
        {
            itemCount = Directory.EnumerateFileSystemEntries(filesDir).Count();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var enumerable = new FileSystemEnumerable<long>(
                filesDir,
                (ref FileSystemEntry entry) => entry.Length,
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) == 0,
                ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0
            };

            foreach (var bytes in enumerable)
            {
                totalBytes += bytes;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new RecycleBinInfo(totalBytes, itemCount);
    }

    internal static bool EmptyTrashDirectory(string trashDir)
    {
        if (!Directory.Exists(trashDir) || IsSymlink(trashDir))
        {
            return true;
        }

        var succeeded = true;
        foreach (var sub in new[] { "files", "info" })
        {
            var subPath = Path.Combine(trashDir, sub);
            if (!Directory.Exists(subPath) || IsSymlink(subPath))
            {
                continue;
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(subPath))
                {
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            if (IsSymlink(entry))
                            {
                                Directory.Delete(entry, recursive: false);
                            }
                            else
                            {
                                DeleteDirectoryContentsSafely(entry, subPath);
                            }
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                    catch (IOException)
                    {
                        succeeded = false;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        succeeded = false;
                    }
                }
            }
            catch (IOException)
            {
                succeeded = false;
            }
            catch (UnauthorizedAccessException)
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private static void DeleteDirectoryContentsSafely(string directory, string rootBoundary)
    {
        var dirInfo = new DirectoryInfo(directory);
        if (!dirInfo.Exists)
        {
            return;
        }

        if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dirInfo.Delete(recursive: false);
            return;
        }

        foreach (var entry in dirInfo.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                entry.Delete();
                continue;
            }

            if (entry is DirectoryInfo childDir)
            {
                DeleteDirectoryContentsSafely(childDir.FullName, rootBoundary);
            }
            else
            {
                entry.Delete();
            }
        }

        dirInfo.Delete(recursive: false);
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return attrs.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
