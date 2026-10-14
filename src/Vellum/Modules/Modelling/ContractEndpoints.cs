using Vellum.Modules.Identity.AgentTokens;
using Deedbox;
using Vellum.Modules.Workspaces;
using Vellum.Shared;
using Vellum.Modules.Trace.Engine;

namespace Vellum.Modules.Modelling;

/// <summary>
/// Operations, channels, subscriptions and bindings: the contracts a scenario plays against. They are
/// authorized like elements (Viewer reads, Editor writes) and accept <c>?branchId=</c>.
/// </summary>
public static class ContractEndpoints
{
    public static WebApplication MapContractEndpoints(this WebApplication app)
    {
        var project = app.MapGroup("/api/projects/{projectId}").RequireAuthorization();

        var operations = project.MapGroup("/operations").WithTags("Operations");

        operations.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Operations(store, streamId, cursor, limit, ct), ct))
            .AllowAgent(AgentScope.Read)
            .Produces<Page<OperationDto>>();

        operations.MapPost("/", (
            Guid projectId, Guid? branchId, AddOperationRequest request, HttpContext http,
            CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddOperation.HandleAsync(http.RequestServices, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/operations/{request.Id}"), ct))
            .Produces<OperationDto>(StatusCodes.Status201Created);

        operations.MapPatch("/{operationId}", (
            Guid projectId, Guid operationId, Guid? branchId, UpdateOperationRequest request, HttpContext http,
            TimeProvider time, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateOperation.HandleAsync(http.RequestServices, projectId, streamId, operationId, userId, request, time, ct)).ToHttpResult(), ct))
            .Produces<OperationDto>();

        operations.MapDelete("/{operationId}", (
            Guid projectId, Guid operationId, Guid? branchId, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveOperation.HandleAsync(store, projectId, streamId, operationId, ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        var channels = project.MapGroup("/channels").WithTags("Channels");

        channels.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Channels(store, streamId, cursor, limit, ct), ct))
            .AllowAgent(AgentScope.Read)
            .Produces<Page<ChannelDto>>();

        channels.MapPost("/", (
            Guid projectId, Guid? branchId, AddChannelRequest request, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddChannel.HandleAsync(store, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/channels/{request.Id}"), ct))
            .Produces<ChannelDto>(StatusCodes.Status201Created);

        channels.MapPatch("/{channelId}", (
            Guid projectId, Guid channelId, Guid? branchId, UpdateChannelRequest request, HttpContext http,
            TimeProvider time, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateChannel.HandleAsync(store, projectId, streamId, channelId, userId, request, time, ct)).ToHttpResult(), ct))
            .Produces<ChannelDto>();

        channels.MapDelete("/{channelId}", (
            Guid projectId, Guid channelId, Guid? branchId, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveChannel.HandleAsync(store, projectId, streamId, channelId, ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        var subscriptions = project.MapGroup("/subscriptions").WithTags("Subscriptions");

        subscriptions.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Subscriptions(store, streamId, cursor, limit, ct), ct))
            .AllowAgent(AgentScope.Read)
            .Produces<Page<SubscriptionDto>>();

        subscriptions.MapPost("/", (
            Guid projectId, Guid? branchId, AddSubscriptionRequest request, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddSubscription.HandleAsync(store, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/subscriptions/{request.Id}"), ct))
            .Produces<SubscriptionDto>(StatusCodes.Status201Created);

        subscriptions.MapPatch("/{subscriptionId}", (
            Guid projectId, Guid subscriptionId, Guid? branchId, UpdateSubscriptionRequest request, HttpContext http,
            TimeProvider time, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateSubscription.HandleAsync(store, projectId, streamId, subscriptionId, userId, request, time, ct)).ToHttpResult(), ct))
            .Produces<SubscriptionDto>();

        subscriptions.MapDelete("/{subscriptionId}", (
            Guid projectId, Guid subscriptionId, Guid? branchId, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveSubscription.HandleAsync(store, projectId, streamId, subscriptionId, ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        var bindings = project.MapGroup("/bindings").WithTags("Bindings");

        bindings.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Bindings(store, streamId, cursor, limit, ct), ct))
            .AllowAgent(AgentScope.Read)
            .Produces<Page<BindingDto>>();

        bindings.MapPost("/", (
            Guid projectId, Guid? branchId, AddBindingRequest request, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await AddBinding.HandleAsync(store, projectId, streamId, request, ct))
                    .ToCreatedResult($"/api/projects/{projectId}/bindings/{request.Id}"), ct))
            .Produces<BindingDto>(StatusCodes.Status201Created);

        bindings.MapPatch("/{bindingId}", (
            Guid projectId, Guid bindingId, Guid? branchId, UpdateBindingRequest request, HttpContext http,
            TimeProvider time, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await UpdateBinding.HandleAsync(store, projectId, streamId, bindingId, userId, request, time, ct)).ToHttpResult(), ct))
            .Produces<BindingDto>();

        bindings.MapDelete("/{bindingId}", (
            Guid projectId, Guid bindingId, Guid? branchId, HttpContext http,
            IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await RemoveBinding.HandleAsync(store, projectId, streamId, bindingId, ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}

/// <summary>Attempts include the first try; <c>maxAttempts = 1</c> means no retry.</summary>
public sealed record RetryDto(int MaxAttempts, int BackoffMs)
{
    public static RetryDto From(RetryPolicy retry) => new(retry.MaxAttempts, retry.BackoffMs);

    public RetryPolicy ToDomain() => new(MaxAttempts, BackoffMs);
}

public sealed record OperationDto(
    Guid Id, Guid OwnerId, string Name, string Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int TimeoutMs, RetryDto Retry,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static OperationDto From(OperationState operation) => new(
        operation.Id, operation.OwnerId, operation.Name, operation.Transport.ToString().ToLowerInvariant(),
        operation.RequestSchemaId, operation.ResponseSchemaId,
        operation.TimeoutMs, RetryDto.From(operation.Retry),
        operation.FactStatus.ToApiString(), ProvenanceMapper.ToDto(operation.Provenance), StaleDto.From(operation.Stale));
}

public sealed record ChannelDto(
    Guid Id, string Name, string Kind,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static ChannelDto From(ChannelState channel) => new(
        channel.Id, channel.Name, channel.Kind.ToString().ToLowerInvariant(),
        channel.FactStatus.ToApiString(), ProvenanceMapper.ToDto(channel.Provenance), StaleDto.From(channel.Stale));
}

public sealed record SubscriptionDto(
    Guid Id, Guid ChannelId, Guid ConsumerId,
    RetryDto Retry, Guid? DeadLetterChannelId,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static SubscriptionDto From(SubscriptionState subscription) => new(
        subscription.Id, subscription.ChannelId, subscription.ConsumerId,
        RetryDto.From(subscription.Retry), subscription.DeadLetterChannelId,
        subscription.FactStatus.ToApiString(), ProvenanceMapper.ToDto(subscription.Provenance), StaleDto.From(subscription.Stale));
}

public sealed record BindingDto(
    Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey,
    string FactStatus, ProvenanceDto? Provenance, StaleDto? Stale = null)
{
    public static BindingDto From(BindingState binding) => new(
        binding.Id, binding.SourceChannelId, binding.TargetChannelId, binding.RoutingKey,
        binding.FactStatus.ToApiString(), ProvenanceMapper.ToDto(binding.Provenance), StaleDto.From(binding.Stale));
}

/// <summary>What the contract requests share: reading a fact status label.</summary>
internal static class ContractRequests
{
    public static bool TryReadFactStatus(string? text, out FactStatus? status, out ValidationError? error)
    {
        status = null;
        error = null;
        if (text is null)
            return true;

        if (!FactStatusFormat.TryParseApiString(text, out var parsed))
        {
            error = new ValidationError("factStatus", "Invalid fact status");
            return false;
        }

        status = parsed;
        return true;
    }
}

/// <summary>
/// Contracts have no read-model rows. A list reads them from the branch's stored state, which
/// <see cref="ContractDecider.MaxPerKind"/> bounds, and returns one page of it. The order is
/// by name, then by ID; subscriptions and bindings have no name and are ordered by ID alone. Text is
/// compared by code point, so the order does not depend on the server's culture.
/// </summary>
public static class ListContracts
{
    public static async Task<IResult> Operations(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Operations.Values, o => o.Name, o => o.Id.Value, OperationDto.From, cursor, limit));
    }

    public static async Task<IResult> Channels(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Channels.Values, c => c.Name, c => c.Id.Value, ChannelDto.From, cursor, limit));
    }

    public static async Task<IResult> Subscriptions(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Subscriptions.Values, _ => "", s => s.Id.Value, SubscriptionDto.From, cursor, limit));
    }

    public static async Task<IResult> Bindings(
        IEventStore store, Guid streamId, string? cursor, int? limit, CancellationToken ct)
    {
        var (state, _) = await store.Load<ModelState>(StreamId.From(streamId), ct);
        return Results.Ok(Page(state.Bindings.Values, _ => "", b => b.Id.Value, BindingDto.From, cursor, limit));
    }

    /// <summary>The same paging as the lists that read rows: 50 items by default, 200 at most.</summary>
    private static Page<TDto> Page<T, TDto>(
        IEnumerable<T> items, Func<T, string> sortKey, Func<T, Guid> id, Func<T, TDto> toDto,
        string? cursor, int? limit)
    {
        var pageSize = Math.Clamp(limit ?? 50, 1, 200);

        if (CursorEncoder.Decode(cursor) is { } after)
        {
            items = items.Where(item =>
            {
                var order = string.CompareOrdinal(sortKey(item), after.SortKey);
                return order > 0 || (order == 0 && id(item).CompareTo(after.Id) > 0);
            });
        }

        var page = items
            .OrderBy(sortKey, StringComparer.Ordinal).ThenBy(id)
            .Take(pageSize + 1)
            .ToList();

        string? next = null;
        if (page.Count > pageSize)
        {
            page.RemoveAt(pageSize);
            next = CursorEncoder.Encode(sortKey(page[^1]), id(page[^1]));
        }

        return new Page<TDto>(page.Select(toDto).ToList(), next);
    }
}

/// <summary>A missing timeout declares none (0). A missing retry means one attempt.</summary>
public sealed record AddOperationRequest(
    Guid Id, Guid OwnerId, string Name, string Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int? TimeoutMs, RetryDto? Retry);

public static class AddOperation
{
    public static async Task<CommandResult<OperationDto>> HandleAsync(
        IServiceProvider services, Guid projectId, Guid streamId, AddOperationRequest request, CancellationToken ct)
    {
        if (request.Transport is null || !ModelEnum.TryParseDefined(request.Transport, out Transport transport))
            return new CommandResult<OperationDto>.Invalid([new ValidationError("transport", "Transport must be http, grpc or command")]);

        var add = new AddOperationCommand(
            request.Id, request.OwnerId, request.Name, transport,
            request.RequestSchemaId, request.ResponseSchemaId,
            request.TimeoutMs ?? 0, request.Retry?.ToDomain() ?? RetryPolicy.None);

        return await SchemaBoundWrite.DecideAsync(
            services, projectId, streamId,
            [("requestSchemaId", request.RequestSchemaId), ("responseSchemaId", request.ResponseSchemaId)],
            state => ContractDecider.AddOperation(state, add),
            state => OperationDto.From(state.Operations[request.Id]),
            ct);
    }
}

/// <summary>
/// A missing member leaves the value as it is. To clear a schema ID, send the set flag with
/// a null ID.
/// </summary>
public sealed record UpdateOperationRequest(
    string? Name, Guid? OwnerId, string? Transport,
    Guid? RequestSchemaId, Guid? ResponseSchemaId,
    int? TimeoutMs, RetryDto? Retry,
    bool SetRequestSchemaId = false, bool SetResponseSchemaId = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateOperation
{
    public static async Task<CommandResult<OperationDto>> HandleAsync(
        IServiceProvider services, Guid projectId, Guid streamId, Guid operationId, string userId, UpdateOperationRequest request, TimeProvider time, CancellationToken ct)
    {
        OperationId id = operationId;

        Transport? transport = null;
        if (request.Transport is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Transport, out Transport parsed))
                return new CommandResult<OperationDto>.Invalid([new ValidationError("transport", "Transport must be http, grpc or command")]);
            transport = parsed;
        }

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<OperationDto>.Invalid([statusError!]);

        var update = new UpdateOperationCommand(
            id, request.Name, request.OwnerId is { } ownerId ? new ElementId(ownerId) : null, transport,
            request.RequestSchemaId, request.SetRequestSchemaId || request.RequestSchemaId is not null,
            request.ResponseSchemaId, request.SetResponseSchemaId || request.ResponseSchemaId is not null,
            request.TimeoutMs, request.Retry?.ToDomain());

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await SchemaBoundWrite.DecideAsync(
            services, projectId, streamId,
            [("requestSchemaId", request.RequestSchemaId), ("responseSchemaId", request.ResponseSchemaId)],
            state => ContractDecider.UpdateOperation(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordOperationProvenance(
                        after, id,
                        requestedStatus ?? after.Operations[id].FactStatus,
                        request.SetProvenance ? supplied : after.Operations[id].Provenance)),
            state => OperationDto.From(state.Operations[id]),
            ct);
    }
}

public static class RemoveOperation
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid operationId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ContractDecider.RemoveOperation(state, operationId),
            ct);
}

