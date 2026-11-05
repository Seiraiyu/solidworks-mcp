using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DimensionOperationTests : DocumentTestBase
{
    static Point2 P(double x, double y) => new Point2(x, y);

    /// <summary>A part with a dimensioned plate sketch and a boss whose depth (D1@Boss-Extrude1) and draft angle (D2) are dimensions.</summary>
    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.MassProperties = new ModelMassResult();
        Run(new SketchCreateOperation(Services), new SketchCreateRequest
        {
            On = "front",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = P(0, 0), Corner2 = P(40, 20) } },
            Dimensions = new List<SketchDimensionItem>
            {
                new SketchDimensionItem { Name = "width", Entities = new List<string> { "outline.bottom" }, Value = 40 },
                new SketchDimensionItem { Name = "height", Entities = new List<string> { "outline.left" }, Value = 20 },
            },
        });
        var boss = doc.AddFeature("Boss-Extrude1", "Extrusion");
        doc.AddFeatureDimension(boss, "D1@Boss-Extrude1", 10, angle: false);
        doc.AddFeatureDimension(boss, "D2@Boss-Extrude1", 3, angle: true);
        doc.AddFeatureDimension(boss, "D3@Boss-Extrude1", 12.5, angle: false, driving: false);
        return doc;
    }

    ModelDimensionsResult List(string? feature = null) => Run(new ModelDimensionsOperation(Services), new ModelDimensionsRequest { Feature = feature });

    DimensionSetResult Set(string dimension, double value) => Run(new DimensionSetOperation(Services), new DimensionSetRequest { Dimension = dimension, Value = value });

    [Fact]
    public void Lists_every_dimension_with_its_value_unit_and_whether_it_drives()
    {
        Part();
        var all = List();
        Assert.Equal(new[] { "width@Sketch1", "height@Sketch1", "D1@Boss-Extrude1", "D2@Boss-Extrude1", "D3@Boss-Extrude1" }, all.Dimensions.Select(d => d.Name));
        var draft = all.Dimensions.Single(d => d.Name == "D2@Boss-Extrude1");
        Assert.Equal("deg", draft.Unit);
        Assert.Equal(3, draft.Value);
        Assert.False(all.Dimensions.Single(d => d.Name == "D3@Boss-Extrude1").Driving);
        Assert.Equal("Sketch1", all.Dimensions[0].Feature);
        Assert.StartsWith("r1.dimension.", all.Dimensions[0].Reference);
    }

    [Fact]
    public void Lists_one_features_dimensions_named_by_name_or_reference()
    {
        Part();
        Assert.Equal(3, List("Boss-Extrude1").Dimensions.Count);
        string sketch = Run(new SketchReadOperation(Services), new SketchReadRequest { Sketch = "Sketch1" }).Sketch.Reference!;
        Assert.Equal(new[] { "width@Sketch1", "height@Sketch1" }, List(sketch).Dimensions.Select(d => d.Name));
        Assert.Equal("feature", Fails(() => List("front")).Error.Details!["field"]);
    }

    [Fact]
    public void Setting_a_dimension_rebuilds_and_reports_it_before_and_after()
    {
        var doc = Part();
        var result = Set("width@Sketch1", 50);
        Assert.Equal(50, result.Dimension.Value);
        var changed = result.Change.Changed.Single();
        Assert.Equal("width@Sketch1", changed.Name);
        Assert.Equal("value", changed.Property);
        Assert.Equal("40 mm", changed.Before);
        Assert.Equal("50 mm", changed.After);
        Assert.Contains(false, doc.Rebuilds);
        Assert.Equal(50, doc.Sketches[0].Dimensions[0].Value);
    }

    [Fact]
    public void A_dimension_is_named_by_its_full_name_in_any_case_or_by_its_reference()
    {
        var doc = Part();
        Assert.Equal("D1@Boss-Extrude1", Set("d1@boss-extrude1", 15).Dimension.Name);
        string reference = List().Dimensions.Single(d => d.Name == "D2@Boss-Extrude1").Reference!;
        var angle = Set(reference, 5);
        Assert.Equal("3 deg", angle.Change.Changed.Single().Before);
        Assert.Equal("5 deg", angle.Change.Changed.Single().After);
    }

    [Theory]
    [InlineData("nope@Sketch1", 10, "dimension")]
    [InlineData("D3@Boss-Extrude1", 10, "dimension")]
    [InlineData("width@Sketch1", 0, "value")]
    [InlineData("width@Sketch1", double.NaN, "value")]
    [InlineData("D2@Boss-Extrude1", 360, "value")]
    public void A_dimension_that_cannot_be_set_so_is_refused_before_anything_changes(string dimension, double value, string field)
    {
        var doc = Part();
        var e = Fails(() => Set(dimension, value));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(doc.Rebuilds);
    }

    [Theory]
    [InlineData("ignored", ErrorCode.VerificationFailed)]
    [InlineData("rebuild_fails", ErrorCode.OperationFailed)]
    [InlineData("rebuild_error", ErrorCode.VerificationFailed)]
    public void A_value_that_does_not_take_is_put_back(string fault, ErrorCode code)
    {
        var doc = Part();
        if (fault == "ignored") doc.DimensionsIgnoreSet = true;
        if (fault == "rebuild_fails") doc.RebuildSucceeds = false;
        if (fault == "rebuild_error") doc.ProblemsAfterRebuild.Add(new RebuildItem { Feature = "Boss-Extrude1", Code = "swFeatureErrorExtrusionDepthZero", Warning = false });
        var e = Fails(() => Set("D1@Boss-Extrude1", 0.001));
        Assert.Equal(code, e.Code);
        Assert.Equal(new List<object?> { "D1@Boss-Extrude1" }, e.Error.Details!["rolled_back"]);
        Assert.Equal(10, doc.FeatureDimensions.Single(d => d.FullName == "D1@Boss-Extrude1").Value);
    }
}
