using System.ComponentModel;
using Cluckwork.Api.Middleware;
using Cluckwork.Application.Modules.Farm.Contracts;
using Cluckwork.Application.Modules.FlockManagement.Contracts;
using Cluckwork.Application.Modules.GeneralInventory.Contracts;
using Cluckwork.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cluckwork.Api.Mcp;

// #1313 — feed use, water use and inventory purchases, the day's work behind Record farm
// operations. Each runs its REST route's validator and handler through the contract, and
// takes effect at once, as the app's own buttons do: there is no draft. So each reply names
// the screen and control where an Owner or Manager corrects it. Notes and lot numbers may
// be sent, as the app takes them, and never come back (#792).
[McpServerToolType]
public sealed class InventoryTools(
    McpCallContext call,
    IdempotentUnit idempotency,
    IFlockLookup flocks,
    IInventoryModule inventory,
    IFarmModule farm,
    IValidator<RecordFeedUsageCommand> feedValidator,
    IValidator<RecordWaterUsageCommand> waterValidator,
    IValidator<RecordPurchaseCommand> purchaseValidator)
{
    public const string RecordFeedUsage = "cluckwork_record_feed_usage";
    public const string RecordWaterUsage = "cluckwork_record_water_usage";
    public const string RecordInventoryPurchase = "cluckwork_record_inventory_purchase";

    private const string CheckInApp = "Before retrying an older call, ask a person to check it in Cluckwork.";

    [McpServerTool(Name = RecordFeedUsage, OpenWorld = false)]
    [Description("Record feed, a supplement or an additive that a flock ate on one day, as Record feed does on the "
        + "Feed screen. The quantity is in the item's own unit. It takes effect at once: the item's oldest stock is "
        + "used first and the cost is estimated from it. Feed records are never edited; an Admin or Manager corrects "
        + "the stock in Cluckwork.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.OperationsWriteScope)]
    [MirrorsRoute("RecordFeedUsage")]
    public async Task<RecordedFeedUsage> RecordFeedUsageAsync(
        [Description(McpWrite.KeyDescription + CheckInApp)] string idempotencyKey,
        [Description("The flock's name, ignoring case, or its id when several flocks share the name.")] string flock,
        [Description("The inventory item's name, ignoring case, or its id.")] string item,
        [Description("The day it was eaten, YYYY-MM-DD.")] DateOnly date,
        [Description("How much, in the item's unit.")] decimal quantity,
        [Description("An optional note. It is stored and never returned.")] string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flock))
            throw new McpException("flock is required.");

        return await McpWrite.RunAsync(idempotency, call, RecordFeedUsage, idempotencyKey,
            new { flock, item, date, quantity, note },
            async () =>
            {
                var target = await FlockTools.ResolveAsync(flocks, flock, ct);
                var stock = await ResolveItemAsync(item, ct);
                var command = new RecordFeedUsageCommand(target.Id, stock.Id, date, quantity, note);
                if (await McpWrite.ValidateAsync(feedValidator, command, "FeedUsage.Invalid", ct) is { IsFailure: true } invalid)
                    return Result.Failure<RecordedFeedUsage>(invalid.Error);
                var used = await inventory.RecordFeedUsageAsync(command, call.AccountId, ct);
                return used.IsFailure
                    ? Result.Failure<RecordedFeedUsage>(used.Error)
                    : Result.Success(new RecordedFeedUsage(
                        used.Value.FeedUsageId, target.Id, target.Name, stock.Id, stock.Name, date, used.Value.QuantityUsed,
                        stock.Unit, McpMoney.From(used.Value.EstimatedCostMinorUnits, used.Value.CurrencyCode, used.Value.CurrencyMinorUnit),
                        "Feed records are never edited. An Admin or Manager corrects the stock with Correct stock on Inventory.",
                        Replayed: false));
            },
            async reply => await flocks.GetAsync(reply.FlockId, ct) is null
                ? throw FlockTools.NotFound(flock)
                : reply with { Replayed = true },
            ct);
    }

    [McpServerTool(Name = RecordWaterUsage, OpenWorld = false)]
    [Description("Record water a flock drank on one day, as Record water does on the Water screen: a quantity, or "
        + "the meter's start and end readings, or both when they agree. It takes effect at once; an Admin or Manager "
        + "corrects it in Cluckwork.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.OperationsWriteScope)]
    [MirrorsRoute("RecordWaterUsage")]
    public async Task<RecordedWaterUsage> RecordWaterUsageAsync(
        [Description(McpWrite.KeyDescription + CheckInApp)] string idempotencyKey,
        [Description("The flock's name, ignoring case, or its id when several flocks share the name.")] string flock,
        [Description("The day, YYYY-MM-DD.")] DateOnly date,
        [Description("Where the water came from: Well, Municipal, Tank or Other.")] string source,
        [Description("How much. Leave it out to take the meter readings' difference.")] decimal? quantity = null,
        [Description("L or gal. Leave it out for litres.")] string? unit = null,
        [Description("The meter's reading before, sent with meterEnd.")] decimal? meterStart = null,
        [Description("The meter's reading after, sent with meterStart.")] decimal? meterEnd = null,
        [Description("An optional note. It is stored and never returned.")] string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(flock))
            throw new McpException("flock is required.");

        return await McpWrite.RunAsync(idempotency, call, RecordWaterUsage, idempotencyKey,
            new { flock, date, source, quantity, unit, meterStart, meterEnd, note },
            async () =>
            {
                var target = await FlockTools.ResolveAsync(flocks, flock, ct);
                var command = new RecordWaterUsageCommand(target.Id, date, quantity, unit, source, meterStart, meterEnd, note);
                if (await McpWrite.ValidateAsync(waterValidator, command, "WaterUsage.Invalid", ct) is { IsFailure: true } invalid)
                    return Result.Failure<RecordedWaterUsage>(invalid.Error);
                var id = await inventory.RecordWaterUsageAsync(command, call.AccountId, ct);
                if (id.IsFailure)
                    return Result.Failure<RecordedWaterUsage>(id.Error);
                // Read back, so the reply carries the quantity and unit the handler stored.
                var stored = (await inventory.ListWaterUsageAsync(target.Id, date, date, 100, 0, ct)).Single(w => w.Id == id.Value);
                return Result.Success(new RecordedWaterUsage(
                    stored.Id, target.Id, target.Name, date, stored.Quantity, stored.Unit, stored.Source.ToString(),
                    "An Admin or Manager corrects it with correct on Water.", Replayed: false));
            },
            async reply => await flocks.GetAsync(reply.FlockId, ct) is null
                ? throw FlockTools.NotFound(flock)
                : reply with { Replayed = true },
            ct);
    }

    [McpServerTool(Name = RecordInventoryPurchase, OpenWorld = false)]
    [Description("Record stock received for an inventory item, as Record purchase does on the Inventory screen. It "
        + "takes effect at once as a new lot. Without a unit cost the item's default cost is used, and an item with "
        + "neither is refused. An Admin or Manager corrects or writes off the lot in Cluckwork.")]
    [Authorize(Policy = AuthPolicies.ProductionWrite)]
    [Authorize(Policy = AuthPolicies.OperationsWriteScope)]
    [MirrorsRoute("RecordInventoryPurchase")]
    public async Task<RecordedPurchase> RecordPurchaseAsync(
        [Description(McpWrite.KeyDescription + CheckInApp)] string idempotencyKey,
        [Description("The inventory item's name, ignoring case, or its id.")] string item,
        [Description("The day it arrived, YYYY-MM-DD.")] DateOnly receivedDate,
        [Description("How much, in the item's unit.")] decimal quantity,
        [Description("The cost of one unit, as a decimal in the farm's currency, such as 12.50.")] decimal? unitCost = null,
        [Description("The supplier's lot number, if any. It is stored and never returned.")] string? lotNumber = null,
        [Description("The expiry date, YYYY-MM-DD, if any.")] DateOnly? expiryDate = null,
        [Description("An optional note. It is stored and never returned.")] string? note = null,
        CancellationToken ct = default) =>
        await McpWrite.RunAsync(idempotency, call, RecordInventoryPurchase, idempotencyKey,
            new { item, receivedDate, quantity, unitCost, lotNumber, expiryDate, note },
            async () =>
            {
                var stock = await ResolveItemAsync(item, ct);
                var settings = unitCost is null ? null : await farm.GetSettingsAsync(ct)
                    ?? throw new McpException("This farm's currency could not be read.");
                long? unitCostMinorUnits = unitCost is { } cost
                    ? McpMoney.ToMinorUnits(cost, settings!.CurrencyMinorUnit, nameof(unitCost))
                    : null;
                var command = new RecordPurchaseCommand(stock.Id, receivedDate, quantity, unitCostMinorUnits, lotNumber, expiryDate, note);
                if (await McpWrite.ValidateAsync(purchaseValidator, command, "InventoryPurchase.Invalid", ct) is { IsFailure: true } invalid)
                    return Result.Failure<RecordedPurchase>(invalid.Error);
                var received = await inventory.RecordPurchaseAsync(command, call.AccountId, ct);
                if (received.IsFailure)
                    return Result.Failure<RecordedPurchase>(received.Error);
                var lot = received.Value;
                if (settings is not null && (lot.UnitCost.CurrencyCode != settings.CurrencyCode
                    || lot.UnitCost.CurrencyMinorUnit != settings.CurrencyMinorUnit))
                    return Result.Failure<RecordedPurchase>(McpMoney.CurrencyChanged(nameof(unitCost)));
                return Result.Success(new RecordedPurchase(
                    lot.Id, stock.Id, stock.Name, lot.ReceivedDate, lot.QuantityReceived, stock.Unit, McpMoney.From(lot.UnitCost),
                    "An Admin or Manager corrects or writes off the lot with Correct stock on Inventory.", Replayed: false));
            },
            // Purchases are not flock-scoped, so a replay has nothing more to re-check.
            reply => Task.FromResult(reply with { Replayed = true }),
            ct);

    private async Task<InventoryItemDetails> ResolveItemAsync(string item, CancellationToken ct) =>
        McpNames.Resolve(await inventory.ListItemsAsync(includeInactive: true, ct), item,
            i => i.Id, i => i.Name, i => i.Active, "inventory item", nameof(item));
}

public sealed record RecordedFeedUsage(
    Guid FeedUsageId, Guid FlockId, string FlockName, Guid ItemId, string Item, DateOnly Date, decimal Quantity,
    string Unit, McpMoney EstimatedCost, string CorrectInApp, bool Replayed);

public sealed record RecordedWaterUsage(
    Guid WaterUsageId, Guid FlockId, string FlockName, DateOnly Date, decimal Quantity, string Unit, string Source,
    string CorrectInApp, bool Replayed);

public sealed record RecordedPurchase(
    Guid LotId, Guid ItemId, string Item, DateOnly ReceivedDate, decimal Quantity, string Unit, McpMoney UnitCost,
    string CorrectInApp, bool Replayed);
