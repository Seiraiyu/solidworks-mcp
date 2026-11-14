using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Operations;

/// <summary>What the annotation operations share: finding what a change made, and how a check that fails is answered.</summary>
static class Annotating
{
    /// <summary>verification_failed for an annotation that failed a check, with what was expected and what was measured.</summary>
    public static SwAgentException Failed(string annotation, string check, string expected, string measured) =>
        new SwAgentException(ErrorCode.VerificationFailed,
            $"the annotation {annotation} failed its {check} check: expected {expected}, measured {measured}",
            new JsonObject { ["check"] = check, ["annotation"] = annotation, ["expected"] = expected, ["measured"] = measured });

    /// <summary>
    /// Of the annotations a change made (<paramref name="made"/>, every one already recorded to roll back), the one
    /// SOLIDWORKS answered with <paramref name="name"/>. operation_failed when it answered none, or a name it did not make:
    /// SOLIDWORKS made no <paramref name="what"/>, saying what appeared instead.
    /// </summary>
    public static AnnotationFacts Mine(IReadOnlyList<AnnotationFacts> made, string? name, string what)
    {
        var mine = name == null ? null : made.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
        if (mine != null) return mine;
        throw new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS made no {what}" + (made.Count > 0 ? $", although {string.Join(", ", made.Select(m => $"{m.Name} in {DrawingAnnotations.Where(m)}"))} appeared" : ""),
            new JsonObject { ["made"] = made.Select(m => (object?)m.Name).ToList() });
    }

    /// <summary>The annotation <paramref name="made"/> names, read again now; verification_failed when it is gone.</summary>
    public static AnnotationFacts Reread(ISwDrawing drawing, AnnotationFacts made) =>
        DrawingAnnotations.Read(drawing, made.Sheet, made.View, made.Name) ?? throw Failed(made.Name, "exists", $"on the drawing in {DrawingAnnotations.Where(made)}", "no such annotation");
}
