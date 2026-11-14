using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// The drawings suite's assembly, faked: pinblock.SLDASM holds the block (block-1, fixed at the origin, 60 x 40 x 20) and
/// a Ø10 x 30 pin (pin-1) standing in its hole at (20, 20), z 0 to 30; its Default configuration has ExplView1, whose one
/// step moves the pin 40 mm along +y. Its front view is on Sheet1 at 1:1, centred at (250, 150), the assembly's origin
/// drawn at (220, 135); it shows the block's front bottom edge and the pin's front circle (r 5 at (20, 20, 30), drawn at
/// (240, 155)) as the components' own entities.
/// </summary>
public abstract class AssemblyDrawingTestBase : DrawingTestBase
{
    internal readonly FakeDocument Asm;
    internal readonly FakeComponent BlockC;
    internal readonly FakeComponent PinC;
    internal readonly FakeEntity BlockEdge;
    internal readonly FakeEntity PinCircle;
    internal readonly FakeView AsmFront;

    protected AssemblyDrawingTestBase()
    {
        Asm = FakeDocument.NewAssembly("pinblock.SLDASM", PathOf("pinblock.SLDASM"));
        System.IO.File.WriteAllText(Asm.Document.Path!, "assembly");
        Asm.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 30) };
        var asm = Asm.Asm!;
        BlockC = asm.AddComponent("block-1", Block.Document.Path!, fix: true);
        BlockC.Info.Box = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(60, 40, 20) };
        PinC = asm.AddComponent("pin-1", PathOf("pin.SLDPRT"), new Point3(20, 20, 0));
        PinC.Info.Box = new Box3 { Min = new Point3(15, 15, 0), Max = new Point3(25, 25, 30) };
        BlockEdge = asm.AddEntity("block-1", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(30, 0, 20), Axis = new Vector3(1, 0, 0), LengthMm = 60 });
        PinCircle = asm.AddEntity("pin-1", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 20, 30), Axis = new Vector3(0, 0, 1), RadiusMm = 5, LengthMm = 10 * System.Math.PI });
        var exploded = new FakeExplodeView("ExplView1", "Default");
        var step = new FakeExplodeStep { Name = "Explode Step1", Move = new Vector3(0, 40, 0), DistanceMm = 40 };
        step.Components.Add(PinC);
        exploded.Steps.Add(step);
        asm.Views.Add(exploded);
        AsmFront = Drw.AddView("Sheet1", Asm, "front", new SheetPoint(250, 150));
        AsmFront.Visible.Add(new VisibleEntity(BlockEdge, false, false));
        AsmFront.Visible.Add(new VisibleEntity(PinCircle, false, false));
    }

    /// <summary>The bill of materials a test puts on the front view first, as if SOLIDWORKS had made it.</summary>
    internal TableFacts Bom()
    {
        string name = Drw.InsertBom(AsmFront.Facts.Name, BomKind.TopLevel, "Default", null, TableAnchor.TopLeft)!;
        Drw.Calls.Clear();
        return Drw.Tabled.Single(t => t.Name == name);
    }
}
