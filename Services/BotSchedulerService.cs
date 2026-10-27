using BotCripto.Models;
using BotCripto.Strategies;

namespace BotCripto.Services;

public class BotSchedulerService
{
    private readonly BotConfig _config;
    private int CheckIntervalMinutes => _config.Monitoring.IntervalMinutes;
    private readonly CryptoDataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly ReportingService _reportingService;
    private readonly SmaCrossoverStrategy _smaStrategy;
    private readonly RiskManager _riskManager;
    private CancellationTokenSource? _cts;
    private Timer? _weeklyReportTimer;
    private bool _isRunning = false;

    private decimal _currentAccountValue;
    private int _checksPerformed = 0;
    private int _signalsGenerated = 0;
    private int _tradesRecorded = 0;
    private readonly Dictionary<string, int> _cumulativeRejectionCounts = new();

    public BotSchedulerService(BotConfig config)
    {
        _config = config;
        var initialCapital = config.Account.InitialCapital;
        _dataService = new CryptoDataService(config.Monitoring.MinVolume, config.Monitoring.MaxCryptocurrenciesToAnalyze);
        _notificationService = new NotificationService();
        _reportingService = new ReportingService();
        _smaStrategy = new SmaCrossoverStrategy(
            config.Strategies.SmaCrossover.RsiOverboughtLevel,
            config.Strategies.SmaCrossover.RsiOversoldLevel);
        _riskManager = new RiskManager(
            initialCapital,
            riskPercentPerTrade: config.Account.RiskPerTrade,
            rewardRiskRatio: config.Account.RewardRiskRatio,
            maxPositionSizePercent: config.Account.MaxPositionSize,
            commissionsPercent: config.Account.CommissionsPercent,
            taxRate: config.Account.TaxRate,
            stopLossPercent: config.RiskManagement.StopLossPercent,
            takeProfitPercent: config.RiskManagement.TakeProfitPercent,
            maxConcurrentTrades: config.RiskManagement.MaxConcurrentTrades,
            minRiskRewardRatio: config.RiskManagement.MinRiskRewardRatio,
            capitalPerTrade: config.Account.CapitalPerTrade // importo fisso per trade (€)
        );

        _currentAccountValue = initialCapital;
    }

