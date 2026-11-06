using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("A format export.part writes.")]
public enum ExportFormat
{
    [Description("STEP (.step or .stp), the schema options.step_schema names.")]
    Step,

    [Description("Parasolid text transmit file (.x_t).")]
    Parasolid,

    [Description("Binary STL (.stl), in mm.")]
    Stl,

    [Description("3MF (.3mf), in mm.")]
    [WireName("threemf")]
    ThreeMf,
}

[Description("The STEP application protocol a STEP export uses.")]
public enum StepSchema
{
    [Description("AP214 (automotive design), SOLIDWORKS' default.")]
    Ap214,

    [Description("AP242 (managed model-based 3D engineering).")]
    Ap242,
}
