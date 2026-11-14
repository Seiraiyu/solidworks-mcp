using System;
using System.Collections.Generic;

namespace SwAgent.AddIn;

/// <summary>
/// Whether a drawing's active sketch is the user editing one (design 371, Model changes; 5.A live run). A drawing always
/// has an active sketch: IModelDoc2.GetActiveSketch2 gives the current sheet's own sketch, or the activated view's, when
/// nothing is being edited. Those are the drawing at rest; any other active sketch is the user editing it. Sketching on
/// the sheet runs a command (ISldWorks.GetRunningCommandInfo) and editing the sheet format clears IDrawingDoc.GetEditSheet,
/// and both of those answer busy in their own checks.
/// </summary>
public static class DrawingSketches
{
    /// <summary>A sketch as SOLIDWORKS gave it: its COM object and its feature name, when it has one.</summary>
    public readonly struct Sketch
    {
        public Sketch(object handle, string? name)
        {
            Handle = handle;
            Name = name;
        }

        public object Handle { get; }
        public string? Name { get; }
    }

    /// <summary>The name of the sketch the user is editing (or "a sketch"), or null when <paramref name="active"/> is none or one of <paramref name="atRest"/>.</summary>
    public static string? UserEditing(Sketch? active, IEnumerable<Sketch> atRest)
    {
        if (active is not Sketch a) return null;
        foreach (var own in atRest)
        {
            if (ReferenceEquals(own.Handle, a.Handle)) return null;
            if (a.Name != null && string.Equals(own.Name, a.Name, StringComparison.Ordinal)) return null;
        }
        return a.Name ?? "a sketch";
    }
}
