using AmneziaGeo.Server.Api.Auth;
using AmneziaGeo.Server.Api.Dns;
using AmneziaGeo.Server.Api.Rules;
using AmneziaGeo.Server.Auth;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Routing.Host;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Api.Outbounds;

/// <summary>
/// The routes the ways out of the host are managed through.
/// </summary>
public static class OutboundEndpoints
{
    /// <summary>
    /// Maps the outbound management routes.
    /// </summary>
    public static IEndpointRouteBuilder MapOutbounds(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var reading = routes.MapGroup("/api/outbounds").RequireScope(Scopes.ReadState);
        reading.MapGet("/", ListAsync);
        reading.MapGet("/{id:long}", FindAsync);
        var writing = routes.MapGroup("/api/outbounds").RequireScope(Scopes.ManageRouting);
        writing.MapGet("/draft", Draft);
        writing.MapAwgOutbounds();
        writing.MapPost("/", AddAsync);
        writing.MapPut("/{id:long}", ChangeAsync);
        writing.MapPost("/{id:long}/switch", SwitchAsync);
        writing.MapPost("/{id:long}/move", MoveAsync);
        writing.MapPost("/{id:long}/apply", ApplyAsync);
        writing.MapPost("/{id:long}/probe", ProbeAsync);
        writing.MapPost("/apply", SyncAsync);
        writing.MapDelete("/{id:long}", RemoveAsync);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        OutboundStore store,
        OutboundHost host,
        OutboundAnswers answers,
        CancellationToken ct)
    {
        var found = await store.ListAsync(ct).ConfigureAwait(false);
        var states = host.States(found);
        var secrets = Secrets(context);

        return Results.Ok(found
            .Select((outbound, at) => answers.Outbound(outbound, states[at], secrets))
            .ToArray());
    }

    private static async Task<IResult> FindAsync(
        long id,
        HttpContext context,
        OutboundStore store,
        OutboundHost host,
        OutboundAnswers answers,
        CancellationToken ct)
    {
        var found = await store.FindAsync(id, ct).ConfigureAwait(false);

        return found is null
            ? Refuse(StatusCodes.Status404NotFound, "unknown-outbound", $"there is no outbound under the number {id}")
            : Results.Ok(answers.Outbound(found, host.State(found), Secrets(context)));
    }

    private static IResult Draft(string? name, string? kind, IEnumerable<IOutboundDriver> drivers,
        OutboundAnswers answers)
    {
        var requested = string.IsNullOrWhiteSpace(kind) ? OutboundKind.Local : kind.Trim();
        var driver = drivers.FirstOrDefault(one => one.Supports(requested));

        return driver is null
            ? Refuse(StatusCodes.Status400BadRequest, "bad-kind", $"no driver carries '{requested}'")
            : Results.Ok(answers.Outbound(
                driver.Draft(string.IsNullOrWhiteSpace(name) ? "out0" : name.Trim(), requested), null, true));
    }

    private static async Task<IResult> AddAsync(
        OutboundRequest request,
        OutboundStore store,
        OutboundHost host,
        RouteApplier routes,
        OutboundAnswers answers,
        CancellationToken ct)
    {
        using var hold = await host.HoldAsync(ct).ConfigureAwait(false);
        var draft = OutboundAnswers.Draft(request);
        if (host.CheckReservation(draft) is { } conflict)
        {
            return Refuse(StatusCodes.Status409Conflict, conflict.Code, conflict.Message);
        }

        var result = await store.AddAsync(draft, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return Explain(result);
        }

        var state = await host.ApplyAsync(result.Record!, ct).ConfigureAwait(false);
        await FirewallAsync(store, host, routes, ct).ConfigureAwait(false);

        return Results.Created(
            $"/api/outbounds/{result.Record!.Id}",
            answers.Outbound(result.Record, state, true));
    }

    private static async Task<IResult> ProbeAsync(
        long id,
        OutboundStore store,
        OutboundHost host,
        ProbeRunner runner,
        RouteApplier routes,
        OutboundAnswers answers,
        CancellationToken ct)
    {
        var found = await store.FindAsync(id, ct).ConfigureAwait(false);
        if (found is null)
        {
            return Refuse(
                StatusCodes.Status404NotFound,
                "unknown-outbound",
                $"there is no outbound under the number {id}");
        }

        if (await runner.RunAsync(found, ct).ConfigureAwait(false))
        {
            await routes.SettleAsync(ct).ConfigureAwait(false);
        }

        return Results.Ok(answers.State(host.State(found)));
    }

