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
            .Produces<Page<OperationDto>>();

        operations.MapPost("/", (
            Guid projectId, Guid? branchId, AddOperationRequest request, HttpContext http,
            ICommandHandler<AddOperationCommandEnvelope, CommandResult<OperationDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(new AddOperationCommandEnvelope(projectId, streamId, request), ct))
                    .ToCreatedResult($"/api/projects/{projectId}/operations/{request.Id}"), ct))
            .Produces<OperationDto>(StatusCodes.Status201Created);

        operations.MapPatch("/{operationId}", (
            Guid projectId, Guid operationId, Guid? branchId, UpdateOperationRequest request, HttpContext http,
            ICommandHandler<UpdateOperationCommandEnvelope, CommandResult<OperationDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await handler.HandleAsync(
                    new UpdateOperationCommandEnvelope(projectId, streamId, operationId, userId, request), ct)).ToHttpResult(), ct))
            .Produces<OperationDto>();

        operations.MapDelete("/{operationId}", (
            Guid projectId, Guid operationId, Guid? branchId, HttpContext http,
            ICommandHandler<RemoveOperationCommandEnvelope, CommandResult> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(
                    new RemoveOperationCommandEnvelope(projectId, streamId, operationId), ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        var channels = project.MapGroup("/channels").WithTags("Channels");

        channels.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Channels(store, streamId, cursor, limit, ct), ct))
            .Produces<Page<ChannelDto>>();

        channels.MapPost("/", (
            Guid projectId, Guid? branchId, AddChannelRequest request, HttpContext http,
            ICommandHandler<AddChannelCommandEnvelope, CommandResult<ChannelDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(new AddChannelCommandEnvelope(projectId, streamId, request), ct))
                    .ToCreatedResult($"/api/projects/{projectId}/channels/{request.Id}"), ct))
            .Produces<ChannelDto>(StatusCodes.Status201Created);

        channels.MapPatch("/{channelId}", (
            Guid projectId, Guid channelId, Guid? branchId, UpdateChannelRequest request, HttpContext http,
            ICommandHandler<UpdateChannelCommandEnvelope, CommandResult<ChannelDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await handler.HandleAsync(
                    new UpdateChannelCommandEnvelope(projectId, streamId, channelId, userId, request), ct)).ToHttpResult(), ct))
            .Produces<ChannelDto>();

        channels.MapDelete("/{channelId}", (
            Guid projectId, Guid channelId, Guid? branchId, HttpContext http,
            ICommandHandler<RemoveChannelCommandEnvelope, CommandResult> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(
                    new RemoveChannelCommandEnvelope(projectId, streamId, channelId), ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        var subscriptions = project.MapGroup("/subscriptions").WithTags("Subscriptions");

        subscriptions.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Subscriptions(store, streamId, cursor, limit, ct), ct))
            .Produces<Page<SubscriptionDto>>();

        subscriptions.MapPost("/", (
            Guid projectId, Guid? branchId, AddSubscriptionRequest request, HttpContext http,
            ICommandHandler<AddSubscriptionCommandEnvelope, CommandResult<SubscriptionDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(new AddSubscriptionCommandEnvelope(projectId, streamId, request), ct))
                    .ToCreatedResult($"/api/projects/{projectId}/subscriptions/{request.Id}"), ct))
            .Produces<SubscriptionDto>(StatusCodes.Status201Created);

        subscriptions.MapPatch("/{subscriptionId}", (
            Guid projectId, Guid subscriptionId, Guid? branchId, UpdateSubscriptionRequest request, HttpContext http,
            ICommandHandler<UpdateSubscriptionCommandEnvelope, CommandResult<SubscriptionDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await handler.HandleAsync(
                    new UpdateSubscriptionCommandEnvelope(projectId, streamId, subscriptionId, userId, request), ct)).ToHttpResult(), ct))
            .Produces<SubscriptionDto>();

        subscriptions.MapDelete("/{subscriptionId}", (
            Guid projectId, Guid subscriptionId, Guid? branchId, HttpContext http,
            ICommandHandler<RemoveSubscriptionCommandEnvelope, CommandResult> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(
                    new RemoveSubscriptionCommandEnvelope(projectId, streamId, subscriptionId), ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        var bindings = project.MapGroup("/bindings").WithTags("Bindings");

        bindings.MapGet("/", (
            Guid projectId, Guid? branchId, string? cursor, int? limit,
            HttpContext http, IEventStore store, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Viewer,
                (streamId, _) => ListContracts.Bindings(store, streamId, cursor, limit, ct), ct))
            .Produces<Page<BindingDto>>();

        bindings.MapPost("/", (
            Guid projectId, Guid? branchId, AddBindingRequest request, HttpContext http,
            ICommandHandler<AddBindingCommandEnvelope, CommandResult<BindingDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(new AddBindingCommandEnvelope(projectId, streamId, request), ct))
                    .ToCreatedResult($"/api/projects/{projectId}/bindings/{request.Id}"), ct))
            .Produces<BindingDto>(StatusCodes.Status201Created);

        bindings.MapPatch("/{bindingId}", (
            Guid projectId, Guid bindingId, Guid? branchId, UpdateBindingRequest request, HttpContext http,
            ICommandHandler<UpdateBindingCommandEnvelope, CommandResult<BindingDto>> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, userId) => (await handler.HandleAsync(
                    new UpdateBindingCommandEnvelope(projectId, streamId, bindingId, userId, request), ct)).ToHttpResult(), ct))
            .Produces<BindingDto>();

        bindings.MapDelete("/{bindingId}", (
            Guid projectId, Guid bindingId, Guid? branchId, HttpContext http,
            ICommandHandler<RemoveBindingCommandEnvelope, CommandResult> handler, CancellationToken ct) =>
            ModelBranches.RunAsync(http, projectId, branchId, WorkspaceRole.Editor,
                async (streamId, _) => (await handler.HandleAsync(
                    new RemoveBindingCommandEnvelope(projectId, streamId, bindingId), ct)).ToHttpResult(), ct))
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
