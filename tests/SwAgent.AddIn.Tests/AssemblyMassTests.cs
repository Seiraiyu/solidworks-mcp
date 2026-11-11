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
        Asm.SetMass("base-1", 8000, 8, new Point3(20, 10, 5));
        Asm.SetMass("post-1", 3000, 3, new Point3(95, 5, 15));
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
        Assert.Empty(m.Skipped!);
    }

    [Fact]
    public void A_share_counts_the_configuration_the_assembly_uses_not_the_files_active_one()
    {
        // Review of 4.B: post.SLDPRT's active configuration is Default (3000 mm³), but the assembly uses its Tall one
        // (5000 mm³), so the whole is 13000 mm³. A share read from the file's own mass said 3000 and failed components_sum.
        Post.Bodies = 1;
        Post.MassProperties = new ModelMassResult { VolumeMm3 = 3000, MassG = 3, DensityKgM3 = 1000, CenterOfMass = new Point3(5, 5, 15) };
        Asm.Named("post-1").Info.Configuration = "Tall";
        Asm.SetMass("post-1", 5000, 5, new Point3(95, 5, 25), "Tall");
        Doc.MassProperties!.MassG = 13;
        Doc.MassProperties.VolumeMm3 = 13000;
        var m = Mass();
        Assert.Equal(5000, m.Components![1].VolumeMm3);
        Assert.Equal(25, m.Components![1].CenterOfMass!.Z);
        Assert.Equal(13, m.Components!.Sum(c => c.MassG), 9);
        Assert.Empty(m.Skipped!);
    }

    [Fact]
    public void A_lightweight_component_is_skipped_with_why_and_the_whole_still_counts_it()
    {
        Asm.Named("post-1").Info.State = ComponentState.Lightweight;
        var m = Mass();
        Assert.Equal(11, m.MassG);
        Assert.Equal(new[] { "base-1" }, m.Components!.Select(c => c.Name));
        var skipped = Assert.Single(m.Skipped!);
        Assert.Equal("post-1", skipped.Name);
        Assert.Equal(ComponentState.Lightweight, skipped.State);
        Assert.Contains("lightweight", skipped.Reason);
    }

    [Fact]
    public void A_share_that_cannot_be_counted_exactly_is_skipped_with_why()
    {
        Asm.MassRefusals["post-1"] = "post-1 uses its configuration Steel, whose material (AISI 1020) is not that of the file's active configuration Default (none set)";
        var m = Mass();
        var skipped = Assert.Single(m.Skipped!);
        Assert.Equal(ComponentState.Resolved, skipped.State);
        Assert.Contains("AISI 1020", skipped.Reason);
        Assert.Equal(8, m.Components!.Single().MassG);
    }

    [Fact]
    public void Shares_beyond_the_whole_fail_verification_even_with_one_skipped()
    {
        Asm.Named("post-1").Info.State = ComponentState.Lightweight;
        Asm.Masses["base-1"]["Default"].MassG = 12;
        var e = Fails(() => Mass());
        Assert.Equal("components_sum", e.Error.Details!["check"]);
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
        Asm.Masses["post-1"]["Default"].MassG = 2.5;
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
