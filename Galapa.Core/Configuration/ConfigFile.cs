using System.Text;
using System.Xml;
using System.Xml.Linq;
using Galapa.Core.Utils;

namespace Galapa.Core.Configuration;

public class InvalidConfigException(string fileContents) : Exception("Invalid config")
{
    public string FileContents { get; } = fileContents;
}

public abstract class ConfigFile
{
    private readonly Func<Stream, Stream> _obfuscatorFactory;
    public readonly string Filename;
    public XDocument? Document;

    protected ConfigFile(string filename, int seed, Func<Stream, Stream> obfuscatorFactory)
    {
        var rootDirectory = RootDirectory ?? throw new InvalidOperationException(
            $"{nameof(ConfigFile)}.{nameof(RootDirectory)} must be set to the DQX save folder before loading config files.");
        var obfuscatedName = FilenameObfuscator.Obfuscate(filename, seed);
        this.Filename = Path.Combine(rootDirectory, obfuscatedName);

        if (Path.GetDirectoryName(this.Filename) is { } dir) Directory.CreateDirectory(dir);

        this._obfuscatorFactory = obfuscatorFactory;
    }

    protected virtual string DefaultContents => string.Empty;

    /// <summary>
    ///     The DQX save folder that config files are read from and written to. There is deliberately no default: loading
    ///     from an unintended folder (such as the working directory) can make <see cref="Models.PlayerList" /> reconcile
    ///     against an empty player list, so every app must set this explicitly.
    /// </summary>
    public static string? RootDirectory { get; set; }

    protected virtual async Task _LoadAsync()
    {
        await this.EnsureCreated();

        await using var fileStream = new FileStream(this.Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var obfuscatorStream = this._obfuscatorFactory(fileStream);
        using var reader = new StreamReader(obfuscatorStream, new UTF8Encoding(false));

        this.Document = await Task.Run(() => XDocument.Load(reader));
    }

    public async Task SaveAsync()
    {
        await using var fileStream = new FileStream(this.Filename, FileMode.Truncate, FileAccess.Write, FileShare.Read);
        await using var obfuscatorStream = this._obfuscatorFactory(fileStream);
        await using var writer =
            new StreamWriter(obfuscatorStream, new UTF8Encoding(false));
        await writer.WriteAsync(await this.Serialize());
        await writer.FlushAsync();
    }

    private async Task<string> Serialize()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Async = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = new UTF8Encoding(false),
            NewLineOnAttributes = false
        };
        await using var ms = new MemoryStream();
        await using var writer = XmlWriter.Create(ms, settings);
        await Task.Run(() => this.Document?.Save(writer));
        await writer.FlushAsync();
        var stringResult = Encoding.UTF8.GetString(ms.ToArray());
        // Precisely match the exact format of the original file, DQXLauncher is very brittle!
        return stringResult.Replace(" />", "/>").Replace("utf-8", "UTF-8") + "\n";
    }

    private async Task EnsureCreated()
    {
        if (!File.Exists(this.Filename))
        {
            await using var fileStream =
                new FileStream(this.Filename, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var obfuscatorStream = this._obfuscatorFactory(fileStream);
            await using var writer = new StreamWriter(obfuscatorStream);

            await writer.WriteAsync(this.DefaultContents);
            await writer.FlushAsync();
        }
    }

    protected InvalidConfigException Invalid()
    {
        using var fileStream = new FileStream(this.Filename, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var obfuscatorStream = this._obfuscatorFactory(fileStream);
        var reader = new StreamReader(obfuscatorStream);
        return new InvalidConfigException(reader.ReadToEnd());
    }
}