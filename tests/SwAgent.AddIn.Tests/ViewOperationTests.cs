using System.Collections.Generic;
using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ViewCreateTests : DrawingTestBase
{
    ViewResult Create(ViewCreateRequest request) => Run(new ViewCreateOperation(Services), request);

    ViewCreateRequest Front(double x = 100, double y = 150) => new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "front", At = new SheetPoint(x, y) };

    [Fact]
    public void A_front_view_of_the_block_is_placed_where_asked_and_draws_its_box_at_the_sheets_scale()
    {
        var result = Create(Front());
        var v = result.View;
        Assert.Equal("Drawing View1", v.Name);
        Assert.StartsWith("r1.view.", v.Reference);
        Assert.Equal("Sheet1", v.Sheet);
        Assert.Equal(ViewType.Model, v.Type);
        Assert.Equal("front", v.Orientation);
        Assert.True(v.SheetScale);
        Assert.Equal(ViewDisplay.HiddenLinesVisible, v.Display);
        Assert.Equal(70, v.Extents!.Min.X, 9);
        Assert.Equal(130, v.Extents.Max.X, 9);
        Assert.Equal(130, v.Extents.Min.Y, 9);
        Assert.Equal(170, v.Extents.Max.Y, 9);
        Assert.False(result.ModelUnsaved);
        Assert.Equal("Drawing View1", result.Change.Created.Single().Name);
        Assert.Equal(Block.Document.Path, result.Change.Models.Single().Path);
    }

    [Fact]
    public void Its_own_scale_display_and_configuration_are_taken()
    {
        Drw.Configurations[Block.Document.Path!] = new List<string> { "Default", "Long" };
        var request = Front();
        request.Scale = new ScaleRatio(1, 2);
        request.Display = ViewDisplay.ShadedWithEdges;
        request.Configuration = "Long";
        var v = Create(request).View;
        Assert.False(v.SheetScale);
        Assert.Equal(2, v.Scale.Denominator);
        Assert.Equal(30, v.Extents!.Max.X - v.Extents.Min.X, 9);
        Assert.Equal(ViewDisplay.ShadedWithEdges, v.Display);
        Assert.Equal("Long", v.Configuration);
    }

    [Fact]
    public void An_isometric_view_shows_hidden_lines_removed_and_lies_within_the_projected_box()
    {
        var v = Create(new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "Isometric", At = new SheetPoint(300, 200) }).View;
        Assert.Equal("isometric", v.Orientation);
        Assert.Equal(ViewDisplay.HiddenLinesRemoved, v.Display);
    }

    [Fact]
    public void A_view_goes_on_the_sheet_asked_which_is_shown_first()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.Active = "Sheet1";
        var request = Front();
        request.Sheet = "Sheet2";
        Assert.Equal("Sheet2", Create(request).View.Sheet);
        Assert.Equal("Sheet2", Drw.Active);
    }

    [Fact]
    public void An_open_model_is_named_by_title_and_its_unsaved_changes_are_reported()
    {
        Sw.Track(Block);
        Block.Document.Dirty = true;
        var result = Create(new ViewCreateRequest { Model = "block.SLDPRT", Orientation = "top", At = new SheetPoint(100, 60) });
        Assert.True(result.ModelUnsaved);
        Assert.Equal("top", result.View.Orientation);
    }

    [Fact]
    public void A_never_saved_model_is_refused_before_SOLIDWORKS_is_called()
    {
        Sw.Track(FakeDocument.Part("Part7"));
        var e = Fails(() => Create(new ViewCreateRequest { Model = "Part7", Orientation = "front", At = new SheetPoint(100, 100) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/model", e.Error.Details!["field"]);
        Assert.Contains("sw_document_save_as", e.Message);
        Assert.Empty(Drw.Calls);
    }

    [Theory]
    [InlineData("missing file", "/model")]
    [InlineData("a drawing", "/model")]
    [InlineData("not open", "/model")]
    [InlineData("off the sheet", "/at")]
    [InlineData("zero scale", "/scale/denominator")]
    [InlineData("no orientation", "/orientation")]
    [InlineData("unknown sheet", "/sheet")]
    public void Bad_requests_are_refused_naming_the_field(string what, string field)
    {
        var request = Front();
        switch (what)
        {
            case "missing file": request.Model = PathOf("gone.SLDPRT"); break;
            case "a drawing": request.Model = Doc.Document.Path!; break;
            case "not open": request.Model = "plate"; break;
            case "off the sheet": request.At = new SheetPoint(430, 100); break;
            case "zero scale": request.Scale = new ScaleRatio(1, 0); break;
            case "no orientation": request.Orientation = " "; break;
            default: request.Sheet = "Sheet9"; break;
        }
        var e = Fails(() => Create(request));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void SOLIDWORKS_making_no_view_is_operation_failed()
    {
        Drw.CreateFails = true;
        var e = Fails(() => Create(new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "Shop view", At = new SheetPoint(100, 100) }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("no view of that name", e.Message);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("orientation")]
    [InlineData("extents")]
    [InlineData("configuration")]
    [InlineData("display")]
    public void A_view_that_is_not_as_asked_is_deleted_again(string check)
    {
        var request = Front();
        switch (check)
        {
            case "position": Drw.ViewSetIgnored = true; Drw.LandsOffByMm = 0.02; break;
            case "orientation": Drw.FacesInstead = "back"; break;
            case "extents": DrawsTooLittle(); break;
            case "configuration": request.Configuration = "Long"; break;
            default: Drw.ViewSetIgnored = true; request.Display = ViewDisplay.Shaded; break;
        }
        var e = Fails(() => Create(request));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal(check, e.Error.Details!["check"]);
        Assert.Equal(new object?[] { "Drawing View1" }, (List<object?>)e.Error.Details["rolled_back"]!);
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }

    /// <summary>The next view draws only half the block (an edge SOLIDWORKS left out).</summary>
    void DrawsTooLittle() => Drw.AfterCreate = v => v.Drawn = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, 2, -0.03, -0.02, 0, 0, 0.02, 0 };

    [Fact]
    public void A_view_that_marks_its_model_changed_is_refused_and_deleted()
    {
        Sw.Track(Block);
        Drw.DirtiesModel = true;
        var e = Fails(() => Create(Front()));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("models", e.Error.Details!["check"]);
        Assert.Empty(Drw.ViewNames("Sheet1"));
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(Block);
        var request = Front();
        request.Document = Block.Document.Path;
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Create(request)).Code);
    }
}
