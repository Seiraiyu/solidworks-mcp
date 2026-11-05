using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SketchCreateTests : DocumentTestBase
{
    static Point2 P(double x, double y) => new Point2(x, y);

    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.MassProperties = new ModelMassResult();
        return doc;
    }

    /// <summary>A 40 x 20 plate outline with a 5 mm hole in its middle, tied to the origin, with named dimensions.</summary>
    static SketchCreateRequest Plate(string on = "front") => new SketchCreateRequest
    {
        On = on,
        Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = P(0, 0), Corner2 = P(40, 20) } },
        Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = P(20, 10), RadiusMm = 2.5 } },
        Lines = new List<SketchLine> { new SketchLine { Id = "axis", Start = P(0, 10), End = P(40, 10), Construction = true } },
        Relations = new List<SketchRelationItem>
        {
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = new List<string> { "outline.left", "origin" } },
            new SketchRelationItem { Type = SketchRelationType.Midpoint, Entities = new List<string> { "hole.center", "axis" } },
        },
        Dimensions = new List<SketchDimensionItem>
        {
            new SketchDimensionItem { Name = "width", Entities = new List<string> { "outline.bottom" }, Value = 40 },
            new SketchDimensionItem { Name = "hole_d", Entities = new List<string> { "hole" }, Value = 5 },
        },
    };

    SketchCreateResult Create(SketchCreateRequest request) => Run(new SketchCreateOperation(Services), request);

    /// <summary>SOLIDWORKS relates two points at one place by merging them (swConstraintType_MERGEPOINTS, 3.B live run): sketch.read calls that coincident.</summary>
    [Theory]
    [InlineData(42, "coincident")]
    [InlineData(9, "coincident")]
    [InlineData(4, "horizontal")]
    [InlineData(25, "horizontal")]
    public void Relation_types_read_as_the_names_sketch_create_takes(int constraint, string name) =>
        Assert.Equal(name, SolidWorksCodes.RelationName(constraint));

    [Fact]
    public void Draws_the_whole_sketch_on_the_front_plane_and_closes_it()
    {
        var doc = Part();
        var result = Create(Plate());

        var sketch = doc.Sketches.Single();
        Assert.Equal("Ebene vorne", ((FakeEntity)sketch.On).Info.Name);
        Assert.True(doc.Editors.Single().Closed);
        Assert.Equal("Sketch1", result.Sketch.Name);
        Assert.StartsWith("r1.sketch.", result.Sketch.Reference);
        Assert.Equal(SolveState.UnderDefined, result.Sketch.Solve);
        Assert.Equal("Sketch1", result.Change.Created.Single().Name);
        Assert.Equal(result.Sketch.Reference, result.Change.Created.Single().Reference);

        Assert.Equal(new[] { "outline", "hole", "axis" }.OrderBy(i => i), result.Ids.Select(i => i.Id).OrderBy(i => i));
        Assert.Equal(4, result.Ids.Single(i => i.Id == "outline").References.Count);
        Assert.All(result.Ids.SelectMany(i => i.References), r => Assert.StartsWith("r1.sketch_entity.", r));

        Assert.Equal(new[] { "width@Sketch1", "hole_d@Sketch1" }, result.Dimensions.Select(d => d.Name));
        Assert.Equal(new[] { 40.0, 5.0 }, result.Dimensions.Select(d => d.Value));
        Assert.All(result.Dimensions, d => Assert.Equal("mm", d.Unit));
        Assert.Equal(DimensionKind.Diameter, sketch.Dimensions[1].Kind);

        Assert.Equal(SketchRelationType.Coincident, sketch.Relations[0].Type);
        Assert.Same(SketchOrigin.Instance, sketch.Relations[0].Entities[1]);
        Assert.Equal(SketchRelationType.Midpoint, sketch.Relations[1].Type);
        Assert.Equal("Arc1.center", ((FakeEntity)sketch.Relations[1].Entities[0]).Info.Name);
    }

    [Fact]
    public void A_rectangles_sides_are_named_by_where_they_are()
    {
        var doc = Part();
        var request = Plate();
        request.Relations = null;
        request.Dimensions = null;
        var result = Create(request);
        var sides = result.Ids.Single(i => i.Id == "outline").References
            .Select(r => doc.Sketches[0].Segments.Single(s => ReferenceEquals(s.Entity, doc.Resolve(SwAgent.Contracts.References.Decode(r, "r").PersistId, out _)))).ToList();
        Assert.Equal(0, sides[0].Start!.Y + sides[0].End!.Y);
        Assert.Equal(80, sides[1].Start!.X + sides[1].End!.X);
        Assert.Equal(40, sides[2].Start!.Y + sides[2].End!.Y);
        Assert.Equal(0, sides[3].Start!.X + sides[3].End!.X);
    }

    [Fact]
    public void A_polygons_sides_run_counterclockwise_from_its_vertex_and_a_centered_rectangle_is_drawn_round_its_centre()
    {
        var doc = Part();
        var result = Create(new SketchCreateRequest
        {
            On = "top",
            Polygons = new List<SketchPolygon> { new SketchPolygon { Id = "hex", Center = P(0, 0), Vertex = P(10, 0), Sides = 6 } },
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "box", Corner1 = P(50, 50), Corner2 = P(60, 55), Centered = true } },
        });
        Assert.Equal("Ebene oben", ((FakeEntity)doc.Sketches[0].On).Info.Name);
        var segments = doc.Sketches[0].Segments;
        SketchSegmentRecord Of(string reference) => segments.Single(s => ReferenceEquals(s.Entity, doc.Resolve(SwAgent.Contracts.References.Decode(reference, "r").PersistId, out _)));
        var side1 = Of(result.Ids.Single(i => i.Id == "hex").References[0]);
        Assert.Equal(10, side1.Start!.X, 6);
        Assert.True(side1.End!.Y > 0);
        var bottom = Of(result.Ids.Single(i => i.Id == "box").References[0]);
        Assert.Equal(45, bottom.Start!.Y, 6);
        Assert.Equal(100, bottom.Start.X + bottom.End!.X, 6);
    }

    [Fact]
    public void A_sketch_can_be_named_but_not_after_a_feature_already_in_the_part()
    {
        var doc = Part();
        var request = Plate();
        request.Name = "profile";
        Assert.Equal("profile", Create(request).Sketch.Name);
        Assert.Equal("width@profile", doc.Sketches[0].Dimensions[0].FullName);

        var again = Plate();
        again.Name = "profile";
        var e = Fails(() => Create(again));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("name", e.Error.Details!["field"]);
        Assert.Single(doc.Sketches);
    }

    [Fact]
    public void A_sketch_goes_on_a_planar_face_but_not_on_a_round_one_or_an_edge()
    {
        var doc = Part();
        var services = Services;
        var top = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, 1) });
        var round = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder });
        var edge = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line });
        Create(Plate(services.References.Describe(doc, top).Reference!));
        Assert.Same(top, doc.Sketches.Single().On);

        Assert.Equal("on", Fails(() => Create(Plate(services.References.Describe(doc, round).Reference!))).Error.Details!["field"]);
        Assert.Equal("on", Fails(() => Create(Plate(services.References.Describe(doc, edge).Reference!))).Error.Details!["field"]);
        Assert.Single(doc.Sketches);
    }

    [Fact]
    public void A_bad_request_or_reference_opens_no_sketch()
    {
        var doc = Part();
        var bad = Plate();
        bad.Circles![0].RadiusMm = -1;
        Assert.Equal("/circles/0/radius_mm", Fails(() => Create(bad)).Error.Details!["field"]);

        var corner = doc.Add(ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(0, 0, 0) });
        string reference = Services.References.Describe(doc, corner).Reference!;
        corner.State = PersistState.Deleted;
        var tied = Plate();
        tied.Relations!.Add(new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = new List<string> { "hole.center", reference } });
        var e = Fails(() => Create(tied));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("/relations/2/entities/1", e.Error.Details!["field"]);
        Assert.Empty(doc.Sketches);
    }

    [Fact]
    public void A_relation_to_the_parts_own_vertex_is_passed_as_that_vertex()
    {
        var doc = Part();
        var corner = doc.Add(ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(40, 20, 0) });
        var request = Plate();
        request.Relations!.Add(new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = new List<string> { "outline.top", Services.References.Describe(doc, corner).Reference! } });
        Create(request);
        Assert.Same(corner, doc.Sketches[0].Relations[2].Entities[1]);
    }

    /// <summary>SOLIDWORKS' IDimension.FullName ends with the document (width@plate@sketches.Part, 3.B live run); agents name a dimension without it.</summary>
    [Theory]
    [InlineData("width@plate@sketches.Part", "width@plate")]
    [InlineData("D1@Boss-Extrude1@dimensions.Part", "D1@Boss-Extrude1")]
    [InlineData("D1@Sketch1", "D1@Sketch1")]
    public void A_dimension_is_named_without_its_document(string fullName, string name) =>
        Assert.Equal(name, SolidWorksCodes.DimensionName(fullName));

    [Fact]
    public void A_relation_refused_with_a_reason_names_its_field_and_the_reason()
    {
        var doc = Part();
        doc.SketchRefusesSaying = SketchRelationType.Midpoint;
        var e = Fails(() => Create(Plate()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("/relations/1", e.Error.Details!["field"]);
        Assert.Contains("SOLIDWORKS selected 1 of 2", e.Message);
        Assert.StartsWith("/relations/1: SOLIDWORKS did not add the midpoint relation", e.Message);
        Assert.Empty(doc.Sketches);
    }

    [Fact]
    public void A_relation_SOLIDWORKS_refuses_rolls_the_sketch_back()
    {
        var doc = Part();
        doc.SketchRefuses = SketchRelationType.Midpoint;
        var e = Fails(() => Create(Plate()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("/relations/1", e.Error.Details!["field"]);
        Assert.Equal(new List<object?> { "Sketch1" }, e.Error.Details["rolled_back"]);
        Assert.Empty(doc.Sketches);
        Assert.True(doc.Editors.Single().Closed);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Sketch1");
    }

    [Theory]
    [InlineData("value")]
    [InlineData("driven")]
    [InlineData("over_defined")]
    [InlineData("counts")]
    [InlineData("refused")]
    public void A_sketch_that_is_not_what_was_asked_is_rolled_back(string fault)
    {
        var doc = Part();
        if (fault == "value") doc.SketchDimensionError = 0.01;
        if (fault == "driven") doc.SketchDimensionsDriven = true;
        if (fault == "over_defined") doc.SketchOverDefines = true;
        if (fault == "counts") doc.SketchLosesCircles = true;
        if (fault == "refused") doc.SketchRefusesDimensions = true;
        var e = Fails(() => Create(Plate()));
        Assert.Equal(fault == "refused" ? ErrorCode.OperationFailed : ErrorCode.VerificationFailed, e.Code);
        Assert.Empty(doc.Sketches);
        Assert.NotNull(e.Error.Details!["rolled_back"]);
    }

    [Fact]
    public void A_rollback_that_fails_leaves_the_sketch_and_says_so()
    {
        var doc = Part();
        doc.SketchOverDefines = true;
        doc.DeleteFails = true;
        var e = Fails(() => Create(Plate()));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Single(doc.Sketches);
        Assert.True(doc.Editors.Single().Closed);
    }

    [Fact]
    public void Nothing_is_drawn_while_the_user_edits_a_sketch()
    {
        var doc = Part();
        doc.EditingSketchName = "Sketch7";
        Assert.Equal(ErrorCode.Busy, Fails(() => Create(Plate())).Code);
        Assert.Empty(doc.Sketches);
    }
}

public class SketchReadTests : DocumentTestBase
{
    static Point2 P(double x, double y) => new Point2(x, y);

    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.MassProperties = new ModelMassResult();
        return doc;
    }

    SketchCreateResult Plate() => Run(new SketchCreateOperation(Services), new SketchCreateRequest
    {
        On = "front",
        Name = "profile",
        Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = P(0, 0), Corner2 = P(40, 20) } },
        Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = P(20, 10), RadiusMm = 2.5 } },
        CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "axis", Start = P(0, 10), End = P(40, 10) } },
        Relations = new List<SketchRelationItem>
        {
            new SketchRelationItem { Type = SketchRelationType.Equal, Entities = new List<string> { "outline.bottom", "outline.top" } },
            new SketchRelationItem { Type = SketchRelationType.Midpoint, Entities = new List<string> { "hole.center", "axis" } },
        },
        Dimensions = new List<SketchDimensionItem> { new SketchDimensionItem { Name = "width", Entities = new List<string> { "outline.bottom" }, Value = 40 } },
    });

    SketchReadResult Read(string sketch) => Run(new SketchReadOperation(Services), new SketchReadRequest { Sketch = sketch });

    [Fact]
    public void Reads_back_every_segment_relation_and_dimension_with_references()
    {
        Part();
        var made = Plate();
        var read = Read("profile");
        Assert.Equal("profile", read.Sketch.Name);
        Assert.Equal(made.Sketch.Reference, read.Sketch.Reference);
        Assert.Equal(4, read.Segments.Count(s => s.Kind == SketchSegmentKind.Line && !s.Construction));
        Assert.Single(read.Segments, s => s.Kind == SketchSegmentKind.Line && s.Construction);
        var hole = Assert.Single(read.Segments, s => s.Kind == SketchSegmentKind.Circle);
        Assert.Equal(2.5, hole.RadiusMm);
        Assert.Equal(20, hole.Center!.X);
        Assert.All(read.Segments, s => Assert.StartsWith("r1.sketch_entity.", s.Reference));
        Assert.Contains(made.Ids.Single(i => i.Id == "hole").References[0], read.Segments.Select(s => s.Reference));

        Assert.Equal(new[] { "equal", "midpoint" }, read.Relations.Select(r => r.Type));
        Assert.Equal(made.Ids.Single(i => i.Id == "outline").References[0], read.Relations[0].Entities[0]);
        Assert.Equal("width@profile", read.Dimensions.Single().Name);
        Assert.Equal(40, read.Dimensions.Single().Value);
        Assert.True(read.Dimensions.Single().Driving);
    }

    [Fact]
    public void A_sketch_is_named_by_its_name_or_its_reference_and_nothing_else()
    {
        var doc = Part();
        var made = Plate();
        Assert.Equal("profile", Read(made.Sketch.Reference!).Sketch.Name);
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Read("front")).Code);
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => Read("Sketch99")).Code);
    }
}

