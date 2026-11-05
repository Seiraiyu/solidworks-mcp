namespace SwAgent.Contracts;

public static class Protocol
{
    /// <summary>Bumped on any breaking change to the envelope or an existing operation.</summary>
    /// 2: busy and timeout answered by the add-in, event notifications, addin.* operations.
    public const int Version = 2;

    public const string PipeNamePrefix = "solidworks-mcp-";

    /// <summary>The add-in's pipe for the SOLIDWORKS process with this id: <c>\\.\pipe\solidworks-mcp-&lt;pid&gt;</c>.</summary>
    public static string PipeName(int processId) => PipeNamePrefix + processId;
}
