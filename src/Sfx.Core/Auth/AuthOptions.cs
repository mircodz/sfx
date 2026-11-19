namespace Sfx.Core.Auth;

/// <summary>Named, possibly repeated, option values. Keys are case-insensitive and have no leading dashes.</summary>
public sealed class AuthOptions
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Keys => _values.Keys;

    public void Add(string key, string value)
    {
        if (!_values.TryGetValue(key, out var list))
        {
            _values[key] = list = [];
        }
        list.Add(value);
    }

    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary>The last value given for <paramref name="key"/>, or null.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var list) ? list[^1] : null;

    public IReadOnlyList<string> GetAll(string key) => _values.TryGetValue(key, out var list) ? list : [];

    public string Require(string key) =>
        Get(key) ?? throw new AuthConfigurationException($"--{key} is required.");
}
