using System;
using Galapa.Launcher.Models;

namespace Galapa.Launcher.Input;

/// <summary>
/// A source of semantic controller actions.
/// </summary>
public interface IControllerActionSource
{
    /// <summary>
    /// Raised when a mapped action is triggered (button pressed).
    /// </summary>
    event EventHandler<ControllerActionEventArgs>? ActionTriggered;

    /// <summary>
    /// Raised when a mapped action repeats (button held).
    /// </summary>
    event EventHandler<ControllerActionEventArgs>? ActionRepeated;
}
