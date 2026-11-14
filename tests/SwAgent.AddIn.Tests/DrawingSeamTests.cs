using System.Linq;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SolidWorksDrawingTests
{
    [Theory]
    [InlineData(420, 297, (int)swDwgPaperSizes_e.swDwgPaperA3size)]
    [InlineData(210, 297, (int)swDwgPaperSizes_e.swDwgPaperA4sizeVertical)]
    [InlineData(279.4, 215.9, (int)swDwgPaperSizes_e.swDwgPaperAsize)]
    [InlineData(1117.6, 863.6, (int)swDwgPaperSizes_e.swDwgPaperEsize)]
    [InlineData(297, 420, (int)swDwgPaperSizes_e.swDwgPapersUserDefined)]
    [InlineData(500, 300, (int)swDwgPaperSizes_e.swDwgPapersUserDefined)]
    public void A_sheet_size_has_SOLIDWORKS_paper_code_or_is_user_defined(double width, double height, int code)
    {
        Assert.Equal(code, SolidWorksDrawing.PaperSize(width, height));
    }

    [Theory]
    [InlineData((int)swDrawingViewTypes_e.swDrawingStandardView, false, ViewType.Model)]
    [InlineData((int)swDrawingViewTypes_e.swDrawingNamedView, false, ViewType.Model)]
    [InlineData((int)swDrawingViewTypes_e.swDrawingProjectedView, false, ViewType.Projected)]
    [InlineData((int)swDrawingViewTypes_e.swDrawingSectionView, false, ViewType.Section)]
    [InlineData((int)swDrawingViewTypes_e.swDrawingDetailView, false, ViewType.Detail)]
    [InlineData((int)swDrawingViewTypes_e.swDrawingAuxiliaryView, false, ViewType.Other)]
    [InlineData((int)swDrawingViewTypes_e.swDrawingNamedView, true, ViewType.FlatPattern)]
    public void View_types_are_named_as_the_design_names_them(int swType, bool flat, ViewType expected)
    {
        Assert.Equal(expected, SolidWorksDrawing.TypeOf(swType, flat));
    }

    [Fact]
    public void Display_modes_go_both_ways_and_faceted_ones_read_as_plain()
    {
        foreach (ViewDisplay d in System.Enum.GetValues(typeof(ViewDisplay)))
            Assert.Equal(d, SolidWorksDrawing.DisplayOf(SolidWorksDrawing.ModeOf(d).Mode));
        Assert.Equal(ViewDisplay.HiddenLinesRemoved, SolidWorksDrawing.DisplayOf((int)swDisplayMode_e.swFACETED_HIDDEN));
        Assert.True(SolidWorksDrawing.ModeOf(ViewDisplay.ShadedWithEdges).Edges);
        Assert.Null(SolidWorksDrawing.DisplayOf((int)swDisplayMode_e.swDisplayModeUNKNOWN));
    }

    [Theory]
    [InlineData("*Front", "front")]
    [InlineData("*Isometric", "isometric")]
    [InlineData("*Trimetric", "trimetric")]
    [InlineData("Shop view", "Shop view")]
    [InlineData("", null)]
    public void Orientation_names_read_as_the_design_names_them(string sw, string? expected)
    {
        Assert.Equal(expected, SolidWorksDrawing.OrientationOf(sw));
    }

    [Theory]
    [InlineData("front", "*Front")]
    [InlineData("dimetric", "*Dimetric")]
    [InlineData("Shop view", "Shop view")]
    public void Orientations_are_asked_for_by_SOLIDWORKS_names(string orientation, string expected)
    {
        Assert.Equal(expected, SolidWorksDrawing.SolidWorksOrientation(orientation));
    }

    [Fact]
    public void Every_drawing_setting_is_a_SOLIDWORKS_document_toggle()
    {
        Assert.Equal(DrawingSettings.All.OrderBy(n => n), SolidWorksDrawing.Toggles.Keys.OrderBy(n => n));
        Assert.Equal(swUserPreferenceToggle_e.swDisplayOrigins, SolidWorksDrawing.Toggles["display_origins"]);
        Assert.Equal(swUserPreferenceToggle_e.swDetailingAutoInsertDimsMarkedForDrawing, SolidWorksDrawing.Toggles["auto_insert_dims_marked_for_drawing"]);
    }
}

