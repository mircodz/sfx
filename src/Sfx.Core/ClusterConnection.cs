using Microsoft.ServiceFabric.Client;
using Sfx.Core.Auth;

namespace Sfx.Core;

/// <param name="Auth">Applied to the client builder in order.</param>
public sealed record ClusterConnection(Uri Endpoint, IReadOnlyList<IClusterAuth> Auth);

public static class ClusterClientFactory
{
    /// <summary>How long one call to the cluster may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static Task<IServiceFabricClient> CreateAsync(ClusterConnection connection, CancellationToken cancellationToken = default)
    {
        var builder = new ServiceFabricClientBuilder()
            .UseEndpoints(connection.Endpoint)
            .ConfigureClientSettings(settings => settings.ClientTimeout = Timeout);

        foreach (var auth in connection.Auth)
        {
            builder = auth.Configure(builder);
        }

        return builder.BuildAsync(cancellationToken);
    }
}

public static class ClusterEndpoint
{
    public const int DefaultPort = 19080;

    /// <summary>Accepts <c>host</c>, <c>host:port</c> or a full URL; fills in the scheme and the default port.</summary>
    public static Uri Parse(string value, bool secure)
    {
        var text = value.Contains("://", StringComparison.Ordinal) ? value : $"{(secure ? "https" : "http")}://{value}";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            throw new FormatException($"Invalid cluster endpoint '{value}'.");
        }

        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var hasPort = authority.LastIndexOf(':') > authority.LastIndexOf(']');
        var builder = new UriBuilder(uri.Scheme, uri.Host, hasPort ? uri.Port : DefaultPort);
        return builder.Uri;
    }
}
