using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Phase 3.D's features, through IFeatureManager and IModelDoc2 (design 371, Features). UI thread only. As in
/// SolidWorksFeatures.cs, each call selects what SOLIDWORKS reads with its marks, makes the feature and clears the
/// selection; millimetres and degrees become metres and radians here. Covered by the parts live suite, not unit tests.
/// </summary>
internal sealed partial class SolidWorksDocument
{
    /// <summary>
    /// InsertProtrusionSwept4 or InsertCutSwept5 with the profile selected with mark 1, the path with mark 4 and each
    /// guide with mark 2: the profile follows the path (twist control 0) without tangency or thin wall.
    /// </summary>
    public object? Sweep(object profile, SweepSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(profile, append: false, mark: 1);
            Pick(s.Path, append: true, mark: 4);
            foreach (var guide in s.Guides) Pick(guide, append: true, mark: 2);
            var fm = Doc.FeatureManager;
            const int follow = (int)swTwistControlType_e.swTwistControlFollowPath;
            const int none = (int)swTangencyType_e.swTangencyNone;
            return s.Cut
                ? fm.InsertCutSwept5(false, true, follow, false, false, none, none, false, 0, 0, 0, none, true, true, 0, true,
                    false, false, false, false, 0, 0)
                : fm.InsertProtrusionSwept4(false, true, follow, false, false, none, none, false, 0, 0, 0, none, s.Merge, true, true, 0, true,
                    false, 0, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertProtrusionBlend2 or InsertCutBlend with the profiles selected in order with mark 1 and the guides with
    /// mark 2: an open loft, no start or end tangency, no thin wall.
    /// </summary>
    public object? Loft(LoftSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            for (int i = 0; i < s.Profiles.Count; i++) Pick(s.Profiles[i], append: i > 0, mark: 1);
            foreach (var guide in s.Guides) Pick(guide, append: true, mark: 2);
            var fm = Doc.FeatureManager;
            const short none = (short)swTangencyType_e.swTangencyNone;
            return s.Cut
                ? fm.InsertCutBlend(false, true, false, 1, none, none, false, 0, 0, 0, true, true)
                : fm.InsertProtrusionBlend2(false, true, false, 1, none, none, 1, 1, true, true, false, 0, 0, 0, s.Merge, true, true, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertRib with the sketch selected (mark 0). It returns nothing, so the new rib is the Rib feature that was not in
    /// the tree before.
    /// </summary>
    public object? Rib(object sketch, RibSpec s)
    {
        return NewFeature("Rib", () =>
        {
            Pick(sketch, append: false, mark: 0);
            Doc.FeatureManager.InsertRib(s.Side == RibSide.Both, s.Side == RibSide.OtherSide, s.ThicknessMm / Mm, 0, s.FlipMaterial,
                s.DraftDeg > 0, s.DraftOutward, s.DraftDeg * Rad, s.NormalToSketch, false);
        });
    }

    /// <summary>Runs <paramref name="make"/> with the selection cleared before and after; the feature of SOLIDWORKS type <paramref name="type"/> it added, or null.</summary>
    IFeature? NewFeature(string type, Action make)
    {
        var before = new HashSet<string>(AllFeatures().Where(f => f.GetTypeName2() == type).Select(f => f.Name), StringComparer.Ordinal);
        Doc.ClearSelection2(true);
        try
        {
            make();
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
        return AllFeatures().FirstOrDefault(f => f.GetTypeName2() == type && !before.Contains(f.Name));
    }

    /// <summary>IModelDoc2.InsertFeatureShell with the faces to remove selected (mark 0); it returns nothing, so the new shell is the Shell feature that was not in the tree before.</summary>
    public object? Shell(IReadOnlyList<object> facesToRemove, double thicknessMm, bool outward)
    {
        return NewFeature("Shell", () =>
        {
            for (int i = 0; i < facesToRemove.Count; i++) Pick(facesToRemove[i], append: i > 0, mark: 0);
            Doc.InsertFeatureShell(thicknessMm / Mm, outward);
        });
    }

    /// <summary>
    /// InsertMultiFaceDraft, a neutral-plane draft without propagation, with the neutral plane selected with mark 1 and
    /// the faces with mark 2.
    /// </summary>
    public object? Draft(DraftSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(s.Neutral, append: false, mark: 1);
            foreach (var face in s.Faces) Pick(face, append: true, mark: 2);
            return Doc.FeatureManager.InsertMultiFaceDraft(s.AngleDeg * Rad, s.Reverse, false,
                (int)swDraftFacePropagationType_e.swFacePropNone, false, false);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// HoleWizard5 with the operation's sketch points selected (mark 0), so a hole goes at each. The fastener table is
    /// the type's in the standard (drill sizes, socket head cap screw, countersunk flat head, tapped hole), the size is
    /// the Hole Wizard's own name for it, and every dimension the table gives is left to it (-1).
    /// </summary>
    public object? Hole(HoleSpec s)
    {
        bool iso = s.Standard == HoleStandard.Iso;
        var (generic, fastener) = s.Type switch
        {
            HoleType.Counterbore => (swWzdGeneralHoleTypes_e.swWzdCounterBore,
                iso ? swWzdHoleStandardFastenerTypes_e.swStandardISOSocketHeadCap : swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricSocketHeadCapScrew),
            HoleType.Countersink => (swWzdGeneralHoleTypes_e.swWzdCounterSink,
                iso ? swWzdHoleStandardFastenerTypes_e.swStandardISOCTSKFlatHead : swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricFlatHead82),
            HoleType.Tapped => (swWzdGeneralHoleTypes_e.swWzdTap,
                iso ? swWzdHoleStandardFastenerTypes_e.swStandardISOTappedHole : swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricTappedHole),
            _ => (swWzdGeneralHoleTypes_e.swWzdHole,
                iso ? swWzdHoleStandardFastenerTypes_e.swStandardISODrillSizes : swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricDrillSizes),
        };
        int standard = (int)(iso ? swWzdHoleStandards_e.swStandardISO : swWzdHoleStandards_e.swStandardAnsiMetric);
        short end = (short)(s.End == HoleEnd.ThroughAll ? swEndConditions_e.swEndCondThroughAll : swEndConditions_e.swEndCondBlind);
        double depth = s.End == HoleEnd.ThroughAll ? -1 : s.DepthMm / Mm;
        var before = new HashSet<string>(AllFeatures().Select(f => f.Name));
        var tried = new List<string>();
        var points = s.Points.Cast<ISketchPoint>().ToList();
        // The size's nominal, mm (5 for Ø5.0 or M5), for the try that gives the diameter rather than -1.
        var nominal = System.Text.RegularExpressions.Regex.Match(s.Size, @"\d+(\.\d+)?");
        double? nominalMm = nominal.Success ? double.Parse(nominal.Value, System.Globalization.CultureInfo.InvariantCulture) : (double?)null;

        // 3.D live run: HoleWizard5 returned null in rounds 1 to 3 with the sketch points, the position sketch and the face
        // selected, the standard (8) and fastener table (143) indexes being SOLIDWORKS' own. So, with the sketch points
        // selected each time (mark 0), the call is tried as before, then (a simple hole) with its diameter given instead of
        // -1, then through CreateDefinition / InitializeHole / CreateFeature, reading back what the definition took.
        object? Attempt(string how, Func<(object? Made, string Said)> make)
        {
            Doc.ClearSelection2(true);
            try
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (!points[i].Select4(i > 0, Marked(0)))
                        throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch point a hole goes at");
                }
                int selected = SelectionCount();
                var (made, said) = make();
                var found = made as IFeature ?? AllFeatures().FirstOrDefault(f => f.GetTypeName2() == "HoleWzd" && !before.Contains(f.Name));
                tried.Add($"{how}: {selected} selected, {said}" + (made == null && found != null ? $", yet {found.Name} was made" : ""));
                return found;
            }
            catch (Exception e) when (e is SwAgentException || e is System.Runtime.InteropServices.COMException || e is InvalidCastException)
            {
                tried.Add($"{how}: {e.GetType().Name}: {e.Message}");
                return null;
            }
            finally
            {
                Doc.ClearSelection2(true);
            }
        }

        (object?, string) Wizard5(double diameter, double value)
        {
            object? made = Doc.FeatureManager.HoleWizard5((int)generic, standard, (int)fastener, s.Size, end, diameter, depth, -1,
                value, value, value, value, value, value, value, value, value, value, value, value, "", false, true, true, true, true, false);
            return (made, $"HoleWizard5 returned {(made == null ? "null" : "a feature")}");
        }

        var feature = Attempt("HoleWizard5, every table value -1", () => Wizard5(-1, -1));
        if (feature == null && s.Type == HoleType.Simple && nominalMm != null)
            feature = Attempt(FormattableString.Invariant($"HoleWizard5 with the diameter {nominalMm} mm and the other values 0"), () => Wizard5(nominalMm.Value / Mm, 0));
        if (feature == null)
        {
            feature = Attempt("CreateDefinition(swFmHoleWzd) + InitializeHole + CreateFeature", () =>
            {
                if (!(Doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmHoleWzd) is IWizardHoleFeatureData2 data))
                    return (null, "CreateDefinition returned no hole definition");
                data.InitializeHole((int)generic, standard, (int)fastener, s.Size, end);
                if (s.End == HoleEnd.Blind) data.HoleDepth = depth;
                string took = FormattableString.Invariant($"the definition took size '{data.FastenerSize}', standard {data.Standard2}, fastener {data.FastenerType2}, hole diameter {data.HoleDiameter * Mm:0.###} mm");
                object? made = Doc.FeatureManager.CreateFeature(data);
                return (made, $"{took}; CreateFeature returned {(made == null ? "null" : "a feature")}");
            });
        }
        s.How = string.Join("; ", tried);
        if (feature != null) return feature;
        s.How += "; " + HoleTables((int)generic, (int)fastener, s.Size);
        throw new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS made no {WireNames.ForEnum(s.Type)} {s.Size} hole at {s.Positions.Count} positions (hole type {generic}, standard {(swWzdHoleStandards_e)standard}, " +
            $"fastener {fastener}); check that {s.Size} is a {WireNames.ForEnum(s.Standard)} size the Hole Wizard lists for a {WireNames.ForEnum(s.Type)} hole. Tried: {s.How}",
            new SwAgent.Contracts.Serialization.JsonObject { ["tried"] = s.How });
    }

    /// <summary>
    /// What ISldWorks.GetHoleStandardsData lists for <paramref name="generic"/>: each standard's index and name, the ISO
    /// and ANSI metric fastener types, and every table of <paramref name="fastener"/> in each, read in-process through
    /// the rows' own IDispatch type information (the interop declares no interface for them; round 3 printed only
    /// System.__ComObject), with whether <paramref name="size"/> is among them. Diagnostic only: never throws.
    /// </summary>
    string HoleTables(int generic, int fastener, string size)
    {
        var text = new List<string>();
        try
        {
            if (!(_sw.GetHoleStandardsData(generic) is IHoleStandardsData data)) return $"GetHoleStandardsData({generic}) returned nothing";
            if (!data.GetHoleStandards(out object indexes, out object names)) return "GetHoleStandards returned false";
            var ids = ComRead.Items(indexes);
            var stds = ComRead.Items(names);
            text.Add("standards: " + string.Join(", ", stds.Select((n, i) => $"{(i < ids.Length ? ids[i] : "?")}={n}")));
            foreach (var std in stds.Where(n => n == "ISO" || n == "ANSI Metric"))
            {
                if (!data.GetFastenerTypes(std, out object fIds, out object fNames)) { text.Add($"{std}: GetFastenerTypes false"); continue; }
                var fi = ComRead.Items(fIds);
                var fn = ComRead.Items(fNames);
                text.Add($"{std} fasteners: " + string.Join(", ", fn.Select((n, i) => $"{(i < fi.Length ? fi[i] : "?")}={n}")));
                if (!fi.Contains(fastener.ToString(System.Globalization.CultureInfo.InvariantCulture))) continue;
                if (!data.GetFastenerTableTypes(std, fastener, out object tables)) { text.Add($"{std} fastener {fastener}: GetFastenerTableTypes false"); continue; }
                foreach (var table in ComRead.Items(tables))
                {
                    if (!int.TryParse(table, out int tid)) continue;
                    if (!data.GetFastenerTable(std, fastener, tid, out object rows)) { text.Add($"{std} fastener {fastener} table {tid}: GetFastenerTable false"); continue; }
                    string read = ComRead.Describe(rows, 3);
                    bool has = read.IndexOf(size, StringComparison.Ordinal) >= 0;
                    text.Add($"{std} fastener {fastener} table {tid} ('{size}' {(has ? "listed" : "not listed")}; U+{string.Join(" U+", size.Select(ch => ((int)ch).ToString("X4")))}): " +
                             (read.Length > 2500 ? read.Substring(0, 2500) + "…" : read));
                }
            }
        }
        catch (Exception e)
        {
            text.Add($"reading the hole tables failed: {e.GetType().Name}: {e.Message}");
        }
        return "Hole Wizard tables: " + string.Join("; ", text);
    }

    /// <summary>
    /// FeatureLinearPattern5 with the first direction selected with mark 1, the second with mark 2, the seeds with mark 4
    /// or the faces with mark 128 (a face pattern is a geometry pattern): spacing and count in each direction, no
    /// offsets or skipped instances.
    /// </summary>
    public object? LinearPattern(LinearPatternSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(s.Direction, append: false, mark: 1);
            if (s.Direction2 != null) Pick(s.Direction2, append: true, mark: 2);
            foreach (var seed in s.Seeds) Pick(seed, append: true, mark: 4);
            foreach (var face in s.Faces) Pick(face, append: true, mark: 128);
            return Doc.FeatureManager.FeatureLinearPattern5(s.Count, s.SpacingMm / Mm, s.Count2, s.Direction2 == null ? 0 : s.SpacingMm2 / Mm,
                s.Reverse, s.Reverse2, "NULL", "NULL", s.Faces.Count > 0, false, false, false, false, false, false, false, false, false, 0, 0, false, false);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>How many instances a pattern or mirror has, the seed included, as its definition reads back (less any skipped).</summary>
    public int PatternInstances(object feature)
    {
        var f = (IFeature)feature;
        object definition = f.GetDefinition();
        int skipped(object? array) => array is object[] items ? items.Length : array is int[] ints ? ints.Length : 0;
        return definition switch
        {
            ILinearPatternFeatureData l => l.D1TotalInstances * Math.Max(1, l.D2TotalInstances) - skipped(l.SkippedItemArray),
            ICircularPatternFeatureData c => c.TotalInstances * (c.Direction2 ? Math.Max(1, c.TotalInstances2) : 1) - skipped(c.SkippedItemArray),
            IMirrorPatternFeatureData _ => 2,
            _ => 0,
        };
    }

    /// <summary>
    /// FeatureCircularPattern5 with the axis selected with mark 1, the seeds with mark 4 or the faces with mark 128 (a
    /// geometry pattern): one direction, the angle total (equal spacing) or between neighbours, no skipped instances.
    /// </summary>
    public object? CircularPattern(CircularPatternSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(s.Axis, append: false, mark: 1);
            foreach (var seed in s.Seeds) Pick(seed, append: true, mark: 4);
            foreach (var face in s.Faces) Pick(face, append: true, mark: 128);
            return Doc.FeatureManager.FeatureCircularPattern5(s.Count, s.AngleDeg * Rad, s.Reverse, "NULL", s.Faces.Count > 0, s.EqualSpacing, false, false,
                false, false, 1, 0, "NULL", false);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>
    /// InsertMirrorFeature2 with the plane selected with mark 2 and the seeds with mark 1, or (a body mirror) every solid
    /// body with mark 256, merged with the original.
    /// </summary>
    public object? Mirror(MirrorSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(s.Plane, append: false, mark: 2);
            if (s.Body)
            {
                var bodies = ((IPartDoc)Doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? Array.Empty<object>();
                foreach (var b in bodies.OfType<IBody2>())
                {
                    if (!b.Select2(true, Marked(256))) throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the body to mirror");
                }
            }
            else
            {
                foreach (var seed in s.Seeds) Pick(seed, append: true, mark: 1);
            }
            return Doc.FeatureManager.InsertMirrorFeature2(s.Body, false, true, false, 0);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    public IReadOnlyList<string> ConfigurationNames() =>
        Doc.GetConfigurationNames() is object names ? ((System.Collections.IEnumerable)names).OfType<string>().ToList() : new List<string>();

    public string ActiveConfiguration() => ((IConfiguration)Doc.GetActiveConfiguration()).Name;

    /// <summary>IConfigurationManager.AddConfiguration2: a copy of the active configuration, rebuilt.</summary>
    public bool AddConfiguration(string name) => Doc.ConfigurationManager.AddConfiguration2(name, "", "", 0, "", "", true) != null;

    public bool ShowConfiguration(string name) => Doc.ShowConfiguration2(name);

    public bool DeleteConfiguration(string name) => Doc.DeleteConfiguration2(name);
}
