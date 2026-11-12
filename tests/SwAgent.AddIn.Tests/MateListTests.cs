using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateListTests : MateTestBase
{
    MateListResult List(string? component = null) =>
        Run(new MateListOperation(Services), new MateListRequest { Document = "stand.SLDASM", Component = component });

    [Fact]
    public void Lists_every_mate_with_its_sides_and_filters_by_component()
    {
        Asm.OnMate = (_, _) => Move(Pin, -40, 10, 0);
        Add(MateType.Concentric, Side(Ref(PinSide)), Side(Ref(PlateHole)));
        Asm.OnMate = null;
        Add(MateType.Lock, Side("block-1"), Side("plate-1"));
        var all = List();
        Assert.Equal(new[] { "Concentric1", "Lock2" }, all.Mates.Select(m => m.Name));
        Assert.Equal(new[] { "pin-1", "plate-1" }, all.Mates[0].Entities.Select(e => e.Component));
        Assert.Equal(GeometryType.Cylinder, all.Mates[0].Entities[0].Geometry);
        Assert.All(all.Mates, m => Assert.StartsWith("r1.mate.", m.Reference));
        Assert.Equal("Lock2", Assert.Single(List("block-1").Mates).Name);
        Assert.Equal(2, List("plate-1").Mates.Count);
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new MateListOperation(Services), new MateListRequest { Document = "block.SLDPRT" })).Code);
    }
}
