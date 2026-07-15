using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Galapa.Core.Game;
using ToolboxSettings = Galapa.Toolbox.Services.Settings;
using CoreSettings = Galapa.Core.Configuration.Settings;

namespace Galapa.Toolbox.Views;

public partial class LauncherPage : UserControl
{
    private GameProcess? _quickLaunchProcess;
    private GameProcess? _customLaunchProcess;

    public LauncherPage()
    {
        this.InitializeComponent();
    }

    private void LaunchWithoutLogin_Click(object? sender, RoutedEventArgs e)
    {
        var startPaused = this.StartPausedCheckBox.IsChecked == true;
        // 56 hex chars placeholder - server will reject but game should boot
        this._quickLaunchProcess = this.LaunchGame(
            "00000000000000000000000000000000000000000000000000000000",
            1,
            startPaused,
            this.StatusText,
            this.ResumeButton);
    }

    private void Resume_Click(object? sender, RoutedEventArgs e)
    {
        this.ResumeProcess(this._quickLaunchProcess, this.StatusText, this.ResumeButton);
    }

    private void LaunchCustomSession_Click(object? sender, RoutedEventArgs e)
    {
        var sessionId = this.CustomSessionId.Text?.Trim() ?? "";
        var playerNumber = (int)(this.CustomPlayerNumber.Value ?? 1);
        var startPaused = this.CustomStartPausedCheckBox.IsChecked == true;

        if (sessionId.Length != 56)
        {
            this.CustomStatusText.Text = "Session ID must be exactly 56 hex characters.";
            return;
        }

        this._customLaunchProcess = this.LaunchGame(
            sessionId,
            playerNumber,
            startPaused,
            this.CustomStatusText,
            this.CustomResumeButton);
    }

    private void CustomResume_Click(object? sender, RoutedEventArgs e)
    {
        this.ResumeProcess(this._customLaunchProcess, this.CustomStatusText, this.CustomResumeButton);
    }

    private GameProcess? LaunchGame(string sessionId, int playerNumber, bool startPaused, TextBlock statusText,
        Button resumeButton)
    {
        var toolboxSettings = ToolboxSettings.Instance;

        var coreSettings = new CoreSettings
        {
            GameFolderPath = toolboxSettings.GameFolderPath
        };

        var gameProcess = new GameProcess(coreSettings)
        {
            SessionId = sessionId,
            PlayerNumber = playerNumber
        };

        try
        {
            if (startPaused)
            {
                gameProcess.StartSuspended();
                statusText.Text =
                    $"Game started paused (PID: {gameProcess.ProcessId}). Attach debugger, then click Resume.";
                statusText.Foreground = Brushes.Orange;
                resumeButton.IsVisible = true;
            }
            else
            {
                gameProcess.Start();
                statusText.Text = $"Game launched! (Player {playerNumber})";
                statusText.Foreground = Brushes.Green;
                resumeButton.IsVisible = false;
            }

            return gameProcess;
        }
        catch (Exception ex)
        {
            statusText.Text = $"Failed: {ex.Message}";
            statusText.Foreground = Brushes.Red;
            resumeButton.IsVisible = false;
            return null;
        }
    }

    private void ResumeProcess(GameProcess? process, TextBlock statusText, Button resumeButton)
    {
        if (process is null || !process.IsSuspended)
        {
            statusText.Text = "No suspended process to resume.";
            statusText.Foreground = Brushes.Red;
            resumeButton.IsVisible = false;
            return;
        }

        try
        {
            process.Resume();
            statusText.Text = $"Game resumed! (PID: {process.ProcessId})";
            statusText.Foreground = Brushes.Green;
            resumeButton.IsVisible = false;
        }
        catch (Exception ex)
        {
            statusText.Text = $"Failed to resume: {ex.Message}";
            statusText.Foreground = Brushes.Red;
        }
    }
}