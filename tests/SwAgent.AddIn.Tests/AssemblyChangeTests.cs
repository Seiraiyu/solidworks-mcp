using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

[Operation("test.assembly_change", typeof(TestAssemblyChangeResult))]
public sealed class TestAssemblyChangeRequest
{
    public string? Document { get; set; }

    /// <summary>Where to fail: early (before anything is made), change, check, rebuild (a new rebuild error) or over (pin-1 over defined).</summary>
    public string? FailIn { get; set; }

    /// <summary>When true, the solve moves pin-1 12 mm up.</summary>
    public bool SolveMovesPin { get; set; }

    /// <summary>When true, pin-1 ignores placements from the check on, as a component its mates hold would.</summary>
    public bool PinStuck { get; set; }
}

public sealed class TestAssemblyChangeResult : IAssemblyChangeResult
{
    public AssemblyChange Change { get; set; } = new AssemblyChange();
}

/// <summary>Inserts a bolt, lets the solve move pin-1 if told to, and fails where it is told to.</summary>
sealed class TestAssemblyChangeOperation : AssemblyChangeOperation<TestAssemblyChangeRequest, TestAssemblyChangeResult>
{
    public TestAssemblyChangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(TestAssemblyChangeRequest request) => request.Document;

    protected override TestAssemblyChangeResult Change(ISwDocument doc, ISwAssembly asm, TestAssemblyChangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.FailIn == "early") throw new SwAgentException(ErrorCode.InvalidArgument, "no such bolt");
        var fake = (FakeAssembly)asm;
        if (request.SolveMovesPin) fake.OnSolve = a => a.Named("pin-1").Info.Transform.Origin.Z = 12;
        var bolt = asm.Insert(@"C:\w\bolt.SLDPRT", null, new Transform());
        tx.Made(new CreatedItem { Name = "bolt-1", Kind = ReferenceKind.Component }, () => asm.Delete(new[] { bolt! }));
        asm.Rebuild();
        if (request.SolveMovesPin) fake.OnSolve = null;
        if (request.FailIn == "change") throw new InvalidOperationException("COM said no");
        if (request.FailIn == "rebuild") ((FakeDocument)doc).Problems.Add(new RebuildItem { Feature = "Coincident1", Code = "swMateErrorOverDefined" });
        if (request.FailIn == "over") fake.Named("pin-1").Info.Constraint = Constraint.OverDefined;
        return new TestAssemblyChangeResult();
    }

    protected override void Check(ISwDocument doc, ISwAssembly asm, TestAssemblyChangeRequest request, TestAssemblyChangeResult result)
    {
        if (request.PinStuck) ((FakeAssembly)asm).PlacementIgnored = true;
        if (request.FailIn == "check") throw new SwAgentException(ErrorCode.VerificationFailed, "the bolt is 3 mm off");
    }
}

public class AssemblyChangeTests : DocumentTestBase
{
    readonly FakeDocument _doc;
    readonly FakeAssembly _asm;

    public AssemblyChangeTests()
    {
        _doc = Sw.Track(FakeDocument.NewAssembly("stand.SLDASM", PathOf("stand.SLDASM")));
        _asm = _doc.Asm!;
        _asm.AddComponent("base-1", PathOf("base.SLDPRT"), fix: true);
        _asm.AddComponent("pin-1", PathOf("pin.SLDPRT"));
        _asm.MateNames.Add("Coincident1");
    }

    TestAssemblyChangeResult Change(TestAssemblyChangeRequest request) => Run(new TestAssemblyChangeOperation(Services), request);

    [Fact]
    public void A_change_reports_what_it_made_what_moved_and_the_assembly_before_and_after()
    {
        var result = Change(new TestAssemblyChangeRequest { SolveMovesPin = true });
        var change = result.Change;
        Assert.Equal("bolt-1", change.Created.Single().Name);
        Assert.Equal(2, change.ComponentsBefore);
        Assert.Equal(3, change.ComponentsAfter);
        Assert.Equal(1, change.MatesBefore);
        Assert.Equal(1, change.MatesAfter);
        var moved = change.Moved.Single();
        Assert.Equal("pin-1", moved.Name);
        Assert.Equal(0, moved.Before.Z);
        Assert.Equal(12, moved.After.Z);
        Assert.False(moved.Rotated);
        Assert.Equal(new[] { "pin-1", "bolt-1" }, change.Constraints.Select(c => c.Name));
        Assert.All(change.Constraints, c => Assert.Equal(Constraint.UnderDefined, c.Constraint));
        Assert.Equal("stand.SLDASM", change.Document.Title);
    }

    [Theory]
    [InlineData("change", ErrorCode.OperationFailed)]
    [InlineData("check", ErrorCode.VerificationFailed)]
    [InlineData("rebuild", ErrorCode.VerificationFailed)]
    [InlineData("over", ErrorCode.VerificationFailed)]
    public void A_failure_deletes_what_was_made_and_puts_moved_components_back(string where, ErrorCode code)
    {
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest { FailIn = where, SolveMovesPin = true }));
        Assert.Equal(code, e.Code);
        Assert.Equal(new object?[] { "bolt-1" }, (List<object?>)e.Error.Details!["rolled_back"]!);
        Assert.Equal(new object?[] { "pin-1" }, (List<object?>)e.Error.Details["positions_restored"]!);
        Assert.Contains("components moved back: pin-1", e.Message);
        Assert.Equal(new[] { "base-1", "pin-1" }, _asm.Items.Select(i => i.Info.Name));
        Assert.Equal(0, _asm.Named("pin-1").Info.Transform.Origin.Z);
    }

    [Fact]
    public void A_newly_over_defined_component_is_named()
    {
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest { FailIn = "over" }));
        Assert.Equal(new object?[] { "pin-1" }, (List<object?>)e.Error.Details!["over_defined"]!);
    }

    [Fact]
    public void A_failure_that_moved_nothing_says_nothing_about_positions()
    {
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest { FailIn = "check" }));
        Assert.False(e.Error.Details!.ContainsKey("positions_restored"));
    }

    [Fact]
    public void A_failure_before_anything_was_made_is_passed_on_as_it_is()
    {
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest { FailIn = "early" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("no such bolt", e.Message);
    }

    [Fact]
    public void A_component_that_will_not_go_back_is_rollback_failed_and_named()
    {
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest { FailIn = "check", SolveMovesPin = true, PinStuck = true }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Equal("verification_failed", e.Error.Details["cause"]);
        Assert.Contains((List<object?>)e.Error.Details["remaining"]!, r => ((string)r!).StartsWith("pin-1: it did not go back"));
    }

    [Fact]
    public void The_users_in_context_edit_is_busy_and_nothing_is_touched()
    {
        _asm.EditingTitle = "pin.SLDPRT";
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest()));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Equal("pin.SLDPRT", e.Error.Details["component"]);
        Assert.Empty(_asm.Calls);
    }

    [Fact]
    public void An_open_command_is_busy_as_for_parts()
    {
        Sw.OpenCommandTitle = "Mate";
        Assert.Equal(ErrorCode.Busy, Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest())).Code);
        Assert.Empty(_asm.Calls);
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        var e = Assert.Throws<SwAgentException>(() => Change(new TestAssemblyChangeRequest { Document = "plate.SLDPRT" }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }
}
