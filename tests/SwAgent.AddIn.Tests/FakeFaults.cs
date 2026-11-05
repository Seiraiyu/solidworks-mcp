namespace SwAgent.AddIn.Tests;

/// <summary>Faults a <see cref="FakeDocument"/> can be told to have, so every error code an operation lists can be reached in a test.</summary>
sealed partial class FakeDocument
{
    /// <summary>Renaming a sketch fails as SOLIDWORKS refusing a call would (api_error).</summary>
    public bool RenameFails { get; set; }

    /// <summary>Every read of a sketch finds a line more than the read before (someone drawing while it is read).</summary>
    public bool SketchChangesAfterRead { get; set; }

    /// <summary>Opening a sketch for editing fails as SOLIDWORKS refusing a call would (api_error).</summary>
    public bool EditSketchFails { get; set; }

    int _dimensionReads;

    /// <summary>Every read of the dimensions after the first finds one more (someone dimensioning while they are read).</summary>
    public bool DimensionsChangeAfterRead { get; set; }
}
