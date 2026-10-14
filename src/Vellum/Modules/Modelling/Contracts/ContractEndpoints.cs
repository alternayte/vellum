using Vellum.Modules.Identity.AgentTokens;
using Deedbox;
using Vellum.Modules.Workspaces.Authorization;
using Vellum.Shared;

namespace Vellum.Modules.Modelling.Contracts;

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
