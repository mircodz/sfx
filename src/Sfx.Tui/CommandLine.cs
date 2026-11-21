using Sfx.Core.Auth;

namespace Sfx.Tui;

/// <param name="Arguments">Positional arguments: an endpoint, or a subcommand and its arguments.</param>
/// <param name="Folder">The folder <c>sfx context add</c> files the context in.</param>
/// <param name="View">The view to open with, overriding the context's; for <c>sfx context add</c>, the context's view.</param>
/// <param name="NoView"><c>--none</c>, for <c>sfx context set-view</c>: the context opens without a view.</param>
public sealed record CommandLineOptions(
    IReadOnlyList<string> Arguments,
    string? Context,
    IReadOnlyList<string> AuthMethods,
    AuthOptions AuthOptions,
    TimeSpan RefreshInterval,
    bool Use,
    bool ShowHelp,
    bool ReadOnly = false,
    bool Power = false,
    bool ShowVersion = false,
    int Port = 0,
    bool NoBrowser = false,
    string? Folder = null,
    string? View = null,
    bool NoView = false);

public sealed class UsageException(string message) : Exception(message);

public static class CommandLine
{
    /// <summary>
    /// Host options (<c>--auth</c>, <c>-c/--context</c>, <c>--refresh</c>, <c>--use</c>, <c>--readonly</c>, <c>--power</c>,
    /// <c>--help</c>, <c>--version</c>, <c>--view</c>, <c>--port</c> and <c>--no-browser</c> for <c>sfx ui</c>, <c>--folder</c> for
    /// <c>sfx context add</c>, and <c>--none</c> for <c>sfx context set-view</c>) are read here; every other <c>--key value</c> is collected for the auth providers.
    /// <paramref name="flags"/> names options that take no value.
    /// </summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args, IReadOnlySet<string> flags)
    {
        var arguments = new List<string>();
        string? context = null;
        var methods = new List<string>();
        var authOptions = new AuthOptions();
        var refresh = TimeSpan.FromSeconds(5);
        var use = false;
        var help = false;
        var version = false;
        var readOnly = false;
        var power = false;
        var port = 0;
        var noBrowser = false;
        string? folder = null;
        string? view = null;
        var noView = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    help = true;
                    continue;
                case "--version":
                    version = true;
                    continue;
                case "--use":
                    use = true;
                    continue;
                case "--readonly":
                    readOnly = true;
                    continue;
                case "--power":
                    power = true;
                    continue;
                case "--no-browser":
                    noBrowser = true;
                    continue;
                case "--none":
                    noView = true;
                    continue;
                case "-c":
                    context = i + 1 < args.Count ? args[++i] : throw new UsageException("-c needs a context name.");
                    continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                arguments.Add(arg);
                continue;
            }

            var key = arg[2..];
            string? value = null;
            var equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (flags.Contains(key))
            {
                value = "true";
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (value is null)
            {
                throw new UsageException($"--{key} needs a value.");
            }

            switch (key)
            {
                case "auth":
                    methods.AddRange(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "context":
                    context = value;
                    break;
                case "folder":
                    folder = value;
                    break;
                case "view":
                    view = value.Trim() is { Length: > 0 } name ? name : throw new UsageException("--view needs a view name.");
                    break;
                case "refresh":
                    refresh = RefreshInterval.TryParse(value, out var interval)
                        ? interval
                        : throw new UsageException($"--refresh expects an interval like 5s, 1m or off, got '{value}'.");
                    break;
                case "port":
                    port = int.TryParse(value, out var p) && p is >= 0 and <= 65535
                        ? p
                        : throw new UsageException($"--port expects a port number, got '{value}'.");
                    break;
                default:
                    authOptions.Add(key, value);
                    break;
            }
        }

        if (folder is not null && !help && arguments is not ["context", "add", ..])
        {
            throw new UsageException("--folder only applies to 'sfx context add'. To file a saved context elsewhere, use 'sfx context move <name> <folder>'.");
        }

        if (noView && !help && arguments is not ["context", "set-view", ..])
        {
            throw new UsageException("--none only applies to 'sfx context set-view <name> --none'.");
        }

        if (view is not null && !help && arguments is ["context", _, ..] and not ["context", "add", ..])
        {
            throw new UsageException("--view applies to opening a cluster and to 'sfx context add'. To change a saved context's view, use 'sfx context set-view <name> <view>'.");
        }

        return new CommandLineOptions(arguments, context, methods, authOptions, refresh, use, help, readOnly, power, version, port, noBrowser, folder, view, noView);
    }
}
