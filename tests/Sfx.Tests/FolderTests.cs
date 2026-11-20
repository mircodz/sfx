using Sfx.Core;
using Sfx.Core.Auth;
using Sfx.Tui;

namespace Sfx.Tests;

public sealed class FolderTests : IDisposable
{
    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sfx-{Guid.NewGuid():N}", "config.toml");
    private readonly ClusterConnector _connector = new(BuiltInAuth.Providers);

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" / ", null)]
    [InlineData("prod", "prod")]
    [InlineData(" /prod//europe/ ", "prod/europe")]
    [InlineData("prod / europe", "prod/europe")]
    public void Normalizes_folder_paths(string? path, string? expected)
    {
        Assert.Equal(expected, FolderPath.Normalize(path));
    }

    [Fact]
    public void Ancestors_go_from_the_outermost_folder_in()
    {
        Assert.Equal(["prod", "prod/europe", "prod/europe/west"], FolderPath.Ancestors("/prod/europe/west/"));
        Assert.Equal(["prod"], FolderPath.Ancestors("prod"));
        Assert.Empty(FolderPath.Ancestors(""));
    }

    [Fact]
    public void Folder_round_trips_without_becoming_an_auth_option()
    {
        var store = ContextStore.Load(_path);
        store.Set(new ClusterContext("prod-weu", "prod-weu", [], new AuthOptions(), Folder: "/prod//europe/"));
        store.Set(new ClusterContext("local", "localhost", [], new AuthOptions(), Folder: " / "));
        store.Save();

        var text = File.ReadAllText(_path);
        Assert.Contains("folder = \"prod/europe\"", text);
        Assert.Single(text.Split('\n'), line => line.StartsWith("folder", StringComparison.Ordinal));

        var reloaded = ContextStore.Load(_path);
        var weu = reloaded.Find("prod-weu")!;
        Assert.Equal("prod/europe", weu.Folder);
        Assert.Empty(weu.Options.Keys);
        _connector.Resolve(weu);
        Assert.Null(reloaded.Find("local")!.Folder);
    }

    [Fact]
    public void Reads_folder_tables_and_ignores_unknown_keys_and_colors()
    {
        Write("""
            [contexts.a]
            endpoint = "a"

            [folders.prod]
            label = "PROD"
            color = "Danger"
            readonly = true
            owner = "team"

            [folders."/prod/europe/"]
            label = " EU "
            color = "purple"

            [folders.dev.local]
            color = "success"
            """);

        var folders = ContextStore.Load(_path).Folders;

        Assert.Equal(["dev", "dev/local", "prod", "prod/europe"], folders.Select(f => f.Path));
        Assert.Equal(new FolderSettings("prod", "PROD", "danger", true), folders[2]);
        Assert.Equal(new FolderSettings("prod/europe", "EU", null, false), folders[3]);
        Assert.Equal(new FolderSettings("dev/local", null, "success", false), folders[1]);
    }

    [Fact]
    public void Resolve_takes_the_nearest_label_and_color_and_any_read_only_folder()
    {
        Write("""
            [contexts.weu]
            endpoint = "weu"
            folder = "prod/europe"
            power = true

            [contexts.us]
            endpoint = "us"
            folder = "prod/us"

            [contexts.dev]
            endpoint = "dev"
            power = true

            [folders.prod]
            label = "PROD"
            color = "danger"
            readonly = true

            [folders."prod/europe"]
            label = "EU"
            """);
        var store = ContextStore.Load(_path);

        var weu = store.Resolve(store.Find("weu")!);
        Assert.Equal("EU", weu.Label);
        Assert.Equal("danger", weu.Color);
        Assert.True(weu.Context.ReadOnly);
        Assert.False(weu.Context.Power);
        Assert.Equal("prod/europe", weu.Context.Folder);

        var us = store.Resolve(store.Find("us")!);
        Assert.Equal("PROD", us.Label);
        Assert.True(us.Context.ReadOnly);

        var dev = store.Find("dev")!;
        Assert.Equal(new ResolvedContext(dev, null, null), store.Resolve(dev));

        // Resolving doesn't change what's saved.
        Assert.False(store.Find("weu")!.ReadOnly);
        Assert.True(store.Find("weu")!.Power);
    }

    [Fact]
    public void Opening_a_context_in_a_read_only_folder_is_read_only_even_with_power()
    {
        Write("""
            context = "weu"

            [contexts.weu]
            endpoint = "weu"
            folder = "prod/europe"

            [folders.prod]
            readonly = true
            """);

        var context = SfxHost.SelectContext(CommandLine.Parse(["--power"], NoFlags), ContextStore.Load(_path));

        Assert.True(context.ReadOnly);
        Assert.False(context.Power);
    }

    [Fact]
    public void Context_add_files_the_context_in_a_folder()
    {
        var output = Run("context", "add", "prod-weu", "prod-weu.example.com", "--folder", "/prod/europe/");

        Assert.Equal("prod/europe", ContextStore.Load(_path).Find("prod-weu")!.Folder);
        Assert.Equal($"Saved context 'prod-weu' in folder 'prod/europe' to {_path}.", output.Trim());
    }

    [Fact]
    public void Context_move_changes_the_folder_and_keeps_the_rest()
    {
        Run("context", "add", "weu", "weu.example.com", "--readonly");

        Assert.Equal("Moved context 'weu' to folder 'prod/europe'.", Run("context", "move", "WEU", "prod/europe").Trim());
        var moved = ContextStore.Load(_path).Find("weu")!;
        Assert.Equal("prod/europe", moved.Folder);
        Assert.True(moved.ReadOnly);
        Assert.Equal("weu.example.com", moved.Endpoint);

        Assert.Equal("Moved context 'weu' to the top level.", Run("context", "move", "weu", "/").Trim());
        Assert.Null(ContextStore.Load(_path).Find("weu")!.Folder);
        Run("context", "move", "weu", "dev");
        Run("context", "move", "weu", "");
        Assert.Null(ContextStore.Load(_path).Find("weu")!.Folder);

        Assert.Throws<UsageException>(() => Run("context", "move", "nope", "prod"));
    }

    [Theory]
    [InlineData("context", "use", "prod", "--folder", "prod")]
    [InlineData("context", "move", "prod", "x", "--folder", "prod")]
    [InlineData("localhost", "--folder", "prod")]
    [InlineData("--folder=prod")]
    public void Folder_option_only_applies_to_context_add(params string[] args)
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(args, NoFlags));
        Assert.Contains("sfx context move", error.Message);
    }

    [Fact]
    public void Folder_option_is_not_an_auth_option()
    {
        var options = CommandLine.Parse(["context", "add", "a", "b", "--folder", "prod"], NoFlags);

        Assert.Equal("prod", options.Folder);
        Assert.Empty(options.AuthOptions.Keys);
    }

    [Fact]
    public void List_groups_contexts_under_their_folders()
    {
        Write("""
            context = "local"

            [contexts.local]
            endpoint = "localhost"

            [contexts.p2]
            endpoint = "p2.example.com"
            folder = "prod2"

            [contexts.prod-weu]
            endpoint = "prod-weu.example.com"
            folder = "prod/europe"

            [contexts.prod-us]
            endpoint = "prod-us.example.com"
            folder = "prod"

            [folders.prod]
            label = "PROD"
            readonly = true

            [folders."prod/europe"]
            label = "EU"
            """);

        var output = Run("context", "list");

        Assert.Equal("""
              NAME          AUTH          MODE      ENDPOINT
            * local         none          normal    localhost
              prod/
                prod-us     none          readonly  prod-us.example.com   [PROD]
                europe/
                  prod-weu  none          readonly  prod-weu.example.com  [EU]
              prod2/
                p2          none          normal    p2.example.com

            """, output.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void List_without_folders_is_unchanged()
    {
        Write("""
            context = "dev"

            [contexts.dev]
            endpoint = "localhost"

            [contexts.prod]
            endpoint = "https://prod"
            auth = "cert"
            readonly = true
            """);

        Assert.Equal("""
              NAME  AUTH          MODE      ENDPOINT
            * dev   none          normal    localhost
              prod  cert          readonly  https://prod

            """, Run("context", "list").ReplaceLineEndings("\n"));
    }

    private void Write(string toml)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, toml);
    }

    private string Run(params string[] args)
    {
        var options = CommandLine.Parse(args, NoFlags);
        var output = new StringWriter();
        ContextCommand.Run(options.Arguments.Skip(1).ToList(), options, ContextStore.Load(_path), _connector, output);
        return output.ToString();
    }
}
