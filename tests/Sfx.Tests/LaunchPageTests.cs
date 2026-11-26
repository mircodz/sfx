using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Web;

namespace Sfx.Tests;

public class LaunchPageTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Url = $"http://127.0.0.1:7080/?token={Token}";

    [Fact]
    public void The_page_is_owner_only_and_leads_to_the_link_with_the_token()
    {
        using var page = LaunchPage.Write(Url, TimeSpan.FromMinutes(1));

        Assert.DoesNotContain(Token, page.Path);
        var html = File.ReadAllText(page.Path);
        Assert.Contains($"location.replace(\"{Url}\")", html);
        Assert.Contains($"content=\"0;url={Url}\"", html);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(page.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(page.Path)!));
        }

        page.Dispose();
        Assert.False(File.Exists(page.Path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(page.Path)));
    }

    [Fact]
    public void The_link_cannot_break_out_of_the_script_or_the_attributes()
    {
        var html = LaunchPage.Html("http://127.0.0.1:1/?token=\"</script><b x='&");

        Assert.DoesNotContain("</script><b", html);
        Assert.DoesNotContain("\"</", html);
        Assert.Contains("url=http://127.0.0.1:1/?token=&quot;&lt;/script&gt;&lt;b x=&#39;&amp;\"", html);
    }

    [Fact]
    public async Task The_page_is_removed_once_its_time_is_up()
    {
        var page = LaunchPage.Write(Url, TimeSpan.FromMilliseconds(50));

        for (var i = 0; i < 100 && File.Exists(page.Path); i++)
        {
            await Task.Delay(50);
        }

        Assert.False(File.Exists(page.Path));
    }

    [Fact]
    public async Task The_browser_is_opened_on_the_page_not_the_token_and_the_server_checks_the_tab_s_connection()
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
        var opened = new List<string>();
        using var stop = new CancellationTokenSource();
        var server = WebServer.RunAsync(host, new WebOptions(port, OpenBrowser: true, Token: Token), opened.Add, stop.Token);

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

        for (var i = 0; i < 50 && opened.Count == 0; i++)
        {
            await Task.Delay(100);
        }
        var path = Assert.Single(opened);
        Assert.DoesNotContain(Token, path);
        Assert.Contains($"http://127.0.0.1:{port}/?token={Token}", File.ReadAllText(path));

        Assert.Equal(HttpStatusCode.OK, contexts.StatusCode);
        Assert.Equal(host.Current.Id, contexts.Headers.GetValues(WebServer.SessionHeader).Single());
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/contexts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/launch", new { code = "x" })).StatusCode);

        var power = new HttpRequestMessage(HttpMethod.Post, "/api/session/power")
        {
            Headers = { { WebServer.TokenHeader, Token }, { WebServer.SessionHeader, "an-earlier-connection" } },
            Content = JsonContent.Create(new { on = true }),
        };
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(power)).StatusCode);
        Assert.False(host.Current.Power);

        await stop.CancelAsync();
        Assert.Equal(0, await server);
        Assert.False(File.Exists(path));
    }
}
