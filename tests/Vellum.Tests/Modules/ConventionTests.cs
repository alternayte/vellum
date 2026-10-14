using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
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
            foreach (var key in entity.GetKeys())
                Assert.Matches(snakeCase, key.GetName(store)!);
            foreach (var foreignKey in entity.GetForeignKeys())
                Assert.Matches(snakeCase, foreignKey.GetConstraintName(store, foreignKey.PrincipalKey.DeclaringEntityType.GetTableName() is { } principal
                    ? StoreObjectIdentifier.Table(principal, foreignKey.PrincipalKey.DeclaringEntityType.GetSchema())
                    : store)!);
            foreach (var index in entity.GetIndexes())
                Assert.Matches(snakeCase, index.GetDatabaseName(store)!);
            foreach (var check in entity.GetCheckConstraints())
                Assert.Matches(snakeCase, check.GetName(store)!);
        }
    }

    [Fact]
    public void Every_enum_column_is_a_postgres_enum_with_snake_case_labels_in_a_module_schema()
    {
        using var db = new AppDbContextFactory().CreateDbContext([]);
        var model = db.GetService<IDesignTimeModel>().Model;
        var snakeCase = new Regex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$");

        var enums = model.GetPostgresEnums().ToList();
        Assert.NotEmpty(enums);
        foreach (var type in enums)
        {
            Assert.Matches(snakeCase, type.Name);
            Assert.NotNull(type.Schema);
            Assert.NotEqual("public", type.Schema);
            Assert.All(type.Labels, label => Assert.Matches(snakeCase, label));
        }

        // An enum property that EF would store as a number has no Postgres enum type.
        var mapped = enums.Select(type => $"{type.Schema}.{type.Name}").ToHashSet();
        foreach (var entity in model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (clr.IsArray)
                    clr = clr.GetElementType()!;
                if (!clr.IsEnum)
                    continue;
                var columnType = property.GetColumnType().TrimEnd('[', ']');
                Assert.True(mapped.Contains(columnType), $"{entity.DisplayName()}.{property.Name} is stored as {columnType}, not as a Postgres enum");
            }
        }
    }
}
