using CourseHub.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CourseHub.Components;

/// <summary>Base for pages that need the signed-in user. Call <see cref="EnsureUserAsync"/> before using it.</summary>
public abstract class AppPage : ComponentBase
{
    [CascadingParameter] protected Task<AuthenticationState>? AuthState { get; set; }
    [Inject] private UserService UserService { get; set; } = default!;
    [Inject] private CourseLibrary CourseLibrary { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>What this user can see; null means everything.</summary>
    protected AccessScope? Access { get; private set; }

    protected int UserId { get; private set; }
    protected bool IsAdmin { get; private set; }
    protected string UserName { get; private set; } = "";
    protected UserInfo? Me { get; set; }
    protected bool IsKid => Me?.IsKid == true;
    /// <summary>Signed in through a guest link: may only open one course.</summary>
    protected bool IsGuest => Me?.IsGuest == true;

    /// <summary>Opening apps (VS Code, Finder…) only makes sense when the server is this Mac.</summary>
    protected static bool CanOpenLocalApps => OperatingSystem.IsMacOS();

    protected async Task EnsureUserAsync()
    {
        if (UserId != 0 || AuthState is null) return;
        var user = (await AuthState).User;
        UserId = user.GetUserId() ?? 0;
        IsAdmin = user.IsAdmin();
        UserName = user.Identity?.Name ?? "";
        Me = await UserService.GetAsync(UserId);
        // A null scope means "everything"; only an unknown user gets nothing.
        Access = Me is null ? AccessScope.Nothing : Me.IsAdmin ? null : Me.Scope;
    }

    /// <summary>Pages other than the course itself send guests back to their course. Returns true when it redirected.</summary>
    protected bool SendGuestToCourse()
    {
        if (!IsGuest) return false;
        var course = Me?.GuestCourse is { } folder ? CourseLibrary.GetCourseByFolder(folder) : null;
        Navigation.NavigateTo(course is null ? "login" : $"course/{course.Id}");
        return true;
    }
}
