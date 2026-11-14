using System.Linq;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;
using Xunit;

namespace SwAgent.AddIn.Tests;

public class SolidWorksTableTests
{
    [Theory]
    [InlineData(BomKind.TopLevel, (int)swBomType_e.swBomType_TopLevelOnly)]
    [InlineData(BomKind.PartsOnly, (int)swBomType_e.swBomType_PartsOnly)]
    [InlineData(BomKind.Indented, (int)swBomType_e.swBomType_Indented)]
    public void Bom_kinds_are_SOLIDWORKS_bom_types_both_ways(BomKind kind, int swType)
    {
        Assert.Equal(swType, SolidWorksDrawing.BomType(kind));
        Assert.Equal(kind, SolidWorksDrawing.BomKindOf(swType));
        Assert.Null(SolidWorksDrawing.BomKindOf((int)swBomType_e.swBomType_Flattened));
    }

    [Theory]
    [InlineData(TableAnchor.TopLeft, (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft)]
    [InlineData(TableAnchor.TopRight, (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight)]
    [InlineData(TableAnchor.BottomLeft, (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomLeft)]
    [InlineData(TableAnchor.BottomRight, (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_BottomRight)]
    public void Table_anchors_are_SOLIDWORKS_anchor_types(TableAnchor anchor, int swType)
    {
        Assert.Equal(swType, SolidWorksDrawing.AnchorType(anchor));
    }

    [Theory]
    [InlineData(BalloonLayout.Square, (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square)]
    [InlineData(BalloonLayout.Circular, (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Circle)]
    [InlineData(BalloonLayout.Top, (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Top)]
    [InlineData(BalloonLayout.Bottom, (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Bottom)]
    [InlineData(BalloonLayout.Left, (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Left)]
    [InlineData(BalloonLayout.Right, (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Right)]
    public void Balloon_layouts_are_SOLIDWORKS_layouts(BalloonLayout layout, int swType)
    {
        Assert.Equal(swType, SolidWorksDrawing.LayoutType(layout));
    }
}

public class AssemblyDrawingSeamTests : AssemblyDrawingTestBase
{
    [Fact]
    public void An_assembly_view_lists_its_components_and_each_ones_entities()
    {
        Assert.Equal(new[] { "block-1", "pin-1" }, Drw.VisibleComponents(AsmFront.Facts.Name));
        Assert.Same(PinCircle, Assert.Single(Drw.VisibleEntities(AsmFront.Facts.Name, true, true, "pin-1")).Entity);
        Assert.Equal(2, Drw.VisibleEntities(AsmFront.Facts.Name, true, true).Count);
        Assert.Empty(Drw.VisibleEntities(AsmFront.Facts.Name, true, true, "washer-1"));
    }

    [Fact]
    public void A_bill_lists_one_row_per_file_with_its_quantity_and_none_without_a_configuration()
    {
        var bom = Bom();
        Assert.Equal(new[] { "1", "2" }, bom.Rows.Select(r => r.ItemNumber));
        Assert.Equal(new[] { "block-1", "pin-1" }, bom.Rows.SelectMany(r => r.Components));
        Assert.All(bom.Rows, r => Assert.Equal(1, r.Quantity));
        string name = Drw.InsertBom(AsmFront.Facts.Name, BomKind.TopLevel, "", null, TableAnchor.TopLeft)!;
        Assert.Empty(Drw.Tables("Sheet1").Single(t => t.Name == name).Rows);
    }

    [Fact]
    public void Balloons_go_on_one_instance_of_each_item_numbered_as_the_bill()
    {
        var bom = Bom();
        Drw.AutoBalloon(AsmFront.Facts.Name, BalloonLayout.Square);
        var balloons = Drw.Annotations("Sheet1", AsmFront.Facts.Name).Where(a => a.Kind == AnnotationKind.Balloon).ToList();
        Assert.Equal(new[] { "block-1", "pin-1" }, balloons.SelectMany(b => b.Components));
        Assert.Equal(bom.Rows.Select(r => r.ItemNumber), balloons.Select(b => b.ItemNumber));
        Drw.AutoBalloon(AsmFront.Facts.Name, BalloonLayout.Square);
        Assert.Equal(2, Drw.Annotations("Sheet1", AsmFront.Facts.Name).Count(a => a.Kind == AnnotationKind.Balloon));
    }

    [Fact]
    public void An_exploded_view_draws_the_pin_moved_by_its_step()
    {
        var collapsed = Drw.ComponentImages(AsmFront.Facts.Name);
        Assert.True(Drw.ShowExploded(AsmFront.Facts.Name, true));
        Assert.True(Drw.ReadView(AsmFront.Facts.Name)!.Exploded);
        var exploded = Drw.ComponentImages(AsmFront.Facts.Name);
        Assert.Equal(collapsed["pin-1"].Min.Y + 40, exploded["pin-1"].Min.Y, 9);
        Assert.Equal(collapsed["block-1"].Min.Y, exploded["block-1"].Min.Y, 9);
    }

    [Fact]
    public void Showing_the_assembly_exploded_moves_its_boxes_and_collapsing_puts_them_back()
    {
        var asm = Asm.Asm!;
        Assert.True(asm.ShowExploded("ExplView1", true));
        Assert.Equal(55, PinC.Info.Box!.Min.Y, 9);
        Assert.Equal(15, asm.CollapsedBox(PinC)!.Min.Y, 9);
        asm.ShowExploded("ExplView1", false);
        Assert.Equal(15, PinC.Info.Box!.Min.Y, 9);
    }

    [Fact]
    public void Tables_are_deleted_by_name_on_their_sheet()
    {
        var bom = Bom();
        Assert.False(Drw.DeleteTables("Sheet1", new[] { "Bill of Materials9" }));
        Assert.True(Drw.DeleteTables("Sheet1", new[] { bom.Name }));
        Assert.Empty(Drw.Tables("Sheet1"));
    }
}
