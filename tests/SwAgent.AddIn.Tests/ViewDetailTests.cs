using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Details of the block's front view at 1:1 centred at (100, 150): drawn 70..130 by 130..170, the hole's axis (20, 20) drawn at (90, 150).</summary>
public class ViewDetailTests : DrawingTestBase
{
    readonly FakeView _front;

    public ViewDetailTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
    }

    ViewDetailResult Detail(ModelPlace centre, double radius = 8, ScaleRatio? scale = null, SheetPoint? at = null, string? label = null, string? parent = null) =>
        Run(new ViewDetailOperation(Services), new ViewDetailRequest
        {
            Parent = parent ?? _front.Facts.Name, Centre = centre, RadiusMm = radius, Scale = scale ?? new ScaleRatio(2, 1), At = at, Label = label,
        });

    static ModelPlace Hole => new ModelPlace { Point = new Point3(20, 20, 10) };

    [Fact]
    public void A_detail_of_the_hole_at_2_to_1_goes_right_of_the_parent_drawn_inside_its_circle()
    {
        var r = Detail(Hole);
        Assert.Equal(ViewType.Detail, r.View.Type);
        Assert.Equal(_front.Facts.Name, r.View.Parent);
        Assert.Equal("A", r.Label);
        Assert.Equal(2, r.View.Scale.Numerator);
        // Right of the front's edge (130) by 20 mm and the drawn radius, 8 x 2.
        Assert.Equal(166, r.View.Position.X, 9);
        Assert.Equal(150, r.View.Position.Y, 9);
        Assert.Equal("circle (90, 150) 8", Drw.Sketched.Single());
        Assert.True(r.PointsInside > 0);
        Assert.Equal(8, r.RadiusMm);
        Assert.Equal(new[] { "A" }, Drw.CutLabels(_front.Facts.Name));
    }

    [Fact]
    public void A_position_and_a_label_are_taken_and_labels_are_shared_with_sections()
    {
        Run(new ViewSectionOperation(Services), new ViewSectionRequest { Parent = _front.Facts.Name, Cut = new SectionCut { Through = Hole } });
        Assert.Equal("B", Detail(Hole, at: new SheetPoint(300, 100)).Label);
        var e = Fails(() => Detail(Hole, label: "A"));
        Assert.Equal("/label", e.Error.Details!["field"]);
        Assert.Equal("Z", Detail(Hole, label: "Z", at: new SheetPoint(300, 220)).Label);
    }

    [Fact]
    public void A_detail_of_a_section_is_taken()
    {
        string section = Run(new ViewSectionOperation(Services), new ViewSectionRequest { Parent = _front.Facts.Name, Cut = new SectionCut { Through = Hole } }).View.Name;
        var r = Detail(Hole, parent: section, at: new SheetPoint(300, 100));
        Assert.Equal(section, r.View.Parent);
    }

    [Theory]
    [InlineData("centre outside", "/centre")]
    [InlineData("radius 0", "/radius_mm")]
    [InlineData("scale 0", "/scale/numerator")]
    [InlineData("off the sheet", "/at")]
    public void Each_bad_request_is_refused_naming_its_field_before_anything_is_drawn(string what, string field)
    {
        var e = Fails(() => _ = what switch
        {
            "centre outside" => Detail(new ModelPlace { Point = new Point3(90, 20, 10) }),
            "radius 0" => Detail(Hole, radius: 0),
            "scale 0" => Detail(Hole, scale: new ScaleRatio(0, 1)),
            _ => Detail(Hole, radius: 200),
        });
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void An_empty_detail_is_refused_as_the_known_gap_and_rolled_back_with_its_circle()
    {
        Drw.DetailEmpty = true;
        var e = Fails(() => Detail(Hole));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("empty", e.Error.Details!["check"]);
        Assert.Contains("known gap", (string)e.Error.Details["measured"]!);
        Assert.Single(Drw.ViewNames("Sheet1"));
        Assert.Empty(Drw.CutLabels(_front.Facts.Name));
        Assert.Contains("delete detail circle A", Drw.Calls);
    }

    [Fact]
    public void A_detail_drawing_beyond_its_circle_is_rolled_back()
    {
        // A square 20 mm across about the detail's centre at 1:1 model metres: drawn 40 mm across at 2:1, past the 16 mm radius.
        Drw.AfterCreate = v => v.Drawn = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, 2, -0.01, -0.01, 0, 0.01, 0.01, 0 };
        var e = Fails(() => Detail(Hole));
        Assert.Equal("drawn", e.Error.Details!["check"]);
        Assert.Contains("beyond its radius 16 mm", (string)e.Error.Details["measured"]!);
    }

    [Fact]
    public void A_detail_turned_from_its_parent_is_rolled_back()
    {
        Drw.AfterCreate = v => v.Transform = new SwAgent.AddIn.Core.ViewTransform(new double[] { 0, 1, 0, -1, 0, 0, 0, 0, 1 }, v.Transform!.TxMm, v.Transform.TyMm, 0, v.Transform.Scale);
        Assert.Equal("turn", Fails(() => Detail(Hole)).Error.Details!["check"]);
    }

    [Fact]
    public void A_detail_SOLIDWORKS_puts_off_where_asked_is_moved_so_its_circles_centre_is_drawn_there()
    {
        // 5.B live run: SOLIDWORKS 2026 SP4 gives a detail's Position as its own origin, about 20 mm from the circle's
        // centre at 2:1 (the fake does too), and drew that centre about 0.34 mm off where it was asked.
        Drw.AfterCreate = v =>
        {
            v.Facts.Position = new SheetPoint(v.Facts.Position.X - 0.344, v.Facts.Position.Y + 0.326);
            var t = v.Transform!;
            v.Transform = new SwAgent.AddIn.Core.ViewTransform(t.Rotation, t.TxMm - 0.344, t.TyMm + 0.326, t.TzMm, t.Scale);
        };
        var r = Detail(Hole, at: new SheetPoint(300, 100));
        // Reported where it draws the circle's centre, as at was given, not at SOLIDWORKS' Position.
        Assert.Equal(300, r.View.Position.X, 9);
        Assert.Equal(100, r.View.Position.Y, 9);
        Assert.True(System.Math.Abs(Drw.ReadView(r.View.Name)!.Position.X - 300) > 10);
        var centre = SwAgent.AddIn.Core.ViewGeometry.ToSheet(Drw.Transform(r.View.Name)!, new Point3(20, 20, 10));
        Assert.Equal(300, centre.X, 9);
        Assert.Equal(100, centre.Y, 9);
    }

    [Fact]
    public void A_detail_whose_circle_reads_elsewhere_than_where_it_was_drawn_is_rolled_back()
    {
        Drw.AfterCreate = v => v.Circle = new Point2(v.Circle!.X + 5, v.Circle.Y);
        var e = Fails(() => Detail(Hole, at: new SheetPoint(300, 100)));
        Assert.Equal("position", e.Error.Details!["check"]);
        Assert.Contains("read from", e.Message);
    }

    [Fact]
    public void Polylines_that_do_not_read_as_edges_are_api_error_giving_SOLIDWORKS_numbers()
    {
        Drw.AfterCreate = v => v.Drawn = new List<double> { 1, 0, 0, 1, 2, 3, 4, 5, 7, 0.25 };
        var e = Fails(() => Detail(Hole));
        Assert.Equal(ErrorCode.ApiError, e.Code);
        Assert.Contains("10 numbers: 1, 0, 0, 1, 2, 3, 4, 5, 7, 0.25", e.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_detail_circle_left_behind_by_a_rollback_is_rollback_failed_naming_it_never_a_clean_rollback(bool deleteSaysSuccess)
    {
        // 5.B live run: the detail failed a check, its view was deleted, and its circle stayed on the parent.
        Drw.DetailEmpty = true;
        if (deleteSaysSuccess) Drw.CutDeleteLeaves = true;
        else Drw.CutDeleteFails = true;
        var e = Fails(() => Detail(Hole));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Contains("detail circle A from Drawing View1", e.Message);
        Assert.DoesNotContain("was rolled back", e.Message);
        Assert.Equal(new[] { "A" }, Drw.CutLabels(_front.Facts.Name));
    }

    [Fact]
    public void SOLIDWORKS_making_no_detail_is_operation_failed()
    {
        Drw.CreateFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Detail(Hole)).Code);
    }
}
