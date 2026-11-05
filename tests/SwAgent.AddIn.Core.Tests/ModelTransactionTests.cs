using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class ModelTransactionTests
{
    readonly List<string> _undone = new List<string>();

    ModelTransaction ThreeSteps(Action? failing = null)
    {
        var tx = new ModelTransaction();
        tx.Made(new CreatedItem { Name = "Sketch1", Kind = ReferenceKind.Sketch }, () => _undone.Add("Sketch1"));
        tx.Edited(new ChangedItem { Name = "D1@Sketch1", Property = "value", Before = "40 mm", After = "50 mm" }, failing ?? (() => _undone.Add("D1@Sketch1")));
        tx.Made(new CreatedItem { Name = "Sketch2", Kind = ReferenceKind.Sketch }, () => _undone.Add("Sketch2"));
        return tx;
    }

    [Fact]
    public void Records_what_was_made_and_edited_in_order()
    {
        var tx = ThreeSteps();
        Assert.Equal(new[] { "Sketch1", "Sketch2" }, tx.Created.Select(c => c.Name));
        Assert.Equal("D1@Sketch1", tx.Changed.Single().Name);
        Assert.Empty(_undone);
    }

    [Fact]
    public void Rolls_back_newest_first_and_forgets_what_it_undid()
    {
        var tx = ThreeSteps();
        var outcome = tx.Rollback();
        Assert.Equal(new[] { "Sketch2", "D1@Sketch1", "Sketch1" }, _undone);
        Assert.Equal(new[] { "Sketch2", "D1@Sketch1", "Sketch1" }, outcome.Undone);
        Assert.True(outcome.Clean);
        Assert.Empty(tx.Created);
        Assert.Empty(tx.Changed);
        Assert.Empty(tx.Rollback().Undone);
    }

    [Fact]
    public void A_step_that_cannot_be_undone_does_not_stop_the_others()
    {
        var tx = ThreeSteps(() => throw new InvalidOperationException("the dimension is locked"));
        var outcome = tx.Rollback();
        Assert.Equal(new[] { "Sketch2", "Sketch1" }, _undone);
        Assert.False(outcome.Clean);
        Assert.Equal("D1@Sketch1: the dimension is locked", outcome.Failed.Single());
    }

    [Fact]
    public void A_failure_in_the_change_is_rolled_back_and_keeps_its_code_and_details()
    {
        var tx = ThreeSteps();
        var cause = new SwAgentException(ErrorCode.InvalidArgument, "lines/1/end is the same point as its start",
            new SwAgent.Contracts.Serialization.JsonObject { ["field"] = "/lines/1/end" });
        var e = tx.Abort(cause, verifying: false);
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/lines/1/end", e.Error.Details!["field"]);
        Assert.Equal(new List<object?> { "Sketch2", "D1@Sketch1", "Sketch1" }, e.Error.Details["rolled_back"]);
        Assert.Contains("rolled back", e.Message);
    }

    [Fact]
    public void An_unexpected_exception_is_operation_failed_while_changing_and_verification_failed_while_checking()
    {
        Assert.Equal(ErrorCode.OperationFailed, ThreeSteps().Abort(new InvalidOperationException("COM said no"), verifying: false).Code);
        var checking = ThreeSteps().Abort(new InvalidOperationException("the sketch has 3 lines, not 4"), verifying: true);
        Assert.Equal(ErrorCode.VerificationFailed, checking.Code);
        Assert.Contains("the sketch has 3 lines, not 4", checking.Message);
    }

    [Fact]
    public void A_rollback_that_fails_is_operation_failed_with_rollback_failed_and_what_remains()
    {
        var tx = ThreeSteps(() => throw new InvalidOperationException("the dimension is locked"));
        var e = tx.Abort(new SwAgentException(ErrorCode.VerificationFailed, "D1 reads 49.9 mm, not 50 mm"), verifying: true);
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Equal(new List<object?> { "D1@Sketch1: the dimension is locked" }, e.Error.Details["remaining"]);
        Assert.Equal("verification_failed", e.Error.Details["cause"]);
        Assert.Contains("D1 reads 49.9 mm, not 50 mm", e.Message);
        Assert.Contains("left with unsaved changes", e.Message);
    }

    [Fact]
    public void A_failure_before_anything_was_made_is_passed_on_unchanged()
    {
        var cause = new SwAgentException(ErrorCode.Busy, "the user is editing Sketch1");
        var e = new ModelTransaction().Abort(cause, verifying: false);
        Assert.Same(cause, e);
    }
}
