namespace ErpGenerator.Api.Services.Implementations.Admin;

internal static class AdminQueryRules
{
    public static int Page(int value) => Math.Max(value, 1);

    public static int PageSize(int value) => Math.Clamp(value, 1, 100);
}