public sealed record AddChannelRequest(Guid Id, string Name, string Kind);

public static class AddChannel
{
    public static async Task<CommandResult<ChannelDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddChannelRequest request, CancellationToken ct)
    {
        if (request.Kind is null || !ModelEnum.TryParseDefined(request.Kind, out ChannelKind kind))
            return new CommandResult<ChannelDto>.Invalid([new ValidationError("kind", "Kind must be topic, queue or exchange")]);

        var add = new AddChannelCommand(request.Id, request.Name, kind);

        return await store.ForProject(projectId).Decide<ModelState, ChannelDto>(
            streamId,
            state => ContractDecider.AddChannel(state, add),
            state => ChannelDto.From(state.Channels[request.Id]),
            ct);
    }
}

public sealed record UpdateChannelRequest(
    string? Name, string? Kind,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateChannel
{
    public static async Task<CommandResult<ChannelDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid channelId, string userId, UpdateChannelRequest request, TimeProvider time, CancellationToken ct)
    {
        ChannelId id = channelId;

        ChannelKind? kind = null;
        if (request.Kind is not null)
        {
            if (!ModelEnum.TryParseDefined(request.Kind, out ChannelKind parsed))
                return new CommandResult<ChannelDto>.Invalid([new ValidationError("kind", "Kind must be topic, queue or exchange")]);
            kind = parsed;
        }

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<ChannelDto>.Invalid([statusError!]);

        var update = new UpdateChannelCommand(id, request.Name, kind);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, ChannelDto>(
            streamId,
            state => ContractDecider.UpdateChannel(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordChannelProvenance(
                        after, id,
                        requestedStatus ?? after.Channels[id].FactStatus,
                        request.SetProvenance ? supplied : after.Channels[id].Provenance)),
            state => ChannelDto.From(state.Channels[id]),
            ct);
    }
}

