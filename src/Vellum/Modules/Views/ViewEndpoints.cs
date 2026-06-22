namespace Vellum.Modules.Views;

public static class ViewEndpoints
{
    public static WebApplication MapViewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/views")
            .RequireAuthorization()
            .WithTags("Views");

        group.MapPost("/", CreateView.Handle);
        group.MapGet("/", ListViews.Handle);
        group.MapGet("/{viewId}", GetView.Handle);
        group.MapPatch("/{viewId}", UpdateView.Handle);
        group.MapDelete("/{viewId}", DeleteView.Handle);
        group.MapPut("/{viewId}/layout", SaveLayout.Handle);

        return app;
    }
}
