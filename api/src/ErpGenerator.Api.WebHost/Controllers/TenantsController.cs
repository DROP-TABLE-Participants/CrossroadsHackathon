using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ErpGenerator.Api.Shared.Tenants;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.CustomerPortalUser)]
[Route("api/customers/tenants")]
public sealed class TenantsController(ITenantService tenants) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TenantListViewModel>> List(CancellationToken cancellationToken) =>
        Ok(await tenants.ListAsync(User.RequireCustomerId(), cancellationToken));

    [HttpPost]
    public async Task<ActionResult<CreateTenantViewModel>> Create(
        CreateTenantInputModel input,
        CancellationToken cancellationToken)
    {
        var tenant = await tenants.CreateAsync(User.RequireCustomerId(), input, cancellationToken);
        return Created($"/api/customers/tenants/{tenant.Tenant.Id}", tenant);
    }
}
