using System.Collections.Generic;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class SectionGeometryTests
{
    // The block (0..60, 0..40, 0..20) in a front view at 1:2 centred at (100, 150): the origin drawn at (85, 140), drawn 85..115 by 140..160.
    static readonly ViewTransform Front = Views.Front(0.5, 85, 140);
    static readonly SheetBox Drawn = new SheetBox { Min = new SheetPoint(85, 140), Max = new SheetPoint(115, 160) };

    [Fact]
    public void A_sheet_point_is_model_mm_about_the_views_position_in_its_sketch_and_back()
    {
        var sketch = ViewCutGeometry.ToViewSketch(new SheetPoint(100, 150), 0.5, new SheetPoint(95, 160));
        Assert.Equal(-10, sketch.X, 9);
        Assert.Equal(20, sketch.Y, 9);
        var back = ViewCutGeometry.FromViewSketch(new SheetPoint(100, 150), 0.5, sketch);
        Assert.Equal(95, back.X, 9);
        Assert.Equal(160, back.Y, 9);
    }

    [Fact]
    public void A_centre_marks_own_position_is_in_its_views_frame()
    {
        // The 5.C live run (round 2): the hole's mark in the front view at (130, 180), 1:1, read ICenterMark.GetPosition(0)
        // (-10, 0) for the hole drawn at (120, 180).
        var drawn = ViewCutGeometry.FromViewSketch(new SheetPoint(130, 180), 1, new Point2(-10, 0));
        Assert.Equal(120, drawn.X, 9);
        Assert.Equal(180, drawn.Y, 9);
    }

    [Fact]
    public void A_horizontal_line_through_the_holes_axis_crosses_the_view_with_an_overrun()
    {
        var line = ViewCutGeometry.CutLine(Front, Drawn, new Point3(20, 20, 10), CutOrientation.Horizontal)!.Value;
        Assert.Equal(80, line.From.X, 9);
        Assert.Equal(150, line.From.Y, 9);
        Assert.Equal(120, line.To.X, 9);
        Assert.Equal(150, line.To.Y, 9);
    }

    [Fact]
    public void A_vertical_line_runs_top_to_bottom_through_the_point()
    {
        var line = ViewCutGeometry.CutLine(Front, Drawn, new Point3(20, 20, 10), CutOrientation.Vertical)!.Value;
        Assert.Equal(95, line.From.X, 9);
        Assert.Equal(165, line.From.Y, 9);
        Assert.Equal(135, line.To.Y, 9);
    }

    [Theory]
    [InlineData(20, 40, CutOrientation.Horizontal)]
    [InlineData(20, 70, CutOrientation.Horizontal)]
    [InlineData(0, 20, CutOrientation.Vertical)]
    [InlineData(-5, 20, CutOrientation.Vertical)]
    public void A_line_on_or_beyond_the_views_edge_cuts_nothing(double x, double y, CutOrientation orientation)
    {
        Assert.Null(ViewCutGeometry.CutLine(Front, Drawn, new Point3(x, y, 10), orientation));
    }

    [Fact]
    public void Without_a_position_a_section_goes_below_or_right_of_its_parent_on_its_centre_line()
    {
        var below = ViewCutGeometry.SectionPlace(new SheetPoint(100, 150), Drawn, 10, CutOrientation.Horizontal, 20);
        Assert.Equal(100, below.X, 9);
        Assert.Equal(115, below.Y, 9);
        var right = ViewCutGeometry.SectionPlace(new SheetPoint(100, 150), Drawn, 10, CutOrientation.Vertical, 20);
        Assert.Equal(140, right.X, 9);
        Assert.Equal(150, right.Y, 9);
    }

    [Fact]
    public void A_section_faces_along_its_parents_up_or_right_either_way_and_shares_its_centre_line()
    {
        // Looking down at a horizontal cut (the top view's axes), the origin drawn at x 85 as in the front.
        var down = Views.Top(0.5, 85, 100);
        Assert.Null(ViewCutGeometry.SectionAlignmentMismatch(Front, down, CutOrientation.Horizontal));
        var up = Views.Of(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0), 0.5, 85, 100);
        Assert.Null(ViewCutGeometry.SectionAlignmentMismatch(Front, up, CutOrientation.Horizontal));
        Assert.Contains("vertical centre line", ViewCutGeometry.SectionAlignmentMismatch(Front, Views.Top(0.5, 86, 100), CutOrientation.Horizontal));
        Assert.Contains("should face", ViewCutGeometry.SectionAlignmentMismatch(Front, Views.Right(0.5, 85, 100), CutOrientation.Horizontal));
        Assert.Null(ViewCutGeometry.SectionAlignmentMismatch(Front, Views.Right(0.5, 200, 140), CutOrientation.Vertical));
    }

    [Fact]
    public void Hatched_faces_in_the_plane_through_the_point_pass()
    {
        var faces = new List<CutFace>
        {
            new CutFace(new Vector3(0, 1, 0), new Point3(0, 20, 0), 300),
            new CutFace(new Vector3(0, -1, 0), new Point3(40, 20, 20), 500),
        };
        Assert.Null(ViewCutGeometry.CutPlaneMismatch(new Vector3(0, 1, 0), new Point3(20, 20, 10), faces));
    }

    [Fact]
    public void No_hatch_a_hatch_without_a_face_a_turned_face_or_a_face_off_the_point_fails_saying_which()
    {
        var through = new Point3(20, 20, 10);
        var up = new Vector3(0, 1, 0);
        Assert.Contains("cut nothing", ViewCutGeometry.CutPlaneMismatch(up, through, new List<CutFace>()));
        Assert.Contains("no planar face", ViewCutGeometry.CutPlaneMismatch(up, through, new List<CutFace> { new CutFace(null, null, 0) }));
        Assert.Contains("square to", ViewCutGeometry.CutPlaneMismatch(up, through, new List<CutFace> { new CutFace(new Vector3(1, 0, 0), new Point3(20, 20, 0), 1) }));
        Assert.Contains("0.02 mm from", ViewCutGeometry.CutPlaneMismatch(up, through, new List<CutFace> { new CutFace(up, new Point3(0, 20.02, 0), 1) }));
    }

    [Fact]
    public void Labels_run_A_to_Z_without_I_O_and_Q_and_skip_those_used()
    {
        Assert.Equal("A", ViewCutGeometry.NextLabel(new string[0]));
        Assert.Equal("C", ViewCutGeometry.NextLabel(new[] { "a", "B" }));
        Assert.Equal("J", ViewCutGeometry.NextLabel(new[] { "A", "B", "C", "D", "E", "F", "G", "H" }));
        Assert.Equal(23, System.Linq.Enumerable.Count(ViewCutGeometry.Labels()) / 2);
    }
}
