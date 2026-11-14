using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>The block's front and top views; its sketch's width and height and its boss's depth are the model's dimensions.</summary>
public class AnnotationImportTests : DrawingTestBase
{
    readonly FakeView _front;
    readonly FakeView _top;

    public AnnotationImportTests()
    {
        _front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        _top = Drw.AddView("Sheet1", Block, "top", new SheetPoint(100, 60));
        var sketch = Block.AddFeature("Sketch1", "ProfileFeature");
        Block.AddFeatureDimension(sketch, "D1@Sketch1", 60, angle: false);
        Block.AddFeatureDimension(sketch, "D2@Sketch1", 40, angle: false);
        var boss = Block.AddFeature("Boss-Extrude1", "Extrusion");
        Block.AddFeatureDimension(boss, "D1@Boss-Extrude1", 20, angle: false);
        Drw.ImportPlan.Add((_front.Facts.Name, AnnotationKind.Dimension, "D1@Sketch1"));
        Drw.ImportPlan.Add((_front.Facts.Name, AnnotationKind.Dimension, "D2@Sketch1"));
        Drw.ImportPlan.Add((_top.Facts.Name, AnnotationKind.Dimension, "D1@Boss-Extrude1"));
    }

    AnnotationImportResult Import(List<string>? views = null, List<AnnotationImportType>? types = null) =>
        Run(new AnnotationImportOperation(Services), new AnnotationImportRequest { Views = views, Types = types });

    [Fact]
    public void Every_views_model_dimensions_come_in_each_equal_to_the_models()
    {
        var r = Import();
        Assert.Equal(new[] { "D1@Sketch1", "D2@Sketch1", "D1@Boss-Extrude1" }, r.Dimensions.Select(d => d.Dimension));
        Assert.All(r.Dimensions, d => Assert.Equal(d.ModelValue, d.Value));
        Assert.All(r.Annotations, a => Assert.True(a.ModelDimension));
        Assert.Equal(_top.Facts.Name, r.Dimensions.Single(d => d.Dimension == "D1@Boss-Extrude1").View);
        Assert.Equal((0, 3), (r.Change.AnnotationsBefore, r.Change.AnnotationsAfter));
        Assert.All(r.Change.Created, c => Assert.Equal(ReferenceKind.Annotation, c.Kind));
        Assert.Equal(0, r.Removed);
        Assert.Empty(r.Duplicates);
        Assert.Contains("import dimensions", Drw.Calls);
    }

    [Fact]
    public void Only_the_views_asked_keep_what_was_brought()
    {
        var r = Import(new List<string> { _front.Facts.Name });
        Assert.Equal(2, r.Dimensions.Count);
        Assert.Equal(1, r.Removed);
        Assert.Empty(Drw.Annotations("Sheet1", _top.Facts.Name));
        Assert.Equal(2, r.Change.AnnotationsAfter);
    }

    [Fact]
    public void A_dimension_shown_in_two_views_is_reported_not_removed()
    {
        Drw.ImportPlan.Add((_front.Facts.Name, AnnotationKind.Dimension, "D1@Boss-Extrude1"));
        var r = Import();
        Assert.Equal(4, r.Dimensions.Count);
        Assert.Equal($"D1@Boss-Extrude1 in {_front.Facts.Name}, {_top.Facts.Name}", Assert.Single(r.Duplicates));
    }

    [Fact]
    public void A_value_that_is_not_the_models_is_refused_and_everything_brought_is_deleted()
    {
        Drw.ImportedOffBy = 0.01;
        var e = Fails(() => Import());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("value", e.Error.Details!["check"]);
        Assert.Empty(Drw.Annotated);
    }

    [Fact]
    public void Datums_come_in_when_asked_and_are_no_dimensions()
    {
        Drw.ImportPlan.Add((_front.Facts.Name, AnnotationKind.Datum, "A"));
        var r = Import(types: new List<AnnotationImportType> { AnnotationImportType.Datums });
        Assert.Equal("A", Assert.Single(r.Annotations).Label);
        Assert.Empty(r.Dimensions);
        Assert.Contains("import datums", Drw.Calls);
    }

    [Fact]
    public void A_drawing_without_views_and_a_view_named_twice_are_refused()
    {
        Assert.Equal("/views/1", Fails(() => Import(new List<string> { _front.Facts.Name, _front.Facts.Name })).Error.Details!["field"]);
        Drw.DeleteViews(new[] { _front.Facts.Name, _top.Facts.Name });
        Assert.Equal("/document", Fails(() => Import()).Error.Details!["field"]);
    }

    [Fact]
    public void What_SOLIDWORKS_will_not_take_out_of_other_views_is_refused_and_said_to_be_left()
    {
        Drw.AnnotationDeleteLeaves = true;
        var e = Fails(() => Import(new List<string> { _front.Facts.Name }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
    }
}
