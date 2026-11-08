using System.Net;
using System.Text.Json;
using Analytics.Core;
using Microsoft.Extensions.Options;

namespace Analytics.Ingest;

internal static class EventEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static int _identityWarned;

    public static async Task<IResult> HandleAsync(
        HttpContext ctx,
        SiteDirectory sites,
        VisitorHasher hasher,
        UserIdHasher users,
        GeoIpUpdater geo,
        EventQueue queue,
        IOptions<IngestOptions> options,
        TimeProvider time,
        ILogger<EventEndpointLog> log)
    {
        // The request body size is capped globally by Kestrel (see Program.cs); oversize bodies get 413.
        EventRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<EventRequest>(ctx.Request.Body, JsonOptions, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "invalid JSON" });
        }
        catch (BadHttpRequestException ex)
        {
            // e.g. 413 for an oversized body. Answering here keeps Kestrel from logging it as an application error.
            return Results.StatusCode(ex.StatusCode);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && ctx.RequestAborted.IsCancellationRequested)
        {
            // The visitor left mid-request (closed the tab). Nothing to answer and nothing worth logging.
            return Results.Empty;
        }

        if (request is null) return Results.BadRequest(new { error = "empty body" });

        if (!EventValidator.TryValidate(request, out var ev, out var error))
        {
            return Results.BadRequest(new { error });
        }

        if (!sites.TryGet(ev!.SiteDomain, out var site))
        {
            return Results.NotFound(new { error = "unknown site" });
        }

        if (options.Value.EnforceOrigin &&
            ctx.Request.Headers.Origin is { Count: > 0 } origin &&
            !OriginAllowed(origin.ToString(), site.Domain))
        {
            return Results.Json(new { error = "origin not allowed" }, statusCode: StatusCodes.Status403Forbidden);
        }

        var userAgent = ctx.Request.Headers.UserAgent.ToString();
        var agent = UserAgentParser.Parse(userAgent);
        if (agent.IsBot) return Results.Accepted(); // acknowledged but not recorded

        var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        var now = time.GetUtcNow().UtcDateTime;

        string visitor;
        if (site.IdentifyUsers && ev.UserId is { } userId && users.IsEnabled)
        {
            visitor = users.Hash(site.Id, userId);
        }
        else
        {
            if (site.IdentifyUsers && ev.UserId is not null && Interlocked.Exchange(ref _identityWarned, 1) == 0)
            {
                log.LogWarning(
                    "Site {Site} has identify_users enabled but Ingest:IdentitySecret is not set; user ids are ignored",
                    site.Domain);
            }
            visitor = await hasher.HashAsync(site.Id.ToString(), ip, userAgent, now, ctx.RequestAborted);
        }

        var record = new EventRecord(
            now, site.Id, visitor, ev.Type, ev.Path, ev.Referrer,
            geo.Lookup(ip), agent.Browser, agent.Os, agent.Device,
            ev.UtmSource, ev.UtmMedium, ev.UtmCampaign, ev.PropsJson);

        if (!queue.TryEnqueue(record))
        {
            ctx.Response.Headers.RetryAfter = "5";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Accepted();
    }

    private static bool OriginAllowed(string origin, string siteDomain) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && SiteDomain.HostMatchesSite(uri.Host, siteDomain);
}

/// <summary>Logger category for <see cref="EventEndpoint"/> (a static class cannot be a type argument).</summary>
internal sealed class EventEndpointLog;
