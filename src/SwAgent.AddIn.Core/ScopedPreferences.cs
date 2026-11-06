using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>Which of SOLIDWORKS' user-preference calls a preference is read and written with.</summary>
public enum PreferenceKind
{
    /// <summary>GetUserPreferenceToggle / SetUserPreferenceToggle: 0 or 1.</summary>
    Toggle,

    /// <summary>GetUserPreferenceIntegerValue / SetUserPreferenceIntegerValue.</summary>
    Integer,

    /// <summary>GetUserPreferenceDoubleValue / SetUserPreferenceDoubleValue, in SOLIDWORKS' own units.</summary>
    Double,
}

/// <summary>One SOLIDWORKS user preference an operation needs at a value for its own duration.</summary>
public sealed class PreferenceSetting
{
    public PreferenceSetting(string name, PreferenceKind kind, int id, double value,
        IReadOnlyList<double>? unrestorable = null, string? field = null, string? unrestorableWhy = null)
    {
        Name = name;
        Kind = kind;
        Id = id;
        Value = value;
        Unrestorable = unrestorable ?? Array.Empty<double>();
        Field = field;
        UnrestorableWhy = unrestorableWhy;
    }

    /// <summary>
    /// Values SOLIDWORKS will not take back through its API (3.E live run: swSTLQuality = 3, custom): a user who has
    /// one of them is never moved off it, since it could not be put back. Empty for most preferences.
    /// </summary>
    public IReadOnlyList<double> Unrestorable { get; }

    /// <summary>The request field the refusal names, for example options.resolution.</summary>
    public string? Field { get; }

    /// <summary>Why the value cannot be put back, and what the caller can do instead, for the refusal's message.</summary>
    public string? UnrestorableWhy { get; }

    /// <summary>The swUserPreference…_e member's name, for the log and the result.</summary>
    public string Name { get; }
    public PreferenceKind Kind { get; }
    public int Id { get; }

    /// <summary>The value: 0 or 1 for a toggle, the integer for an integer preference.</summary>
    public double Value { get; }

    public override string ToString() => $"{Name} = {Format(Kind, Value)}";

    internal static string Format(PreferenceKind kind, double value) =>
        kind == PreferenceKind.Toggle ? (value != 0 ? "true" : "false") : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Reads and writes SOLIDWORKS user preferences of every kind. UI thread only. Either call may throw (COM).</summary>
public interface ITypedPreferenceStore
{
    double Get(PreferenceKind kind, int id);
    void Set(PreferenceKind kind, int id, double value);
}

/// <summary>The scoped journal on disk: each preference's value before the operation and the value it set.</summary>
public sealed class ScopedPreferenceJournal
{
    [Required]
    public string Operation { get; set; } = "";

    [Required]
    public List<ScopedPreferenceEntry> Entries { get; set; } = new List<ScopedPreferenceEntry>();
}

public sealed class ScopedPreferenceEntry
{
    [Required]
    public string Name { get; set; } = "";
    public PreferenceKind Kind { get; set; }
    public int Id { get; set; }
    public double Original { get; set; }
    public double Applied { get; set; }
}

/// <summary>
/// Sets SOLIDWORKS user preferences for the duration of one operation and puts them back (design 371, Export: export
/// options are user preferences, recorded, set, and restored, journalled like phase 2's automation preferences). The
/// journal is written before anything is changed and deleted once everything is back, so a crash mid-operation is
/// undone at the next load by <see cref="Recover"/>. Only a preference still at the value set is put back. UI thread
/// only; nothing here throws except <see cref="Apply"/>, which first puts back whatever it had set.
/// </summary>
public sealed class ScopedPreferences
{
    readonly ITypedPreferenceStore _store;
    readonly string _journalPath;
    readonly ILog _log;

    public ScopedPreferences(ITypedPreferenceStore store, string journalPath, ILog log)
    {
        _store = store;
        _journalPath = journalPath;
        _log = log;
    }

    /// <summary><c>%LOCALAPPDATA%\solidworks-mcp\export-preferences-journal.json</c>.</summary>
    public static string DefaultJournalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp", "export-preferences-journal.json");

    public string JournalPath => _journalPath;

