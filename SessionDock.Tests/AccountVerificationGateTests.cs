using SessionDock.Services;

namespace SessionDock.Tests;

public sealed class AccountVerificationGateTests
{
    [Fact]
    public void AutomaticVerification_RunsWhenNoSuppressionIsArmed()
    {
        var gate = new AccountVerificationGate();
        var token = new WebSessionToken(1, "account");

        Assert.True(gate.ShouldRunAutomaticVerification(token));
    }

    [Fact]
    public void MatchingSuppression_IsConsumedByOnlyTheFirstPageEvent()
    {
        var gate = new AccountVerificationGate();
        var firstToken = new WebSessionToken(1, "account");
        var laterToken = new WebSessionToken(2, "account");

        using var suppression = gate.SuppressNextAutomaticVerification("account");

        Assert.False(gate.ShouldRunAutomaticVerification(firstToken));
        Assert.True(gate.ShouldRunAutomaticVerification(firstToken));
        Assert.True(gate.ShouldRunAutomaticVerification(laterToken));
    }

    [Fact]
    public void DifferentAccount_DoesNotConsumeSuppression()
    {
        var gate = new AccountVerificationGate();
        var expectedToken = new WebSessionToken(2, "expected");
        var otherToken = new WebSessionToken(3, "other");
        using var suppression = gate.SuppressNextAutomaticVerification("expected");

        Assert.True(gate.ShouldRunAutomaticVerification(otherToken));
        Assert.False(gate.ShouldRunAutomaticVerification(expectedToken));
    }

    [Fact]
    public void DisposedUnusedSuppression_DoesNotHideLaterNavigation()
    {
        var gate = new AccountVerificationGate();
        var token = new WebSessionToken(1, "account");

        gate.SuppressNextAutomaticVerification("account").Dispose();

        Assert.True(gate.ShouldRunAutomaticVerification(token));
    }
}
