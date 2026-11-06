using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

[Operation("test.change", typeof(TestChangeResult))]
public sealed class TestChangeRequest
{
    public string? Document { get; set; }

    /// <summary>Where to fail: change, check, or rebuild (a new rebuild error, caught by the common verification).</summary>
    public string? FailIn { get; set; }

    public bool RollbackFails { get; set; }
}

public sealed class TestChangeResult : IModelChangeResult
{
    public ModelChange Change { get; set; } = new ModelChange();
}

/// <summary>Makes Sketch9 and grows the part by 100 mm³, failing where it is told to.</summary>
sealed class TestChangeOperation : ModelChangeOperation<TestChangeRequest, TestChangeResult>
{
    public TestChangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(TestChangeRequest request) => request.Document;

    protected override TestChangeResult Change(ISwDocument document, TestChangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        var doc = (FakeDocument)document;
        var sketch = doc.AddFeature("Sketch9", "ProfileFeature", ReferenceKind.Sketch);
        doc.MassProperties!.VolumeMm3 += 100;
        tx.Made(new CreatedItem { Name = "Sketch9", Kind = ReferenceKind.Sketch }, () =>
        {
            if (request.RollbackFails) throw new InvalidOperationException("Sketch9 is locked");
            doc.RemoveFeature(sketch);
            doc.MassProperties!.VolumeMm3 -= 100;
        });
        if (request.FailIn == "change") throw new InvalidOperationException("COM said no");
        if (request.FailIn == "rebuild") doc.Problems.Add(new RebuildItem { Feature = "Sketch9", Code = "swSketchErrorInvalid", Warning = false });
        return new TestChangeResult();
    }

    protected override void Check(ISwDocument doc, TestChangeRequest request, TestChangeResult result)
    {
        if (request.FailIn == "check") throw new SwAgentException(ErrorCode.VerificationFailed, "the sketch has 3 lines, not 4");
    }
}

