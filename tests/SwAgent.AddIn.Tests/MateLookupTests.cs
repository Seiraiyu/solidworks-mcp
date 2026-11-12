using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class MateLookupTests : MateTestBase
{
    [Fact]
    public void A_mate_is_found_by_name_and_by_reference()
    {
        var made = Asm.AddMate(new MateSpec { Type = MateType.Coincident, Entities = new object[] { PinTop, PlateTop } }, out _)!;
        Assert.Same(made, MateLookup.Find(Services.References, Doc, Asm, "coincident1", "/mate"));
        string reference = Services.References.Issue(Doc, made, Doc.Describe(made))!;
        Assert.StartsWith("r1.mate.", reference);
        Assert.Same(made, MateLookup.Find(Services.References, Doc, Asm, reference, "/mate"));
    }

    [Fact]
    public void An_unknown_mate_names_the_field_and_lists_the_mates()
    {
        Asm.AddMate(new MateSpec { Type = MateType.Coincident, Entities = new object[] { PinTop, PlateTop } }, out _);
        var e = Fails(() => MateLookup.Find(Services.References, Doc, Asm, "Distance9", "/mates/1"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/mates/1", e.Error.Details!["field"]);
        Assert.Contains("Coincident1", (System.Collections.Generic.List<object?>)e.Error.Details["mates"]!);
    }

    [Fact]
    public void A_deleted_mates_reference_is_unresolved()
    {
        var made = Asm.AddMate(new MateSpec { Type = MateType.Coincident, Entities = new object[] { PinTop, PlateTop } }, out _)!;
        string reference = Services.References.Issue(Doc, made, Doc.Describe(made))!;
        Asm.DeleteMates(new[] { made });
        Assert.Equal(ErrorCode.ReferenceUnresolved, Fails(() => MateLookup.Find(Services.References, Doc, Asm, reference, "/mate")).Code);
    }

    [Fact]
    public void A_side_resolves_with_its_component_and_a_lock_side_is_a_component()
    {
        var face = MateEntities.Resolve(Services.References, Doc, Asm, MateType.Coincident, Side(Ref(PinTop)), 0);
        Assert.Equal("pin-1", face.Info.Component);
        Assert.Equal(GeometryType.Plane, face.Info.Geometry);
        var part = MateEntities.Resolve(Services.References, Doc, Asm, MateType.Lock, Side("pin-1"), 1);
        Assert.Equal(ReferenceKind.Component, part.Info.Kind);
        Assert.Equal("pin-1", part.Info.Component);
    }

    [Fact]
    public void A_lock_side_with_a_component_field_is_refused()
    {
        var e = Fails(() => MateEntities.Resolve(Services.References, Doc, Asm, MateType.Lock, Side("pin-1", "plate-1"), 1));
        Assert.Equal("/entities/1/component", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_component_is_not_a_face()
    {
        var e = Fails(() => MateEntities.Resolve(Services.References, Doc, Asm, MateType.Coincident, Side(Services.References.Issue(Doc, Pin.Entity, Doc.Describe(Pin.Entity))!), 0));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/entities/0/entity", e.Error.Details!["field"]);
    }
}
