// src/Vellum/Modules/Docs/DocEndpoints.cs
namespace Vellum.Modules.Docs;

public static class DocEndpoints
{
    public static WebApplication MapDocEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/docs")
            .RequireAuthorization()
            .WithTags("Documents");

        group.MapPost("/", CreateDoc.Handle);
        group.MapGet("/", ListDocs.Handle);
        group.MapGet("/{docId}", GetDoc.Handle);
        group.MapPatch("/{docId}", UpdateDoc.Handle);
        group.MapDelete("/{docId}", DeleteDoc.Handle);

        return app;
    }
}
