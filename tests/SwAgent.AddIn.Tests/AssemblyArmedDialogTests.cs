using System.Collections.Generic;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>diag.arm_dialog's message box, shown mid-change by every assembly change (design 455, Testing: a mate that raises a dialog mid-operation; question 693).</summary>
public class AssemblyArmedDialogTests : MateTestBase
{
    [Fact]
    public void A_dialog_answered_ok_lets_the_mate_stand_and_is_shown_once()
    {
        Asm.OnMate = (_, _) => Move(Pin, -40, 10, 0);
        var shown = new List<string>();
        Services.ShowBox = box =>
        {
            shown.Add(box.Title);
            return "ok";
        };
        Services.Runtime.ArmDialog(new DiagDialogRequest { Title = "mid-mate", Text = "x", Buttons = DiagDialogButtons.OkCancel });
        var r = Add(MateType.Concentric, Side(Ref(PinSide)), Side(Ref(PlateHole)));
        Assert.Equal(new[] { "mid-mate" }, shown.ToArray());
        Assert.Single(Asm.MateItems);
        Assert.Equal("pin-1", Assert.Single(r.Change.Moved).Name);
        Asm.OnMate = null;
        Add(MateType.Parallel, Side(Ref(BlockFront)), Side(Ref(PlateFront)));
        Assert.Single(shown);
        Assert.Equal(2, Asm.MateItems.Count);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("no")]
    public void A_dialog_answered_cancel_or_no_takes_the_mate_back_and_puts_the_pin_back(string pressed)
    {
        Asm.OnMate = (_, _) => Move(Pin, -40, 10, 0);
        Services.ShowBox = _ => pressed;
        Services.Runtime.ArmDialog(new DiagDialogRequest { Title = "mid-mate", Text = "x", Buttons = DiagDialogButtons.YesNo });
        var e = Fails(() => Add(MateType.Concentric, Side(Ref(PinSide)), Side(Ref(PlateHole))));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(pressed, e.Error.Details!["pressed"]);
        Assert.Equal("mid-mate", e.Error.Details["dialog"]);
        Assert.Contains("taken back", e.Message);
        Assert.Empty(Asm.MateItems);
        Assert.Equal(60, Pin.Info.Transform.Origin.X, 9);
        Assert.Contains("pin-1", (List<object?>)e.Error.Details["positions_restored"]!);
    }
}