    /// <summary>At load: puts back what an operation interrupted by a crash left set, if its journal is still on disk.</summary>
    public void Recover()
    {
        ScopedPreferenceJournal? journal;
        try
        {
            if (!File.Exists(_journalPath)) return;
            journal = JsonCodec.Deserialize<ScopedPreferenceJournal>(File.ReadAllText(_journalPath));
        }
        catch (Exception e)
        {
            _log.Error($"preferences: the scoped journal {_journalPath} is unreadable ({e.Message}); set aside as .bad, not trusted");
            SetAside();
            return;
        }
        _log.Warn($"preferences: {journal.Operation} stopped without restoring its preferences; restoring them from its journal");
        if (Restore(journal.Entries, $"recovered at load after {journal.Operation}")) Delete();
        else _log.Error("preferences: some scoped preferences could not be restored; the journal is kept for the next load");
    }

    /// <summary>
    /// Each preference as SOLIDWORKS holds it now, as name = value (or what reading it threw), so a result SOLIDWORKS
    /// did not make from its preferences can say what they were. Never throws.
    /// </summary>
    public IReadOnlyList<string> ReadBack(IReadOnlyList<PreferenceSetting> settings) =>
        settings.Select(s =>
        {
            try
            {
                return $"{s.Name} = {PreferenceSetting.Format(s.Kind, _store.Get(s.Kind, s.Id))}";
            }
            catch (Exception e)
            {
                return $"{s.Name}: reading it failed ({e.Message})";
            }
        }).ToList();

    /// <summary>
    /// Records each preference's value, writes the journal, then sets every one and reads it back. Disposing the result
    /// puts them back and deletes the journal. api_error (after putting back what it set) when a preference cannot be
    /// read, written or does not keep its value.
    /// </summary>
    public IDisposable Apply(string operation, IReadOnlyList<PreferenceSetting> settings)
    {
        if (File.Exists(_journalPath))
        {
            // An earlier operation's values are still to be put back: never record over them.
            Recover();
            if (File.Exists(_journalPath))
            {
                throw new SwAgentException(ErrorCode.ApiError,
                    $"the SOLIDWORKS preferences an earlier call set are not all back yet (journal {_journalPath}); {operation} changes nothing until they are",
                    new JsonObject { ["journal"] = _journalPath });
            }
        }
        var entries = new List<ScopedPreferenceEntry>();
        foreach (var s in settings)
        {
            double original;
            try
            {
                original = _store.Get(s.Kind, s.Id);
            }
            catch (Exception e)
            {
                throw Failed(operation, s, $"reading it failed: {e.Message}");
            }
            if (!Same(s.Kind, original, s.Value) && s.Unrestorable.Any(u => Same(s.Kind, u, original)))
            {
                // Never change a preference SOLIDWORKS would not let us put back (3.E live run, round 3).
                throw new SwAgentException(ErrorCode.InvalidArgument,
                    $"{operation} would change the SOLIDWORKS preference {s.Name} from the user's {PreferenceSetting.Format(s.Kind, original)} to {PreferenceSetting.Format(s.Kind, s.Value)}, " +
                    $"and could not put it back: {s.UnrestorableWhy ?? "SOLIDWORKS does not take that value through its API"}; nothing was changed",
                    new JsonObject { ["field"] = s.Field, ["preference"] = s.Name, ["value"] = PreferenceSetting.Format(s.Kind, original) });
            }
            entries.Add(new ScopedPreferenceEntry { Name = s.Name, Kind = s.Kind, Id = s.Id, Original = original, Applied = s.Value });
        }
        try
        {
            Write(new ScopedPreferenceJournal { Operation = operation, Entries = entries });
        }
        catch (Exception e)
        {
            throw new SwAgentException(ErrorCode.ApiError,
                $"{operation} needs SOLIDWORKS preferences set for the call, but their journal {_journalPath} could not be written ({e.Message}); nothing was changed",
                new JsonObject { ["journal"] = _journalPath });
        }
        var set = new List<ScopedPreferenceEntry>();
        foreach (var e in entries)
        {
            string? problem = null;
            try
            {
                if (!Same(e.Kind, _store.Get(e.Kind, e.Id), e.Applied))
                {
                    _store.Set(e.Kind, e.Id, e.Applied);
                    set.Add(e);
                }
                double now = _store.Get(e.Kind, e.Id);
                if (!Same(e.Kind, now, e.Applied)) problem = $"SOLIDWORKS kept {PreferenceSetting.Format(e.Kind, now)}";
            }
            catch (Exception ex)
            {
                problem = $"setting it failed: {ex.Message}";
            }
            if (problem != null)
            {
                bool restored = Restore(set, $"{operation} could not set {e.Name}");
                if (restored) Delete();
                else _log.Error("preferences: some scoped preferences could not be restored; the journal is kept for the next load");
                throw Failed(operation, new PreferenceSetting(e.Name, e.Kind, e.Id, e.Applied), problem, restored);
            }
        }
        foreach (var e in set) _log.Info($"preferences: {e.Name} set to {PreferenceSetting.Format(e.Kind, e.Applied)} for {operation} (was {PreferenceSetting.Format(e.Kind, e.Original)})");
        return new Scope(this, operation, entries);
    }

