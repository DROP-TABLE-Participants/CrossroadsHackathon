using System.Globalization;
using static RuntimeHtml;

/// <summary>
/// Shared, application-level UI primitives reused by every runtime page:
/// the single status vocabulary (one semantic tone + readable label per state),
/// inline icons, and small building blocks (badges, breadcrumbs, empty states).
/// Centralising these keeps components consistent across modules instead of each
/// page styling raw markup independently.
/// </summary>
internal static class RuntimeUi
{
    /// <summary>
    /// Maps an arbitrary workflow / record / deployment status onto one of the
    /// five shared semantic tones. Colour never carries meaning alone — the pill
    /// always shows a text label and a shape (dot) too.
    /// </summary>
    public static string StatusTone(string? status)
    {
        var key = (status ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return key switch
        {
            "active" or "approved" or "completed" or "complete" or "done" or "success" or "succeeded"
                or "enabled" or "posted" or "paid" or "resolved" or "fulfilled" or "received" or "delivered"
                or "deployed" or "promoted" or "healthy" or "passed" => "tone-success",
            "pending" or "waiting" or "pending_approval" or "awaiting_approval" or "in_review" or "review"
                or "on_hold" or "hold" or "partial" or "needs_action" or "action_required" => "tone-warning",
            "rejected" or "failed" or "failure" or "error" or "cancelled" or "canceled" or "denied"
                or "blocked" or "overdue" or "expired" or "void" or "reversed" or "unhealthy" => "tone-danger",
            "running" or "in_progress" or "processing" or "started" or "scheduled" or "unread" or "new"
                or "open" or "submitted" or "sent" or "info" or "waiting_for_interview" => "tone-info",
            _ => "tone-neutral"
        };
    }

    /// <summary>Renders a status pill with the shared tone + a human-readable label.</summary>
    public static string StatusPill(string? status, string? label = null) =>
        $"""<span class="status-pill {StatusTone(status)}">{Html(label ?? Humanize(status))}</span>""";

    /// <summary>Title-cases a snake_case / kebab-case identifier for display.</summary>
    public static string Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var parts = value.Trim().Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(part =>
            part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>Two-letter avatar initials from an email or display name.</summary>
    public static string Initials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var name = value.Contains('@') ? value[..value.IndexOf('@')] : value;
        var parts = name.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return char.ToUpperInvariant(value[0]).ToString();
        }

        var first = char.ToUpperInvariant(parts[0][0]);
        var second = parts.Length > 1 ? char.ToUpperInvariant(parts[1][0]) : (parts[0].Length > 1 ? char.ToUpperInvariant(parts[0][1]) : ' ');
        return second == ' ' ? first.ToString() : $"{first}{second}";
    }

