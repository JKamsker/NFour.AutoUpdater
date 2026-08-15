using NFour.AutoUpdater.Core;
using NFour.AutoUpdater.Gateway;
using NFour.AutoUpdater.Server;
using Xunit;

namespace NFour.AutoUpdater.Architecture.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void ServerHasNoDirectSigningPrimitiveDependency()
    {
        var references = typeof(ManagementState).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("BouncyCastle.Cryptography", references);
    }

    [Fact]
    public void GatewayDoesNotReferenceTheManagementServer()
    {
        var references = typeof(GatewayMarker).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(typeof(ManagementState).Assembly.GetName().Name, references);
    }

    [Fact]
    public void CoreDoesNotReferenceTheServer()
    {
        var references = typeof(ContentHash).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(typeof(ManagementState).Assembly.GetName().Name, references);
    }
}
