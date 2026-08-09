namespace Talon.Tests;

public sealed class TalonStartInfoTests
{
    [Fact]
    public void InjectorAndRuntimeWireModelsStayInSync()
    {
        var injectorProperties = typeof(Talon.Injector.TalonStartInfo)
            .GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(property => property.Name);
        var runtimeProperties = typeof(Talon.TalonStartInfo)
            .GetProperties()
            .Select(property => (property.Name, property.PropertyType))
            .OrderBy(property => property.Name);

        Assert.Equal(runtimeProperties, injectorProperties);
    }

    [Fact]
    public void InjectorAndRuntimeSerializeTheSameWireKeys()
    {
        var runtime = new Talon.TalonStartInfo
        {
            Version = 1,
            OverrideDirectory = "override",
            PacketCapturePath = "capture.pcapng",
            NetworkSmokeTest = true,
            VfsCensus = true,
        };
        var injector = new Talon.Injector.TalonStartInfo
        {
            Version = 1,
            OverrideDirectory = "override",
            PacketCapturePath = "capture.pcapng",
            NetworkSmokeTest = true,
            VfsCensus = true,
        };

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(runtime),
            System.Text.Json.JsonSerializer.Serialize(injector));
    }
}
