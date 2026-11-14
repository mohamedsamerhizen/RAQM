using RAQM.Api.Helpers;

namespace RAQM.Api.Services.SaaS;

public sealed class PlatformAuthority(IConfiguration configuration)
{
    public bool Allows(Guid actor, string permission) => actor != Guid.Empty &&
        configuration.GetSection("SaaS:Operators:" + permission).GetChildren().Any(x => Guid.TryParse(x.Value, out var id) && id == actor);

    public void Require(Guid actor, string permission)
    {
        if (!Allows(actor, permission)) throw new ApiException(403, "Platform operator permission required.", "saas_operator");
    }
}
