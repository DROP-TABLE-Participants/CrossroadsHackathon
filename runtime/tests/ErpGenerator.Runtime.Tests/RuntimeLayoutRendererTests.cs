using FluentAssertions;
using ErpGenerator.Runtime.Shared.Tenants;

namespace ErpGenerator.Runtime.Tests;

public sealed class RuntimeLayoutRendererTests
{
    [Fact]
    public void Sidebar_keeps_records_in_a_scrollable_collapsible_group()
    {
        var tenant = new ResolvedTenantEnvironment(
            "tenant-1",
            "stable-1",
            "dandelion-chocolate",
            "dandelion-chocolate.staging.localhost",
            TenantEnvironmentKind.Staging,
            "erp_tenant_staging",
            null,
            "http://dandelion-chocolate.staging.localhost");
        var session = new RuntimeSession(
            tenant.TenantStableId,
            tenant.Host,
            1,
            "owner@example.test",
            "runtime_owner",
            DateTimeOffset.UtcNow.AddHours(1));
        var entities = Enumerable.Range(1, 32)
            .Select(index => new RuntimeEntityDefinition(
                $"entity_{index}",
                $"entity-{index}",
                $"entity_{index}",
                $"Entity {index}",
                "runtime",
                $"entity_{index}",
                "name",
                null,
                []))
            .ToArray();

        var html = RuntimeLayoutRenderer.ShellPage("Dashboard", tenant, session, entities, "<p>Dashboard</p>");

        html.Should().Contain("data-nav-group=\"records\"");
        html.Should().Contain("aria-controls=\"records-navigation\"");
        html.Should().Contain("""<span class="nav-group-count">32</span>""");
        html.Should().Contain("erpgenerator-nav-records");
        RuntimeStyles.Css.Should().Contain(".nav { flex: 1 1 auto; min-height: 0;");
        RuntimeStyles.Css.Should().Contain("overflow-y: auto;");
        RuntimeStyles.Css.Should().Contain(""".nav-group[data-expanded="false"] .nav-group-links { display: none; }""");
    }
}
