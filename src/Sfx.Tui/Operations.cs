using Cellar.Terminal;
using System.Globalization;
using Cellar.Text;
using Cellar.Theming;
using Cellar.Widgets;

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

/// <summary>Where a requested change has got to, as the cluster reports it.</summary>
/// <param name="Fraction">How far along, 0 to 1, when that can be counted (upgrade domains, partitions).</param>
public sealed record OperationState(bool Done, string Detail, bool Failed = false, double? Fraction = null)
{
    public static OperationState Running(string detail, double? fraction = null) => new(false, detail, Fraction: fraction);

    public static OperationState Finished(string detail) => new(true, detail);

    public static OperationState Failure(string detail) => new(true, detail, Failed: true);
}

/// <summary>
/// One change to the cluster, described for the confirmation, the activity strip and the action log.
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
    IReadOnlyList<OperationOption>? Options = null)
{
    /// <summary>Lines under the title in the confirmation, e.g. what an edit changes.</summary>
    public IReadOnlyList<StyledText> Details { get; init; } = [];

    /// <summary>
    /// Follows the change after the cluster accepted it, until it's done: given the time since it was requested,
    /// asks the cluster where it's got to. Null when the request is all there is to it.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task<OperationState>>? Track { get; init; }

    /// <summary>How long <see cref="Track"/> keeps asking before it leaves the change to the cluster.</summary>
    public TimeSpan TrackFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Where <see cref="Run"/> says how far it has got, e.g. an upload; the activity strip shows it.</summary>
    public OperationProgress? Progress { get; init; }
}

/// <summary>
/// How far a long request has got while it runs (an upload, say), before the cluster has accepted it. Created before
/// the operation so its <see cref="Operation.Run"/> can report to it; the screen shows the reports.
/// </summary>
public sealed class OperationProgress : IProgress<OperationState>
{
    private Action<OperationState>? _listener;

    /// <summary>The last report, if any.</summary>
    public OperationState? Last { get; private set; }

    public void Report(OperationState value)
    {
        Last = value;
        _listener?.Invoke(value);
    }

    public void Report(double? fraction, string detail) => Report(OperationState.Running(detail, fraction));

    /// <summary>Sends every report to <paramref name="listener"/> from now on.</summary>
    public void Attach(Action<OperationState> listener) => _listener = listener;
}

/// <summary>One entry of a pick list.</summary>
public sealed record Choice(string Title, string? Subtitle, Action Pick);

/// <summary>What the screen offers actions: confirm-and-run, pick from a list, ask for a value, and edit a form.</summary>
public interface IActionHost
{
    /// <summary>Confirms (unless it changes nothing), runs, and tracks one operation.</summary>
    void Run(Operation operation);

    /// <summary>
    /// Runs one action on several rows: the operations <paramref name="queue"/> asks to <see cref="Run"/> are
    /// confirmed once, together, then run one after another until one fails. Actions that need a pick, an answer
    /// or a form can't be queued.
    /// </summary>
    /// <param name="title">What happens to them all, e.g. "Restart replica: 3 marked rows".</param>
    /// <param name="note">Shown in the confirmation, e.g. which rows are left out.</param>
    /// <param name="started">Called once they're confirmed.</param>
    void Batch(string title, Action queue, string? note = null, Action? started = null);

    /// <summary>Loads the choices off the UI thread, then shows them as a searchable list.</summary>
    void Pick(string title, Func<CancellationToken, Task<IReadOnlyList<Choice>>> load);

    /// <summary>Asks for one value in the footer, prefilled with <paramref name="initial"/>; Enter only goes through
    /// once <paramref name="validate"/> (if any) returns null.</summary>
    void Ask(string label, string initial, Func<string, string?>? validate, Action<string> submit);

    /// <summary>
    /// Loads a form's fields off the UI thread and shows them; once submitted, <paramref name="review"/> turns the
    /// edited fields into the operation to confirm. Going back from the confirmation returns to the form, and so does
    /// a review that refuses what was typed (it says why).
    /// </summary>
    /// <param name="unchanged">Whether the form may be submitted as it opened, e.g. one creating something with defaults.</param>
    void Edit(
        string title,
        Func<CancellationToken, Task<IReadOnlyList<FormField>>> load,
        Func<IReadOnlyList<FormField>, Operation> review,
        bool unchanged = false);