public class ModelChangeTests : DocumentTestBase
{
    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.Bodies = 1;
        doc.MassProperties = new ModelMassResult { VolumeMm3 = 8000 };
        doc.QuickBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(40, 20, 10) };
        return doc;
    }

    TestChangeResult Change(TestChangeRequest request) => Run(new TestChangeOperation(Services), request);

    [Fact]
    public void A_change_returns_what_it_made_and_the_part_before_and_after()
    {
        var doc = Part();
        var result = Change(new TestChangeRequest());
        Assert.Equal("Sketch9", result.Change.Created.Single().Name);
        Assert.Equal(1, result.Change.BodiesBefore);
        Assert.Equal(1, result.Change.BodiesAfter);
        Assert.Equal(8000, result.Change.VolumeBeforeMm3);
        Assert.Equal(8100, result.Change.VolumeAfterMm3);
        Assert.Equal(40, result.Change.Box!.Max.X);
        Assert.Equal(0, result.Change.Rebuild.Errors);
        Assert.Equal(doc.Document.Path, result.Change.Document.Path);
        Assert.Contains(doc.Tree, f => f.Name == "Sketch9");
    }

    [Fact]
    public void A_sketch_the_user_is_editing_makes_it_busy_and_nothing_is_changed()
    {
        var doc = Part();
        doc.EditingSketchName = "Sketch1";
        var e = Fails(() => Change(new TestChangeRequest()));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        Assert.Equal("before", e.Error.Details["detected"]);
        Assert.Equal("Sketch1", e.Error.Details["sketch"]);
        Assert.Equal(false, e.Error.Details["started"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sketch9");
    }

    [Fact]
    public void A_command_the_user_has_open_makes_it_busy()
    {
        Part();
        Sw.OpenCommandTitle = "Fillet";
        var e = Fails(() => Change(new TestChangeRequest()));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("Fillet", e.Error.Details!["command"]);
        Assert.Contains("Fillet", e.Message);
    }

    [Theory]
    [InlineData("change", ErrorCode.OperationFailed)]
    [InlineData("check", ErrorCode.VerificationFailed)]
    [InlineData("rebuild", ErrorCode.VerificationFailed)]
    public void A_failure_anywhere_leaves_the_part_as_it_was(string where, ErrorCode code)
    {
        var doc = Part();
        var e = Fails(() => Change(new TestChangeRequest { FailIn = where }));
        Assert.Equal(code, e.Code);
        Assert.Equal(new List<object?> { "Sketch9" }, e.Error.Details!["rolled_back"]);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sketch9");
        Assert.Equal(8000, doc.MassProperties!.VolumeMm3);
    }

    [Fact]
    public void A_rebuild_error_the_part_already_had_is_not_blamed_on_the_change()
    {
        var doc = Part();
        doc.Problems.Add(new RebuildItem { Feature = "Fillet1", Code = "swFeatureErrorFilletNoEdge", Warning = false });
        var result = Change(new TestChangeRequest());
        Assert.Equal(1, result.Change.Rebuild.Errors);
        Assert.Contains(doc.Tree, f => f.Name == "Sketch9");
    }

    [Fact]
    public void A_rollback_that_fails_says_so_and_leaves_the_part_open()
    {
        var doc = Part();
        var e = Fails(() => Change(new TestChangeRequest { FailIn = "check", RollbackFails = true }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Contains(doc.Tree, f => f.Name == "Sketch9");
        Assert.Contains(doc, Sw.Docs);
    }

    [Fact]
    public void Only_parts_are_changed()
    {
        Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Change(new TestChangeRequest())).Code);
    }
}

public class DiagEditingTests : DocumentTestBase
{
    [Fact]
    public void Reports_what_the_precondition_reads_without_changing_anything()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        var services = new AddInServices(Sw, "0.1.0-test", new SwAgent.AddIn.Core.AgentRuntime("0.1.0-test", 1, SwAgent.AddIn.Core.NullLog.Instance, diagnosticsEnabled: true));
        var op = new SwAgent.AddIn.Operations.DiagEditingOperation(services);
        var idle = Run(op, new DiagEditingRequest());
        Assert.Null(idle.Sketch);
        Assert.Null(idle.Command);
        doc.EditingSketchName = "Sketch2";
        Sw.OpenCommandTitle = "Boss-Extrude";
        var busy = Run(op, new DiagEditingRequest());
        Assert.Equal("Sketch2", busy.Sketch);
        Assert.Equal("Boss-Extrude", busy.Command);
    }

    [Fact]
    public void Is_refused_unless_diagnostics_are_on()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Run(new SwAgent.AddIn.Operations.DiagEditingOperation(Services), new DiagEditingRequest())).Code);
    }
}

/// <summary>diag.arm_dialog's message box, shown mid-change by every model change (design 371: the dialog-during-feature fault case).</summary>
public class ArmedDialogTests : DocumentTestBase
{
    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.Bodies = 1;
        doc.MassProperties = new ModelMassResult { VolumeMm3 = 8000 };
        return doc;
    }

    TestChangeResult Change(TestChangeRequest request) => Run(new TestChangeOperation(Services), request);

    [Fact]
    public void An_armed_dialog_answered_ok_lets_the_change_go_on_and_is_shown_once()
    {
        var doc = Part();
        var shown = new List<string>();
        Services.ShowBox = box =>
        {
            shown.Add(box.Title);
            return "ok";
        };
        Services.Runtime.ArmDialog(new DiagDialogRequest { Title = "mid-change", Text = "x", Buttons = DiagDialogButtons.OkCancel });
        Change(new TestChangeRequest());
        Assert.Equal(new[] { "mid-change" }, shown.ToArray());
        Assert.Contains(doc.Tree, f => f.Name == "Sketch9");
        doc.RemoveFeature((FakeEntity)doc.FeatureByName("Sketch9")!);
        Change(new TestChangeRequest());
        Assert.Single(shown);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("no")]
    public void An_armed_dialog_answered_cancel_or_no_rolls_the_change_back(string pressed)
    {
        var doc = Part();
        Services.ShowBox = _ => pressed;
        Services.Runtime.ArmDialog(new DiagDialogRequest { Title = "mid-change", Text = "x", Buttons = DiagDialogButtons.YesNo });
        var e = Fails(() => Change(new TestChangeRequest()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(pressed, e.Error.Details!["pressed"]);
        Assert.Contains("taken back", e.Message);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sketch9");
        Assert.Equal(8000, doc.MassProperties!.VolumeMm3);
    }
}
