using System.Collections.Generic;
using System.Linq;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Core;

/// <summary>One slot IComponent2.GetRemainingDOFs fills: its swDofStatus_e, its direction, and for a rotation a point on its axis (mm).</summary>
public sealed class DofSlot
{
    public DofSlot(int status, Vector3? direction, Point3? through = null)
    {
        Status = status;
        Direction = direction;
        Through = through;
    }

    public int Status { get; }

    public Vector3? Direction { get; }

    public Point3? Through { get; }
}

/// <summary>What IComponent2.GetRemainingDOFs answered: its swRemainingDofs_e, two rotation slots and two translation slots.</summary>
public sealed class RemainingDofs
{
    public int Result { get; set; }

    public List<DofSlot> Rotations { get; } = new List<DofSlot>();

    public List<DofSlot> Translations { get; } = new List<DofSlot>();
}

/// <summary>
/// A component's free motion (design 455, Degrees of freedom), from its constrained state and what
/// GetRemainingDOFs gives; never probed by moving anything. Where SOLIDWORKS cannot describe it, the counts are absent
/// and the note says why.
/// </summary>
public static class Freedom
{
    // swRemainingDofs_e
    public const int Restricted = 0;
    public const int Unrestricted = 1;
    public const int Unavailable = 2;
    public const int Failed = 3;
    public const int RootComponent = 4;

    /// <summary>swDofStatus_Free (3) and swDofStatus_FreeNormal (4).</summary>
    public static bool IsFree(int status) => status == 3 || status == 4;

    public static ComponentFreedom Of(string name, Constraint constraint, int mates, Transform at, RemainingDofs? dofs)
    {
        var f = new ComponentFreedom { Name = name, Constraint = constraint };
        switch (constraint)
        {
            case Constraint.Fixed:
            case Constraint.FullyDefined:
                f.FreeTranslations = 0;
                f.FreeRotations = 0;
                return f;
            case Constraint.OverDefined:
                f.Note = "its mates conflict, so SOLIDWORKS gives no free motion; mate.list shows each mate's error";
                return f;
            case Constraint.Unknown:
                f.Note = "SOLIDWORKS could not say how far its position is defined";
                return f;
        }
        if (mates == 0)
        {
            var axes = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
            f.Translations.AddRange(axes);
            f.Rotations.AddRange(axes.Select(a => new FreeRotation { Axis = a, Through = at.Origin }));
            f.FreeTranslations = 3;
            f.FreeRotations = 3;
            f.Note = "no mates: free in every direction";
            return f;
        }
        if (dofs == null)
        {
            f.Note = "SOLIDWORKS gave no remaining degrees of freedom (GetRemainingDOFs)";
            return f;
        }
        if (dofs.Result != Restricted)
        {
            f.Note = dofs.Result switch
            {
                Unrestricted => "it can move in more ways than SOLIDWORKS describes (more than two slides or two turns)",
                // Live 4.C run: every under-defined, mated component answered this.
                Unavailable => "SOLIDWORKS 2026 SP4's GetRemainingDOFs did not report free motion (swRemainingDofs_Unavailable; plan 4.C live run); constrained state only",
                Failed => "SOLIDWORKS failed to work out its remaining degrees of freedom (swRemainingDofs_Failed)",
                RootComponent => "SOLIDWORKS answered as for the root component (swRemainingDofs_RootComponent)",
                _ => $"SOLIDWORKS answered swRemainingDofs_e {dofs.Result}",
            };
            return f;
        }
        foreach (var t in dofs.Translations.Where(s => IsFree(s.Status) && s.Direction != null)) f.Translations.Add(t.Direction!);
        foreach (var r in dofs.Rotations.Where(s => IsFree(s.Status) && s.Direction != null)) f.Rotations.Add(new FreeRotation { Axis = r.Direction!, Through = r.Through ?? at.Origin });
        if (f.Translations.Count + f.Rotations.Count == 0)
        {
            f.Note = "SOLIDWORKS reported no free motion although the component is under defined";
            return f;
        }
        f.FreeTranslations = f.Translations.Count;
        f.FreeRotations = f.Rotations.Count;
        return f;
    }
}
