using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// plate-1, a 40 x 20 x 10 block with a Ø10 hole at (20, 10), fixed at the origin; pin-1, Ø10 x 30, standing at
/// (60, 0, 0); block-1, 40 x 20 x 10, at (0, 40, 0). Faces are given in assembly coordinates, as the fakes hold them.
/// </summary>
public abstract class MateTestBase : AssemblyTestBase
{
    internal readonly FakeComponent Plate, Pin, Block;
    internal readonly FakeEntity PlateTop, PlateFront, PlateLeft, PlateHole, PinSide, PinTop, PinBottom, BlockBottom, BlockFront, BlockLeft;

    protected MateTestBase()
    {
        Plate = Asm.AddComponent("plate-1", PathOf("base.SLDPRT"), fix: true);
        Pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"), new Point3(60, 0, 0));
        Block = Asm.AddComponent("block-1", PathOf("base.SLDPRT"), new Point3(0, 40, 0));
        PlateTop = Face("plate-1", new Point3(20, 10, 10), new Vector3(0, 0, 1));
        PlateFront = Face("plate-1", new Point3(20, 0, 5), new Vector3(0, -1, 0));
        PlateLeft = Face("plate-1", new Point3(0, 10, 5), new Vector3(-1, 0, 0));
        PlateHole = Asm.AddEntity("plate-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(20, 10, 5), Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        PinSide = Asm.AddEntity("pin-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(60, 0, 15), Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        PinTop = Face("pin-1", new Point3(60, 0, 30), new Vector3(0, 0, 1));
        PinBottom = Face("pin-1", new Point3(60, 0, 0), new Vector3(0, 0, -1));
        BlockBottom = Face("block-1", new Point3(20, 50, 0), new Vector3(0, 0, -1));
        BlockFront = Face("block-1", new Point3(20, 40, 5), new Vector3(0, -1, 0));
        BlockLeft = Face("block-1", new Point3(0, 50, 5), new Vector3(-1, 0, 0));
    }

    FakeEntity Face(string component, Point3 origin, Vector3 normal) =>
        Asm.AddEntity(component, ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Origin = origin, Normal = normal, AreaMm2 = 100 });

    internal string Ref(FakeEntity e) => Services.References.Describe(Doc, e).Reference!;

    /// <summary>What a solve does: moves a component and every face of it by (dx, dy, dz).</summary>
    internal void Move(FakeComponent c, double dx, double dy, double dz)
    {
        var o = c.Info.Transform.Origin;
        c.Info.Transform.Origin = new Point3(o.X + dx, o.Y + dy, o.Z + dz);
        foreach (var e in Doc.Entities.Where(e => e.Component == c && e.Info.Origin != null))
        {
            var p = e.Info.Origin!;
            e.Info.Origin = new Point3(p.X + dx, p.Y + dy, p.Z + dz);
        }
    }

    internal static MateEntity Side(string entity, string? component = null) => new MateEntity { Entity = entity, Component = component };

    internal static T Copy<T>(T value) where T : class => JsonCodec.Deserialize<T>(JsonCodec.Serialize(value));
}
