using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("dimension.set", typeof(DimensionSetResult))]
[Description("Sets a driving dimension, of a sketch or a feature, to a value in mm (degrees for an angle), named by its full name (width@Sketch3, D1@Boss-Extrude1) or its reference; then rebuilds the part. Checked: the value reads back and no feature has a new rebuild error; otherwise the old value is put back. Busy with user_editing while the user is editing a sketch or has a command open. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class DimensionSetRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The dimension: its full name as model.dimensions or sketch.read gave it (case does not matter), or its reference.")]
    public string Dimension { get; set; } = "";

    [Description("Its new value: mm, more than 0; or degrees for an angle, more than 0 and less than 360.")]
    public double Value { get; set; }
}

[Description("The dimension set, and the part after it.")]
public sealed class DimensionSetResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: the value before and after, and the part rebuilt.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The dimension, read back.")]
    public DimensionInfo Dimension { get; set; } = new DimensionInfo();
}

[Operation("model.dimensions", typeof(ModelDimensionsResult), ReadOnly = true)]
[Description("Lists a part's dimensions, or one feature's or sketch's: each one's full name, value in mm or degrees, whether it drives or is driven, the feature it belongs to, and its reference. dimension.set changes a driving one. References are opaque: pass one back exactly as it was given, and never build or change one.")]
public sealed class ModelDimensionsRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Description("Only this feature's or sketch's: its name or reference. Every dimension in the part, in tree order, when absent.")]
    public string? Feature { get; set; }
}

[Description("A part's dimensions.")]
public sealed class ModelDimensionsResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The dimensions, in tree order.")]
    public List<DimensionInfo> Dimensions { get; set; } = new List<DimensionInfo>();
}
