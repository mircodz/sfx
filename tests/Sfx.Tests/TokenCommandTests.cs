using Microsoft.ServiceFabric.Common;
using Sfx.Core.Auth;

namespace Sfx.Tests;

public class TokenCommandTests
{
    // A token command written in PowerShell: echoes what it was asked, or fails when asked to be silent.
    private const string Script = """
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        if ($env:SFX_TEST_FAIL_SILENT -eq '1' -and -not $request.interactive) { [Console]::Error.Write('no cached sign-in'); exit 3 }
        "token:$($request.serviceName):$($request.interactive)"
        """;

    private static readonly TokenServiceMetadata Metadata = new("bWV0YQ==", "test-cluster", "cluster.example.com");

    private static TokenCommand Pwsh(string script) => new("pwsh", ["-NoProfile", "-NonInteractive", "-Command", script]);

    private static bool HasPwsh() =>
        Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")));

    [Theory]
    [InlineData("helper.exe", "helper.exe")]
    [InlineData("  \"C:\\Program Files\\sfx\\get-token.exe\" --flag  \"two words\" ", "C:\\Program Files\\sfx\\get-token.exe", "--flag", "two words")]
    public void Parse_splits_on_spaces_and_keeps_quoted_parts(string commandLine, string fileName, params string[] arguments)
    {
        var command = TokenCommand.Parse(commandLine);

        Assert.Equal(fileName, command.FileName);
        Assert.Equal(arguments, command.Arguments);
    }

    [Fact]
    public async Task Runs_the_command_with_the_metadata_on_stdin()
    {
        if (!HasPwsh())
        {
            return;
        }

        var token = await Pwsh(Script).RunAsync(new TokenRequest("bWV0YQ==", "test-cluster", "cluster.example.com", true), CancellationToken.None);

        Assert.Equal("token:test-cluster:True", token);
    }

    [Fact]
    public async Task A_failing_command_reports_its_exit_code_and_stderr()
    {
        if (!HasPwsh())
        {
            return;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pwsh("[Console]::Error.Write('denied'); exit 5").RunAsync(new TokenRequest("", "", "", false), CancellationToken.None));

        Assert.Equal("pwsh failed (exit 5): denied", error.Message);
    }

    [Fact]
    public async Task Prompts_once_then_reuses_the_token()
    {
        if (!HasPwsh())
        {
            return;
        }

        var auth = new ClaimsCommandAuth(new ServerIdentity([], ["cn"]), Pwsh(Script), interactive: true, _ => Task.CompletedTask);

        Assert.Equal("token:test-cluster:True", await auth.GetTokenAsync(Metadata, CancellationToken.None));
        // Cached: a second call doesn't run the command (or prompt) again.
        Assert.Equal("token:test-cluster:True", await auth.GetTokenAsync(Metadata, CancellationToken.None));
    }

    [Fact]
    public void Provider_requires_a_command_and_reads_the_interactive_flag()
    {
        var options = new AuthOptions();
        options.Add("server-cn", "server.example.com");
        var provider = new ClaimsCommandAuthProvider();

        Assert.Throws<AuthConfigurationException>(() => provider.Create(new AuthContext(options, _ => Task.CompletedTask)));

        options.Add("token-command", "helper.exe");
        options.Add("interactive", "true");
        Assert.IsType<ClaimsCommandAuth>(provider.Create(new AuthContext(options, _ => Task.CompletedTask)));
        Assert.True(ClaimsCommandAuthProvider.IsSet(options, "interactive"));
        Assert.True(provider.Options.Single(o => o.Name == "interactive").IsFlag);
    }
}
