using TheSingularityWorkshop.Foundry;
using Xunit;

namespace Foundry.Workflow.Tests;

public sealed class PinnedHttpsCiAiArtifactTransportTests
{
    [Fact]
    public async Task RejectsUnregisteredOriginBeforeOpeningNetworkConnection()
    {
        using var transport = PinnedHttpsCiAiArtifactTransport.Create(
            [new Uri("https://artifacts.example.test/")]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await transport.GetAsync(
                new Uri("https://storage.example.test/object.zip?signature=opaque"),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("http://artifacts.example.test/object.json")]
    [InlineData("https://127.0.0.1/object.json")]
    [InlineData("https://user@artifacts.example.test/object.json")]
    [InlineData("https://artifacts.example.test:8443/object.json")]
    public async Task RejectsRequestUrisOutsideHttpsDnsOriginPolicy(string value)
    {
        using var transport = PinnedHttpsCiAiArtifactTransport.Create(
            [new Uri("https://artifacts.example.test/")]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await transport.GetAsync(new Uri(value), CancellationToken.None));
    }

    [Fact]
    public void RequiresAtLeastOneRegisteredOrigin()
    {
        Assert.Throws<ArgumentException>(() =>
            PinnedHttpsCiAiArtifactTransport.Create(Array.Empty<Uri>()));
    }

    [Fact]
    public void RejectsDuplicateRegisteredOrigins()
    {
        Assert.Throws<ArgumentException>(() =>
            PinnedHttpsCiAiArtifactTransport.Create(
            [
                new Uri("https://artifacts.example.test/"),
                new Uri("https://ARTIFACTS.example.test/")
            ]));
    }

    [Theory]
    [InlineData("http://artifacts.example.test/")]
    [InlineData("https://artifacts.example.test:8443/")]
    [InlineData("https://artifacts.example.test/path")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://user@artifacts.example.test/")]
    public void RejectsInvalidRegisteredOrigins(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            PinnedHttpsCiAiArtifactTransport.Create([new Uri(value)]));
    }

    [Fact]
    public async Task RejectsUseAfterDispose()
    {
        var transport = PinnedHttpsCiAiArtifactTransport.Create(
            [new Uri("https://artifacts.example.test/")]);
        transport.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await transport.GetAsync(
                new Uri("https://artifacts.example.test/evidence.json"),
                CancellationToken.None));
    }
}
