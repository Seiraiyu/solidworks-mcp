using System;
using System.IO;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// What document.new's sheet, sheet.add and sheet.set share (design 611, Sheets): checking the values asked before
/// SOLIDWORKS is called, the sheet values they make, and whether a sheet reads back as asked (sizes within 0.01 mm,
/// the scale exactly, the projection, the format's path).
/// </summary>
public static class SheetSetups
{
    /// <summary>
    /// invalid_argument naming the field by its JSON pointer under <paramref name="pointer"/> (sheet/format in document.new,
    /// /format in sheet.add) for anything SheetSizes refuses, or a format that is not an existing .SLDDRT file.
    /// </summary>
    public static void Validate(SheetSetup setup, string pointer)
    {
        SheetSizes.Asked(setup, pointer);
        if (setup.Format != null) setup.Format = PathRules.Existing(setup.Format, Field(pointer, "format"), ".SLDDRT");
    }

    /// <summary>The values a sheet gets: <paramref name="from"/>'s, with what <paramref name="setup"/> asks for, named <paramref name="name"/> when given.</summary>
    public static SheetFacts Apply(SheetFacts from, SheetSetup setup, string? name)
    {
        var wanted = from.Copy();
        if (name != null) wanted.Name = name;
        if (SheetSizes.Asked(setup, "") is (double w, double h))
        {
            wanted.WidthMm = w;
            wanted.HeightMm = h;
        }
        if (setup.Scale != null) wanted.Scale = new ScaleRatio(setup.Scale.Numerator, setup.Scale.Denominator);
        if (setup.Projection != null) wanted.Projection = setup.Projection.Value;
        if (setup.Format != null) wanted.Format = Path.GetFullPath(setup.Format);
        return wanted;
    }

    /// <summary>Null when <paramref name="actual"/> reads back as <paramref name="wanted"/>; otherwise the check that failed, what was asked and what was read.</summary>
    public static (string Check, string Expected, string Measured)? Mismatch(SheetFacts wanted, SheetFacts actual)
    {
        if (Math.Abs(actual.WidthMm - wanted.WidthMm) > SheetSizes.ToleranceMm || Math.Abs(actual.HeightMm - wanted.HeightMm) > SheetSizes.ToleranceMm)
            return ("size", Size(wanted), Size(actual));
        if (!DrawingReader.SameScale(actual.Scale, wanted.Scale)) return ("scale", wanted.Scale.ToString(), actual.Scale.ToString());
        if (actual.Projection != wanted.Projection) return ("projection", WireNames.ForEnum(wanted.Projection), WireNames.ForEnum(actual.Projection));
        if (wanted.Format != null && !(actual.Format != null && string.Equals(Full(actual.Format), Full(wanted.Format), StringComparison.OrdinalIgnoreCase)))
            return ("format", wanted.Format, actual.Format ?? "none");
        if (wanted.Name.Length > 0 && !string.Equals(actual.Name, wanted.Name, StringComparison.Ordinal)) return ("name", wanted.Name, actual.Name);
        return null;
    }

    /// <summary>verification_failed for a sheet that did not read back as asked, with what was asked and read.</summary>
    public static SwAgentException NotAsAsked(string sheet, (string Check, string Expected, string Measured) mismatch) =>
        new SwAgentException(ErrorCode.VerificationFailed,
            $"the sheet {sheet} did not take its {mismatch.Check}: asked {mismatch.Expected}, read back {mismatch.Measured}",
            new JsonObject { ["check"] = mismatch.Check, ["sheet"] = sheet, ["expected"] = mismatch.Expected, ["measured"] = mismatch.Measured });

    /// <summary>
    /// The sheet format file SOLIDWORKS names (ISheet.GetTemplateName), or null when the name is not a full path to a file:
    /// the default drawing template's sheet reads "*.drt", its format held in the template rather than in a file (5.A live run).
    /// </summary>
    public static string? FormatFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name!.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.IndexOfAny(new[] { '*', '?' }) >= 0) return null;
        return Path.IsPathRooted(name) && Path.GetFileName(name).Length > 0 ? name : null;
    }

    /// <summary>A sheet's values for a ChangedItem.</summary>
    public static string Describe(SheetFacts s) =>
        $"{Size(s)}, {s.Scale}, {WireNames.ForEnum(s.Projection)}{(s.Format == null ? "" : ", " + Name(s.Format))}";

    static string Full(string path) => FormatFile(path) is string file ? Path.GetFullPath(file) : path;

    static string Name(string path) => FormatFile(path) is string file ? Path.GetFileName(file) : path;

    static string Size(SheetFacts s) =>
        FormattableString.Invariant($"{s.WidthMm:0.##} x {s.HeightMm:0.##} mm");

    static string Field(string pointer, string name) => pointer + "/" + name;
}
