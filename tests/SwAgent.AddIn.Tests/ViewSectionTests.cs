using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Sections of the block's front view at 1:1 centred at (100, 150): drawn 70..130 by 130..170, the hole's axis (20, 20) drawn at (90, 150).</summary>
public class ViewSectionTests : DrawingTestBase
{
    readonly FakeView _front;

    public ViewSectionTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        Block.Add(ReferenceKind.Axis, new EntityInfo { Name = "Axis1", Geometry = GeometryType.Line, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1) });
    }

    ViewSectionResult Section(ModelPlace through, CutOrientation orientation = CutOrientation.Horizontal, SheetPoint? at = null, string? label = null, bool? reversed = null, string? parent = null) =>
        Run(new ViewSectionOperation(Services), new ViewSectionRequest
        {
            Parent = parent ?? _front.Facts.Name, Cut = new SectionCut { Through = through, Orientation = orientation }, At = at, Label = label, Reversed = reversed,
        });

    static ModelPlace Hole => new ModelPlace { Point = new Point3(20, 20, 10) };

    [Fact]
    public void A_horizontal_cut_through_the_hole_goes_below_the_parent_labelled_A_and_hatches_the_plane()
    {
        var r = Section(Hole);
        Assert.Equal(ViewType.Section, r.View.Type);
        Assert.Equal(_front.Facts.Name, r.View.Parent);
        Assert.Equal("A", r.Label);
        Assert.Equal(1, r.HatchedFaces);
        Assert.Equal(100, r.HatchedAreaMm2);
        Assert.Equal(20, r.Through.Y);
        // Below the front with a 20 mm gap: the block is 20 deep, so centred 10 + 20 under its lowest edge (130).
        Assert.Equal(100, r.View.Position.X, 9);
        Assert.Equal(100, r.View.Position.Y, 9);
        Assert.Equal("line (65, 150) (135, 150)", Drw.Sketched.Single());
        Assert.Equal(new[] { "A" }, Drw.CutLabels(_front.Facts.Name));
        Assert.Equal(ReferenceKind.View, r.Change.Created.Single().Kind);
    }

    [Fact]
    public void An_axis_by_name_places_the_cut_and_a_vertical_cut_goes_right_on_the_horizontal_centre_line()
    {
        var r = Section(new ModelPlace { Entity = "Axis1" }, CutOrientation.Vertical, new SheetPoint(200, 150));
        Assert.Equal("line (90, 175) (90, 125)", Drw.Sketched.Single());
        Assert.StartsWith("Axis1 at (20, 20, 0)", r.ThroughIs);
        Assert.Equal(200, r.View.Position.X, 9);
    }

    [Fact]
    public void Labels_follow_on_and_one_in_use_is_refused()
    {
        Section(Hole);
        Assert.Equal("B", Section(Hole, CutOrientation.Vertical).Label);
        var e = Fails(() => Section(Hole, label: "a"));
        Assert.Equal("/label", e.Error.Details!["field"]);
    }

    [Fact]
    public void Reversed_reads_back_and_the_section_faces_the_other_way()
    {
        var r = Section(Hole, reversed: true);
        Assert.True(r.Reversed);
    }

    [Theory]
    [InlineData(20, 40, "/cut/through")]
    [InlineData(20, -5, "/cut/through")]
    public void A_place_on_or_beyond_the_parents_edges_is_refused_before_anything_is_drawn(double x, double y, string field)
    {
        var e = Fails(() => Section(new ModelPlace { Point = new Point3(x, y, 10) }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Contains("cut nothing", e.Message);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_position_off_the_parents_centre_line_is_refused()
    {
        var e = Fails(() => Section(Hole, at: new SheetPoint(110, 80)));
        Assert.Equal("/at/x", e.Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_section_or_detail_is_not_cut_again()
    {
        string section = Section(Hole).View.Name;
        var e = Fails(() => Section(Hole, CutOrientation.Vertical, parent: section));
        Assert.Equal("/parent", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_cut_off_the_point_is_rolled_back_with_its_section_line()
    {
        Drw.CutOffByMm = 0.5;
        var e = Fails(() => Section(Hole));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("cut_plane", e.Error.Details!["check"]);
        Assert.Contains("0.5 mm from", (string)e.Error.Details["measured"]!);
        Assert.Equal(new[] { _front.Facts.Name }, Drw.ViewNames("Sheet1"));
        Assert.Empty(Drw.CutLabels(_front.Facts.Name));
        Assert.Contains("delete section line A", Drw.Calls);
    }

    [Fact]
    public void A_section_SOLIDWORKS_deletes_with_its_line_is_rolled_back_without_deleting_the_line_again()
    {
        Drw.DeleteTakesCuts = true;
        Drw.HatchesWithoutFaces = true;
        var e = Fails(() => Section(Hole));
        Assert.Contains("no planar face", (string)e.Error.Details!["measured"]!);
        Assert.DoesNotContain("delete section line A", Drw.Calls);
        Assert.Empty(Drw.CutLabels(_front.Facts.Name));
    }

    [Fact]
    public void A_section_that_cuts_nothing_is_refused()
    {
        Drw.CutOffByMm = 30;
        var e = Fails(() => Section(Hole));
        Assert.Contains("cut nothing", (string)e.Error.Details!["measured"]!);
    }

    [Fact]
    public void A_section_line_left_behind_by_a_rollback_is_operation_failed_naming_it()
    {
        Drw.CutOffByMm = 0.5;
        Drw.CutDeleteFails = true;
        var e = Fails(() => Section(Hole));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Contains("section line A", e.Message);
    }

    [Fact]
    public void SOLIDWORKS_making_no_section_is_operation_failed()
    {
        Drw.CreateFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Section(Hole)).Code);
    }

    [Fact]
    public void A_section_off_its_parents_centre_line_is_deleted_again()
    {
        Drw.AfterCreate = v => v.Transform = new SwAgent.AddIn.Core.ViewTransform(v.Transform!.Rotation, v.Transform.TxMm + 1, v.Transform.TyMm, 0, v.Transform.Scale);
        var e = Fails(() => Section(Hole));
        Assert.Equal("alignment", e.Error.Details!["check"]);
        Assert.Single(Drw.ViewNames("Sheet1"));
    }
}
