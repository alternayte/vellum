using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Deedbox;
using Microsoft.Extensions.DependencyInjection;
using Vellum.Shared;

namespace Vellum.Tests.Shared;

/// <summary>Every error answer is the shared envelope, including the ones no endpoint wrote.</summary>
public class ErrorEnvelopeTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private const string Secret = "connection string Password=hunter2";

    [Fact]
    public async Task A_refused_role_check_is_a_403_in_the_envelope()
    {
        using var client = await TestHost.SignInAsync(fixture.Factory);

        var response = await client.GetAsync("/api/instance/settings");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal(("forbidden", "Forbidden", "Requires the instance admin role"), (error.Type, error.Title, error.Detail));
    }

    [Fact]
    public async Task An_exception_that_no_handler_takes_is_a_500_in_the_envelope_that_tells_nothing_of_it()
    {
        // Diagnostics catches the failures of the event store it expects; this one it does not.
        using var factory = TestHost.Create(fixture.ConnectionString, configure: services =>
        {
            var original = services.Last(d => d.ServiceType == typeof(IEventStoreAdmin));
            services.Remove(original);
            services.Add(new ServiceDescriptor(typeof(IEventStoreAdmin), provider =>
            {
                var inner = (IEventStoreAdmin)(original.ImplementationInstance
                    ?? original.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!));
                var proxy = DispatchProxy.Create<IEventStoreAdmin, FailingStatus>();
                ((FailingStatus)(object)proxy).Inner = inner;
                return proxy;
            }, original.Lifetime));
        });
        using var admin = factory.CreateClient(new() { HandleCookies = true, AllowAutoRedirect = false });
        var login = await admin.PostAsJsonAsync("/api/auth/login", new { email = fixture.AdminEmail, password = TestHost.AdminPassword });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());

        var response = await admin.GetAsync("/api/instance/diagnostics");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!;
        Assert.Equal(("internal_error", "An error occurred while processing your request.", null), (error.Type, error.Title, error.Detail));
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
    }

    /// <summary>An event store whose status read fails with an exception nobody expects.</summary>
    public class FailingStatus : DispatchProxy
    {
        public IEventStoreAdmin Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IEventStoreAdmin.GetStatusAsync)
                ? throw new InvalidOperationException(Secret)
                : targetMethod.Invoke(Inner, args);
    }
}
