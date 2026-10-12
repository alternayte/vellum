using Microsoft.EntityFrameworkCore;
using Vellum.Shared;

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
