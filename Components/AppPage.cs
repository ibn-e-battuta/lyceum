using CourseHub.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CourseHub.Components;

/// <summary>Base for pages that need the signed-in user. Call <see cref="EnsureUserAsync"/> before using it.</summary>
public abstract class AppPage : ComponentBase
{
    [CascadingParameter] protected Task<AuthenticationState>? AuthState { get; set; }

    protected int UserId { get; private set; }
    protected bool IsAdmin { get; private set; }
    protected string UserName { get; private set; } = "";

    /// <summary>Opening apps (VS Code, Finder…) only makes sense when the server is this Mac.</summary>
    protected static bool CanOpenLocalApps => OperatingSystem.IsMacOS();

    protected async Task EnsureUserAsync()
    {
        if (UserId != 0 || AuthState is null) return;
        var user = (await AuthState).User;
        UserId = user.GetUserId() ?? 0;
        IsAdmin = user.IsAdmin();
        UserName = user.Identity?.Name ?? "";
    }
}
