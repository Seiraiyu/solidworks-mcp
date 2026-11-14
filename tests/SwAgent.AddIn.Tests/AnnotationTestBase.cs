using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// The block's front view at 1:1 centred at (100, 150), its origin drawn at (70, 130): the front face's left, right and
/// bottom edges, the hole's front circle (r 5 at (20, 20, 20), drawn at (90, 150)) and the corner (60, 40, 20) are shown;
/// the back face's left edge is not.
/// </summary>
public abstract class AnnotationTestBase : DrawingTestBase
{
    internal readonly FakeView Front;
    internal readonly FakeEntity Left;
    internal readonly FakeEntity Right;
    internal readonly FakeEntity Bottom;
    internal readonly FakeEntity Hole;
    internal readonly FakeEntity Corner;
    internal readonly FakeEntity Back;

    protected AnnotationTestBase()
    {
        Front = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        Left = Line(new Point3(0, 20, 20), new Vector3(0, 1, 0), 40);
        Right = Line(new Point3(60, 20, 20), new Vector3(0, 1, 0), 40);
        Bottom = Line(new Point3(30, 0, 20), new Vector3(1, 0, 0), 60);
        Hole = Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 20), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 10 * System.Math.PI });
        Corner = Block.Add(ReferenceKind.Vertex, new EntityInfo { Geometry = GeometryType.Point, Origin = new Point3(60, 40, 20) });
        Back = Line(new Point3(0, 20, 0), new Vector3(0, 1, 0), 40);
        foreach (var e in new[] { Left, Right, Bottom, Hole }) Front.Visible.Add(new VisibleEntity(e, false, false));
        Front.Visible.Add(new VisibleEntity(Corner, true, false));
    }

    FakeEntity Line(Point3 middle, Vector3 axis, double length) =>
        Block.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = middle, Axis = axis, LengthMm = length });

    /// <summary>An entity of the block as <paramref name="view"/> (the front by default) shows it, by its reference.</summary>
    internal ViewEntity On(FakeEntity entity, string? view = null) =>
        new ViewEntity { View = view ?? Front.Facts.Name, Reference = Services.References.Describe(Block, entity).Reference };
}
