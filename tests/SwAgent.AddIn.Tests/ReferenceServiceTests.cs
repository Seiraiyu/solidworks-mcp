using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ReferenceServiceTests
{
    readonly RenameMap _renames = new RenameMap();
    readonly ReferenceService _refs;
    readonly FakeDocument _part = FakeDocument.Part("Part1");
    readonly FakeEntity _top;

    public ReferenceServiceTests()
    {
        _refs = new ReferenceService(_renames);
        _top = _part.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 800, Normal = new Vector3(0, 0, 1) });
    }

    [Fact]
    public void A_reference_issued_for_an_entity_resolves_back_to_it()
    {
        var info = _refs.Describe(_part, _top);
        Assert.StartsWith("r1.face.", info.Reference);
        Assert.Equal("plane face of Boss-Extrude1, 800 mm²", References.Decode(info.Reference!, "f").Description);
        Assert.Equal("Part1", References.Decode(info.Reference!, "f").DocumentKey);
        var (entity, kind) = _refs.Resolve(_part, info.Reference!, "face", ReferenceKind.Face);
        Assert.Same(_top, entity);
        Assert.Equal(ReferenceKind.Face, kind);
    }

    [Fact]
    public void No_persistent_id_means_no_reference()
    {
        _top.Id = null;
        Assert.Null(_refs.Describe(_part, _top).Reference);
    }

    [Theory]
    [InlineData("front", "Ebene vorne")]
    [InlineData("TOP", "Ebene oben")]
    [InlineData("right", "Ebene rechts")]
    [InlineData("origin", "Ursprung")]
    [InlineData("Ebene oben", "Ebene oben")]
    public void Standard_names_resolve_by_position_whatever_the_template_language(string name, string expected)
    {
        var (entity, _) = _refs.Resolve(_part, name, "on");
        Assert.Equal(expected, ((FakeEntity)entity).Info.Name);
    }

    [Fact]
    public void An_unknown_name_is_invalid_argument()
    {
        var e = Assert.Throws<SwAgentException>(() => _refs.Resolve(_part, "Boss-Extrude9", "feature"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("feature", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_kind_not_accepted_is_invalid_argument()
    {
        var e = Assert.Throws<SwAgentException>(() => _refs.Resolve(_part, "front", "edges/0", ReferenceKind.Edge));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("plane", e.Error.Details!["kind"]);
    }

    [Theory]
    [InlineData(PersistState.Deleted, "deleted")]
    [InlineData(PersistState.Suppressed, "suppressed")]
    [InlineData(PersistState.Invalid, "not_found")]
    public void A_reference_that_no_longer_finds_its_entity_is_unresolved_with_what_it_was(PersistState state, string reason)
    {
        string reference = _refs.Describe(_part, _top).Reference!;
        _top.State = state;
        var e = Assert.Throws<SwAgentException>(() => _refs.Resolve(_part, reference, "faces/0"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal(reason, e.Error.Details!["reason"]);
        Assert.Equal("plane face of Boss-Extrude1, 800 mm²", e.Error.Details["last_was"]);
        Assert.Equal("face", e.Error.Details["kind"]);
        Assert.Equal("faces/0", e.Error.Details["field"]);
    }

    [Fact]
    public void A_reference_never_resolves_to_something_of_another_kind()
    {
        string reference = _refs.Describe(_part, _top).Reference!;
        _top.ResolvesTo = _part.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line });
        var e = Assert.Throws<SwAgentException>(() => _refs.Resolve(_part, reference, "face"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("changed_kind", e.Error.Details!["reason"]);
    }

    [Fact]
    public void A_reference_from_another_document_is_unresolved()
    {
        string reference = _refs.Describe(_part, _top).Reference!;
        var other = FakeDocument.Part("Part2");
        var e = Assert.Throws<SwAgentException>(() => _refs.Resolve(other, reference, "face"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("other_document", e.Error.Details!["reason"]);
    }

    [Fact]
    public void A_reference_follows_its_document_saved_under_a_new_name()
    {
        string reference = _refs.Describe(_part, _top).Reference!;
        _renames.Record("Part1", @"C:\parts\bracket.SLDPRT");
        var saved = new FakeDocument("bracket.SLDPRT", @"C:\parts\bracket.SLDPRT");
        saved.Entities.AddRange(_part.Entities);
        Assert.Same(_top, _refs.Resolve(saved, reference, "face").Entity);
    }
}
