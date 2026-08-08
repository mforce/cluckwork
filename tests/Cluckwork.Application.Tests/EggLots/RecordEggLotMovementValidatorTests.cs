namespace Cluckwork.Application.Tests.EggLots;

using Cluckwork.Application.Features.EggLots.RecordEggLotMovement;

public sealed class RecordEggLotMovementValidatorTests
{
    private readonly RecordEggLotMovementValidator _validator = new();

    private static RecordEggLotMovementCommand Cmd(
        string type = "Discard", int delta = -10, string reason = "cooler breakage") =>
        new(Guid.NewGuid(), type, delta, reason);

    [Theory]
    [InlineData("Discard", -10)]
    [InlineData("InternalUse", -3)]
    [InlineData("Reconciliation", -5)]
    [InlineData("Reconciliation", 5)] // recount may find eggs
    public async Task AllowedTypeAndSign_Passes(string type, int delta)
    {
        var result = await _validator.ValidateAsync(Cmd(type, delta));
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("Discard", 10)]      // a discard cannot add eggs
    [InlineData("InternalUse", 3)]
    public async Task PositiveDelta_ForWriteOffTypes_Fails(string type, int delta)
    {
        var result = await _validator.ValidateAsync(Cmd(type, delta));
        Assert.Contains(result.Errors, e =>
            e.ErrorCode == "EggLotMovement.QuantityDelta.NegativeForWriteOff");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Sale")]
    [InlineData("Adjustment")]
    [InlineData("Void")]
    [InlineData("Transfer")]
    [InlineData("bogus")]
    public async Task DisallowedType_Fails(string type)
    {
        var result = await _validator.ValidateAsync(Cmd(type));
        Assert.Contains(result.Errors, e =>
            e.ErrorCode == "EggLotMovement.MovementType.Allowed");
    }

    [Fact]
    public async Task ZeroDelta_Fails()
    {
        var result = await _validator.ValidateAsync(Cmd(delta: 0));
        Assert.Contains(result.Errors, e =>
            e.ErrorCode == "EggLotMovement.QuantityDelta.NonZero");
    }

    [Fact]
    public async Task EmptyLotId_Fails()
    {
        var result = await _validator.ValidateAsync(
            new RecordEggLotMovementCommand(Guid.Empty, "Discard", -1, "why"));
        Assert.Contains(result.Errors, e =>
            e.PropertyName == nameof(RecordEggLotMovementCommand.EggLotId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingReason_Fails(string reason)
    {
        var result = await _validator.ValidateAsync(Cmd(reason: reason));
        Assert.Contains(result.Errors, e =>
            e.ErrorCode == "EggLotMovement.Reason.Required");
    }

    [Fact]
    public async Task OverlongReason_Fails()
    {
        var result = await _validator.ValidateAsync(Cmd(reason: new string('x', 501)));
        Assert.Contains(result.Errors, e =>
            e.ErrorCode == "EggLotMovement.Reason.MaxLength");
    }
}
