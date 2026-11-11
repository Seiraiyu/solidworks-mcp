using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.Contracts;

namespace SwAgent.AddIn;

/// <summary>
/// Sketched bends, unfold and fold, and convert to sheet metal (design 454, Bends and flat pattern; Convert a solid),
/// through the calls CADapter measured building on 2026 SP4: InsertSheetMetal3dBend with the sketch selected and the
/// fixed face picked at a point inside it (CreateDefinition(swFmSketchBend) returns nothing); InsertSheetMetalUnfold and
/// InsertSheetMetalFold with the fixed face at mark 1 and each bend at mark 2; InsertConvertToSheetMetal2 with the fixed
/// face and every bend edge selected as entities. Proven by the sheet-metal live suite, not by unit tests, and never
/// trusted on their answer: the operations measure what was made.
/// </summary>
internal sealed partial class SolidWorksDocument
{
    /// <summary>SOLIDWORKS' sketched-bend position (swFlangePositionTypes_e, as a short); bend_centerline is not offered (measured a no-op).</summary>
    static short SketchedPosition(BendPosition position) => position switch
    {
        BendPosition.MaterialInside => (short)swFlangePositionTypes_e.swFlangePositionTypeMaterialInside,
        BendPosition.MaterialOutside => (short)swFlangePositionTypes_e.swFlangePositionTypeMaterialOutside,
        _ => (short)swFlangePositionTypes_e.swFlangePositionTypeBendOutside,
    };

    public object? SketchedBend(object sketch, Point3 fixedPoint, SketchedBendSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            Pick(sketch, append: false, mark: 0);
            // The fixed face by a point inside it, as CADapter did: the point says which side of the lines stays put.
            if (!Doc.Extension.SelectByID2("", "FACE", fixedPoint.X / Mm, fixedPoint.Y / Mm, fixedPoint.Z / Mm, true, 0, null, 0))
                throw new SwAgentException(ErrorCode.ApiError, System.FormattableString.Invariant($"SOLIDWORKS did not select the fixed face at ({fixedPoint.X:0.###}, {fixedPoint.Y:0.###}, {fixedPoint.Z:0.###}) mm"));
            var fm = Doc.FeatureManager;
            var cba = s.Allowance == null ? null : Write(fm.CreateCustomBendAllowance(), s.Allowance);
            return fm.InsertSheetMetal3dBend(s.AngleDeg * Rad, s.RadiusMm == null, (s.RadiusMm ?? 0) / Mm, s.Flip, SketchedPosition(s.Position), cba);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    /// <summary>The fixed face at mark 1 and each bend at mark 2: the selection Unfold and Fold need (CADapter; with another, SOLIDWORKS makes the feature and unfolds nothing).</summary>
    void PickFolds(object fixedFace, IReadOnlyList<object> bends)
    {
        Pick(fixedFace, append: false, mark: 1);
        foreach (var bend in bends) Pick(bend, append: true, mark: 2);
    }

    public void Unfold(object fixedFace, IReadOnlyList<object> bends)
    {
        Doc.ClearSelection2(true);
        try
        {
            PickFolds(fixedFace, bends);
            Doc.InsertSheetMetalUnfold();
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    public void Fold(object fixedFace, IReadOnlyList<object> bends)
    {
        Doc.ClearSelection2(true);
        try
        {
            PickFolds(fixedFace, bends);
            Doc.InsertSheetMetalFold();
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }

    public IReadOnlyList<FoldRecord> Folds()
    {
        var list = new List<FoldRecord>();
        foreach (var feature in TopFeatures())
        {
            string type = feature.GetTypeName2();
            if (type != SheetMetalParts.UnfoldType && type != SheetMetalParts.FoldType) continue;
            object? fixedFace = null;
            var bends = new List<string>();
            if (feature.GetDefinition() is IFoldsFeatureData data && data.AccessSelections(Doc, null))
            {
                try
                {
                    fixedFace = data.FixedFace;
                    foreach (var bend in data.Bends as object[] ?? new object[0])
                    {
                        string? name = bend switch
                        {
                            IFeature f => f.Name,
                            IFace2 face => (face.GetFeature() as IFeature)?.Name,
                            _ => null,
                        };
                        if (name != null) bends.Add(name);
                    }
                }
                finally
                {
                    data.ReleaseSelectionAccess();
                }
            }
            list.Add(new FoldRecord(feature, feature.Name, type == SheetMetalParts.UnfoldType, fixedFace, bends));
        }
        return list;
    }

    public void ConvertToSheet(object fixedFace, IReadOnlyList<object> bendEdges, ConvertSpec s)
    {
        Doc.ClearSelection2(true);
        try
        {
            // Entities, not coordinates: by coordinate SOLIDWORKS silently refuses a second edge (CADapter).
            Pick(fixedFace, append: false, mark: 0);
            foreach (var edge in bendEdges) Pick(edge, append: true, mark: 0);
            Doc.FeatureManager.InsertConvertToSheetMetal2(s.ThicknessMm / Mm, s.ReverseThickness, false, s.BendRadiusMm / Mm, s.GapMm / Mm,
                (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, 0.5, 0, 0.5, s.KeepBody);
        }
        finally
        {
            Doc.ClearSelection2(true);
        }
    }
}
