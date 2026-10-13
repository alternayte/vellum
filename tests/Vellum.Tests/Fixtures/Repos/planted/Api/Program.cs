using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Planted.Api;

public static class Program
{
    // Start with: api --password pl4nt-flag-cs -p pl4nt-flag-cs2
    private const string Arguments = "--password pl4nt-flag-cs3 --api-key pl4nt-flag-cs4";
    private const string Broker = "//app:pl4nt-url-cs@rabbitmq:5672";
    private const string Mirror = "app:pl4nt-url-cs2@mirror.acme.example";
    private const string Shibboleth = "pl4nt-short-cs";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql("Host=db;Username=app;Pin=4821-pl4nt-short"));
        builder.Services.AddMassTransit(bus =>
        {
            bus.AddConsumer<SessionConsumer>();
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(Broker);
                cfg.ReceiveEndpoint("planted.session-opened", endpoint => endpoint.ConfigureConsumer<SessionConsumer>(context));
            });
        });

        var app = builder.Build();
        app.MapGet("/sessions", () => Results.Ok());
        app.Run();
    }
}

/// <summary>A field that is NAMED like a credential is a name. Its default value is a value.</summary>
public sealed record SessionOpened(Guid SessionId, string Token = "pl4nt-schema-cs", string Password = "pl4nt-schema-cs2")
{
    public const string Example = "pl4nt-schema-cs3";
    public string ApiKey { get; init; } = "pl4nt-schema-cs4";
}

public sealed class SessionConsumer : IConsumer<SessionOpened>
{
    public Task Consume(ConsumeContext<SessionOpened> context) => Task.CompletedTask;
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Session> Sessions => Set<Session>();
}

public sealed class Session
{
    public Guid Id { get; set; }
}
