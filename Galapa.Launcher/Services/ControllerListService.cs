using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using Galapa.Launcher.Models;
using Microsoft.Extensions.Logging;
using SDL3;
using Vortice.DirectInput;

namespace Galapa.Launcher.Services;

/// <summary>
///     Service that lists controllers visible to both SDL3 and DirectInput,
///     correlating them and providing connect/disconnect events.
/// </summary>
public class ControllerListService : IDisposable
{
    private const int PollIntervalMs = 250; // 4Hz; only pumps SDL3 events, which is cheap

    // DirectInput has no hot-plug notifications and is expensive to enumerate (full
    // native HID re-init per device, on the UI thread), so we never poll it in steady
    // state. SDL3 is our hot-plug source; when it reports a change we re-enumerate
    // DirectInput. A just-added device can take a moment to surface in DirectInput, so
    // we reconcile over a short bounded window after each change, then go idle.
    private const int ReconcileRetryTicks = 4; // ~1s at 250ms
    private readonly IDirectInput8 _directInput;

    private readonly ILogger<ControllerListService> _logger;
    private readonly DispatcherTimer _pollTimer;

    // Raw device lists from each API
    private readonly Dictionary<uint, Sdl3Device> _sdl3Devices = new();
    private readonly HashSet<string> _warnedDirectInputOnly = new();

    // Track devices we've already warned about to avoid log spam
    private readonly HashSet<string> _warnedSdl3Only = new();

    // Correlated controllers (present in both APIs)
    private Dictionary<string, Controller> _correlatedControllers = new();
    private Dictionary<Guid, DirectInputDevice> _directInputDevices = new();

    // State
    private bool _sdl3Initialized;
    private int _reconcileTicksRemaining;

    public ControllerListService(ILogger<ControllerListService> logger)
    {
        this._logger = logger;
        this._directInput = DInput.DirectInput8Create();
        this._pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        this._pollTimer.Tick += this.OnPollTick;
    }

    /// <summary>
    ///     Gets the list of currently connected and correlated controllers.
    /// </summary>
    public IReadOnlyList<Controller> Controllers => this._correlatedControllers.Values.ToList();

    /// <summary>
    ///     Event raised when a controller is connected (present in both SDL3 and DirectInput).
    /// </summary>
    public event EventHandler<ControllerEventArgs>? ControllerConnected;

    /// <summary>
    ///     Event raised when a controller is disconnected (no longer in both APIs).
    /// </summary>
    public event EventHandler<ControllerEventArgs>? ControllerDisconnected;

    /// <summary>
    ///     Starts the controller list service, beginning enumeration and event detection.
    /// </summary>
    public void Start()
    {
        this._logger.LogInformation("Starting controller list service");

        if (!this._sdl3Initialized)
        {
            if (!SDL.Init(SDL.InitFlags.Joystick))
            {
                this._logger.LogError("Failed to initialize SDL3 joystick subsystem: {Error}", SDL.GetError());
                return;
            }

            this._sdl3Initialized = true;
        }

        // Do initial enumeration
        this.EnumerateSdl3Devices();
        this.EnumerateDirectInputDevices();
        this.CorrelateDevices();

        this._pollTimer.Start();
    }

    /// <summary>
    ///     Stops the controller list service.
    /// </summary>
    public void Stop()
    {
        this._logger.LogInformation("Stopping controller list service");
        this._pollTimer.Stop();
    }

    public void Dispose()
    {
        this.Stop();
        this._pollTimer.Tick -= this.OnPollTick;

        if (this._sdl3Initialized)
        {
            SDL.QuitSubSystem(SDL.InitFlags.Joystick);
            this._sdl3Initialized = false;
        }

        this._directInput.Dispose();
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        // SDL3 is our hot-plug source and must be pumped regularly; this is cheap.
        if (this.ProcessSdl3Events())
            // A joystick was added/removed. DirectInput has no change notification and
            // may not surface a just-added device immediately, so reconcile over a short
            // bounded window rather than polling forever.
            this._reconcileTicksRemaining = ReconcileRetryTicks;

        if (this._reconcileTicksRemaining == 0)
            return;

        this._reconcileTicksRemaining--;
        this.EnumerateDirectInputDevices();
        this.CorrelateDevices();
    }

