using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// The real assembly, through IAssemblyDoc and IComponent2. UI thread only. Converts metres to millimetres here and in
/// <see cref="Placement"/>, nowhere else. Covered by the assemblies live suite, not by unit tests.
/// </summary>
internal sealed class SolidWorksAssembly : ISwAssembly
{
    const double Mm = 1000.0;

    readonly ISldWorks _sw;
    readonly IModelDoc2 _doc;
    readonly IAssemblyDoc _asm;

    public SolidWorksAssembly(ISldWorks sw, IModelDoc2 doc)
    {
        _sw = sw;
        _doc = doc;
        _asm = (IAssemblyDoc)doc;
    }

    public IReadOnlyList<object> Components(bool topLevelOnly)
    {
        var list = new List<object>();
        if (!(_doc.ConfigurationManager?.ActiveConfiguration?.GetRootComponent3(true) is IComponent2 root)) return list;
        Walk(root, () => _doc.FirstFeature() as IFeature, topLevelOnly, list);
        return list;
    }

    /// <summary>
    /// Each level in FeatureManager tree order: GetChildren does not promise it (4.A live run, round 5), so the children
    /// are sorted by the component features ("Reference") of the parent's own feature tree.
    /// </summary>
    static void Walk(IComponent2 parent, Func<IFeature?> firstFeature, bool topLevelOnly, List<object> into)
    {
        if (!(parent.GetChildren() is object[] children)) return;
        var kids = children.OfType<IComponent2>().ToList();
        if (kids.Count == 0) return;
        foreach (var child in TreeOrder.Sort(kids, TreeNames(firstFeature), c => Leaf(c.Name2)))
        {
            into.Add(child);
            if (!topLevelOnly) Walk(child, () => child.FirstFeature() as IFeature, false, into);
        }
    }

    /// <summary>The components' names (last segment) in the order the feature tree holds them; empty when it cannot be read.</summary>
    static IReadOnlyList<string> TreeNames(Func<IFeature?> firstFeature)
    {
        var names = new List<string>();
        try
        {
            for (var f = firstFeature(); f != null; f = f.GetNextFeature() as IFeature)
            {
                if (f.GetTypeName2() == "Reference" && f.GetSpecificFeature2() is IComponent2 c) names.Add(Leaf(c.Name2));
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is InvalidCastException)
        {
            names.Clear();
        }
        return names;
    }

    static string Leaf(string name) => name.Substring(name.LastIndexOf('/') + 1);

    public object? ComponentByName(string name) =>
        (object?)_asm.GetComponentByName(name)
        ?? Components(false).Cast<IComponent2>().FirstOrDefault(c => string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase));

