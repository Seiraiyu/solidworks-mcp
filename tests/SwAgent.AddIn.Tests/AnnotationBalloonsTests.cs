using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AnnotationBalloonsTests : AssemblyDrawingTestBase
{
    AnnotationBalloonsResult Balloons(string? view = null, BalloonLayout? layout = null) =>
        Run(new AnnotationBalloonsOperation(Services), new AnnotationBalloonsRequest { View = view ?? AsmFront.Facts.Name, Layout = layout });

    [Fact]
    public void Each_item_gets_a_balloon_numbered_as_the_bill_numbers_its_row()
    {
        var bom = Bom();
        var result = Balloons(layout: BalloonLayout.Top);
        Assert.Equal(bom.Name, result.Bom);
        Assert.Equal((2, 0), (result.Items, result.Already));
        Assert.Equal(new[] { ("block-1", "1"), ("pin-1", "2") }, result.Made.Select(b => (b.Component!, b.ItemNumber!)));
        Assert.All(result.Made, b => Assert.Equal(AnnotationKind.Balloon, b.Kind));
        Assert.Equal(new[] { ReferenceKind.Annotation, ReferenceKind.Annotation }, result.Change.Created.Select(c => c.Kind));
        Assert.Contains($"balloons {AsmFront.Facts.Name} top", Drw.Calls);
    }

    [Fact]
    public void Without_a_bill_each_item_has_its_own_number()
    {
        var result = Balloons();
        Assert.Null(result.Bom);
        Assert.Equal(2, result.Made.Select(b => b.ItemNumber).Distinct().Count());
    }

    [Fact]
    public void A_balloon_lists_back_attached_to_its_component_by_reference()
    {
        var pin = Balloons().Made.Single(b => b.Component == "pin-1");
        Assert.Equal(Services.References.Describe(Asm, PinCircle).Reference, Assert.Single(pin.Attached).Reference);
    }

    [Fact]
    public void Items_already_ballooned_are_skipped()
    {
        Balloons();
        var again = Balloons();
        Assert.Empty(again.Made);
        Assert.Equal((2, 2), (again.Items, again.Already));
    }

    [Fact]
    public void Balloons_numbered_unlike_the_bill_are_refused_and_deleted()
    {
        Bom();
        Drw.BalloonsMisnumbered = true;
        var e = Fails(() => Balloons());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("balloon_item", e.Error.Details!["check"]);
        Assert.DoesNotContain(Drw.Annotated, a => a.Kind == AnnotationKind.Balloon);
    }

    [Fact]
    public void Two_balloons_on_one_item_are_refused_and_deleted()
    {
        var pin2 = Asm.Asm!.AddComponent("pin-2", PinC.Info.Path!, new Point3(40, 20, 0));
        AsmFront.Visible.Add(new VisibleEntity(Asm.Asm.AddEntity("pin-2", ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(40, 20, 30), Axis = new Vector3(0, 0, 1), RadiusMm = 5 }), false, false));
        Drw.BalloonsEveryInstance = true;
        var e = Fails(() => Balloons());
        Assert.Equal("balloon_count", e.Error.Details!["check"]);
        Assert.DoesNotContain(Drw.Annotated, a => a.Kind == AnnotationKind.Balloon);
        Assert.NotNull(pin2);
    }

    [Fact]
    public void A_balloon_attached_to_nothing_is_refused()
    {
        Drw.BalloonsUnattached = true;
        Assert.Equal("balloon_component", Fails(() => Balloons()).Error.Details!["check"]);
    }

    [Fact]
    public void Balloons_SOLIDWORKS_does_not_make_are_operation_failed()
    {
        Drw.BalloonsFail = true;
        var e = Fails(() => Balloons());
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Contains("made no balloons", e.Message);
    }

    [Fact]
    public void A_view_of_a_part_is_refused()
    {
        var part = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        var e = Fails(() => Balloons(part.Facts.Name));
        Assert.Equal("/view", e.Error.Details!["field"]);
        Assert.DoesNotContain(Drw.Calls, c => c.StartsWith("balloons"));
    }
}
