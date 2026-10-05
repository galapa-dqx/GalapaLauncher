using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;
using Galapa.Launcher.Services;

namespace Galapa.Launcher.ViewModels.Editing;

/// <summary>
///     Edits the game's <see cref="InstallRoot" />: a typed path and a folder picker sharing one
///     <see cref="PreferenceField{T}" />, plus the text derived from the committed folder.
/// </summary>
public sealed partial class InstallFolderEditor : ObservableObject, IDisposable
{
    public static readonly TimeSpan TypingDebounce = TimeSpan.FromMilliseconds(400);

    private readonly IFolderPicker _folderPicker;
    private readonly Settings _settings;

    public InstallFolderEditor(Settings settings, ISettingsPersistence persistence, IFolderPicker folderPicker)
    {
        this._settings = settings;
        this._folderPicker = folderPicker;
        this.Field = new PreferenceField<string?>(
            settings,
            persistence,
            nameof(Settings.GameFolderPath),
            s => s.GameFolderPath,
            (s, value) => s.GameFolderPath = value,
            TypingDebounce);

        this._settings.PropertyChanged += this.OnSettingsPropertyChanged;
    }

    public PreferenceField<string?> Field { get; }

    /// <summary>
    ///     What the folder should contain, for help text.
    /// </summary>
    public string Help =>
        $"The folder Dragon Quest X is installed in, which contains the Boot and Game folders. " +
        $"The launcher starts {InstallRoot.RelativeExecutablePath} from here.";

    /// <summary>
    ///     The executable that will be launched, derived from the committed folder.
    /// </summary>
    public string? ExecutablePath => this._settings.InstallRoot?.ExecutablePath;

    public void Dispose()
    {
        this._settings.PropertyChanged -= this.OnSettingsPropertyChanged;
        this.Field.Dispose();
    }

    /// <summary>
    ///     Lets the user pick a folder and submits it immediately, through the same path as typed input.
    /// </summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await this._folderPicker.PickFolderAsync(
            "Select the Dragon Quest X folder",
            this.Field.Committed ?? this.Field.Draft);
        if (path is null) return;

        await this.Field.SubmitAsync(path);
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.InstallRoot)) this.OnPropertyChanged(nameof(this.ExecutablePath));
    }
}
