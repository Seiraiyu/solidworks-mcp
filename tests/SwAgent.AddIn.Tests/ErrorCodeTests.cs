using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>
/// Every error code each operation lists in the plan's code table is reachable (design 371, Error handling): one case per
/// code, each the plainest way to get it. The dispatcher's own busy (not started), timeout and dialog_open, which every
/// operation can answer, are proven by phase 2's tests.
/// </summary>
public class ErrorCodeTests : DocumentTestBase
{
    static Point2 P(double x, double y) => new Point2(x, y);

    FakeDocument Part()
    {
        var doc = Sw.Track(FakeDocument.Part("Part1", PathOf("part1.SLDPRT")));
        doc.MassProperties = new ModelMassResult();
        return doc;
    }

    static SketchCreateRequest Circle(string? name = null) => new SketchCreateRequest
    {
        On = "front",
        Name = name,
        Circles = new List<SketchCircle> { new SketchCircle { Id = "c", Center = P(0, 0), RadiusMm = 5 } },
        Lines = new List<SketchLine> { new SketchLine { Id = "l", Start = P(10, 0), End = P(20, 0) } },
        Relations = new List<SketchRelationItem> { new SketchRelationItem { Type = SketchRelationType.Horizontal, Entities = new List<string> { "l" } } },
        Dimensions = new List<SketchDimensionItem> { new SketchDimensionItem { Entities = new List<string> { "c" }, Value = 10 } },
    };

