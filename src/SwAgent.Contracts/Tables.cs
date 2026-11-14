using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("What kind of table it is.")]
public enum TableKind
{
    [Description("A bill of materials (table.bom makes them).")]
    Bom,

    [Description("Any other table (general, hole, revision, weld, title block): read and deleted, never made here.")]
    Other,
}

[Description("Which bill of materials: what its rows count.")]
public enum BomKind
{
    [Description("One row per distinct file and configuration among the assembly's top-level components; a subassembly is one row.")]
    TopLevel,

    [Description("One row per distinct part file and configuration at every level; subassemblies are not rows, their parts are.")]
    PartsOnly,

    [Description("The top-level rows, each subassembly's rows after it, counted per subassembly.")]
    Indented,
}

[Description("Which corner of a table sits at its anchor point.")]
public enum TableAnchor
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

[Description("How annotation.balloons lays balloons out around a view (SOLIDWORKS' Auto Balloon layouts).")]
public enum BalloonLayout
{
    [Description("Around the view in a square.")]
    Square,

    [Description("Around the view in a circle.")]
    Circular,

    [Description("In a row above the view.")]
    Top,

    [Description("In a row below the view.")]
    Bottom,

    [Description("In a column left of the view.")]
    Left,

    [Description("In a column right of the view.")]
    Right,
}

[Description("A row of a table, as read back.")]
public sealed class TableRow
{
    [Required]
    [Description("Its cells' text as shown, column by column.")]
    public List<string> Cells { get; set; } = new List<string>();

    [Description("A bill of materials row's item number, as SOLIDWORKS numbers it.")]
    public string? ItemNumber { get; set; }

    [Description("A bill of materials row's quantity, from its quantity column; absent when the table has none or it is not a number.")]
    public int? Quantity { get; set; }

    [Description("A bill of materials row's component file, full path; absent when its components have none or several.")]
    public string? Path { get; set; }

    [Description("The configuration of that file its components use.")]
    public string? Configuration { get; set; }

    [Required]
    [Description("A bill of materials row's components (pin-1, sub-1/pin-1), as SOLIDWORKS lists them for the row; empty for another table.")]
    public List<string> Components { get; set; } = new List<string>();
}

[Description("A table on a drawing, read back: its columns and rows as text, and for a bill of materials each row's component file and configuration.")]
public sealed class TableInfo
{
    [Required]
    [Description("Its name, as SOLIDWORKS names its annotation (unique on its sheet).")]
    public string Name { get; set; } = "";

    [Description("Its reference (r1.table.…). It names the table by its sheet and name: one deleted, or whose sheet is renamed or deleted, no longer resolves. References are opaque: pass one back exactly as it was given, and never build or change one.")]
    public string? Reference { get; set; }

    [Required]
    [Description("The sheet it is on.")]
    public string Sheet { get; set; } = "";

    [Description("The view it is anchored to (a bill of materials' view); absent for one on the sheet itself.")]
    public string? View { get; set; }

    [Description("bom or other.")]
    public TableKind Kind { get; set; }

    [Description("A bill of materials' kind: top_level, parts_only or indented.")]
    public BomKind? BomKind { get; set; }

    [Description("The assembly configuration a bill of materials lists.")]
    public string? Configuration { get; set; }

    [Description("A bill of materials' feature name (Bill of Materials1).")]
    public string? Feature { get; set; }

    [Description("Where SOLIDWORKS says the table is, sheet mm (its annotation's position); read back, not checked.")]
    public SheetPoint? Position { get; set; }

    [Required]
    [Description("Its column titles, left to right.")]
    public List<string> Columns { get; set; } = new List<string>();

    [Required]
    [Description("Its rows below the header, top to bottom.")]
    public List<TableRow> Rows { get; set; } = new List<TableRow>();
}

[Operation("table.bom", typeof(TableResult))]
[Description("Puts a bill of materials on a drawing for a view of an assembly (IView.InsertBomTable4 with the view's configuration named, never empty: CADapter measured an empty name making a table with no rows that reports success), in SOLIDWORKS' standard template: top_level (one row per distinct file and configuration among the top-level components; a subassembly is one row), parts_only (parts at every level) or indented. at (sheet mm) puts its anchor corner there; at the sheet format's bill of materials anchor point when absent. Checked by reading the table's cells back, not by SOLIDWORKS' answer: one row per distinct component file and configuration at the level asked, each with the quantity assembly.components counts for it (suppressed components are not counted), in any order; a table with no rows is verification_failed; the model is unchanged. The view's configuration must be the assembly's active one, which assembly.components reads (activate it with configuration.activate). Otherwise the table is deleted again (rolled back)." + DrawingChangeText.Waits)]
public sealed class TableBomRequest
{
    [Description("The drawing: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The view of the assembly the bill lists: a reference (r1.view.…) or its name (Drawing View1).")]
    public string View { get; set; } = "";

    [Description("top_level, parts_only or indented; top_level when absent.")]
    public BomKind? Kind { get; set; }

    [Description("Where the table's anchor corner goes, sheet mm. The sheet format's bill of materials anchor point when absent.")]
    public SheetPoint? At { get; set; }

    [Description("Which corner of the table is at at (or the format's anchor point): top_left, top_right, bottom_left or bottom_right; top_left when absent.")]
    public TableAnchor? Anchor { get; set; }
}

[Description("The table made.")]
public sealed class TableResult : IDrawingChangeResult
{
    [Required]
    [Description("The table, as read back.")]
    public TableInfo Table { get; set; } = new TableInfo();

    [Required]
    [Description("The rows the bill should have, counted from assembly.components: file (configuration) xN.")]
    public List<string> Expected { get; set; } = new List<string>();

    [Required]
    [Description("What changed in the drawing: created holds the table; tables before and after; models each model shown, unchanged.")]
    public DrawingChange Change { get; set; } = new DrawingChange();
}

[Description("Where an exploded view draws one component, against where its explode steps put it.")]
public sealed class ExplodedImage
{
    [Required]
    [Description("The component (pin-1).")]
    public string Name { get; set; } = "";

    [Required]
    [Description("How far its explode steps move its origin, as the view draws that move, sheet mm (the assembly's move turned through the view's transform and scaled).")]
    public SheetPoint Moved { get; set; } = new SheetPoint();

    [Required]
    [Description("Its box exploded, read in the assembly and projected through the view's transform and scaled, sheet mm: what it draws must lie inside this.")]
    public SheetBox Expected { get; set; } = new SheetBox();

    [Description("The box around the edges the view draws for it, sheet mm; absent when it draws none of its edges (hidden behind another).")]
    public SheetBox? Drawn { get; set; }
}