public static class RemoveChannel
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid channelId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ContractDecider.RemoveChannel(state, channelId),
            ct);
}

/// <summary>
/// A missing retry means one attempt. Without a dead-letter channel, a message that fails
/// every attempt is dropped.
/// </summary>
public sealed record AddSubscriptionRequest(
    Guid Id, Guid ChannelId, Guid ConsumerId,
    RetryDto? Retry, Guid? DeadLetterChannelId);

public static class AddSubscription
{
    public static Task<CommandResult<SubscriptionDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddSubscriptionRequest request, CancellationToken ct)
    {
        var add = new AddSubscriptionCommand(
            request.Id, request.ChannelId, request.ConsumerId,
            request.Retry?.ToDomain() ?? RetryPolicy.None,
            request.DeadLetterChannelId is { } deadLetter ? new ChannelId(deadLetter) : null);

        return store.ForProject(projectId).Decide<ModelState, SubscriptionDto>(
            streamId,
            state => ContractDecider.AddSubscription(state, add),
            state => SubscriptionDto.From(state.Subscriptions[request.Id]),
            ct);
    }
}

/// <summary>
/// A missing member leaves the value as it is. To clear the dead-letter channel, send the
/// set flag with a null ID.
/// </summary>
public sealed record UpdateSubscriptionRequest(
    Guid? ChannelId, Guid? ConsumerId, RetryDto? Retry,
    Guid? DeadLetterChannelId, bool SetDeadLetterChannelId = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateSubscription
{
    public static async Task<CommandResult<SubscriptionDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid subscriptionId, string userId, UpdateSubscriptionRequest request, TimeProvider time, CancellationToken ct)
    {
        SubscriptionId id = subscriptionId;

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<SubscriptionDto>.Invalid([statusError!]);

        var update = new UpdateSubscriptionCommand(
            id,
            request.ChannelId is { } channelId ? new ChannelId(channelId) : null,
            request.ConsumerId is { } consumerId ? new ElementId(consumerId) : null,
            request.Retry?.ToDomain(),
            request.DeadLetterChannelId is { } deadLetter ? new ChannelId(deadLetter) : null,
            request.SetDeadLetterChannelId || request.DeadLetterChannelId is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, SubscriptionDto>(
            streamId,
            state => ContractDecider.UpdateSubscription(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordSubscriptionProvenance(
                        after, id,
                        requestedStatus ?? after.Subscriptions[id].FactStatus,
                        request.SetProvenance ? supplied : after.Subscriptions[id].Provenance)),
            state => SubscriptionDto.From(state.Subscriptions[id]),
            ct);
    }
}

public static class RemoveSubscription
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid subscriptionId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ContractDecider.RemoveSubscription(state, subscriptionId),
            ct);
}