    SketchCreateResult Create(SketchCreateRequest request) => Run(new SketchCreateOperation(Services), request);

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sketch_create_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Create(Circle())).Code);
            return;
        }
        var doc = Part();
        var request = Circle();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Circles![0].RadiusMm = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane });
                request.On = Services.References.Describe(doc, face).Reference!;
                face.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch7";
                break;
            case ErrorCode.OperationFailed:
                doc.SketchRefuses = SketchRelationType.Horizontal;
                break;
            case ErrorCode.VerificationFailed:
                doc.SketchOverDefines = true;
                break;
            case ErrorCode.ApiError:
                doc.RenameFails = true;
                request.Name = "profile";
                break;
        }
        Assert.Equal(code, Fails(() => Create(request)).Code);
        Assert.Empty(doc.Sketches);
    }

    [Fact]
    public void Sketch_create_rebuilds_after_taking_a_sketch_back()
    {
        var doc = Part();
        doc.SketchOverDefines = true;
        Fails(() => Create(Circle()));
        Assert.Empty(doc.Sketches);
        Assert.Equal(new[] { false }, doc.Rebuilds);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.VerificationFailed)]
    public void Sketch_read_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new SketchReadOperation(Services), new SketchReadRequest { Sketch = "Sketch1" })).Code);
            return;
        }
        var doc = Part();
        var made = Create(Circle());
        string sketch = "Sketch1";
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                sketch = "front";
                break;
            case ErrorCode.ReferenceUnresolved:
                sketch = made.Sketch.Reference!;
                doc.DeleteFeature(doc.Sketches[0].Feature);
                break;
            case ErrorCode.VerificationFailed:
                doc.SketchChangesAfterRead = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new SketchReadOperation(Services), new SketchReadRequest { Sketch = sketch })).Code);
    }

    SketchCreateResult Modify(SketchModifyRequest request) => Run(new SketchModifyOperation(Services), request);

    static SketchGeometry Ring() => new SketchGeometry
    {
        Circles = new List<SketchCircle> { new SketchCircle { Id = "ring", Center = P(0, 0), RadiusMm = 8 } },
        Relations = new List<SketchRelationItem> { new SketchRelationItem { Type = SketchRelationType.Fix, Entities = new List<string> { "ring" } } },
        Dimensions = new List<SketchDimensionItem> { new SketchDimensionItem { Entities = new List<string> { "ring" }, Value = 16 } },
    };

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Sketch_modify_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Modify(new SketchModifyRequest { Sketch = "Sketch1", Add = Ring() })).Code);
            return;
        }
        var doc = Part();
        var made = Create(Circle());
        var request = new SketchModifyRequest { Sketch = "Sketch1", Add = Ring() };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Add = null;
                break;
            case ErrorCode.ReferenceUnresolved:
                string line = made.Ids.Single(i => i.Id == "l").References[0];
                request.Remove = new List<string> { line };
                doc.Entities.Remove(doc.Entities.Single(e => Services.References.Describe(doc, e).Reference == line));
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.SketchRefuses = SketchRelationType.Fix;
                break;
            case ErrorCode.VerificationFailed:
                doc.SketchOverDefines = true;
                break;
            case ErrorCode.ApiError:
                doc.EditSketchFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Modify(request)).Code);
        Assert.Equal(2, doc.Sketches[0].Segments.Count);
    }

    [Fact]
    public void Sketch_modify_rebuilds_after_taking_additions_back()
    {
        var doc = Part();
        Create(Circle());
        doc.SketchOverDefines = true;
        Fails(() => Modify(new SketchModifyRequest { Sketch = "Sketch1", Add = Ring() }));
        Assert.Equal(2, doc.Sketches[0].Segments.Count);
        Assert.Equal(new[] { false }, doc.Rebuilds);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    public void Dimension_set_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new DimensionSetOperation(Services), new DimensionSetRequest { Dimension = "D1@Sketch1", Value = 12 })).Code);
            return;
        }
        var doc = Part();
        Create(Circle());
        var request = new DimensionSetRequest { Dimension = "D1@Sketch1", Value = 12 };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Dimension = "D7@Sketch1";
                break;
            case ErrorCode.ReferenceUnresolved:
                var dimension = doc.Sketches[0].Dimensions[0].Entity;
                request.Dimension = Services.References.Describe(doc, dimension).Reference!;
                doc.Entities.Remove(dimension);
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Fillet";
                break;
            case ErrorCode.OperationFailed:
                doc.RebuildSucceeds = false;
                break;
            case ErrorCode.VerificationFailed:
                doc.DimensionsIgnoreSet = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new DimensionSetOperation(Services), request)).Code);
        Assert.Equal(10, doc.Sketches[0].Dimensions[0].Value);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.VerificationFailed)]
    public void Model_dimensions_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new ModelDimensionsOperation(Services), new ModelDimensionsRequest())).Code);
            return;
        }
        var doc = Part();
        var made = Create(Circle());
        var request = new ModelDimensionsRequest();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Feature = "front";
                break;
            case ErrorCode.ReferenceUnresolved:
                request.Feature = made.Sketch.Reference!;
                doc.DeleteFeature(doc.Sketches[0].Feature);
                break;
            case ErrorCode.VerificationFailed:
                doc.DimensionsChangeAfterRead = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new ModelDimensionsOperation(Services), request)).Code);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.WrongDocumentType)]
    public void Diag_editing_reaches_every_listed_code(ErrorCode code)
    {
        var services = new AddInServices(Sw, "0.1.0-test", new SwAgent.AddIn.Core.AgentRuntime("0.1.0-test", 1, SwAgent.AddIn.Core.NullLog.Instance, diagnosticsEnabled: true));
        if (code == ErrorCode.WrongDocumentType) Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
        else
        {
            Part();
            services = Services;
        }
        Assert.Equal(code, Fails(() => Run(new DiagEditingOperation(services), new DiagEditingRequest())).Code);
    }

    /// <summary>A part with Sketch1, a 40 x 20 rectangle, to build features from.</summary>
    FakeDocument Profiled()
    {
        var doc = Part();
        Create(new SketchCreateRequest { On = "front", Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "r", Corner1 = P(0, 0), Corner2 = P(40, 20) } } });
        return doc;
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_extrude_reaches_every_listed_code(ErrorCode code)
    {
        var request = new FeatureExtrudeRequest { Sketch = "Sketch1", DepthMm = 10 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new FeatureExtrudeOperation(Services), request)).Code);
            return;
        }
        var doc = Profiled();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.DepthMm = null;
                break;
            case ErrorCode.ReferenceUnresolved:
                var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane });
                request.End = ExtrudeEnd.UpToSurface;
                request.DepthMm = null;
                request.UpTo = Services.References.Describe(doc, face).Reference!;
                face.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Boss-Extrude";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FeatureVolumeError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new FeatureExtrudeOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Extrusion");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_revolve_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new FeatureRevolveOperation(Services), new FeatureRevolveRequest { Sketch = "Sketch1", Axis = "Axis1" })).Code);
            return;
        }
        var doc = Part();
        var made = Create(new SketchCreateRequest
        {
            On = "front",
            Rectangles = new List<SketchRectangle> { new SketchRectangle { Id = "r", Corner1 = P(10, 0), Corner2 = P(20, 30) } },
            CenterLines = new List<SketchCenterLine> { new SketchCenterLine { Id = "axis", Start = P(0, 0), End = P(0, 40) } },
        });
        var request = new FeatureRevolveRequest { Sketch = "Sketch1", Axis = made.Ids.Single(i => i.Id == "axis").References[0] };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.AngleDeg = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                doc.Entities.Single(e => Services.References.Describe(doc, e).Reference == request.Axis).State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FeatureVolumeError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new FeatureRevolveOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Revolution");
    }

    /// <summary>A part with a 40 x 20 x 10 block (Sketch1, Boss-Extrude1) and one of its edges.</summary>
    (FakeDocument Doc, string Edge) Blocked()
    {
        var doc = Profiled();
        Run(new FeatureExtrudeOperation(Services), new FeatureExtrudeRequest { Sketch = "Sketch1", DepthMm = 10 });
        var edge = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Line, LengthMm = 10 });
        return (doc, Services.References.Describe(doc, edge).Reference!);
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_fillet_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new FeatureFilletOperation(Services), new FeatureFilletRequest { Edges = new List<string> { "Edge1" }, RadiusMm = 2 })).Code);
            return;
        }
        var (doc, edge) = Blocked();
        var request = new FeatureFilletRequest { Edges = new List<string> { edge }, RadiusMm = 2 };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.RadiusMm = -1;
                break;
            case ErrorCode.ReferenceUnresolved:
                doc.Entities.Single(e => e.Kind == ReferenceKind.Edge).State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Fillet";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.EdgesSurvive = true;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new FeatureFilletOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Fillet");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Feature_chamfer_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new FeatureChamferOperation(Services), new FeatureChamferRequest { Edges = new List<string> { "Edge1" }, DistanceMm = 2 })).Code);
            return;
        }
        var (doc, edge) = Blocked();
        var request = new FeatureChamferRequest { Edges = new List<string> { edge }, DistanceMm = 2 };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.DistanceMm = 0;
                break;
            case ErrorCode.ReferenceUnresolved:
                doc.Entities.Single(e => e.Kind == ReferenceKind.Edge).State = PersistState.Suppressed;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch1";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.FeatureAddsNothing = true;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new FeatureChamferOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Type == "Chamfer");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Refgeom_plane_reaches_every_listed_code(ErrorCode code)
    {
        var request = new RefgeomPlaneRequest { Mode = PlaneMode.Offset, From = "front", DistanceMm = 15 };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new RefgeomPlaneOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.DistanceMm = null;
                break;
            case ErrorCode.ReferenceUnresolved:
                var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(0, 0, 1) });
                request.From = Services.References.Describe(doc, face).Reference!;
                face.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Plane";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.RefGeometryError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new RefgeomPlaneOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Plane1");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Refgeom_axis_reaches_every_listed_code(ErrorCode code)
    {
        var request = new RefgeomAxisRequest { Mode = AxisMode.TwoPlanes, Entities = new List<string> { "front", "right" } };
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new RefgeomAxisOperation(Services), request)).Code);
            return;
        }
        var doc = Part();
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.Entities.RemoveAt(1);
                break;
            case ErrorCode.ReferenceUnresolved:
                var face = doc.Add(ReferenceKind.Face, new EntityInfo { Geometry = GeometryType.Plane, Normal = new Vector3(1, 0, 0) });
                request.Entities[1] = Services.References.Describe(doc, face).Reference!;
                face.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                doc.EditingSketchName = "Sketch9";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.RefGeometryError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new RefgeomAxisOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Axis1");
    }

    [Theory]
    [InlineData(ErrorCode.InvalidArgument)]
    [InlineData(ErrorCode.ReferenceUnresolved)]
    [InlineData(ErrorCode.WrongDocumentType)]
    [InlineData(ErrorCode.Busy)]
    [InlineData(ErrorCode.OperationFailed)]
    [InlineData(ErrorCode.VerificationFailed)]
    [InlineData(ErrorCode.ApiError)]
    public void Refgeom_point_reaches_every_listed_code(ErrorCode code)
    {
        if (code == ErrorCode.WrongDocumentType)
        {
            Sw.Track(new FakeDocument("Assem1", null, DocumentType.Assembly));
            Assert.Equal(code, Fails(() => Run(new RefgeomPointOperation(Services), new RefgeomPointRequest { Mode = PointMode.ArcCenter, Entities = new List<string> { "Edge1" } })).Code);
            return;
        }
        var doc = Part();
        var circle = doc.Add(ReferenceKind.Edge, new EntityInfo { Geometry = GeometryType.Circle, Origin = new Point3(20, 10, 10) });
        var request = new RefgeomPointRequest { Mode = PointMode.ArcCenter, Entities = new List<string> { Services.References.Describe(doc, circle).Reference! } };
        switch (code)
        {
            case ErrorCode.InvalidArgument:
                request.DistanceMm = 2;
                break;
            case ErrorCode.ReferenceUnresolved:
                circle.State = PersistState.Deleted;
                break;
            case ErrorCode.Busy:
                Sw.OpenCommandTitle = "Point";
                break;
            case ErrorCode.OperationFailed:
                doc.FeatureRefuses = true;
                break;
            case ErrorCode.VerificationFailed:
                doc.RefGeometryError = 1;
                break;
            case ErrorCode.ApiError:
                doc.FeatureSelectFails = true;
                break;
        }
        Assert.Equal(code, Fails(() => Run(new RefgeomPointOperation(Services), request)).Code);
        Assert.DoesNotContain(doc.Tree, f => f.Name == "Point1");
    }
}