using System.IO;
using System.Linq;
using SwAgent.AddIn.Operations;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class DrawingReadTests : DrawingTestBase
{
    DrawingReadResult Read(string? document = null) => Run(new DrawingReadOperation(Services), new DrawingReadRequest { Document = document });

    [Fact]
    public void Reads_every_sheet_and_view_as_a_drafter_reads_them()
    {
        Drw.AddSheet(new SheetFacts { Name = "Sheet2", WidthMm = 297, HeightMm = 210, Scale = new ScaleRatio(1, 2), Projection = Projection.FirstAngle });
        Drw.Active = "Sheet1";
        Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150), new ScaleRatio(1, 2));
        var result = Read();
        Assert.Equal("Sheet1", result.ActiveSheet);
        Assert.Equal(new[] { "Sheet1", "Sheet2" }, result.Sheets.Select(s => s.Name));
        var a4 = result.Sheets[1];
        Assert.Equal(SheetSize.A4, a4.Size);
        Assert.Equal(Projection.FirstAngle, a4.Projection);
        Assert.False(a4.Active);
        Assert.StartsWith("r1.sheet.", a4.Reference);
        var view = Assert.Single(result.Sheets[0].Views);
        Assert.Equal("Drawing View1", view.Name);
        Assert.StartsWith("r1.view.", view.Reference);
        Assert.Equal(ViewType.Model, view.Type);
        Assert.Equal("front", view.Orientation);
        Assert.Equal(Block.Document.Path, view.Model);
        Assert.False(view.SheetScale);
        Assert.Equal(85, view.Extents!.Min.X, 9);
        Assert.Equal(115, view.Extents.Max.X, 9);
        Assert.Equal(140, view.Extents.Min.Y, 9);
        Assert.Equal(160, view.Extents.Max.Y, 9);
    }

    [Fact]
    public void Lists_each_model_the_drawing_shows_with_its_own_flags()
    {
        Drw.AddView("Sheet1", Block, "front", new SheetPoint(100, 150));
        Block.Document.Dirty = true;
        File.WriteAllText(Block.Document.Path = PathOf("block.SLDPRT"), "part");
        Doc.Uses.Clear();
        Doc.Uses.AddRange(new[] { Block.Document.Path, PathOf("gone.SLDPRT") });
        var models = Read("block.SLDDRW").Models;
        Assert.Equal(2, models.Count);
        Assert.True(models[0].Loaded);
        Assert.True(models[0].Dirty);
        Assert.False(models[0].Missing);
        Assert.False(models[1].Loaded);
        Assert.True(models[1].Missing);
    }

    [Fact]
    public void A_part_is_the_wrong_document_type()
    {
        Sw.Track(FakeDocument.Part("plate.SLDPRT", PathOf("plate.SLDPRT")));
        var e = Fails(() => Read("plate.SLDPRT"));
        Assert.Equal(ErrorCode.WrongDocumentType, e.Code);
    }
}
