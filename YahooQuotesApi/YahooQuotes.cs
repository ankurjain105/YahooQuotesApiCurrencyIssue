﻿using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace YahooQuotesApi;

public sealed class YahooQuotes
{
    private readonly ILogger Logger;
    private readonly IClock Clock;
    private readonly YahooSnapshot Snapshot;
    private readonly YahooHistory History;
    private readonly bool UseNonAdjustedClose;

    internal YahooQuotes(YahooQuotesBuilder builder)
    {
        Logger = builder.Logger;
        Clock = builder.Clock;
        IHttpClientFactory httpFactory = new HttpClientFactoryCreator(Logger).Create();
        Snapshot = new YahooSnapshot(Clock, Logger, httpFactory, builder.SnapshotCacheDuration);
        History = new YahooHistory(Clock, Logger, httpFactory, builder.HistoryStartDate, builder.HistoryCacheDuration, builder.PriceHistoryFrequency);
        UseNonAdjustedClose = builder.NonAdjustedClose;
    }

    public async Task<Security?> GetAsync(string symbol, HistoryFlags historyFlags = HistoryFlags.None, string historyBase = "", CancellationToken ct = default) =>
        (await GetAsync(new[] { symbol }, historyFlags, historyBase, ct).ConfigureAwait(false)).Values.Single();

    public async Task<Dictionary<string, Security?>> GetAsync(IEnumerable<string> symbols, HistoryFlags historyFlags = HistoryFlags.None, string historyBase = "", CancellationToken ct = default)
    {
        List<Symbol> syms = symbols
            .Select(s => s.ToSymbol())
            .Distinct()
            .ToList();

        Symbol? historyBaseSymbol = null;
        if (!string.IsNullOrEmpty(historyBase))
        {
            if (!Symbol.TryCreate(historyBase, out Symbol hbs))
                throw new ArgumentException($"Invalid base symbol: {historyBase}.");
            historyBaseSymbol = hbs;
        }
        Dictionary<Symbol, Security?> securities = await GetAsync(syms, historyFlags, historyBaseSymbol, ct).ConfigureAwait(false);
        return syms.ToDictionary(s => s.Name, s => securities[s], StringComparer.OrdinalIgnoreCase);
    }

    public async Task<Security?> GetAsync(Symbol symbol, HistoryFlags historyFlags = HistoryFlags.None, Symbol? historyBase = null, CancellationToken ct = default) =>
        (await GetAsync(new[] { symbol }, historyFlags, historyBase, ct).ConfigureAwait(false)).Values.Single();

    public async Task<Dictionary<Symbol, Security?>> GetAsync(IEnumerable<Symbol> symbols, HistoryFlags historyFlags = HistoryFlags.None, Symbol? historyBase = null, CancellationToken ct = default)
    {
        HashSet<Symbol> syms = symbols.ToHashSet();
        if (historyBase is not null)
        {
            if (historyBase.Value.IsCurrencyRate)
                throw new ArgumentException($"Invalid base symbol: {historyBase}.");
            if (syms.Any(s => s.IsCurrencyRate))
                throw new ArgumentException($"Invalid symbol: {syms.First(s => s.IsCurrencyRate)}.");
            if (!historyFlags.HasFlag(HistoryFlags.PriceHistory))
                throw new ArgumentException("PriceHistory must be enabled when historyBase is specified.");
        }
        if (historyBase is null && syms.Any(s => s.IsCurrency))
            throw new ArgumentException($"Invalid symbol: {syms.First(s => s.IsCurrency)}.");
        try
        {
            Dictionary<Symbol, Security?> securities = await GetSecuritiesAsync(syms, historyFlags, historyBase, ct).ConfigureAwait(false);
            return syms.ToDictionary(symbol => symbol, symbol => securities[symbol]);
        }
        catch (Exception e)
        {
            Logger.LogCritical(e, "YahooQuotes: GetAsync() error.");
            throw;
        }
    }

    private async Task<Dictionary<Symbol, Security?>> GetSecuritiesAsync(HashSet<Symbol> symbols, HistoryFlags historyFlags, Symbol? historyBase, CancellationToken ct)
    {
        HashSet<Symbol> stockAndCurrencyRateSymbols = symbols.Where(s => s.IsStock || s.IsCurrencyRate).ToHashSet();
        if (historyBase is not null && historyBase.Value.IsStock)
            stockAndCurrencyRateSymbols.Add(historyBase.Value);
        Dictionary<Symbol, Security?> securities = await Snapshot.GetAsync(stockAndCurrencyRateSymbols, ct).ConfigureAwait(false);

        if (historyFlags == HistoryFlags.None)
            return securities;

        if (historyBase is not null)
            await AddCurrencies(symbols, historyBase.Value, securities, ct).ConfigureAwait(false);

        await AddHistoryToSecurities(securities, historyFlags, ct).ConfigureAwait(false);

        if (historyBase is not null)
            HistoryBaseComposer.Compose(symbols, historyBase.Value, securities);

        return securities;
    }

