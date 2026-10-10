using FluentAssertions;
using ErpGenerator.Runtime.Shared.Compilation.Validation;

namespace ErpGenerator.Runtime.Tests;

public sealed class BundleSafetyValidatorTests
{
    [Theory]
    [InlineData(
        "Single-site UK made-to-order furniture manufacturing ERP covering quotation, order release, procurement, inventory, production, shop-floor reporting, delivery, invoicing, job costing, month-end control, and management reporting. Xero remains the statutory ledger and payment-reconciliation system; SharePoint remains read-only document evidence storage.")]
    [InlineData("Users can select a supplier, update a quote, or delete a draft; changes are audited.")]
    [InlineData("Notes -- including review comments -- remain visible.")]
    public void Ordinary_declarative_text_is_allowed(string text)
    {
        var errors = new List<string>();

        BundleSafetyValidator.RejectSqlLikeContent(text, "bundle.description", errors);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("customer; DROP TABLE app.orders;")]
    [InlineData("SELECT * FROM users;")]
    [InlineData("INSERT INTO users (name) VALUES ('x');")]
    [InlineData("UPDATE users SET role = 'admin';")]
    [InlineData("DELETE FROM users WHERE id = 1;")]
    [InlineData("ALTER TABLE users ADD COLUMN is_admin boolean;")]
    [InlineData("TRUNCATE TABLE users;")]
    public void Sql_statements_are_rejected(string text)
    {
        var errors = new List<string>();

        BundleSafetyValidator.RejectSqlLikeContent(text, "bundle.description", errors);

        errors.Should().ContainSingle(error =>
            error.Contains("SQL-like", StringComparison.OrdinalIgnoreCase));
    }
}
