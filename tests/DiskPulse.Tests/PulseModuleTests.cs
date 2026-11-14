using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using DiskPulse.PulseModule;
using PCPulse.Contracts;
using Xunit;

namespace Cleaner.Tests;

public class PulseModuleTests
{
    [Fact]
    public void ModuleManifest_HasRequiredValidFields()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "module.json");
        Assert.True(File.Exists(manifestPath), $"Не найден module.json по пути {manifestPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;

        Assert.Equal("diskpulse", root.GetProperty("id").GetString());
        Assert.Equal("DiskPulse", root.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
        Assert.Equal("1.0", root.GetProperty("contractVersion").GetString());

        var version = root.GetProperty("version").GetString();
        Assert.True(Version.TryParse(version, out var parsedVersion) && parsedVersion >= new Version(0, 1, 0), $"Некорректная версия: {version}");

        var platforms = root.GetProperty("platforms").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("windows", platforms);
        Assert.Contains("linux", platforms);

        Assert.Equal("DiskPulse.PulseModule.dll", root.GetProperty("entryAssembly").GetString());
        Assert.Equal("DiskPulse.PulseModule.DiskPulseModule", root.GetProperty("entryType").GetString());

        var helper = root.GetProperty("helperExecutable");
        Assert.Equal("DiskPulse.Helper.exe", helper.GetProperty("windows").GetString());
        Assert.Equal("DiskPulse.Helper", helper.GetProperty("linux").GetString());
    }

    [Fact]
    public async Task ModuleLifecycle_InitializeSummaryAndDisposeWork()
    {
        var moduleDir = Directory.CreateTempSubdirectory("diskpulse-module-test");
        try
        {
            var hostContext = new TestPulseHostContext(moduleDir.FullName);
            await using var module = new DiskPulseModule();

            Assert.Equal("diskpulse", module.Descriptor.Id);
            Assert.Equal(PulseContractVersion.Current, module.Descriptor.ContractVersion);
            Assert.Equal(PulsePlatform.All, module.Descriptor.SupportedPlatforms);
            Assert.True(module.Descriptor.RequiresElevationForSomeActions);

            Assert.Throws<InvalidOperationException>(() => module.CreateView());

            await module.InitializeAsync(hostContext);
            Assert.True(Directory.Exists(hostContext.ModuleDataDirectory));

            var summary = await module.GetSummaryAsync();
            Assert.NotNull(summary);
            Assert.False(string.IsNullOrWhiteSpace(summary!.Headline));
            Assert.False(string.IsNullOrWhiteSpace(summary.Details));
        }
        finally
        {
            if (moduleDir.Exists)
            {
                moduleDir.Delete(true);
            }
        }
    }

    [Fact]
    public async Task ModuleCanBeLoadedInsideIsolatedAssemblyLoadContext()
    {
        var assemblyPath = typeof(DiskPulseModule).Assembly.Location;
        var baseDir = Path.GetDirectoryName(assemblyPath)!;
        var alc = new TestModuleLoadContext(baseDir);

        try
        {
            var loadedAsm = alc.LoadFromAssemblyPath(assemblyPath);
            var moduleType = loadedAsm.GetType("DiskPulse.PulseModule.DiskPulseModule", throwOnError: true)!;
            var instance = Assert.IsAssignableFrom<IPulseModule>(Activator.CreateInstance(moduleType));

            Assert.Equal("diskpulse", instance.Descriptor.Id);
            await instance.DisposeAsync();
        }
        finally
        {
            alc.Unload();
        }
    }

    private sealed class TestModuleLoadContext(string moduleDir) : AssemblyLoadContext(isCollectible: true)
    {
        private static bool IsHostSharedAssembly(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return string.Equals(name, "PCPulse.Contracts", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Avalonia", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("MicroCom.", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "SkiaSharp", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("SkiaSharp.", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "HarfBuzzSharp", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("HarfBuzzSharp.", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("Tmds.DBus", StringComparison.OrdinalIgnoreCase);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (IsHostSharedAssembly(assemblyName.Name))
            {
                return null; // Хост предоставляет контракты и runtime Avalonia из default ALC
            }

            var candidate = Path.Combine(moduleDir, $"{assemblyName.Name}.dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }

    private sealed class TestPulseHostContext(string dataDirectory) : IPulseHostContext
    {
        public string ModuleId => "diskpulse";
        public Version HostVersion => new(1, 0, 0);
        public string ContractVersion => PulseContractVersion.Current;
        public string ModuleDataDirectory { get; } = dataDirectory;
        public string ModuleInstallDirectory => AppContext.BaseDirectory;
        public IPulseDialogService Dialogs { get; } = new NoOpDialogService();
        public IPulseSettingsStore Settings { get; } = new InMemorySettingsStore();
        public IPulseElevationService Elevation { get; } = new NoOpElevationService();
        public IPulseLogger Logger { get; } = new NoOpLogger();

        public void RequestSummaryRefresh() { }
    }

    private sealed class NoOpDialogService : IPulseDialogService
    {
        public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string confirmText = "Продолжить", string cancelText = "Отмена", bool isDestructive = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class InMemorySettingsStore : IPulseSettingsStore
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);

        public Task<T?> LoadAsync<T>(string key, CancellationToken cancellationToken = default) where T : class =>
            Task.FromResult(_values.TryGetValue(key, out var json) && json is not null
                ? JsonSerializer.Deserialize<T>(json)
                : null);

        public Task SaveAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
        {
            _values[key] = JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpElevationService : IPulseElevationService
    {
        public bool IsElevated => false;
        public Task<int> RunElevatedHelperAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default) => Task.FromResult(1);
    }

    private sealed class NoOpLogger : IPulseLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
