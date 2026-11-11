using System;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SheetEdgeTests : FeatureTestBase
{
    /// <summary>Sketch1 (40 × 20) as a 2 mm tab straight through the fake: its base face at z = 0, normal +z.</summary>
    FakeDocument Tab()
    {
        var doc = Part();
        doc.SheetMetal.BaseFlange(doc.FeatureByName("Sketch1")!, new BaseFlangeSpec { ThicknessMm = 2, BendRadiusMm = 3 });
        return doc;
    }

    [Fact]
    public void An_edge_where_the_sheet_ends_gives_its_ends_the_face_it_bounds_and_the_way_out_of_the_material()
    {
        var doc = Tab();
        var edge = doc.SheetEdge(new Point3(0, 20, 0), new Point3(40, 20, 0), new Vector3(0, 1, 0));
        var read = SheetMetalParts.Edge(doc, edge, "/edges/0", 2);
        Assert.Equal(40, read.LengthMm, 9);
        Assert.Equal((0.0, 20.0, 0.0), (read.Start.X, read.Start.Y, read.Start.Z));
        Assert.Equal((40.0, 20.0, 0.0), (read.End.X, read.End.Y, read.End.Z));
        Assert.Same(doc.SheetFaceList[0], read.Face);
        Assert.Equal(1, read.Normal.Z);
        Assert.Equal(1, read.Outward.Y);
        Assert.Equal(0, read.FaceOrigin!.Z);
    }

    [Fact]
    public void An_edge_that_is_not_where_the_sheet_ends_is_refused_at_its_field()
    {
        var doc = Tab();
        var curved = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, LengthMm = 10, Origin = new Point3(0, 0, 0), Axis = new Vector3(0, 0, 1) });
        var e = Assert.Throws<SwAgentException>(() => SheetMetalParts.Edge(doc, curved, "/edges/1", 2));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/edges/1", e.Error.Details!["field"]);
        Assert.Contains("circle edge", e.Message);

        var inside = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 40, Origin = new Point3(20, 10, 0), Axis = new Vector3(1, 0, 0) });
        e = Assert.Throws<SwAgentException>(() => SheetMetalParts.Edge(doc, inside, "/edges/0", 2));
        Assert.Contains("not an edge where the sheet ends", e.Message);

        var wrongThickness = doc.SheetEdge(new Point3(0, 0, 0), new Point3(40, 0, 0), new Vector3(0, -1, 0));
        e = Assert.Throws<SwAgentException>(() => SheetMetalParts.Edge(doc, wrongThickness, "/edges/0", 3));
        Assert.Contains("3 mm wide", e.Message);
    }
}
