using ErpGenerator.Runtime.Shared.Tenants;
using ErpGenerator.Runtime.Data.Databases;

namespace ErpGenerator.Runtime.Tests;

internal static class RuntimeRecordTestHelper
{
    public static async Task<long> CreateDraftTimesheetAsync(
        ResolvedTenantEnvironment tenant,
        RuntimeRecordStore records,
        RuntimeUserContext user,
        string suffix,
        double hours = 6.5)
    {
        var customer = await records.CreateRecordAsync(tenant, user, "customers", RuntimeJson.JsonObject(
            ("company_name", $"Customer {suffix}"),
            ("contact_email", $"customer-{suffix}@example.test"),
            ("status", "active")));
        var employee = await records.CreateRecordAsync(tenant, user, "employees", RuntimeJson.JsonObject(
            ("full_name", $"Employee {suffix}"),
            ("email", $"employee-{suffix}@example.test"),
            ("role", "Consultant")));
        var project = await records.CreateRecordAsync(tenant, user, "projects", RuntimeJson.JsonObject(
            ("customer_id", customer["id"]!),
            ("title", $"Project {suffix}"),
            ("budget", 1000),
            ("starts_on", "2026-07-02")));
        var timesheet = await records.CreateRecordAsync(tenant, user, "timesheets", RuntimeJson.JsonObject(
            ("employee_id", employee["id"]!),
            ("project_id", project["id"]!),
            ("logged_on", "2026-07-02"),
            ("hours", hours),
            ("approval_status", "draft")));

        return Convert.ToInt64(timesheet["id"]);
    }
}
