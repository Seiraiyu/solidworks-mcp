using System;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

public class DrawnEntityTests
{
    /// <summary>The Ø10 hole through the block at (20, 20), its front edge on z = 20.</summary>
    static EntityInfo Hole => new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 2 * Math.PI * 5 };

    [Fact]
    public void A_hole_seen_face_on_is_a_circle_at_its_sheet_position_with_its_radius_scaled()
    {
        var drawn = ViewGeometry.Draw(Views.Front(0.5, 85, 140), Hole, vertex: false);
        Assert.Equal(DrawnShape.Circle, drawn.Shape);
        Assert.Equal(95, drawn.Centre!.X, 9);
        Assert.Equal(150, drawn.Centre.Y, 9);
        Assert.Equal(2.5, drawn.RadiusMm!.Value, 9);
    }

    [Fact]
    public void A_hole_seen_edge_on_is_a_line_across_its_diameter()
    {
        var drawn = ViewGeometry.Draw(Views.Top(1, 70, 100), Hole, vertex: false);
        Assert.Equal(DrawnShape.Line, drawn.Shape);
        Assert.Equal(10, Math.Abs(drawn.End!.X - drawn.Start!.X), 9);
        Assert.Equal(drawn.Start.Y, drawn.End.Y, 9);
    }

    [Fact]
    public void Part_of_a_circle_is_an_arc_and_a_circle_at_an_angle_is_other()
    {
        var arc = Hole;
        arc.LengthMm = Math.PI * 5;
        Assert.Equal(DrawnShape.Arc, ViewGeometry.Draw(Views.Front(), arc, vertex: false).Shape);
        Assert.Equal(DrawnShape.Other, ViewGeometry.Draw(Views.Isometric(), Hole, vertex: false).Shape);
    }

    [Fact]
    public void A_straight_edge_is_a_line_end_to_end_and_seen_end_on_a_point()
    {
        var top = new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(30, 40, 20), Axis = new Vector3(1, 0, 0), LengthMm = 60 };
        var line = ViewGeometry.Draw(Views.Front(1, 70, 130), top, vertex: false);
        Assert.Equal(DrawnShape.Line, line.Shape);
        Assert.Equal(70, line.Start!.X, 9);
        Assert.Equal(130, line.End!.X, 9);
        Assert.Equal(170, line.Start.Y, 9);
        var point = ViewGeometry.Draw(Views.Right(1, 0, 0), top, vertex: false);
        Assert.Equal(DrawnShape.Point, point.Shape);
    }

    [Fact]
    public void A_vertex_is_a_point_and_a_spline_is_other()
    {
        var vertex = ViewGeometry.Draw(Views.Front(1, 70, 130), new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(60, 40, 20) }, vertex: true);
        Assert.Equal(DrawnShape.Point, vertex.Shape);
        Assert.Equal(130, vertex.Start!.X, 9);
        Assert.Equal(DrawnShape.Other, ViewGeometry.Draw(Views.Front(), new EntityInfo { Geometry = GeometryType.Spline }, vertex: false).Shape);
    }
}
