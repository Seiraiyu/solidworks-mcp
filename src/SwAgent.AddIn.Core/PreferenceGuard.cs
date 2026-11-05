using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>A SOLIDWORKS toggle preference automation needs set, and the value it needs.</summary>
public sealed class AutomationPreference
{
    public AutomationPreference(string name, int id, bool automationValue)
    {
        Name = name;
        Id = id;
        AutomationValue = automationValue;
    }

    public string Name { get; }
    public int Id { get; }
    public bool AutomationValue { get; }
}

/// <summary>Reads and writes SOLIDWORKS toggle preferences. UI thread only. Either call may throw (COM).</summary>
public interface IPreferenceStore
{
    bool Get(int id);
    void Set(int id, bool value);
}

/// <summary>The journal on disk: the user's values and what was applied, so a crash can be undone at the next load.</summary>
public sealed class PreferenceJournal
{
    [Required]
    public List<PreferenceJournalEntry> Entries { get; set; } = new List<PreferenceJournalEntry>();
}

public sealed class PreferenceJournalEntry
{
    [Required]
    public string Name { get; set; } = "";
    public int Id { get; set; }
    public bool Original { get; set; }
    public bool Applied { get; set; }
}

/// <summary>
/// Applies the automation preferences for a connected client and restores the user's values afterwards: on
/// disconnect, on unload, and -- through a journal written before anything is changed -- at the next load after a
/// crash. Only a preference still at the value we applied is put back; one the user changed meanwhile is left.
/// The originals in a journal already on disk are never replaced. Every method runs on the UI thread and none throws.
/// </summary>
public sealed class PreferenceGuard
{
    readonly IPreferenceStore _store;
    readonly IReadOnlyList<AutomationPreference> _preferences;
    readonly string _journalPath;
    readonly ILog _log;
    volatile List<PreferenceJournalEntry>? _applied;
    volatile IReadOnlyList<string> _notTaken = Array.Empty<string>();

    public PreferenceGuard(IPreferenceStore store, IReadOnlyList<AutomationPreference> preferences, string journalPath, ILog log)
    {
        _store = store;
        _preferences = preferences;
        _journalPath = journalPath;
        _log = log;
    }