public class FakeDrawingTests
{
    static (FakeDocument Drawing, FakeDocument Block) Scene()
    {
        var drawing = FakeDocument.NewDrawing("block.SLDDRW", @"C:\d\block.SLDDRW");
        var block = FakeDocument.Part("block.SLDPRT", @"C:\d\block.SLDPRT");
        block.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
        drawing.Drw!.Models[block.Document.Path!] = block;
        return (drawing, block);
    }

    [Fact]
    public void A_new_drawing_has_one_sheet_and_no_view_and_is_a_drawing_only()
    {
        var (drawing, block) = Scene();
        Assert.Equal(new[] { "Sheet1" }, drawing.Drw!.SheetNames());
        Assert.Empty(drawing.Drw.ViewNames("Sheet1"));
        Assert.Null(block.Drawing());
        Assert.Null(drawing.Assembly());
    }

    [Fact]
    public void A_model_view_is_drawn_through_its_transform_centred_where_it_was_put()
    {
        var (drawing, block) = Scene();
        var d = drawing.Drw!;
        string name = d.CreateModelView(block.Document.Path!, "front", new SheetPoint(100, 150))!;
        var facts = d.ReadView(name)!;
        Assert.Equal(100, facts.Position.X, 9);
        var t = d.Transform(name)!;
        Assert.Equal("front", ViewGeometry.Orientation(ViewGeometry.Facing(t)));
        var drawn = ViewGeometry.DrawnExtents(d.Polylines(name), facts.Position, t.Scale)!;
        Assert.True(ViewGeometry.SameBox(ViewGeometry.Project(t, block.ExactBox!), drawn));
        Assert.Equal(new[] { block.Document.Path }, drawing.Uses);
    }

    [Fact]
    public void A_projected_view_is_turned_toward_its_side_and_aligned()
    {
        var (drawing, block) = Scene();
        var d = drawing.Drw!;
        string front = d.CreateModelView(block.Document.Path!, "front", new SheetPoint(100, 150))!;
        string right = d.ProjectView(front, new SheetPoint(200, 160))!;
        Assert.Null(ViewGeometry.AlignmentMismatch(d.Transform(front)!, d.Transform(right)!, ProjectDirection.Right));
        Assert.Equal(150, d.ReadView(right)!.Position.Y, 9);
        d.SetPosition(right, new SheetPoint(220, 100));
        Assert.Equal(220, d.ReadView(right)!.Position.X, 9);
        Assert.Equal(150, d.ReadView(right)!.Position.Y, 9);
    }

    [Fact]
    public void A_view_that_follows_the_sheet_is_redrawn_at_its_new_scale()
    {
        var (drawing, block) = Scene();
        var d = drawing.Drw!;
        string front = d.CreateModelView(block.Document.Path!, "front", new SheetPoint(100, 150))!;
        var sheet = d.ReadSheet("Sheet1")!;
        sheet.Scale = new ScaleRatio(1, 2);
        d.SetSheet("Sheet1", sheet);
        Assert.Equal(0.5, d.Transform(front)!.Scale);
        Assert.Equal(2, d.ReadView(front)!.Scale.Denominator);
        var drawn = ViewGeometry.DrawnExtents(d.Polylines(front), d.ReadView(front)!.Position, 0.5)!;
        Assert.Equal(30, ViewGeometry.Width(drawn), 9);
    }

