using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class PatternFakeTests : AssemblyTestBase
{
    [Fact]
    public void A_fake_linear_pattern_makes_instances_along_the_direction()
    {
        var pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"), new Point3(10, 0, 0));
        var edge = Doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(), Axis = new Vector3(1, 0, 0) });
        var made = Asm.LinearPattern(new ComponentLinearSpec { Seeds = new[] { (object)pin.Entity }, Direction = edge, SpacingMm = 20, Count = 3 }, out string tried);
        Assert.NotNull(made);
        Assert.Equal("fake: made LocalLPattern1", tried);
        var facts = Asm.ReadPattern(made!);
        Assert.Equal(ComponentPatternKind.Linear, facts.Kind);
        Assert.Equal(new[] { 30.0, 50.0 }, facts.Instances.Select(i => Asm.Read(i).Transform.Origin.X));
        Assert.Same(made, Asm.PatternByName("LocalLPattern1"));
    }

    [Fact]
    public void Deleting_a_fake_pattern_takes_its_instances_and_leaves_its_seed()
    {
        var pin = Asm.AddComponent("pin-1", PathOf("pin.SLDPRT"));
        var edge = Doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, Origin = new Point3(), Axis = new Vector3(1, 0, 0) });
        var made = Asm.LinearPattern(new ComponentLinearSpec { Seeds = new[] { (object)pin.Entity }, Direction = edge, SpacingMm = 20, Count = 3 }, out _)!;
        Asm.DeletePatterns(new[] { made });
        Assert.Empty(Asm.Patterns());
        Assert.Equal(new[] { "pin-1" }, Asm.Items.Select(c => c.Info.Name));
    }

    [Fact]
    public void A_fake_interference_session_counts_its_reads_and_dones()
    {
        Asm.AddComponent("block-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("block-2", PathOf("base.SLDPRT"), new Point3(30, 0, 0));
        Asm.Clashes.Add(new FakeClash("block-1", "block-2", 2000, null));
        var session = Asm.OpenInterference(new InterferenceOptions());
        Assert.Equal(2000, Assert.Single(session.Read()).VolumeMm3);
        session.Done();
        var s = Assert.Single(Asm.Sessions);
        Assert.Equal(1, s.Reads);
        Assert.Equal(1, s.Dones);
    }
}
