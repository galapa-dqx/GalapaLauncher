using Galapa.Launcher.Input;
using Galapa.Launcher.Models;

namespace Galapa.Launcher.Tests.Input;

public class NavigationContextTests
{
    [Fact]
    public void AttachChild_SetsParentAndChildren()
    {
        var root = new NavigationContext("Root");
        var child = new NavigationContext("Child");

        root.AttachChild(child);

        Assert.Same(root, child.Parent);
        Assert.Equal([child], root.Children);
    }

    [Fact]
    public void AttachChild_MovesChildBetweenParents()
    {
        var first = new NavigationContext("First");
        var second = new NavigationContext("Second");
        var child = new NavigationContext("Child");
        first.ActiveChild = child;

        second.AttachChild(child);

        Assert.Same(second, child.Parent);
        Assert.Empty(first.Children);
        Assert.Null(first.ActiveChild);
        Assert.Equal([child], second.Children);
    }

    [Fact]
    public void AttachChild_RejectsCycles()
    {
        var root = new NavigationContext("Root");
        var child = new NavigationContext("Child");
        var grandchild = new NavigationContext("Grandchild");
        root.AttachChild(child);
        child.AttachChild(grandchild);

        Assert.Throws<InvalidOperationException>(() => root.AttachChild(root));
        Assert.Throws<InvalidOperationException>(() => grandchild.AttachChild(root));
        Assert.Throws<InvalidOperationException>(() => grandchild.AttachChild(child));
    }

    [Fact]
    public void ActiveChild_AttachesUnattachedContexts()
    {
        var root = new NavigationContext("Root");
        var child = new NavigationContext("Child");

        root.ActiveChild = child;

        Assert.Same(root, child.Parent);
        Assert.Same(child, root.ActiveChild);
        Assert.Same(child, root.ActiveLeaf);
    }

    [Fact]
    public void ActiveLeaf_FollowsEnabledActiveChildren()
    {
        var root = new NavigationContext("Root");
        var app = new NavigationContext("App");
        var settings = new NavigationContext("Settings");
        root.ActiveChild = app;
        app.ActiveChild = settings;

        Assert.Same(settings, root.ActiveLeaf);

        settings.IsEnabled = false;
        Assert.Same(app, root.ActiveLeaf);

        app.IsEnabled = false;
        Assert.Same(root, root.ActiveLeaf);
    }

    [Fact]
    public void Activate_SetsTheActiveChainFromTheRoot()
    {
        var root = new NavigationContext("Root");
        var app = new NavigationContext("App");
        var onboarding = new NavigationContext("Onboarding");
        var settings = new NavigationContext("Settings");
        root.AttachChild(app);
        root.AttachChild(onboarding);
        app.AttachChild(settings);
        root.ActiveChild = onboarding;

        settings.Activate();

        Assert.Same(app, root.ActiveChild);
        Assert.Same(settings, app.ActiveChild);
        Assert.Same(settings, root.ActiveLeaf);
    }

    [Fact]
    public void IsReachable_RequiresEveryAncestorEnabled()
    {
        var root = new NavigationContext("Root");
        var app = new NavigationContext("App");
        var settings = new NavigationContext("Settings");
        root.AttachChild(app);
        app.AttachChild(settings);

        Assert.True(settings.IsReachable);

        app.IsEnabled = false;
        Assert.True(settings.IsEnabled);
        Assert.False(settings.IsReachable);
    }

    [Fact]
    public void Changed_BubblesToAncestors()
    {
        var root = new NavigationContext("Root");
        var app = new NavigationContext("App");
        var settings = new NavigationContext("Settings");
        root.AttachChild(app);
        app.AttachChild(settings);
        var rootChanges = 0;
        var appChanges = 0;
        root.Changed += (_, _) => rootChanges++;
        app.Changed += (_, _) => appChanges++;

        settings.IsEnabled = false;
        settings.IsEnabled = false; // no-op

        Assert.Equal(1, rootChanges);
        Assert.Equal(1, appChanges);
    }

    [Fact]
    public void DetachChild_ClearsActiveChild()
    {
        var root = new NavigationContext("Root");
        var child = new NavigationContext("Child");
        root.ActiveChild = child;

        Assert.True(root.DetachChild(child));
        Assert.False(root.DetachChild(child));

        Assert.Null(child.Parent);
        Assert.Null(root.ActiveChild);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Handle_RespectsEnabledStateAndHandler()
    {
        var handled = new List<ControllerAction>();
        var context = new NavigationContext("Ctx", (action, _) =>
        {
            handled.Add(action);
            return action == ControllerAction.BumperRight;
        });

        Assert.True(context.Handle(ControllerAction.BumperRight, false));
        Assert.False(context.Handle(ControllerAction.BumperLeft, false));

        context.IsEnabled = false;
        Assert.False(context.Handle(ControllerAction.BumperRight, false));

        Assert.Equal([ControllerAction.BumperRight, ControllerAction.BumperLeft], handled);
        Assert.False(new NavigationContext("NoHandler").Handle(ControllerAction.Confirm, false));
    }
}
