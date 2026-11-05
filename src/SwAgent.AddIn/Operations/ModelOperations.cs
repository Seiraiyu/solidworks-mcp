using System;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;

namespace SwAgent.AddIn.Operations;

public sealed class ModelTreeOperation : Operation<ModelTreeRequest, ModelTreeResult>
{
    readonly AddInServices _services;

    public ModelTreeOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelTreeResult Run(ModelTreeRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.tree");
        var nodes = doc.Features().Select(f =>
        {
            var info = _services.References.Describe(doc, f.Feature);
            return new FeatureNode
            {
                Name = f.Name,
                Type = f.Type,
                Kind = info.Kind,
                Reference = info.Reference,
                Parent = f.Parent,
                Suppressed = f.Suppressed,
                Error = f.ErrorCode == 0 ? null : SolidWorksCodes.FeatureError(f.ErrorCode),
                Warning = f.ErrorCode != 0 && f.Warning,
            };
        }).ToList();
        return new ModelTreeResult { Document = doc.Info(), Features = nodes };
    }

    /// <summary>The tree read again has the same features in the same order.</summary>
    protected override void Verify(ModelTreeRequest request, ModelTreeResult result, IOperationContext context)
    {
        var doc = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document));
        var names = doc.Features().Select(f => f.Name).ToList();
        if (!names.SequenceEqual(result.Features.Select(f => f.Name)))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the feature tree changed while it was read; ask again");
    }
}

public sealed class ModelMassOperation : Operation<ModelMassRequest, ModelMassResult>
{
    readonly AddInServices _services;

    public ModelMassOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelMassResult Run(ModelMassRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.mass");
        int bodies = doc.SolidBodyCount();
        var mass = bodies == 0 ? null : doc.Mass();
        var result = mass ?? new ModelMassResult();
        result.Document = doc.Info();
        result.Bodies = bodies;
        result.Material = doc.Material();
        result.PrincipalMomentsGMm2 = result.PrincipalMomentsGMm2.OrderBy(m => m).ToList();
        if (bodies > 0 && mass == null)
            throw new SwAgentException(ErrorCode.OperationFailed, $"SOLIDWORKS gave no mass properties for {result.Document.Title}, which has {bodies} solid bodies");
        return result;
    }

    /// <summary>Mass is density times volume, and the volume read again is the same.</summary>
    protected override void Verify(ModelMassRequest request, ModelMassResult result, IOperationContext context)
    {
        if (result.Bodies == 0) return;
        double expected = result.DensityKgM3 * result.VolumeMm3 * 1e-6;
        if (Math.Abs(result.MassG - expected) > 1e-6 * Math.Max(1, expected))
        {
            throw new SwAgentException(ErrorCode.VerificationFailed,
                $"SOLIDWORKS reported a mass of {result.MassG} g, but density × volume is {expected} g");
        }
        var again = DocumentLocator.Find(_services.SolidWorks, DocumentKeys.Of(result.Document)).Mass();
        if (again == null || Math.Abs(again.VolumeMm3 - result.VolumeMm3) > 1e-9 * Math.Max(1, result.VolumeMm3))
            throw new SwAgentException(ErrorCode.VerificationFailed, "the volume changed while it was read; ask again");
    }
}

public sealed class ModelBoundingBoxOperation : Operation<ModelBoundingBoxRequest, ModelBoundingBoxResult>
{
    readonly AddInServices _services;

    public ModelBoundingBoxOperation(AddInServices services)
    {
        _services = services;
    }

    protected override ModelBoundingBoxResult Run(ModelBoundingBoxRequest request, IOperationContext context)
    {
        var doc = DocumentLocator.FindPart(_services.SolidWorks, request.Document, "model.bounding_box");
        bool precise = request.Precise == true;
        int bodies = doc.SolidBodyCount();
        var box = bodies == 0 ? null : doc.BoundingBox(precise);
        return new ModelBoundingBoxResult
        {
            Document = doc.Info(),
            Bodies = bodies,
            Box = box,
            SizeX = box == null ? 0 : box.Max.X - box.Min.X,
            SizeY = box == null ? 0 : box.Max.Y - box.Min.Y,
            SizeZ = box == null ? 0 : box.Max.Z - box.Min.Z,
            Precise = precise,
        };
    }

    /// <summary>A part with bodies has a box, and its corners are in order.</summary>
    protected override void Verify(ModelBoundingBoxRequest request, ModelBoundingBoxResult result, IOperationContext context)
    {
        if (result.Bodies > 0 && result.Box == null)
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS gave no bounding box for a part with {result.Bodies} solid bodies");
        if (result.Box != null && (result.SizeX < 0 || result.SizeY < 0 || result.SizeZ < 0))
            throw new SwAgentException(ErrorCode.VerificationFailed, $"SOLIDWORKS gave a box whose corners are out of order: {result.Box.Min} to {result.Box.Max}");
    }
}
