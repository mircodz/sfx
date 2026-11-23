namespace Sfx.Core;

/// <summary>
/// sfx won't do what was asked, and the message says why in words meant for the user: the input is wrong, or the
/// cluster's state is in the way (<see cref="Conflict"/>), e.g. a version that isn't provisioned. Nothing was changed.
/// </summary>
public sealed class RefusedException(string message, bool conflict = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Whether it's the cluster's state that's in the way rather than the input.</summary>
    public bool Conflict => conflict;
}
