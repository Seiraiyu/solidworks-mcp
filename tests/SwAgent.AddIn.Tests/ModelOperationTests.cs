using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class ModelTreeTests : DocumentTestBase
{
    [Fact]
    public void Lists_features_in_order_with_references_suppression_and_errors()
    {
        var doc = Sw.Track(new FakeDocument("Part1"));
        doc.AddFeature("Front Plane", "RefPlane", ReferenceKind.Plane);
        doc.AddFeature("Boss-Extrude1", "Extrusion");
        doc.AddFeature("Sketch1", "ProfileFeature", ReferenceKind.Sketch, parent: "Boss-Extrude1");
        doc.AddFeature("Fillet1", "Fillet", suppressed: true, errorCode: 13);
        doc.AddFeature("Sketch2", "ProfileFeature", ReferenceKind.Sketch, errorCode: 1, warning: true);
        var result = Run(new ModelTreeOperation(Services), new ModelTreeRequest());
        Assert.Equal(new[] { "Front Plane", "Boss-Extrude1", "Sketch1", "Fillet1", "Sketch2" }, result.Features.Select(f => f.Name));
        Assert.Equal(ReferenceKind.Plane, result.Features[0].Kind);
        Assert.StartsWith("r1.plane.", result.Features[0].Reference);
        Assert.StartsWith("r1.feature.", result.Features[1].Reference);
        Assert.Equal("Boss-Extrude1", result.Features[2].Parent);
        Assert.True(result.Features[3].Suppressed);
        Assert.Equal("swFeatureErrorFilletNoEdge", result.Features[3].Error);
        Assert.False(result.Features[3].Warning);
        Assert.True(result.Features[4].Warning);
        Assert.Null(result.Features[1].Error);
    }

    [Fact]
    public void The_tree_is_for_parts_and_mass_is_not_for_drawings()
    {
        Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ModelTreeOperation(Services), new ModelTreeRequest())).Code);
        Sw.Track(new FakeDocument("Sheet1", null, DocumentType.Drawing));
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ModelMassOperation(Services), new ModelMassRequest { Document = "Sheet1" })).Code);
        Assert.Equal(ErrorCode.WrongDocumentType, Fails(() => Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest { Document = "Assem1" })).Code);
    }
}

public class ModelMassTests : DocumentTestBase
{
    static ModelMassResult Block() => new ModelMassResult
    {
        VolumeMm3 = 8000,
        AreaMm2 = 2800,
        MassG = 8,
        DensityKgM3 = 1000,
        CenterOfMass = new Point3(20, 10, 5),
        PrincipalMomentsGMm2 = new List<double> { 1333.33, 333.33, 1133.33 },
    };

    [Fact]
    public void Reports_mass_properties_with_the_material_and_moments_smallest_first()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        doc.Bodies = 1;
        doc.MassProperties = Block();
        doc.MaterialName = "1060 Alloy";
        var result = Run(new ModelMassOperation(Services), new ModelMassRequest());
        Assert.Equal(1, result.Bodies);
        Assert.Equal(8000, result.VolumeMm3);
        Assert.Equal("1060 Alloy", result.Material);
        Assert.Equal(new[] { 333.33, 1133.33, 1333.33 }, result.PrincipalMomentsGMm2);
        Assert.Equal("Part1", result.Document.Title);
    }

    [Fact]
    public void A_part_with_no_body_reports_zeros()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        var result = Run(new ModelMassOperation(Services), new ModelMassRequest());
        Assert.Equal(0, result.Bodies);
        Assert.Equal(0, result.VolumeMm3);
        Assert.Null(result.CenterOfMass);
    }

    [Fact]
    public void Bodies_without_mass_properties_is_operation_failed()
    {
        Sw.Track(FakeDocument.Part("Part1")).Bodies = 1;
        Assert.Equal(ErrorCode.OperationFailed, Fails(() => Run(new ModelMassOperation(Services), new ModelMassRequest())).Code);
    }

    [Fact]
    public void A_mass_that_is_not_density_times_volume_fails_verification()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        doc.Bodies = 1;
        doc.MassProperties = Block();
        doc.MassProperties.MassG = 9;
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => Run(new ModelMassOperation(Services), new ModelMassRequest())).Code);
    }
}

public class ModelBoundingBoxTests : DocumentTestBase
{
    static Box3 Box(double x, double y, double z) => new Box3 { Min = new Point3(0, 0, 0), Max = new Point3(x, y, z) };

    [Fact]
    public void Quick_by_default_exact_when_asked_with_sizes()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1"));
        doc.Bodies = 1;
        doc.QuickBox = Box(40.2, 20.2, 10.2);
        doc.ExactBox = Box(40, 20, 10);
        var quick = Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest());
        Assert.False(quick.Precise);
        Assert.Equal(40.2, quick.SizeX, 9);
        var exact = Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest { Precise = true });
        Assert.True(exact.Precise);
        Assert.Equal((40.0, 20.0, 10.0), (exact.SizeX, exact.SizeY, exact.SizeZ));
    }

    [Fact]
    public void No_body_no_box()
    {
        Sw.Track(FakeDocument.Part("Part1"));
        var result = Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest());
        Assert.Null(result.Box);
        Assert.Equal(0, result.SizeX);
    }

    [Fact]
    public void Bodies_without_a_box_fail_verification()
    {
        Sw.Track(FakeDocument.Part("Part1")).Bodies = 2;
        Assert.Equal(ErrorCode.VerificationFailed, Fails(() => Run(new ModelBoundingBoxOperation(Services), new ModelBoundingBoxRequest())).Code);
    }
}
