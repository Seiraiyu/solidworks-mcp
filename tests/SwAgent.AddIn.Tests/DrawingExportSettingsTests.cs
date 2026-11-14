using System.Linq;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DrawingExportSettingsTests
{
    [Fact]
    public void A_pdf_opens_no_viewer()
    {
        Assert.Equal(new[] { "swPDFViewOnSave = false" }, DrawingExportSettings.For(DrawingExportFormat.Pdf).Select(s => s.ToString()));
    }

    [Theory]
    [InlineData(DrawingExportFormat.Dxf)]
    [InlineData(DrawingExportFormat.Dwg)]
    public void A_dxf_or_dwg_is_the_shown_sheet_at_sheet_scale_with_views_as_entities_and_no_box_to_answer(DrawingExportFormat format)
    {
        Assert.Equal(new[]
        {
            "swDxfMultiSheetOption = 0", "swDxfOutputNoScale = 0", "swDxfExportViewAsBlock = false", "swDxfMapping = false", "swDXFExportHiddenLayersWarnIsOn = false",
        }, DrawingExportSettings.For(format).Select(s => s.ToString()));
    }

    [Theory]
    [InlineData(DrawingExportFormat.Pdf, ".pdf")]
    [InlineData(DrawingExportFormat.Dxf, ".dxf")]
    [InlineData(DrawingExportFormat.Dwg, ".dwg")]
    public void Each_format_has_its_extension(DrawingExportFormat format, string extension)
    {
        Assert.Equal(extension, DrawingExportSettings.Extension(format));
    }

    [Fact]
    public void The_preference_ids_are_solidworks_own()
    {
        // swconst 2026 SP4: swPDFViewOnSave 617, swDxfMultiSheetOption 253, swDxfOutputNoScale 136, swDxfExportViewAsBlock 806, swDxfMapping 8, swDXFExportHiddenLayersWarnIsOn 453.
        Assert.Equal(617, DrawingExportSettings.For(DrawingExportFormat.Pdf).Single().Id);
        Assert.Equal(new[] { 253, 136, 806, 8, 453 }, DrawingExportSettings.For(DrawingExportFormat.Dxf).Select(s => s.Id));
    }
}
