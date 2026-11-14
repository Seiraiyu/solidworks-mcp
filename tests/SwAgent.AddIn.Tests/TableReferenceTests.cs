using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

[Operation("test.table_change", typeof(TestTableChangeResult))]
public sealed class TestTableChangeRequest
{
    /// <summary>Where to fail: check (after the bill is made), or dirty (the assembly marked changed).</summary>
    public string? FailIn { get; set; }
}

public sealed class TestTableChangeResult : IDrawingChangeResult
{
    public TableInfo? Table { get; set; }

    public DrawingChange Change { get; set; } = new DrawingChange();
}

/// <summary>Puts a bill of materials on the assembly's front view, records it, and fails where it is told to.</summary>
sealed class TestTableChangeOperation : DrawingChangeOperation<TestTableChangeRequest, TestTableChangeResult>
{
    public TestTableChangeOperation(AddInServices services)
        : base(services)
    {
    }

    protected override string? DocumentOf(TestTableChangeRequest request) => null;

    protected override TestTableChangeResult Change(ISwDocument doc, ISwDrawing drawing, TestTableChangeRequest request, ModelTransaction tx, IOperationContext context)
    {
        var view = drawing.SheetNames().SelectMany(drawing.ViewNames).Single(v => drawing.ModelOf(v)?.Assembly() != null);
        string name = drawing.InsertBom(view, BomKind.TopLevel, "Default", null, TableAnchor.TopLeft)!;
        var made = DrawingTables.Read(drawing, "Sheet1", name)!;
        MadeTable(doc, drawing, tx, made);
        if (request.FailIn == "dirty") ((FakeDocument)drawing.ModelOf(view)!).Document.Dirty = true;
        return new TestTableChangeResult { Table = DescribeTable(doc, made) };
    }

    protected override void Check(ISwDocument doc, ISwDrawing drawing, TestTableChangeRequest request, TestTableChangeResult result)
    {
        if (request.FailIn == "check") throw new SwAgentException(ErrorCode.VerificationFailed, "the bill has a row too many");
    }
}

public class TableReferenceTests : AssemblyDrawingTestBase
{
    [Fact]
    public void A_table_resolves_by_its_reference_its_name_or_name_at_sheet()
    {
        var bom = Bom();
        string reference = Services.References.IssueTable(Doc, bom);
        Assert.StartsWith("r1.table.", reference);
        Assert.Equal(bom.Name, Services.References.ResolveTable(Doc, Drw, reference, "/table").Name);
        Assert.Equal(bom.Name, Services.References.ResolveTable(Doc, Drw, bom.Name, "/table").Name);
        Assert.Equal(bom.Name, Services.References.ResolveTable(Doc, Drw, bom.Name + "@Sheet1", "/table").Name);
    }

    [Fact]
    public void A_name_two_sheets_share_needs_its_sheet()
    {
        var bom = Bom();
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 420, HeightMm = 297 });
        var copy = bom.Copy();
        copy.Sheet = "Sheet2";
        Drw.Tabled.Add(copy);
        var e = Assert.Throws<SwAgentException>(() => Services.References.ResolveTable(Doc, Drw, bom.Name, "/table"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("name@sheet", e.Message);
        Assert.Equal("Sheet2", Services.References.ResolveTable(Doc, Drw, bom.Name + "@Sheet2", "/table").Sheet);
    }

    [Fact]
    public void A_deleted_table_no_longer_resolves_and_another_kind_is_refused()
    {
        var bom = Bom();
        string reference = Services.References.IssueTable(Doc, bom);
        Drw.DeleteTables("Sheet1", new[] { bom.Name });
        var gone = Assert.Throws<SwAgentException>(() => Services.References.ResolveTable(Doc, Drw, reference, "/table"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, gone.Code);
        Assert.Equal("not_found", gone.Error.Details!["reason"]);
        string view = Services.References.IssueView(Doc, Drw.ReadView(AsmFront.Facts.Name)!);
        var wrong = Assert.Throws<SwAgentException>(() => Services.References.ResolveTable(Doc, Drw, view, "/table"));
        Assert.Equal(ErrorCode.InvalidArgument, wrong.Code);
        Assert.Contains("needs a table", wrong.Message);
    }

    [Fact]
    public void A_table_cannot_be_given_where_a_model_entity_is_needed()
    {
        string reference = Services.References.IssueTable(Doc, Bom());
        var e = Assert.Throws<SwAgentException>(() => Services.References.Resolve(Asm, reference, "/entity"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("table of a drawing", e.Message);
    }

    [Fact]
    public void A_change_counts_tables_before_and_after_and_reports_the_table_made()
    {
        var result = Run(new TestTableChangeOperation(Services), new TestTableChangeRequest());
        Assert.Equal((0, 1), (result.Change.TablesBefore, result.Change.TablesAfter));
        var made = Assert.Single(result.Change.Created);
        Assert.Equal(ReferenceKind.Table, made.Kind);
        Assert.StartsWith("r1.table.", made.Reference);
        Assert.Equal(TableKind.Bom, result.Table!.Kind);
        Assert.Equal(2, result.Table.Rows.Count);
        Assert.Equal(1, Drw.ReadView(AsmFront.Facts.Name)!.Tables);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("dirty")]
    public void A_table_made_by_a_change_that_fails_is_deleted_again(string failIn)
    {
        var e = Assert.Throws<SwAgentException>(() => Run(new TestTableChangeOperation(Services), new TestTableChangeRequest { FailIn = failIn }));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Empty(Drw.Tabled);
        Assert.Contains(Drw.Calls, c => c.StartsWith("delete tables "));
    }

    [Fact]
    public void A_table_SOLIDWORKS_will_not_delete_is_rollback_failed()
    {
        Drw.TableDeleteLeaves = true;
        var e = Assert.Throws<SwAgentException>(() => Run(new TestTableChangeOperation(Services), new TestTableChangeRequest { FailIn = "check" }));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal(true, e.Error.Details!["rollback_failed"]);
        Assert.Single(Drw.Tabled);
    }
}
