using Vellum.Modules.Settings;

namespace Vellum.Modules.GitHub;

/// <summary>
/// The GitHub token that the person who started a local instance gives in <c>GH_TOKEN</c>,
/// usually from <c>gh auth token</c>. It is read once at start-up and never stored. A hosted
/// instance ignores it: one token in the environment would let every tenant read what it can
/// read. Vellum reads <c>GH_TOKEN</c> only, never <c>GITHUB_TOKEN</c>, so the job token of a
/// CI runner is not taken by accident.
/// </summary>
public sealed class EnvironmentGitHubToken
{
    public const string VariableName = "GH_TOKEN";

    /// <summary>The only origin the token is sent to: it is a github.com token.</summary>
    public const string Origin = "https://api.github.com";

    public EnvironmentGitHubToken(IConfiguration configuration, Bootstrap bootstrap)
    {
        var value = configuration[VariableName]?.Trim();
        IsSet = !string.IsNullOrEmpty(value);
        IgnoredByHostedProfile = IsSet && bootstrap.Profile == OperatingProfile.Hosted;
        Token = IsSet && !IgnoredByHostedProfile ? value : null;
    }

    /// <summary>The token, when this is a local instance and <c>GH_TOKEN</c> is set.</summary>
    public string? Token { get; }

    public bool IsSet { get; }

    public bool IgnoredByHostedProfile { get; }

    /// <summary><c>in_use</c>, <c>ignored_hosted</c> or <c>not_set</c>.</summary>
    public string State => Token is not null ? "in_use" : IgnoredByHostedProfile ? "ignored_hosted" : "not_set";
}
