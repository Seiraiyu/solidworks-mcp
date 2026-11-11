using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

public class AssemblyContractTests
{
    [Fact]
    public void A_document_is_visible_unless_it_says_otherwise()
    {
        Assert.True(new DocumentInfo().Visible);
        Assert.Contains("\"visible\":true", JsonCodec.Serialize(new DocumentInfo { Title = "Part1" }));
        var hidden = JsonCodec.Deserialize<DocumentInfo>(JsonCodec.Serialize(new DocumentInfo { Title = "pin.SLDPRT", Visible = false }));
        Assert.False(hidden.Visible);
    }

    [Fact]
    public void A_transform_defaults_to_the_assembly_axes_and_round_trips_in_mm()
    {
        var t = new Transform();
        Assert.Equal(1, t.XAxis.X);
        Assert.Equal(1, t.YAxis.Y);
        Assert.Equal(1, t.ZAxis.Z);
        var back = JsonCodec.Deserialize<Transform>(JsonCodec.Serialize(new Transform { Origin = new Point3(10, 20, 30.5) }));
        Assert.Equal(30.5, back.Origin.Z);
        Assert.Contains("\"x_axis\"", JsonCodec.Serialize(t));
    }

    [Fact]
    public void Protocol_5_speaks_assemblies()
    {
        Assert.Equal(5, Protocol.Version);
        Assert.Equal(ReferenceKind.Component, References.KindOf(References.Encode(new ReferenceData(ReferenceKind.Component, "a.SLDASM", new byte[] { 1 }, "bracket-1"))));
        Assert.Equal(ReferenceKind.Mate, References.KindOf(References.Encode(new ReferenceData(ReferenceKind.Mate, "a.SLDASM", new byte[] { 1 }, "Coincident1"))));
    }
}
