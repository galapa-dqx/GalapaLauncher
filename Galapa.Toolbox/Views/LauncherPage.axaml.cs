using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    private async void LaunchWithoutLogin_Click(object? sender, RoutedEventArgs e)
    {
        // 56 hex chars placeholder - server will reject but game should boot
        const string placeholderSessionId = "00000000000000000000000000000000000000000000000000000000";

        if (this.InjectTalonCheckBox.IsChecked == true)
        {
            await this.LaunchWithTalonAsync(placeholderSessionId, 1, this.StatusText, this.ResumeButton);
            return;
        }

        var startPaused = this.StartPausedCheckBox.IsChecked == true;
        this._quickLaunchProcess = this.LaunchGame(
            placeholderSessionId,
            1,
            startPaused,
            this.StatusText,
            this.ResumeButton);
    }

    private void Resume_Click(object? sender, RoutedEventArgs e)
    {
        this.ResumeProcess(this._quickLaunchProcess, this.StatusText, this.ResumeButton);
    }

    private async void LaunchCustomSession_Click(object? sender, RoutedEventArgs e)
    {
        var sessionId = this.CustomSessionId.Text?.Trim() ?? "";
        var playerNumber = (int)(this.CustomPlayerNumber.Value ?? 1);

        if (sessionId.Length != 56)
        {
            this.CustomStatusText.Text = "Session ID must be exactly 56 hex characters.";
            this.CustomStatusText.Foreground = Brushes.Red;
            return;
        }

        if (this.CustomInjectTalonCheckBox.IsChecked == true)
        {
            await this.LaunchWithTalonAsync(sessionId, playerNumber, this.CustomStatusText, this.CustomResumeButton);
            return;
        }

        var startPaused = this.CustomStartPausedCheckBox.IsChecked == true;
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

    /// <summary>
    ///     Launches the game via the bundled x86 <c>Talon.Injector.exe</c>, which starts DQX
    ///     suspended, injects <c>Talon.Boot.dll</c> via an early-bird APC, and resumes it. The
    ///     Toolbox runs as x64 and cannot inject in-process, so it spawns the injector as a child.
    /// </summary>
    private async Task LaunchWithTalonAsync(string sessionId, int playerNumber, TextBlock statusText,
        Button resumeButton)
    {
        // Talon resumes the game itself — there is no manual Resume step in this path.
        resumeButton.IsVisible = false;

        try
        {
            var coreSettings = new CoreSettings
            {
                GameFolderPath = ToolboxSettings.Instance.GameFolderPath
            };

            var gameProcess = new GameProcess(coreSettings)
            {
                SessionId = sessionId,
                PlayerNumber = playerNumber
            };

            var commandLine = gameProcess.BuildCommandLine();
            var workingDir = gameProcess.WorkingDirectory;

            var injectorPath = Path.Combine(AppContext.BaseDirectory, "Talon", "Talon.Injector.exe");
            if (!File.Exists(injectorPath))
            {
                statusText.Text =
                    $"Talon.Injector.exe not found at '{injectorPath}'. Build the solution with " +
                    "Visual Studio / MSBuild (not 'dotnet build') so Talon and its native boot DLL are bundled.";
                statusText.Foreground = Brushes.Red;
                return;
            }

            statusText.Text = "Launching via Talon injector…";
            statusText.Foreground = Brushes.Orange;

            var (exitCode, output) = await Task.Run(() => RunInjector(injectorPath, workingDir, commandLine));

            if (exitCode == 0)
            {
                statusText.Text = $"Launched with Talon injected! (Player {playerNumber})";
                statusText.Foreground = Brushes.Green;
            }
            else
            {
                statusText.Text = $"Talon injection failed (exit {exitCode}). {output}";
                statusText.Foreground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            statusText.Text = $"Failed: {ex.Message}";
            statusText.Foreground = Brushes.Red;
        }
    }

    private static (int ExitCode, string Output) RunInjector(string injectorPath, string workingDir,
        string gameCommandLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = injectorPath,
            // Raw argument string on purpose: the game command line must reach the injector
            // VERBATIM after the "--" marker (Talon's raw-tail CLI contract). Building it by hand
            // preserves DQX's -StartupToken quoting; ArgumentList would re-quote and corrupt it.
            Arguments = $"--working-dir \"{workingDir}\" -- {gameCommandLine}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(injectorPath)!
        };

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start Talon.Injector.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);

        var output = string.Join(" ", (stdout + " " + stderr)
            .Split('\r', '\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

        return (proc.HasExited ? proc.ExitCode : -1, output);
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
