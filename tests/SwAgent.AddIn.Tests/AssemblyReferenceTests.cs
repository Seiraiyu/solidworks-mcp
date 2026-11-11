using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: references to entities of components, issued against the assembly (design 455, Assembly references).</summary>
public class AssemblyReferenceTests
{
    internal readonly FakeSolidWorks Sw = new FakeSolidWorks();
    internal readonly RenameMap Renames = new RenameMap();
    internal readonly ReferenceService Refs;
    internal readonly FakeDocument Doc;
    internal readonly FakeAssembly Asm;
    internal readonly FakeEntity Top;

    public AssemblyReferenceTests()
    {
        Refs = new ReferenceService(Renames);
        Doc = Sw.Track(FakeDocument.NewAssembly("stand.SLDASM", @"C:\w\stand.SLDASM"));
        Asm = Doc.Asm!;
        Asm.AddComponent("base-1", @"C:\w\base.SLDPRT", fix: true);
        Asm.AddComponent("post-1", @"C:\w\post.SLDPRT", new Point3(100, 0, 0));
        Top = Asm.AddEntity("post-1", ReferenceKind.Face, new EntityInfo
        {
            Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 100, Normal = new Vector3(0, 0, 1), Origin = new Point3(95, 5, 30),
        });
    }

    string Issue(FakeEntity e) => Refs.Describe(Doc, e).Reference!;

    SwAgentException Unresolved(string reference)
    {
        var e = Assert.Throws<SwAgentException>(() => Refs.Resolve(Doc, reference, "a"));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        return e;
    }

    [Fact]
    public void A_components_face_is_issued_against_the_assembly_naming_its_component()
    {
        var info = Refs.Describe(Doc, Top);
        Assert.Equal("post-1", info.Component);
        var data = References.Decode(info.Reference!, "a");
        Assert.Equal(@"C:\w\stand.SLDASM", data.DocumentKey);
        Assert.Equal("post-1", data.Component);
        Assert.Equal(@"C:\w\post.SLDPRT", data.ComponentPath);
        Assert.Equal("plane face of Boss-Extrude1 in post-1, 100 mm²", data.Description);
        Assert.Same(Top, Refs.Resolve(Doc, info.Reference!, "a").Entity);
    }

    [Fact]
    public void The_assemblys_own_plane_names_no_component()
    {
        var front = Doc.StandardPlane(0)!;
        var info = Refs.Describe(Doc, front);
        Assert.Null(info.Component);
        Assert.Null(References.Decode(info.Reference!, "a").Component);
    }

    [Fact]
    public void A_component_reference_carries_its_own_name_and_file()
    {
        var post = Asm.Named("post-1");
        var data = References.Decode(Refs.Describe(Doc, post.Entity).Reference!, "a");
        Assert.Equal("post-1", data.Component);
        Assert.Equal(@"C:\w\post.SLDPRT", data.ComponentPath);
        Assert.Equal("component post-1", data.Description);
    }

    [Theory]
    [InlineData(ComponentState.Suppressed, "component_suppressed", "component.set")]
    [InlineData(ComponentState.Lightweight, "component_lightweight", "component.set")]
    [InlineData(ComponentState.Missing, "component_missing", @"C:\w\post.SLDPRT is not there")]
    public void A_face_of_a_component_that_is_not_loaded_says_why(ComponentState state, string reason, string remedy)
    {
        string reference = Issue(Top);
        Asm.Named("post-1").Info.State = state;
        var e = Unresolved(reference);
        Assert.Equal(reason, e.Error.Details!["reason"]);
        Assert.Contains(remedy, e.Message);
        Assert.Contains("in post-1", (string)e.Error.Details["last_was"]!);
    }

    [Fact]
    public void A_face_whose_component_is_now_another_file_is_never_resolved()
    {
        string reference = Issue(Top);
        // SOLIDWORKS found post.SLDPRT missing and loaded a same-named file from a folder it had opened (4.A live run, case 7).
        Asm.Named("post-1").Info.Path = @"C:\elsewhere\post.SLDPRT";
        var e = Unresolved(reference);
        Assert.Equal("component_file_changed", e.Error.Details!["reason"]);
        Assert.Contains(@"C:\elsewhere\post.SLDPRT", e.Message);
    }

    [Fact]
    public void A_component_saved_under_a_new_name_keeps_its_references()
    {
        string reference = Issue(Top);
        Asm.Named("post-1").Info.Path = @"C:\w\post-v2.SLDPRT";
        Renames.Record(@"C:\w\post.SLDPRT", @"C:\w\post-v2.SLDPRT");
        Assert.Same(Top, Refs.Resolve(Doc, reference, "a").Entity);
    }

    [Fact]
    public void A_deleted_face_is_unresolved_as_before()
    {
        string reference = Issue(Top);
        Top.State = PersistState.Deleted;
        Assert.Equal("deleted", Unresolved(reference).Error.Details!["reason"]);
    }

    [Fact]
    public void A_suppressed_components_own_reference_still_finds_it()
    {
        var post = Asm.Named("post-1");
        string reference = Issue(post.Entity);
        Doc.SuppressedComponentResolves = true;
        Asm.SetSuppression(post.Entity, Suppression.Suppressed);
        Assert.Same(post.Entity, ComponentLookup.Find(Refs, Doc, Asm, reference, "component"));
    }
}
