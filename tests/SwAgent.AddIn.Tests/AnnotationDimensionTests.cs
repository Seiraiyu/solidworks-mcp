using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AnnotationDimensionTests : AnnotationTestBase
{
    AnnotationDimensionResult Dimension(DimensionKind kind, params ViewEntity[] entities) =>
        Run(new AnnotationDimensionOperation(Services), new AnnotationDimensionRequest { Kind = kind, Entities = entities.ToList(), At = new SheetPoint(100, 190) });

    [Fact]
    public void A_horizontal_dimension_between_the_sides_reads_60_and_is_the_drawings_driven_one()
    {
        var r = Dimension(DimensionKind.Horizontal, On(Left), On(Right));
        Assert.Equal(60, r.Value, 9);
        Assert.Equal(60, r.ModelValue, 9);
        Assert.Equal("the horizontal distance between the line and the line", r.Measured);
        Assert.Equal(AnnotationKind.Dimension, r.Annotation.Kind);
        Assert.False(r.Annotation.ModelDimension);
        Assert.Equal(Front.Facts.Name, r.Annotation.View);
        Assert.Equal(2, r.Annotation.Attached.Count);
        Assert.Equal(ReferenceKind.Annotation, Assert.Single(r.Change.Created).Kind);
        Assert.Contains($"dimension horizontal {Front.Facts.Name}", Drw.Calls);
    }

    [Fact]
    public void The_holes_diameter_and_radius_and_the_right_angle_at_the_corner()
    {
        Assert.Equal(10, Dimension(DimensionKind.Diameter, On(Hole)).Value, 9);
        Assert.Equal(5, Dimension(DimensionKind.Radius, On(Hole)).Value, 9);
        Assert.Equal(90, Dimension(DimensionKind.Angle, On(Left), On(Bottom)).Value, 9);
        Assert.Equal(40, Dimension(DimensionKind.Horizontal, On(Hole), On(Corner)).Value, 9);
    }

    [Fact]
    public void A_view_at_1_to_2_still_reads_model_mm()
    {
        var small = Drw.AddView("Sheet1", Block, "front", new SheetPoint(300, 150), new ScaleRatio(1, 2));
        small.Visible.Add(new VisibleEntity(Left, false, false));
        small.Visible.Add(new VisibleEntity(Right, false, false));
        Assert.Equal(60, Dimension(DimensionKind.Linear, On(Left, small.Facts.Name), On(Right, small.Facts.Name)).Value, 9);
    }

    [Fact]
    public void A_value_off_the_models_by_two_microns_is_refused_and_the_dimension_deleted()
    {
        Drw.DimensionOffBy = 0.002;
        var e = Fails(() => Dimension(DimensionKind.Horizontal, On(Left), On(Right)));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("value", e.Error.Details!["check"]);
        Assert.Empty(Drw.Annotated);
    }

    [Fact]
    public void A_driving_dimension_is_refused()
    {
        Drw.DimensionsDrive = true;
        Assert.Equal("driven", Fails(() => Dimension(DimensionKind.Diameter, On(Hole))).Error.Details!["check"]);
    }

    [Fact]
    public void An_edge_the_view_does_not_show_is_refused_naming_the_view_and_the_edge()
    {
        var e = Fails(() => Dimension(DimensionKind.Linear, On(Back)));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/entities/0", e.Error.Details!["field"]);
        Assert.Equal(Front.Facts.Name, e.Error.Details["view"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void Entities_that_make_no_such_dimension_are_refused_before_SOLIDWORKS_is_asked()
    {
        Assert.Equal("/entities", Fails(() => Dimension(DimensionKind.Horizontal, On(Hole))).Error.Details!["field"]);
        Assert.Equal("/entities", Fails(() => Dimension(DimensionKind.Angle, On(Left), On(Right))).Error.Details!["field"]);
        Assert.Equal("/entities", Fails(() => Dimension(DimensionKind.Linear, On(Left), On(Right), On(Bottom))).Error.Details!["field"]);
        var other = Drw.AddView("Sheet1", Block, "front", new SheetPoint(300, 150));
        other.Visible.Add(new VisibleEntity(Right, false, false));
        Assert.Equal("/entities/1/view", Fails(() => Dimension(DimensionKind.Linear, On(Left), On(Right, other.Facts.Name))).Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void Text_off_the_sheet_is_refused_and_a_dimension_SOLIDWORKS_does_not_make_fails()
    {
        var e = Fails(() => Run(new AnnotationDimensionOperation(Services), new AnnotationDimensionRequest { Kind = DimensionKind.Diameter, Entities = new List<ViewEntity> { On(Hole) }, At = new SheetPoint(500, 10) }));
        Assert.Equal("/at", e.Error.Details!["field"]);
        Drw.AnnotateFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Dimension(DimensionKind.Diameter, On(Hole))).Code);
    }

    [Fact]
    public void A_dimension_that_marks_the_model_as_changed_is_refused_and_deleted()
    {
        Sw.Track(Block);
        Drw.AnnotatingDirtiesModel = true;
        var e = Fails(() => Dimension(DimensionKind.Diameter, On(Hole)));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("models", e.Error.Details!["check"]);
        Assert.Empty(Drw.Annotated);
    }
}