    private static async Task<IResult> ChangeAsync(
        long id,
        OutboundRequest request,
        OutboundStore store,
        BalanceStore balancers,
        RouteStore rules,
        OutboundHost host,
        RouteApplier routes,
        DnsHost resolver,
        OutboundAnswers answers,
        ILogger<OutboundHost> logger,
        CancellationToken ct)
    {
        using var hold = await host.HoldAsync(ct).ConfigureAwait(false);
        var held = await store.FindAsync(id, ct).ConfigureAwait(false);
        var draft = OutboundAnswers.Draft(request);
        if (held is not null && host.CheckReservation(draft, held) is { } conflict)
        {
            return Refuse(StatusCodes.Status409Conflict, conflict.Code, conflict.Message);
        }

        if (held is not null
            && request.Balancers is { } holders
            && await balancers.RefuseHoldersAsync(held.Name, holders, ct).ConfigureAwait(false) is { } refusal)
        {
            return Refuse(StatusCodes.Status400BadRequest, refusal.Code, refusal.Message);
        }

        if (held is not null
            && request.Rules is { } leaving
            && await rules.RefuseLeavingAsync(held.Name, leaving, ct).ConfigureAwait(false) is { } refused)
        {
            return Refuse(StatusCodes.Status400BadRequest, refused.Code, refused.Message);
        }

        var result = await store.ChangeAsync(id, draft, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return Explain(result);
        }

        if (request.Balancers is not null)
        {
            await balancers.HoldAsync(result.Record!.Name, request.Balancers, ct).ConfigureAwait(false);
        }

        if (request.Rules is not null)
        {
            await rules.LeaveThroughAsync(result.Record!.Name, request.Rules, ct).ConfigureAwait(false);
        }

        var state = await host.ApplyAsync(result.Record!, ct, held).ConfigureAwait(false);
        if (state.Fault.Length > 0)
        {
            logger.LogWarning("the outbound {Outbound} is saved but its host change failed: {Reason}",
                result.Record!.Name, state.Fault);
        }
        else if (held is not null && (held.Name != result.Record!.Name || held.Kind != result.Record.Kind
            || held.InterfaceName != result.Record.InterfaceName))
        {
            logger.LogInformation("the host completed the change of outbound {Outbound} from {Kind} to {NewKind}",
                result.Record.Name, held.Kind, result.Record.Kind);
        }

        await FirewallAsync(store, host, routes, ct).ConfigureAwait(false);
        if (held is not null)
        {
            await resolver.FollowAsync(held.Name, result.Record!.Name, ct).ConfigureAwait(false);
        }

        return Results.Ok(answers.Outbound(result.Record!, state, true));
    }

    private static async Task<IResult> SwitchAsync(
        long id,
        OutboundSwitchRequest request,
        OutboundStore store,
        OutboundHost host,
        RouteApplier routes,
        OutboundAnswers answers,
        CancellationToken ct)
    {
        if (request.On is not { } on)
        {
            return Refuse(StatusCodes.Status400BadRequest, "incomplete", "a switch needs the on field");
        }

        using var hold = await host.HoldAsync(ct).ConfigureAwait(false);
        var result = await store.SwitchAsync(id, on, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return Explain(result);
        }

        var state = await host.ApplyAsync(result.Record!, ct).ConfigureAwait(false);
        await FirewallAsync(store, host, routes, ct).ConfigureAwait(false);

        return Results.Ok(answers.Outbound(result.Record!, state, true));
    }

    private static async Task<IResult> MoveAsync(
        long id,
        OutboundMoveRequest request,
        OutboundStore store,
        OutboundHost host,
        OutboundAnswers answers,
        CancellationToken ct)
    {
        var result = await store.MoveAsync(id, request.Up, ct).ConfigureAwait(false);

        return result.IsOk
            ? Results.Ok(answers.Outbound(result.Record!, host.State(result.Record!), true))
            : Explain(result);
    }

    private static async Task<IResult> RemoveAsync(
        long id,
        OutboundStore store,
        OutboundHost host,
        RouteApplier routes,
        DnsHost resolver,
        CancellationToken ct)
    {
        using var hold = await host.HoldAsync(ct).ConfigureAwait(false);
        var result = await store.RemoveAsync(id, resolver.Asking, ct).ConfigureAwait(false);
        if (!result.IsOk)
        {
            return Explain(result);
        }

        await host.WithdrawAsync(result.Record!, ct).ConfigureAwait(false);
        await FirewallAsync(store, host, routes, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> ApplyAsync(long id, OutboundStore store, OutboundHost host, RouteApplier routes,
        OutboundAnswers answers, CancellationToken ct)
    {
        using var hold = await host.HoldAsync(ct).ConfigureAwait(false);
        var found = await store.FindAsync(id, ct).ConfigureAwait(false);
        if (found is null)
        {
            return Refuse(StatusCodes.Status404NotFound, "unknown-outbound",
                $"there is no outbound under the number {id}");
        }

        var state = await host.ApplyAsync(found, ct).ConfigureAwait(false);
        await FirewallAsync(store, host, routes, ct).ConfigureAwait(false);

        return Results.Ok(answers.Outbound(found, state, true));
    }

    private static async Task<IResult> SyncAsync(OutboundStore store, OutboundHost host, RouteApplier routes,
        OutboundAnswers answers, CancellationToken ct)
    {
        using var hold = await host.HoldAsync(ct).ConfigureAwait(false);
        var found = await store.ListAsync(ct).ConfigureAwait(false);
        try
        {
            var states = await host.SyncAsync(found, ct).ConfigureAwait(false);
            await routes.SettleAsync(ct).ConfigureAwait(false);

            return Results.Ok(found
                .Select((outbound, at) => answers.Outbound(outbound, states[at], true))
                .ToArray());
        }
        catch (HostNetworkException ex)
        {
            return Refuse(StatusCodes.Status409Conflict, "host-refused", ex.Message);
        }
    }

    private static async Task FirewallAsync(OutboundStore store, OutboundHost host, RouteApplier routes,
        CancellationToken ct)
    {
        try
        {
            await host.FirewallAsync(await store.ListAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (HostNetworkException)
        {
        }

        await routes.SettleAsync(ct).ConfigureAwait(false);
    }

    private static bool Secrets(HttpContext context) => context.Caller()?.Holds(Scopes.ManageRouting) == true;

    private static IResult Explain(OutboundResult result) =>
        Refuse(Status(result.Outcome), result.Code, result.Message);

    private static int Status(OutboundOutcome outcome) => outcome switch
    {
        OutboundOutcome.Unknown => StatusCodes.Status404NotFound,
        OutboundOutcome.NameTaken or OutboundOutcome.NoMark => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static IResult Refuse(int status, string error, string message) =>
        Results.Json(new Failure(error, message), statusCode: status);
}
