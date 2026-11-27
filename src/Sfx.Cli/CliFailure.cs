namespace Sfx.Cli;

/// <summary>What a scripting command exits with.</summary>
public static class ExitCode
{
    public const int Ok = 0;
    public const int ClusterFailed = 1;
    public const int Usage = 2;
    public const int Refused = 3;
    public const int Timeout = 4;
    public const int CannotConnect = 5;
}

/// <summary>Ends a scripting command with <paramref name="message"/> on stderr and <paramref name="exitCode"/>.</summary>
public sealed class CliFailure(string message, int exitCode) : Exception(message)
{
    public int Code => exitCode;

    public static CliFailure Usage(string message) => new(message, ExitCode.Usage);

    public static CliFailure NotFound(string message) => new(message, ExitCode.ClusterFailed);
}
