using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class AssemblyFindTests : AssemblyInspectionBase
{
    ModelFindResult Find(ModelFindRequest request) => Run(new ModelFindOperation(Services), request);

    public AssemblyFindTests()
    {
        Post.AddFeature("Boss-Extrude1", "Extrusion");
        Asm.AddEntity("post-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", Normal = new Vector3(0, 0, 1), Origin = new Point3(95, 5, 30), DistanceMm = 0 });
        Asm.AddEntity("base-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", Normal = new Vector3(0, 0, 1), Origin = new Point3(20, 10, 10), DistanceMm = 3 });
    }

    [Fact]
    public void Every_resolved_components_faces_are_searched_with_assembly_references()
    {
        var found = Find(new ModelFindRequest { Kind = TopologyKind.Face, Direction = new Vector3(0, 0, 1) });
        Assert.Equal(new[] { "post-1", "base-1" }, found.Entities.Select(e => e.Component).OrderByDescending(c => c));
        Assert.All(found.Entities, e => Assert.Equal(Doc.Document.Path, References.Decode(e.Reference!, "e").DocumentKey));
        Assert.All(found.Entities, e => Assert.Equal(e.Component, References.Decode(e.Reference!, "e").Component));
        Assert.Empty(found.Skipped);
    }

    [Fact]
    public void A_component_limits_the_search()
    {
        var found = Find(new ModelFindRequest { Kind = TopologyKind.Face, Component = "post-1" });
        Assert.Equal(2, found.Total);
        Assert.All(found.Entities, e => Assert.Equal("post-1", e.Component));
    }

    [Fact]
    public void A_feature_at_a_component_searches_only_that_component()
    {
        var found = Find(new ModelFindRequest { Kind = TopologyKind.Face, Feature = "Boss-Extrude1@post-1" });
        Assert.Equal(2, found.Total);
        Assert.All(found.Entities, e => Assert.Equal("post-1", e.Component));
    }

    [Fact]
    public void A_feature_of_another_component_than_the_one_searched_is_refused()
    {
        var e = Fails(() => Find(new ModelFindRequest { Kind = TopologyKind.Face, Component = "base-1", Feature = "Boss-Extrude1@post-1" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("feature", e.Error.Details!["field"]);
    }

    [Fact]
    public void Near_is_measured_in_the_assembly_nearest_first()
    {
        var found = Find(new ModelFindRequest { Kind = TopologyKind.Face, Near = new Point3(95, 5, 30), MaxDistanceMm = 5 });
        Assert.Equal("post-1", found.Entities[0].Component);
        Assert.Equal(0, found.Entities[0].DistanceMm);
    }

    [Fact]
    public void Components_without_geometry_are_listed_as_skipped()
    {
        Asm.Named("base-1").Info.State = ComponentState.Lightweight;
        Asm.AddComponent("gone-1", PathOf("gone.SLDPRT")).Info.State = ComponentState.Missing;
        var found = Find(new ModelFindRequest { Kind = TopologyKind.Face });
        Assert.All(found.Entities, e => Assert.Equal("post-1", e.Component));
        Assert.Equal(new[] { "base-1", "gone-1" }, found.Skipped.Select(s => s.Name));
        Assert.Equal(ComponentState.Lightweight, found.Skipped[0].State);
        Assert.Equal(ComponentState.Missing, found.Skipped[1].State);
    }

    [Fact]
    public void A_subassembly_searches_what_is_inside_it()
    {
        Asm.AddComponent("sub-1", PathOf("sub.SLDASM"));
        Asm.AddComponent("sub-1/pin-1", PathOf("pin.SLDPRT"), parent: "sub-1");
        Asm.AddEntity("sub-1/pin-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Cylinder, Axis = new Vector3(0, 0, 1), RadiusMm = 5 });
        var found = Find(new ModelFindRequest { Kind = TopologyKind.Face, Component = "sub-1" });
        Assert.Equal("sub-1/pin-1", Assert.Single(found.Entities).Component);
    }

    [Fact]
    public void A_component_filter_in_a_part_is_refused()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        var e = Fails(() => Find(new ModelFindRequest { Document = "block.SLDPRT", Kind = TopologyKind.Face, Component = "post-1" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("component", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_found_face_measures_from_its_reference()
    {
        var top = Find(new ModelFindRequest { Kind = TopologyKind.Face, Component = "post-1", Direction = new Vector3(0, 0, 1) }).Entities.Single();
        var m = Run(new ModelMeasureOperation(Services), new ModelMeasureRequest { A = top.Reference!, B = "front" });
        Assert.Equal("post-1", m.A.Component);
    }
}
