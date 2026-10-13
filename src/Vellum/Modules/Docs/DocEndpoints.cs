// src/Vellum/Modules/Docs/DocEndpoints.cs
using Vellum.Shared;

namespace Vellum.Modules.Docs;

public static class DocEndpoints
{
    public static WebApplication MapDocEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectId}/docs")
            .RequireAuthorization()
            // A page of prose is larger than a command.
            .WithBodyLimit(RequestBodyLimit.Document)
            .WithTags("Documents");

        group.MapPost("/", CreateDoc.Handle);
        group.MapGet("/", ListDocs.Handle);
        group.MapGet("/{docId}", GetDoc.Handle);
        group.MapPatch("/{docId}", UpdateDoc.Handle);
        group.MapDelete("/{docId}", DeleteDoc.Handle);

        return app;
    }
}
