using System;
using System.Collections.Generic;
using System.IO;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>
/// How a document is named in a reference and in the add-in's own tables: its full path, or for a document never
/// saved its title. Keys compare without regard to case, as Windows paths do.
/// </summary>
public static class DocumentKeys
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Of(DocumentInfo document) => Of(document.Path, document.Title);

    public static string Of(string? path, string title) => string.IsNullOrEmpty(path) ? title : Path.GetFullPath(path);

    public static bool Same(string a, string b) => Comparer.Equals(a, b);
}

/// <summary>
/// Documents saved under a new name while this add-in is loaded: old key to new key. A reference issued while a part
/// was Part1 still finds it after it is saved as bracket.SLDPRT (design 371, References). Thread safe.
/// </summary>
public sealed class RenameMap
{
    const int MaxHops = 64;

    readonly object _gate = new object();
    readonly Dictionary<string, string> _next = new Dictionary<string, string>(DocumentKeys.Comparer);

    /// <summary>Records that the document known as <paramref name="oldKey"/> is now <paramref name="newKey"/>.</summary>
    public void Record(string oldKey, string newKey)
    {
        if (DocumentKeys.Same(oldKey, newKey)) return;
        lock (_gate)
        {
            _next[oldKey] = newKey;
            // A document saved back under an earlier name must not loop.
            _next.Remove(newKey);
        }
    }

    /// <summary>
    /// A document is open under <paramref name="key"/> again (opened, created or seen by the add-in): references to that
    /// key name it now, not the document it was once saved as.
    /// </summary>
    public void Forget(string key)
    {
        lock (_gate) _next.Remove(key);
    }

    /// <summary>The key a document known as <paramref name="key"/> has now, following every rename.</summary>
    public string Current(string key)
    {
        lock (_gate)
        {
            string current = key;
            for (int hop = 0; hop < MaxHops && _next.TryGetValue(current, out var next); hop++) current = next;
            return current;
        }
    }
}

/// <summary>
/// The documents the connected client opened or created (document.new, document.open). document.close closes only
/// these unless the agent says the user asked (design 371, Documents). Keyed by each document's key now: a save under
/// a new name moves its entry (<see cref="Rename"/>). Cleared when the client disconnects. Thread safe.
/// </summary>
public sealed class OwnedDocuments
{
    readonly object _gate = new object();
    readonly HashSet<string> _keys = new HashSet<string>(DocumentKeys.Comparer);

    public void Add(string key)
    {
        lock (_gate) _keys.Add(key);
    }

    /// <summary>The document known as <paramref name="oldKey"/> is now <paramref name="newKey"/>; still owned if it was.</summary>
    public void Rename(string oldKey, string newKey)
    {
        lock (_gate)
        {
            if (_keys.Remove(oldKey)) _keys.Add(newKey);
        }
    }

    public bool Contains(string key)
    {
        lock (_gate) return _keys.Contains(key);
    }

    public void Remove(string key)
    {
        lock (_gate) _keys.Remove(key);
    }

    public void Clear()
    {
        lock (_gate) _keys.Clear();
    }

    public int Count
    {
        get { lock (_gate) return _keys.Count; }
    }
}
