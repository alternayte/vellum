using Vellum.Modules.Identity.AgentTokens;
using Vellum.Shared;

namespace Vellum.Modules.Workspaces;

public static class WorkspaceEndpoints
{
    public static WebApplication MapWorkspaceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workspaces")
            .RequireAuthorization()
            .WithTags("Workspaces");

        group.MapPost("/", CreateWorkspace.Handle);
        group.MapGet("/", ListWorkspaces.Handle);
        group.MapPatch("/{id}", UpdateWorkspace.Handle);
        group.MapDelete("/{id}", DeleteWorkspace.Handle);

        var members = group.MapGroup("/{workspaceId}/members").WithTags("Members");
        members.MapGet("/", ListMembers.Handle).Produces<MembersResponse>();
        members.MapPatch("/{memberUserId}", ChangeMemberRole.Handle).RequireHuman().Produces<MembershipDto>();
        members.MapDelete("/{memberUserId}", RemoveMember.Handle).RequireHuman();

        // A person joins a workspace only by accepting an invitation; nobody adds a member directly.
        var invitations = group.MapGroup("/{workspaceId}/invitations").WithTags("Members").RequireHuman();
        invitations.MapGet("/", ListInvitations.Handle).Produces<List<InvitationDto>>();
        invitations.MapPost("/", CreateInvitation.Handle).WithBodyLimit(RequestBodyLimit.Default)
            .Produces<IssuedInvitationDto>(StatusCodes.Status201Created)
            .Produces<IssuedInvitationDto>()
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ErrorResponse>(StatusCodes.Status429TooManyRequests)
            .AddEndpointFilter(CallerRate.Filter<InvitationRate>);
        invitations.MapDelete("/{invitationId:guid}", RevokeInvitation.Handle);

        // The invitations of the signed-in person, in any workspace.
        var mine = app.MapGroup("/api/invitations").WithTags("Members").RequireHuman();
        mine.MapGet("/", MyInvitations.List).Produces<List<MyInvitationDto>>();
        // The secret of the link is in the body of each of these, never in the URL.
        var answers = mine.MapGroup("/{invitationId:guid}")
            .WithBodyLimit(RequestBodyLimit.Default)
            .AddEndpointFilter(CallerRate.Filter<InvitationResponseRate>);
        answers.MapPost("/open", MyInvitations.Open).Produces<MyInvitationDto>();
        answers.MapPost("/accept", MyInvitations.Accept).Produces<MembershipDto>();
        answers.MapPost("/decline", MyInvitations.Decline);

        var projects = group.MapGroup("/{workspaceId}/projects").WithTags("Projects");
        projects.MapPost("/", CreateProject.Handle);
        projects.MapGet("/", ListProjects.Handle).AllowAgent(AgentScope.Read);

        var projectsGroup = app.MapGroup("/api/projects")
            .RequireAuthorization()
            .WithTags("Projects");
        projectsGroup.MapGet("/{projectId}", GetProject.Handle).Produces<ProjectDetailDto>();
        projectsGroup.MapPatch("/{projectId}", UpdateProject.Handle);
        projectsGroup.MapDelete("/{projectId}", DeleteProject.Handle);

        return app;
    }
}
