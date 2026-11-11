using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>Plan 4.B: model.bounding_box of an assembly (base at the origin, the turned post at (100, 0, 0)).</summary>
public class AssemblyBoxTests : AssemblyInspectionBase
{
    [Fact]
    public void An_assemblys_box_spans_its_components()
    {
        Doc.Bodies = 2;
        Doc.ExactBox = new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(100, 20, 30) };
        var box = Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest { Precise = true });
        Assert.Equal(100, box.SizeX);
        Assert.Equal(20, box.SizeY);
        Assert.Equal(30, box.SizeZ);
        Assert.Equal(DocumentType.Assembly, box.Document.Type);
    }

    [Fact]
    public void An_assembly_with_bodies_and_no_box_fails_verification()
    {
        Doc.Bodies = 2;
        var e = Fails(() => Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest()));
        Assert.Equal(ErrorCode.VerificationFailed, e.Code);
        Assert.Contains("stand.SLDASM", e.Message);
    }
}
