namespace Vellum.Modules.Modelling.Entities;

public class MessageEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid Branch { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid ProducerId { get; set; }
    public Guid[] ConsumerIds { get; set; } = [];
    public Guid? SchemaId { get; set; }
    public string[] Tags { get; set; } = [];
}
