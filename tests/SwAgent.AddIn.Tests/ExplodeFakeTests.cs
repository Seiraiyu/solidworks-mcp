using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ExplodeFakeTests : AssemblyTestBase
{
    [Fact]
    public void A_fake_view_explodes_its_steps_and_collapses_back()
    {
        var pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"), new Point3(20, 20, 0));
        var up = Doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(), Axis = new Vector3(0, 0, 1) });
        Assert.Null(Asm.AddExplodeStep(new ExplodeStepSpec { Components = new[] { (object)pin.Entity }, Direction = up, DistanceMm = 40 }, out _));
        string view = Asm.CreateExplodedView()!;
        Assert.Equal("ExplView1", view);
        Assert.Equal("Explode Step1", Asm.AddExplodeStep(new ExplodeStepSpec { Components = new[] { (object)pin.Entity }, Direction = up, DistanceMm = 40 }, out string tried));
        Assert.Equal("fake: made Explode Step1", tried);
        Assert.Equal(new[] { "ExplView1" }, Asm.ExplodedViews("Default"));
        Assert.Null(Asm.ShownExploded());
        Assert.True(Asm.ShowExploded(view, true));
        Assert.Equal(view, Asm.ShownExploded());
        Assert.Equal(40, Asm.Read(pin.Entity).Transform.Origin.Z, 9);
        var step = Assert.Single(Asm.ExplodeSteps(view));
        Assert.Equal(40, step.DistanceMm);
        Assert.Same(pin.Entity, Assert.Single(step.Components));
        Assert.True(Asm.ShowExploded(view, false));
        Assert.Null(Asm.ShownExploded());
        Assert.Equal(0, Asm.Read(pin.Entity).Transform.Origin.Z, 9);
    }

    [Fact]
    public void A_fake_step_can_go_against_its_direction_and_a_view_be_emptied()
    {
        var pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"));
        var up = Doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(), Axis = new Vector3(0, 0, 1) });
        Asm.StepGoesAgainst = true;
        string view = Asm.CreateExplodedView()!;
        Asm.AddExplodeStep(new ExplodeStepSpec { Components = new[] { (object)pin.Entity }, Direction = up, DistanceMm = 10 }, out _);
        Asm.ShowExploded(view, true);
        Assert.Equal(-10, Asm.Read(pin.Entity).Transform.Origin.Z, 9);
        Assert.True(Asm.DeleteExplodedView(view, out string tried));
        Assert.Equal("fake: steps deleted, the view left empty", tried);
        Assert.Equal(new[] { view }, Asm.ExplodedViews("Default"));
        Assert.Empty(Asm.ExplodeSteps(view));
        Assert.Null(Asm.ShownExploded());
        Assert.Equal(0, Asm.Read(pin.Entity).Transform.Origin.Z, 9);
    }
}
