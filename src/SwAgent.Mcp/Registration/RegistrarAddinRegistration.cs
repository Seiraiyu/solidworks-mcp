using SwAgent.Client;

namespace SwAgent.Mcp.Registration;

/// <summary>The supervisor's view of the add-in's registration, read from HKCU by the registrar.</summary>
public sealed class RegistrarAddinRegistration : IAddinRegistration
{
    readonly AddinRegistrar _registrar;

    public RegistrarAddinRegistration(AddinRegistrar registrar)
    {
        _registrar = registrar;
    }

    public AddinRegistrationInfo Read()
    {
        var status = _registrar.Status();
        return new AddinRegistrationInfo(status.Registered, status.Path, status.FileExists);
    }
}
