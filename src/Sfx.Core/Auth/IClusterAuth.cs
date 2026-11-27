using Microsoft.ServiceFabric.Client;

namespace Sfx.Core.Auth;

/// <summary>
/// Configures how a client authenticates to a cluster. A connection can apply several, in order,
/// so an extra layer (e.g. custom server-certificate validation) can sit on top of a primary method.
/// </summary>
public interface IClusterAuth
{
    ServiceFabricClientBuilder Configure(ServiceFabricClientBuilder builder);
}

/// <summary>Creates an <see cref="IClusterAuth"/> from user-supplied options. Register one per auth method.</summary>
public interface IClusterAuthProvider
{
    /// <summary>The name used to select this method, e.g. <c>--auth cert</c>.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>Whether connections using this method talk HTTPS.</summary>
    bool RequiresTls { get; }

    IReadOnlyList<AuthOptionInfo> Options { get; }

    IClusterAuth Create(AuthContext context);
}

public sealed record AuthOptionInfo(string Name, string Description, bool IsFlag = false);

/// <param name="Options">Every option the user passed; a provider reads the ones it declared.</param>
/// <param name="Notify">Shows a message to the user, e.g. a device code to enter in a browser.</param>
/// <param name="Interactive">
/// Whether someone is there to sign in. When not, a method that would prompt throws <see cref="SignInNeedsPersonException"/>.
/// </param>
public sealed record AuthContext(AuthOptions Options, Func<string, Task> Notify, bool Interactive = true);

public sealed class AuthConfigurationException(string message) : Exception(message);

/// <summary>Signing in would prompt, and nobody is there to answer.</summary>
/// <param name="how">How it would have prompted, e.g. "device code".</param>
public sealed class SignInNeedsPersonException(string how) : Exception($"Signing in needs a person ({how}).")
{
    public string How => how;
}
