using BaGetter.Core.Configuration;

namespace BaGetter.Core;

public sealed class NugetAuthenticationOptions
{
    /// <summary>
    /// If set, the policy that will be used to authorize access to the Razor Pages.
    /// </summary>
    public string RazorPagesPolicy { get; set; }

    /// <summary>
    /// Username and password credentials for downloading packages.
    /// </summary>
    public NugetCredentials[] Credentials { get; set; }

    /// <summary>
    /// Api keys for pushing packages into the feed.
    /// </summary>
    public ApiKey[] ApiKeys { get; set; }
}
