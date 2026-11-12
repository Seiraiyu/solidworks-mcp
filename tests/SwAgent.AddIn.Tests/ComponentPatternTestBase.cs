using System.Collections.Generic;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// plate-1, an 80 x 80 x 10 plate fixed at the origin, with a Ø10 hole at its centre (40, 40) and CirPattern1, four Ø10
/// holes round it at (60, 40), (40, 60), (20, 40) and (40, 20); pin-1 standing in the (60, 40) hole; block-1 at
/// (0, -40, 0). Faces are in assembly coordinates, as the fakes hold them.
/// </summary>
public abstract class ComponentPatternTestBase : AssemblyTestBase
{
    internal readonly FakeComponent Plate, Pin, Block;
    internal readonly FakeEntity PlateEdge, PlateTop, PlateFront, PlateCentre;
    internal readonly FakeDocument PlatePart;

    protected ComponentPatternTestBase()
    {
        Plate = Asm.AddComponent("plate-1", PathOf("base.SLDPRT"), fix: true);
        Pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"), new Point3(60, 40, 0));
        Block = Asm.AddComponent("block-1", PathOf("base.SLDPRT"), new Point3(0, -40, 0));
        PlateEdge = Asm.AddEntity("plate-1", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(40, 0, 0), Axis = new Vector3(1, 0, 0), LengthMm = 80 });
        PlateTop = Asm.AddEntity("plate-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Origin = new Point3(40, 40, 10), Normal = new Vector3(0, 0, 1) });
        PlateFront = Asm.AddEntity("plate-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Origin = new Point3(40, 0, 5), Normal = new Vector3(0, -1, 0) });
        PlateCentre = Asm.AddEntity("plate-1", ReferenceKind.Face, Hole(40, 40));
        PlatePart = FakeDocument.Part("base.SLDPRT", PathOf("base.SLDPRT"));
        PlatePart.AddFeature("CirPattern1", "CirPattern");
        PlatePart.AddFeature("Boss-Extrude1", "Extrusion");
        Plate.Model = PlatePart;
        Asm.DrivingFaces["CirPattern1"] = new List<EntityInfo> { Hole(60, 40), Hole(40, 60), Hole(20, 40), Hole(40, 20) };
        Asm.DrivingFaces["Boss-Extrude1"] = new List<EntityInfo> { new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Origin = new Point3(40, 40, 10), Normal = new Vector3(0, 0, 1) } };
    }

    internal static EntityInfo Hole(double x, double y) =>
        new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Cylinder, Origin = new Point3(x, y, 5), Axis = new Vector3(0, 0, 1), RadiusMm = 5 };

    internal string Ref(FakeEntity e) => Services.References.Describe(Doc, e).Reference!;

    internal static Transform At(double x, double y, double z) => new Transform { Origin = new Point3(x, y, z) };

    internal ComponentPatternResult Linear(ComponentLinearPatternRequest request)
    {
        request.Document ??= "stand.SLDASM";
        return Run(new ComponentLinearPatternOperation(Services), request);
    }

    internal ComponentPatternResult Circular(ComponentCircularPatternRequest request)
    {
        request.Document ??= "stand.SLDASM";
        return Run(new ComponentCircularPatternOperation(Services), request);
    }

    /// <summary>block-1 in a row of three along the plate's front edge, 20 mm apart.</summary>
    internal ComponentPatternResult BlockRow() =>
        Linear(new ComponentLinearPatternRequest { Components = { "block-1" }, Direction = Ref(PlateEdge), SpacingMm = 20, Count = 3 });
}
