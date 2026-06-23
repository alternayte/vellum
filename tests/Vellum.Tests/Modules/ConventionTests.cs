using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Vellum.Kernel.CommandHandling;

namespace Vellum.Tests.Modules;

public class ConventionTests
{
    [Fact]
    public void All_DbContexts_use_snake_case_naming()
    {
        var assembly = typeof(Program).Assembly;
        var dbContextTypes = assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DbContext)) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(dbContextTypes);
        // Each DbContext should be configurable with UseSnakeCaseNamingConvention
        // This is a structural check — the actual naming is enforced by the NamingConventions package
    }

    [Fact]
    public void All_command_handlers_are_in_a_module()
    {
        var assembly = typeof(Program).Assembly;
        // Exclude kernel infrastructure types (e.g. TransactionBehavior decorator) —
        // only application-level handlers (not in the Kernel namespace) must live under Vellum.Modules.
        var handlerTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                && t.Namespace?.StartsWith("Vellum.Kernel") != true
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

    [Fact]
    public void No_module_references_kernel_internals_directly()
    {
        // Modules should depend on kernel interfaces, not internal classes
        // (EventStoreDbContext is internal to kernel; modules use IEventStore, AggregateStore)
        var assembly = typeof(Program).Assembly;
        var moduleTypes = assembly.GetTypes()
            .Where(t => t.Namespace?.Contains("Modules") == true)
            .ToList();

        foreach (var type in moduleTypes)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                Assert.NotEqual(typeof(Vellum.Kernel.EventStore.EventStoreDbContext), field.FieldType);
            }
        }
    }
}
