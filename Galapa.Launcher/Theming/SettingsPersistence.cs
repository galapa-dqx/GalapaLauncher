using Galapa.Core.Configuration;

namespace Galapa.Launcher.Theming;

public interface ISettingsPersistence
{
    Task SaveAsync(Settings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsPersistence : ISettingsPersistence
{
    public Task SaveAsync(Settings settings, CancellationToken cancellationToken = default) =>
        settings.SaveAsync(cancellationToken);
}
