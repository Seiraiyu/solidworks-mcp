using System.ComponentModel;

namespace SwAgent.Contracts;

[Description("A point in the part's model space, in millimetres.")]
public sealed class Point3
{
    public Point3()
    {
    }

    public Point3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    [Description("X, mm.")]
    public double X { get; set; }

    [Description("Y, mm.")]
    public double Y { get; set; }

    [Description("Z, mm.")]
    public double Z { get; set; }

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

[Description("A direction in the part's model space, of unit length.")]
public sealed class Vector3
{
    public Vector3()
    {
    }

    public Vector3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    [Description("X component.")]
    public double X { get; set; }

    [Description("Y component.")]
    public double Y { get; set; }

    [Description("Z component.")]
    public double Z { get; set; }

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

[Description("An axis-aligned box in model space, in millimetres.")]
public sealed class Box3
{
    [Required]
    [Description("The corner with the smallest coordinates.")]
    public Point3 Min { get; set; } = new Point3();

    [Required]
    [Description("The corner with the largest coordinates.")]
    public Point3 Max { get; set; } = new Point3();
}
