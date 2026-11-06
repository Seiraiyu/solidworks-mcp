using System.Collections.Generic;
using System.ComponentModel;

namespace SwAgent.Contracts;

[Operation("configuration.list", typeof(ConfigurationListResult), ReadOnly = true)]
[Description("A part's configurations, by name, and which one is active. feature.suppress and dimension.set act in the active configuration.")]
public sealed class ConfigurationListRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }
}

[Description("A part's configurations.")]
public sealed class ConfigurationListResult
{
    [Required]
    [Description("The part.")]
    public DocumentInfo Document { get; set; } = new DocumentInfo();

    [Required]
    [Description("The active configuration's name.")]
    public string Active { get; set; } = "";

    [Required]
    [Description("Every configuration's name, in SOLIDWORKS' order.")]
    public List<string> Configurations { get; set; } = new List<string>();
}

[Operation("configuration.add", typeof(ConfigurationResult))]
[Description("Adds a configuration to a part, a copy of copy_of (the active configuration when absent), and makes it active, as SOLIDWORKS does. Checked: the part lists it and it is active, and the part rebuilds without a new error. Otherwise it is deleted and the configuration that was active is active again (rolled back). Busy with user_editing while the user is editing a sketch or has a command open.")]
public sealed class ConfigurationAddRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The new configuration's name: not one the part has already.")]
    public string Name { get; set; } = "";

    [Description("The configuration to copy: its name. The active one when absent.")]
    public string? CopyOf { get; set; }
}

[Operation("configuration.activate", typeof(ConfigurationResult))]
[Description("Makes one of a part's configurations active; SOLIDWORKS then shows and rebuilds the part as that configuration has it. Checked: it is active and the part rebuilds without a new error; otherwise the one that was active is active again (rolled back). The evidence block's volume before and after shows what changed. Busy with user_editing while the user is editing a sketch or has a command open.")]
public sealed class ConfigurationActivateRequest
{
    [Description("The part: its title or full path. The active document when absent.")]
    public string? Document { get; set; }

    [Required]
    [Description("The configuration's name.")]
    public string Name { get; set; } = "";
}

[Description("A part's configurations after a change.")]
public sealed class ConfigurationResult : IModelChangeResult
{
    [Required]
    [Description("The evidence: the active configuration before and after, and the part rebuilt.")]
    public ModelChange Change { get; set; } = new ModelChange();

    [Required]
    [Description("The active configuration's name now.")]
    public string Active { get; set; } = "";

    [Required]
    [Description("Every configuration's name now, in SOLIDWORKS' order.")]
    public List<string> Configurations { get; set; } = new List<string>();
}
