namespace Sfx.Cli;

/// <summary>The scripting commands' own options; the host reads the rest (context, auth, <c>--readonly</c>, <c>--power</c>).</summary>
/// <param name="Output">What <c>-o</c> asked for; null for the command's default.</param>
/// <param name="Health">The health states <c>--health</c> keeps, as the UI words (Healthy, Warning, Error, Unknown).</param>
/// <param name="Endpoint">The cluster to use instead of a saved context; it takes <c>--auth</c> and auth options.</param>
public sealed record CliOptions(
    string? Output = null,
    string? Filter = null,
    IReadOnlySet<string>? Health = null,
    bool NoHeaders = false,
    bool Long = false,
    string? Endpoint = null)
{
    /// <summary>The first of these options given, as typed, to say it doesn't apply; null when there are none.</summary>
    public string? Given { get; private init; }

    /// <summary>
    /// Takes the scripting options out of <paramref name="args"/>, wherever they are, and returns the rest for the host's
    /// parser. Throws a usage <see cref="CliFailure"/> for a missing or unknown value.
    /// </summary>
    public static (CliOptions Options, IReadOnlyList<string> Remaining) Split(IReadOnlyList<string> args)
    {
        var options = new CliOptions();
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            var (name, inline) = arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is var eq and > 0
                ? (arg[..eq], arg[(eq + 1)..])
                : arg.StartsWith("-o", StringComparison.Ordinal) && arg.Length > 2 ? ("-o", arg[2..].TrimStart('=')) : (arg, null);

            string Value() => inline ?? (i + 1 < args.Count ? args[++i] : throw CliFailure.Usage($"{name} needs a value."));

            switch (name)
            {
                case "-o" or "--output":
                    options = options with { Output = Value().ToLowerInvariant() };
                    break;
                case "--filter":
                    options = options with { Filter = Value() };
                    break;
                case "--health":
                    options = options with { Health = HealthStates(Value()) };
                    break;
                case "--endpoint":
                    options = options with { Endpoint = Value() };
                    break;
                case "--no-headers" when inline is null:
                    options = options with { NoHeaders = true };
                    break;
                case "-l" or "--long" when inline is null:
                    options = options with { Long = true };
                    break;
                default:
                    rest.Add(arg);
                    continue;
            }
            options = options with { Given = options.Given ?? name };
        }
        return (options, rest);
    }

    private static HashSet<string> HealthStates(string value)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        foreach (var word in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            states.Add(word.ToLowerInvariant() switch
            {
                "ok" or "healthy" => "Healthy",
                "warning" => "Warning",
                "error" => "Error",
                "unknown" => "Unknown",
                _ => throw CliFailure.Usage($"--health expects ok, warning or error (or several, comma-separated), got '{word}'."),
            });
        }
        return states.Count > 0 ? states : throw CliFailure.Usage("--health needs a value.");
    }
}
