using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ServiceFabric.Client;
using Microsoft.ServiceFabric.Common;
using Microsoft.ServiceFabric.Common.Security;

namespace Sfx.Core.Auth;

/// <summary>What a token command reads on stdin: the cluster's token-service metadata, as the cluster returned it.</summary>
public sealed record TokenRequest(string Metadata, string ServiceName, string ServiceDnsName, bool Interactive);

/// <summary>
/// An external program that turns a <see cref="TokenRequest"/> (JSON on stdin) into the authorization header value
/// (stdout). Keeps sign-in libraries that can't load in sfx, such as .NET Framework ones, in their own process.
/// </summary>
public sealed class TokenCommand(string fileName, IReadOnlyList<string> arguments)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string FileName => fileName;

    public IReadOnlyList<string> Arguments => arguments;

    /// <summary>Splits a command line on spaces, keeping double-quoted parts together.</summary>
    public static TokenCommand Parse(string commandLine)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }
        if (started)
        {
            parts.Add(current.ToString());
        }

        return parts.Count == 0
            ? throw new AuthConfigurationException("--token-command is empty.")
            : new TokenCommand(parts[0], parts.Skip(1).ToList());
    }

    public async Task<string> RunAsync(TokenRequest request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        using var kill = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        });

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, Json));
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command exited (or closed its input) before reading the request: what it said and its exit code
            // explain that better than a broken pipe, so carry on and report those.
        }
        await process.WaitForExitAsync(cancellationToken);

        var token = (await stdout).Trim();
        if (process.ExitCode != 0 || token.Length == 0)
        {
            var error = (await stderr).Trim();
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} failed (exit {process.ExitCode}){(error.Length > 0 ? ": " + error : ".")}");
        }
        return token;
    }
}

/// <summary>
/// Claims-based sign-in: the cluster hands out its token-service metadata, a <see cref="TokenCommand"/> turns
/// it into a token. Tokens are reused for <see cref="TokenLifetime"/>; with <paramref name="interactive"/> the first
/// sign-in may prompt, later ones are silent and fall back to prompting if that fails.
/// </summary>
public sealed class ClaimsCommandAuth(ServerIdentity server, TokenCommand command, bool interactive, Func<string, Task> notify) : IClusterAuth
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);

    // One sign-in at a time: concurrent requests would otherwise each prompt.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTime _expiresUtc;
    private bool _signedIn;

    public ServiceFabricClientBuilder Configure(ServiceFabricClientBuilder builder) =>
        builder.UseClaimsSecurity(_ => Task.FromResult<SecuritySettings>(
            new DstsClaimsSecuritySettings(GetTokenAsync, server.ToRemoteSettings())));

    internal async Task<string> GetTokenAsync(TokenServiceMetadata metadata, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTime.UtcNow < _expiresUtc)
            {
                return _token;
            }

            var prompt = interactive && !_signedIn;
            string token;
            try
            {
                token = await command.RunAsync(Request(metadata, prompt), cancellationToken);
            }
            catch (Exception) when (interactive && !prompt && !cancellationToken.IsCancellationRequested)
            {
                await notify("Silent sign-in failed; signing in interactively…");
                token = await command.RunAsync(Request(metadata, true), cancellationToken);
            }

            _token = token;
            _expiresUtc = DateTime.UtcNow + TokenLifetime;
            _signedIn = true;
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static TokenRequest Request(TokenServiceMetadata metadata, bool interactive) =>
        new(metadata.Metadata ?? "", metadata.ServiceName ?? "", metadata.ServiceDnsName ?? "", interactive);
}

public sealed class ClaimsCommandAuthProvider : IClusterAuthProvider
{
    public static readonly AuthOptionInfo Interactive = new("interactive", "allow a sign-in prompt (the first time, and when a silent sign-in fails)", IsFlag: true);

    public string Name => "claims-command";

    public string Description => "claims token from an external command";

    public bool RequiresTls => true;

    public IReadOnlyList<AuthOptionInfo> Options { get; } =
    [
        new("token-command", "program that reads the cluster's token-service metadata as JSON on stdin and prints the token"),
        Interactive,
        .. ServerIdentity.Options,
    ];

    public IClusterAuth Create(AuthContext context) =>
        new ClaimsCommandAuth(
            ServerIdentity.FromOptions(context.Options),
            TokenCommand.Parse(context.Options.Require("token-command")),
            IsSet(context.Options, Interactive.Name),
            context.Notify);

    public static bool IsSet(AuthOptions options, string flag) =>
        options.Get(flag) is { } value && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
}
