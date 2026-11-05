using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class PreferenceGuardTests
{
    sealed class FakeStore : IPreferenceStore
    {
        public readonly Dictionary<int, bool> Values = new Dictionary<int, bool>();
        public readonly List<string> Writes = new List<string>();
        public readonly HashSet<int> FailSet = new HashSet<int>();
        public readonly HashSet<int> Ignored = new HashSet<int>();

        public bool Get(int id) => Values[id];

        public void Set(int id, bool value)
        {
            if (FailSet.Contains(id)) throw new InvalidOperationException("COM said no");
            Writes.Add($"{id}={value}");
            if (!Ignored.Contains(id)) Values[id] = value;
        }
    }

    static readonly AutomationPreference[] Prefs =
    {
        new AutomationPreference("input_dim", 10, false),
        new AutomationPreference("save_reminder", 20, false),
    };

    static (PreferenceGuard Guard, FakeStore Store, string Journal) Make(bool dim = true, bool reminder = false)
    {
        var store = new FakeStore();
        store.Values[10] = dim;
        store.Values[20] = reminder;
        string journal = Path.Combine(TempDir.Create(), "preferences-journal.json");
        return (new PreferenceGuard(store, Prefs, journal, new MemoryLog()), store, journal);
    }

    [Fact]
    public void Apply_writes_the_journal_first_then_changes_only_what_differs()
    {
        var (guard, store, journal) = Make(dim: true, reminder: false);
        guard.Apply();
        Assert.True(guard.Applied);
        Assert.Equal(new[] { "10=False" }, store.Writes);
        Assert.Contains("\"original\":true", File.ReadAllText(journal));
        Assert.Empty(guard.NotTaken);
    }

    [Fact]
    public void Apply_twice_is_once()
    {
        var (guard, store, _) = Make();
        guard.Apply();
        guard.Apply();
        Assert.Single(store.Writes);
    }

    [Fact]
    public void A_set_that_throws_leaves_the_guard_applied_the_journal_intact_and_is_reported()
    {
        var (guard, store, journal) = Make(dim: true, reminder: true);
        store.FailSet.Add(10);
        guard.Apply();
        Assert.True(guard.Applied);
        Assert.Equal(new[] { "input_dim" }, guard.NotTaken);
        string before = File.ReadAllText(journal);
        guard.Apply(); // a retried hello must not re-read automation values as originals
        Assert.Equal(before, File.ReadAllText(journal));
        Assert.Equal(new[] { "20=False" }, store.Writes);
    }

    [Fact]
    public void A_preference_that_does_not_stick_is_reported()
    {
        var (guard, store, _) = Make(dim: true);
        store.Ignored.Add(10);
        guard.Apply();
        Assert.Equal(new[] { "input_dim" }, guard.NotTaken);
    }

    [Fact]
    public void Apply_keeps_the_originals_of_a_journal_already_on_disk()
    {
        var (first, store, journal) = Make(dim: true);
        first.Apply(); // journal: input_dim original true; the store now holds false
        var second = new PreferenceGuard(store, Prefs, journal, new MemoryLog()); // e.g. recovery could not run
        second.Apply();
        Assert.Contains("\"original\":true", File.ReadAllText(journal));
        second.Restore("client disconnected");
        Assert.True(store.Values[10]);
    }

    [Fact]
    public void A_journal_that_cannot_be_written_applies_nothing()
    {
        var store = new FakeStore();
        store.Values[10] = true;
        store.Values[20] = true;
        string notADirectory = Path.Combine(TempDir.Create(), "file");
        File.WriteAllText(notADirectory, "x");
        var guard = new PreferenceGuard(store, Prefs, Path.Combine(notADirectory, "preferences-journal.json"), new MemoryLog());
        guard.Apply();
        Assert.False(guard.Applied);
        Assert.Empty(store.Writes);
        Assert.Equal(new[] { "input_dim", "save_reminder" }, guard.NotTaken);
    }

    [Fact]
    public void Restore_puts_back_the_users_value_and_deletes_the_journal()
    {
        var (guard, store, journal) = Make(dim: true);
        guard.Apply();
        guard.Restore("client disconnected");
        Assert.False(guard.Applied);
        Assert.True(store.Values[10]);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void A_restore_that_fails_keeps_the_journal_for_the_next_load()
    {
        var (guard, store, journal) = Make(dim: true);
        guard.Apply();
        store.FailSet.Add(10);
        guard.Restore("client disconnected");
        Assert.True(File.Exists(journal));
        store.FailSet.Clear();
        var next = new PreferenceGuard(store, Prefs, journal, new MemoryLog());
        next.Recover();
        Assert.True(store.Values[10]);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void Restore_leaves_a_preference_the_user_changed_meanwhile()
    {
        var (guard, store, _) = Make(dim: true);
        guard.Apply();
        store.Values[10] = true; // the user turned it back on by hand
        store.Writes.Clear();
        guard.Restore("client disconnected");
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Recover_replays_a_journal_a_crash_left_behind()
    {
        var (first, store, journal) = Make(dim: true);
        first.Apply(); // then SOLIDWORKS "crashes": no Restore
        var second = new PreferenceGuard(store, Prefs, journal, new MemoryLog());
        second.Recover();
        Assert.True(second.RecoveredAtLoad);
        Assert.True(store.Values[10]);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void Recover_without_a_journal_does_nothing()
    {
        var (guard, store, _) = Make();
        guard.Recover();
        Assert.False(guard.RecoveredAtLoad);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void An_unreadable_journal_is_set_aside_not_trusted()
    {
        var (guard, store, journal) = Make();
        File.WriteAllText(journal, "{ not json");
        guard.Recover();
        Assert.False(File.Exists(journal));
        Assert.True(File.Exists(journal + ".bad"));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Recovery_with_the_users_value_already_back_says_so_and_restores_nothing()
    {
        var (first, store, journal) = Make(dim: true);
        first.Apply();                   // journal: input_dim original true, applied false
        store.Values[10] = true;         // SOLIDWORKS did not keep the applied value across the kill
        store.Writes.Clear();
        var log = new MemoryLog();
        var second = new PreferenceGuard(store, Prefs, journal, log);
        second.Recover();
        Assert.Empty(store.Writes);
        Assert.True(log.Contains("input_dim already at the user's value True; nothing to restore (recovered at load)"));
        Assert.False(log.Contains("the user changed it"));
        Assert.False(File.Exists(journal));
    }
}
