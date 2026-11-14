using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>The front view as the base sets it, the hole's back circle shown too; and a top view that sees the hole's two half-cylinders from the side.</summary>
public class AnnotationCentreTests : AnnotationTestBase
{
    readonly FakeView _top;
    readonly FakeEntity _half1;
    readonly FakeEntity _half2;

    public AnnotationCentreTests()
    {
        var back = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 10 * System.Math.PI });
        Front.Visible.Add(new VisibleEntity(back, false, false));
        _top = Drw.AddView("Sheet1", Block, "top", new SheetPoint(100, 60));
        _half1 = Block.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(20, 20, 0), Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        _half2 = Block.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(20, 20, 20), Axis = new Vector3(0, 0, -1), RadiusMm = 5 });
        _top.Faces.Add(_half1);
        _top.Faces.Add(_half2);
        Front.Faces.Add(_half1);
    }

    AnnotationCentresResult Marks(params ViewEntity[] entities) =>
        Run(new AnnotationCentreMarksOperation(Services), new AnnotationCentreMarksRequest { View = Front.Facts.Name, Entities = entities.Length == 0 ? null : entities.ToList() });

    AnnotationCentresResult Lines(string view, params ViewEntity[] entities) =>
        Run(new AnnotationCentreLinesOperation(Services), new AnnotationCentreLinesRequest { View = view, Entities = entities.Length == 0 ? null : entities.ToList() });

    [Fact]
    public void The_hole_gets_one_centre_mark_at_its_drawn_centre_for_both_its_edges()
    {
        var r = Marks();
        Assert.Equal((1, 0), (r.Eligible, r.Already));
        var mark = Assert.Single(r.Made);
        Assert.Equal(AnnotationKind.CentreMark, mark.Kind);
        Assert.Equal(90, mark.Position!.X, 9);
        Assert.Equal(150, mark.Position.Y, 9);
        Assert.Equal(ReferenceKind.Annotation, Assert.Single(r.Change.Created).Kind);
    }

    [Fact]
    public void A_circle_already_marked_is_skipped()
    {
        Marks();
        var again = Marks(On(Hole));
        Assert.Equal((1, 1), (again.Eligible, again.Already));
        Assert.Empty(again.Made);
        Assert.Empty(again.Change.Created);
    }

    [Fact]
    public void A_straight_edge_takes_no_centre_mark_and_an_edge_of_another_view_is_refused()
    {
        Assert.Equal("/entities/0", Fails(() => Marks(On(Left))).Error.Details!["field"]);
        _top.Visible.Add(new VisibleEntity(Left, false, false));
        Assert.Equal("/entities/0/view", Fails(() => Marks(On(Left, _top.Facts.Name))).Error.Details!["field"]);
        Assert.Empty(Drw.Annotated);
    }

    [Fact]
    public void A_mark_SOLIDWORKS_put_off_the_centre_is_refused_and_deleted()
    {
        Drw.AnnotationLandsOffByMm = 0.02;
        Drw.PositionNote = "IAnnotation.GetPosition (117.226, 175.84); ICenterMark.GetPosition(0) (120.02, 180)";
        var e = Fails(() => Marks());
        Assert.Equal("position", e.Error.Details!["check"]);
        Assert.Contains("ICenterMark.GetPosition(0) (120.02, 180)", e.Message);
        Assert.Empty(Drw.Annotated);
    }

    [Fact]
    public void The_holes_two_halves_seen_from_the_side_get_one_centre_line()
    {
        var r = Lines(_top.Facts.Name);
        Assert.Equal((1, 0), (r.Eligible, r.Already));
        var line = Assert.Single(r.Made);
        Assert.Equal(AnnotationKind.CentreLine, line.Kind);
        Assert.Equal(Services.References.Describe(Block, _half1).Reference, Assert.Single(line.Attached).Reference);
        var again = Lines(_top.Facts.Name, On(_half2, _top.Facts.Name));
        Assert.Equal((1, 1), (again.Eligible, again.Already));
        Assert.Empty(again.Made);
    }

    [Fact]
    public void A_centre_line_between_two_parallel_edges_is_attached_to_both()
    {
        var r = Lines(Front.Facts.Name, On(Left), On(Right));
        Assert.Equal(2, Assert.Single(r.Made).Attached.Count);
    }

    [Fact]
    public void A_cylinder_seen_end_on_edges_not_parallel_and_an_unattached_line_are_refused()
    {
        Assert.Equal("/entities/0", Fails(() => Lines(Front.Facts.Name, On(_half1))).Error.Details!["field"]);
        Assert.Equal("/entities", Fails(() => Lines(Front.Facts.Name, On(Left), On(Bottom))).Error.Details!["field"]);
        Drw.AttachIgnored = true;
        var e = Fails(() => Lines(_top.Facts.Name));
        Assert.Equal("attached", e.Error.Details!["check"]);
        Assert.Empty(Drw.Annotated);
    }
}
