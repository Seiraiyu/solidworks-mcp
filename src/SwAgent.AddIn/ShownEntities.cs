using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// References for what a drawing view shows (view.entities, an annotation's attachments). SP4 hands back proxies for a
/// view's entities whose persistent ids are twins (5.C live run, round 2: the front view's top and bottom edges had one
/// reference, its left and right edges another, and the part resolved none of them), so each reference is issued from
/// the model's own entity (ISwDrawing.ModelEntities) and kept only when it is verified: it resolves in the model, and that
/// entity maps back into the view (ISwDrawing.InView) to the very entity shown. One that cannot be verified, or that two
/// entities of the view would share, is not issued: null, never a twin.
/// </summary>
public static class ShownEntities
{
    /// <summary>The verified reference for <paramref name="shown"/>, or null.</summary>
    public static string? Reference(ReferenceService references, ISwDocument model, ISwDrawing drawing, string view, object shown)
    {
        foreach (var own in drawing.ModelEntities(view, shown).Concat(new[] { shown }))
        {
            if (model.KindOf(own) == null) continue;
            string? reference;
            object back;
            try
            {
                reference = references.Describe(model, own).Reference;
                if (reference == null) continue;
                back = references.Resolve(model, reference, "").Entity;
            }
            catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException)
            {
                continue;
            }
            if (drawing.InView(view, back) is object drawn && drawing.SameEntity(drawn, shown)) return reference;
        }
        return null;
    }

    /// <summary>The verified references for each of <paramref name="shown"/>, in order; any two would share are dropped from both.</summary>
    public static IReadOnlyList<string?> References(ReferenceService references, ISwDocument model, ISwDrawing drawing, string view, IReadOnlyList<object> shown)
    {
        var refs = shown.Select(s => Reference(references, model, drawing, view, s)).ToList();
        var twins = new HashSet<string>(refs.Where(r => r != null).GroupBy(r => r!, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.Ordinal);
        return refs.Select(r => r != null && twins.Contains(r) ? null : r).ToList();
    }
}
