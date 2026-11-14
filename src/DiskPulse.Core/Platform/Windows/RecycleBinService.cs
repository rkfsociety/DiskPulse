using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cleaner;

public sealed class RecycleBinService : IRecycleBinService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref QueryInfo info);

    [SupportedOSPlatform("windows")]
    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr owner, string? rootPath, uint flags);

    private const uint NoConfirmation = 0x00000001;
    private const uint NoProgressUi = 0x00000002;
    private const uint NoSound = 0x00000004;
    private const int RecycleBinAlreadyEmpty = unchecked((int)0x8000FFFF);

    public RecycleBinInfo GetInfo(IEnumerable<string>? roots = null)
    {
        var perRoot = GetInfoPerRoot(roots);
        var totalBytes = perRoot.Values.Sum(info => info.Bytes);
        var totalItems = perRoot.Values.Sum(info => (long)info.Items);
        return new RecycleBinInfo(totalBytes, checked((int)Math.Min(totalItems, int.MaxValue)));
    }

    public IReadOnlyDictionary<string, RecycleBinInfo> GetInfoPerRoot(IEnumerable<string>? roots = null)
    {
        var results = new Dictionary<string, RecycleBinInfo>(CleanerPaths.PathComparer);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var root in roots ?? [string.Empty])
            {
                results[root ?? string.Empty] = new RecycleBinInfo(0, 0);
            }

            return results;
        }

        foreach (var root in roots ?? [null!])
        {
            var info = new QueryInfo { Size = Marshal.SizeOf<QueryInfo>() };
            var key = root ?? string.Empty;
            results[key] = SHQueryRecycleBin(root, ref info) == 0
                ? new RecycleBinInfo(info.Bytes, checked((int)Math.Min(info.Items, int.MaxValue)))
                : new RecycleBinInfo(0, 0);
        }

        return results;
    }

    public IReadOnlyDictionary<string, bool> EmptyPerRoot(IEnumerable<string>? roots = null)
    {
        var results = new Dictionary<string, bool>(CleanerPaths.PathComparer);
        foreach (var root in roots ?? [null!])
        {
            results[root ?? string.Empty] = Empty(root);
        }

        return results;
    }

    public bool Empty(string? root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var result = SHEmptyRecycleBin(IntPtr.Zero, root, NoConfirmation | NoProgressUi | NoSound);
        return result == 0 || result == RecycleBinAlreadyEmpty;
    }
}
