namespace SwAgent.Contracts;

public static class Protocol
{
    /// <summary>Bumped on any breaking change to the envelope or an existing operation.</summary>
    /// 2: busy and timeout answered by the add-in, event notifications, addin.* operations.
    /// 3: persistent references, the document, model and selection families, session.status internal.
    /// 4: the sheetmetal family.
    /// 5: assemblies: component and mate references, DocumentInfo.visible, the assembly and component families.
    /// 6: sheet-metal flanges and hems: sheetmetal.edge_flange, sheetmetal.miter_flange, sheetmetal.hem.
    /// 7: assembly-context references (payload versions 3 and 4, EntityInfo.component), model, selection and configuration operations on assemblies.
    /// 8: sheet-metal bends and flattening: sheetmetal.sketched_bend, sheetmetal.flatten, sheetmetal.unfold, sheetmetal.fold, sheetmetal.convert.
    /// 9: mates and degrees of freedom: mate.add, mate.edit, mate.delete, mate.list, assembly.dof; r1.mate references.
    /// 10: flat-pattern export: export.flat_pattern.
    /// 11: component patterns and interference: component.linear_pattern, component.circular_pattern, component.feature_pattern, assembly.interference; component.delete deletes patterns.
    /// 12: exploded views and assembly export: explode.create, explode.list, explode.show, explode.delete, export.assembly; diag.arm_dialog stops assembly changes too.
    /// 13: drawings: document.new makes drawings; the drawing, sheet and view families; sheet and view references.
    public const int Version = 13;

    public const string PipeNamePrefix = "solidworks-mcp-";

    /// <summary>The add-in's pipe for the SOLIDWORKS process with this id: <c>\\.\pipe\solidworks-mcp-&lt;pid&gt;</c>.</summary>
    public static string PipeName(int processId) => PipeNamePrefix + processId;
}
