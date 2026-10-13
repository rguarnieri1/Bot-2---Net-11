using BotCripto.Models;
using BotCripto.Strategies;

namespace BotCripto.Services;

public class BotSchedulerService
{
    private const int CheckIntervalMinutes = 10;
    private readonly CryptoDataService _dataService;
    private readonly NotificationService _notificationService;
    private readonly ReportingService _reportingService;
    private readonly SmaCrossoverStrategy _smaStrategy;
    private readonly RiskManager _riskManager;
    private Timer? _marketCheckTimer;
    private Timer? _weeklyReportTimer;
    private bool _isRunning = false;

    private decimal _currentAccountValue = 1000m;
    private int _checksPerformed = 0;
    private int _signalsGenerated = 0;
    private int _tradesRecorded = 0;
    private readonly Dictionary<string, int> _cumulativeRejectionCounts = new();

    public BotSchedulerService(decimal initialCapital = 1000m)
    {
        _dataService = new CryptoDataService();
        _notificationService = new NotificationService();
        _reportingService = new ReportingService();
        _smaStrategy = new SmaCrossoverStrategy();
        _riskManager = new RiskManager(
            initialCapital,
            riskPercentPerTrade: 0.02m,      // 2% rischio per trade
            rewardRiskRatio: 2.0m,            // 2:1 R:R
            maxPositionSizePercent: 0.10m,    // Max 10% per trade
            commissionsPercent: 0.6m,         // 0.6% commissioni
            taxRate: 0.26m                    // 26% tasse
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

        // Esegui il primo controllo immediatamente
        await CheckMarketAsync();

        // Timer per il controllo ogni CheckIntervalMinutes minuti
        _marketCheckTimer = new Timer(
            async _ => await CheckMarketAsync(),
            null,
            TimeSpan.FromMinutes(CheckIntervalMinutes),
            TimeSpan.FromMinutes(CheckIntervalMinutes));

        // Timer per il report settimanale (ogni lunedì alle 00:00)
        var now = DateTime.UtcNow;
        var nextMonday = now.AddDays((DayOfWeek.Monday - now.DayOfWeek + 7) % 7);
        if (nextMonday <= now)
            nextMonday = nextMonday.AddDays(7);

        var timeUntilMonday = nextMonday - now;
        _weeklyReportTimer = new Timer(
            _ => _reportingService.GenerateWeeklyReport(),
            null,
            timeUntilMonday,
            TimeSpan.FromDays(7));

        Console.WriteLine($"⏰ Prossimo report settimanale: {nextMonday:yyyy-MM-dd HH:mm:ss} UTC");

        await Task.Delay(-1);
    }

    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _marketCheckTimer?.Dispose();
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
            Console.WriteLine($"📈 Analizzando {cryptos.Count} criptovalute (max 500 per performance)...");

            int signalsFound = 0;
            int signalsFiltered = 0;
            var rejectionCounts = new Dictionary<string, int>();
            await CloseTradesAtTakeProfitAsync(cryptos);
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
                    var candles = await _dataService.GetCandlesAsync(crypto.Symbol, "4h", 100);

                    if (candles.Count < 50)
                    {
                        CountRejection("Dati insufficienti (candele < 50)");
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
            if (_checksPerformed % 10 == 0)
            {
                var allTrades = _reportingService.GetAllTrades();
                var metrics = _riskManager.CalculatePerformanceMetrics(allTrades, 1000m);
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

    // Chiude i trade aperti che hanno raggiunto il take profit o lo stop loss salvati all'apertura
    // (per i trade senza livelli salvati: take profit di default a entry * (1 + TakeProfitPercent)).
    private async Task CloseTradesAtTakeProfitAsync(List<Cryptocurrency> cryptos)
    {
        var prices = cryptos.GroupBy(c => c.Symbol).ToDictionary(g => g.Key, g => g.First().CurrentPrice);

        foreach (var trade in _reportingService.GetAllTrades().Where(t => t.Status == "Open").ToList())
        {
            if (!prices.TryGetValue(trade.Symbol, out var price) || price <= 0)
                continue;

            var takeProfit = trade.TakeProfitPrice ?? trade.EntryPrice * (1 + RiskManager.TakeProfitPercent);

            if (price >= takeProfit)
            {
                await _reportingService.CloseTradeAsync(trade.Symbol, price);
                Console.WriteLine($"   🎯 Take profit: {trade.Symbol} chiuso a {price} (entrata {trade.EntryPrice}, TP {takeProfit})");
            }
            else if (trade.StopLossPrice is decimal stop && price <= stop)
            {
                await _reportingService.CloseTradeAsync(trade.Symbol, price);
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
