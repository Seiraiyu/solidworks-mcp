namespace SwAgent.AddIn.Core;

/// <summary>Applies and restores the automation preferences. Task 17 fills this in.</summary>
public sealed class PreferenceGuard
{
    public bool Applied { get; private set; }
    public bool RecoveredAtLoad { get; private set; }
    public System.Collections.Generic.IReadOnlyList<string> NotTaken { get; } = System.Array.Empty<string>();
}