    private bool ProcessSdl3Events()
    {
        var changed = false;

        while (SDL.PollEvent(out var ev))
            switch ((SDL.EventType)ev.Type)
            {
                case SDL.EventType.JoystickAdded:
                    var addedId = ev.JDevice.Which;
                    this._logger.LogDebug("SDL3: Joystick added (ID: {Id})", addedId);
                    var device = this.CreateSdl3Device(addedId);
                    if (device != null)
                    {
                        this._sdl3Devices[addedId] = device;
                        changed = true;
                    }

                    break;

                case SDL.EventType.JoystickRemoved:
                    var removedId = ev.JDevice.Which;
                    this._logger.LogDebug("SDL3: Joystick removed (ID: {Id})", removedId);
                    if (this._sdl3Devices.Remove(removedId))
                        changed = true;
                    break;
            }

        return changed;
    }

    private void EnumerateSdl3Devices()
    {
        this._sdl3Devices.Clear();
        var joystickIds = SDL.GetJoysticks(out var count);

        if (joystickIds == null || count == 0)
            return;

        for (var i = 0; i < count; i++)
        {
            var joystickId = joystickIds[i];
            var device = this.CreateSdl3Device(joystickId);
            if (device != null)
                this._sdl3Devices[joystickId] = device;
        }
    }

    private Sdl3Device? CreateSdl3Device(uint joystickId)
    {
        var name = SDL.GetJoystickNameForID(joystickId) ?? "Unknown";
        var vendorId = SDL.GetJoystickVendorForID(joystickId);
        var productId = SDL.GetJoystickProductForID(joystickId);
        var gamepadType = SDL.GetGamepadTypeForID(joystickId);

        if (vendorId == 0 || productId == 0)
        {
            this._logger.LogWarning("Could not determine VID/PID for SDL3 joystick '{Name}'", name);
            return null;
        }

        return new Sdl3Device(joystickId, name, vendorId, productId, gamepadType);
    }

    private void EnumerateDirectInputDevices()
    {
        var currentDevices = new Dictionary<Guid, DirectInputDevice>();

        var devices = this._directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
        foreach (var deviceInstance in devices)
        {
            var diDevice = this.CreateDirectInputDevice(deviceInstance);
            if (diDevice != null)
                currentDevices[deviceInstance.InstanceGuid] = diDevice;
        }

        this._directInputDevices = currentDevices;
    }

    private DirectInputDevice? CreateDirectInputDevice(DeviceInstance device)
    {
        // VID/PID are encoded in ProductGuid for HID devices
        // Format: VVVV PPPP-0000-0000-0000-504944564944
        // where VVVV is Vendor ID and PPPP is Product ID
        var guidBytes = device.ProductGuid.ToByteArray();
        var vendorId = BitConverter.ToUInt16(guidBytes, 0);
        var productId = BitConverter.ToUInt16(guidBytes, 2);

        if (vendorId == 0 || productId == 0)
        {
            this._logger.LogWarning("Could not determine VID/PID for DirectInput device '{Name}'", device.InstanceName);
            return null;
        }

        return new DirectInputDevice(
            device.InstanceGuid,
            device.ProductGuid,
            device.InstanceName,
            vendorId,
            productId
        );
    }

