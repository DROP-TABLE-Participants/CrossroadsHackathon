using System.Net;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Bundles;
using ErpGenerator.Runtime.Shared.Compilation;
using ErpGenerator.Runtime.Shared.Deployment;
using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Services.ControlPlane;
using ErpGenerator.Runtime.Data.Databases;
using ErpGenerator.Runtime.Data.Databases.Preparation;

namespace ErpGenerator.Runtime.Services.DeploymentBroker;

public sealed class CanonicalBundleDeploymentService(
    IControlPlaneClient controlPlane,
    ITenantDatabaseProvisioner provisioner,
    ITenantPreparationOperationFence preparationFence,
    RuntimeDatabase runtimeDatabase,
    RuntimeBundleTestRunner bundleTests,
    ErpBundleCompiler compiler,
    ErpBundleMigrationPlanner migrationPlanner,
    ILogger<CanonicalBundleDeploymentService> logger) : ICanonicalBundleDeploymentService
{
    private static readonly TimeSpan ActivationLifetime = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions ArtifactJsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedGrantActions = new(StringComparer.Ordinal)
    {
        "read", "create", "update", "archive", "restore", "start_workflow",
        "approve_task", "reject_task", "read_task", "admin"
    };

    public async Task<CanonicalBundleDeploymentResponse> DeployAsync(
        CanonicalBundleDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateRequest(request);
            var tenant = await controlPlane.ResolveTenantEnvironmentAsync(
                request.Target.Host,
                TenantEnvironmentKind.Erp,
                cancellationToken) ?? throw new InvalidOperationException("The ERP tenant could not be resolved.");
            ValidateTarget(request.Target, tenant);

            var compiled = compiler.Compile(Map(request));
            await using var operationFence = await preparationFence.AcquireAsync(tenant, cancellationToken);
            await provisioner.ProvisionAsync(tenant, cancellationToken);
            var active = await runtimeDatabase.ReadActiveCompiledBundleAsync(tenant, cancellationToken);
            var plan = migrationPlanner.PlanMigration(active, compiled);
            if (plan.IsBlocked)
                return Failure(request, "migration_blocked", "The generated ERP requires a blocked schema change.");

            var deployment = await runtimeDatabase.ApplyBundleDeploymentAsync(
                tenant,
                compiled,
                plan,
                "erp",
                "staged",
                activate: true,
                message: "Canonical generated ERP deployed by ErpGenerator.Api.",
                cancellationToken);
            if (deployment.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                return Failure(request, deployment.FailureCode ?? "deployment_failed", "The generated ERP could not be deployed to ERP.");

            var acceptanceTests = BuildAcceptanceTests(request.Bundle);
            var testRun = await bundleTests.RunAsync(
                tenant,
                compiled.Version,
                compiled.BundleHash,
                seed: null,
                acceptanceTests,
                cancellationToken);
            if (!testRun.Status.Equals("passed", StringComparison.OrdinalIgnoreCase))
                return Failure(request, "acceptance_failed", "The generated ERP did not pass its executable acceptance tests.");

            var activation = await CreateActivationAsync(tenant, request.OwnerEmail, cancellationToken);
            return new CanonicalBundleDeploymentResponse(
                true,
                "ready",
                request.OperationId,
                request.Target,
                tenant.RuntimeBaseUrl,
                compiled.BundleId,
                compiled.Version,
                request.ArtifactHash.ToLowerInvariant(),
                compiled.BundleHash,
                deployment.DeploymentId.ToString(),
                testRun.Status,
                testRun.TestSuiteHash,
                activation);
        }
        catch (BundleCompilationException exception)
        {
            logger.LogWarning(
                exception,
                "Canonical ERP deployment {OperationId} failed bundle validation: {ValidationErrors}",
                request.OperationId,
                string.Join(" | ", exception.Errors));
            return Failure(request, "bundle_validation_failed", "The generated ERP bundle did not pass runtime validation.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Canonical ERP deployment {OperationId} failed.", request.OperationId);
            return Failure(request, "deployment_failed", "The generated ERP could not be deployed to ERP.");
        }
    }

    private async Task<RuntimeDeploymentActivation?> CreateActivationAsync(
        ResolvedTenantEnvironment tenant,
        string? ownerEmail,
        CancellationToken cancellationToken)
    {
        var email = ownerEmail?.Trim();
        if (string.IsNullOrWhiteSpace(email)) return null;
        await runtimeDatabase.ExpirePendingOwnerActivationTokensAsync(tenant, email, cancellationToken);
        var token = await runtimeDatabase.CreateActivationTokenAsync(tenant, email, ActivationLifetime, cancellationToken);
        return new RuntimeDeploymentActivation(
            $"{tenant.RuntimeBaseUrl.TrimEnd('/')}/activate?token={WebUtility.UrlEncode(token)}",
            email,
            DateTimeOffset.UtcNow.Add(ActivationLifetime));
    }

    internal static ErpBundle Map(CanonicalBundleDeploymentRequest request)
    {
        var generated = request.Bundle;
        var generatedEntityKeys = generated.Entities
            .Select(value => value.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedReports = generated.Reports
            .ToDictionary(value => value.Key, StringComparer.OrdinalIgnoreCase);
        var generatedDashboards = generated.Dashboards
            .ToDictionary(value => value.Key, StringComparer.OrdinalIgnoreCase);
        var roles = new Dictionary<string, ErpRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = new ErpRole
            {
                DisplayName = "Owner",
                Grants = [new ErpGrant { Action = "*", Entity = "*" }]
            }
        };
        foreach (var permission in generated.Permissions)
        {
            var roleKey = NormalizeKey(permission.Role);
            if (string.IsNullOrWhiteSpace(roleKey) || roleKey == "owner") continue;
            var role = new ErpRole { DisplayName = permission.Role };
            foreach (var grant in permission.Grants)
            {
                AddGeneratedGrant(
                    role,
                    grant,
                    generatedEntityKeys,
                    generatedReports,
                    generatedDashboards);
            }
            roles[roleKey] = role;
        }

        var entities = generated.Entities.Select(entity =>
        {
            var fields = entity.Fields.Select(field => new ErpField
            {
                Id = field.Key,
                Name = RuntimeFieldName(field),
                DisplayName = field.Label,
                Type = MapFieldType(field.Type),
                Required = field.Required,
                References = field.References
            }).ToList();
            return new ErpEntity
            {
                Id = entity.Key,
                Name = entity.Key,
                DisplayName = entity.Label,
                Display = new ErpEntityDisplay
                {
                    Primary = fields[0].Name,
                    Secondary = fields.Count > 1 ? fields[1].Name : fields[0].Name
                },
                Fields = fields
            };
        }).ToList();

        var workflows = new List<ErpWorkflow>();
        foreach (var workflow in generated.Workflows)
        {
            var steps = new List<ErpWorkflowStep>();
            var approvalRole = NormalizeKey(workflow.ApprovalRole);
            if (!string.IsNullOrWhiteSpace(approvalRole))
            {
                if (!roles.TryGetValue(approvalRole, out var role))
                {
                    role = new ErpRole { DisplayName = workflow.ApprovalRole! };
                    roles[approvalRole] = role;
                }
                AddGrant(role, "read", workflow.EntityKey);
                AddGrant(role, "read_task", workflow.EntityKey);
                AddGrant(role, "approve_task", workflow.EntityKey);
                AddGrant(role, "reject_task", workflow.EntityKey);
                steps.Add(new ErpWorkflowStep
                {
                    Id = "approval",
                    Type = "approval",
                    Label = workflow.Label,
                    AssignedTo = new ErpWorkflowAssignment { Type = "role", Role = approvalRole },
                    ApproveLabel = "Approve",
                    RejectLabel = "Reject"
                });
            }
            if (!string.IsNullOrWhiteSpace(workflow.StatusField) && !string.IsNullOrWhiteSpace(workflow.ApprovedValue))
            {
                steps.Add(new ErpWorkflowStep
                {
                    Id = "apply_status",
                    Type = "update_record",
                    Set = new Dictionary<string, string> { [workflow.StatusField] = workflow.ApprovedValue }
                });
            }
            steps.Add(new ErpWorkflowStep
            {
                Id = "audit_completed",
                Type = "audit_event",
                Event = $"{workflow.EntityKey}.{workflow.Key}.completed",
                Message = $"{workflow.Label} completed."
            });
            workflows.Add(new ErpWorkflow
            {
                Id = workflow.Key,
                Name = workflow.Label,
                Entity = workflow.EntityKey,
                Trigger = new ErpWorkflowTrigger { Action = workflow.TriggerAction },
                Steps = steps
            });
        }

        var reports = generated.Reports.Select(report => new ErpReport
        {
            Key = report.Key,
            Label = report.Label,
            Description = report.Label,
            Source = report.EntityKey,
            Metrics = [new ErpReportMetric { Key = "record_count", Label = "Records", Op = "count" }]
        }).ToList();
        var reportKeys = reports.Select(value => value.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dashboardReports = generated.Dashboards.SelectMany(value => value.Reports)
            .Where(reportKeys.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ErpBundle
        {
            Id = $"erp_{request.GenerationProjectId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}",
            Name = generated.Name,
            Version = "0.1.0",
            Description = generated.Summary,
            Modules =
            [
                new ErpModule
                {
                    Id = "core",
                    Name = generated.Modules.FirstOrDefault() ?? "Core",
                    Entities = entities,
                    Workflows = workflows
                }
            ],
            Reports = reports,
            Dashboard = new ErpDashboard
            {
                Widgets = dashboardReports.Select(report => new ErpDashboardWidget
                {
                    Key = $"{report}_count",
                    Label = reports.Single(value => value.Key.Equals(report, StringComparison.OrdinalIgnoreCase)).Label,
                    Type = "metric",
                    Report = report,
                    Metric = "record_count"
                }).ToList()
            },
            Roles = roles
        };
    }

    internal static IReadOnlyList<BundleAcceptanceTestDocument> BuildAcceptanceTests(
        CanonicalGeneratedErpBundle generated)
    {
        var entities = generated.Entities.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var reports = generated.Reports.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        var tests = new List<BundleAcceptanceTestDocument>();
        foreach (var source in generated.AcceptanceTests)
        {
            var stepId = $"{source.Key}_step";
            var steps = new List<BundleAcceptanceStep>();
            var assertions = new List<BundleAcceptanceAssertion>();
            if (source.Kind == "entity_create" && source.EntityKey is not null && entities.TryGetValue(source.EntityKey, out var entity))
            {
                var fields = entity.Fields.ToDictionary(value => value.Key, StringComparer.Ordinal);
                var values = source.Values.ToDictionary(
                    value => value.FieldKey,
                    value => (object?)ConvertAcceptanceValue(fields[value.FieldKey], value.Value),
                    StringComparer.Ordinal);
                steps.Add(new BundleAcceptanceStep
                {
                    Id = stepId,
                    Type = BundleAcceptanceStepTypes.CreateRecord,
                    Entity = entity.Key,
                    AsRole = "owner",
                    Values = values
                });
                assertions.Add(new BundleAcceptanceAssertion
                {
                    Type = "record_exists",
                    Entity = entity.Key,
                    Record = new BundleRecordSelector { Ref = stepId }
                });
            }
            else if (source.Kind == "report_access" && source.ReportKey is not null && reports.Contains(source.ReportKey))
            {
                steps.Add(new BundleAcceptanceStep
                {
                    Id = stepId,
                    Type = "call_report",
                    Report = source.ReportKey,
                    AsRole = "owner"
                });
            }
            else
            {
                throw new InvalidOperationException($"Acceptance test '{source.Key}' does not match the generated ERP contract.");
            }

            tests.Add(new BundleAcceptanceTestDocument
            {
                Test = new BundleAcceptanceTest
                {
                    Key = source.Key,
                    Label = source.Label,
                    Origin = BundleAcceptanceTestOrigins.Authored
                },
                Steps = steps,
                Assertions = assertions
            });
        }
        if (tests.Count == 0) throw new InvalidOperationException("At least one executable acceptance test is required.");
        BundleAcceptanceTestContractHasher.Stamp(tests);
        return tests;
    }

    private static object ConvertAcceptanceValue(CanonicalGeneratedField field, string value) => field.Type switch
    {
        "integer" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) => result,
        "decimal" when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) => result,
        "boolean" when bool.TryParse(value, out var result) => result,
        "reference" => throw new InvalidOperationException("Acceptance test reference values are unsupported."),
        _ => value
    };

    private static void AddGeneratedGrant(
        ErpRole role,
        string value,
        IReadOnlySet<string> entityKeys,
        IReadOnlyDictionary<string, CanonicalGeneratedReport> reports,
        IReadOnlyDictionary<string, CanonicalGeneratedDashboard> dashboards)
    {
        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !SupportedGrantActions.Contains(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) return;

        var action = parts[0];
        var resource = parts[1];
        if (entityKeys.Contains(resource) || !action.Equals("read", StringComparison.OrdinalIgnoreCase))
        {
            AddGrant(role, action, resource);
            return;
        }

        if (reports.TryGetValue(resource, out var report))
        {
            AddGrant(role, "read", report.EntityKey);
            AddPageGrant(role, "reports", "*");
            AddPageGrant(role, "report_detail", report.Key);
            return;
        }

        if (dashboards.TryGetValue(resource, out var dashboard))
        {
            AddPageGrant(role, "dashboard", "*");
            foreach (var reportKey in dashboard.Reports)
            {
                if (reports.TryGetValue(reportKey, out var dashboardReport))
                {
                    AddGrant(role, "read", dashboardReport.EntityKey);
                }
            }
            return;
        }

        AddGrant(role, action, resource);
    }

    private static void AddGrant(ErpRole role, string action, string entity)
    {
        if (!role.Grants.Any(value => value.Action == action && value.Entity == entity))
            role.Grants.Add(new ErpGrant { Action = action, Entity = entity });
    }

    private static void AddPageGrant(ErpRole role, string target, string resource)
    {
        if (!role.Grants.Any(value =>
                value.Scope == PermissionGrantScopes.Page &&
                value.Action == GranularPermissionActions.View &&
                value.Target == target &&
                value.Entity == resource))
        {
            role.Grants.Add(new ErpGrant
            {
                Scope = PermissionGrantScopes.Page,
                Action = GranularPermissionActions.View,
                Target = target,
                Entity = resource
            });
        }
    }

    private static string NormalizeKey(string? value) =>
        value?.Trim().ToLowerInvariant().Replace(' ', '_') ?? string.Empty;

    private static string RuntimeFieldName(CanonicalGeneratedField field) =>
        field.Type == "reference" ? $"{field.Key}_id" : field.Key;

    private static string MapFieldType(string value) => value switch
    {
        "long_text" => "text",
        "integer" => "integer",
        "decimal" => "number",
        "boolean" => "boolean",
        "date" => "date",
        "datetime" => "datetime",
        "reference" => "relation",
        _ => "string"
    };

    internal static void ValidateRequest(CanonicalBundleDeploymentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) ||
            request.Bundle.Entities.Count == 0 ||
            !IsSha256(request.ArtifactHash) ||
            !string.Equals(
                request.ArtifactHash,
                ComputeArtifactHash(request.Bundle),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A complete canonical deployment request is required.");
    }

    internal static string ComputeArtifactHash(CanonicalGeneratedErpBundle bundle)
    {
        var canonicalJson = JsonSerializer.Serialize(bundle, ArtifactJsonOptions);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateTarget(RuntimeDeploymentTarget target, ResolvedTenantEnvironment tenant)
    {
        if (target.Environment != "erp" || target.TenantId != tenant.TenantId ||
            target.TenantStableId != tenant.TenantStableId || target.DatabaseName != tenant.DatabaseName ||
            !target.Host.Equals(tenant.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deployment target lineage no longer matches the control plane.");
    }

    private static CanonicalBundleDeploymentResponse Failure(
        CanonicalBundleDeploymentRequest request,
        string code,
        string message) => new(
        false,
        "failed",
        request.OperationId,
        request.Target,
        FailureCode: code,
        SafeError: message);
}
