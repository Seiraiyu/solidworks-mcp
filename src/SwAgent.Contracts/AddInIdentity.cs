namespace SwAgent.Contracts;

/// <summary>Who the add-in is to COM and to SOLIDWORKS. The add-in's attributes and the registration both use these.</summary>
public static class AddInIdentity
{
    /// <summary>The add-in class's CLSID. Never changes: SOLIDWORKS keys its add-in list by it.</summary>
    public const string Clsid = "F02622A4-EBE9-4423-B033-FC63F0990B72";

    public const string ProgId = "SwAgent.AddIn";

    public const string ClassName = "SwAgent.AddIn.SwAgentAddIn";

    public const string AssemblyFileName = "SwAgent.AddIn.dll";

    public const string Title = "solidworks-mcp";

    public const string Description = "Lets an AI agent drive SOLIDWORKS through the solidworks-mcp server.";

    /// <summary>The CLSID as the registry writes it: upper case, in braces.</summary>
    public static string ClsidKey => "{" + Clsid + "}";
}
