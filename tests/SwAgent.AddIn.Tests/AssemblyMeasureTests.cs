using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// An assembly with base-1 at the origin and the turned post-1 at (100, 0, 0) (4.A's live case 3), each face given in
/// assembly coordinates, and post-1's own part, for the forms an entity of a component is named by (plan 4.B).
/// </summary>
public abstract class AssemblyInspectionBase : AssemblyTestBase
{
    internal readonly FakeDocument Post;
    internal readonly FakeEntity PostTopInPart;
    internal readonly FakeEntity BaseRight;
    internal readonly FakeEntity PostLeft;

    protected AssemblyInspectionBase()
    {
        Post = Sw.Track(FakeDocument.Part("post.SLDPRT", PathOf("post.SLDPRT")));
        Post.Document.Visible = false;
        PostTopInPart = Post.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 100, Normal = new Vector3(0, 0, 1), Origin = new Point3(5, 5, 30) });
        Asm.AddComponent("base-1", PathOf("base.SLDPRT"), fix: true);
        Asm.AddComponent("post-1", PathOf("pin.SLDPRT"), new Point3(100, 0, 0)).Model = Post;
        BaseRight = Asm.AddEntity("base-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 200, Normal = new Vector3(1, 0, 0), Origin = new Point3(40, 10, 5) });
        PostLeft = Asm.AddEntity("post-1", ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Feature = "Boss-Extrude1", AreaMm2 = 300, Normal = new Vector3(-1, 0, 0), Origin = new Point3(90, 10, 5) });
    }

    internal string Ref(FakeEntity e) => Services.References.Describe(Doc, e).Reference!;
}

public class AssemblyMeasureTests : AssemblyInspectionBase
{
    ModelMeasureResult Measure(ModelMeasureRequest request) => Run(new ModelMeasureOperation(Services), request);

    [Fact]
    public void Faces_of_two_components_are_measured_in_the_assembly()
    {
        var m = Measure(new ModelMeasureRequest { A = Ref(BaseRight), B = Ref(PostLeft) });
        Assert.Equal(50, m.DistanceMm, 9);
        Assert.Equal(0, m.AngleDeg!.Value, 9);
        Assert.Equal("base-1", m.A.Component);
        Assert.Equal("post-1", m.B.Component);
        Assert.Equal(DocumentType.Assembly, m.Document.Type);
    }

    [Fact]
    public void A_part_reference_with_its_component_measures_the_same_face_as_the_assemblys_reference()
    {
        string partRef = Services.References.Describe(Post, PostTopInPart).Reference!;
        var m = Measure(new ModelMeasureRequest { A = partRef, AComponent = "post-1", B = "front@post-1" });
        Assert.Equal("post-1", m.A.Component);
        Assert.Equal("post-1", m.B.Component);
        var again = Measure(new ModelMeasureRequest { A = m.A.Reference!, B = "front@post-1" });
        Assert.Equal(m.DistanceMm, again.DistanceMm, 9);
        Assert.Equal(m.A.Reference, again.A.Reference);
    }

    [Fact]
    public void The_assemblys_own_planes_measure_against_a_components()
    {
        var m = Measure(new ModelMeasureRequest { A = "top", B = Ref(PostLeft) });
        Assert.Equal(90, m.AngleDeg!.Value, 9);
        Assert.Null(m.A.Component);
    }

    [Fact]
    public void A_component_field_in_a_part_names_itself()
    {
        Sw.Track(FakeDocument.Part("block.SLDPRT", PathOf("block.SLDPRT")));
        var e = Fails(() => Measure(new ModelMeasureRequest { Document = "block.SLDPRT", A = "front", AComponent = "post-1", B = "top" }));
        Assert.Equal(ErrorCode.InvalidArgument, e.Code);
        Assert.Equal("a_component", e.Error.Details!["field"]);
    }

    [Fact]
    public void A_face_of_a_suppressed_component_is_unresolved_with_the_reason()
    {
        string post = Ref(PostLeft);
        Asm.Named("post-1").Info.State = ComponentState.Suppressed;
        var e = Fails(() => Measure(new ModelMeasureRequest { A = Ref(BaseRight), B = post }));
        Assert.Equal(ErrorCode.ReferenceUnresolved, e.Code);
        Assert.Equal("component_suppressed", e.Error.Details!["reason"]);
        Assert.Equal("b", e.Error.Details["field"]);
    }

    [Fact]
    public void A_drawing_is_the_wrong_document_type()
    {
        var drawing = Sw.Track(new FakeDocument("sheet.SLDDRW", PathOf("sheet.SLDDRW"), DocumentType.Drawing));
        var e = Fails(() => Measure(new ModelMeasureRequest { Document = "sheet.SLDDRW", A = "front", B = "top" }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("parts and assemblies", e.Message);
    }
}
