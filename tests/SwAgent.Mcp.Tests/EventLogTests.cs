using System.Linq;
using SwAgent.Client;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Mcp.Tests;

public class EventLogTests
{
    static EventEnvelope E(long seq, string kind = "document.saved") =>
        new EventEnvelope { Seq = seq, Kind = kind, Time = "2026-11-05T12:00:00." + seq.ToString("D7") + "Z", Data = new JsonObject() };

    [Fact]
    public void Events_are_numbered_by_the_server_and_read_after_a_cursor()
    {
        var log = new EventLog();
        log.Add(7, E(1));
        log.Add(7, E(2));
        log.Add(7, E(3));
        var all = log.Read(null, null, null);
        Assert.Equal(new long[] { 1, 2, 3 }, all.Events.Select(e => e.Seq).ToArray());
        Assert.False(all.Missed);
        Assert.Equal(3L, all.LastSeq);
        Assert.Equal(7, all.ProcessId);
        var later = log.Read(2, null, null);
        Assert.Equal(new long[] { 3 }, later.Events.Select(e => e.Seq).ToArray());
        Assert.Empty(log.Read(3, null, null).Events);
        Assert.Equal(3L, log.Read(3, null, null).LastSeq);
    }

    [Fact]
    public void A_skip_in_the_addins_sequence_reads_as_missed()
    {
        var log = new EventLog();
        log.Add(7, E(1));
        log.Add(7, E(4));
        Assert.True(log.Read(null, null, null).Missed);
        Assert.False(log.Read(2, null, null).Missed); // the gap is before event 2 (add-in seq 4), already read past
    }

    [Fact]
    public void A_replayed_event_is_kept_once()
    {
        var log = new EventLog();
        log.Add(7, E(1));
        log.Add(7, E(2));
        log.Add(7, E(2));
        Assert.Equal(2, log.Read(null, null, null).Events.Count);
    }

    [Fact]
    public void An_addin_loaded_again_counts_from_1_and_the_cursor_still_works()
    {
        var log = new EventLog();
        log.Add(7, E(1));
        log.Add(7, E(2));
        log.Add(7, E(1, "session.closing"));
        var after = log.Read(2, null, null);
        Assert.Equal(new long[] { 3 }, after.Events.Select(e => e.Seq).ToArray());
        Assert.False(after.Missed);
    }

    [Fact]
    public void Another_process_replaces_the_events_and_an_old_cursor_reads_missed()
    {
        var log = new EventLog();
        log.Add(7, E(1));
        log.Add(7, E(2));
        log.Add(9, E(1));
        var all = log.Read(null, null, null);
        Assert.Equal(new long[] { 3 }, all.Events.Select(e => e.Seq).ToArray());
        Assert.True(all.Missed);
        Assert.Equal(9, all.ProcessId);
        Assert.False(log.Read(2, null, null).Missed);
    }

    [Fact]
    public void Kinds_filter_and_limit_pages_with_more()
    {
        var log = new EventLog();
        for (int i = 1; i <= 5; i++) log.Add(7, E(i, i % 2 == 0 ? "dialog.opened" : "document.saved"));
        var page = log.Read(null, new[] { "document.saved" }, 2);
        Assert.Equal(new long[] { 1, 3 }, page.Events.Select(e => e.Seq).ToArray());
        Assert.True(page.More);
        Assert.Equal(3L, page.LastSeq);
        var rest = log.Read(page.LastSeq, new[] { "document.saved" }, 2);
        Assert.Equal(new long[] { 5 }, rest.Events.Select(e => e.Seq).ToArray());
        Assert.False(rest.More);
    }

    [Fact]
    public void Past_capacity_the_oldest_are_dropped_and_reading_from_before_them_is_missed()
    {
        var log = new EventLog();
        for (int i = 1; i <= EventLog.Capacity + 5; i++) log.Add(7, E(i));
        var all = log.Read(null, null, EventLog.Capacity);
        Assert.Equal(6L, all.Events[0].Seq);
        Assert.True(all.Missed);
        Assert.False(log.Read(5, null, null).Missed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Limit_is_bounded(int limit)
    {
        Assert.Equal(ErrorCode.InvalidArgument, Assert.Throws<SwAgentException>(() => new EventLog().Read(null, null, limit)).Code);
    }
}