    private void CorrelateDevices()
    {
        // Group SDL3 devices by VID/PID
        var sdl3ByVidPid = this._sdl3Devices.Values
            .GroupBy(d => (d.VendorId, d.ProductId))
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.JoystickId).ToList());

        // Group DirectInput devices by VID/PID
        var diByVidPid = this._directInputDevices.Values
            .GroupBy(d => (d.VendorId, d.ProductId))
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.InstanceGuid).ToList());

        var newCorrelated = new Dictionary<string, Controller>();

        // Track current unmatched devices to clear warnings when they're resolved
        var currentSdl3Only = new HashSet<string>();
        var currentDirectInputOnly = new HashSet<string>();

        // Find matching VID/PID groups
        foreach (var (vidPid, sdl3List) in sdl3ByVidPid)
        {
            if (!diByVidPid.TryGetValue(vidPid, out var diList))
            {
                // SDL3-only devices - log warning once
                foreach (var sdl3 in sdl3List)
                {
                    var warnKey = $"{sdl3.VendorId:X4}:{sdl3.ProductId:X4}:{sdl3.Name}";
                    currentSdl3Only.Add(warnKey);
                    if (this._warnedSdl3Only.Add(warnKey))
                        this._logger.LogWarning(
                            "Controller '{Name}' (VID:{VID:X4} PID:{PID:X4}) visible only to SDL3",
                            sdl3.Name, sdl3.VendorId, sdl3.ProductId);
                }

                continue;
            }

            // Match by instance index (position in sorted list)
            var matchCount = Math.Min(sdl3List.Count, diList.Count);
            for (var i = 0; i < matchCount; i++)
            {
                var key = GenerateCorrelationKey(vidPid.VendorId, vidPid.ProductId, i);

                // Reuse existing Controller object to preserve event subscriptions
                if (this._correlatedControllers.TryGetValue(key, out var existing))
                {
                    newCorrelated[key] = existing;
                }
                else
                {
                    var controller = new Controller
                    {
                        Id = key,
                        Name = sdl3List[i].Name, // Prefer SDL3 name (better mappings)
                        VendorId = vidPid.VendorId,
                        ProductId = vidPid.ProductId,
                        InstanceIndex = i,
                        GamepadType = sdl3List[i].GamepadType,
                        Sdl3JoystickId = sdl3List[i].JoystickId,
                        DirectInputInstanceGuid = diList[i].InstanceGuid
                    };
                    newCorrelated[key] = controller;
                }
            }

            // Log unmatched SDL3 devices (once)
            for (var i = matchCount; i < sdl3List.Count; i++)
            {
                var warnKey = $"{sdl3List[i].VendorId:X4}:{sdl3List[i].ProductId:X4}:{i}";
                currentSdl3Only.Add(warnKey);
                if (this._warnedSdl3Only.Add(warnKey))
                    this._logger.LogWarning(
                        "Controller '{Name}' instance {Index} visible only to SDL3",
                        sdl3List[i].Name, i);
            }

            // Log unmatched DirectInput devices (once)
            for (var i = matchCount; i < diList.Count; i++)
            {
                var warnKey = $"{diList[i].VendorId:X4}:{diList[i].ProductId:X4}:{i}";
                currentDirectInputOnly.Add(warnKey);
                if (this._warnedDirectInputOnly.Add(warnKey))
                    this._logger.LogWarning(
                        "Controller '{Name}' instance {Index} visible only to DirectInput",
                        diList[i].InstanceName, i);
            }
        }

        // Log DirectInput-only VID/PIDs (once)
        foreach (var (vidPid, diList) in diByVidPid)
            if (!sdl3ByVidPid.ContainsKey(vidPid))
                foreach (var di in diList)
                {
                    var warnKey = $"{di.VendorId:X4}:{di.ProductId:X4}:{di.InstanceName}";
                    currentDirectInputOnly.Add(warnKey);
                    if (this._warnedDirectInputOnly.Add(warnKey))
                        this._logger.LogWarning(
                            "Controller '{Name}' (VID:{VID:X4} PID:{PID:X4}) visible only to DirectInput",
                            di.InstanceName, di.VendorId, di.ProductId);
                }

        // Clear warnings for devices that are no longer unmatched
        this._warnedSdl3Only.IntersectWith(currentSdl3Only);
        this._warnedDirectInputOnly.IntersectWith(currentDirectInputOnly);

        // Detect changes and fire events
        this.ProcessControllerChanges(newCorrelated);
        this._correlatedControllers = newCorrelated;
    }

    private void ProcessControllerChanges(Dictionary<string, Controller> newCorrelated)
    {
        // Detect disconnections (was correlated, no longer correlated)
        foreach (var (key, oldController) in this._correlatedControllers)
            if (!newCorrelated.ContainsKey(key))
            {
                this._logger.LogInformation("Controller disconnected: {Name} ({Type}) ({Id})",
                    oldController.Name, oldController.GamepadType, oldController.DirectInputInstanceGuid);
                this.ControllerDisconnected?.Invoke(this, new ControllerEventArgs
                {
                    Controller = oldController
                });
            }

        // Detect connections (now correlated, wasn't before)
        foreach (var (key, newController) in newCorrelated)
            if (!this._correlatedControllers.ContainsKey(key))
            {
                this._logger.LogInformation("Controller connected: {Name} ({Type}) ({Id})",
                    newController.Name, newController.GamepadType, newController.DirectInputInstanceGuid);
                this.ControllerConnected?.Invoke(this, new ControllerEventArgs
                {
                    Controller = newController
                });
            }
    }

    private static string GenerateCorrelationKey(ushort vendorId, ushort productId, int instanceIndex)
    {
        return $"{vendorId:X4}:{productId:X4}:{instanceIndex}";
    }

    // Internal record types for raw device data
    private sealed record Sdl3Device(
        uint JoystickId,
        string Name,
        ushort VendorId,
        ushort ProductId,
        SDL.GamepadType GamepadType
    );

    private sealed record DirectInputDevice(
        Guid InstanceGuid,
        Guid ProductGuid,
        string InstanceName,
        ushort VendorId,
        ushort ProductId
    );
}