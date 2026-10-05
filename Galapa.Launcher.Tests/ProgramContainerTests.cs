using DryIoc;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels;
using Galapa.Launcher.Views;

namespace Galapa.Launcher.Tests;

public class ProgramContainerTests
{
    [Fact]
    public void Container_CanResolveTheMainWindowGraph()
    {
        using var container = Program.CreateServiceProvider();

        var errors = container.Validate(typeof(MainWindow), typeof(MainWindowViewModel), typeof(ControllerInputRouter));

        Assert.Empty(errors.Select(e => e.Value.Message));
    }
}
