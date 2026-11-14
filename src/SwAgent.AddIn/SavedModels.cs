using System.IO;
using System.Linq;
using SwAgent.AddIn.Core;
using SwAgent.Contracts;
using SwAgent.Contracts.Serialization;

namespace SwAgent.AddIn;

/// <summary>
/// The saved-model rule (design 611, Drawing documents): a drawing view references a model file, and SOLIDWORKS asks to
/// save a never-saved model in a Save As box that waits for the user, so every operation that puts a model on a drawing
/// names a saved part or assembly, checked here before any SOLIDWORKS call.
/// </summary>
public static class SavedModels
{
    /// <summary>
    /// The full path of the model <paramref name="model"/> names: an open part or assembly by title or path, which must
    /// have been saved, or the path of an existing .SLDPRT or .SLDASM. invalid_argument naming <paramref name="field"/>
    /// otherwise, telling to save a never-saved model with sw_document_save_as first. The open document, when it is open.
    /// </summary>
    public static string Require(ISolidWorksApplication sw, string model, string field, out ISwDocument? open)
    {
        open = null;
        if (string.IsNullOrWhiteSpace(model))
            throw Invalid(field, $"{field}: name the model, by the full path of its .SLDPRT or .SLDASM or the title of an open part or assembly");
        if (Path.IsPathRooted(model))
        {
            string full = Path.GetFullPath(model);
            open = sw.Documents().FirstOrDefault(d => d.Info().Path is string p && DocumentKeys.Same(Path.GetFullPath(p), full));
            if (open == null) return PathRules.Existing(model, field, ".SLDPRT", ".SLDASM");
        }
        else
        {
            open = DocumentLocator.Find(sw, model, field);
        }
        var info = open.Info();
        if (info.Type == DocumentType.Drawing)
            throw Invalid(field, $"{field}: {info.Title} is a drawing; a view shows a part or an assembly");
        if (info.Path == null)
        {
            throw new SwAgentException(ErrorCode.InvalidArgument,
                $"{field}: {info.Title} has never been saved, and a drawing view needs a model file (SOLIDWORKS would ask to save it in a box that waits for the user). Save it with sw_document_save_as first, then name it here",
                new JsonObject { ["field"] = field, ["document"] = info.Title, ["saved"] = false });
        }
        return Path.GetFullPath(info.Path);
    }

    static SwAgentException Invalid(string field, string message) =>
        new SwAgentException(ErrorCode.InvalidArgument, message, new JsonObject { ["field"] = field });
}
