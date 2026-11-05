using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class SketchDraftTests
{
    static Point2 P(double x, double y) => new Point2(x, y);

    /// <summary>A 40 x 20 rectangle at the origin with a 5 mm hole, a construction line, named dimensions and relations.</summary>
    static SketchGeometry Plate() => new SketchGeometry
    {
        Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "outline", Corner1 = P(0, 0), Corner2 = P(40, 20) } },
        Circles = new List<SketchCircle> { new SketchCircle { Id = "hole", Center = P(20, 10), RadiusMm = 2.5 } },
        Lines = new List<SketchLine> { new SketchLine { Id = "axis", Start = P(0, 10), End = P(40, 10), Construction = true } },
        Relations = new List<SketchRelationItem>
        {
            new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = new List<string> { "outline.bottom", "origin" } },
            new SketchRelationItem { Type = SketchRelationType.Midpoint, Entities = new List<string> { "hole.center", "axis" } },
        },
        Dimensions = new List<SketchDimensionItem>
        {
            new SketchDimensionItem { Name = "width", Entities = new List<string> { "outline.bottom" }, Value = 40 },
            new SketchDimensionItem { Name = "hole_d", Entities = new List<string> { "hole" }, Value = 5 },
        },
    };

    static SwAgentException Refused(SketchGeometry g, string prefix = "") => Assert.Throws<SwAgentException>(() => SketchDraft.Check(g, prefix));

    [Fact]
    public void A_valid_sketch_lists_its_entities_in_drawing_order_with_their_fields()
    {
        var draft = SketchDraft.Check(Plate(), "");
        Assert.Equal(new[] { "axis", "hole", "outline" }, draft.Entities.Select(e => e.Id).OrderBy(i => i, StringComparer.Ordinal));
        Assert.Equal("/rectangles/0", draft.Entities.Single(e => e.Id == "outline").Field);
        Assert.Equal(SketchItemKind.Rectangle, draft.Entities.Single(e => e.Id == "outline").Kind);
        Assert.Equal(2, draft.Relations.Count);
        Assert.Equal(DimensionKind.Diameter, draft.Dimensions.Single(d => d.Name == "hole_d").Kind);
        Assert.Equal(DimensionKind.Linear, draft.Dimensions.Single(d => d.Name == "width").Kind);
    }

    [Fact]
    public void Targets_are_ids_with_a_part_references_or_the_origin()
    {
        var draft = SketchDraft.Check(Plate(), "");
        var coincident = draft.Relations[0].Targets;
        Assert.Equal("outline", coincident[0].Id);
        Assert.Equal("bottom", coincident[0].Part);
        Assert.True(coincident[1].IsOrigin);
        var midpoint = draft.Relations[1].Targets;
        Assert.Equal("center", midpoint[0].Part);
        Assert.Null(midpoint[1].Part);
        Assert.Equal("/relations/1/entities/1", midpoint[1].Field);

        var g = Plate();
        g.Relations!.Add(new SketchRelationItem { Type = SketchRelationType.Coincident, Entities = new List<string> { "hole.center", "r1.vertex.AQID" } });
        var withReference = SketchDraft.Check(g, "").Relations[2].Targets[1];
        Assert.True(withReference.IsReference);
        Assert.Equal("r1.vertex.AQID", withReference.Text);
    }

    [Theory]
    [InlineData("", "/lines/0/id")]
    [InlineData("2nd", "/lines/0/id")]
    [InlineData("r1", "/lines/0/id")]
    [InlineData("origin", "/lines/0/id")]
    [InlineData("hole", "/circles/0/id")]
    public void An_id_must_be_a_short_name_unique_in_the_request(string id, string field)
    {
        var g = Plate();
        g.Lines![0].Id = id;
        var e = Refused(g);
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }

    [Fact]
    public void Fields_are_json_pointers_under_the_prefix_a_modify_gives()
    {
        var g = Plate();
        g.Lines![0].End = P(0, 10);
        Assert.Equal("/add/lines/0/end", Refused(g, "/add").Error.Details!["field"]);
    }

    public static IEnumerable<object[]> BadGeometry()
    {
        yield return new object[] { (Action<SketchGeometry>)(g => g.Lines![0].End = g.Lines[0].Start), "/lines/0/end" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Lines![0].Start = P(double.NaN, 0)), "/lines/0/start" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Circles![0].RadiusMm = 0), "/circles/0/radius_mm" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Rectangles![0].Corner2 = P(40, 0)), "/rectangles/0/corner2" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Arcs = new List<SketchArc> { new SketchArc { Id = "a", Center = P(0, 0), Start = P(10, 0), End = P(0, 12) } }), "/arcs/0/end" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Polygons = new List<SketchPolygon> { new SketchPolygon { Id = "hex", Center = P(0, 0), Vertex = P(5, 0), Sides = 2 } }), "/polygons/0/sides" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Slots = new List<SketchSlot> { new SketchSlot { Id = "s", Start = P(0, 0), End = P(10, 0), WidthMm = -1 } }), "/slots/0/width_mm" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Splines = new List<SketchSpline> { new SketchSpline { Id = "sp", Points = new List<Point2> { P(0, 0) } } }), "/splines/0/points" };
        yield return new object[] { (Action<SketchGeometry>)(g => g.Points = new List<SketchPointItem> { new SketchPointItem { Id = "p", At = P(1e7, 0) } }), "/points/0/at" };
    }

    [Theory]
    [MemberData(nameof(BadGeometry))]
    public void Impossible_geometry_names_its_field(Action<SketchGeometry> spoil, string field)
    {
        var g = Plate();
        spoil(g);
        Assert.Equal(field, Refused(g).Error.Details!["field"]);
    }

    [Theory]
    [InlineData("nothing", "/relations/0/entities/0")]
    [InlineData("hole.start", "/relations/0/entities/0")]
    [InlineData("outline", "/relations/0/entities/0")]
    [InlineData("axis.middle", "/relations/0/entities/0")]
    public void A_target_must_name_an_id_and_a_part_it_has(string target, string field)
    {
        var g = Plate();
        g.Relations![0].Entities[0] = target;
        Assert.Equal(field, Refused(g).Error.Details!["field"]);
    }

    [Theory]
    [InlineData(SketchRelationType.Coincident, 1)]
    [InlineData(SketchRelationType.Symmetric, 2)]
    [InlineData(SketchRelationType.Horizontal, 3)]
    public void A_relation_needs_the_right_number_of_entities(SketchRelationType type, int count)
    {
        var g = Plate();
        g.Relations![0] = new SketchRelationItem { Type = type, Entities = Enumerable.Repeat("axis", count).ToList() };
        Assert.Equal("/relations/0/entities", Refused(g).Error.Details!["field"]);
    }

    [Fact]
    public void Dimensions_need_a_positive_value_a_fitting_kind_and_unique_names()
    {
        var g = Plate();
        g.Dimensions![0].Value = 0;
        Assert.Equal("/dimensions/0/value", Refused(g).Error.Details!["field"]);

        g = Plate();
        g.Dimensions![0].Kind = DimensionKind.Radius;
        Assert.Equal("/dimensions/0/kind", Refused(g).Error.Details!["field"]);

        g = Plate();
        g.Dimensions![1].Name = "width";
        Assert.Equal("/dimensions/1/name", Refused(g).Error.Details!["field"]);

        g = Plate();
        g.Dimensions![0].Entities = new List<string> { "outline.bottom", "outline.top", "axis" };
        Assert.Equal("/dimensions/0/entities", Refused(g).Error.Details!["field"]);

        g = Plate();
        g.Dimensions!.Add(new SketchDimensionItem { Entities = new List<string> { "outline.bottom", "outline.left" }, Value = 400, Kind = DimensionKind.Angle });
        Assert.Equal("/dimensions/2/value", Refused(g).Error.Details!["field"]);
    }

    [Fact]
    public void A_sketch_with_nothing_in_it_is_refused()
    {
        Assert.Equal("", Refused(new SketchGeometry()).Error.Details!["field"]);
    }

    [Fact]
    public void Counts_what_SOLIDWORKS_should_hold_by_kind()
    {
        var g = Plate();
        g.Polygons = new List<SketchPolygon> { new SketchPolygon { Id = "hex", Center = P(60, 10), Vertex = P(65, 10), Sides = 6 } };
        g.Slots = new List<SketchSlot> { new SketchSlot { Id = "slot", Start = P(0, 40), End = P(20, 40), WidthMm = 4 } };
        g.CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "cl", Start = P(0, -5), End = P(0, 30) } };
        var counts = SketchDraft.Check(g, "").Expected;
        Assert.Equal(4 + 6 + 2, counts.Lines);
        Assert.Equal(2, counts.Arcs);
        Assert.Equal(1, counts.Circles);
        Assert.Equal(0, counts.Splines);
        Assert.Equal(2, counts.ConstructionLines);
    }
}
