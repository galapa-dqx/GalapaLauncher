using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Galapa.Launcher.ViewModels.AppFrame;
using UserControl = Avalonia.Controls.UserControl;

namespace Galapa.Launcher.Views.AppFrame;

public partial class AppFrame : UserControl, IControllerInputHandler
{
    public AppFrame()
    {
        this.InitializeComponent();
    }

    public bool HandleControllerInput(ControllerAction action, bool isRepeat)
    {
        // Handle L1/R1 for tab switching
        if (action is not (ControllerAction.BumperLeft or ControllerAction.BumperRight))
            return false;

        if (this.DataContext is not AppFrameViewModel vm || vm.Pages.Count == 0)
            return false;

        if (vm.IsOnboarding)
            return false;

        var currentIndex = vm.SelectedPage != null ? vm.Pages.IndexOf(vm.SelectedPage) : 0;
        var newIndex = action switch
        {
            ControllerAction.BumperLeft => (currentIndex - 1 + vm.Pages.Count) % vm.Pages.Count,
            ControllerAction.BumperRight => (currentIndex + 1) % vm.Pages.Count,
            _ => currentIndex
        };

        if (newIndex != currentIndex)
        {
            vm.SelectedPage = vm.Pages[newIndex];
            return true;
        }

        return false;
    }
}