    [Fact]
    public void A_section_is_cut_where_its_line_was_drawn_faces_across_it_and_is_aligned()
    {
        var (drawing, block) = Scene();
        var d = drawing.Drw!;
        string front = d.CreateModelView(block.Document.Path!, "front", new SheetPoint(100, 150))!;
        // The front draws the block 70..130 by 130..170; a line at y 150 is the block's y 20.
        string section = d.CutSection(front, new SheetPoint(65, 150), new SheetPoint(135, 150), new SheetPoint(100, 90), "A", false)!;
        var facts = d.ReadView(section)!;
        Assert.Equal(ViewType.Section, facts.Type);
        Assert.Equal(front, facts.Parent);
        Assert.Equal("A", d.ReadSection(section)!.Label);
        Assert.Equal(new[] { "A" }, d.CutLabels(front));
        Assert.Null(ViewCutGeometry.SectionAlignmentMismatch(d.Transform(front)!, d.Transform(section)!, CutOrientation.Horizontal));
        var face = Assert.Single(d.CutFaces(section));
        Assert.Null(ViewCutGeometry.CutPlaneMismatch(ViewGeometry.Facing(d.Transform(section)!), new Point3(30, 20, 10), d.CutFaces(section)));
        Assert.Equal(20, face.Point!.Y, 9);
        Assert.Equal("line (65, 150) (135, 150)", d.Sketched.Single());
        d.DeleteViews(new[] { section });
        Assert.Equal(new[] { "A" }, d.CutLabels(front));
        Assert.True(d.DeleteSectionLine(front, "A"));
        Assert.Empty(d.CutLabels(front));
    }

    [Fact]
    public void A_detail_is_its_parent_turned_the_same_drawn_inside_its_circle_at_its_scale()
    {
        var (drawing, block) = Scene();
        var d = drawing.Drw!;
        string front = d.CreateModelView(block.Document.Path!, "front", new SheetPoint(100, 150))!;
        // The hole's centre (20, 20) is drawn at (90, 150); a circle of 8 mm there, shown at 2:1 at (300, 100).
        string detail = d.DetailView(front, new SheetPoint(90, 150), 8, new SheetPoint(300, 100), new ScaleRatio(2, 1), "B")!;
        var t = d.Transform(detail)!;
        Assert.Equal(2, t.Scale);
        Assert.Null(ViewCutGeometry.DetailTurnMismatch(d.Transform(front)!, t));
        var centre = ViewGeometry.ToSheet(t, new Point3(20, 20, 0));
        Assert.Equal(300, centre.X, 9);
        Assert.Equal(100, centre.Y, 9);
        var points = ViewGeometry.DrawnPoints(d.Polylines(detail), d.ReadView(detail)!.Position, t.Scale);
        Assert.Null(ViewCutGeometry.OutsideCircle(points, centre, 16));
        Assert.Equal("B", d.ReadDetail(detail)!.Label);
        Assert.Equal("circle (90, 150) 8", d.Sketched.Single());
    }

    [Fact]
    public void A_flat_pattern_view_is_made_only_of_an_open_part_and_draws_its_flat_size()
    {
        var (drawing, _) = Scene();
        var d = drawing.Drw!;
        var sw = new FakeSolidWorks();
        d.Sw = sw;
        var tab = FakeDocument.Part("tab.SLDPRT", @"C:\d\tab.SLDPRT");
        tab.Sheet = new SheetDefaults { ThicknessMm = 2, BendRadiusMm = 3 };
        tab.FlatStraightMm = 77;
        tab.FlatWidthMm = 100;
        tab.FakeBends.Add(new FakeBend { Feature = tab.AddFeature("EdgeBend1", "OneBend"), Owner = "Edge-Flange1", AngleDeg = 90, RadiusMm = 3 });
        Assert.Null(d.FlatPatternView(tab.Document.Path!, "Default", new SheetPoint(200, 150)));
        sw.Track(tab);
        string flat = d.FlatPatternView(tab.Document.Path!, "Default", new SheetPoint(200, 150))!;
        Assert.Equal(ViewType.FlatPattern, d.ReadView(flat)!.Type);
        Assert.Equal(1, d.BendLines(flat));
        var drawn = ViewGeometry.DrawnExtents(d.Polylines(flat), new SheetPoint(200, 150), 1)!;
        Assert.Equal(100, ViewGeometry.Width(drawn), 4);
        Assert.Equal(83.2832, ViewGeometry.Height(drawn), 4);
        d.SetBendNotes(flat, true);
        Assert.True(d.BendNotes(flat));
    }
}
