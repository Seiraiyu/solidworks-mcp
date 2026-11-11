using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("configuration.list", typeof(ConfigurationListResult), ReadOnly = true)]
[Description("A part's or an assembly's configurations, by name, and which one is active. feature.suppress and dimension.set act in a part's active configuration; component.set and the component's state in an assembly's.")]
public sealed class ConfigurationListRequest
{
    [Description("The part or assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A part's or assembly's configurations.")]
public sealed class ConfigurationListResult
{
    [Required]
    [Description("The part or assembly.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The active configuration's name.")]
    public string Active { get; set; } = "";

    [Required]
    [Description("Every configuration's name, in SOLIDWORKS' order.")]
    public List<string> Configurations { get; set; } = new List<string>();
}

[Operation("configuration.add", typeof(ConfigurationResult))]
[Description("Adds a configuration to a part or an assembly, a copy of copy_of (the active configuration when absent), and makes it active, as SOLIDWORKS does. Checked: the document lists it and it is active, and it rebuilds without a new error. Otherwise it is deleted and the configuration that was active is active again (rolled back). Busy with user_editing while the user is editing a sketch, has a command open, or is editing a component in the assembly's context.")]
public sealed class ConfigurationAddRequest
{
    [Description("The part or assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The new configuration's name: not one the part has already.")]
    public string Name { get; set; } = "";

    [Description("The configuration to copy: its name. The active one when absent.")]
    public string? CopyOf { get; set; }
}

[Operation("configuration.activate", typeof(ConfigurationResult))]
[Description("Makes one of a part's or an assembly's configurations active; SOLIDWORKS then shows and rebuilds the document as that configuration has it (in an assembly, with each component's suppression, configuration and position as it sets them). Checked: it is active and the document rebuilds without a new error; otherwise the one that was active is active again (rolled back). The evidence block's volume before and after shows what changed. Busy with user_editing while the user is editing a sketch, has a command open, or is editing a component in the assembly's context.")]
public sealed class ConfigurationActivateRequest
{
    [Description("The part or assembly: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The configuration's name.")]
    public string Name { get; set; } = "";
}

[Description("A part's or assembly's configurations after a change.")]
public sealed class ConfigurationResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: the active configuration before and after, and the part or assembly rebuilt.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The active configuration's name now.")]
    public string Active { get; set; } = "";

    [Required]
    [Description("Every configuration's name now, in SOLIDWORKS' order.")]
    public List<string> Configurations { get; set; } = new List<string>();
}