    /// <summary>Loads something off the UI thread (saying <paramref name="title"/> meanwhile), then hands it to
    /// <paramref name="then"/> on the UI thread; a failure is shown as an error instead.</summary>
    void Load<T>(string title, Func<CancellationToken, Task<T>> load, Action<T> then);

    /// <summary>
    /// Shows the pages <paramref name="load"/> returns, the first one at the bottom: with <paramref name="replace"/>,
    /// instead of the pages shown now (the first is a root page, e.g. the applications); otherwise on top of them.
    /// </summary>
    void Open(string title, Func<CancellationToken, Task<IReadOnlyList<IResourceView>>> load, bool replace = false);

    /// <summary>Says something in the footer, e.g. why an action can't run here.</summary>
    void Warn(string message);

    /// <summary>Opens the <c>:</c> prompt with <paramref name="text"/> typed, e.g. a command that takes a path.</summary>
    void Command(string text);

    /// <summary>Whether actions of this risk are offered: read-only contexts allow none that change the cluster,
    /// and power actions wait for <c>:power</c>.</summary>
    bool Allows(ActionRisk risk, bool power);
}

/// <summary>An action on one kind of entity. Bound to a table's selected row, or to the entity a describe page shows.</summary>
/// <param name="Keys">Its keys, the same on every kind of entity: <c>ctrl+r</c> restarts whatever is selected.</param>
public sealed record EntityAction<T>(
    string Title,
    ActionRisk Risk,
    Action<T> Run,
    IReadOnlyList<KeySequence>? Keys = null,
    IReadOnlyList<string>? Commands = null,
    bool Power = false,
    Func<T, bool>? AppliesTo = null)
{
    /// <summary>Its keymap id: the title, lower case with dashes, e.g. "restart-replica".</summary>
    public string Id { get; } = "row." + string.Join('-', Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray())).Where(word => word.Length > 0));

    public bool Applies(T subject) => AppliesTo?.Invoke(subject) ?? true;

    /// <summary>The action on one entity, offered while the host allows its risk.</summary>
    public AppAction Bind(T subject, string group, IActionHost host) =>
        new(Id, Title, _ => Run(subject)) { Group = group, Keys = Keys ?? [], Commands = Commands ?? [], When = () => host.Allows(Risk, Power) };
}

/// <summary>The confirmations operations ask for, as Cellar dialogs.</summary>
public static class Confirmations
{
    /// <summary>How many of a batch's operations the confirmation lists by name.</summary>
    public const int Listed = 8;

    /// <summary>y/n, or typing the target for a destructive operation; its options are toggles.</summary>
    public static ConfirmDialog For(Operation operation) =>
        new(operation.Title)
        {
            Details = operation.Details,
            Warning = operation.Warning,
            Risk = operation.Risk == ActionRisk.Destructive ? ConfirmRisk.Destructive : ConfirmRisk.Normal,
            ConfirmText = operation.Target,
            Toggles = Toggles(operation),
        };

    /// <summary>One confirmation for them all: lists them, and a destructive batch asks to type how many there are.</summary>
    public static ConfirmDialog ForBatch(string title, IReadOnlyList<Operation> operations, string? note)
    {
        var details = operations.Take(Listed).Select(operation => StyledText.Of("  " + operation.Title).Fg(Theme.Current.Foreground)).ToList();
        if (operations.Count > Listed)
        {
            details.Add(Styles.Muted($"  and {operations.Count - Listed} more"));
        }
        if (note is not null)
        {
            details.Add(Styles.Muted(note));
        }

        return new ConfirmDialog(title)
        {
            Details = details,
            Warning = (operations[0].Warning is { } warning ? warning + " " : "") + "They run one after another, and stop at the first that fails.",
            Risk = operations.Any(operation => operation.Risk == ActionRisk.Destructive) ? ConfirmRisk.Destructive : ConfirmRisk.Normal,
            ConfirmText = operations.Count.ToString(CultureInfo.InvariantCulture),
            Toggles = Toggles(operations[0]),
        };
    }

    /// <summary>The keys of the options switched on.</summary>
    public static IReadOnlySet<char> Chosen(ConfirmResult result) =>
        result.Toggles.Where(toggle => toggle.On).Select(toggle => toggle.Key).ToHashSet();

    private static List<ConfirmToggle> Toggles(Operation operation) =>
        operation.Options?.Select(option => new ConfirmToggle(option.Key, option.Label)).ToList() ?? [];
}