    public ComponentInfo Read(object component)
    {
        var c = (IComponent2)component;
        string path = c.GetPathName();
        bool subassembly = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase);
        var state = StateOf(c, path);
        bool fixedInPlace = c.IsFixed();
        return new ComponentInfo
        {
            Name = c.Name2,
            Path = path.Length == 0 ? null : path,
            Configuration = c.ReferencedConfiguration,
            State = state,
            Fixed = fixedInPlace,
            Hidden = c.IsHidden(false),
            Transform = TransformOf(c),
            Box = state == ComponentState.Resolved && c.GetBox(false, false) is double[] box && box.Length >= 6
                ? new Box3 { Min = new Point3(box[0] * Mm, box[1] * Mm, box[2] * Mm), Max = new Point3(box[3] * Mm, box[4] * Mm, box[5] * Mm) }
                : null,
            Subassembly = subassembly,
            SolveAs = subassembly ? (c.Solving == (int)swComponentSolvingOption_e.swComponentFlexibleSolving ? SolveAs.Flexible : SolveAs.Rigid) : (SolveAs?)null,
            Constraint = fixedInPlace ? Constraint.Fixed : MapConstraint(c.GetConstrainedStatus()),
            Parent = c.GetParent() is IComponent2 parent ? parent.Name2 : null,
            Mates = c.GetMates() is object[] mates ? mates.Length : 0,
        };
    }

    static ComponentState StateOf(IComponent2 c, string path) => c.GetSuppression2() switch
    {
        (int)swComponentSuppressionState_e.swComponentSuppressed => path.Length > 0 && !File.Exists(path) ? ComponentState.Missing : ComponentState.Suppressed,
        (int)swComponentSuppressionState_e.swComponentLightweight => ComponentState.Lightweight,
        (int)swComponentSuppressionState_e.swComponentFullyLightweight => ComponentState.Lightweight,
        _ => ComponentState.Resolved,
    };

    static Transform TransformOf(IComponent2 c) => c.Transform2 is MathTransform xf && xf.ArrayData is double[] a ? Placement.FromArray(a) : new Transform();

    public ComponentIdentity Identify(object component)
    {
        var c = (IComponent2)component;
        string path = c.GetPathName() ?? "";
        return new ComponentIdentity(c.Name2, path.Length == 0 ? null : path, StateOf(c, path));
    }

    public ISwDocument? ModelOf(object component)
    {
        var c = (IComponent2)component;
        if (StateOf(c, c.GetPathName() ?? "") != ComponentState.Resolved) return null;
        return c.GetModelDoc2() is IModelDoc2 model ? new SolidWorksDocument(_sw, model) : null;
    }

    public object? Corresponding(object component, object entity) => ((IComponent2)component).GetCorresponding(entity);

    /// <summary>
    /// A feature by its own document's description of the same-named feature; a face, edge or vertex as SOLIDWORKS gives
    /// it, which is in the component's own coordinates (plan 4.B: the assemblies live suite's turned post proves it).
    /// Either is then moved by the component's placement.
    /// </summary>
    public EntityInfo Describe(object component, object entity)
    {
        var c = (IComponent2)component;
        EntityInfo info;
        if (entity is IFeature feature && ModelOf(c) is SolidWorksDocument model && model.FeatureByName(feature.Name) is object own)
            info = model.Describe(own);
        else
            info = new SolidWorksDocument(_sw, _doc).Describe(entity);
        Placement.MoveToAssembly(TransformOf(c), info);
        info.Component = c.Name2;
        return info;
    }

    public IReadOnlyList<object> Topology(object component, TopologyKind kind)
    {
        var list = new List<object>();
        var c = (IComponent2)component;
        if (StateOf(c, c.GetPathName() ?? "") != ComponentState.Resolved) return list;
        object bodyInfo;
        if (!(c.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) is object[] bodies)) return list;
        foreach (var body in bodies.OfType<IBody2>())
        {
            object? items = kind switch
            {
                TopologyKind.Edge => body.GetEdges(),
                TopologyKind.Vertex => body.GetVertices(),
                _ => body.GetFaces(),
            };
            if (items is object[] array) list.AddRange(array);
        }
        return list;
    }

    public double DistanceMm(object component, object entity, Point3 point) =>
        SolidWorksDocument.Distance(entity, Placement.ToComponent(TransformOf((IComponent2)component), point));

    /// <summary>
    /// A component's share: the solid bodies IComponent2.GetBodies3 gives, which are the configuration the assembly
    /// references (plan 4.B review: the file's own mass is its active configuration, so after component.set
    /// {configuration} the shares no longer added up to the whole). Each body's IBody2.GetMassProperties at its part's
    /// density, its centre moved into the assembly by the part component's placement; a subassembly's share is its
    /// resolved part components', suppressed ones left out as the whole leaves them out. Not IMassProperty2.SelectedItems:
    /// on SOLIDWORKS 2026 SP4 setting it to a component crashed SOLIDWORKS (auCmdTarget_c::objectFromDispatch; plan 4.B,
    /// Task 14 round 1).
    /// </summary>
    public ComponentMass? Mass(object component, out string? refused)
    {
        refused = null;
        var top = (IComponent2)component;
        double volume = 0, mass = 0, mx = 0, my = 0, mz = 0;
        foreach (var c in PartsOf(top))
        {
            var state = StateOf(c, c.GetPathName() ?? "");
            if (state == ComponentState.Suppressed || state == ComponentState.Missing) continue;
            string who = c == top ? Leaf(top.Name2) : $"{Leaf(c.Name2)} in {Leaf(top.Name2)}";
            if (state == ComponentState.Lightweight)
            {
                refused = $"{who} is lightweight, so SOLIDWORKS has not loaded its bodies; component.set {{suppression: resolved}} loads it";
                return null;
            }
            if (!(c.GetModelDoc2() is IModelDoc2 model)) return null;
            string configuration = c.ReferencedConfiguration ?? "";
            if (!(Density(model, configuration, who, out refused) is double density)) return null;
            object bodyInfo;
            if (!(c.GetBodies3((int)swBodyType_e.swSolidBody, out bodyInfo) is object[] bodies)) continue;
            var placement = TransformOf(c);
            foreach (var body in bodies.OfType<IBody2>())
            {
                string db;
                string own = body.GetMaterialPropertyName(configuration, out db) ?? "";
                if (own.Length > 0 && !string.Equals(own, MaterialOf(model, configuration), StringComparison.OrdinalIgnoreCase))
                {
                    refused = $"{who} has a body of its own material ({own}), whose density SOLIDWORKS does not give through the API";
                    return null;
                }
                // Centre of mass x, y, z (m), volume (m³), area (m²), mass (kg), then the moments.
                if (!(body.GetMassProperties(density) is double[] p) || p.Length < 6) return null;
                double v = p[3] * 1e9, m = p[5] * 1000;
                var centre = Placement.ToAssembly(placement, new Point3(p[0] * Mm, p[1] * Mm, p[2] * Mm));
                volume += v;
                mass += m;
                mx += m * centre.X;
                my += m * centre.Y;
                mz += m * centre.Z;
            }
        }
        return new ComponentMass
        {
            Name = top.Name2,
            VolumeMm3 = volume,
            MassG = mass,
            CenterOfMass = mass > 0 ? new Point3(mx / mass, my / mass, mz / mass) : null,
        };
    }

    /// <summary>The component itself when it is a part; a subassembly's part components at every level below it.</summary>
    static IEnumerable<IComponent2> PartsOf(IComponent2 c)
    {
        var children = (c.GetChildren() as object[])?.OfType<IComponent2>().ToList();
        if (children == null || children.Count == 0)
        {
            yield return c;
            yield break;
        }
        foreach (var child in children)
        {
            // A suppressed subassembly's parts have no mass in the whole either.
            if (child.GetChildren() is object[] grand && grand.Length > 0 && child.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
            foreach (var part in PartsOf(child)) yield return part;
        }
    }

    static string MaterialOf(IModelDoc2 model, string configuration)
    {
        if (!(model is IPartDoc part)) return "";
        string db;
        return part.GetMaterialPropertyName2(configuration, out db) ?? "";
    }

    /// <summary>
    /// The part's density in kg/m³ for the configuration the assembly references. SOLIDWORKS gives it only for the file's
    /// active configuration, so a referenced configuration with another material is refused, naming both.
    /// </summary>
    double? Density(IModelDoc2 model, string configuration, string who, out string? refused)
    {
        refused = null;
        string active = model.ConfigurationManager?.ActiveConfiguration?.Name ?? "";
        string referenced = MaterialOf(model, configuration), current = MaterialOf(model, active);
        if (!string.Equals(referenced, current, StringComparison.OrdinalIgnoreCase))
        {
            refused = $"{who} uses its configuration {configuration}, whose material ({Named(referenced)}) is not that of the file's active configuration {active} ({Named(current)}); " +
                      "SOLIDWORKS gives the density of the active one only";
            return null;
        }
        var own = new SolidWorksDocument(_sw, model).Mass();
        if (own == null || !(own.DensityKgM3 > 0))
        {
            refused = $"SOLIDWORKS gave no density for {who}'s file in its active configuration {active}";
            return null;
        }
        return own.DensityKgM3;

        static string Named(string material) => material.Length == 0 ? "none set" : material;
    }

    internal static Constraint MapConstraint(int status) => status switch
    {
        (int)swConstrainedStatus_e.swFullyConstrained => Constraint.FullyDefined,
        (int)swConstrainedStatus_e.swUnderConstrained => Constraint.UnderDefined,
        (int)swConstrainedStatus_e.swOverConstrained => Constraint.OverDefined,
        _ => Constraint.Unknown,
    };

    public string? EditingComponent() =>
        _asm.GetEditTarget() is IModelDoc2 target && !string.Equals(target.GetTitle(), _doc.GetTitle(), StringComparison.OrdinalIgnoreCase)
            ? target.GetTitle()
            : null;

    public int MateCount()
    {
        var group = MateGroup();
        int count = 0;
        for (var f = group?.GetFirstSubFeature() as IFeature; f != null; f = f.GetNextSubFeature() as IFeature) count++;
        return count;
    }

    public IReadOnlyList<object> Mates()
    {
        var list = new List<object>();
        for (var f = MateGroup()?.GetFirstSubFeature() as IFeature; f != null; f = f.GetNextSubFeature() as IFeature)
        {
            if (f.GetSpecificFeature2() is IMate2) list.Add(f);
        }
        return list;
    }

    public object? MateByName(string name) =>
        Mates().Cast<IFeature>().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public MateFacts ReadMate(object mate)
    {
        var f = (IFeature)mate;
        var m = (IMate2)f.GetSpecificFeature2();
        int type = m.Type;
        int error = f.GetErrorCode2(out bool _);
        var facts = new MateFacts
        {
            Name = f.Name,
            Type = SolidWorksCodes.MateTypeOf(type),
            SolidWorksType = SolidWorksCodes.MateTypeName(type),
            Alignment = type == (int)swMateType_e.swMateLOCK || type == (int)swMateType_e.swMatePERPENDICULAR ? (MateAlignment?)null : SolidWorksCodes.AlignmentOf(m.Alignment),
            Flipped = m.Flipped,
            Suppressed = f.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) is bool[] s && s.Length > 0 && s[0],
            Error = error == 0 ? null : SolidWorksCodes.FeatureError(error),
        };
        switch (f.GetDefinition())
        {
            case IDistanceMateFeatureData d:
                facts.DistanceMm = d.Distance * Mm;
                break;
            case IAngleMateFeatureData a:
                facts.AngleDeg = a.Angle * 180 / Math.PI;
                break;
        }
        string own = _doc.GetPathName() ?? "";
        for (int i = 0; i < m.GetMateEntityCount(); i++)
        {
            var side = m.MateEntity(i);
            // The assembly's own planes come back with the root component, which is no component of ours.
            var component = side.ReferenceComponent is IComponent2 c && !string.IsNullOrEmpty(c.Name2) && !string.Equals(c.GetPathName(), own, StringComparison.OrdinalIgnoreCase) ? c : null;
            object? entity = type == (int)swMateType_e.swMateLOCK ? component : side.Reference;
            if (entity != null) facts.Entities.Add((component, entity));
        }
        return facts;
    }

    /// <summary>
    /// Selects the two sides (IEntity.Select4, IFeature.Select2, IComponent2.Select4), then IAssemblyDoc.AddMate5 on the
    /// selection. Not CreateMate: its EntitiesToMate is a SAFEARRAY of dispatch, the kind of setter that crashed
    /// SOLIDWORKS 2026 SP4 in plan 4.B (IMassProperty2.SelectedItems); selecting is how SetFixed and Delete work, proven live.
    /// </summary>
    public object? AddMate(MateSpec spec, out string? error)
    {
        error = null;
        int before = Mates().Count;
        _doc.ClearSelection2(true);
        try
        {
            foreach (var entity in spec.Entities)
            {
                bool selected = entity switch
                {
                    IComponent2 c => c.Select4(true, null, false),
                    IFeature f => f.Select2(true, 0),
                    IEntity e => e.Select4(true, null),
                    _ => false,
                };
                if (!selected) throw new SwAgentException(ErrorCode.ApiError, $"SOLIDWORKS would not select a {entity.GetType().Name} to mate it");
            }
            double distance = (spec.DistanceMm ?? 0) / Mm, angle = (spec.AngleDeg ?? 0) * Math.PI / 180;
            _asm.AddMate5(SolidWorksCodes.MateType(spec.Type), SolidWorksCodes.Align(spec.Alignment ?? MateAlignment.Closest), spec.Flip,
                distance, distance, distance, 1, 1, angle, angle, angle, false, false, 0, out int status);
            if (status != (int)swAddMateError_e.swAddMateError_NoError) error = SolidWorksCodes.AddMateError(status);
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
        var all = Mates();
        if (all.Count <= before) return null;
        // SOLIDWORKS appends a new mate to the end of the MateGroup.
        var made = (IFeature)all[all.Count - 1];
        if (spec.Name != null) made.Name = spec.Name;
        return made;
    }

    public bool EditMate(object mate, MateSpec spec)
    {
        var f = (IFeature)mate;
        object data = f.GetDefinition();
        int? align = spec.Alignment is MateAlignment a ? SolidWorksCodes.Align(a) : (int?)null;
        switch (data)
        {
            case IDistanceMateFeatureData d:
                if (spec.DistanceMm is double mm) d.Distance = mm / Mm;
                if (align != null) d.MateAlignment = align.Value;
                d.FlipDimension = spec.Flip;
                break;
            case IAngleMateFeatureData g:
                if (spec.AngleDeg is double deg) g.Angle = deg * Math.PI / 180;
                if (align != null) g.MateAlignment = align.Value;
                g.FlipDimension = spec.Flip;
                break;
            case ICoincidentMateFeatureData c when align != null:
                c.MateAlignment = align.Value;
                break;
            case IConcentricMateFeatureData c when align != null:
                c.MateAlignment = align.Value;
                break;
            case IParallelMateFeatureData c when align != null:
                c.MateAlignment = align.Value;
                break;
            case ITangentMateFeatureData c when align != null:
                c.MateAlignment = align.Value;
                break;
        }
        bool ok = f.ModifyDefinition(data, _doc, null);
        _doc.EditRebuild3();
        return ok;
    }

    public bool SetMateSuppressed(object mate, bool suppressed) =>
        ((IFeature)mate).SetSuppression2(
            suppressed ? (int)swFeatureSuppressionAction_e.swSuppressFeature : (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
            (int)swInConfigurationOpts_e.swThisConfiguration, null);

    public void DeleteMates(IReadOnlyList<object> mates)
    {
        _doc.ClearSelection2(true);
        try
        {
            foreach (IFeature f in mates)
            {
                if (!f.Select2(true, 0)) throw new SwAgentException(ErrorCode.ApiError, $"the mate {f.Name} could not be selected to delete it");
            }
            if (!_doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", mates.Cast<IFeature>().Select(f => f.Name))}");
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    /// <summary>IComponent2.GetRemainingDOFs: two rotations (point and direction each), then two translations; points in metres.</summary>
    public RemainingDofs? RemainingDofs(object component)
    {
        var dofs = AskRemainingDofs((IComponent2)component);
        if (dofs?.Result != Freedom.Unavailable) return dofs;
        // Live 4.C run: unselected, every mated component answered swRemainingDofs_Unavailable. Ask once more with it
        // selected, as Move Component has it, and leave nothing selected.
        _doc.ClearSelection2(true);
        try
        {
            return ((IComponent2)component).Select4(false, null, false) ? AskRemainingDofs((IComponent2)component) ?? dofs : dofs;
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    static RemainingDofs? AskRemainingDofs(IComponent2 component)
    {
        try
        {
            int result = component.GetRemainingDOFs(
                out int r1PointStatus, out MathPoint r1Point, out int r1Status, out MathVector r1Dir,
                out int r2PointStatus, out MathPoint r2Point, out int r2Status, out MathVector r2Dir,
                out int t1Status, out MathVector t1Dir, out int t2Status, out MathVector t2Dir);
            var dofs = new RemainingDofs { Result = result };
            dofs.Rotations.Add(new DofSlot(r1Status, Direction(r1Dir), Point(r1Point)));
            dofs.Rotations.Add(new DofSlot(r2Status, Direction(r2Dir), Point(r2Point)));
            dofs.Translations.Add(new DofSlot(t1Status, Direction(t1Dir)));
            dofs.Translations.Add(new DofSlot(t2Status, Direction(t2Dir)));
            return dofs;
        }
        catch (COMException)
        {
            return null;
        }

        static Vector3? Direction(MathVector? v) => v?.ArrayData is double[] a && a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : null;

        static Point3? Point(MathPoint? p) => p?.ArrayData is double[] a && a.Length >= 3 ? new Point3(a[0] * Mm, a[1] * Mm, a[2] * Mm) : null;
    }

    IFeature? MateGroup()
    {
        for (var f = _doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
        {
            if (f.GetTypeName2() == "MateGroup") return f;
        }
        return null;
    }

    public IReadOnlyList<string> MatesOf(object component)
    {
        var names = new List<string>();
        if (!(((IComponent2)component).GetMates() is object[] mates)) return names;
        foreach (var m in mates) names.Add((m as IFeature)?.Name ?? "a mate");
        return names;
    }

    public object? Insert(string path, string? configuration, Transform at)
    {
        int type = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
        IModelDoc2? opened = null;
        if (!(_sw.GetOpenDocumentByName(path) is IModelDoc2))
        {
            // AddComponent5 needs the file in memory: load it without a window, resolved, as SOLIDWORKS does when inserting by hand.
            int errors = 0, warnings = 0;
            int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent
                | (type == (int)swDocumentTypes_e.swDocASSEMBLY ? (int)swOpenDocOptions_e.swOpenDocOptions_OverrideDefaultLoadLightweight : 0);
            _sw.DocumentVisible(false, type);
            try
            {
                opened = _sw.OpenDoc6(path, type, options, configuration ?? "", ref errors, ref warnings);
            }
            finally
            {
                _sw.DocumentVisible(true, type);
            }
            if (opened == null)
            {
                var names = SolidWorksCodes.LoadErrors(errors);
                throw new SwAgentException(ErrorCode.OperationFailed,
                    $"SOLIDWORKS could not load {path} to insert it: {string.Join(", ", names.DefaultIfEmpty("no error reported"))}",
                    new JsonObject { ["path"] = path, ["errors"] = names.Select(n => (object?)n).ToList() });
            }
        }
        int activateErrors = 0;
        _sw.ActivateDoc3(_doc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
        var component = _asm.AddComponent5(path, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig, "",
            configuration != null, configuration ?? "", at.Origin.X / Mm, at.Origin.Y / Mm, at.Origin.Z / Mm);
        if (component == null)
        {
            if (opened != null) _sw.CloseDoc(opened.GetTitle());
            return null;
        }
        SetTransform(component, at);
        return component;
    }

    public void SetTransform(object component, Transform transform)
    {
        var math = (IMathUtility)_sw.GetMathUtility();
        ((IComponent2)component).Transform2 = (MathTransform)math.CreateTransform(Placement.ToArray(transform));
        _doc.GraphicsRedraw2();
    }

    public void SetFixed(object component, bool fix)
    {
        _doc.ClearSelection2(true);
        try
        {
            if (!((IComponent2)component).Select4(false, null, false))
                throw new SwAgentException(ErrorCode.ApiError, $"the component {((IComponent2)component).Name2} could not be selected to {(fix ? "fix" : "float")} it");
            if (fix) _asm.FixComponent();
            else _asm.UnfixComponent();
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public bool SetSuppression(object component, Suppression suppression) =>
        ((IComponent2)component).SetSuppression2(suppression == Suppression.Suppressed
            ? (int)swComponentSuppressionState_e.swComponentSuppressed
            : (int)swComponentSuppressionState_e.swComponentFullyResolved) == (int)swSuppressionError_e.swSuppressionChangeOk;

    public void SetConfiguration(object component, string configuration)
    {
        ((IComponent2)component).ReferencedConfiguration = configuration;
        _doc.EditRebuild3();
    }

    public void SetSolveAs(object component, SolveAs solveAs)
    {
        var c = (IComponent2)component;
        _doc.ClearSelection2(true);
        try
        {
            if (!c.Select4(false, null, false))
                throw new SwAgentException(ErrorCode.ApiError, $"the component {c.Name2} could not be selected to make it {WireNames.ForEnum(solveAs)}");
            // CompConfigProperties5 sets every property of the selected component at once: everything but solving is passed back as it is.
            bool ok = _asm.CompConfigProperties5(c.GetSuppression2(),
                solveAs == SolveAs.Flexible ? (int)swComponentSolvingOption_e.swComponentFlexibleSolving : (int)swComponentSolvingOption_e.swComponentRigidSolving,
                !c.IsHidden(false), true, c.ReferencedConfiguration, c.ExcludeFromBOM, c.IsEnvelope());
            if (!ok) throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not make {c.Name2} {WireNames.ForEnum(solveAs)}");
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public void SetHidden(object component, bool hidden) =>
        ((IComponent2)component).Visible = hidden ? (int)swComponentVisibilityState_e.swComponentHidden : (int)swComponentVisibilityState_e.swComponentVisible;

    public void Delete(IReadOnlyList<object> components)
    {
        _doc.ClearSelection2(true);
        try
        {
            foreach (IComponent2 c in components)
            {
                if (!c.Select4(true, null, false)) throw new SwAgentException(ErrorCode.ApiError, $"the component {c.Name2} could not be selected to delete it");
            }
            if (!_doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS did not delete {string.Join(", ", components.Cast<IComponent2>().Select(c => c.Name2))}");
        }
        finally
        {
            _doc.ClearSelection2(true);
        }
    }

    public void Rebuild() => _doc.EditRebuild3();

    public bool EditInContext(object component)
    {
        _doc.ClearSelection2(true);
        if (!((IComponent2)component).Select4(false, null, false)) return false;
        int info = 0;
        int status = _asm.EditPart2(true, false, ref info);
        _doc.ClearSelection2(true);
        return status == (int)swEditPartCommandStatus_e.swEditPartSuccessful;
    }

    public void EditAssembly() => _asm.EditAssembly();
}
