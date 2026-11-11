using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateAddTests : MateTestBase
{
    [Fact]
    public void Concentric_puts_the_pin_on_the_hole_axis_and_says_what_it_measured()
    {
        Asm.OnMate = (_, _) => Move(Pin, -40, 10, 0);
        var r = Add(MateType.Concentric, Side(Ref(PinSide)), Side(Ref(PlateHole)));
        Assert.Equal("Concentric1", r.Mate.Name);
        Assert.Equal(MateType.Concentric, r.Mate.Type);
        Assert.StartsWith("r1.mate.", r.Mate.Reference);
        Assert.Equal(new[] { "angle_deg", "axis_offset_mm" }, r.Measured.Select(m => m.Condition));
        Assert.All(r.Measured, m => Assert.Equal(0, m.Measured, 9));
        Assert.Equal("pin-1", Assert.Single(r.Change.Moved).Name);
        Assert.Equal(ReferenceKind.Mate, Assert.Single(r.Change.Created).Kind);
        Assert.Equal(1, r.Change.MatesAfter);
        Assert.Contains(r.Change.Constraints, c => c.Name == "pin-1");
    }

    [Fact]
    public void A_mate_that_does_not_hold_is_deleted_and_the_pin_put_back()
    {
        Asm.OnMate = (_, _) => Move(Pin, -40, 12, 0);
        var e = Fails(() => Add(MateType.Concentric, Side(Ref(PinSide)), Side(Ref(PlateHole))));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Equal("axis_offset_mm", e.Error.Details!["check"]);
        Assert.Equal(2.0, (double)e.Error.Details["measured"]!, 9);
        Assert.Contains("axis offset 2 mm, expected 0", e.Message);
        Assert.Empty(Asm.MateItems);
        Assert.Equal(60, Pin.Info.Transform.Origin.X, 9);
        Assert.Contains("pin-1", (System.Collections.Generic.List<object?>)e.Error.Details["positions_restored"]!);
    }

    [Fact]
    public void A_distance_mate_is_measured_as_the_offset_of_its_planes()
    {
        Asm.OnMate = (_, _) => Move(Block, 15, 0, 0);
        var r = Add(MateType.Distance, Side(Ref(BlockLeft)), Side(Ref(PlateLeft)), distance: 15);
        Assert.Equal(15, r.Measured.Single().Measured, 9);
        Assert.Equal(15, r.Mate.DistanceMm);
    }

    [Theory]
    [InlineData(MateType.Distance, null, null, "/distance_mm")]
    [InlineData(MateType.Coincident, 5.0, null, "/distance_mm")]
    [InlineData(MateType.Angle, null, null, "/angle_deg")]
    [InlineData(MateType.Angle, null, 200.0, "/angle_deg")]
    public void Values_belong_to_their_types(MateType type, double? distance, double? angle, string field)
    {
        var e = Fails(() => Add(type, Side(Ref(BlockLeft)), Side(Ref(PlateLeft)), distance, angle));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal(field, e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void A_pair_the_type_cannot_join_is_refused_before_SOLIDWORKS_is_asked()
    {
        var e = Fails(() => Add(MateType.Concentric, Side(Ref(PinTop)), Side(Ref(PlateTop))));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/entities", e.Error.Details!["field"]);
        Assert.Empty(Asm.Calls);
    }

    [Fact]
    public void Two_sides_of_one_component_are_refused()
    {
        var e = Fails(() => Add(MateType.Parallel, Side(Ref(PinTop)), Side(Ref(PinBottom))));
        Assert.Equal("/entities/1", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_mate_needs_exactly_two_sides()
    {
        var e = Fails(() => Run(new SwAgent.AddIn.Operations.MateAddOperation(Services), new MateAddRequest { Document = "stand.SLDASM", Type = MateType.Coincident, Entities = { Side(Ref(PinTop)) } }));
        Assert.Equal("/entities", e.Error.Details!["field"]);
    }

    [Fact]
    public void Lock_holds_the_pair_and_has_no_alignment()
    {
        var r = Add(MateType.Lock, Side("pin-1"), Side("plate-1"));
        Assert.Equal(new[] { "relative_offset_mm", "relative_turn_deg" }, r.Measured.Select(m => m.Condition));
        Assert.Null(r.Mate.Alignment);
        var e = Fails(() => Add(MateType.Lock, Side("block-1"), Side("plate-1"), alignment: MateAlignment.Aligned));
        Assert.Equal("/alignment", e.Error.Details!["field"]);
    }

    [Fact]
    public void SOLIDWORKS_refusing_is_operation_failed_with_its_error_name()
    {
        Asm.AddMateError = "swAddMateError_IncorrectSelections";
        var e = Fails(() => Add(MateType.Coincident, Side(Ref(PinBottom)), Side(Ref(PlateTop))));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Equal("swAddMateError_IncorrectSelections", e.Error.Details!["mate_error"]);
        Assert.Empty(Asm.MateItems);
    }

    [Fact]
    public void An_over_defining_mate_SOLIDWORKS_made_anyway_is_deleted_and_the_components_put_back()
    {
        Asm.AddMateError = "swAddMateError_OverDefinedAssembly";
        Asm.AddMateKeepsMate = true;
        Asm.OnMate = (_, _) => Move(Pin, 0, 0, 7);
        var e = Fails(() => Add(MateType.Coincident, Side(Ref(PinBottom)), Side(Ref(PlateTop))));
        Assert.Equal(ErrorCode.OperationFailed, e.Code);
        Assert.Empty(Asm.MateItems);
        Assert.Equal(0, Pin.Info.Transform.Origin.Z, 9);
        Assert.Contains("Coincident1", (System.Collections.Generic.List<object?>)e.Error.Details!["rolled_back"]!);
    }

    [Fact]
    public void A_newly_over_defined_component_is_rolled_back_by_verify()
    {
        Asm.OnMate = (a, _) => { Move(Pin, -40, 10, 0); Pin.Info.Constraint = Constraint.OverDefined; };
        var e = Fails(() => Add(MateType.Concentric, Side(Ref(PinSide)), Side(Ref(PlateHole))));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Contains("pin-1", (System.Collections.Generic.List<object?>)e.Error.Details!["over_defined"]!);
        Assert.Empty(Asm.MateItems);
    }

    [Fact]
    public void Busy_while_a_component_is_edited_in_context()
    {
        Asm.EditingTitle = "pin.SLDPRT";
        var e = Fails(() => Add(MateType.Coincident, Side(Ref(PinBottom)), Side(Ref(PlateTop))));
        Assert.Equal(ErrorCode.Busy, e.Code);
        Assert.Equal("user_editing", e.Error.Details!["reason"]);
    }

    [Fact]
    public void A_part_reference_with_its_component_mates_the_same_face()
    {
        var part = Sw.Track(FakeDocument.Part("pin.SLDPRT", PathOf("pin.SLDPRT")));
        part.Document.Visible = false;
        var own = part.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Origin = new Point3(60, 0, 15), Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        Pin.Model = part;
        Asm.OnMate = (_, m) => { Move(Pin, -40, 10, 0); foreach (var s in m.Sides.Where(s => s.Component == Pin)) s.Info.Origin = new Point3(20, 10, 15); };
        var r = Add(MateType.Concentric, Side(Services.References.Describe(part, own).Reference!, "pin-1"), Side(Ref(PlateHole)));
        Assert.Equal("pin-1", r.Mate.Entities[0].Component);
        Assert.Null(SwAgent.AddIn.Core.MateGeometry.Failing(r.Measured));
    }

    [Fact]
    public void A_name_already_taken_is_refused()
    {
        Asm.OnMate = (_, _) => Move(Block, 0, 0, 10);
        Add(MateType.Coincident, Side(Ref(BlockBottom)), Side(Ref(PlateTop)), name: "seat");
        Asm.OnMate = null;
        var e = Fails(() => Add(MateType.Parallel, Side(Ref(BlockFront)), Side(Ref(PlateFront)), name: "seat"));
        Assert.Equal("/name", e.Error.Details!["field"]);
    }
}
