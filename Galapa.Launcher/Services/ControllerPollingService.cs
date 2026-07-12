using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Galapa.Launcher.Models;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.DirectInput;

namespace Galapa.Launcher.Services;

/// <summary>
/// Service that polls DirectInput devices and fires button events on Controllers.
/// Runs at ~120Hz on a background thread to avoid blocking the UI.
/// </summary>
public class ControllerPollingService : IDisposable
{
    private const int PollIntervalMs = 8; // ~120Hz

    // DirectInput HRESULTs signalling the device is no longer readable (e.g. unplugged).
    // Expected during normal use, not an error worth a stack trace.
    private const int DIERR_INPUTLOST = unchecked((int)0x8007001E);
    private const int DIERR_NOTACQUIRED = unchecked((int)0x8007001F);

    private readonly ILogger<ControllerPollingService> _logger;
    private readonly ControllerListService _controllerListService;
    private readonly IDirectInput8 _directInput;
    private readonly Dictionary<string, IDirectInputDevice8> _devices = new();
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _started;
    private int _pollCount;

    public ControllerPollingService(
        ILogger<ControllerPollingService> logger,
        ControllerListService controllerListService)
    {
        this._logger = logger;
        this._controllerListService = controllerListService;
        this._directInput = DInput.DirectInput8Create();
    }

    public void Start()
    {
        if (this._started)
            return;

        this._logger.LogInformation("Starting controller polling service");
        this._started = true;

        this._controllerListService.ControllerConnected += this.OnControllerConnected;
        this._controllerListService.ControllerDisconnected += this.OnControllerDisconnected;
        this._controllerListService.Start();

        // Acquire existing controllers
        foreach (var controller in this._controllerListService.Controllers)
        {
            this.AcquireController(controller);
        }

        // Start background polling
        this._cts = new CancellationTokenSource();
        this._pollTask = Task.Run(() => this.PollLoop(this._cts.Token));
    }

    public void Stop()
    {
        if (!this._started)
            return;

        this._logger.LogInformation("Stopping controller polling service");
        this._started = false;

        // Stop the poll loop
        this._cts?.Cancel();
        try
        {
            this._pollTask?.Wait(1000);
        }
        catch (AggregateException)
        {
            // Expected on cancellation
        }

        this._cts?.Dispose();
        this._cts = null;
        this._pollTask = null;

        this._controllerListService.ControllerConnected -= this.OnControllerConnected;
        this._controllerListService.ControllerDisconnected -= this.OnControllerDisconnected;

        // Release all devices
        lock (this._lock)
        {
            foreach (var (id, device) in this._devices)
                try
                {
                    device.Unacquire();
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Error releasing device {Id}", id);
                }

            this._devices.Clear();
        }
    }

    private void OnControllerConnected(object? sender, ControllerEventArgs e)
    {
        this.AcquireController(e.Controller);
    }

    private void OnControllerDisconnected(object? sender, ControllerEventArgs e)
    {
        this.ReleaseController(e.Controller);
    }

    private void AcquireController(Controller controller)
    {
        lock (this._lock)
        {
            if (this._devices.ContainsKey(controller.Id))
            {
                this._logger.LogDebug("Controller {Name} already acquired", controller.Name);
                return;
            }

            try
            {
                this._logger.LogInformation("Acquiring DirectInput device for {Name} (GUID: {Guid})",
                    controller.Name, controller.DirectInputInstanceGuid);

                var device = this._directInput.CreateDevice(controller.DirectInputInstanceGuid);
                device.SetDataFormat<RawJoystickState>();
                device.SetCooperativeLevel(IntPtr.Zero, CooperativeLevel.NonExclusive | CooperativeLevel.Background);
                device.Acquire();

                this._devices[controller.Id] = device;
                this._logger.LogInformation("Successfully acquired DirectInput device for {Name}", controller.Name);
            }
            catch (Exception ex)
            {
                this._logger.LogError(ex, "Failed to acquire DirectInput device for controller {Name}",
                    controller.Name);
            }
        }
    }

