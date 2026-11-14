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

    /// <summary>
    /// As <see cref="UserEditing(Sketch?, IEnumerable{Sketch})"/>, with the sheet's own sketch at rest and the activated
    /// view's own sketch at rest only when the add-in activated that view (<paramref name="activatedByAddIn"/>, from
    /// <see cref="ViewActivations"/>). The 5.C live run (round 2) measured the user's Edit Sketch on a view's own sketch
    /// (section line A, in Drawing View1) making that sketch active just as the add-in's ActivateView does, so who
    /// activated the view is what tells them apart.
    /// </summary>
    public static string? UserEditing(Sketch? active, Sketch? sheet, string? activeView, Sketch? activeViewSketch, string? activatedByAddIn)
    {
        var atRest = new List<Sketch>();
        if (sheet is Sketch s) atRest.Add(s);
        if (activeViewSketch is Sketch v && activeView != null && string.Equals(activeView, activatedByAddIn, StringComparison.Ordinal)) atRest.Add(v);
        return UserEditing(active, atRest);
    }
}

/// <summary>
/// The view the add-in itself activated in each drawing (IDrawingDoc.ActivateView, for a note in a view or a line drawn
/// in its sketch), by the drawing's title, until the operation that activated it ends and the drawing is settled
/// (ISwDrawing.Settle). Only that view's own sketch is the drawing at rest; a view the user activated, or whose sketch
/// the user is editing, is the user editing (design 611, Drawing changes; the coxswain's decision in the 5.C live run).
/// Read and written on SOLIDWORKS' UI thread only.
/// </summary>
public static class ViewActivations
{
    static readonly Dictionary<string, string> Views = new Dictionary<string, string>(StringComparer.Ordinal);

    public static void Record(string drawing, string view) => Views[drawing] = view;

    public static string? ByAddIn(string drawing) => Views.TryGetValue(drawing, out var view) ? view : null;

    public static void Clear(string drawing) => Views.Remove(drawing);

    /// <summary>
    /// Puts a drawing back at rest (ISwDrawing.Settle). With <paramref name="adopt"/> (a drawing change ending: nothing but
    /// the add-in runs on SOLIDWORKS' UI thread while one does, and the guard refused it if the user had a view open), the
    /// view active now is the add-in's, whether it activated it or SOLIDWORKS did while inserting into it (the 5.C live run,
    /// round 6: a dimension inserted into Drawing View1 left it activated). The add-in's view is let go of by activating
    /// the sheet's own view (<paramref name="activate"/>), and forgotten once the drawing is at rest; a view the user
    /// activated is never touched. Returns the add-in's view still held when the drawing could not be put at rest (it
    /// stays recorded, so the next change is not refused for it, and the result says so); null otherwise.
    /// </summary>
    public static string? Settle(string drawing, string? activeView, string sheetView, bool adopt, Func<string, bool> activate, Func<bool> atRest)
    {
        bool aView = activeView != null && !string.Equals(activeView, sheetView, StringComparison.Ordinal);
        if (adopt && aView) Record(drawing, activeView!);
        var mine = ByAddIn(drawing);
        if (mine == null) return null;
        if (aView && string.Equals(activeView, mine, StringComparison.Ordinal)) activate(sheetView);
        if (!atRest()) return mine;
        Clear(drawing);
        return null;
    }
}
