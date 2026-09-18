namespace SecureFlow.Api.Projects;

/// <summary>
/// A team grouping of projects, used by the Group Manager dashboard. Dev-only: there is no user
/// directory yet, so which groups a manager sees is chosen client-side rather than enforced here.
/// </summary>
public sealed class Group
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
}