    private void ReleaseController(Controller controller)
    {
        lock (this._lock)
        {
            if (!this._devices.TryGetValue(controller.Id, out var device))
                return;

            try
            {
                device.Unacquire();
                device.Dispose();
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Error releasing device for controller {Name}", controller.Name);
            }

            this._devices.Remove(controller.Id);

            // Clear controller state
            controller.LastState = ControllerState.Empty;
            controller.ButtonHeldSince = ImmutableDictionary<ControllerButton, DateTime>.Empty;
            controller.ButtonLastRepeat = ImmutableDictionary<ControllerButton, DateTime>.Empty;
        }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        this._logger.LogDebug("Poll loop started on thread {ThreadId}", Environment.CurrentManagedThreadId);

        while (!ct.IsCancellationRequested)
        {
            this._pollCount++;
            if (this._pollCount == 1 || this._pollCount % 500 == 0)
                this._logger.LogInformation("Poll tick #{Count}, controllers={ControllerCount}, devices={DeviceCount}",
                    this._pollCount, this._controllerListService.Controllers.Count, this._devices.Count);

            var now = DateTime.UtcNow;

            // Get a snapshot of controllers to avoid issues during iteration
            var controllers = this._controllerListService.Controllers;

            foreach (var controller in controllers)
            {
                IDirectInputDevice8? device;
                lock (this._lock)
                {
                    if (!this._devices.TryGetValue(controller.Id, out device))
                        continue;
                }

                try
                {
                    device.Poll();
                    var rawState = device.GetCurrentJoystickState();
                    var newState = this.CreateControllerState(rawState);

                    this.ProcessStateChange(controller, newState, now);
                    controller.LastState = newState;
                }
                catch (SharpGenException ex) when (
                    ex.ResultCode.Code is DIERR_INPUTLOST or DIERR_NOTACQUIRED)
                {
                    // Device access was lost, almost always because the controller was
                    // unplugged. SDL3 will also fire a disconnect, but releasing here on
                    // the first lost poll stops the 120Hz loop re-throwing every tick.
                    this._logger.LogDebug("Lost access to controller {Name} (likely unplugged); releasing",
                        controller.Name);
                    this.ReleaseController(controller);
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Failed to poll controller {Name}, releasing", controller.Name);
                    this.ReleaseController(controller);
                }
            }

            try
            {
                await Task.Delay(PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        this._logger.LogDebug("Poll loop ended");
    }

    private ControllerState CreateControllerState(JoystickState rawState)
    {
        // Convert button states to immutable array
        var buttons = ImmutableArray.CreateBuilder<bool>(128);
        for (var i = 0; i < 128; i++)
        {
            buttons.Add(rawState.Buttons[i]);
        }

        // Get POV hat (first one)
        var pov = rawState.PointOfViewControllers[0];

        // Get stick position (centered at 32767, convert to -32768 to 32767)
        var stickX = rawState.X - 32767;
        var stickY = rawState.Y - 32767;

        return new ControllerState(buttons.ToImmutable(), pov, stickX, stickY);
    }

    private void ProcessStateChange(Controller controller, ControllerState newState, DateTime now)
    {
        var oldActive = controller.LastState.GetActiveButtons();
        var newActive = newState.GetActiveButtons();

        // Find newly pressed buttons
        foreach (var button in newActive)
        {
            if (!oldActive.Contains(button))
            {
                // Button just pressed
                this._logger.LogInformation("Button pressed: {Button} on {Controller}", button, controller.Name);
                controller.ButtonHeldSince = controller.ButtonHeldSince.SetItem(button, now);
                controller.ButtonLastRepeat = controller.ButtonLastRepeat.SetItem(button, now);

                // Dispatch to UI thread
                Dispatcher.UIThread.Post(() => controller.RaiseButtonPressed(button));
            }
            else
            {
                // Button held - check for repeat
                if (controller.ButtonHeldSince.TryGetValue(button, out var heldSince) &&
                    controller.ButtonLastRepeat.TryGetValue(button, out var lastRepeat))
                {
                    var timeSincePress = (now - heldSince).TotalMilliseconds;
                    var timeSinceLastRepeat = (now - lastRepeat).TotalMilliseconds;

                    if (timeSincePress >= Controller.RepeatDelayMs &&
                        timeSinceLastRepeat >= Controller.RepeatIntervalMs)
                    {
                        controller.ButtonLastRepeat = controller.ButtonLastRepeat.SetItem(button, now);
                        Dispatcher.UIThread.Post(() => controller.RaiseButtonRepeat(button));
                    }
                }
            }
        }

        // Find released buttons
        foreach (var button in oldActive)
        {
            if (!newActive.Contains(button))
            {
                // Button just released
                controller.ButtonHeldSince = controller.ButtonHeldSince.Remove(button);
                controller.ButtonLastRepeat = controller.ButtonLastRepeat.Remove(button);
                Dispatcher.UIThread.Post(() => controller.RaiseButtonReleased(button));
            }
        }
    }

    public void Dispose()
    {
        this.Stop();
        this._directInput.Dispose();
    }
}