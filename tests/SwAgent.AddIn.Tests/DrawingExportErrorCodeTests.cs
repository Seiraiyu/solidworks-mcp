using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

/// <summary>wrong_document_type both ways (design 611, Error handling): a drawing given to a model export names export.drawing, and a model given to export.drawing names the model exports.</summary>
public class DrawingExportErrorCodeTests : DrawingTestBase
{
    [Fact]
    public void A_drawing_given_to_export_part_names_export_drawing()
    {
        var e = Fails(() => Run(new ExportPartOperation(Services), new ExportPartRequest { Document = "block.SLDDRW", Path = PathOf("x.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("export.drawing", (string)e.Error.Details!["instead"]!);
        Assert.Contains("export.drawing", e.Message);
    }

    [Fact]
    public void A_drawing_given_to_export_assembly_names_export_drawing()
    {
        var e = Fails(() => Run(new ExportAssemblyOperation(Services), new ExportAssemblyRequest { Document = "block.SLDDRW", Path = PathOf("x.step"), Format = ExportFormat.Step }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("export.drawing", (string)e.Error.Details!["instead"]!);
    }

    [Fact]
    public void A_drawing_given_to_export_flat_pattern_names_export_drawing()
    {
        var e = Fails(() => Run(new ExportFlatPatternOperation(Services), new ExportFlatPatternRequest { Document = "block.SLDDRW", Path = PathOf("x.dxf"), Format = FlatPatternFormat.Dxf }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        Assert.Contains("export.drawing", (string)e.Error.Details!["instead"]!);
    }

    [Fact]
    public void A_part_given_to_export_drawing_names_the_model_exports()
    {
        Sw.Track(Block);
        var e = Fails(() => Run(new ExportDrawingOperation(Services), new ExportDrawingRequest { Document = "block.SLDPRT", Path = PathOf("x.pdf"), Format = DrawingExportFormat.Pdf }));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
        string instead = (string)e.Error.Details!["instead"]!;
        Assert.Contains("export.part", instead);
        Assert.Contains("export.assembly", instead);
        Assert.Contains("export.flat_pattern", instead);
    }
}
