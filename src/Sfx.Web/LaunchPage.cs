using System.Net;
using System.Text.Encodings.Web;

namespace Sfx.Web;

/// <summary>
/// A page that sends the browser on to sfx ui with the token. The browser is opened on this file rather than on the
/// link, as a command line is visible to other local users (ps) and a file in a private folder isn't.
/// </summary>
internal sealed class LaunchPage : IDisposable
{
    private readonly string _folder;

    /// <summary>Gone after <paramref name="lifetime"/>, by which time the browser has read it, or on <see cref="Dispose"/>.</summary>
    public static LaunchPage Write(string url, TimeSpan lifetime)
    {
        // Owner-only on Unix (0700); on Windows the user's temp folder is already private.
        var folder = Directory.CreateTempSubdirectory("sfx-ui-").FullName;
        var path = System.IO.Path.Combine(folder, "open.html");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var writer = new StreamWriter(path, System.Text.Encoding.UTF8, options))
        {
            writer.Write(Html(url));
        }

        var page = new LaunchPage(folder, path);
        _ = Task.Delay(lifetime).ContinueWith(_ => page.Dispose(), TaskScheduler.Default);
        return page;
    }

    private LaunchPage(string folder, string path)
    {
        _folder = folder;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Encoded for an attribute and, separately, for a script string, whatever the URL holds.</summary>
    internal static string Html(string url) => $"""
        <!doctype html>
        <meta charset="utf-8">
        <meta name="referrer" content="no-referrer">
        <meta http-equiv="refresh" content="0;url={WebUtility.HtmlEncode(url)}">
        <title>sfx ui</title>
        <script>location.replace("{JavaScriptEncoder.Default.Encode(url)}");</script>
        <a href="{WebUtility.HtmlEncode(url)}">Open sfx ui</a>
        """;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Owner-only, and in a folder the OS clears.
        }
    }
}
