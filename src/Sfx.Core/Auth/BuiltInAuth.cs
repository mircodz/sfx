using Microsoft.ServiceFabric.Client;

namespace Sfx.Core.Auth;

public sealed class NoAuth : IClusterAuth
{
    public static readonly NoAuth Instance = new();

    public ServiceFabricClientBuilder Configure(ServiceFabricClientBuilder builder) => builder;
}

public sealed class NoAuthProvider : IClusterAuthProvider
{
    public string Name => "none";

    public string Description => "unsecured cluster";

    public bool RequiresTls => false;

    public IReadOnlyList<AuthOptionInfo> Options => [];

    public IClusterAuth Create(AuthContext context) => NoAuth.Instance;
}

public sealed class WindowsAuth : IClusterAuth
{
    public ServiceFabricClientBuilder Configure(ServiceFabricClientBuilder builder) => builder.UseWindowsSecurity();
}

public sealed class WindowsAuthProvider : IClusterAuthProvider
{
    public string Name => "windows";

    public string Description => "Windows (Kerberos) as the current user";

    public bool RequiresTls => true;

    public IReadOnlyList<AuthOptionInfo> Options => [];

    public IClusterAuth Create(AuthContext context) => new WindowsAuth();
}

public static class BuiltInAuth
{
    public static IReadOnlyList<IClusterAuthProvider> Providers { get; } =
    [
        new NoAuthProvider(),
        new CertificateAuthProvider(),
        new AadAuthProvider(),
        new WindowsAuthProvider(),
        new ClaimsCommandAuthProvider(),
    ];
}
