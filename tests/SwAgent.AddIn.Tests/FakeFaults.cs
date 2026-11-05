namespace SwAgent.AddIn.Tests;

/// <summary>Faults a <see cref="FakeDocument"/> can be told to have, so every error code an operation lists can be reached in a test.</summary>
sealed partial class FakeDocument
{
    /// <summary>Renaming a sketch fails as SOLIDWORKS refusing a call would (api_error).</summary>
    public bool RenameFails { get; set; }
}
