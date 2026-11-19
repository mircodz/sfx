using Sfx.Tui;

namespace Sfx.Tests;

public class CommandLineTests
{
    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();

    [Fact]
    public void Splits_host_options_from_auth_options()
    {
        var options = CommandLine.Parse(
            ["mycluster", "--auth", "cert,custom", "--cert-thumbprint", "AB", "--server-cn=c1", "--server-cn", "c2", "--refresh", "0"],
            NoFlags);

        Assert.Equal(["mycluster"], options.Arguments);
        Assert.Equal(["cert", "custom"], options.AuthMethods);
        Assert.Equal("AB", options.AuthOptions.Get("cert-thumbprint"));
        Assert.Equal(["c1", "c2"], options.AuthOptions.GetAll("server-cn"));
        Assert.Equal(TimeSpan.Zero, options.RefreshInterval);
    }

    [Fact]
    public void Flags_do_not_consume_the_next_argument()
    {
        var options = CommandLine.Parse(["--interactive", "mycluster"], new HashSet<string> { "interactive" });

        Assert.Equal(["mycluster"], options.Arguments);
        Assert.Equal("true", options.AuthOptions.Get("interactive"));
    }

    [Fact]
    public void Reads_subcommands_context_and_use()
    {
        var options = CommandLine.Parse(["context", "add", "prod", "https://prod", "--use", "-c", "other"], NoFlags);

        Assert.Equal(["context", "add", "prod", "https://prod"], options.Arguments);
        Assert.True(options.Use);
        Assert.Equal("other", options.Context);
        Assert.Empty(options.AuthOptions.Keys);
    }

    [Fact]
    public void Reads_version_flag()
    {
        var options = CommandLine.Parse(["--version"], NoFlags);

        Assert.True(options.ShowVersion);
        Assert.Empty(options.Arguments);
    }

    [Theory]
    [InlineData("--cert-thumbprint")]
    [InlineData("--refresh", "soon")]
    [InlineData("-c")]
    public void Rejects_malformed_arguments(params string[] args)
    {
        Assert.Throws<UsageException>(() => CommandLine.Parse(args, NoFlags));
    }
}
