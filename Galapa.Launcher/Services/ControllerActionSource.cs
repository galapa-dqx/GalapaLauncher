using System;
using System.Collections.Generic;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Services;

/// <summary>
/// Translates button events from controllers into semantic action events
/// using the configured button mappings.
/// </summary>
public class ControllerActionSource : IControllerActionSource, IDisposable
{
    private readonly ILogger<ControllerActionSource> _logger;
    private readonly ControllerListService _controllerListService;
    private readonly ControllerConfigService _configService;
    private readonly HashSet<Controller> _subscribedControllers = new();

    public ControllerActionSource(
        ILogger<ControllerActionSource> logger,
        ControllerListService controllerListService,
        ControllerConfigService configService)
    {
        this._logger = logger;
        this._controllerListService = controllerListService;
        this._configService = configService;
    }

    /// <summary>
    /// Raised when a mapped action is triggered (button pressed).
    /// </summary>
    public event EventHandler<ControllerActionEventArgs>? ActionTriggered;

    /// <summary>
    /// Raised when a mapped action repeats (button held).
    /// </summary>
    public event EventHandler<ControllerActionEventArgs>? ActionRepeated;

    /// <summary>
    /// Starts listening for controller events.
    /// </summary>
    public void Start()
    {
        this._controllerListService.ControllerConnected += this.OnControllerConnected;
        this._controllerListService.ControllerDisconnected += this.OnControllerDisconnected;

        // Subscribe to existing controllers
        foreach (var controller in this._controllerListService.Controllers)
        {
            this.SubscribeToController(controller);
        }

        this._logger.LogDebug("ControllerActionSource started");
    }

    /// <summary>
    /// Stops listening for controller events.
    /// </summary>
    public void Stop()
    {
        this._controllerListService.ControllerConnected -= this.OnControllerConnected;
        this._controllerListService.ControllerDisconnected -= this.OnControllerDisconnected;

        // Unsubscribe from all controllers
        foreach (var controller in this._subscribedControllers)
        {
            this.UnsubscribeFromController(controller);
        }

        this._subscribedControllers.Clear();

        this._logger.LogDebug("ControllerActionSource stopped");
    }

    private void OnControllerConnected(object? sender, ControllerEventArgs e)
    {
        this.SubscribeToController(e.Controller);
    }

    private void OnControllerDisconnected(object? sender, ControllerEventArgs e)
    {
        this.UnsubscribeFromController(e.Controller);
        this._subscribedControllers.Remove(e.Controller);
    }

    private void SubscribeToController(Controller controller)
    {
        if (!this._subscribedControllers.Add(controller))
        {
            this._logger.LogDebug("Already subscribed to {Name}", controller.Name);
            return;
        }

        controller.ButtonPressed += this.OnButtonPressed;
        controller.ButtonRepeat += this.OnButtonRepeat;
        this._logger.LogInformation("Subscribed to button events for {Name} (Id={Id})", controller.Name, controller.Id);
    }

    private void UnsubscribeFromController(Controller controller)
    {
        controller.ButtonPressed -= this.OnButtonPressed;
        controller.ButtonRepeat -= this.OnButtonRepeat;
        this._logger.LogDebug("Unsubscribed from button events for {Name}", controller.Name);
    }

    private void OnButtonPressed(object? sender, ControllerButtonEventArgs e)
    {
        var action = this.MapButtonToAction(e.Controller, e.Button);
        if (action.HasValue)
        {
            this._logger.LogInformation("Action triggered: {Action} from {Button}", action.Value, e.Button);
            this.ActionTriggered?.Invoke(this, new ControllerActionEventArgs
            {
                Controller = e.Controller,
                Action = action.Value,
                IsRepeat = false
            });
        }
        else
        {
            this._logger.LogDebug("No action mapped for button {Button}", e.Button);
        }
    }

    private void OnButtonRepeat(object? sender, ControllerButtonEventArgs e)
    {
        var action = this.MapButtonToAction(e.Controller, e.Button);
        if (action.HasValue)
        {
            this._logger.LogDebug("Action repeated: {Action} from {Button}", action.Value, e.Button);
            this.ActionRepeated?.Invoke(this, new ControllerActionEventArgs
            {
                Controller = e.Controller,
                Action = action.Value,
                IsRepeat = true
            });
        }
    }

    private ControllerAction? MapButtonToAction(Controller controller, ControllerButton button)
    {
        var config = this._configService.GetConfig(controller);
        return config.GetAction(button);
    }

    public void Dispose()
    {
        this.Stop();
    }
}