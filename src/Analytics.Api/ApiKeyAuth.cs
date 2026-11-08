using Analytics.Core;
using Microsoft.Extensions.Caching.Memory;

namespace Analytics.Api;

/// <summary>Bearer API-key authentication implemented as endpoint filters.</summary>
internal static class ApiKeyAuth
{
    private const string ItemKey = "analytics.api-key";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    // Keys revoked through this process. Cached entries are checked against it, so revocation is immediate here;
    // a key deleted directly in the database stops working within CacheTtl. Ids are never reused.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> Revoked = new();

    public static void MarkRevoked(long id) => Revoked[id] = 0;

    /// <summary>Validates "Authorization: Bearer &lt;key&gt;".</summary>
    public static async ValueTask<object?> AuthenticateAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;

        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return Unauthorized();

        var token = header["Bearer ".Length..].Trim();
        if (token.Length is 0 or > 200) return Unauthorized();

        var hash = ApiKeys.Hash(token);
        var cacheKey = "api-key:" + Convert.ToHexString(hash);
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();

        if (!cache.TryGetValue(cacheKey, out ApiKeyInfo? info) || info is null)
        {
            info = await http.RequestServices.GetRequiredService<IApiKeyStore>().FindAsync(hash, http.RequestAborted);
            if (info is null) return Unauthorized();
            cache.Set(cacheKey, info, CacheTtl);
        }
        else if (Revoked.ContainsKey(info.Id))
        {
            cache.Remove(cacheKey);
            return Unauthorized();
        }

        http.Items[ItemKey] = info;
        return await next(ctx);
    }

    public static async ValueTask<object?> RequireAdminAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var info = ctx.HttpContext.Items[ItemKey] as ApiKeyInfo;
        return info?.Scope == ApiKeyScope.Admin
            ? await next(ctx)
            : Results.Json(new { error = "this endpoint requires an admin API key" }, statusCode: StatusCodes.Status403Forbidden);
    }

    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(RequireAdminAsync);

    private static IResult Unauthorized() =>
        Results.Json(new { error = "missing or invalid API key" }, statusCode: StatusCodes.Status401Unauthorized);
}
