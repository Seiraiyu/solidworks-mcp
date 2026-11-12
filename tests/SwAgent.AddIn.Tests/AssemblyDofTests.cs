using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AssemblyDofTests : MateTestBase
{
    AssemblyDofResult Dof(params string[] components) =>
        Run(new AssemblyDofOperation(Services), new AssemblyDofRequest { Document = "stand.SLDASM", Components = components.Length == 0 ? null : components.ToList() });

    [Fact]
    public void Each_top_level_component_with_its_state_and_free_motion()
    {
        Block.Mates.Add("Coincident1");
        var dofs = new RemainingDofs { Result = Freedom.Restricted };
        dofs.Translations.Add(new DofSlot(3, new Vector3(1, 0, 0)));
        dofs.Translations.Add(new DofSlot(3, new Vector3(0, 1, 0)));
        dofs.Rotations.Add(new DofSlot(3, new Vector3(0, 0, 1), new Point3(20, 50, 10)));
        dofs.Rotations.Add(new DofSlot(0, null));
        Asm.Dofs["block-1"] = dofs;
        var r = Dof();
        Assert.Equal(new[] { "plate-1", "pin-1", "block-1" }, r.Components.Select(c => c.Name));
        Assert.Equal(Constraint.Fixed, r.Components[0].Constraint);
        Assert.Equal(0, r.Components[0].FreeTranslations);
        Assert.Equal(3, r.Components[1].FreeRotations);
        Assert.Equal(2, r.Components[2].FreeTranslations);
        Assert.Equal(1, r.Components[2].FreeRotations);
        Assert.All(r.Components, c => Assert.StartsWith("r1.component.", c.Reference));
    }

    [Fact]
    public void Named_components_only_and_an_unknown_one_names_its_field()
    {
        Assert.Equal("pin-1", Assert.Single(Dof("pin-1").Components).Name);
        Assert.Equal("/components/1", Fails(() => Dof("pin-1", "nope-1")).Error.Details!["field"]);
    }

    [Fact]
    public void A_mated_component_SOLIDWORKS_cannot_describe_has_a_note_and_no_counts()
    {
        Pin.Mates.Add("Concentric1");
        Asm.Dofs["pin-1"] = new RemainingDofs { Result = Freedom.Failed };
        var pin = Dof("pin-1").Components.Single();
        Assert.Null(pin.FreeTranslations);
        Assert.Contains("swRemainingDofs_Failed", pin.Note);
    }
}
