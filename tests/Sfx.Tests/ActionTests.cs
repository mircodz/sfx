using System.Text;
using Cellar.Terminal;
using Sfx.Tui;

namespace Sfx.Tests;

public class ActionTests
{
    private static KeyEvent Char(char c, KeyModifiers modifiers = KeyModifiers.None) => new(Key.Char, new Rune(c), modifiers);

    [Fact]
    public void Chords_match_plain_ctrl_and_named_keys()
    {
        Assert.True(KeyChord.Of('r').Matches(Char('r')));
        Assert.False(KeyChord.Of('r').Matches(Char('r', KeyModifiers.Control)));
        Assert.False(KeyChord.Of('r').Matches(Char('R')));
        Assert.True(KeyChord.Of('G').Matches(Char('G', KeyModifiers.Shift)));

        Assert.True(KeyChord.Ctrl('p').Matches(Char('p', KeyModifiers.Control)));
        Assert.True(KeyChord.Ctrl('p').Matches(Char('P', KeyModifiers.Control)));
        Assert.False(KeyChord.Ctrl('p').Matches(Char('p')));

        Assert.True(KeyChord.Named(Key.Backspace).Matches(new KeyEvent(Key.Backspace, default, KeyModifiers.None)));
        Assert.False(KeyChord.Named(Key.Backspace).Matches(Char('h')));
    }

    [Fact]
    public void Chords_format_for_help_and_footer()
    {
        Assert.Equal("ctrl+p", KeyChord.Ctrl('p').ToString());
        Assert.Equal("^p", KeyChord.Ctrl('p').Short);
        Assert.Equal("backspace", KeyChord.Named(Key.Backspace).ToString());
        Assert.Equal("bksp", KeyChord.Named(Key.Backspace).Short);

        var action = new ShellAction("Back", "Nav", _ => { }, [KeyChord.Named(Key.Backspace), KeyChord.Of('h')], ["back"]);
        Assert.Equal("backspace/h  :back", action.Hint);
    }

    [Fact]
    public void Earlier_actions_win_a_key_so_views_can_override_globals()
    {
        var ran = "";
        ShellAction[] actions =
        [
            new("View refresh", "View", _ => ran = "view", [KeyChord.Of('r')]),
            new("Global refresh", "General", _ => ran = "global", [KeyChord.Of('r')]),
        ];

        ActionLookup.ForKey(actions, Char('r'))!.Run(null);

        Assert.Equal("view", ran);
        Assert.Null(ActionLookup.ForKey(actions, Char('x')));
    }

    [Fact]
    public void Commands_match_any_alias_and_pass_the_rest_as_argument()
    {
        ShellAction[] actions = [new("Contexts", "Views", _ => { }, Commands: ["ctx", "context"])];

        var match = ActionLookup.ForCommand(actions, "  CONTEXT   prod west ")!.Value;

        Assert.Equal("Contexts", match.Action.Title);
        Assert.Equal("prod west", match.Argument);
        Assert.Null(ActionLookup.ForCommand(actions, "ctx")!.Value.Argument);
        Assert.Null(ActionLookup.ForCommand(actions, "nodes"));
        Assert.Null(ActionLookup.ForCommand(actions, "   "));
    }
}
