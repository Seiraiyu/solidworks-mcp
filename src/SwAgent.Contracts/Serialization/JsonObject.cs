using System;
using System.Collections;
using System.Collections.Generic;

namespace SwAgent.Contracts.Serialization;

/// <summary>A JSON object that keeps its properties in the order they were added.</summary>
public sealed class JsonObject : IEnumerable<KeyValuePair<string, object?>>
{
    readonly List<string> _keys = new List<string>();
    readonly Dictionary<string, object?> _values = new Dictionary<string, object?>(StringComparer.Ordinal);

    public int Count => _keys.Count;

    public IReadOnlyList<string> Keys => _keys;

    public object? this[string key]
    {
        get => _values[key];
        set
        {
            if (!_values.ContainsKey(key)) _keys.Add(key);
            _values[key] = value;
        }
    }

    public void Add(string key, object? value)
    {
        if (_values.ContainsKey(key)) throw new ArgumentException($"property '{key}' is already present");
        _keys.Add(key);
        _values[key] = value;
    }

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    public bool Remove(string key)
    {
        if (!_values.Remove(key)) return false;
        _keys.Remove(key);
        return true;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var key in _keys) yield return new KeyValuePair<string, object?>(key, _values[key]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
