using Cellar.Widgets;

namespace Sfx.Tui;

/// <summary>
/// What was typed at the <c>:</c> and <c>/</c> prompts, kept across runs in a file next to the config: one entry per
/// line, after the prompt's character (<c>:ctx prod</c>, <c>/payments</c>). The file is trimmed to the newest entries
/// when it's read. Failing to read or write it only loses the history.
/// </summary>
public sealed class CommandHistory
{
    private const int Capacity = 200;

    private readonly string _path;
    private readonly List<string> _lines;

    public CommandHistory(string path)
    {
        _path = path;
        _lines = Read(path);
        if (_lines.Count > 2 * Capacity)
        {
            _lines = _lines[^(2 * Capacity)..];
            Write(() => File.WriteAllLines(path, _lines));
        }
    }

    /// <summary>The history of the prompt that starts with <paramref name="prompt"/>, saving what's added to it.</summary>
    public InputHistory For(char prompt)
    {
        var history = new InputHistory(Capacity);
        history.Load(_lines.Where(line => line.Length > 1 && line[0] == prompt).Select(line => line[1..]));
        history.OnAdd = entry =>
        {
            if (!entry.Contains('\n'))
            {
                Write(() => File.AppendAllLines(_path, [prompt + entry]));
            }
        };
        return history;
    }

    private static List<string> Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Write(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
