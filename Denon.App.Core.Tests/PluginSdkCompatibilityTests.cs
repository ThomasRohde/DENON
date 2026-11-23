using System.Reflection;
using Denon.App.PluginSdk;
using Xunit;

namespace Denon.App.Core.Tests;

public sealed class PluginSdkCompatibilityTests
{
    private sealed class MinimalContext : IPluginContext
    {
        public string PluginId => "test";
        public IPluginLogger Logger => null!;
        public IPluginEventLogger Events => null!;
        public IPluginStorage Storage => null!;
        public IPluginReceiverBridge Receiver => null!;
        public HttpClient CreateHttpClient(bool allowLocalSelfSignedTls = false, TimeSpan? timeout = null) => new();
        public void ReportStatus(string statusText, PluginHealthState health) { }
    }

    [Fact]
    public void New_context_members_have_safe_defaults()
    {
        // A context written for an older SDK (no ReceiverControl, no IsAppShuttingDown) still compiles and works.
        IPluginContext context = new MinimalContext();
        Assert.Null(context.ReceiverControl);
        Assert.False(context.IsAppShuttingDown);
    }

    [Fact]
    public void Sdk_assembly_version_stays_pinned_for_older_hosts()
    {
        // Plugins built against this SDK must bind on hosts with SDK 1.16 (.NET refuses a higher referenced version).
        var assembly = typeof(IDenonPlugin).Assembly;
        Assert.Equal(new Version(1, 16, 0, 0), assembly.GetName().Version);

        var fileVersion = Version.Parse(assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.True(fileVersion >= new Version(1, 17), $"File version {fileVersion} should carry the real SDK version.");
    }
}
