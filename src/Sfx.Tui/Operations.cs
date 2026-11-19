namespace Sfx.Tui;

/// <summary>How careful to be before running an action.</summary>
public enum ActionRisk
{
    /// <summary>Doesn't change the cluster.</summary>
    None,

    /// <summary>Changes the cluster; asks y/n first.</summary>
    Confirm,

    /// <summary>Hard to undo; the user types the target's name to go ahead.</summary>
    Destructive,
}

/// <summary>A toggle offered in the confirmation, e.g. <c>i</c> to ignore placement constraints.</summary>
public sealed record OperationOption(char Key, string Label);

/// <summary>
/// One change to the cluster, described for the confirmation, the status line and the action log.
/// Every mutating action goes through <see cref="IActionHost.Run"/> so they all behave the same way.
/// </summary>
/// <param name="Title">What happens, e.g. "Restart replica 1234 on _Node_1".</param>
/// <param name="Target">What to type to confirm a destructive operation: the replica id, node or app name.</param>
/// <param name="Run">Gets the keys of the options that were switched on.</param>
/// <param name="Warning">Shown in the confirmation: what could go wrong.</param>
public sealed record Operation(
    string Title,
    string Target,
    ActionRisk Risk,
    Func<CancellationToken, IReadOnlySet<char>, Task> Run,
    string? Warning = null,
    IReadOnlyList<OperationOption>? Options = null);

/// <summary>One entry of a pick list.</summary>
public sealed record Choice(string Title, string? Subtitle, Action Pick);

/// <summary>What the screen offers actions: confirm-and-run, pick from a list, and ask for a value.</summary>
public interface IActionHost
{
    void Run(Operation operation);

    /// <summary>Loads the choices off the UI thread, then shows them as a searchable list.</summary>
    void Pick(string title, Func<CancellationToken, Task<IReadOnlyList<Choice>>> load);

    /// <summary>Asks for one value in the footer, prefilled with <paramref name="initial"/>.</summary>
    void Ask(string label, string initial, Action<string> submit);
}

/// <summary>An action on one kind of entity. Bound to a table's selected row, or to the entity a describe page shows.</summary>
public sealed record EntityAction<T>(
    string Title,
    ActionRisk Risk,
    Action<T> Run,
    IReadOnlyList<KeyChord>? Keys = null,
    IReadOnlyList<string>? Commands = null,
    bool Power = false,
    Func<T, bool>? AppliesTo = null)
{
    public bool Applies(T subject) => AppliesTo?.Invoke(subject) ?? true;

    public ShellAction Bind(T subject, string group) =>
        new(Title, group, _ => Run(subject), Keys, Commands, Risk: Risk, Power: Power);
}
