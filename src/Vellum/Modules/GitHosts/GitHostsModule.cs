namespace Vellum.Modules.GitHosts;

/// <summary>
/// The git hosts other than GitHub: GitLab (cloud and self-managed), Bitbucket Cloud and Azure
/// DevOps Repos, each through a Refit REST adapter on the outbound pipeline. No git protocol
/// client: a Source reads a commit's file list and its files over the REST API.
/// </summary>
public static class GitHostsModule
{
    public static IServiceCollection AddGitHostsModule(this IServiceCollection services)
    {
        services.AddScoped<GitHostReader>();
        return services;
    }

    public static WebApplication MapGitHostEndpoints(this WebApplication app) => app;
}
