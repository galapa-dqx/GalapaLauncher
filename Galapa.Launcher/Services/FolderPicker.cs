using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Galapa.Launcher.Services;

/// <summary>
///     Asks the user to choose a folder. A view-model seam, so folder picking is testable without a storage provider.
/// </summary>
public interface IFolderPicker
{
    /// <summary>
    ///     Shows a folder picker.
    /// </summary>
    /// <param name="title">The picker's title.</param>
    /// <param name="startLocation">A folder to open the picker at, if it exists.</param>
    /// <returns>The chosen folder's local path, or null if the user cancelled or chose a non-local folder.</returns>
    Task<string?> PickFolderAsync(string title, string? startLocation, CancellationToken cancellationToken = default);
}

/// <summary>
///     <see cref="IFolderPicker" /> backed by the main window's Avalonia storage provider, resolved when a picker is
///     shown rather than at construction, since the window may not exist yet when this is created.
/// </summary>
public sealed class AvaloniaFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync(
        string title,
        string? startLocation,
        CancellationToken cancellationToken = default)
    {
        var storage = GetStorageProvider();

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(startLocation))
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(startLocation);

        cancellationToken.ThrowIfCancellationRequested();
        var folders = await storage.OpenFolderPickerAsync(options);
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static IStorageProvider GetStorageProvider()
    {
        var mainWindow = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return TopLevel.GetTopLevel(mainWindow)?.StorageProvider
               ?? throw new InvalidOperationException("A folder picker needs the main window to be open.");
    }
}
