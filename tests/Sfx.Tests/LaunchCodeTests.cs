using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class LaunchCodeTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_code_is_traded_for_the_token_once()
    {
        var codes = new LaunchCodes(Token, new Clock());
        var code = codes.Issue();

        Assert.DoesNotContain(Token, code);
        Assert.Equal(Token, codes.Redeem(code));
        Assert.Null(codes.Redeem(code));
    }

    [Fact]
    public void A_code_expires_after_a_minute()
    {
        var clock = new Clock();
        var codes = new LaunchCodes(Token, clock);
        var fresh = codes.Issue();
        var stale = codes.Issue();

        clock.Now += TimeSpan.FromSeconds(59);
        Assert.Equal(Token, codes.Redeem(fresh));
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(codes.Redeem(stale));
    }

    [Fact]
    public void Codes_are_each_their_own()
    {
        var codes = new LaunchCodes(Token, new Clock());
        var first = codes.Issue();
        var second = codes.Issue();

        Assert.NotEqual(first, second);
        Assert.Equal(Token, codes.Redeem(second));
        Assert.Equal(Token, codes.Redeem(first));
    }

    [Fact]
    public void The_launch_endpoint_answers_401_to_a_wrong_used_or_missing_code()
    {
        var codes = new LaunchCodes(Token, new Clock());
        var code = codes.Issue();

        Assert.Equal(401, Status(WebServer.Launch(codes, new LaunchRequest("not-a-code"))));
        Assert.Equal(401, Status(WebServer.Launch(codes, new LaunchRequest(null))));

        var ok = WebServer.Launch(codes, new LaunchRequest(code));
        Assert.Equal(200, Status(ok));
        Assert.Equal(Token, Assert.IsAssignableFrom<IValueHttpResult<LaunchDto>>(ok).Value?.Token);
        Assert.Equal(401, Status(WebServer.Launch(codes, new LaunchRequest(code))));
    }

    [Fact]
    public async Task The_server_takes_launch_codes_without_the_token_and_checks_the_tab_s_connection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var context = new ClusterContext("dev", "localhost", [], new AuthOptions());
        var session = new ClusterSession(context, new Uri("http://localhost:19080"), new ClusterQueries(null!));
        var store = ContextStore.Load(Path.Combine(Path.GetTempPath(), $"sfx-launch-{Guid.NewGuid():N}.toml"));
        var log = new ActionLog(Path.Combine(Path.GetTempPath(), $"sfx-launch-{Guid.NewGuid():N}.log"));
        var host = new UiHost(session, store, new ClusterConnector(BuiltInAuth.Providers), log, "0.0.0");
        using var stop = new CancellationTokenSource();
        var server = WebServer.RunAsync(host, new WebOptions(port, OpenBrowser: false, Token: Token), stop.Token);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        HttpResponseMessage contexts = null!;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                contexts = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/contexts") { Headers = { { WebServer.TokenHeader, Token } } });
                break;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal(HttpStatusCode.OK, contexts.StatusCode);
        Assert.Equal(host.Current.Id, contexts.Headers.GetValues(WebServer.SessionHeader).Single());
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/contexts")).StatusCode);

        var launch = await http.PostAsJsonAsync(WebServer.LaunchPath, new LaunchRequest("nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, launch.StatusCode);
        Assert.Contains("expired or was already used", await launch.Content.ReadAsStringAsync());

        var power = new HttpRequestMessage(HttpMethod.Post, "/api/session/power")
        {
            Headers = { { WebServer.TokenHeader, Token }, { WebServer.SessionHeader, "an-earlier-connection" } },
            Content = JsonContent.Create(new { on = true }),
        };
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(power)).StatusCode);
        Assert.False(host.Current.Power);

        await stop.CancelAsync();
        Assert.Equal(0, await server);
    }

    private static int? Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
}
