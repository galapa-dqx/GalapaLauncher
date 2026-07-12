using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Galapa.Core.Game;
using ToolboxSettings = Galapa.Toolbox.Services.Settings;
using CoreSettings = Galapa.Core.Configuration.Settings;

namespace Galapa.Toolbox.Views;

public partial class LauncherPage : UserControl
{
    public LauncherPage()
    {
        this.InitializeComponent();
    }

    private void LaunchWithoutLogin_Click(object? sender, RoutedEventArgs e)
    {
        // 56 hex chars placeholder - server will reject but game should boot
        this.LaunchGame("00000000000000000000000000000000000000000000000000000000", 1, this.StatusText);
    }

    private void LaunchCustomSession_Click(object? sender, RoutedEventArgs e)
    {
        var sessionId = this.CustomSessionId.Text?.Trim() ?? "";
        var playerNumber = (int)(this.CustomPlayerNumber.Value ?? 1);

        if (sessionId.Length != 56)
        {
            this.CustomStatusText.Text = "Session ID must be exactly 56 hex characters.";
            return;
        }

        this.LaunchGame(sessionId, playerNumber, this.CustomStatusText);
    }

    private void LaunchGame(string sessionId, int playerNumber, TextBlock statusText)
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
            gameProcess.Start();
            statusText.Text = $"Game launched! (Player {playerNumber})";
            statusText.Foreground = Avalonia.Media.Brushes.Green;
        }
        catch (Exception ex)
        {
            statusText.Text = $"Failed: {ex.Message}";
            statusText.Foreground = Avalonia.Media.Brushes.Red;
        }
    }
}
