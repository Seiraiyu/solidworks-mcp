using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

[Operation("test.drawing_change", typeof(TestDrawingChangeResult))]
public sealed class TestDrawingChangeRequest
{
    public string? Document { get; set; }

    /// <summary>Where to fail: early (before anything is made), change, check, dirty (the block marked changed) or errors (a new rebuild error in the block).</summary>
    public string? FailIn { get; set; }

    /// <summary>When true, the sheet's scale is edited too (recorded to put back).</summary>
    public bool Rescale { get; set; }
}

public sealed class TestDrawingChangeResult : IDrawingChangeResult
{
    public DrawingChange Change { get; set; } = new DrawingChange();
}

/// <summary>Adds Sheet2 and a front view of the block on it, edits Sheet1's scale if told to, and fails where it is told to.</summary>
sealed class TestDrawingChangeOperation : DrawingChangeOperation<TestDrawingChangeRequest, TestDrawingChangeResult>
{
    public TestDrawingChangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(TestDrawingChangeRequest request) => request.Document;

    protected override TestDrawingChangeResult Change(ISwDocument doc, ISwDrawing drawing, TestDrawingChangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        if (request.FailIn == "early") throw new SwAgentException(ErrorCode.InvalidArgument, "no such sheet");
        drawing.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        MadeSheet(doc, drawing, tx, "Sheet2");
        string view = drawing.CreateModelView(((FakeDrawing)drawing).Models.Keys.Single(), "front", new SheetPoint(100, 100))!;
        MadeView(doc, drawing, tx, view);
        if (request.Rescale)
        {
            var was = drawing.ReadSheet("Sheet1")!;
            var now = was.Copy();
            now.Scale = new ScaleRatio(1, 2);
            drawing.SetSheet("Sheet1", now);
            tx.Edited(new ChangedItem { Name = "Sheet1", Property = "scale", Before = "1:1", After = "1:2" }, () => drawing.SetSheet("Sheet1", was));
        }
        if (request.FailIn == "change") throw new InvalidOperationException("COM said no");
        var block = (FakeDocument)drawing.ModelOf(view)!;
        if (request.FailIn == "dirty") block.Document.Dirty = true;
        if (request.FailIn == "errors") block.Problems.Add(new RebuildItem { Feature = "Boss-Extrude1", Code = "swFeatureErrorFilletNoEdge" });
        return new TestDrawingChangeResult();
    }

    protected override void Check(ISwDocument doc, ISwDrawing drawing, TestDrawingChangeRequest request, TestDrawingChangeResult result)
    {
        if (request.FailIn == "check") throw new SwAgentException(ErrorCode.VerificationFailed, "the view is 3 mm off");
    }
}

/// <summary>An agent's drawing, block.SLDDRW, and the block it can show, saved; the drawing active.</summary>
public abstract class DrawingTestBase : DocumentTestBase
{
    internal readonly FakeDocument Doc;
    internal readonly FakeDrawing Drw;
    internal readonly FakeDocument Block;

    protected DrawingTestBase()
    {
        Doc = Sw.Track(FakeDocument.NewDrawing("block.SLDDRW", PathOf("block.SLDDRW")));
        Drw = Doc.Drw!;
        Drw.Sw = Sw;
        Block = FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT"));
        System.IO.File.WriteAllText(Block.Document.Path!, "part");
        Block.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
        Drw.Models[Block.Document.Path!] = Block;
        Sw.CurrentDoc = Doc;
        Services.Runtime.DocumentOpened(Doc.Document.Path!, owned: true);
    }

    /// <summary>
    /// Phase 6's tab, saved at tab.SLDPRT: 100 mm wide with a 25 mm flange, one 90° bend at R 3, 2 mm, K 0.5, so flat
    /// 100 × 83.2832. Open unless told otherwise, when SOLIDWORKS opens it on request.
    /// </summary>
    internal FakeDocument Tab(bool open = true)
    {
        var tab = FakeDocument.Part("tab.SLDPRT", PathOf("tab.SLDPRT"));
        System.IO.File.WriteAllText(tab.Document.Path!, "part");
        tab.AddFeature("Sheet-Metal1", SheetMetalParts.SheetMetalType);
        tab.AddFeature("Flat-Pattern1", SheetMetalParts.FlatPatternType, suppressed: true);
        tab.Sheet = new SheetDefaults { ThicknessMm = 2, BendRadiusMm = 3, Allowance = new BendAllowance { KFactor = 0.5 } };
        tab.FlatStraightMm = 77;
        tab.FlatWidthMm = 100;
        tab.FakeBends.Add(new FakeBend { Feature = tab.AddFeature("EdgeBend1", "OneBend", parent: "Edge-Flange1"), Owner = "Edge-Flange1", AngleDeg = 90, RadiusMm = 3 });
        if (open) Sw.Track(tab);
        else Sw.OpenWith = p => new OpenOutcome(string.Equals(p, tab.Document.Path, StringComparison.OrdinalIgnoreCase) ? tab : null, 0, 0);
        return tab;
    }
}

public class DrawingChangeTests : DrawingTestBase
{
    TestDrawingChangeResult Change(TestDrawingChangeRequest request) => Run(new TestDrawingChangeOperation(Services), request);

