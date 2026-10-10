using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ErpGenerator.Api.WebHost.Infrastructure;

namespace ErpGenerator.Api.WebHost.Realtime;

[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
public sealed class AdminRealtimeHub : Hub;
