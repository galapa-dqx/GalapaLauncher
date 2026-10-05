using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;

namespace Galapa.Launcher.ViewModels.Editing;

/// <summary>
///     Live, validated editing of one <see cref="Settings" /> property, with no separate Save step.
/// </summary>
/// <remarks>
///     <para>
///         The UI edits <see cref="Draft" /> immediately. A draft is checked against the validation rules declared on the
///         <see cref="Settings" /> property; only a valid draft is committed into <see cref="Settings" />, and each commit
///         schedules a save through <see cref="ISettingsPersistence" />. Invalid drafts never reach the model.
///     </para>
///     <para>
///         Typed input and programmatic input (such as a folder picker) both go through <see cref="SubmitAsync" />;
///         typing only adds a debounce because validation may touch the file system. The newest submission always wins:
///         it cancels a pending debounce, and results from older submissions are discarded.
///     </para>
///     <para>
///         A failed save keeps the committed value live and reports the failure in <see cref="PersistenceError" />.
///         Not thread-safe: use from the UI thread.
///     </para>
/// </remarks>
public sealed partial class PreferenceField<T> : ObservableObject, IDisposable
{
    private readonly Func<Settings, T> _get;
    private readonly ISettingsPersistence _persistence;
    private readonly string _propertyName;
    private readonly Action<Settings, T> _set;
    private readonly Settings _settings;

    private bool _applyingToModel;
    private CancellationTokenSource? _debounceCancellation;
    private int _saveGeneration;
    private bool _settingDraft;
    private int _submitGeneration;

    /// <summary>
    ///     The value being edited. Setting it (for example through a two-way binding) submits it with the debounce.
    /// </summary>
    [ObservableProperty] private T _draft;

    /// <summary>
    ///     The value currently in <see cref="Settings" />, which is what the rest of the launcher sees.
    /// </summary>
    [ObservableProperty] private T _committed;

    /// <summary>
    ///     Why the draft is not committed, or null if it is valid.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _validationError;

    /// <summary>
    ///     Why the committed value is not on disk, or null if the last save succeeded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _persistenceError;

    /// <summary>
    ///     Whether a debounced draft is waiting to be validated.
    /// </summary>
    [ObservableProperty] private bool _isValidating;

    /// <summary>
    ///     Whether the latest commit is being saved.
    /// </summary>
    [ObservableProperty] private bool _isSaving;

    /// <param name="settings">The settings instance the field edits.</param>
    /// <param name="persistence">Saves <paramref name="settings" /> after each commit.</param>
    /// <param name="propertyName">The <see cref="Settings" /> property whose validation rules apply.</param>
    /// <param name="get">Reads the property.</param>
    /// <param name="set">Writes the property.</param>
    /// <param name="debounce">How long typed input waits before it is validated.</param>
    public PreferenceField(
        Settings settings,
        ISettingsPersistence persistence,
        string propertyName,
        Func<Settings, T> get,
        Action<Settings, T> set,
        TimeSpan debounce)
    {
        this._settings = settings;
        this._persistence = persistence;
        this._propertyName = propertyName;
        this._get = get;
        this._set = set;
        this.Debounce = debounce;

        this._committed = get(settings);
        this._draft = this._committed;
        this._validationError = this.GetModelError();
        this._settings.PropertyChanged += this.OnSettingsPropertyChanged;
    }

    /// <summary>
    ///     How long typed input waits before it is validated.
    /// </summary>
    public TimeSpan Debounce { get; set; }

    /// <summary>
    ///     The message to show on the row: a validation error first, otherwise a persistence error.
    /// </summary>
    public string? Error => this.ValidationError ?? this.PersistenceError;

    public bool HasError => this.Error is not null;

    public void Dispose()
    {
        this._settings.PropertyChanged -= this.OnSettingsPropertyChanged;
        this.CancelDebounce();
    }

    /// <summary>
    ///     Submits a value: sets the draft, validates it (after <see cref="Debounce" /> if <paramref name="debounce" />),
    ///     and if it is valid commits it into <see cref="Settings" /> and waits for the save. Both typed input and pickers
    ///     use this, so they share one validation and commit path.
    /// </summary>
    /// <returns>A task that completes when this submission is fully handled or superseded. It does not throw.</returns>
    public async Task SubmitAsync(T value, bool debounce = false)
    {
        var generation = ++this._submitGeneration;
        this.CancelDebounce();
        this.IsValidating = false;
        this.SetDraft(value);

        if (debounce && this.Debounce > TimeSpan.Zero)
        {
            var cancellation = this._debounceCancellation = new CancellationTokenSource();
            this.IsValidating = true;
            try
            {
                await Task.Delay(this.Debounce, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (generation == this._submitGeneration) this.IsValidating = false;
            }
        }

        if (generation != this._submitGeneration) return;

        this.ValidationError = this.Validate(value);
        if (this.ValidationError is not null) return;

        // Nothing new to commit, unless the last save failed and this is a retry.
        if (EqualityComparer<T>.Default.Equals(value, this.Committed) && this.PersistenceError is null) return;

        this.Commit(value);
        await this.SaveAsync();
    }

    private string? Validate(T value)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(this._settings) { MemberName = this._propertyName };
        return Validator.TryValidateProperty(value, context, results)
            ? null
            : results.FirstOrDefault()?.ErrorMessage ?? "Invalid value";
    }

    private void Commit(T value)
    {
        this.Committed = value;
        this._applyingToModel = true;
        try
        {
            this._set(this._settings, value);
        }
        finally
        {
            this._applyingToModel = false;
        }
    }

    private async Task SaveAsync()
    {
        var generation = ++this._saveGeneration;
        this.IsSaving = true;

        string? error;
        try
        {
            await this._persistence.SaveAsync(this._settings);
            error = null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }

        // A newer save owns the persistence state.
        if (generation != this._saveGeneration) return;
        this.PersistenceError = error;
        this.IsSaving = false;
    }

    partial void OnDraftChanged(T value)
    {
        if (!this._settingDraft) _ = this.SubmitAsync(value, true);
    }

    private void SetDraft(T value)
    {
        this._settingDraft = true;
        try
        {
            this.Draft = value;
        }
        finally
        {
            this._settingDraft = false;
        }
    }

    private void CancelDebounce()
    {
        this._debounceCancellation?.Cancel();
        this._debounceCancellation?.Dispose();
        this._debounceCancellation = null;
    }

    // Someone else changed the property: that value is now committed, and it replaces any edit in progress.
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (this._applyingToModel || e.PropertyName != this._propertyName) return;

        ++this._submitGeneration;
        this.CancelDebounce();
        this.IsValidating = false;

        var value = this._get(this._settings);
        this.Committed = value;
        this.SetDraft(value);
        this.ValidationError = this.GetModelError();
    }

    // The committed value can be invalid when it did not come through this field, for example a stale folder loaded
    // from disk; show that on the row so the user knows to fix it.
    private string? GetModelError() =>
        this._settings.GetErrors(this._propertyName).OfType<ValidationResult>().FirstOrDefault()?.ErrorMessage;
}