    /// <summary>
    /// Turns a tenant slug ("balkan-furniture-b31bcd0ff5af") into a readable
    /// company name ("Balkan Furniture") by dropping a trailing stable-id hex
    /// suffix and title-casing the rest.
    /// </summary>
    public static string HumanizeTenant(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "ERP";
        }

        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1)
        {
            var last = parts[^1];
            if (last.Length >= 8 && last.All(Uri.IsHexDigit))
            {
                parts.RemoveAt(parts.Count - 1);
            }
        }

        return parts.Count == 0
            ? slug
            : string.Join(" ", parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>Breadcrumb trail. The final crumb is marked as the current page.</summary>
    public static string Breadcrumbs(params (string Label, string? Href)[] crumbs)
    {
        if (crumbs.Length == 0)
        {
            return "";
        }

        var items = new List<string>();
        for (var i = 0; i < crumbs.Length; i++)
        {
            var (label, href) = crumbs[i];
            if (i > 0)
            {
                items.Add("""<span class="sep" aria-hidden="true">/</span>""");
            }

            items.Add(i == crumbs.Length - 1 || string.IsNullOrEmpty(href)
                ? $"""<span aria-current="page">{Html(label)}</span>"""
                : $"""<a href="{Html(href)}">{Html(label)}</a>""");
        }

        return $"""<nav class="breadcrumbs" aria-label="Breadcrumb">{string.Join("", items)}</nav>""";
    }

    /// <summary>A full empty / zero / no-results state with icon, explanation and optional action.</summary>
    public static string EmptyState(string title, string description, string iconName = "inbox", string actionHtml = "") =>
        $"""
        <div class="empty-state">
          <span class="empty-icon" aria-hidden="true">{Icon(iconName, "")}</span>
          <strong>{Html(title)}</strong>
          <span>{Html(description)}</span>
          {(string.IsNullOrEmpty(actionHtml) ? "" : $"""<div class="empty-actions">{actionHtml}</div>""")}
        </div>
        """;

    /// <summary>
    /// Picks a distinct icon for a generated entity from keywords in its key and
    /// display name, so Customers, Suppliers, Quotes, Goods Receipts, etc. don't
    /// all share the generic records icon. Falls back to the records icon.
    /// </summary>
    public static string EntityIconName(string key, string displayName)
    {
        var s = $" {key} {displayName} ".ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        bool Has(params string[] words) => words.Any(w => s.Contains(w));

        if (Has("supplier", "vendor")) return "truck";
        if (Has("customer", "client", "buyer", "contact", "lead", "prospect")) return "users";
        if (Has("employee", "staff", "worker", "personnel", "user ")) return "user";
        if (Has("quote", "quotation", "estimate", "proposal", "offer")) return "file-text";
        if (Has("invoice", "bill ", "billing")) return "receipt";
        if (Has("receipt", "grn", "goods in")) return "boxes";
        if (Has("payment", "remittance", "settlement", "expense")) return "credit-card";
        if (Has("product", "item", "sku", "material", "part", "catalog", "good")) return "package";
        if (Has("inventory", "stock", "warehouse", "lot", "batch", "bin")) return "boxes";
        if (Has("order", "purchase", "requisition", "cart")) return "cart";
        if (Has("project", "job ", "assignment")) return "folder";
        if (Has("shipment", "delivery", "dispatch", "transport")) return "truck";
        if (Has("contract", "agreement", "document", "policy", "note")) return "file-text";
        if (Has("category", "tag", "label", "price", "pricing")) return "tag";
        if (Has("company", "organization", "organisation", "branch", "department", "location", "site")) return "building";
        if (Has("task", "ticket", "request", "approval")) return "clipboard";
        if (Has("payment", "transaction", "account", "ledger", "journal")) return "credit-card";
        return "records";
    }

    /// <summary>An inline, lucide-style stroke icon. cls defaults to the nav sizing class.</summary>
    public static string Icon(string name, string cls = "nav-icon")
    {
        var path = name switch
        {
            "dashboard" => """<rect x="3" y="3" width="7" height="9"/><rect x="14" y="3" width="7" height="5"/><rect x="14" y="12" width="7" height="9"/><rect x="3" y="16" width="7" height="5"/>""",
            "reports" => """<path d="M3 3v18h18"/><path d="m19 9-5 5-4-4-3 3"/>""",
            "tasks" => """<path d="M11 12H3"/><path d="m15 6-2 2-2-2"/><path d="M11 6H3"/><path d="M21 18H3"/><path d="m15 12 6 6"/>""",
            "bell" => """<path d="M10.268 21a2 2 0 0 0 3.464 0"/><path d="M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"/>""",
            "users" => """<path d="M18 21a8 8 0 0 0-16 0"/><circle cx="10" cy="8" r="5"/><path d="M22 20c0-3.37-2-6.5-4-8a5 5 0 0 0-.45-8.3"/>""",
            "settings" => """<path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/>""",
            "records" => """<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/>""",
            "plus" => """<path d="M5 12h14"/><path d="M12 5v14"/>""",
            "download" => """<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><path d="M7 10l5 5 5-5"/><path d="M12 15V3"/>""",
            "search" => """<circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/>""",
            "message" => """<path d="M7.9 20A9 9 0 1 0 4 16.1L2 22Z"/>""",
            "help" => """<circle cx="12" cy="12" r="10"/><path d="M9.09 9a3 3 0 1 1 5.83 1c0 2-3 2-3 4"/><path d="M12 18h.01"/>""",
            "sun" => """<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/>""",
            "menu" => """<line x1="4" y1="6" x2="20" y2="6"/><line x1="4" y1="12" x2="20" y2="12"/><line x1="4" y1="18" x2="20" y2="18"/>""",
            "panel-left" => """<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M9 3v18"/>""",
            "more" => """<circle cx="12" cy="5" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="12" cy="19" r="1"/>""",
            "sort" => """<path d="m7 15 5 5 5-5"/><path d="m7 9 5-5 5 5"/>""",
            "sort-asc" => """<path d="m7 9 5-5 5 5"/>""",
            "sort-desc" => """<path d="m7 15 5 5 5-5"/>""",
            "chevron-right" => """<path d="m9 18 6-6-6-6"/>""",
            "chevron-down" => """<path d="m6 9 6 6 6-6"/>""",
            "check" => """<path d="M20 6 9 17l-5-5"/>""",
            "x" => """<path d="M18 6 6 18"/><path d="m6 6 12 12"/>""",
            "alert" => """<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"/><path d="M12 9v4"/><path d="M12 17h.01"/>""",
            "info" => """<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>""",
            "inbox" => """<path d="M22 12h-6l-2 3h-4l-2-3H2"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>""",
            "filter" => """<polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3"/>""",
            "arrow-left" => """<path d="m12 19-7-7 7-7"/><path d="M19 12H5"/>""",
            "external" => """<path d="M15 3h6v6"/><path d="M10 14 21 3"/><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>""",
            "clock" => """<circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/>""",
            "lock" => """<rect width="18" height="11" x="3" y="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>""",
            "logout" => """<path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" x2="9" y1="12" y2="12"/>""",
            "copy" => """<rect width="14" height="14" x="8" y="8" rx="2" ry="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/>""",
            "workflow" => """<rect width="8" height="8" x="3" y="3" rx="2"/><path d="M7 11v4a2 2 0 0 0 2 2h4"/><rect width="8" height="8" x="13" y="13" rx="2"/>""",
            "package" => """<path d="M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z"/><path d="M12 22V12"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="m7.5 4.27 9 5.15"/>""",
            "truck" => """<path d="M14 18V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v11a1 1 0 0 0 1 1h2"/><path d="M15 18H9"/><path d="M19 18h2a1 1 0 0 0 1-1v-3.65a1 1 0 0 0-.22-.624l-3.48-4.35A1 1 0 0 0 17.52 8H14"/><circle cx="17" cy="18" r="2"/><circle cx="7" cy="18" r="2"/>""",
            "user" => """<circle cx="12" cy="8" r="5"/><path d="M20 21a8 8 0 0 0-16 0"/>""",
            "file-text" => """<path d="M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z"/><path d="M14 2v4a2 2 0 0 0 2 2h4"/><path d="M10 9H8"/><path d="M16 13H8"/><path d="M16 17H8"/>""",
            "receipt" => """<path d="M4 2v20l2-1 2 1 2-1 2 1 2-1 2 1 2-1 2 1V2l-2 1-2-1-2 1-2-1-2 1-2-1-2 1Z"/><path d="M16 8h-6a2 2 0 1 0 0 4h4a2 2 0 1 1 0 4H8"/><path d="M12 17.5v-11"/>""",
            "boxes" => """<path d="M2.97 12.92A2 2 0 0 0 2 14.63v3.24a2 2 0 0 0 .97 1.71l3 1.8a2 2 0 0 0 2.06 0L12 19v-5.5l-5-3-4.03 2.42Z"/><path d="m7 16.5-4.74-2.85"/><path d="m7 16.5 5-3"/><path d="M7 16.5v5.17"/><path d="M12 13.5V19l3.97 2.38a2 2 0 0 0 2.06 0l3-1.8a2 2 0 0 0 .97-1.71v-3.24a2 2 0 0 0-.97-1.71L17 10.5l-5 3Z"/><path d="m17 16.5-5-3"/><path d="m17 16.5 4.74-2.85"/><path d="M17 16.5v5.17"/><path d="M7.97 4.42A2 2 0 0 0 7 6.13v4.37l5 3 5-3V6.13a2 2 0 0 0-.97-1.71l-3-1.8a2 2 0 0 0-2.06 0l-3 1.8Z"/><path d="M12 8 7.26 5.15"/><path d="m12 8 4.74-2.85"/><path d="M12 13.5V8"/>""",
            "credit-card" => """<rect width="20" height="14" x="2" y="5" rx="2"/><line x1="2" x2="22" y1="10" y2="10"/>""",
            "folder" => """<path d="M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"/>""",
            "clipboard" => """<rect width="8" height="4" x="8" y="2" rx="1" ry="1"/><path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"/>""",
            "cart" => """<circle cx="8" cy="21" r="1"/><circle cx="19" cy="21" r="1"/><path d="M2.05 2.05h2l2.66 12.42a2 2 0 0 0 2 1.58h9.78a2 2 0 0 0 1.95-1.57l1.65-7.43H5.12"/>""",
            "building" => """<rect width="16" height="20" x="4" y="2" rx="2" ry="2"/><path d="M9 22v-4h6v4"/><path d="M8 6h.01M16 6h.01M12 6h.01M12 10h.01M12 14h.01M16 10h.01M16 14h.01M8 10h.01M8 14h.01"/>""",
            "tag" => """<path d="M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z"/><circle cx="7.5" cy="7.5" r=".5" fill="currentColor"/>""",
            "sparkles" => """<path d="M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z"/><path d="M20 3v4"/><path d="M22 5h-4"/><path d="M4 17v2"/><path d="M5 18H3"/>""",
            "trending-up" => """<path d="M16 7h6v6"/><path d="m22 7-8.5 8.5-5-5L2 17"/>""",
            _ => """<ellipse cx="12" cy="5" rx="9" ry="3"/><path d="M3 5v14a9 3 0 0 0 18 0V5"/><path d="M3 12a9 3 0 0 0 18 0"/>"""
        };
        var cssClass = string.IsNullOrEmpty(cls) ? "" : $" class=\"{cls}\"";
        return $"""<svg{cssClass} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">{path}</svg>""";
    }
}
