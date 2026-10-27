using BotCripto.Services;

// Log su file di tutto l'output (Logs/cycles_yyyy-MM-dd.log; i backtest in Logs/backtest_yyyy-MM-dd.log)
var logPrefix = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault()?.StartsWith("--backtest", StringComparison.OrdinalIgnoreCase) == true ? "backtest" : "cycles";
FileLogger.Install(Path.Combine(AppContext.BaseDirectory, "Logs"), logPrefix);
Console.WriteLine($"▶️  Avvio Bot 2 - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC - eseguibile in {AppContext.BaseDirectory}");

// Qualsiasi errore che chiude il processo finisce nel log, così si sa perché il bot si è fermato
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.WriteLine($"💥 ERRORE FATALE, il bot si chiude: {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.WriteLine($"⚠️  Eccezione non gestita in un task: {e.Exception}");
    e.SetObserved();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    Console.WriteLine($"⏹️  Bot 2 terminato - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
Console.CancelKeyPress += (_, _) =>
    Console.WriteLine("⏹️  Chiusura richiesta con Ctrl+C");

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
    Console.WriteLine("  • Capitale investibile: illimitato");
    Console.WriteLine($"  • Intervallo Monitoraggio: {config.Monitoring.IntervalMinutes} minuti");
    Console.WriteLine($"  • Max Crypto: {config.Monitoring.MaxCryptocurrenciesToAnalyze}");
    Console.WriteLine($"  • Importo per trade: €{config.Account.CapitalPerTrade:F2}");
    Console.WriteLine($"  • Take Profit: {config.RiskManagement.TakeProfitPercent:P0} | Max trade aperti: illimitati");
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