    [Fact]
    public void A_change_reports_what_it_made_the_sheets_and_views_before_and_after_and_the_models_it_shows()
    {
        var result = Change(new TestDrawingChangeRequest { Rescale = true });
        var c = result.Change;
        Assert.Equal(new[] { "Sheet2", "Drawing View1" }, c.Created.Select(i => i.Name));
        Assert.Equal(new[] { ReferenceKind.Sheet, ReferenceKind.View }, c.Created.Select(i => i.Kind));
        Assert.StartsWith("r1.view.", c.Created[1].Reference);
        Assert.Equal("scale", c.Changed.Single().Property);
        Assert.Equal((1, 2, 0, 1), (c.SheetsBefore, c.SheetsAfter, c.ViewsBefore, c.ViewsAfter));
        var model = Assert.Single(c.Models);
        Assert.Equal(Block.Document.Path, model.Path);
        Assert.False(model.DirtyBefore);
        Assert.False(model.DirtyAfter);
        Assert.False(Block.Document.Visible);
    }

    [Theory]
    [InlineData("change", ErrorCode.OperationFailed)]
    [InlineData("check", ErrorCode.VerificationFailed)]
    public void A_failure_deletes_what_was_made_newest_first_and_puts_edits_back(string failIn, ErrorCode code)
    {
        var e = Fails(() => Change(new TestDrawingChangeRequest { FailIn = failIn, Rescale = true }));
        Assert.Equal(code, e.Code);
        Assert.Equal(new object?[] { "Sheet1", "Drawing View1", "Sheet2" }, (List<object?>)e.Error.Details!["rolled_back"]!);
        Assert.Equal(new[] { "Sheet1" }, Drw.SheetNames());
        Assert.Equal(1, Drw.ReadSheet("Sheet1")!.Scale.Denominator);
        Assert.Equal("delete Drawing View1", Drw.Calls[Drw.Calls.Count - 2]);
    }

    [Theory]
    [InlineData("dirty", "marked as having unsaved changes")]
    [InlineData("errors", "1 more rebuild errors")]
    public void A_model_the_change_touched_is_refused_and_the_change_rolled_back(string failIn, string what)
    {
        Sw.Track(Block);
        var e = Fails(() => Change(new TestDrawingChangeRequest { FailIn = failIn }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("models", e.Error.Details!["check"]);
        Assert.Contains(what, (string)((List<object?>)e.Error.Details["models"]!).Single()!);
        Assert.Equal(new[] { "Sheet1" }, Drw.SheetNames());
    }

    [Fact]
    public void A_model_dirty_before_the_change_is_no_change()
    {
        Sw.Track(Block);
        Block.Document.Dirty = true;
        var result = Change(new TestDrawingChangeRequest { FailIn = "dirty" });
        Assert.True(result.Change.Models.Single().DirtyBefore);
    }

    [Fact]
    public void A_failure_before_anything_was_made_keeps_its_own_code()
    {
        var e = Fails(() => Change(new TestDrawingChangeRequest { FailIn = "early" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_rollback_that_fails_is_operation_failed_naming_what_remains_on_the_drawing()
    {
        Drw.DeleteFails = true;
        var e = Fails(() => Change(new TestDrawingChangeRequest { FailIn = "check" }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Equal("verification_failed", e.Error.Details["cause"]);
        Assert.Contains("drawing.read", e.Message);
    }

    [Theory]
    [InlineData("sheet format")]
    [InlineData("sketch")]
    [InlineData("command")]
    public void The_user_editing_makes_it_busy_before_anything_is_made(string what)
    {
        if (what == "sheet format") Drw.EditingFormat = true;
        if (what == "sketch") Doc.EditingSketchName = "Sketch1";
        if (what == "command") Sw.OpenCommandTitle = "Note";
        var e = Fails(() => Change(new TestDrawingChangeRequest()));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
        if (what == "sheet format") Assert.Equal(true, e.Error.Details["sheet_format"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void The_sheet_format_is_named_rather_than_the_formats_own_sketch_SOLIDWORKS_has_active()
    {
        Drw.EditingFormat = true;
        Doc.EditingSketchName = "Sheet Format1";
        var e = Fails(() => Change(new TestDrawingChangeRequest()));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal(true, e.Error.Details!["sheet_format"]);
        Assert.False(e.Error.Details.ContainsKey("sketch"));
        Assert.Contains("editing the sheet format", e.Message);
    }

    [Fact]
    public void Diag_editing_reads_a_drawings_sheet_format_and_view_sketch_as_its_guard_does()
    {
        var services = new AddInServices(Sw, "0.1.0-test", new AgentRuntime("0.1.0-test", 1, NullLog.Instance, diagnosticsEnabled: true));
        var op = new SwAgent.AddIn.Operations.DiagEditingOperation(services);
        var idle = Run(op, new DiagEditingRequest());
        Assert.False(idle.SheetFormat);
        Assert.Null(idle.Sketch);
        Drw.EditingFormat = true;
        Doc.EditingSketchName = "Section Line1";
        var busy = Run(op, new DiagEditingRequest { Document = Doc.Document.Path });
        Assert.True(busy.SheetFormat);
        Assert.Equal("Section Line1", busy.Sketch);
    }

    [Fact]
    public void A_part_or_assembly_is_the_wrong_document_type_naming_the_drawing_tools()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        var e = Fails(() => Change(new TestDrawingChangeRequest { Document = "plate.SLDPRT" }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("works on drawings", e.Message);
        Assert.Contains("view.create", (string)e.Error.Details!["instead"]!);
    }
}
