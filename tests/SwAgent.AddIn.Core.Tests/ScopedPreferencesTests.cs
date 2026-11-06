using System;
using System.Collections.Generic;
using System.IO;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ScopedPreferencesTests
{
    sealed class FakeStore : ITypedPreferenceStore
    {
        public readonly Dictionary<(PreferenceKind, int), double> Values = new Dictionary<(PreferenceKind, int), double>();
        public readonly List<string> Writes = new List<string>();
        public readonly HashSet<int> FailSet = new HashSet<int>();
        public readonly HashSet<int> FailGet = new HashSet<int>();
        public readonly HashSet<int> Ignored = new HashSet<int>();

        public double Get(PreferenceKind kind, int id)
        {
            if (FailGet.Contains(id)) throw new InvalidOperationException("COM said no");
            return Values[(kind, id)];
        }

        public void Set(PreferenceKind kind, int id, double value)
        {
            if (FailSet.Contains(id)) throw new InvalidOperationException("COM said no");
            Writes.Add($"{id}={value}");
            if (!Ignored.Contains(id)) Values[(kind, id)] = value;
        }
    }

    static readonly PreferenceSetting Schema = new PreferenceSetting("swStepAP", PreferenceKind.Integer, 75, 242);
    static readonly PreferenceSetting Binary = new PreferenceSetting("swSTLBinaryFormat", PreferenceKind.Toggle, 69, 1);
    static readonly PreferenceSetting Deviation = new PreferenceSetting("swSTLDeviation", PreferenceKind.Double, 2, 0.0001);

    static (ScopedPreferences Prefs, FakeStore Store, string Journal, MemoryLog Log) Make()
    {
        var store = new FakeStore();
        store.Values[(PreferenceKind.Integer, 75)] = 214;
        store.Values[(PreferenceKind.Toggle, 69)] = 1;
        store.Values[(PreferenceKind.Double, 2)] = 0.0005;
        string journal = Path.Combine(TempDir.Create(), "export-preferences-journal.json");
        var log = new MemoryLog();
        return (new ScopedPreferences(store, journal, log), store, journal, log);
    }

    [Fact]
    public void Apply_journals_first_sets_what_differs_and_dispose_puts_it_back()
    {
        var (prefs, store, journal, _) = Make();
        var scope = prefs.Apply("export.part", new[] { Schema, Binary, Deviation });
        Assert.Equal(new[] { "75=242", "2=0.0001" }, store.Writes);
        Assert.True(File.Exists(journal));
        Assert.Contains("\"original\":214", File.ReadAllText(journal));
        scope.Dispose();
        Assert.Equal(214, store.Values[(PreferenceKind.Integer, 75)]);
        Assert.Equal(0.0005, store.Values[(PreferenceKind.Double, 2)]);
        Assert.False(File.Exists(journal));
        scope.Dispose();
        Assert.Equal(4, store.Writes.Count);
    }

    [Fact]
    public void A_crash_mid_operation_is_undone_at_the_next_load()
    {
        var (prefs, store, journal, log) = Make();
        prefs.Apply("export.part", new[] { Schema });
        // SOLIDWORKS dies here: the scope is never disposed. The next load recovers from the journal.
        var next = new ScopedPreferences(store, journal, log);
        next.Recover();
        Assert.Equal(214, store.Values[(PreferenceKind.Integer, 75)]);
        Assert.False(File.Exists(journal));
        Assert.True(log.Contains("export.part stopped without restoring"));
    }

    [Fact]
    public void A_value_changed_since_it_was_set_is_left_alone()
    {
        var (prefs, store, journal, _) = Make();
        var scope = prefs.Apply("export.part", new[] { Schema });
        store.Values[(PreferenceKind.Integer, 75)] = 203;
        scope.Dispose();
        Assert.Equal(203, store.Values[(PreferenceKind.Integer, 75)]);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void A_preference_SOLIDWORKS_does_not_keep_is_api_error_and_the_rest_is_put_back()
    {
        var (prefs, store, journal, _) = Make();
        store.Ignored.Add(2);
        var e = Assert.Throws<SwAgentException>(() => prefs.Apply("export.part", new[] { Schema, Deviation }));
        Assert.Equal(ErrorCode.ApiError, e.Code);
        Assert.Equal("swSTLDeviation", e.Error.Details!["preference"]);
        Assert.Contains("SOLIDWORKS kept 0.0005", e.Message);
        Assert.Equal(214, store.Values[(PreferenceKind.Integer, 75)]);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void A_set_or_read_that_throws_is_api_error()
    {
        var (prefs, store, _, _) = Make();
        store.FailSet.Add(75);
        Assert.Equal(ErrorCode.ApiError, Assert.Throws<SwAgentException>(() => prefs.Apply("export.part", new[] { Schema })).Code);
        var (other, otherStore, journal, _) = Make();
        otherStore.FailGet.Add(69);
        Assert.Contains("reading it failed", Assert.Throws<SwAgentException>(() => other.Apply("export.part", new[] { Binary })).Message);
        Assert.False(File.Exists(journal));
        Assert.Empty(otherStore.Writes);
    }

    [Fact]
    public void An_earlier_journal_is_recovered_before_a_new_one_is_written()
    {
        var (prefs, store, journal, log) = Make();
        prefs.Apply("export.part", new[] { Schema });
        var next = new ScopedPreferences(store, journal, log);
        using (next.Apply("export.part", new[] { Schema }))
        {
            Assert.Contains("\"original\":214", File.ReadAllText(journal));
        }
        Assert.Equal(214, store.Values[(PreferenceKind.Integer, 75)]);
    }

    [Fact]
    public void An_earlier_journal_that_cannot_be_put_back_blocks_the_next_call()
    {
        var (prefs, store, journal, log) = Make();
        prefs.Apply("export.part", new[] { Schema });
        store.FailSet.Add(75);
        var e = Assert.Throws<SwAgentException>(() => new ScopedPreferences(store, journal, log).Apply("export.part", new[] { Schema }));
        Assert.Equal(ErrorCode.ApiError, e.Code);
        Assert.Contains("not all back yet", e.Message);
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void An_unreadable_journal_is_set_aside()
    {
        var (prefs, store, journal, _) = Make();
        File.WriteAllText(journal, "{not json");
        prefs.Recover();
        Assert.False(File.Exists(journal));
        Assert.True(File.Exists(journal + ".bad"));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Doubles_compare_within_a_part_in_a_billion()
    {
        Assert.True(ScopedPreferences.Same(PreferenceKind.Double, 0.0001, 0.0001 + 1e-15));
        Assert.False(ScopedPreferences.Same(PreferenceKind.Double, 0.0001, 0.0002));
        Assert.True(ScopedPreferences.Same(PreferenceKind.Toggle, 1, 1));
        Assert.Equal("swSTLBinaryFormat = true", Binary.ToString());
    }
}
