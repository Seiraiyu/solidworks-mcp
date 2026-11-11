using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: model.mass of an assembly, with each top-level component's share (base 8000 mm³ and post 3000 mm³ at 1000 kg/m³).</summary>
public class AssemblyMassTests : AssemblyInspectionBase
{
    public AssemblyMassTests()
    {
        Doc.Bodies = 2;
        Doc.MassProperties = new ModelMassResult { VolumeMm3 = 11000, MassG = 11, DensityKgM3 = 1000, AreaMm2 = 4600, CenterOfMass = new Point3(39.09, 7.27, 8.18), PrincipalMomentsGMm2 = { 3, 1, 2 } };
        Asm.Masses["base-1"] = new ModelMassResult { VolumeMm3 = 8000, MassG = 8, CenterOfMass = new Point3(20, 10, 5) };
        Asm.Masses["post-1"] = new ModelMassResult { VolumeMm3 = 3000, MassG = 3, CenterOfMass = new Point3(95, 5, 15) };
    }

    ModelMassResult Mass() => Run(new ModelMassOperation(Services), new ModelMassRequest());

    [Fact]
    public void An_assemblys_mass_is_its_components_with_each_share()
    {
        var m = Mass();
        Assert.Equal(11, m.MassG);
        Assert.Equal(new[] { "base-1", "post-1" }, m.Components!.Select(c => c.Name));
        Assert.Equal(3, m.Components![1].MassG);
        Assert.Equal(95, m.Components![1].CenterOfMass!.X);
        Assert.Equal(1000, m.DensityKgM3, 9);
        Assert.Null(m.Material);
        Assert.Equal(new double[] { 1, 2, 3 }, m.PrincipalMomentsGMm2);
    }

    [Fact]
    public void A_suppressed_component_has_no_share()
    {
        Asm.Named("post-1").Info.State = ComponentState.Suppressed;
        Doc.MassProperties!.MassG = 8;
        Doc.MassProperties.VolumeMm3 = 8000;
        Assert.Equal(new[] { "base-1" }, Mass().Components!.Select(c => c.Name));
    }

    [Fact]
    public void Shares_that_do_not_add_up_fail_verification()
    {
        Asm.Masses["post-1"].MassG = 2.5;
        var e = Fails(() => Mass());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("components_sum", e.Error.Details!["check"]);
    }

    [Fact]
    public void A_component_with_no_mass_properties_is_operation_failed()
    {
        Asm.Masses.Remove("post-1");
        var e = Fails(() => Mass());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("post-1", e.Error.Details!["component"]);
    }

    [Fact]
    public void A_part_has_no_shares()
    {
        Post.Bodies = 1;
        Post.MassProperties = new ModelMassResult { VolumeMm3 = 3000, MassG = 3, DensityKgM3 = 1000 };
        Assert.Null(Run(new ModelMassOperation(Services), new ModelMassRequest { Document = "post.SLDPRT" }).Components);
    }
}
