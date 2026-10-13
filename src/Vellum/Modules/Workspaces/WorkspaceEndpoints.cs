using Vellum.Modules.Identity.AgentTokens;

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

        var members = group.MapGroup("/{workspaceId}/members").WithTags("Members");
        members.MapGet("/", ListMembers.Handle).Produces<MembersResponse>();
        members.MapPost("/", InviteMember.Handle).RequireHuman();
        members.MapPatch("/{memberUserId}", ChangeMemberRole.Handle).RequireHuman().Produces<MembershipDto>();
        members.MapDelete("/{memberUserId}", RemoveMember.Handle).RequireHuman();

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
