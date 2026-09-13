using Galapa.Launcher.Input;
using Galapa.Launcher.Models;

namespace Galapa.Launcher.Tests.Input;

public class NavigationContextServiceTests
{
    private static NavigationContext Consuming(string name, ControllerAction action, List<string> log)
    {
        return new NavigationContext(name, (a, _) =>
        {
            log.Add($"{name}:{a}");
            return a == action;
        });
    }

    [Fact]
    public void ActiveContext_DefaultsToRoot()
    {
        var service = new NavigationContextService();

        Assert.Same(service.Root, service.ActiveContext);
        Assert.Null(service.FocusedContext);
        Assert.False(service.Dispatch(ControllerAction.Confirm, false));
    }

    [Fact]
    public void Dispatch_StartsAtTheActiveLeafAndBubblesToAncestors()
    {
        var log = new List<string>();
        var service = new NavigationContextService();
        var app = Consuming("App", ControllerAction.BumperRight, log);
        var settings = Consuming("Settings", ControllerAction.TriggerRight, log);
        service.Root.ActiveChild = app;
        app.ActiveChild = settings;

        Assert.Same(settings, service.ActiveContext);
        Assert.True(service.Dispatch(ControllerAction.TriggerRight, false));
        Assert.True(service.Dispatch(ControllerAction.BumperRight, false));
        Assert.False(service.Dispatch(ControllerAction.Confirm, false));

        Assert.Equal(
        [
            "Settings:TriggerRight",
            "Settings:BumperRight", "App:BumperRight",
            "Settings:Confirm", "App:Confirm"
        ], log);
    }

    [Fact]
    public void FocusedContext_OverridesTheLogicalPath()
    {
        var log = new List<string>();
        var service = new NavigationContextService();
        var app = Consuming("App", ControllerAction.BumperRight, log);
        var sidebar = Consuming("Sidebar", ControllerAction.BumperRight, log);
        var sidebarChild = Consuming("SidebarChild", ControllerAction.Confirm, log);
        service.Root.ActiveChild = app;
        service.Root.AttachChild(sidebar);
        sidebar.ActiveChild = sidebarChild;

        service.SetFocusedContext(sidebar);

        // The focused context's own active chain is followed, not the root's.
        Assert.Same(sidebarChild, service.ActiveContext);
        Assert.True(service.Dispatch(ControllerAction.BumperRight, false));
        Assert.Equal(["SidebarChild:BumperRight", "Sidebar:BumperRight"], log);

        service.SetFocusedContext(null);
        Assert.Same(app, service.ActiveContext);
    }

    [Fact]
    public void FocusedContext_FallsBackToNearestReachableAncestor()
    {
        var service = new NavigationContextService();
        var app = new NavigationContext("App");
        var settings = new NavigationContext("Settings");
        var onboarding = new NavigationContext("Onboarding");
        service.Root.AttachChild(app);
        app.ActiveChild = settings;
        service.Root.ActiveChild = onboarding;
        app.IsEnabled = false;

        service.SetFocusedContext(settings);

        Assert.Same(settings, service.FocusedContext);
        Assert.Same(onboarding, service.ActiveContext);

        app.IsEnabled = true;
        Assert.Same(settings, service.ActiveContext);
    }

    [Fact]
    public void FocusedContext_OutsideTheTreeIsIgnored()
    {
        var service = new NavigationContextService();
        var app = new NavigationContext("App");
        service.Root.ActiveChild = app;
        var detached = new NavigationContext("Detached");

        service.SetFocusedContext(detached);

        Assert.Same(app, service.ActiveContext);
    }

    [Fact]
    public void ActiveContextChanged_FiresForFocusAndTreeChanges()
    {
        var service = new NavigationContextService();
        var app = new NavigationContext("App");
        var settings = new NavigationContext("Settings");
        var changes = new List<(string Previous, string Current)>();
        service.ActiveContextChanged += (_, e) => changes.Add((e.Previous.Name, e.Current.Name));

        service.Root.ActiveChild = app; // Root -> App
        app.ActiveChild = settings; // App -> Settings
        service.SetFocusedContext(app); // no change: App's leaf is still Settings
        settings.IsEnabled = false; // Settings -> App
        service.SetFocusedContext(null); // no change
        app.IsEnabled = false; // App -> Root

        Assert.Equal(
        [
            ("Root", "App"),
            ("App", "Settings"),
            ("Settings", "App"),
            ("App", "Root")
        ], changes);
    }

    [Fact]
    public void Dispatch_NeverReachesADisabledSubtree()
    {
        var log = new List<string>();
        var service = new NavigationContextService();
        var app = Consuming("App", ControllerAction.BumperRight, log);
        var settings = Consuming("Settings", ControllerAction.BumperRight, log);
        service.Root.ActiveChild = app;
        app.ActiveChild = settings;
        app.IsEnabled = false;
        service.SetFocusedContext(settings);

        Assert.False(service.Dispatch(ControllerAction.BumperRight, false));
        Assert.Empty(log);
    }
}
