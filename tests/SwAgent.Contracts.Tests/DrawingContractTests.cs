using System;
using System.Text;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class DrawingContractTests
{
    [Fact]
    public void Protocol_13_speaks_drawings()
    {
        Assert.Equal(13, Protocol.Version);
    }

    [Theory]
    [InlineData(ReferenceKind.Sheet, "r1.sheet.", "Sheet1")]
    [InlineData(ReferenceKind.View, "r1.view.", "Drawing View1")]
    [InlineData(ReferenceKind.Annotation, "r1.annotation.", "Note1")]
    [InlineData(ReferenceKind.Table, "r1.table.", "Bill of Materials1")]
    public void Drawing_kinds_carry_a_name_as_their_persistent_id(ReferenceKind kind, string prefix, string name)
    {
        string text = References.Encode(new ReferenceData(kind, @"C:\d\block.SLDDRW", Encoding.UTF8.GetBytes(name), $"{WireNames.ForEnum(kind)} {name}"));
        Assert.StartsWith(prefix, text);
        var back = References.Decode(text, "view");
        Assert.Equal(kind, back.Kind);
        Assert.Equal(name, Encoding.UTF8.GetString(back.PersistId));
    }

    [Theory]
    [InlineData("drawing", 60)]
    [InlineData("sheet", 30)]
    [InlineData("view", 60)]
    [InlineData("annotation", 60)]
    [InlineData("table", 60)]
    public void Drawing_families_have_their_designed_deadlines(string family, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), OperationFamilies.DefaultDeadline(family));
    }
}
