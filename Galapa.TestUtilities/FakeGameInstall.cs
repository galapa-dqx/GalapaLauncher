namespace Galapa.TestUtilities;

/// <summary>
///     A temporary folder laid out like a Dragon Quest X install root, with an empty <c>Game\DQXGame.exe</c>.
/// </summary>
public sealed class FakeGameInstall : IDisposable
{
    private readonly TempDirectory _directory = new();

    public FakeGameInstall()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(this.Path, "Game"));
        File.WriteAllText(System.IO.Path.Combine(this.Path, "Game", "DQXGame.exe"), "");
    }

    public string Path => this._directory.Path;

    public void Dispose() => this._directory.Dispose();
}
