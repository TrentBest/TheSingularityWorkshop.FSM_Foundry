using System.Net;
using TheSingularityWorkshop.Foundry;

namespace Foundry.Workflow.Tests;

public sealed class PinnedHttpsArtifactHttpClientFactoryTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.10", false)]
    [InlineData("203.0.113.10", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:20::1", false)]
    [InlineData("2002:0808:0808::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void PublicAddressClassifierRejectsSpecialPurposeRanges(string value, bool expected)
    {
        Assert.Equal(expected,
            PinnedHttpsArtifactHttpClientFactory.IsPubliclyRoutable(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("http://artifacts.example.com/")]
    [InlineData("https://artifacts.example.com:8443/")]
    [InlineData("https://user@artifacts.example.com/")]
    [InlineData("https://artifacts.example.com/path")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://artifacts.example.com/?token=secret")]
    public void RejectsOriginsOutsideStrictRegisteredHttpsOriginPolicy(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            PinnedHttpsArtifactHttpClientFactory.Create(new Uri(value)));
    }

    [Fact]
    public void CreatesClientForRegisteredHttpsOrigin()
    {
        using var client = PinnedHttpsArtifactHttpClientFactory.Create(
            new Uri("https://artifacts.example.com/"));

        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }
}
