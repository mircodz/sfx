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

    private const string Commented = """
        # sfx contexts
        context = "prod" # the usual

        # Production, west europe
        [contexts.prod]
        endpoint = "https://prod:19080" # behind the VPN
        auth = ["cert"]
        # cert-thumbprint = "OLD"
        cert-thumbprint = "AB12"

        [contexts.dev]
        endpoint = 'localhost'

        # Folders
        [folders.prod]
        label = "PROD" # shouted

        """;

    [Fact]
    public void Comments_survive_adding_changing_and_removing_contexts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Commented);

        var store = ContextStore.Load(_path);
        store.Set(new ClusterContext("staging", "https://staging:19080", [], new AuthOptions(), ReadOnly: true));
        store.Set(store.Find("dev")! with { Folder = "local" });
        store.Save();
        Assert.Equal(
            """
            # sfx contexts
            context = "prod" # the usual

            # Production, west europe
            [contexts.prod]
            endpoint = "https://prod:19080" # behind the VPN
            auth = ["cert"]
            # cert-thumbprint = "OLD"
            cert-thumbprint = "AB12"

            [contexts.dev]
            endpoint = 'localhost'
            folder = "local"

            [contexts.staging]
            endpoint = "https://staging:19080"
            readonly = true

            # Folders
            [folders.prod]
            label = "PROD" # shouted

            """,
            File.ReadAllText(_path));

        store.Remove("prod");
        store.Remove("staging");
        store.Save();
        Assert.Equal(
            """
            # sfx contexts

            [contexts.dev]
            endpoint = 'localhost'
            folder = "local"

            # Folders
            [folders.prod]
            label = "PROD" # shouted

            """,
            File.ReadAllText(_path));
        Assert.Null(ContextStore.Load(_path).Active);
    }

    [Fact]
    public void A_save_keeps_what_was_written_to_the_file_since_the_load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Commented);
        var store = ContextStore.Load(_path);

        var other = ContextStore.Load(_path);
        other.Set(new ClusterContext("ext", "https://ext:19080", [], new AuthOptions()));
        other.Set(other.Find("dev")! with { Endpoint = "devbox" });
        other.Active = "ext";
        other.Save();

        store.Set(store.Find("dev")! with { Folder = "local" });
        store.Set(new ClusterContext("mine", "https://mine:19080", [], new AuthOptions()));
        store.Save();

        Assert.Equal(["dev", "ext", "mine", "prod"], store.Contexts.Select(c => c.Name));
        Assert.Equal("ext", store.Active);
        var reloaded = ContextStore.Load(_path);
        Assert.Equal(["dev", "ext", "mine", "prod"], reloaded.Contexts.Select(c => c.Name));
        Assert.Equal(("devbox", "local"), (reloaded.Find("dev")!.Endpoint, reloaded.Find("dev")!.Folder));
        Assert.Equal("ext", reloaded.Active);
        Assert.Contains("# Production, west europe", File.ReadAllText(_path));
    }

    [Fact]
    public void Removing_a_context_removed_elsewhere_changes_nothing_else()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Commented);
        var store = ContextStore.Load(_path);
        File.WriteAllText(_path, Commented.Replace("context = \"prod\"", "context = \"dev\"").Replace("[contexts.prod]", "[contexts.prod2]"));

        Assert.True(store.Remove("prod"));
        store.Save();

        Assert.Equal("dev", store.Active);
        Assert.Equal(["dev", "prod2"], store.Contexts.Select(c => c.Name));
    }

    [Fact]
    public void Layouts_the_edits_cannot_follow_are_written_out_whole()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, """
            [contexts]
            prod = { endpoint = "https://prod:19080", auth = "cert" }
            """);

        var store = ContextStore.Load(_path);
        store.Set(store.Find("prod")! with { Endpoint = "https://prod2:19080" });
        store.Save();

        var prod = ContextStore.Load(_path).Find("prod")!;
        Assert.Equal(("https://prod2:19080", "cert"), (prod.Endpoint, Assert.Single(prod.Auth)));
    }

    [Fact]
    public void Saves_by_replacing_the_file_and_keeps_its_permissions()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, Commented);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var store = ContextStore.Load(_path);
        store.Active = "dev";
        store.Save();
        store.Set(new ClusterContext("new", "localhost", [], new AuthOptions()));
        store.Save();

        Assert.Equal(["config.toml"], Directory.GetFiles(Path.GetDirectoryName(_path)!).Select(Path.GetFileName));
        Assert.Equal("dev", ContextStore.Load(_path).Active);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_path));
        }
    }

    [Fact]
    public void Saving_through_a_symlink_keeps_the_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path)!;
        var target = Path.Combine(directory, "dotfiles", "sfx.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, Commented);
        File.CreateSymbolicLink(_path, target);

        var store = ContextStore.Load(_path);
        store.Active = "dev";
        store.Save();

        Assert.NotNull(new FileInfo(_path).LinkTarget);
        Assert.Contains("context = \"dev\" # the usual", File.ReadAllText(target));
        Assert.Equal(["sfx.toml"], Directory.GetFiles(Path.GetDirectoryName(target)!).Select(Path.GetFileName));
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
