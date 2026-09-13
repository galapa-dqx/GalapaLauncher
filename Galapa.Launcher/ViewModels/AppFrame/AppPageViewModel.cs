using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Launcher.Input;

namespace Galapa.Launcher.ViewModels.AppFrame;

public abstract class AppPageViewModel : ObservableObject
{
    /// <summary>
    ///     The navigation context this page contributes, if any. When the page is selected the
    ///     app shell activates it as a nested context so the page's handlers run first.
    /// </summary>
    public virtual NavigationContext? NavigationContext => null;
}
