using Microsoft.ServiceFabric.Common;
using Sfx.Core;
using Sfx.Core.Auth;

namespace Sfx.Tests;

public class CoreTests
{
    [Fact]
    public async Task PageAsync_follows_continuation_tokens()
    {
        var pages = new Dictionary<string, PagedData<int>>
        {
            [""] = new(new ContinuationToken("2"), [1, 2]),
            ["2"] = new(new ContinuationToken("3"), [3]),
            ["3"] = new(new ContinuationToken(""), [4]),
        };
        var requested = new List<string>();

        var all = await ClusterQueries.PageAsync(token =>
        {
            var key = token?.ToString() ?? "";
            requested.Add(key);
            return Task.FromResult(pages[key]);
        });

        Assert.Equal([1, 2, 3, 4], all);
        Assert.Equal(["", "2", "3"], requested);
    }

    [Theory]
    [InlineData("ab:cd ef", "ABCDEF")]
    [InlineData("\u200eAB CD", "ABCD")]
    public void NormalizeThumbprint_keeps_only_hex(string input, string expected)
    {
        Assert.Equal(expected, CertificateLoader.NormalizeThumbprint(input));
    }

    [Fact]
    public void ServerIdentity_requires_thumbprint_or_common_name()
    {
        Assert.Throws<AuthConfigurationException>(() => ServerIdentity.FromOptions(new AuthOptions()));

        var options = new AuthOptions();
        options.Add("server-cn", "a.example.com");
        options.Add("server-cn", "b.example.com");
        Assert.Equal(["a.example.com", "b.example.com"], ServerIdentity.FromOptions(options).CommonNames);
    }

    [Fact]
    public void CertificateAuthProvider_needs_exactly_one_certificate_source()
    {
        var options = new AuthOptions();
        options.Add("server-cn", "cluster");
        var context = new AuthContext(options, _ => Task.CompletedTask);

        Assert.Throws<AuthConfigurationException>(() => new CertificateAuthProvider().Create(context));
    }

    [Fact]
    public async Task Aad_off_a_terminal_fails_rather_than_starting_a_device_code()
    {
        var options = new AuthOptions();
        options.Add("server-cn", "cluster");
        var notified = false;
        var auth = (AadAuth)new AadAuthProvider().Create(new AuthContext(
            options,
            _ =>
            {
                notified = true;
                return Task.CompletedTask;
            },
            Interactive: false));
        var metadata = new AadMetadata(
            "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000001",
            "00000000-0000-0000-0000-000000000002",
            "00000000-0000-0000-0000-000000000003",
            "https://login.microsoftonline.com",
            "urn:ietf:wg:oauth:2.0:oob",
            "00000000-0000-0000-0000-000000000001");

        var error = await Assert.ThrowsAsync<SignInNeedsPersonException>(() => auth.GetTokenAsync(metadata, CancellationToken.None));

        Assert.Equal("device code", error.How);
        Assert.False(notified);
    }

    [Fact]
    public void Natural_order_reads_numbers_as_numbers_and_ignores_case()
    {
        string[] names = ["_Node_10", "b", "_Node_2", "1.10.0", "A", "_node_1", "1.9.0", "x02", "x1"];

        Assert.Equal(["1.9.0", "1.10.0", "_node_1", "_Node_2", "_Node_10", "A", "b", "x1", "x02"], names.Order(NaturalComparer.Instance));
    }
}
