using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class SelectionReporterTests
{
    readonly EventHub _hub = new EventHub(NullLog.Instance);
    readonly MemoryLog _log = new MemoryLog();
    readonly List<Action> _posted = new List<Action>();
    bool _running;
    int _reads;

    /// <summary>A reporter whose UI-thread posts run when <see cref="RunPosted"/> is called, as the dispatcher would run them.</summary>
    SelectionReporter Reporter(TimeSpan? quiet = null) =>
        new SelectionReporter(_hub, () => _running, _log, a => { lock (_posted) _posted.Add(a); }, quiet ?? TimeSpan.FromHours(1));

    void RunPosted()
    {
        List<Action> now;
        lock (_posted)
        {
            now = _posted.ToList();
            _posted.Clear();
        }
        foreach (var a in now) a();
    }

    Func<SelectionChangedEvent> Selected(params string[] faces) => () =>
    {
        _reads++;
        return new SelectionChangedEvent
        {
            Title = "Part1",
            Total = faces.Length,
            Items = faces.Select(f => new SelectedItem { Type = "face", Entity = new EntityInfo { Kind = ReferenceKind.Face, Reference = f } }).ToList(),
        };
    };

    IReadOnlyList<EventEnvelope> Raised() => _hub.Since(null).Events.Where(e => e.Kind == "selection.changed").ToList();

    [Fact]
    public void A_burst_of_notifications_reads_the_selection_once_on_the_UI_thread()
    {
        using var reporter = Reporter();
        reporter.Changed(Selected("a"));
        reporter.Changed(Selected("a", "b"));
        reporter.Changed(Selected("a", "b", "c"));
        Assert.Equal(0, _reads);
        reporter.Flush();
        Assert.Equal(0, _reads);
        RunPosted();
        Assert.Equal(1, _reads);
        var raised = Assert.Single(Raised());
        Assert.Equal(3, ((List<object?>)raised.Data!["items"]!).Count);
    }

    [Fact]
    public void The_same_selection_is_not_raised_twice()
    {
        using var reporter = Reporter();
        reporter.Changed(Selected("a"));
        reporter.Flush();
        RunPosted();
        reporter.Changed(Selected("a"));
        reporter.Flush();
        RunPosted();
        reporter.Changed(Selected());
        reporter.Flush();
        RunPosted();
        Assert.Equal(2, Raised().Count);
    }

    [Fact]
    public void Notifications_during_an_operation_are_the_addins_and_dropped()
    {
        using var reporter = Reporter();
        _running = true;
        Assert.True(reporter.Suppressed);
        reporter.Changed(Selected("a"));
        reporter.Flush();
        RunPosted();
        Assert.Empty(Raised());
        Assert.Equal(0, _reads);
    }

    [Fact]
    public void An_operation_standing_in_for_the_user_is_reported()
    {
        using var reporter = Reporter();
        _running = true;
        using (reporter.AllowDuringOperation())
        {
            Assert.False(reporter.Suppressed);
            reporter.Changed(Selected("a"));
        }
        Assert.True(reporter.Suppressed);
        reporter.Flush();
        RunPosted();
        Assert.Single(Raised());
    }

    [Fact]
    public async Task Read_by_itself_once_the_selection_is_still()
    {
        using var reporter = new SelectionReporter(_hub, () => false, _log, a => a(), TimeSpan.FromMilliseconds(50));
        reporter.Changed(Selected("a"));
        for (int i = 0; i < 100 && Raised().Count == 0; i++) await Task.Delay(20);
        Assert.Single(Raised());
    }

    [Fact]
    public void A_read_that_throws_is_logged_and_nothing_escapes()
    {
        using var reporter = Reporter();
        reporter.Changed(() => throw new InvalidOperationException("the document closed"));
        reporter.Flush();
        RunPosted();
        Assert.Empty(Raised());
        Assert.Contains(_log.Lines, l => l.Contains("the document closed"));
    }

    [Fact]
    public void A_post_that_throws_is_logged_and_nothing_escapes()
    {
        using var reporter = new SelectionReporter(_hub, () => false, _log, _ => throw new ObjectDisposedException("dispatcher"), TimeSpan.FromHours(1));
        reporter.Changed(Selected("a"));
        reporter.Flush();
        Assert.Contains(_log.Lines, l => l.Contains("scheduling"));
    }

    [Fact]
    public void Nothing_is_raised_after_dispose()
    {
        var reporter = Reporter();
        reporter.Changed(Selected("a"));
        reporter.Flush();
        reporter.Dispose();
        RunPosted();
        reporter.Changed(Selected("b"));
        reporter.Flush();
        RunPosted();
        Assert.Empty(Raised());
    }
}
