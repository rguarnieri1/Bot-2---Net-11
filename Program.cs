using BotCripto.Services;

Console.WriteLine(@"
╔════════════════════════════════════════════════════════╗
║       🤖 BOT 2 - SMA-Crypto - VERSIONE 1.1             ║
║                 Backtest + Live Trading                ║
╚════════════════════════════════════════════════════════╝
");

// Check se è backtest mode
var cmdArgs = Environment.GetCommandLineArgs();
bool isBacktestMode = cmdArgs.Length > 1 && cmdArgs[1].ToLower() == "--backtest";

if (isBacktestMode)
{
    await RunBacktestAsync();
}
else
{
    await RunLiveAsync();
}

async Task RunLiveAsync()
{
    var config = BotConfig.Load();
    var scheduler = new BotSchedulerService(config);

    Console.WriteLine("Strategia Principale Attiva:");
    Console.WriteLine("  ⭐ SMA Strategy (Simple Moving Average) + Candle Confirmation");
    Console.WriteLine("     • Win Rate Atteso: 55-60%");
    Console.WriteLine("     • Configurazione: SMA 10, 20, 50");
    Console.WriteLine("     • Filtri: Volume, Momentum, Candle Body, RSI, Golden/Death Cross");
    Console.WriteLine("\nImpostazioni:");
    var capital = config.Account.InitialCapital;
    Console.WriteLine($"  • Capitale Iniziale: €{capital:F2}");
    Console.WriteLine($"  • Intervallo Monitoraggio: {config.Monitoring.IntervalMinutes} minuti");
    Console.WriteLine($"  • Max Crypto: {config.Monitoring.MaxCryptocurrenciesToAnalyze}");
    Console.WriteLine($"  • Risk per Trade: {config.Account.RiskPerTrade:P0} (€{capital * config.Account.RiskPerTrade:F2})");
    Console.WriteLine($"  • Importo per trade: €{config.Account.CapitalPerTrade:F2} (fisso, nessun tetto massimo)");
    Console.WriteLine($"  • Take Profit: {config.RiskManagement.TakeProfitPercent:P0} | Max trade aperti: {config.RiskManagement.MaxConcurrentTrades}");
    Console.WriteLine("  • Report Settimanale: Lunedì 00:00");
    Console.WriteLine($"  • Tracking Metriche: Ogni {config.Reporting.ShowMetricsEveryNCycles} cicli");

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // lascia terminare il ciclo corrente e chiudi in modo ordinato
        scheduler.Stop();
    };

    try
    {
        await scheduler.StartAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore: {ex.Message}");
    }
    finally
    {
        scheduler.Stop();
    }
}

async Task RunBacktestAsync()
{
    Console.WriteLine("\n🔍 MODALITA' BACKTEST SINTETICO ATTIVATA\n");

    var backtest = new SyntheticBacktest(initialCapital: 100m);

    try
    {
        // Esegui backtest su 365 giorni (1 anno)
        // con 20 simboli, 55% win-rate atteso, 8 trade per simbolo
        var result = backtest.RunBacktest(
            numSymbols: 20,
            daysOfData: 365,
            expectedWinRate: 0.55m,
            tradesPerSymbol: 8
        );

        // Stampa report dettagliato
        backtest.PrintBacktestReport(result);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Errore nel backtest: {ex.Message}\n{ex.StackTrace}");
    }
}

