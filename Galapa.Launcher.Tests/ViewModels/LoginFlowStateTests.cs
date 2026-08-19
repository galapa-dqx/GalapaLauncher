using Galapa.Core.Game.Authentication;
using Galapa.Core.Game;
using Galapa.Launcher.ViewModels;
using Galapa.Launcher.ViewModels.LoginFrame;
using Galapa.Launcher.Services;
using Galapa.Core.Models;
using Galapa.TestUtilities;
using Moq;

namespace Galapa.Launcher.Tests.ViewModels;

public class LoginFlowStateTests
{
    [Fact]
    public void SavePassword_PropertyChanged_Fires()
    {
        // Arrange
        var state = new LoginFlowState();
        using var tracker = new PropertyChangedTracker(state);

        // Act
        state.SavePassword = true;

        // Assert
        Assert.True(tracker.WasPropertyChanged(nameof(LoginFlowState.SavePassword)));
        Assert.Equal(1, tracker.GetPropertyChangedCount(nameof(LoginFlowState.SavePassword)));
    }

    [Fact]
    public void SaveUser_PropertyChanged_Fires()
    {
        // Arrange
        var state = new LoginFlowState();
        using var tracker = new PropertyChangedTracker(state);

        // Act
        state.SaveUser = true;

        // Assert
        Assert.True(tracker.WasPropertyChanged(nameof(LoginFlowState.SaveUser)));
        Assert.Equal(1, tracker.GetPropertyChangedCount(nameof(LoginFlowState.SaveUser)));
    }

    [Fact]
    public void Strategy_PropertyChanged_Fires()
    {
        // Arrange
        var state = new LoginFlowState();
        using var tracker = new PropertyChangedTracker(state);
        var mockStrategy = new Mock<LoginStrategy>();

        // Act
        state.Strategy = mockStrategy.Object;

        // Assert
        Assert.True(tracker.WasPropertyChanged(nameof(LoginFlowState.Strategy)));
        Assert.Equal(1, tracker.GetPropertyChangedCount(nameof(LoginFlowState.Strategy)));
    }

    [Fact]
    public void Strategy_CanBeSetAndRetrieved()
    {
        // Arrange
        var state = new LoginFlowState();
        var mockStrategy = new Mock<LoginStrategy>();

        // Act
        state.Strategy = mockStrategy.Object;

        // Assert
        Assert.Equal(mockStrategy.Object, state.Strategy);
    }

    [Fact]
    public void InitialState_IsCorrect()
    {
        // Arrange & Act
        var state = new LoginFlowState();

        // Assert
        Assert.False(state.SavePassword);
        Assert.False(state.SaveUser);
        Assert.Null(state.Strategy);
    }

    [Fact]
    public void SavePassword_MultipleChanges_FiresMultipleEvents()
    {
        // Arrange
        var state = new LoginFlowState();
        using var tracker = new PropertyChangedTracker(state);

        // Act
        state.SavePassword = true;
        state.SavePassword = false;
        state.SavePassword = true;

        // Assert
        Assert.Equal(3, tracker.GetPropertyChangedCount(nameof(LoginFlowState.SavePassword)));
    }

    [Fact]
    public void SaveUser_MultipleChanges_FiresMultipleEvents()
    {
        // Arrange
        var state = new LoginFlowState();
        using var tracker = new PropertyChangedTracker(state);

        // Act
        state.SaveUser = true;
        state.SaveUser = false;
        state.SaveUser = true;

        // Assert
        Assert.Equal(3, tracker.GetPropertyChangedCount(nameof(LoginFlowState.SaveUser)));
    }

    [Fact]
    public void Properties_AreIndependent()
    {
        // Arrange
        var state = new LoginFlowState();
        using var tracker = new PropertyChangedTracker(state);
        var mockStrategy = new Mock<LoginStrategy>();

        // Act
        state.SavePassword = true;
        state.SaveUser = true;
        state.Strategy = mockStrategy.Object;

        // Assert - each property should have fired exactly once
        Assert.Equal(1, tracker.GetPropertyChangedCount(nameof(LoginFlowState.SavePassword)));
        Assert.Equal(1, tracker.GetPropertyChangedCount(nameof(LoginFlowState.SaveUser)));
        Assert.Equal(1, tracker.GetPropertyChangedCount(nameof(LoginFlowState.Strategy)));
    }

    [Fact]
    public async Task GeneratedLoginCommandsAreNonNullAndInvokable()
    {
        var navigation = new LoginNavigationService();
        var flow = new LoginFlowState();
        var credentials = new Mock<IPlayerCredentialFactory>();
        var players = new PlayerSelectPageViewModel(navigation, flow, new PlayerList(credentials.Object));
        var strategy = new Mock<LoginStrategy>();
        strategy.Setup(value => value.Start()).ReturnsAsync(new AskUsernamePassword());
        var item = new TestPlayerItem(strategy.Object);

        Assert.NotNull(players.SelectPlayerCommand);
        await players.SelectPlayerCommand.ExecuteAsync(item);
        Assert.Same(strategy.Object, flow.Strategy);
        Assert.IsType<AskUsernamePassword>(navigation.Step);

        var frame = new LoginFrameViewModel(navigation,
            () => throw new InvalidOperationException(),
            () => players,
            () => throw new InvalidOperationException(),
            () => throw new InvalidOperationException());
        Assert.NotNull(frame.ReturnToPlayerSelectCommand);
        frame.ReturnToPlayerSelectCommand.Execute(null);
        Assert.Same(players, frame.Page);
    }

    [Fact]
    public void SavePassword_CanBeSetToFalse()
    {
        // Arrange
        var state = new LoginFlowState { SavePassword = true };

        // Act
        state.SavePassword = false;

        // Assert
        Assert.False(state.SavePassword);
    }

    [Fact]
    public void SaveUser_CanBeSetToFalse()
    {
        // Arrange
        var state = new LoginFlowState { SaveUser = true };

        // Act
        state.SaveUser = false;

        // Assert
        Assert.False(state.SaveUser);
    }

    [Fact]
    public void Strategy_CanBeSetToNull()
    {
        // Arrange
        var state = new LoginFlowState();
        var mockStrategy = new Mock<LoginStrategy>();
        state.Strategy = mockStrategy.Object;

        // Act
        state.Strategy = null;

        // Assert
        Assert.Null(state.Strategy);
    }

    private sealed class TestPlayerItem(LoginStrategy strategy) : PlayerListItem
    {
        public override LoginStrategy LoginStrategy => strategy;
        public override string Text => "Test";
    }
}