    sealed class Scope : IDisposable
    {
        readonly ScopedPreferences _owner;
        readonly string _operation;
        IReadOnlyList<ScopedPreferenceEntry>? _entries;

        public Scope(ScopedPreferences owner, string operation, IReadOnlyList<ScopedPreferenceEntry> entries)
        {
            _owner = owner;
            _operation = operation;
            _entries = entries;
        }

        public void Dispose()
        {
            var entries = _entries;
            _entries = null;
            if (entries == null) return;
            if (_owner.Restore(entries, $"{_operation} finished")) _owner.Delete();
            else _owner._log.Error("preferences: some scoped preferences could not be restored; the journal is kept for the next load");
        }
    }

    bool Restore(IEnumerable<ScopedPreferenceEntry> entries, string why)
    {
        bool ok = true;
        foreach (var e in entries.Where(e => !Same(e.Kind, e.Original, e.Applied)))
        {
            try
            {
                double now = _store.Get(e.Kind, e.Id);
                if (!Same(e.Kind, now, e.Applied))
                {
                    _log.Info($"preferences: {e.Name} is {PreferenceSetting.Format(e.Kind, now)}, not the value set; left as it is ({why})");
                    continue;
                }
                _store.Set(e.Kind, e.Id, e.Original);
                // Read back: SOLIDWORKS can ignore a set without saying so (3.E live run: swSTLQuality = 3).
                double after = _store.Get(e.Kind, e.Id);
                if (!Same(e.Kind, after, e.Original))
                {
                    _log.Error($"preferences: {e.Name} could not be put back to {PreferenceSetting.Format(e.Kind, e.Original)}: SOLIDWORKS kept {PreferenceSetting.Format(e.Kind, after)} ({why}); the journal is kept");
                    ok = false;
                    continue;
                }
                _log.Info($"preferences: {e.Name} restored to {PreferenceSetting.Format(e.Kind, e.Original)} ({why})");
            }
            catch (Exception ex)
            {
                _log.Error($"preferences: restoring {e.Name} failed ({why})", ex);
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>Toggles and integers are equal or not; doubles within a part in a billion (SOLIDWORKS stores them as it likes).</summary>
    internal static bool Same(PreferenceKind kind, double a, double b) =>
        kind == PreferenceKind.Double ? Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))) : Math.Round(a) == Math.Round(b);

    static SwAgentException Failed(string operation, PreferenceSetting s, string problem, bool restored = true) =>
        new SwAgentException(ErrorCode.ApiError,
            $"{operation} needs the SOLIDWORKS preference {s.Name} at {PreferenceSetting.Format(s.Kind, s.Value)}, but {problem}; " +
            (restored
                ? "every preference it had set was put back and nothing was done"
                : "the preferences it had set could not all be put back (the add-in log names them; the journal is kept and they are tried again before the next export), and nothing was exported"),
            new JsonObject { ["preference"] = s.Name, ["value"] = PreferenceSetting.Format(s.Kind, s.Value) });

    void Write(ScopedPreferenceJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        string temp = _journalPath + ".tmp";
        File.WriteAllText(temp, JsonCodec.Serialize(journal));
        if (File.Exists(_journalPath)) File.Replace(temp, _journalPath, null);
        else File.Move(temp, _journalPath);
    }

    void Delete()
    {
        try
        {
            File.Delete(_journalPath);
        }
        catch (Exception e)
        {
            _log.Warn($"preferences: could not delete the scoped journal {_journalPath}: {e.Message}");
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
            _log.Warn($"preferences: could not set the scoped journal aside: {e.Message}");
        }
    }
}
