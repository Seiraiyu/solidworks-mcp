using System;
using System.Linq;
using SwAgent.Contracts.Serialization;
using Xunit;

namespace SwAgent.Contracts.Tests;

/// <summary>Plan 4.B: references to entities of components, issued in an assembly, carry the component they belong to.</summary>
public class AssemblyReferenceContractTests
{
    static byte[] LikeSolidWorks() => Enumerable.Range(0, 620).Select(i => (byte)(i % 16 == 0 ? i / 16 : i % 4 == 0 ? 0xFF : 0)).ToArray();

    [Fact]
    public void A_component_entity_reference_round_trips_with_its_component_and_file()
    {
        var data = new ReferenceData(ReferenceKind.Face, @"C:\w\stand.SLDASM", new byte[] { 1, 2, 3 }, "plane face of Boss-Extrude1 in sub-1/pin-1, 100 mm²",
            "sub-1/pin-1", @"C:\w\pin.SLDPRT");
        var back = References.Decode(References.Encode(data), "a");
        Assert.Equal("sub-1/pin-1", back.Component);
        Assert.Equal(@"C:\w\pin.SLDPRT", back.ComponentPath);
        Assert.Equal(data.Description, back.Description);
        Assert.Equal(data.PersistId, back.PersistId);
    }

    [Fact]
    public void A_long_component_entity_reference_is_compressed_and_round_trips()
    {
        var data = new ReferenceData(ReferenceKind.Edge, @"C:\w\stand.SLDASM", LikeSolidWorks(), "circle edge of Cut-Extrude1 in plate-1", "plate-1", @"C:\w\plate.SLDPRT");
        string text = References.Encode(data);
        Assert.True(text.Length < 400, $"{text.Length} characters");
        var back = References.Decode(text, "a");
        Assert.Equal("plate-1", back.Component);
        Assert.Equal(@"C:\w\plate.SLDPRT", back.ComponentPath);
    }

    [Fact]
    public void A_parts_reference_carries_no_component_and_keeps_its_old_payload()
    {
        var data = new ReferenceData(ReferenceKind.Face, @"C:\w\pin.SLDPRT", new byte[] { 1, 2, 3 }, "a face");
        string text = References.Encode(data);
        Assert.Null(References.Decode(text, "a").Component);
        // Version 1 (plain, three blocks): a part's reference reads exactly as 4.A's add-in issued it.
        string payload = text.Substring("r1.face.".Length).Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        Assert.Equal(1, Convert.FromBase64String(payload)[0]);
    }

    [Fact]
    public void A_component_without_a_file_round_trips_with_no_path()
    {
        var back = References.Decode(References.Encode(new ReferenceData(ReferenceKind.Plane, "Assem1", new byte[] { 7 }, "plane Front Plane in virtual-1", "virtual-1")), "a");
        Assert.Equal("virtual-1", back.Component);
        Assert.Null(back.ComponentPath);
    }

    [Fact]
    public void An_entity_describes_its_component()
    {
        var info = new EntityInfo { Kind = ReferenceKind.Face, Geometry = GeometryType.Plane, Component = "bracket-1" };
        var back = JsonCodec.Deserialize<EntityInfo>(JsonCodec.Serialize(info));
        Assert.Equal("bracket-1", back.Component);
        Assert.Contains("\"component\":\"bracket-1\"", JsonCodec.Serialize(info));
    }
}