    /// <summary><c>%LOCALAPPDATA%\solidworks-mcp\preferences-journal.json</c>.</summary>
    public static string DefaultJournalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "preferences-journal.json");

    public bool Applied => _applied != null;

    public bool RecoveredAtLoad { get; private set; }

    /// <summary>Preferences the last Apply could not set or that did not stick; read from any thread.</summary>
    public IReadOnlyList<string> NotTaken => _notTaken;

    /// <summary>At load: restores what an earlier session left applied, if its journal is still on disk.</summary>
    public void Recover()
    {
        var journal = ReadJournal();
        if (journal == null) return;
        _log.Warn("preferences: a journal from an earlier session was found (SOLIDWORKS or the add-in stopped without restoring); restoring");
        if (RestoreEntries(journal.Entries, "recovered at load")) DeleteJournal();
        else _log.Error("preferences: some preferences could not be restored; the journal is kept for the next load");
        RecoveredAtLoad = true;
    }

    /// <summary>
    /// Records the user's values (or keeps those of a journal already on disk), writes the journal, marks the guard
    /// applied, then sets the automation values. Idempotent while applied.
    /// </summary>
    public void Apply()
    {
        if (_applied != null) return;
        var existing = ReadJournal()?.Entries.ToDictionary(e => e.Id) ?? new Dictionary<int, PreferenceJournalEntry>();
        var entries = new List<PreferenceJournalEntry>();
        var notTaken = new List<string>();
        foreach (var p in _preferences)
        {
            bool original;
            if (existing.TryGetValue(p.Id, out var kept))
            {
                original = kept.Original;
            }
            else if (!TryGet(p, out original))
            {
                notTaken.Add(p.Name);
                continue;
            }
            entries.Add(new PreferenceJournalEntry { Name = p.Name, Id = p.Id, Original = original, Applied = p.AutomationValue });
        }
        try
        {
            WriteJournal(new PreferenceJournal { Entries = entries });
        }
        catch (Exception e)
        {
            _log.Error($"preferences: the journal {_journalPath} could not be written ({e.Message}); no preference was changed", e);
            _notTaken = _preferences.Select(p => p.Name).ToList();
            return;
        }
        _applied = entries;
        foreach (var e in entries)
        {
            try
            {
                if (_store.Get(e.Id) != e.Applied)
                {
                    _store.Set(e.Id, e.Applied);
                    _log.Info($"preferences: {e.Name} set to {e.Applied} for automation (user's value {e.Original})");
                }
                if (_store.Get(e.Id) != e.Applied)
                {
                    _log.Warn($"preferences: SOLIDWORKS did not keep {e.Name} at {e.Applied}");
                    notTaken.Add(e.Name);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"preferences: setting {e.Name} failed", ex);
                notTaken.Add(e.Name);
            }
        }
        _notTaken = notTaken;
    }

    /// <summary>Puts back the user's values; nothing when not applied. The journal goes only if every restore worked.</summary>
    public void Restore(string why)
    {
        var entries = _applied;
        if (entries == null) return;
        bool ok = RestoreEntries(entries, why);
        _applied = null;
        _notTaken = Array.Empty<string>();
        if (ok) DeleteJournal();
        else _log.Error("preferences: some preferences could not be restored; the journal is kept for the next load");
    }

    bool TryGet(AutomationPreference p, out bool value)
    {
        try
        {
            value = _store.Get(p.Id);
            return true;
        }
        catch (Exception e)
        {
            _log.Error($"preferences: reading {p.Name} failed; it is left alone", e);
            value = false;
            return false;
        }
    }

    bool RestoreEntries(IEnumerable<PreferenceJournalEntry> entries, string why)
    {
        bool ok = true;
        foreach (var e in entries.Where(e => e.Original != e.Applied))
        {
            try
            {
                bool now = _store.Get(e.Id);
                if (now != e.Applied)
                {
                    // No longer at the automation value, so already at the user's: they set it back by hand, or
                    // SOLIDWORKS did not keep the applied value (it does not persist one across a kill).
                    _log.Info($"preferences: {e.Name} already at the user's value {now}; nothing to restore ({why})");
                    continue;
                }
                _store.Set(e.Id, e.Original);
                _log.Info($"preferences: {e.Name} restored to {e.Original} ({why})");
            }
            catch (Exception ex)
            {
                _log.Error($"preferences: restoring {e.Name} failed ({why})", ex);
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>The journal on disk, or null when there is none or it is unreadable (then set aside as .bad).</summary>
    PreferenceJournal? ReadJournal()
    {
        try
        {
            if (!File.Exists(_journalPath)) return null;
            return JsonCodec.Deserialize<PreferenceJournal>(File.ReadAllText(_journalPath));
        }
        catch (Exception e)
        {
            _log.Error($"preferences: the journal {_journalPath} is unreadable ({e.Message}); set aside as .bad, not trusted");
            SetAside();
            return null;
        }
    }

    void WriteJournal(PreferenceJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        string temp = _journalPath + ".tmp";
        File.WriteAllText(temp, JsonCodec.Serialize(journal));
        if (File.Exists(_journalPath)) File.Replace(temp, _journalPath, null);
        else File.Move(temp, _journalPath);
    }

    void DeleteJournal()
    {
        try
        {
            File.Delete(_journalPath);
        }
        catch (Exception e)
        {
            _log.Warn($"preferences: could not delete the journal {_journalPath}: {e.Message}");
        }
    }

    void SetAside()
    {
        try
        {
            string bad = _journalPath + ".bad";
            if (File.Exists(bad)) File.Delete(bad);
            File.Move(_journalPath, bad);
        }
        catch (Exception e)
        {
            _log.Warn($"preferences: could not set the journal aside: {e.Message}");
        }
    }
}
