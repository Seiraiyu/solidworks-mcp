using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class TableReadDeleteTests : AssemblyDrawingTestBase
{
    readonly TableFacts _bom;

    public TableReadDeleteTests()
    {
        _bom = Bom();
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        Drw.Active = "Sheet1";
        // A general table the user made on Sheet2, read and deleted like any other.
        Drw.Tabled.Add(new TableFacts { Name = "General Table1", Sheet = "Sheet2", Columns = { "A", "B" }, Rows = { new TableRow { Cells = { "x", "y" } } } });
    }

    TableReadResult Read(string? table = null, string? sheet = null) =>
        Run(new TableReadOperation(Services), new TableReadRequest { Table = table, Sheet = sheet });

    TableDeleteResult Delete(params string[] tables) =>
        Run(new TableDeleteOperation(Services), new TableDeleteRequest { Tables = tables.ToList() });

    [Fact]
    public void Every_table_is_read_with_its_rows_and_a_bill_with_what_each_row_lists()
    {
        var all = Read().Tables;
        Assert.Equal(new[] { _bom.Name, "General Table1" }, all.Select(t => t.Name));
        var bom = all[0];
        Assert.Equal(TableKind.Bom, bom.Kind);
        Assert.Equal(new[] { "ITEM NO.", "PART NUMBER", "DESCRIPTION", "QTY." }, bom.Columns);
        Assert.Equal(PinC.Info.Path, bom.Rows[1].Path);
        Assert.Equal(new[] { "2", "pin", "", "1" }, bom.Rows[1].Cells);
        var general = all[1];
        Assert.Equal((TableKind.Other, "Sheet2"), (general.Kind, general.Sheet));
        Assert.Empty(general.Rows[0].Components);
    }

    [Fact]
    public void One_table_is_read_by_reference_or_a_sheets_by_sheet()
    {
        string reference = Services.References.IssueTable(Doc, _bom);
        Assert.Equal(_bom.Name, Assert.Single(Read(reference).Tables).Name);
        Assert.Equal("General Table1", Assert.Single(Read(sheet: "Sheet2").Tables).Name);
        Assert.Equal("/sheet", Fails(() => Read(reference, "Sheet2")).Error.Details!["field"]);
    }

    [Fact]
    public void A_read_that_changes_under_it_fails_verification()
    {
        IOperation o = new TableReadOperation(Services);
        var request = new TableReadRequest();
        var result = (TableReadResult)o.Run(request, new TestContext());
        Drw.Tabled.RemoveAt(1);
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => o.Verify(request, result, new TestContext())).Code);
    }

    [Fact]
    public void Tables_on_two_sheets_are_deleted_and_the_rest_kept()
    {
        var result = Delete(_bom.Name, "General Table1@Sheet2");
        Assert.Equal(new[] { _bom.Name + "@Sheet1", "General Table1@Sheet2" }, result.Deleted);
        Assert.Empty(Drw.Tabled);
        Assert.Equal((2, 0), (result.Change.TablesBefore, result.Change.TablesAfter));
        Assert.Equal(new[] { "deleted", "deleted" }, result.Change.Changed.Select(c => c.Property));
    }

    [Fact]
    public void A_deleted_bill_no_longer_resolves()
    {
        string reference = Services.References.IssueTable(Doc, _bom);
        Delete(reference);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Read(reference)).Code);
        Assert.Equal("General Table1", Assert.Single(Read().Tables).Name);
    }

    [Fact]
    public void Naming_one_twice_or_none_is_refused()
    {
        Assert.Equal("/tables/1", Fails(() => Delete(_bom.Name, _bom.Name)).Error.Details!["field"]);
        Assert.Equal("/tables", Fails(() => Delete()).Error.Details!["field"]);
        Assert.Equal(2, Drw.Tabled.Count);
    }

    [Fact]
    public void A_table_SOLIDWORKS_leaves_fails_the_check_saying_so()
    {
        Drw.TableDeleteLeaves = true;
        var e = Fails(() => Delete(_bom.Name));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("tables", e.Error.Details!["check"]);
        Assert.Contains(_bom.Name + "@Sheet1", e.Message);
    }

    [Fact]
    public void A_second_sheet_refused_after_the_first_was_deleted_says_what_cannot_come_back()
    {
        Drw.TableDeleteFailsOn = "Sheet2";
        var e = Fails(() => Delete(_bom.Name, "General Table1"));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Contains(_bom.Name + "@Sheet1 was deleted and cannot be put back", e.Message);
        Assert.Equal("General Table1", Assert.Single(Drw.Tabled).Name);
    }

    [Fact]
    public void A_delete_SOLIDWORKS_refuses_is_operation_failed_with_nothing_deleted()
    {
        Drw.TableDeleteFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Delete(_bom.Name)).Code);
        Assert.Equal(2, Drw.Tabled.Count);
    }
}
