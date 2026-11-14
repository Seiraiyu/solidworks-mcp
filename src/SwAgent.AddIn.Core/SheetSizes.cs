using System;
using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn.Core;

/// <summary>
/// The standard paper sizes a sheet can be (design 611, Sheets), in exact millimetres landscape (wider than tall): ISO
/// 216 a4 to a0, and ANSI Y14.1 a to e (8.5 x 11 in to 34 x 44 in, at exactly 25.4 mm to the inch, written out so they are the nearest doubles to the exact mm). A portrait sheet is the same
/// size turned. A sheet read back is named a size when its width and height are that size's within 0.01 mm.
/// </summary>
public static class SheetSizes
{
    public const double ToleranceMm = 0.01;

    public const double MinMm = 10;

    public const double MaxMm = 10000;

    static readonly Dictionary<SheetSize, (double Long, double Short)> Landscape = new Dictionary<SheetSize, (double, double)>
    {
        [SheetSize.A4] = (297, 210),
        [SheetSize.A3] = (420, 297),
        [SheetSize.A2] = (594, 420),
        [SheetSize.A1] = (841, 594),
        [SheetSize.A0] = (1189, 841),
        [SheetSize.A] = (279.4, 215.9),
        [SheetSize.B] = (431.8, 279.4),
        [SheetSize.C] = (558.8, 431.8),
        [SheetSize.D] = (863.6, 558.8),
        [SheetSize.E] = (1117.6, 863.6),
    };

    /// <summary>The width and height of a standard size lying <paramref name="orientation"/>, mm.</summary>
    public static (double WidthMm, double HeightMm) Of(SheetSize size, SheetOrientation orientation)
    {
        var (l, s) = Landscape[size];
        return orientation == SheetOrientation.Landscape ? (l, s) : (s, l);
    }

    /// <summary>Which way a sheet of this width and height lies: landscape unless it is taller than wide.</summary>
    public static SheetOrientation OrientationOf(double widthMm, double heightMm) =>
        heightMm > widthMm ? SheetOrientation.Portrait : SheetOrientation.Landscape;

    /// <summary>The standard size this width and height are, within 0.01 mm either way round; null for a custom size.</summary>
    public static SheetSize? Match(double widthMm, double heightMm)
    {
        var orientation = OrientationOf(widthMm, heightMm);
        foreach (var size in Landscape.Keys)
        {
            var (w, h) = Of(size, orientation);
            if (Math.Abs(w - widthMm) <= ToleranceMm && Math.Abs(h - heightMm) <= ToleranceMm) return size;
        }
        return null;
    }

    /// <summary>
    /// The width and height <paramref name="setup"/> asks for, mm, or null when it leaves the size as it is. invalid_argument
    /// naming the field by its JSON pointer under <paramref name="pointer"/> (sheet for document.new's sheet, or empty for a
    /// request's own fields: /width_mm) when a size and a
    /// width or height are both given, only one of width and height is, orientation comes without size, a custom size is
    /// outside 10 to 10000 mm, or a scale is not more than 0. A format must be an .SLDDRT path (checked by the caller,
    /// which knows whether it exists).
    /// </summary>
    public static (double WidthMm, double HeightMm)? Asked(SheetSetup setup, string pointer)
    {
        if (setup.Scale != null)
        {
            if (!(setup.Scale.Numerator > 0) || double.IsInfinity(setup.Scale.Numerator))
                throw Invalid(pointer + "/scale/numerator", $"the scale's numerator must be more than 0, not {setup.Scale.Numerator}");
            if (!(setup.Scale.Denominator > 0) || double.IsInfinity(setup.Scale.Denominator))
                throw Invalid(pointer + "/scale/denominator", $"the scale's denominator must be more than 0, not {setup.Scale.Denominator}");
        }
        bool custom = setup.WidthMm != null || setup.HeightMm != null;
        if (setup.Size != null && custom)
            throw Invalid(pointer + (setup.WidthMm != null ? "/width_mm" : "/height_mm"), "give a standard size or a width and height, not both");
        if (setup.Orientation != null && setup.Size == null)
            throw Invalid(pointer + "/orientation", "orientation goes with a standard size; a custom sheet's width and height say which way it lies");
        if (setup.Size is SheetSize size) return Of(size, setup.Orientation ?? SheetOrientation.Landscape);
        if (!custom) return null;
        if (setup.WidthMm == null) throw Invalid(pointer + "/width_mm", "a custom sheet needs width_mm as well as height_mm");
        if (setup.HeightMm == null) throw Invalid(pointer + "/height_mm", "a custom sheet needs height_mm as well as width_mm");
        foreach (var (field, value) in new[] { ("/width_mm", setup.WidthMm.Value), ("/height_mm", setup.HeightMm.Value) })
        {
            if (!(value >= MinMm && value <= MaxMm))
                throw Invalid(pointer + field, $"a sheet's {field.Substring(1).Replace("_mm", "")} must be {MinMm} to {MaxMm} mm, not {value}");
        }
        return (setup.WidthMm.Value, setup.HeightMm.Value);
    }

    /// <summary>Every standard size, for messages.</summary>
    public static string Names => string.Join(", ", Landscape.Keys.Select(k => WireNames.ForEnum(k)));

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, $"{field}: {message}", new JsonObject { ["field"] = field });
}
