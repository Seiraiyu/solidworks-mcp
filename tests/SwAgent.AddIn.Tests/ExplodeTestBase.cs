using SwAgent.AddIn.Operations;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// plate-1, fixed at the origin, with an edge along X, a Ø10 hole whose axis runs up Z through (20, 20), and its top face
/// at z 10; bracket-1 at (10, 10, 10); pin-1 at (20, 20, 0). Faces and edges are in assembly coordinates, as the fakes
/// hold them. The assembly's own front plane (z) is the fake document's first plane.
/// </summary>
public abstract class ExplodeTestBase : AssemblyTestBase
{
    internal readonly FakeComponent Plate, Bracket, Pin;
    internal readonly FakeEntity PlateEdge, PlateHole, PlateTop;

    protected ExplodeTestBase()
    {
        Plate = Asm.AddComponent("plate-1", PathOf("base.SLDPRT"), fix: true);
        Bracket = Asm.AddComponent("bracket-1", PathOf("base.SLDPRT"), new Point3(10, 10, 10));
        Pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"), new Point3(20, 20, 0));
        PlateEdge = Asm.AddEntity("plate-1", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(40, 0, 0), Axis = new Vector3(1, 0, 0), LengthMm = 80 });
        PlateHole = Asm.AddEntity("plate-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(20, 20, 5), Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        PlateTop = Asm.AddEntity("plate-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Origin = new Point3(40, 20, 10), Normal = new Vector3(0, 0, 1) });
    }

    internal string Ref(FakeEntity e) => Services.References.Describe(Doc, e).Reference!;

    internal static ExplodeStep Step(string direction, double? distance, params string[] components)
    {
        var s = new ExplodeStep { Direction = direction, DistanceMm = distance };
        s.Components.AddRange(components);
        return s;
    }

    internal static ExplodeCreateRequest Request(params ExplodeStep[] steps)
    {
        var r = new ExplodeCreateRequest { Document = "stand.SLDASM" };
        r.Steps.AddRange(steps);
        return r;
    }

    internal ExplodeCreateResult Explode(ExplodeCreateRequest request) => Run(new ExplodeCreateOperation(Services), request);

    internal ExplodeCreateResult Explode(params ExplodeStep[] steps) => Explode(Request(steps));
}