public class SketchModifyTests : DocumentTestBase
{
    static Point2 P(double x, double y) => new Point2(x, y);

    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.MassProperties = new ModelMassResult();
        return doc;
    }

    SketchCreateResult Plate() => Run(new SketchCreateOperation(Services), new SketchCreateRequest
    {
        On = "front",
        Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = P(0, 0), Corner2 = P(40, 20) } },
        Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = P(20, 10), RadiusMm = 2.5 } },
        Dimensions = new List<SketchDimensionItem> { new SketchDimensionItem { Name = "width", Entities = new List<string> { "outline.bottom" }, Value = 40 } },
    });

    SketchCreateResult Modify(SketchModifyRequest request) => Run(new SketchModifyOperation(Services), request);

    static SketchGeometry Boss(string holeReference) => new SketchGeometry
    {
        Circles = new List<SketchCircle> { new SketchCircle { Id = "boss", Center = P(20, 10), RadiusMm = 6 } },
        Relations = new List<SketchRelationItem> { new SketchRelationItem { Type = SketchRelationType.Concentric, Entities = new List<string> { "boss", holeReference } } },
        Dimensions = new List<SketchDimensionItem> { new SketchDimensionItem { Name = "boss_d", Entities = new List<string> { "boss" }, Value = 12 } },
    };

    [Fact]
    public void Adds_to_an_existing_sketch_relating_new_entities_to_old_ones()
    {
        var doc = Part();
        var plate = Plate();
        string hole = plate.Ids.Single(i => i.Id == "hole").References[0];
        var result = Modify(new SketchModifyRequest { Sketch = "Sketch1", Add = Boss(hole) });

        var sketch = doc.Sketches.Single();
        Assert.Equal(2, sketch.Segments.Count(s => s.Kind == SketchSegmentKind.Circle));
        Assert.Equal("boss", result.Ids.Single().Id);
        Assert.Equal(new[] { "boss_d@Sketch1" }, result.Dimensions.Select(d => d.Name));
        Assert.Equal(SketchRelationType.Concentric, sketch.Relations.Single().Type);
        Assert.Equal(hole, Services.References.Describe(doc, sketch.Relations.Single().Entities[1]).Reference);
        Assert.Empty(result.Change.Created);
        var changed = result.Change.Changed.Single();
        Assert.Equal("Sketch1", changed.Name);
        Assert.Equal("5 segments, 1 dimensions", changed.Before);
        Assert.Equal("6 segments, 2 dimensions", changed.After);
        Assert.True(doc.Editors.All(e => e.Closed));
    }

    [Fact]
    public void Removes_entities_named_by_reference_after_the_additions_are_checked()
    {
        var doc = Part();
        var plate = Plate();
        string hole = plate.Ids.Single(i => i.Id == "hole").References[0];
        var result = Modify(new SketchModifyRequest { Sketch = plate.Sketch.Reference!, Remove = new List<string> { hole } });
        Assert.DoesNotContain(doc.Sketches[0].Segments, s => s.Kind == SketchSegmentKind.Circle);
        Assert.Equal("4 segments, 1 dimensions", result.Change.Changed.First().After);
        Assert.Equal("removed", result.Change.Changed.Last().Property);
    }

    [Fact]
    public void Only_the_sketchs_own_entities_can_be_removed()
    {
        var doc = Part();
        Plate();
        var other = Run(new SketchCreateOperation(Services), new SketchCreateRequest { On = "top", Circles = new List<SketchCircle> { new SketchCircle { Id = "c", Center = P(0, 0), RadiusMm = 1 } } });
        var e = Fails(() => Modify(new SketchModifyRequest { Sketch = "Sketch1", Remove = new List<string> { other.Ids[0].References[0] } }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/remove/0", e.Error.Details!["field"]);
        Assert.Single(doc.Sketches[1].Segments);
    }

    [Fact]
    public void Additions_that_over_define_the_sketch_are_taken_back_and_nothing_is_removed()
    {
        var doc = Part();
        var plate = Plate();
        string hole = plate.Ids.Single(i => i.Id == "hole").References[0];
        doc.SketchOverDefines = true;
        var e = Fails(() => Modify(new SketchModifyRequest { Sketch = "Sketch1", Add = Boss(hole), Remove = new List<string> { plate.Ids[0].References[0] } }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        var sketch = doc.Sketches.Single();
        Assert.Equal(5, sketch.Segments.Count);
        Assert.Empty(sketch.Relations);
        Assert.Equal(new[] { "width@Sketch1" }, sketch.Dimensions.Select(d => d.FullName));
        Assert.True(doc.Editors.All(ed => ed.Closed));
    }

    [Fact]
    public void A_modify_with_nothing_to_do_or_on_something_that_is_not_a_sketch_is_refused()
    {
        Part();
        Plate();
        Assert.Equal("", Fails(() => Modify(new SketchModifyRequest { Sketch = "Sketch1" })).Error.Details!["field"]);
        Assert.Equal("sketch", Fails(() => Modify(new SketchModifyRequest { Sketch = "front", Remove = new List<string>() })).Error.Details!["field"]);
    }
}