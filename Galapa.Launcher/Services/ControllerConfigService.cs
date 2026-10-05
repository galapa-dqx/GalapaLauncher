using System;
using System.Collections.Generic;
using System.IO;
using Galapa.Core.Configuration;
using Galapa.Core.Serialization;
using Galapa.Launcher.Models;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Services;

/// <summary>
/// Manages controller configurations, loading from PadConfig XML when available
/// or creating defaults based on controller type.
/// </summary>
public class ControllerConfigService
{
    private readonly ILogger<ControllerConfigService> _logger;
    private readonly Dictionary<Guid, ControllerConfig> _configs = new();

    public ControllerConfigService(ILogger<ControllerConfigService> logger)
    {
        this._logger = logger;
    }

    /// <summary>
    /// Gets or creates a config for the specified controller.
    /// </summary>
    public ControllerConfig GetConfig(Controller controller)
    {
        // Check cache first
        if (this._configs.TryGetValue(controller.DirectInputInstanceGuid, out var cached))
            return cached;

        // Try to load from game's PAD_CONFIG.xml
        var config = this.TryLoadFromPadConfig(controller);

        // Fall back to defaults if no config found
        config ??= ControllerConfig.CreateDefault(controller);

        this._configs[controller.DirectInputInstanceGuid] = config;
        return config;
    }

    /// <summary>
    /// Clears cached config for a controller (useful when config changes).
    /// </summary>
    public void ClearConfig(Controller controller)
    {
        this._configs.Remove(controller.DirectInputInstanceGuid);
    }

    /// <summary>
    /// Clears all cached configs.
    /// </summary>
    public void ClearAll()
    {
        this._configs.Clear();
    }

    private ControllerConfig? TryLoadFromPadConfig(Controller controller)
    {
        try
        {
            var padConfigPath = Path.Combine(SaveRoot.Location, "PAD_CONFIG.xml");

            if (!File.Exists(padConfigPath))
            {
                this._logger.LogDebug("No PAD_CONFIG.xml found at {Path}", padConfigPath);
                return null;
            }

            var padConfig = PadConfigXmlSerializer.Load(padConfigPath);

            // Check if this config matches the controller's GUID
            if (padConfig.PadInfo.DeviceGuid != controller.DirectInputInstanceGuid)
            {
                this._logger.LogDebug(
                    "PAD_CONFIG.xml GUID {ConfigGuid} doesn't match controller {ControllerGuid}",
                    padConfig.PadInfo.DeviceGuid,
                    controller.DirectInputInstanceGuid);
                return null;
            }

            this._logger.LogInformation(
                "Loaded controller config from PAD_CONFIG.xml for {Name}",
                controller.Name);

            return ControllerConfig.FromPadConfig(padConfig);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Failed to load PAD_CONFIG.xml");
            return null;
        }
    }
}
