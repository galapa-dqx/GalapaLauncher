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
}
