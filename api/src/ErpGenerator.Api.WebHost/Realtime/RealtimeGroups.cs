namespace ErpGenerator.Api.WebHost.Realtime;

internal static class RealtimeGroups
{
    public static string Customer(Guid customerId) => $"customer:{customerId:D}";
}
