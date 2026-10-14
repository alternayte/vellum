using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Vellum.Shared;

namespace Vellum.Tests.Modules;

public class ConventionTests
{
    [Fact]
    public void Every_table_and_column_has_a_snake_case_name_in_a_module_schema()
    {
        using var db = new AppDbContextFactory().CreateDbContext([]);
        var model = db.GetService<IDesignTimeModel>().Model;
        var snakeCase = new Regex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$");

        var entities = model.GetEntityTypes().ToList();
        Assert.NotEmpty(entities);
        foreach (var entity in entities)
        {
            var table = entity.GetTableName();
            Assert.NotNull(table);
            Assert.Matches(snakeCase, table);
            // Each module owns a schema of its own; nothing lands in public.
            Assert.Matches(snakeCase, entity.GetSchema() ?? "public");
            Assert.NotEqual("public", entity.GetSchema() ?? "public");

            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
                Assert.Matches(snakeCase, property.GetColumnName(store)!);
        }
    }

    [Fact]
    public void All_command_handlers_are_in_a_module()
    {
        var assembly = typeof(Program).Assembly;
        var handlerTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                && t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)))
            .ToList();

        Assert.NotEmpty(handlerTypes);
        foreach (var handler in handlerTypes)
        {
            Assert.Contains("Vellum.Modules", handler.Namespace!,
                StringComparison.Ordinal);
        }
    }
}
