namespace Vellum.Modules.Drafts.Entities;

public class CommentEntity
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Guid? EntityId { get; set; }
    public string? EntityType { get; set; }
    public string Author { get; set; } = null!;
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
