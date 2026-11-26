using System.Security.Cryptography;

namespace Sfx.Web;

public sealed record LaunchRequest(string? Code);

public sealed record LaunchDto(string Token);

/// <summary>
/// Codes for the link sfx ui opens in the browser, which other local users can see on the browser's command line; each
/// is traded for the access token once, within a minute.
/// </summary>
internal sealed class LaunchCodes(string token, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);

    public string Issue()
    {
        var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            var now = time.GetUtcNow();
            foreach (var expired in _expiries.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            {
                _expiries.Remove(expired);
            }
            _expiries[code] = now + Lifetime;
        }
        return code;
    }

    /// <summary>The token, if the code was issued and neither used nor expired; a code is gone after one try either way.</summary>
    public string? Redeem(string? code)
    {
        lock (_lock)
        {
            return code is not null && _expiries.Remove(code, out var expires) && time.GetUtcNow() < expires ? token : null;
        }
    }
}
