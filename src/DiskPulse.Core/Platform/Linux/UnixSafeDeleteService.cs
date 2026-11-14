namespace Cleaner;

internal sealed class UnixSafeDeleteService : ISafeDeleteService
{
    public DeleteAttemptResult TryDelete(string fullPath, string root, DateTime? maxLastWriteTimeUtc = null) =>
        TryDeleteUnix(fullPath, root, maxLastWriteTimeUtc);

    internal static DeleteAttemptResult TryDeleteUnix(string fullPath, string root, DateTime? maxLastWriteTimeUtc = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(root))
            {
                return new DeleteAttemptResult(false, CleanupSkipReason.AccessDenied);
            }

            var normalizedFullPath = Path.GetFullPath(fullPath);
            var normalizedRoot = CleanerPaths.EnsureTrailingSeparator(root);

            if (!normalizedFullPath.StartsWith(normalizedRoot, CleanerPaths.PathComparison))
            {
                return new DeleteAttemptResult(false, CleanupSkipReason.AccessDenied);
            }

            // Проверяем каждый каталог от корня к файлу на наличие символьной ссылки / reparse point.
            var trimmedRoot = CleanerPaths.TrimTrailingSeparator(normalizedRoot);
            var current = Path.GetDirectoryName(normalizedFullPath);
            while (!string.IsNullOrEmpty(current))
            {
                if (!Directory.Exists(current))
                {
                    return new DeleteAttemptResult(false, CleanupSkipReason.NotFound);
                }

                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.LinkTarget is not null || dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return new DeleteAttemptResult(false, CleanupSkipReason.ReparsePoint);
                }

                if (string.Equals(CleanerPaths.TrimTrailingSeparator(current), trimmedRoot, CleanerPaths.PathComparison))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }

            var fileInfo = new FileInfo(normalizedFullPath);
            if (!fileInfo.Exists)
            {
                return new DeleteAttemptResult(false, CleanupSkipReason.NotFound);
            }

            if (fileInfo.LinkTarget is not null || fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new DeleteAttemptResult(false, CleanupSkipReason.ReparsePoint);
            }

            if (maxLastWriteTimeUtc is { } threshold && fileInfo.LastWriteTimeUtc > threshold)
            {
                return new DeleteAttemptResult(false, CleanupSkipReason.Changed);
            }

            fileInfo.Delete();
            return new DeleteAttemptResult(true, CleanupSkipReason.None);
        }
        catch (FileNotFoundException)
        {
            return new DeleteAttemptResult(false, CleanupSkipReason.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return new DeleteAttemptResult(false, CleanupSkipReason.NotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return new DeleteAttemptResult(false, CleanupSkipReason.AccessDenied);
        }
        catch (IOException ex)
        {
            var code = ex.HResult & 0xFFFF;
            return new DeleteAttemptResult(false, code is 32 or 33 ? CleanupSkipReason.FileInUse : CleanupSkipReason.Unknown);
        }
    }
}
