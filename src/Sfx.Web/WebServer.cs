using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Client.Exceptions;
using Microsoft.ServiceFabric.Common.Exceptions;
using Sfx.Core;

namespace Sfx.Web;

/// <param name="Port">0 picks a free port.</param>
/// <param name="Token">The secret the browser must send; random when null.</param>
public sealed record WebOptions(int Port = 0, bool OpenBrowser = true, string? Token = null);

/// <summary>
/// <c>sfx ui</c>: serves the browser UI and its API on 127.0.0.1 for one connected cluster.
/// The API acts with the user's cluster credentials, so it only answers requests that carry the token printed at start
/// (other sites can't read or send it) and only for a localhost Host header (so DNS rebinding can't reach it).
/// </summary>
public static class WebServer
{
    public const string TokenHeader = "X-Sfx-Token";

    public static async Task<int> RunAsync(UiHost host, WebOptions options, CancellationToken cancellationToken)
    {
        // A short configured token would be guessable; anything under 16 characters gets a random one instead.
        var token = options.Token is { Length: >= 16 } configured ? configured : Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(host);
        builder.Services.AddScoped(_ => host.Current);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.Port));
        await using var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (!IsLocalHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            await next(context);
        });

        app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"), api => api.Use(async (context, next) =>
        {
            var sent = context.Request.Headers[TokenHeader].ToString();
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(sent), System.Text.Encoding.UTF8.GetBytes(token)))
            {
                await Error(context, StatusCodes.Status401Unauthorized, "Open sfx ui from the link it printed; that link carries the access token.");
                return;
            }

            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
            }
            catch (ActionException ex)
            {
                await Error(context, ex.Status, ex.Message);
            }
            catch (RefusedException ex)
            {
                await Error(context, ex.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (BadHttpRequestException ex)
            {
                await Error(context, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (NotFoundException ex)
            {
                await Error(context, StatusCodes.Status404NotFound, ex.Message);
            }
            catch (ServiceFabricException ex) when (ex.ErrorCode.ToString() is var code && (code.Contains("NOT_FOUND") || code.Contains("DOES_NOT_EXIST")))
            {
                await Error(context, StatusCodes.Status404NotFound, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                await Error(context, StatusCodes.Status409Conflict, ex.Message);
            }
            catch (Exception ex) when (ex is ServiceFabricException or ServiceFabricRequestException or HttpRequestException
                or TimeoutException or TaskCanceledException)
            {
                // The cluster (or the way to it) failed, not sfx.
                await Error(context, StatusCodes.Status502BadGateway, ex.GetBaseException().Message);
            }
        }));

        Api.Map(app, host);
        app.MapFallback("/api/{**rest}", context => Error(context, StatusCodes.Status404NotFound, "No such endpoint."));
        // A plain MapFallback skips paths that look like files, which is every asset.
        app.MapFallback("{**path}", ServeAsset);

        try
        {
            await app.StartAsync(cancellationToken);
        }
        catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
        {
            Console.Error.WriteLine(
                $"sfx: port {options.Port} is already in use, probably by another sfx ui. Pick another with --port, or leave it out to use any free one.");
            return 1;
        }
        var port = new Uri(app.Urls.First()).Port;
        var url = $"http://127.0.0.1:{port}/?token={token}";
        var session = host.Current;
        Console.WriteLine($"sfx ui for {session.Context.Name} ({session.Cluster.Endpoint}){(session.ReadOnly ? ", read-only" : "")}; switch contexts in the browser");
        Console.WriteLine($"  {url}");
        Console.WriteLine("Press Ctrl+C to stop.");
        if (options.OpenBrowser)
        {
            Open(url);
        }

        await app.WaitForShutdownAsync(cancellationToken);
        return 0;
    }

    private static bool IsLocalHost(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    private static Task Error(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static readonly Assembly Self = typeof(WebServer).Assembly;

    /// <summary>The built UI, embedded as "wwwroot/..." (the separator follows the OS that built it).</summary>
    private static readonly Dictionary<string, string> Assets = Self.GetManifestResourceNames()
        .Where(n => n.StartsWith("wwwroot", StringComparison.Ordinal))
        .ToDictionary(n => n["wwwroot".Length..].Replace('\\', '/').TrimStart('/'), n => n, StringComparer.Ordinal);

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static async Task ServeAsset(HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimStart('/') ?? "";
        if (!Assets.ContainsKey(path))
        {
            // Unknown paths get the app, which routes in the browser.
            path = "index.html";
        }
        if (!Assets.TryGetValue(path, out var resource))
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("This sfx was built without its web UI. Build it with bun (see web/) and rebuild sfx.");
            return;
        }

        context.Response.ContentType = ContentTypes.TryGetContentType(path, out var type) ? type : "application/octet-stream";
        // Vite fingerprints everything under assets/, so those never change; index.html always might.
        context.Response.Headers.CacheControl = path.StartsWith("assets/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
        await using var stream = Self.GetManifestResourceStream(resource)!;
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser to open (e.g. over SSH): the printed link is enough.
        }
    }
}