/// <summary>
/// A binding without a routing key passes every message that reaches its source. An empty
/// key reads as none.
/// </summary>
public sealed record AddBindingRequest(Guid Id, Guid SourceChannelId, Guid TargetChannelId, string? RoutingKey);

public static class AddBinding
{
    public static Task<CommandResult<BindingDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, AddBindingRequest request, CancellationToken ct)
    {
        var add = new AddBindingCommand(request.Id, request.SourceChannelId, request.TargetChannelId, request.RoutingKey);

        return store.ForProject(projectId).Decide<ModelState, BindingDto>(
            streamId,
            state => ContractDecider.AddBinding(state, add),
            state => BindingDto.From(state.Bindings[request.Id]),
            ct);
    }
}

/// <summary>
/// A missing member leaves the value as it is. To clear the routing key, send the set flag
/// with a null key.
/// </summary>
public sealed record UpdateBindingRequest(
    Guid? SourceChannelId, Guid? TargetChannelId,
    string? RoutingKey, bool SetRoutingKey = false,
    string? FactStatus = null, ProvenanceInput? Provenance = null, bool SetProvenance = false);

public static class UpdateBinding
{
    public static async Task<CommandResult<BindingDto>> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid bindingId, string userId, UpdateBindingRequest request, TimeProvider time, CancellationToken ct)
    {
        BindingId id = bindingId;

        if (!ContractRequests.TryReadFactStatus(request.FactStatus, out var requestedStatus, out var statusError))
            return new CommandResult<BindingDto>.Invalid([statusError!]);

        var update = new UpdateBindingCommand(
            id,
            request.SourceChannelId is { } sourceId ? new ChannelId(sourceId) : null,
            request.TargetChannelId is { } targetId ? new ChannelId(targetId) : null,
            request.RoutingKey,
            request.SetRoutingKey || request.RoutingKey is not null);

        // The acceptance stamp is taken once, so the decision stays a pure function of the state.
        var supplied = request.SetProvenance
            ? ProvenanceMapper.ToDomain(request.Provenance, Guid.Parse(userId), time.GetUtcNow())
            : null;

        return await store.ForProject(projectId).Decide<ModelState, BindingDto>(
            streamId,
            state => ContractDecider.UpdateBinding(state, update).Then(state, after =>
                requestedStatus is null && !request.SetProvenance
                    ? new CommandResult<IReadOnlyList<object>>.Success([])
                    : ContractDecider.RecordBindingProvenance(
                        after, id,
                        requestedStatus ?? after.Bindings[id].FactStatus,
                        request.SetProvenance ? supplied : after.Bindings[id].Provenance)),
            state => BindingDto.From(state.Bindings[id]),
            ct);
    }
}

public static class RemoveBinding
{
    public static Task<CommandResult> HandleAsync(
        IEventStore store, Guid projectId, Guid streamId, Guid bindingId, CancellationToken ct) =>
        store.ForProject(projectId).Decide<ModelState>(
            streamId,
            state => ContractDecider.RemoveBinding(state, bindingId),
            ct);
}
