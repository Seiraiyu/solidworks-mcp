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

        // 3.D live run, round 1: HoleWizard5 made nothing with the sketch points selected and the cause could not be read,
        // so each way of telling it where the holes go is tried in turn, and what SOLIDWORKS did with each is reported.
        object? Attempt(string how, Action select)
        {
            Doc.ClearSelection2(true);
            try
            {
                select();
                int selected = SelectionCount();
                object? made = Doc.FeatureManager.HoleWizard5((int)generic, standard, (int)fastener, s.Size, end, -1, depth, -1,
                    -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, "", false, true, true, true, true, false);
                var found = made as IFeature ?? AllFeatures().FirstOrDefault(f => f.GetTypeName2() == "HoleWzd" && !before.Contains(f.Name));
                tried.Add($"{how}: {selected} selected, HoleWizard5 returned {(made == null ? "null" : "a feature")}" +
                          (made == null && found != null ? $", yet {found.Name} was made" : ""));
                return found;
            }
            catch (SwAgentException e)
            {
                tried.Add($"{how}: {e.Message}");
                return null;
            }
            finally
            {
                Doc.ClearSelection2(true);
            }
        }

        var points = s.Points.Cast<ISketchPoint>().ToList();
        var feature = Attempt("the sketch points (mark 0)", () =>
            {
                for (int i = 0; i < points.Count; i++)
                {
                    if (!points[i].Select4(i > 0, Marked(0)))
                        throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the sketch point a hole goes at");
                }
            })
            ?? Attempt("the position sketch (mark 0)", () =>
            {
                if (!(points.Count > 0 && points[0].GetSketch() is IFeature sketch && sketch.Select2(false, 0)))
                    throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the position sketch");
            })
            ?? Attempt("the face at the first position (SelectByID2 FACE)", () =>
            {
                var p = s.Positions[0];
                if (!Doc.Extension.SelectByID2("", "FACE", p.X / Mm, p.Y / Mm, p.Z / Mm, false, 0, null, 0))
                    throw new SwAgentException(ErrorCode.ApiError, "SOLIDWORKS did not select the face at the first position");
            });
        s.How = string.Join("; ", tried);
        if (feature != null) return feature;
        // Round 2: null all three ways, the face selected too, so the size or the table indexes are in doubt: say what
        // SOLIDWORKS' Hole Wizard tables list for this hole type.
        s.How += "; " + HoleTables((int)generic, s.Size);
        throw new SwAgentException(ErrorCode.OperationFailed,
            $"SOLIDWORKS made no {WireNames.ForEnum(s.Type)} {s.Size} hole at {s.Positions.Count} positions (hole type {generic}, standard {(swWzdHoleStandards_e)standard}, " +
            $"fastener {fastener}); check that {s.Size} is a {WireNames.ForEnum(s.Standard)} size the Hole Wizard lists for a {WireNames.ForEnum(s.Type)} hole. Tried: {s.How}",
            new SwAgent.Contracts.Serialization.JsonObject { ["tried"] = s.How });
    }

    /// <summary>
    /// What ISldWorks.GetHoleStandardsData lists for <paramref name="generic"/>: each standard's index and name, the
    /// fastener types of the ISO and ANSI metric ones, and the first sizes of each of their tables, with whether
    /// <paramref name="size"/> is among them. Diagnostic only: any failure is reported, never thrown.
    /// </summary>
    string HoleTables(int generic, string size)
    {
        var text = new List<string>();
        static string[] Items(object? o) => o switch
        {
            null => new string[0],
            Array a => a.Cast<object?>().Select(x => x is Array inner ? "[" + string.Join(", ", Items(inner)) + "]" : Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToArray(),
            _ => new[] { Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) ?? "" },
        };
        try
        {
            if (!(_sw.GetHoleStandardsData(generic) is IHoleStandardsData data)) return $"GetHoleStandardsData({generic}) returned nothing";
            if (!data.GetHoleStandards(out object indexes, out object names)) return "GetHoleStandards returned false";
            var ids = Items(indexes);
            var stds = Items(names);
            text.Add("standards: " + string.Join(", ", stds.Select((n, i) => $"{(i < ids.Length ? ids[i] : "?")}={n}")));
            foreach (var std in stds.Where(n => n.IndexOf("ISO", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Metric", StringComparison.OrdinalIgnoreCase) >= 0).Take(2))
            {
                if (!data.GetFastenerTypes(std, out object fIds, out object fNames)) { text.Add($"{std}: GetFastenerTypes false"); continue; }
                var fi = Items(fIds);
                var fn = Items(fNames);
                text.Add($"{std} fasteners: " + string.Join(", ", fn.Select((n, i) => $"{(i < fi.Length ? fi[i] : "?")}={n}")));
                foreach (var id in fi.Take(3))
                {
                    if (!int.TryParse(id, out int fastener)) continue;
                    if (!data.GetFastenerTableTypes(std, fastener, out object tables)) continue;
                    foreach (var table in Items(tables).Take(1))
                    {
                        if (!int.TryParse(table, out int tid) || !data.GetFastenerTable(std, fastener, tid, out object rows)) continue;
                        var all = Items(rows);
                        bool has = all.Any(r => r.IndexOf(size, StringComparison.Ordinal) >= 0);
                        text.Add($"{std} fastener {id} table {tid} ({all.Length} entries, '{size}' {(has ? "listed" : "not listed")}): {string.Join(" | ", all.Take(6))}");
                    }
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
