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

    [Theory]
    [InlineData(Constraint.FullyDefined, "fully_defined")]
    [InlineData(Constraint.UnderDefined, "under_defined")]
    [InlineData(Constraint.OverDefined, "over_defined")]
    public void Constraints_have_snake_case_wire_names(Constraint constraint, string wire)
    {
        Assert.Equal(wire, WireNames.ForEnum(constraint));
    }

    [Fact]
    public void A_component_round_trips_with_its_transform_box_and_constraint()
    {
        var component = new ComponentInfo
        {
            Name = "sub-1/pin-1",
            Path = @"C:\w\pin.SLDPRT",
            Configuration = "Default",
            State = ComponentState.Resolved,
            Transform = new Transform { Origin = new Point3(5, 0, 0) },
            Box = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(10, 10, 30) },
            Subassembly = false,
            Constraint = Constraint.UnderDefined,
            Parent = "sub-1",
            Mates = 2,
        };
        string json = JsonCodec.Serialize(component);
        Assert.Contains("\"constraint\":\"under_defined\"", json);
        Assert.DoesNotContain("solve_as", json);
        var back = JsonCodec.Deserialize<ComponentInfo>(json);
        Assert.Equal(json, JsonCodec.Serialize(back));
    }

    [Fact]
    public void The_assembly_evidence_block_round_trips()
    {
        var change = new AssemblyChange
        {
            Document = new DocumentInfo { Title = "stand.SLDASM", Type = DocumentType.Assembly },
            ComponentsBefore = 1,
            ComponentsAfter = 2,
            Moved = { new MovedComponent { Name = "pin-1", Before = new Point3(0, 0, 0), After = new Point3(0, 0, 12), Rotated = true } },
            Constraints = { new ComponentConstraint { Name = "pin-1", Constraint = Constraint.UnderDefined } },
        };
        string json = JsonCodec.Serialize(change);
        Assert.Equal(json, JsonCodec.Serialize(JsonCodec.Deserialize<AssemblyChange>(json)));
    }

    [Fact]
    public void Protocol_7_speaks_assemblies()
    {
        Assert.Equal(7, Protocol.Version);
        Assert.Equal(ReferenceKind.Component, References.KindOf(References.Encode(new ReferenceData(ReferenceKind.Component, "a.SLDASM", new byte[] { 1 }, "bracket-1"))));
        Assert.Equal(ReferenceKind.Mate, References.KindOf(References.Encode(new ReferenceData(ReferenceKind.Mate, "a.SLDASM", new byte[] { 1 }, "Coincident1"))));
    }
}
