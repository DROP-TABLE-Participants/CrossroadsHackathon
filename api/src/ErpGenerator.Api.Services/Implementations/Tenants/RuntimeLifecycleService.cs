using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ErpGenerator.Api.Shared.Generation;
using ErpGenerator.Api.Shared.Tenants;

namespace ErpGenerator.Api.Services.Implementations.Tenants;

public sealed class RuntimeLifecycleService(
    ErpGeneratorDbContext dbContext,
    IRuntimeDeploymentClient runtime) : IRuntimeLifecycleService
{
    public async Task<OneTimeActivationLinkViewModel> CreateActivationLinkAsync(
        Guid customerId,
        Guid runId,
        CancellationToken cancellationToken)
    {
        const string normalizedEnvironment = "erp";
        var deployment = await dbContext.Deployments
            .Include(value => value.Tenant).ThenInclude(value => value.Environments)
            .Include(value => value.ErpBundleVersion).ThenInclude(value => value.GenerationRun)
                .ThenInclude(value => value.GenerationProject).ThenInclude(value => value.Customer)
            .Where(value => value.ErpBundleVersion.GenerationRunId == runId &&
                            value.ErpBundleVersion.GenerationRun.GenerationProject.CustomerId == customerId &&
                            value.Environment == normalizedEnvironment && value.Status == "ready")
            .OrderByDescending(value => value.CreatedAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The requested ERP environment is not ready.");
        var target = deployment.Tenant.Environments.Single(value => value.Environment == normalizedEnvironment);
        var project = deployment.ErpBundleVersion.GenerationRun.GenerationProject;
        var email = project.OwnerEmail ?? project.Customer.Email
            ?? throw new InvalidOperationException("ERP owner email is missing.");
        var operationId = OperationId("activation", runId.ToString(), normalizedEnvironment, Guid.NewGuid().ToString("N"));
        var result = await runtime.CreateActivationAsync(new RuntimeOwnerActivationInputModel(
            operationId,
            runId.ToString(),
            project.Id.ToString(),
            new RuntimeDeploymentTargetViewModel(
                deployment.TenantId.ToString(), deployment.Tenant.StableId, target.Host,
                target.Environment, target.DatabaseName),
            email), cancellationToken);
        if (!result.Success || result.Activation is null)
            throw new InvalidOperationException(result.SafeError ?? "Runtime activation link could not be created.");

        target.OwnerActivationUrl = result.Activation.Url;
        target.OwnerActivationExpiresAt = result.Activation.ExpiresAt;
        var normalizedEmail = result.Activation.Email.Trim().ToLowerInvariant();
        var activation = await dbContext.OwnerActivations.SingleOrDefaultAsync(
            value => value.TenantEnvironmentId == target.Id && value.Email == normalizedEmail, cancellationToken);
        if (activation is null)
        {
            activation = new OwnerActivation { TenantEnvironmentId = target.Id, Email = normalizedEmail };
            dbContext.OwnerActivations.Add(activation);
        }
        activation.Status = "pending";
        await dbContext.SaveChangesAsync(cancellationToken);
        return new OneTimeActivationLinkViewModel(
            normalizedEnvironment, "pending", result.Activation.Url,
            result.Activation.Email, result.Activation.ExpiresAt);
    }

    private static string OperationId(params string[] values)
    {
        var canonical = string.Join('\0', values.Select(value => value.Trim()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