    public async Task StartAsync()
    {
        if (_isRunning)
        {
            Console.WriteLine("Bot è già in esecuzione.");
            return;
        }

        _isRunning = true;
        Console.WriteLine("\n🤖 Bot Cripto avviato!");
        Console.WriteLine($"📊 Monitoraggio ogni {CheckIntervalMinutes} minuti...\n");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Timer per il report settimanale (ogni lunedì alle 00:00)
        var now = DateTime.UtcNow;
        var nextMonday = now.AddDays((DayOfWeek.Monday - now.DayOfWeek + 7) % 7);
        if (nextMonday <= now)
            nextMonday = nextMonday.AddDays(7);

        var timeUntilMonday = nextMonday - now;
        _weeklyReportTimer = new Timer(
            _ =>
            {
                try { _reportingService.GenerateWeeklyReport(); }
                catch (Exception ex) { Console.WriteLine($"❌ Errore nel report settimanale: {ex.Message}"); }
            },
            null,
            timeUntilMonday,
            TimeSpan.FromDays(7));

        Console.WriteLine($"⏰ Prossimo report settimanale: {nextMonday:yyyy-MM-dd HH:mm:ss} UTC");

        // Cicli sequenziali: un ciclo non parte mai mentre il precedente è ancora in corso.
        // Se un ciclo dura più dell'intervallo, i tick persi vengono saltati (non accumulati).
        using var periodic = new PeriodicTimer(TimeSpan.FromMinutes(CheckIntervalMinutes));
        try
        {
            do
            {
                await CheckMarketAsync();
            }
            while (await periodic.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // arresto richiesto (Ctrl+C / Stop)
        }
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts?.Cancel();
        _weeklyReportTimer?.Dispose();
        Console.WriteLine("\n🛑 Bot Cripto fermato.");
    }

    private async Task CheckMarketAsync()
    {
        try
        {
            _checksPerformed++;
            Console.WriteLine($"\n⏱️  Ciclo #{_checksPerformed} - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");

            // Recupera tutte le criptovalute
            var cryptos = await _dataService.GetLargeCapCryptocurrenciesAsync();
            Console.WriteLine($"📈 Analizzando {cryptos.Count} criptovalute (max {_config.Monitoring.MaxCryptocurrenciesToAnalyze})...");

            int signalsFound = 0;
            int signalsFiltered = 0;
            var rejectionCounts = new Dictionary<string, int>();
            await CloseTradesAtTakeProfitAsync(cryptos);
            RefreshAccountValue();
            var openTrades = _reportingService.GetAllTrades().Where(t => t.Status == "Open").ToList();

            var openSymbols = new HashSet<string>(openTrades.Select(t => t.Symbol));

            void CountRejection(string category)
            {
                rejectionCounts.TryGetValue(category, out var c);
                rejectionCounts[category] = c + 1;

                _cumulativeRejectionCounts.TryGetValue(category, out var cc);
                _cumulativeRejectionCounts[category] = cc + 1;
            }

            foreach (var crypto in cryptos)
            {
                try
                {
                    // Recupera candele per l'analisi
                    var candles = await _dataService.GetCandlesAsync(crypto.Symbol, _config.Monitoring.CandleTimeframe, _config.Monitoring.CandlesLookback);

                    if (candles.Count < _config.Monitoring.MinCandlesForAnalysis)
                    {
                        CountRejection($"Dati insufficienti (candele < {_config.Monitoring.MinCandlesForAnalysis})");
                        continue;
                    }

                    if (openSymbols.Contains(crypto.Symbol))
                    {
                        CountRejection("Trade già aperto su questo simbolo");
                        continue;
                    }

                    var volatilityPercent = CalculateVolatility(candles);

                    // ✅ STRATEGIA PRINCIPALE: SMA CROSSOVER

                    // 1️⃣ SMA Strategy + Candle Confirmation (Principale - Win Rate 55-60%)
                    var smaResult = _smaStrategy.Analyze(crypto.Symbol, candles);

                    // Il bot opera solo long (spot): i segnali SELL (death cross) non aprono posizioni
                    if (smaResult.IsSignal && smaResult.Indicators.TryGetValue("Trend", out var trend) && trend < 0)
                    {
                        CountRejection("Strategia: segnale SELL ignorato (solo long)");
                    }
                    else if (smaResult.IsSignal)
                    {
                        // Valida con RiskManager
                        var positionResult = _riskManager.CalculatePosition(
                            crypto.Symbol,
                            crypto.CurrentPrice,
                            volatilityPercent,
                            openTrades,
                            _currentAccountValue,
                            smaResult.Indicators.TryGetValue("StopLoss", out var strategyStop) ? strategyStop : null
                        );

                        if (positionResult.IsValid)
                        {
                            smaResult.Indicators["PositionSize"] = positionResult.PositionSize;
                            smaResult.Indicators["RiskRewardRatio"] = positionResult.RiskRewardRatio;
                            smaResult.Indicators["Leverage"] = positionResult.LeverageRatio;
                            smaResult.Indicators["ExpectedProfit"] = positionResult.ExpectedProfit;

                            await _notificationService.SendNotificationAsync(smaResult);
                            _reportingService.RecordTrade(
                                crypto.Symbol,
                                crypto.CurrentPrice,
                                "SMA Strategy (Primary)",
                                positionResult.StopLossPrice,
                                positionResult.TargetPrice,
                                positionResult.PositionSize);

                            openSymbols.Add(crypto.Symbol);
                            openTrades.Add(_reportingService.GetAllTrades().Last());
                            _signalsGenerated++;
                            _tradesRecorded++;
                            signalsFound++;
                        }
                        else
                        {
                            signalsFiltered++;
                            CountRejection(ClassifyRiskRejection(positionResult.Reason));
                        }
                    }
                    else
                    {
                        CountRejection(ClassifyStrategyRejection(smaResult.Signal));
                    }

                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ⚠️  {crypto.Symbol}: {ex.Message}");
                }
            }

            // Report del ciclo
            Console.WriteLine($"\n✅ Ciclo completato:");
            Console.WriteLine($"   • Segnali trovati: {signalsFound}");
            Console.WriteLine($"   • Segnali filtrati (rischio): {signalsFiltered}");
            Console.WriteLine($"   • Trade aperti: {openTrades.Count}");
            Console.WriteLine($"   • Account value (stimato): €{_currentAccountValue:F2}");

            if (rejectionCounts.Count > 0)
            {
                Console.WriteLine("\n📊 Motivi di scarto in questo ciclo:");
                foreach (var kv in rejectionCounts.OrderByDescending(k => k.Value))
                {
                    Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                }
            }

            // Calcola e mostra metriche giornaliere ogni 10 cicli
            if (_checksPerformed % _config.Reporting.ShowMetricsEveryNCycles == 0)
            {
                var allTrades = _reportingService.GetAllTrades();
                var metrics = _riskManager.CalculatePerformanceMetrics(allTrades, _config.Account.InitialCapital);
                if (metrics.TotalTrades > 0)
                {
                    Console.WriteLine($"\n📊 Metriche cumulative (dopo {_checksPerformed} cicli):");
                    Console.WriteLine($"   • Win Rate: {metrics.WinRate:P}");
                    Console.WriteLine($"   • Profit Factor: {metrics.ProfitFactor:F2}");
                    Console.WriteLine($"   • Total P&L: €{metrics.TotalProfit:F2}");
                    Console.WriteLine($"   • Expectancy: €{metrics.Expectancy:F2}");
                }

                if (_cumulativeRejectionCounts.Count > 0)
                {
                    Console.WriteLine($"\n📊 Motivi di scarto cumulativi (dopo {_checksPerformed} cicli):");
                    foreach (var kv in _cumulativeRejectionCounts.OrderByDescending(k => k.Value))
                    {
                        Console.WriteLine($"   • {kv.Key}: {kv.Value}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Errore nel controllo del mercato: {ex.Message}");
        }
    }

    // Profitto netto in € (commissioni e tasse incluse) di un trade chiuso al prezzo indicato.
    private decimal? NetProfit(Trade trade, decimal exitPrice) =>
        trade.PositionSize is > 0
            ? _riskManager.CalculateProfitAndTaxes(trade.EntryPrice, exitPrice, trade.PositionSize.Value, exitPrice > trade.EntryPrice).NetProfit
            : null;

    // Valore account = capitale iniziale + profitto netto (dopo commissioni e tasse) dei trade chiusi.
    // Ricalcolato dallo storico, così resta corretto anche dopo un riavvio.
    // I trade senza size salvata (aperti prima di questa versione) non sono valorizzabili e vengono ignorati.
    private void RefreshAccountValue()
    {
        var realized = _reportingService.GetAllTrades()
            .Where(t => t.Status == "Closed" && t.PositionSize.HasValue)
            .Sum(t => t.Profit ?? 0);

        _currentAccountValue = _config.Account.InitialCapital + realized;
    }

    // Chiude i trade aperti che hanno raggiunto il take profit o lo stop loss salvati all'apertura
    // (per i trade senza livelli salvati: take profit di default a entry * (1 + TakeProfitPercent)).
    private async Task CloseTradesAtTakeProfitAsync(List<Cryptocurrency> cryptos)
    {
        var prices = cryptos.GroupBy(c => c.Symbol).ToDictionary(g => g.Key, g => g.First().CurrentPrice);

        foreach (var trade in _reportingService.GetAllTrades().Where(t => t.Status == "Open").ToList())
        {
            if (!prices.TryGetValue(trade.Symbol, out var price) || price <= 0)
                continue;

            var takeProfit = trade.TakeProfitPrice ?? trade.EntryPrice * (1 + _riskManager.TakeProfitPercent);

            if (price >= takeProfit)
            {
                await _reportingService.CloseTradeAsync(trade.Symbol, price, NetProfit(trade, price));
                Console.WriteLine($"   🎯 Take profit: {trade.Symbol} chiuso a {price} (entrata {trade.EntryPrice}, TP {takeProfit})");
            }
            else if (trade.StopLossPrice is decimal stop && price <= stop)
            {
                await _reportingService.CloseTradeAsync(trade.Symbol, price, NetProfit(trade, price));
                Console.WriteLine($"   🛑 Stop loss: {trade.Symbol} chiuso a {price} (entrata {trade.EntryPrice}, SL {stop})");
            }
        }
    }

    // Raggruppa i motivi di scarto della strategia in categorie leggibili per la diagnostica.
    private static string ClassifyStrategyRejection(string signal)
    {
        if (signal.Contains("SMA not aligned"))
            return "Strategia: trend non allineato (SMA10/20/50)";
        if (signal.Contains("Golden Cross") || signal.Contains("Death Cross"))
            return "Strategia: nessun cross recente";
        if (signal.Contains("Volume insufficient"))
            return "Strategia: volume insufficiente";
        if (signal.Contains("Momentum"))
            return "Strategia: momentum contrario al trend";
        if (signal.Contains("candle not"))
            return "Strategia: candela non abbastanza forte";
        if (signal.Contains("RSI"))
            return "Strategia: RSI estremo";
        if (signal == "No Signal")
            return "Strategia: dati insufficienti per il calcolo";
        return $"Strategia: altro ({signal})";
    }

    // Raggruppa i motivi di scarto del RiskManager in categorie leggibili per la diagnostica.
    private static string ClassifyRiskRejection(string reason)
    {
        if (reason.Contains("Max concurrent trades"))
            return "Risk Manager: troppi trade aperti simultaneamente";
        if (reason.Contains("profit"))
            return "Risk Manager: profitto non copre commissioni";
        if (reason.Contains("Risk-reward"))
            return "Risk Manager: rapporto rischio/rendimento insufficiente";
        if (reason.Contains("Stop loss"))
            return "Risk Manager: errore calcolo stop loss";
        return $"Risk Manager: altro ({reason})";
    }

    private decimal CalculateVolatility(List<Candle> candles)
    {
        if (candles.Count < 20)
            return 0.02m;

        var closes = candles.TakeLast(20).Select(c => c.Close).ToList();
        var returns = new List<decimal>();

        for (int i = 1; i < closes.Count; i++)
        {
            returns.Add((closes[i] - closes[i - 1]) / closes[i - 1]);
        }

        var avg = returns.Average();
        var variance = returns.Sum(r => (r - avg) * (r - avg)) / returns.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return Math.Max(0.01m, Math.Min(stdDev, 0.08m));
    }
}
