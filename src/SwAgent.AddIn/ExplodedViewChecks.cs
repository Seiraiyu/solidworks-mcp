using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// Exploded assembly views on a drawing (design 611, Assembly drawings; plan 5.D). A drawing view shows its
/// configuration's exploded view (phase 4.E) through IView.ShowExploded, which names none: SOLIDWORKS draws the
/// configuration's exploded view. Where that puts each component is read in the assembly itself, as explode.list reads a
/// view (shown for a moment, then the assembly shows what it showed before), which SOLIDWORKS allows only for the
/// assembly's active configuration (4.E). An exploded view cannot be selected through the API (4.E), so nothing here
/// selects one.
/// </summary>
public static class ExplodedViewChecks
{
    /// <summary>
    /// The assembly <paramref name="view"/> shows and the exploded views (with steps) of <paramref name="configuration"/>
    /// (the assembly's active one when null) it can be shown in. invalid_argument naming <paramref name="field"/> for a view of a part; naming
    /// <paramref name="configurationField"/> for a configuration that is not the assembly's active one; and naming
    /// <paramref name="field"/>, pointing at sw_explode_create, when the configuration has no exploded view with steps.
    /// </summary>
    public static (ISwDocument Model, ISwAssembly Assembly, IReadOnlyList<string> Views) Require(ISwDrawing drawing, string view, string? configuration, string field, string configurationField)
    {
        var model = drawing.ModelOf(view) ?? throw Invalid(field, $"{field}: {view} shows no model that is loaded, so nothing to explode");
        var asm = model.Assembly() ?? throw Invalid(field, $"{field}: {view} shows {model.Info().Title}, a part; only a view of an assembly shows it exploded");
        string active = model.ActiveConfiguration();
        configuration ??= active;
        if (!string.Equals(active, configuration, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(configurationField,
                $"{configurationField}: {view} shows {configuration} of {model.Info().Title}, but its active configuration is {active}, and where an exploded view puts each component is read in the assembly, which shows exploded views only of its active configuration (SOLIDWORKS 2026 SP4); activate {configuration} with sw_configuration_activate on {model.Info().Title} first");
        }
        var views = asm.ExplodedViews(configuration).Where(v => asm.ExplodeSteps(v).Count > 0).ToList();
        if (views.Count == 0)
        {
            throw Invalid(field,
                $"{field}: {configuration} of {model.Info().Title} has no exploded view with steps to show (explode.list lists them); make one with sw_explode_create");
        }
        return (model, asm, views);
    }

    /// <summary>
    /// Each top-level component's placement and box collapsed and exploded in <paramref name="explodedView"/>, read in the
    /// assembly: collapsed first (any view shown exploded collapsed for it), then shown exploded; afterwards the assembly
    /// shows what it showed before.
    /// </summary>
    public static List<(string Name, Transform Collapsed, Box3? CollapsedBox, Transform Exploded, Box3? ExplodedBox)> Places(ISwAssembly asm, string explodedView)
    {
        string? shown = asm.ShownExploded();
        var places = new List<(string, Transform, Box3?, Transform, Box3?)>();
        try
        {
            if (shown != null) asm.ShowExploded(shown, false);
            var collapsed = asm.Components(topLevelOnly: true).Select(asm.Read).ToList();
            if (!asm.ShowExploded(explodedView, true))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS would not show {explodedView} in the assembly to read where it puts each component", new JsonObject { ["exploded_view"] = explodedView });
            var exploded = asm.Components(topLevelOnly: true).Select(asm.Read).ToList();
            foreach (var c in collapsed)
            {
                var e = exploded.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                if (e != null) places.Add((c.Name, c.Transform, c.Box, e.Transform, e.Box));
            }
        }
        finally
        {
            asm.ShowExploded(explodedView, false);
            if (shown != null) asm.ShowExploded(shown, true);
        }
        return places;
    }

    /// <summary>
    /// The exploded view <paramref name="view"/> draws, of <paramref name="views"/>, and each component's image: the first
    /// whose places the view's images fit (ExplodedImages.Check). verification_failed with the first view's failure when
    /// none fits.
    /// </summary>
    public static (string ExplodedView, List<ExplodedImage> Images) Check(ISwDrawing drawing, ISwAssembly asm, string view, IReadOnlyList<string> views)
    {
        var t = drawing.Transform(view) ?? throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS gives no transform for {view}");
        var drawn = drawing.ComponentImages(view);
        (string Check, string Component, string Expected, string Measured)? first = null;
        foreach (var exploded in views)
        {
            var components = Places(asm, exploded)
                .Select(p => new ComponentPlaces(p.Name, p.Collapsed, p.Exploded, p.ExplodedBox, p.CollapsedBox, drawn.TryGetValue(p.Name, out var box) ? box : null))
                .ToList();
            var (images, failure) = ExplodedImages.Check(t, components);
            if (failure == null) return (exploded, images);
            first ??= failure;
        }
        var f = first!.Value;
        throw new SwAgentException(ErrorCode.VerificationFailed,
            $"the view {view} failed its {f.Check} check{(f.Component.Length > 0 ? " for " + f.Component : "")}: expected {f.Expected}, measured {f.Measured}",
            new JsonObject { ["check"] = f.Check, ["view"] = view, ["component"] = f.Component, ["expected"] = f.Expected, ["measured"] = f.Measured, ["exploded_views"] = views.Select(v => (object?)v).ToList() });
    }

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });
}
