using System;
using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Design 611, Error handling: every code each drawing operation defines is reachable.</summary>
public class DrawingErrorCodeTests : DrawingTestBase
{
    readonly FakeView _front;
    readonly string _right;

    public DrawingErrorCodeTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        _right = Drw.ProjectView(_front.Facts.Name, new SheetPoint(160, 150))!;
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.Active = "Sheet1";
        Drw.Calls.Clear();
    }

    /// <summary>One request of every drawing operation, on <paramref name="document"/> (the active document when null).</summary>
    IEnumerable<(string Name, Action Call)> Every(string? document) => new (string, Action)[]
    {
        ("drawing.read", () => Run(new DrawingReadOperation(Services), new DrawingReadRequest { Document = document })),
        ("sheet.add", () => Run(new SheetAddOperation(Services), new SheetAddRequest { Document = document, Size = SheetSize.A4 })),
        ("sheet.set", () => Run(new SheetSetOperation(Services), new SheetSetRequest { Document = document, Sheet = "Sheet1", Scale = new ScaleRatio(1, 2) })),
        ("sheet.activate", () => Run(new SheetActivateOperation(Services), new SheetActivateRequest { Document = document, Sheet = "Sheet2" })),
        ("sheet.delete", () => Run(new SheetDeleteOperation(Services), new SheetDeleteRequest { Document = document, Sheets = { "Sheet2" } })),
        ("view.create", () => Run(new ViewCreateOperation(Services), new ViewCreateRequest { Document = document, Model = Block.Document.Path!, Orientation = "top", At = new SheetPoint(100, 60) })),
        ("view.project", () => Run(new ViewProjectOperation(Services), new ViewProjectRequest { Document = document, Parent = _front.Facts.Name, Direction = ProjectDirection.Above })),
        ("view.set", () => Run(new ViewSetOperation(Services), new ViewSetRequest { Document = document, View = _front.Facts.Name, Display = ViewDisplay.Shaded })),
        ("view.delete", () => Run(new ViewDeleteOperation(Services), new ViewDeleteRequest { Document = document, Views = { _right } })),
        ("view.entities", () => Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { Document = document, View = _front.Facts.Name })),
    };

    [Fact]
    public void Wrong_document_type_for_every_drawing_operation_on_a_part_naming_the_drawing_tools()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        foreach (var (name, call) in Every("plate.SLDPRT"))
        {
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.WrongDocumentType, $"{name}: {e.Code}");
            Assert.Contains("document.new", (string)e.Error.Details!["instead"]!);
        }
    }

    [Fact]
    public void Wrong_document_type_for_a_model_operation_on_a_drawing()
    {
        var e = Fails(() => Run(new ModelTreeOperation(Services), new ModelTreeRequest { Document = "block.SLDDRW" }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }

    [Fact]
    public void Busy_for_every_change_while_the_user_edits_the_sheet_format()
    {
        Drw.EditingFormat = true;
        foreach (var (name, call) in Every(null))
        {
            if (name == "drawing.read" || name == "view.entities") continue;
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.Busy, $"{name}: {e.Code}");
            Assert.Equal("user_editing", e.Error.Details!["reason"]);
        }
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void Reference_unresolved_for_a_sheet_or_view_that_is_gone()
    {
        string sheet = Services.References.IssueSheet(Doc, "Sheet2");
        string view = Services.References.IssueView(Doc, Drw.ReadView(_right)!);
        Drw.DeleteSheet("Sheet2");
        Drw.DeleteViews(new[] { _right });
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new SheetSetOperation(Services), new SheetSetRequest { Sheet = sheet, Scale = new ScaleRatio(1, 2) })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new SheetActivateOperation(Services), new SheetActivateRequest { Sheet = sheet })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new ViewSetOperation(Services), new ViewSetRequest { View = view, Display = ViewDisplay.Shaded })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = view })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new ViewProjectOperation(Services), new ViewProjectRequest { Parent = view, Direction = ProjectDirection.Right })).Code);
    }

    [Fact]
    public void Operation_failed_when_SOLIDWORKS_does_nothing()
    {
        Drw.AddSheetFails = Drw.CreateFails = Drw.DeleteFails = Drw.ActivateFails = true;
        foreach (var (name, call) in Every(null))
        {
            if (name == "drawing.read" || name == "sheet.set" || name == "view.set" || name == "view.entities") continue;
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.OperationFailed, $"{name}: {e.Code}");
        }
    }

    [Fact]
    public void Verification_failed_when_SOLIDWORKS_answers_but_the_drawing_says_otherwise()
    {
        Drw.SetSheetIgnored = Drw.ViewSetIgnored = true;
        Drw.SheetScaleIgnored = new ScaleRatio(9, 1);
        Drw.FacesInstead = "bottom";
        foreach (var name in new[] { "sheet.add", "sheet.set", "view.create", "view.set" })
        {
            var call = System.Linq.Enumerable.Single(Every(null), c => c.Name == name).Call;
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.VerificationFailed, $"{name}: {e.Code}");
        }
    }

    [Fact]
    public void Api_error_when_SOLIDWORKS_gives_a_view_no_transform()
    {
        _front.Transform = null;
        Assert.Equal(ErrorCode.ApiError, Fails(() => Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = _front.Facts.Name })).Code);
    }
}
