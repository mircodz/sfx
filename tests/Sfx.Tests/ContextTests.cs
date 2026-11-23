using Sfx.Core;
using Sfx.Core.Auth;

namespace Sfx.Tests;

public sealed class ContextStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sfx-{Guid.NewGuid():N}", "config.toml");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Saves_and_reloads_contexts_with_their_auth_options()
    {
        var options = new AuthOptions();
        options.Add("cert-thumbprint", "AB12");
        options.Add("server-cn", "a.example.com");
        options.Add("server-cn", "b.example.com");

        var store = ContextStore.Load(_path);
        store.Set(new ClusterContext("prod", "https://prod:19080", ["cert"], options));
        store.Set(new ClusterContext("local", "localhost", [], new AuthOptions()));
        store.Active = "prod";
        store.Save();

        var reloaded = ContextStore.Load(_path);
        Assert.Equal("prod", reloaded.Active);
        Assert.Equal(["local", "prod"], reloaded.Contexts.Select(c => c.Name));

        var prod = reloaded.Find("PROD")!;
        Assert.Equal("https://prod:19080", prod.Endpoint);
        Assert.Equal(["cert"], prod.Auth);
        Assert.Equal("AB12", prod.Options.Get("cert-thumbprint"));
        Assert.Equal(["a.example.com", "b.example.com"], prod.Options.GetAll("server-cn"));
        Assert.Empty(reloaded.Find("local")!.Auth);
    }

    [Fact]
    public void Set_replaces_and_remove_clears_the_active_context()
    {
        var store = ContextStore.Load(_path);
        store.Set(new ClusterContext("dev", "old-host", [], new AuthOptions()));
        store.Set(new ClusterContext("DEV", "new-host", [], new AuthOptions()));
        store.Active = "DEV";

        Assert.Equal("new-host", Assert.Single(store.Contexts).Endpoint);
        Assert.True(store.Remove("dev"));
        Assert.Null(store.Active);
        Assert.False(store.Remove("dev"));
    }

    [Fact]
    public void Reads_hand_written_files()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            context = "local"

            [contexts.local]
            endpoint = "localhost"

            [contexts.secure]
            endpoint = "https://secure"
            auth = "aad"
            server-thumbprint = ["AA", "BB"]
            """);

        var store = ContextStore.Load(_path);

        var secure = store.Find("secure")!;
        Assert.Equal(["aad"], secure.Auth);
        Assert.Equal(["AA", "BB"], secure.Options.GetAll("server-thumbprint"));
    }

    [Fact]
    public void Changing_a_context_keeps_its_place_and_the_blank_lines_between_tables()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            [contexts.a]
            endpoint = "a:19080"

            [contexts.b]
            endpoint = "b:19080"

            [contexts.c]
            endpoint = "c:19080"

            [folders.prod]
            label = "PROD"
            """);
        var store = ContextStore.Load(_path);
        store.Set(store.Contexts.Single(c => c.Name == "b") with { Endpoint = "b2:19080" });
        store.Save();

        var text = File.ReadAllText(_path);
        Assert.True(text.IndexOf("[contexts.a]") < text.IndexOf("[contexts.b]") && text.IndexOf("[contexts.b]") < text.IndexOf("[contexts.c]"), text);
        Assert.Contains("endpoint = \"b2:19080\"\n\n[contexts.c]", text.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("\n\n\n", text.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("[contexts]", text);
    }
}

public class ClusterConnectorTests
{
    private readonly ClusterConnector _connector = new(BuiltInAuth.Providers);

    [Fact]
    public void Resolve_defaults_to_no_auth()
    {
        var providers = _connector.Resolve(new ClusterContext("local", "localhost", [], new AuthOptions()));

        Assert.Equal("none", Assert.Single(providers).Name);
    }

    [Fact]
    public void Resolve_rejects_unknown_methods_and_foreign_options()
    {
        var options = new AuthOptions();
        options.Add("cert-thumbprint", "AB");

        Assert.Throws<AuthConfigurationException>(() =>
            _connector.Resolve(new ClusterContext("x", "host", ["kerberos5"], new AuthOptions())));
        Assert.Throws<AuthConfigurationException>(() =>
            _connector.Resolve(new ClusterContext("x", "host", ["aad"], options)));
    }

    [Fact]
    public void Later_providers_replace_built_ins_with_the_same_name()
    {
        var custom = new NoAuthProvider();
        var connector = new ClusterConnector([.. BuiltInAuth.Providers, custom]);

        Assert.Same(custom, connector.Resolve(new ClusterContext("x", "host", ["none"], new AuthOptions()))[0]);
    }

}
