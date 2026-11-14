using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Design 611, Error handling: every code the assembly-drawing operations of plan 5.D define is reachable.</summary>
public class AssemblyDrawingErrorCodeTests : AssemblyDrawingTestBase
{
    readonly TableFacts _bom;

    public AssemblyDrawingErrorCodeTests()
    {
        _bom = Bom();
    }

    /// <summary>One request of every plan 5.D operation (and the exploded forms of view.create and view.set), on <paramref name="document"/> (the active document when null).</summary>
    IEnumerable<(string Name, Action Call)> Every(string? document) => new (string, Action)[]
    {
        ("view.create", () => Run(new ViewCreateOperation(Services), new ViewCreateRequest { Document = document, Model = Asm.Document.Path!, Orientation = "front", At = new SheetPoint(250, 150), Exploded = true })),
        ("view.set", () => Run(new ViewSetOperation(Services), new ViewSetRequest { Document = document, View = AsmFront.Facts.Name, Exploded = true })),
        ("view.entities", () => Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { Document = document, View = AsmFront.Facts.Name, Component = "pin-1" })),
        ("table.bom", () => Run(new TableBomOperation(Services), new TableBomRequest { Document = document, View = AsmFront.Facts.Name })),
        ("table.read", () => Run(new TableReadOperation(Services), new TableReadRequest { Document = document })),
        ("table.delete", () => Run(new TableDeleteOperation(Services), new TableDeleteRequest { Document = document, Tables = { _bom.Name } })),
        ("annotation.balloons", () => Run(new AnnotationBalloonsOperation(Services), new AnnotationBalloonsRequest { Document = document, View = AsmFront.Facts.Name })),
    };

    Action Call(string name) => Every(null).Single(c => c.Name == name).Call;

    static readonly string[] ReadOnly = { "view.entities", "table.read" };

    [Fact]
    public void Wrong_document_type_for_every_operation_on_a_part_naming_the_drawing_tools()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        foreach (var (name, call) in Every("plate.SLDPRT"))
        {
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.WrongDocumentType, $"{name}: {e.Code}");
            Assert.Contains("document.new", (string)e.Error.Details!["instead"]!);
        }
    }

    [Fact]
    public void Busy_for_every_change_while_the_user_edits_the_sheet_format_and_reads_still_read()
    {
        Drw.EditingFormat = true;
        foreach (var (name, call) in Every(null).Where(c => !ReadOnly.Contains(c.Name)))
        {
            var e = Fails(call);
            Assert.True(e.Code == ErrorCode.Busy, $"{name}: {e.Code}");
            Assert.Equal(true, e.Error.Details!["sheet_format"]);
        }
        Assert.Empty(Drw.Calls);
        Assert.Single(Run(new TableReadOperation(Services), new TableReadRequest()).Tables);
        Assert.Single(Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = AsmFront.Facts.Name, Component = "pin-1" }).Entities);
    }

    [Fact]
    public void Reference_unresolved_for_a_table_or_a_view_that_is_gone()
    {
        string table = Services.References.IssueTable(Doc, _bom);
        Drw.DeleteTables("Sheet1", new[] { _bom.Name });
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new TableReadOperation(Services), new TableReadRequest { Table = table })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new TableDeleteOperation(Services), new TableDeleteRequest { Tables = { table } })).Code);
        string view = Services.References.IssueView(Doc, Drw.ReadView(AsmFront.Facts.Name)!);
        Drw.DeleteViews(new[] { AsmFront.Facts.Name });
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new TableBomOperation(Services), new TableBomRequest { View = view })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new AnnotationBalloonsOperation(Services), new AnnotationBalloonsRequest { View = view })).Code);
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = view })).Code);
    }

    [Fact]
    public void Operation_failed_when_SOLIDWORKS_makes_shows_or_deletes_nothing()
    {
        Drw.BomFails = true;
        Drw.BalloonsFail = true;
        Drw.TableDeleteFails = true;
        foreach (var name in new[] { "table.bom", "table.delete", "annotation.balloons" })
        {
            var e = Fails(Call(name));
            Assert.True(e.Code == ErrorCode.OperationFailed, $"{name}: {e.Code}");
        }
    }

    [Theory]
    [InlineData("view.create")]
    [InlineData("view.set")]
    [InlineData("table.bom")]
    [InlineData("table.delete")]
    [InlineData("annotation.balloons")]
    public void Verification_failed_when_SOLIDWORKS_answers_but_the_drawing_says_otherwise(string name)
    {
        switch (name)
        {
            case "table.bom": Drw.BomQuantityOffBy = 1; break;
            case "table.delete": Drw.TableDeleteLeaves = true; break;
            case "annotation.balloons": Drw.BalloonsMisnumbered = true; break;
            default: Drw.ImagesOffByMm = 5; break;
        }
        var e = Fails(Call(name));
        Assert.True(e.Code == ErrorCode.VerificationFailed, $"{name}: {e.Code}");
    }

    [Theory]
    [InlineData("view.create", "/exploded")]
    [InlineData("view.set", "/exploded")]
    [InlineData("table.bom", "/view")]
    [InlineData("annotation.balloons", "/view")]
    [InlineData("view.entities", "/component")]
    public void Invalid_argument_naming_the_field_for_a_view_of_a_part(string name, string field)
    {
        var part = Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        string v = part.Facts.Name;
        Action call = name switch
        {
            "view.create" => () => Run(new ViewCreateOperation(Services), new ViewCreateRequest { Model = Block.Document.Path!, Orientation = "front", At = new SheetPoint(100, 150), Exploded = true }),
            "view.set" => () => Run(new ViewSetOperation(Services), new ViewSetRequest { View = v, Exploded = true }),
            "table.bom" => () => Run(new TableBomOperation(Services), new TableBomRequest { View = v }),
            "annotation.balloons" => () => Run(new AnnotationBalloonsOperation(Services), new AnnotationBalloonsRequest { View = v }),
            _ => () => Run(new ViewEntitiesOperation(Services), new ViewEntitiesRequest { View = v, Component = "pin-1" }),
        };
        var e = Fails(call);
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
    }
}
