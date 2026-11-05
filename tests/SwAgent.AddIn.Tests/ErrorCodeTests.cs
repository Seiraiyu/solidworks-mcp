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
}