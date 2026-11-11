using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Core.Tests;

/// <summary>Plan 4.B: geometry read in a component's own coordinates, moved into the assembly's.</summary>
public class PlacementSpaceTests
{
    /// <summary>4.A's turned post: its x axis along the assembly's y, at (100, 0, 0).</summary>
    static Transform Turned() => Placement.Make(new Point3(100, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), "x", "z");

    [Fact]
    public void A_point_moves_with_the_origin_and_turns_with_the_axes()
    {
        var p = Placement.ToAssembly(Turned(), new Point3(10, 0, 30));
        Assert.Equal(100, p.X, 9);
        Assert.Equal(10, p.Y, 9);
        Assert.Equal(30, p.Z, 9);
        var q = Placement.ToAssembly(Turned(), new Point3(0, 10, 0));
        Assert.Equal(90, q.X, 9);
        Assert.Equal(0, q.Y, 9);
    }

    [Fact]
    public void A_direction_turns_but_does_not_move()
    {
        var n = Placement.ToAssembly(Turned(), new Vector3(-1, 0, 0));
        Assert.Equal(0, n.X);
        Assert.Equal(-1, n.Y, 12);
        Assert.Equal(0, n.Z);
    }

    [Fact]
    public void To_component_undoes_to_assembly()
    {
        var t = Placement.Make(new Point3(5, -7, 12), new Vector3(0, 0, 1), new Vector3(1, 0, 0), "x", "z");
        var back = Placement.ToComponent(t, Placement.ToAssembly(t, new Point3(3, 4, 5)));
        Assert.Equal(3, back.X, 9);
        Assert.Equal(4, back.Y, 9);
        Assert.Equal(5, back.Z, 9);
    }

    [Fact]
    public void A_box_becomes_the_box_of_its_moved_corners()
    {
        var box = Placement.ToAssembly(Turned(), new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(10, 10, 30) });
        Assert.Equal(90, box.Min.X, 9);
        Assert.Equal(100, box.Max.X, 9);
        Assert.Equal(0, box.Min.Y, 9);
        Assert.Equal(10, box.Max.Y, 9);
        Assert.Equal(30, box.Max.Z, 9);
    }

    [Fact]
    public void A_description_moves_its_point_direction_and_box_only()
    {
        var face = new EntityInfo
        {
            Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 300,
            Normal = new Vector3(-1, 0, 0), Origin = new Point3(0, 5, 15),
            Box = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(0, 10, 30) },
        };
        Placement.MoveToAssembly(Turned(), face);
        Assert.Equal(-1, face.Normal!.Y, 12);
        Assert.Equal(95, face.Origin!.X, 9);
        Assert.Equal(0, face.Origin.Y, 9);
        Assert.Equal(90, face.Box!.Min.X, 9);
        Assert.Equal(0, face.Box.Max.Y, 9);
        Assert.Equal(300, face.AreaMm2);
        Assert.Equal("Boss-Extrude1", face.Feature);
    }
}
