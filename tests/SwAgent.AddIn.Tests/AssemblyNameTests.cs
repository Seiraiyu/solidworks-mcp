using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: name@component and the {component, entity} pair, each mapped into the assembly (design 455, Assembly references).</summary>
public class AssemblyNameTests
{
    readonly FakeSolidWorks _sw = new FakeSolidWorks();
    readonly ReferenceService _refs = new ReferenceService(new RenameMap());
    readonly FakeDocument _doc;
    readonly FakeAssembly _asm;
    readonly FakeDocument _post;
    readonly FakeEntity _postTop;

    public AssemblyNameTests()
    {
        _doc = _sw.Track(FakeDocument.NewAssembly("stand.SLDASM", @"C:\w\stand.SLDASM"));
        _asm = _doc.Asm!;
        _post = _sw.Track(FakeDocument.Part("post.SLDPRT", @"C:\w\post.SLDPRT"));
        _post.Document.Visible = false;
        _postTop = _post.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 100, Normal = new Vector3(0, 0, 1) });
        _post.Add(ReferenceKind.Axis, new EntityInfo { Name = "Axis1", Geometry = GeometryType.Line, Axis = new Vector3(0, 0, 1) });
        _asm.AddComponent("base-1", @"C:\w\base.SLDPRT", fix: true);
        _asm.AddComponent("post-1", @"C:\w\post.SLDPRT", new Point3(100, 0, 0)).Model = _post;
        _asm.AddComponent("sub-1", @"C:\w\sub.SLDASM");
        _asm.AddComponent("sub-1/pin-1", @"C:\w\post.SLDPRT", parent: "sub-1").Model = _post;
    }

    SwAgentException Fails(System.Action action) => Assert.Throws<SwAgentException>(action);

    [Fact]
    public void Front_at_a_component_is_its_own_first_plane_in_the_assembly()
    {
        var (entity, kind) = _refs.Resolve(_doc, "front@post-1", "a");
        Assert.Equal(ReferenceKind.Plane, kind);
        Assert.Same(_asm.Named("post-1").Counterparts[(FakeEntity)_post.StandardPlane(0)!], entity);
        var info = _refs.Describe(_doc, entity);
        Assert.Equal("post-1", info.Component);
        Assert.Equal("post-1", References.Decode(info.Reference!, "a").Component);
    }

    [Fact]
    public void A_nested_components_axis_is_named_by_its_path()
    {
        var (entity, kind) = _refs.Resolve(_doc, "Axis1@sub-1/pin-1", "a", ReferenceKind.Axis);
        Assert.Equal(ReferenceKind.Axis, kind);
        Assert.Equal("sub-1/pin-1", _refs.Describe(_doc, entity).Component);
    }

    [Fact]
    public void A_plain_standard_name_in_an_assembly_is_the_assemblys_own()
    {
        var (entity, _) = _refs.Resolve(_doc, "top", "a");
        Assert.Same(_doc.StandardPlane(1), entity);
        Assert.Null(_refs.Describe(_doc, entity).Component);
    }

    [Fact]
    public void An_unknown_name_in_an_assembly_points_at_name_at_component()
    {
        var e = Fails(() => _refs.Resolve(_doc, "Boss-Extrude1", "a"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("front@bracket-1", e.Message);
    }

    [Fact]
    public void An_unknown_component_lists_the_components()
    {
        var e = Fails(() => _refs.Resolve(_doc, "front@bracket-9", "/entities/0"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("/entities/0", e.Error.Details!["field"]);
        Assert.NotNull(e.Error.Details["components"]);
    }

    [Theory]
    [InlineData("@post-1")]
    [InlineData("front@")]
    public void Half_a_name_at_component_is_refused(string name)
    {
        Assert.Equal(ErrorCode.InvalidArgument, Fails(() => _refs.Resolve(_doc, name, "a")).Code);
    }

    [Fact]
    public void Name_at_component_in_a_part_is_refused()
    {
        var e = Fails(() => _refs.Resolve(_post, "front@post-1", "a"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Contains("only an assembly", e.Message);
    }

    [Theory]
    [InlineData(ComponentState.Lightweight, "lightweight")]
    [InlineData(ComponentState.Suppressed, "suppressed")]
    [InlineData(ComponentState.Missing, "missing")]
    public void A_component_that_is_not_loaded_is_invalid_argument_naming_it(ComponentState state, string wire)
    {
        _asm.Named("post-1").Info.State = state;
        var e = Fails(() => _refs.Resolve(_doc, "front@post-1", "/entities/1"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("post-1", e.Error.Details!["component"]);
        Assert.Equal(wire, e.Error.Details["state"]);
        Assert.Equal("/entities/1", e.Error.Details["field"]);
        if (state != ComponentState.Missing) Assert.Contains("component.set", e.Message);
    }

    [Fact]
    public void A_part_reference_with_its_component_is_the_same_face_in_the_assembly()
    {
        string partRef = _refs.Describe(_post, _postTop).Reference!;
        var (entity, kind) = _refs.ResolveInComponent(_doc, partRef, "post-1", "a", "a_component", ReferenceKind.Face);
        Assert.Equal(ReferenceKind.Face, kind);
        Assert.Same(_asm.Named("post-1").Counterparts[_postTop], entity);
        var asmRef = _refs.Describe(_doc, entity).Reference!;
        Assert.Same(entity, _refs.Resolve(_doc, asmRef, "a").Entity);
        Assert.Equal(@"C:\w\stand.SLDASM", References.Decode(asmRef, "a").DocumentKey);
    }

    [Fact]
    public void A_name_with_its_component_is_that_components_own()
    {
        var (entity, _) = _refs.ResolveInComponent(_doc, "front", "post-1", "a", "a_component");
        Assert.Same(_asm.Named("post-1").Counterparts[(FakeEntity)_post.StandardPlane(0)!], entity);
    }

    [Fact]
    public void A_reference_from_another_file_is_not_mapped()
    {
        var other = _sw.Track(FakeDocument.Part("base.SLDPRT", @"C:\w\base.SLDPRT"));
        string baseRef = _refs.Describe(other, other.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane })).Reference!;
        var e = Fails(() => _refs.ResolveInComponent(_doc, baseRef, "post-1", "a", "a_component"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("other_document", e.Error.Details!["reason"]);
    }

    [Fact]
    public void A_component_in_a_part_is_refused_naming_the_component_field()
    {
        var e = Fails(() => _refs.ResolveInComponent(_post, "front", "post-1", "a", "a_component"));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("a_component", e.Error.Details!["field"]);
    }

    [Fact]
    public void No_counterpart_is_operation_failed()
    {
        _asm.CorrespondingFails = true;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => _refs.Resolve(_doc, "front@post-1", "a")).Code);
    }

    [Fact]
    public void A_kind_the_field_does_not_take_is_refused()
    {
        var e = Fails(() => _refs.Resolve(_doc, "front@post-1", "a", ReferenceKind.Face));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("plane", e.Error.Details!["kind"]);
    }
}
