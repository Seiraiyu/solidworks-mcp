using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class TableBomTests : AssemblyDrawingTestBase
{
    TableResult Bom(BomKind? kind = null, SheetPoint? at = null, string? view = null) =>
        Run(new TableBomOperation(Services), new TableBomRequest { View = view ?? AsmFront.Facts.Name, Kind = kind, At = at });

    [Fact]
    public void A_top_level_bill_lists_the_block_and_the_pin_once_each()
    {
        var result = Bom();
        var t = result.Table;
        Assert.Equal(TableKind.Bom, t.Kind);
        Assert.Equal(BomKind.TopLevel, t.BomKind);
        Assert.Equal("Default", t.Configuration);
        Assert.StartsWith("r1.table.", t.Reference);
        Assert.Equal(new[] { "1", "2" }, t.Rows.Select(r => r.ItemNumber));
        Assert.Equal(new[] { "block-1", "pin-1" }, t.Rows.SelectMany(r => r.Components));
        Assert.Equal(new[] { "block.SLDPRT (Default) x1", "pin.SLDPRT (Default) x1" }, result.Expected);
        Assert.Equal(ReferenceKind.Table, Assert.Single(result.Change.Created).Kind);
        Assert.Equal((0, 1), (result.Change.TablesBefore, result.Change.TablesAfter));
        Assert.Contains($"bom {AsmFront.Facts.Name} top_level Default", Drw.Calls);
    }

    [Fact]
    public void Two_pins_are_one_row_of_two()
    {
        Asm.Asm!.AddComponent("pin-2", PinC.Info.Path!, new Point3(40, 20, 0));
        var pin = Bom().Table.Rows.Single(r => r.Path == PinC.Info.Path);
        Assert.Equal(2, pin.Quantity);
        Assert.Equal(new[] { "pin-1", "pin-2" }, pin.Components);
    }

    [Fact]
    public void The_kind_asked_is_made_and_put_at_the_point_asked()
    {
        var t = Bom(BomKind.PartsOnly, new SheetPoint(20, 280)).Table;
        Assert.Equal(BomKind.PartsOnly, t.BomKind);
        Assert.Equal(20, t.Position!.X, 9);
    }

    [Fact]
    public void A_view_of_a_part_or_a_point_off_the_sheet_is_refused_before_SOLIDWORKS_is_asked()
    {
        var part = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        var e = Fails(() => Bom(view: part.Facts.Name));
        Assert.Equal(("/view", ErrorCode.InvalidArgument), (e.Error.Details!["field"], e.Code));
        Assert.Contains("a part", e.Message);
        Assert.Equal("/at", Fails(() => Bom(at: new SheetPoint(500, 10))).Error.Details!["field"]);
        Assert.DoesNotContain(Drw.Calls, c => c.StartsWith("bom "));
    }

    [Fact]
    public void A_view_of_a_configuration_that_is_not_active_is_refused()
    {
        AsmFront.Facts.Configuration = "Long";
        var e = Fails(() => Bom());
        Assert.Equal("/view", e.Error.Details!["field"]);
        Assert.Contains("sw_configuration_activate", e.Message);
    }

    [Fact]
    public void A_bill_with_no_rows_is_refused_and_deleted()
    {
        Drw.BomHeaderOnly = true;
        var e = Fails(() => Bom());
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("rows", e.Error.Details!["check"]);
        Assert.Contains("no rows", e.Message);
        Assert.Empty(Drw.Tabled);
    }

    [Fact]
    public void A_wrong_quantity_is_refused_saying_both_and_the_bill_deleted()
    {
        Drw.BomQuantityOffBy = 1;
        var e = Fails(() => Bom());
        Assert.Equal("rows", e.Error.Details!["check"]);
        Assert.Contains("missing block.SLDPRT (Default) x1", e.Message);
        Assert.Empty(Drw.Tabled);
    }

    [Fact]
    public void A_bill_SOLIDWORKS_does_not_make_is_operation_failed()
    {
        Drw.BomFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Bom()).Code);
        Assert.Empty(Drw.Tabled);
    }

    [Fact]
    public void A_bill_that_marks_the_assembly_as_changed_is_rolled_back()
    {
        Drw.BomDirtiesModel = true;
        var e = Fails(() => Bom());
        Assert.Equal("models", e.Error.Details!["check"]);
        Assert.Empty(Drw.Tabled);
    }
}
