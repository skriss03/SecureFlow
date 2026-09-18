namespace SecureFlow.Api.Projects;

/// <summary>
/// The portfolio the dashboard ships with: real public repositories, scanned through the normal
/// from-repo pipeline. Order here is the portfolio order shown on the dashboard by default.
/// </summary>
public static class DemoCatalog
{
    /// <param name="Sample">Set instead of <paramref name="RepoUrl"/> to build from a built-in sample model.</param>
    public sealed record Entry(string Name, string Group, string? RepoUrl, string? Sample = null);

    public static readonly Entry[] Entries =
    {
        new("OpdFlow", "Platform Team", "https://github.com/skriss03/OpdFlow"),
        new("ShopFast (flawed e-commerce)", "Payments Team", null, "shopfast"),
        new("eShop (.NET reference)", "Payments Team", "https://github.com/dotnet/eShop"),
        new("Online Boutique", "Payments Team", "https://github.com/GoogleCloudPlatform/microservices-demo"),
        new("Sock Shop", "Payments Team", "https://github.com/microservices-demo/microservices-demo"),
        new("Spring PetClinic", "Growth Team", "https://github.com/spring-projects/spring-petclinic"),
        new("RealWorld", "Growth Team", "https://github.com/gothinkster/realworld"),
        new("Mastodon", "Growth Team", "https://github.com/mastodon/mastodon"),
        new("Immich", "Growth Team", "https://github.com/immich-app/immich"),
        new("Gitea", "Platform Team", "https://github.com/go-gitea/gitea"),
        new("Prometheus", "Platform Team", "https://github.com/prometheus/prometheus"),
        new("MinIO", "Platform Team", "https://github.com/minio/minio"),
    };
}
