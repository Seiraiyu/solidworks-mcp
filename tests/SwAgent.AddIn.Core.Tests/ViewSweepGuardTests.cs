using System;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ViewSweepGuardTests
{
    string? _running;
    int _clears;
    bool _clearCloses = true;

    T Read<T>(Func<T> read) => ViewSweepGuard.Read(() => _running, () =>
    {
        _clears++;
        if (_clearCloses) _running = null;
    }, "Drawing View8", "reading the faces the view shows of pin-1", read);

    [Fact]
    public void A_read_that_leaves_nothing_open_answers_and_clears_nothing()
    {
        Assert.Equal(3, Read(() => 3));
        Assert.Equal(0, _clears);
    }

    [Fact]
    public void A_read_that_leaves_a_command_open_is_cleared_and_refused()
    {
        // SP4 (5.D live run, round 4): a per-component GetVisibleEntities2 left a view's PropertyManager open.
        var e = Assert.Throws<SwAgentException>(() => Read(() => { _running = "Drawing View8"; return 3; }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("Drawing View8", e.Error.Details!["view"]);
        Assert.Equal("Drawing View8", e.Error.Details["command"]);
        Assert.Equal(true, e.Error.Details["cleared"]);
        Assert.Contains("reading the faces the view shows of pin-1 in Drawing View8", e.Message);
        Assert.Contains("it was cleared", e.Message);
        Assert.Equal(1, _clears);
        Assert.Null(_running);
    }

    [Fact]
    public void A_command_clearing_does_not_close_is_named_as_still_open()
    {
        _clearCloses = false;
        var e = Assert.Throws<SwAgentException>(() => Read(() => { _running = "Drawing View8"; return 3; }));
        Assert.Equal(false, e.Error.Details!["cleared"]);
        Assert.Contains("'Drawing View8' is still open: close it in SOLIDWORKS", e.Message);
        Assert.Equal("Drawing View8", _running);
    }

    [Fact]
    public void A_command_open_before_the_read_is_the_users_and_is_left_alone()
    {
        _running = "Extrude";
        Assert.Equal(3, Read(() => 3));
        Assert.Equal(0, _clears);
        Assert.Equal("Extrude", _running);
    }

    [Fact]
    public void A_read_that_fails_and_leaves_a_command_open_is_cleared_and_refused_for_that()
    {
        var e = Assert.Throws<SwAgentException>(() => Read<int>(() => { _running = "Drawing View8"; throw new InvalidOperationException("COM"); }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(1, _clears);
        Assert.Null(_running);
    }

    [Fact]
    public void A_read_that_fails_leaving_nothing_open_fails_as_it_did()
    {
        Assert.Throws<InvalidOperationException>(() => Read<int>(() => throw new InvalidOperationException("COM")));
        Assert.Equal(0, _clears);
    }
}