    private async Task AddCurrencies(HashSet<Symbol> symbols, Symbol historyBase, Dictionary<Symbol, Security?> securities, CancellationToken ct)
    {
        // currency securities + historyBase currency + security currencies
        HashSet<Symbol> currencySymbols = symbols.Where(s => s.IsCurrency).ToHashSet();
        if (historyBase.IsCurrency)
            currencySymbols.Add(historyBase);
        foreach (Security security in securities.Values.NotNull())
        {
            if (!Symbol.TryCreate(security.Currency + "=X", out Symbol currencySymbol))
                security.PriceHistoryBase = Result<ValueTick[]>.Fail($"Invalid currency symbol: '{security.Currency}'.");
            else
                currencySymbols.Add(currencySymbol);
        }

        HashSet<Symbol> rateSymbols = currencySymbols
            .Where(c => c.Currency != "USD")
            .Select(c => $"USD{c.Currency}=X".ToSymbol())
            .ToHashSet();

        if (!rateSymbols.Any())
            return;

        Dictionary<Symbol, Security?> currencyRateSecurities = await Snapshot.GetAsync(rateSymbols, ct).ConfigureAwait(false);
        foreach (var security in currencyRateSecurities)
            securities[security.Key] = security.Value; // long symbol
    }

    private async Task AddHistoryToSecurities(Dictionary<Symbol, Security?> securities, HistoryFlags historyFlags, CancellationToken ct)
    {
        List<Security> secs = securities.Values.NotNull().ToList();

        List<Task> tasks = new();

        ParallelOptions parallelOptions = new()
        {
            //MaxDegreeOfParallelism = 16,
            CancellationToken = ct
        };

        if (historyFlags.HasFlag(HistoryFlags.PriceHistory))
        {
            tasks.Add(Parallel.ForEachAsync(secs, parallelOptions, async (sec, ct) =>
            {
                sec.PriceHistory = await History.GetTicksAsync<PriceTick>(sec.Symbol, ct).ConfigureAwait(false);
                sec.PriceHistoryBase = GetPriceHistoryBase(sec.PriceHistory, sec);
            }));
        }
        if (historyFlags.HasFlag(HistoryFlags.DividendHistory))
        {
            tasks.Add(Parallel.ForEachAsync(secs, parallelOptions, async (sec, ct) =>
                 sec.DividendHistory = await History.GetTicksAsync<DividendTick>(sec.Symbol, ct).ConfigureAwait(false)));
        }
        if (historyFlags.HasFlag(HistoryFlags.SplitHistory))
        {
            tasks.Add(Parallel.ForEachAsync(secs, parallelOptions, async (sec, ct) =>
                 sec.SplitHistory = await History.GetTicksAsync<SplitTick>(sec.Symbol, ct).ConfigureAwait(false)));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private Result<ValueTick[]> GetPriceHistoryBase(Result<PriceTick[]> result, Security security)
    {
        if (result.HasError)
            return Result<ValueTick[]>.Fail(result.Error);
        if (!result.Value.Any())
            return Result<ValueTick[]>.Fail("No history available.");
        if (security.ExchangeTimezone is null)
            return Result<ValueTick[]>.Fail("Exchange timezone not found.");
        if (security.ExchangeCloseTime == default)
            return Result<ValueTick[]>.Fail("ExchangeCloseTime not found.");

        List<ValueTick> ticks = result.Value.Select(priceTick => new ValueTick(
            priceTick.Date.At(security.ExchangeCloseTime).InZoneLeniently(security.ExchangeTimezone!).ToInstant(),
            UseNonAdjustedClose ? priceTick.Close : priceTick.AdjustedClose,
            priceTick.Volume
        )).ToList();

        if (!ticks.Any())
            return Result<ValueTick[]>.Fail("No history available."); // ????????

        return AddLatest(ticks, security);
    }

    private Result<ValueTick[]> AddLatest(List<ValueTick> ticks, Security security)
    {
        ZonedDateTime snapTime = security.RegularMarketTime;
        if (snapTime == default)
        {
            Logger.LogDebug("RegularMarketTime unavailable for symbol: {Symbol}.", security.Symbol);
            return Result<ValueTick[]>.Ok(ticks.ToArray());
        }

        decimal? snapPrice = security.RegularMarketPrice;
        if (snapPrice is null)
        {
            Logger.LogDebug("RegularMarketPrice unavailable for symbol: {Symbol}.", security.Symbol);
            return Result<ValueTick[]>.Ok(ticks.ToArray());
        }

        Instant now = Clock.GetCurrentInstant();
        Instant snapTimeInstant = snapTime.ToInstant();
        if (snapTimeInstant > now)
        {
            if ((snapTimeInstant - now) > Duration.FromSeconds(10))
                Logger.LogWarning("Snapshot date: {SnapTimeInstant} which follows current date: {Now} adjusted for symbol: {Symbol}.", snapTimeInstant, now, security.Symbol);
            snapTimeInstant = now;
        }

        ValueTick latestHistory = ticks.Last();
        if (latestHistory.Date >= snapTimeInstant)
        {   // if history already includes snapshot, or exchange closes early
            Logger.LogTrace("History tick with date: {Date} follows snapshot date: {SnapTimeInstant} removed for symbol: {Symbol}.", latestHistory.Date, snapTimeInstant, security.Symbol);
            ticks.Remove(latestHistory);
            if (!ticks.Any() || ticks.Last().Date >= snapTimeInstant)
                return Result<ValueTick[]>.Fail($"Invalid dates.");
        }

        long? volume = security.RegularMarketVolume;
        if (volume is null)
        {
            Logger.LogTrace("RegularMarketVolume unavailable for symbol: {Symbol}.", security.Symbol);
            volume = 0;
        }

        ticks.Add(new ValueTick(
            snapTimeInstant,
            Convert.ToDouble(snapPrice, CultureInfo.InvariantCulture),
            volume.Value
        )); // hist < snap < now

        return Result<ValueTick[]>.Ok(ticks.ToArray());
    }
}
