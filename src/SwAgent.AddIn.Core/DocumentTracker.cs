using System;
using System.Collections.Generic;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>The open documents as last read. Task 15 fills this in.</summary>
public sealed class DocumentTracker
{
    public IReadOnlyList<DocumentInfo> Current { get; private set; } = Array.Empty<DocumentInfo>();
    public DateTime? AsOfUtc { get; private set; }
}
