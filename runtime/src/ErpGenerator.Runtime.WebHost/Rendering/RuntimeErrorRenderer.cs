using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeErrorRenderer
{
    public static string RenderUnknownTenant() =>
        Page("Tenant not found", ErrorCard("lock", "Unregistered host", "This runtime host is not registered.",
            "Check the address, or open your ERP from the ErpGenerator account panel.", ""));

    public static string RenderProvisioningShell(string tenantSlug) =>
        Page("Runtime provisioning", ErrorCard("clock", Html(RuntimeUi.HumanizeTenant(tenantSlug)),
            "This ERP is waiting for its first deployment.",
            "Once a bundle is deployed to this environment, your workspace will be ready here.", ""));

    public static string RenderNotFound(string title, string message) =>
        Page(title, ErrorCard("search", "Not found", title, message,
            """<a class="button" href="/">Back to dashboard</a>"""));

    public static string RenderErrors(IReadOnlyList<RuntimeValidationError> errors) =>
        $"""
        <main class="center">
          <section class="panel error-card">
            <span class="error-glyph" aria-hidden="true">{RuntimeUi.Icon("alert", "")}</span>
            <h1>Validation failed</h1>
            <div class="error-list" style="text-align:left">
              <p class="error-list-title">Please fix the following:</p>
              {string.Join("", errors.Select(error => $"<p>{Html(error.Field)}: {Html(error.Message)}</p>"))}
            </div>
          </section>
        </main>
        """;

    static string ErrorCard(string icon, string eyebrow, string title, string message, string actionHtml) =>
        $"""
        <main class="center">
          <section class="panel error-card">
            <span class="error-glyph" aria-hidden="true">{RuntimeUi.Icon(icon, "")}</span>
            <p class="eyebrow">{eyebrow}</p>
            <h1>{Html(title)}</h1>
            <p>{Html(message)}</p>
            {actionHtml}
          </section>
        </main>
        """;
}
