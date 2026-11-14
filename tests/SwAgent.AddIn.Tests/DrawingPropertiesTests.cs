using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>The drawing has a Title already.</summary>
public class DrawingPropertiesTests : DrawingTestBase
{
    public DrawingPropertiesTests()
    {
        Drw.Props["Title"] = "OLD";
    }

    DrawingPropertiesResult Properties(params (string Name, string Value)[] set) =>
        Run(new DrawingPropertiesOperation(Services), new DrawingPropertiesRequest
        {
            Set = set.Length == 0 ? null : set.Select(p => new DrawingPropertyValue { Name = p.Name, Value = p.Value }).ToList(),
        });

    [Fact]
    public void Reading_writes_nothing()
    {
        var r = Properties();
        Assert.Equal("OLD", Assert.Single(r.Properties).Value);
        Assert.Empty(r.Written);
        Assert.Empty(r.Change.Changed);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void Title_and_part_number_are_written_and_a_linked_description_reads_resolved()
    {
        var r = Properties(("Title", "BLOCK"), ("PartNo", "BLK-001"), ("Description", "$PRP:\"PartNo\" block"));
        Assert.Equal(new[] { "Title", "PartNo", "Description" }, r.Written);
        Assert.Equal("BLOCK", r.Properties.Single(p => p.Name == "Title").Resolved);
        var description = r.Properties.Single(p => p.Name == "Description");
        Assert.Equal("$PRP:\"PartNo\" block", description.Value);
        Assert.Equal("BLK-001 block", description.Resolved);
        var title = r.Change.Changed.Single(c => c.Name == "Title");
        Assert.Equal(("OLD", "BLOCK"), (title.Before, title.After));
        Assert.Equal("", r.Change.Changed.Single(c => c.Name == "PartNo").Before);
    }

    [Fact]
    public void A_name_differing_only_in_case_overwrites_the_one_the_drawing_has()
    {
        var r = Properties(("title", "BLOCK"));
        Assert.Equal(new[] { "Title" }, r.Written);
        Assert.Equal("BLOCK", Drw.Props["Title"]);
        Assert.Single(Drw.Props);
    }

    [Fact]
    public void A_property_SOLIDWORKS_refuses_puts_the_old_values_back_and_deletes_the_new_ones()
    {
        Drw.PropertyRefused = "Material";
        var e = Fails(() => Properties(("Title", "BLOCK"), ("PartNo", "BLK-001"), ("Material", "6082")));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("OLD", Drw.Props["Title"]);
        Assert.False(Drw.Props.ContainsKey("PartNo"));
    }

    [Fact]
    public void A_value_that_does_not_read_back_is_refused()
    {
        Drw.PropertiesIgnored = true;
        var e = Fails(() => Properties(("Title", "BLOCK")));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("Title", e.Error.Details!["property"]);
    }

    [Fact]
    public void Names_that_are_empty_or_repeated_are_refused()
    {
        Assert.Equal("/set/0/name", Fails(() => Properties((" ", "X"))).Error.Details!["field"]);
        Assert.Equal("/set/1/name", Fails(() => Properties(("PartNo", "A"), ("partno", "B"))).Error.Details!["field"]);
        Assert.Empty(Drw.Calls);
    }

    [Fact]
    public void A_part_is_refused_naming_the_drawing_tools()
    {
        Sw.Track(Block);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new DrawingPropertiesOperation(Services), new DrawingPropertiesRequest { Document = Block.Document.Path })).Code);
    }
}
