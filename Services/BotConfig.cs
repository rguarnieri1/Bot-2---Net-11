using Newtonsoft.Json;

namespace BotCripto.Services;

// Impostazioni lette da config.json (unica fonte di verità per i parametri di trading).
// I valori di default qui sotto vengono usati solo se il file manca o una chiave non è presente.
public class BotConfig
{
    [JsonProperty("account")]
    public AccountConfig Account { get; set; } = new();

    [JsonProperty("monitoring")]
    public MonitoringConfig Monitoring { get; set; } = new();

    [JsonProperty("riskManagement")]
    public RiskConfig RiskManagement { get; set; } = new();

    [JsonProperty("strategies")]
    public StrategiesConfig Strategies { get; set; } = new();

    [JsonProperty("reporting")]
    public ReportingConfig Reporting { get; set; } = new();

    public static BotConfig Load(string fileName = "config.json")
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                var config = JsonConvert.DeserializeObject<BotConfig>(File.ReadAllText(path)) ?? new BotConfig();
                Console.WriteLine($"⚙️  Configurazione caricata da {path}");
                return config;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Errore nella lettura di {path}: {ex.Message} - uso i valori di default");
                return new BotConfig();
            }
        }

        Console.WriteLine($"⚠️  {fileName} non trovato - uso i valori di default");
        return new BotConfig();
    }
}

public class AccountConfig
{
    // Nessun importo totale investibile (capitale illimitato): ogni trade investe questo importo fisso in €
    [JsonProperty("capitalPerTrade")] public decimal CapitalPerTrade { get; set; } = 10m;
    [JsonProperty("rewardRiskRatio")] public decimal RewardRiskRatio { get; set; } = 2.0m;
    [JsonProperty("commissionsPercent")] public decimal CommissionsPercent { get; set; } = 0.8m;
    [JsonProperty("taxRate")] public decimal TaxRate { get; set; } = 0.26m;
}

public class MonitoringConfig
{
    [JsonProperty("intervalMinutes")] public int IntervalMinutes { get; set; } = 10;
    [JsonProperty("maxCryptocurrenciesToAnalyze")] public int MaxCryptocurrenciesToAnalyze { get; set; } = 500;
    [JsonProperty("candleTimeframe")] public string CandleTimeframe { get; set; } = "4h";
    [JsonProperty("candlesLookback")] public int CandlesLookback { get; set; } = 100;
    [JsonProperty("minCandlesForAnalysis")] public int MinCandlesForAnalysis { get; set; } = 50;
    [JsonProperty("minVolume")] public decimal MinVolume { get; set; } = 2_000_000m;
}

public class RiskConfig
{
    [JsonProperty("stopLossPercent")] public decimal StopLossPercent { get; set; } = 0.03m;
    [JsonProperty("takeProfitPercent")] public decimal TakeProfitPercent { get; set; } = 0.02m;
    [JsonProperty("minRiskRewardRatio")] public decimal MinRiskRewardRatio { get; set; } = 0.5m;
}

public class StrategiesConfig
{
    [JsonProperty("smaCrossover")] public SmaConfig SmaCrossover { get; set; } = new();
}

public class SmaConfig
{
    [JsonProperty("rsiOverboughtLevel")] public decimal RsiOverboughtLevel { get; set; } = 70m;
    [JsonProperty("rsiOversoldLevel")] public decimal RsiOversoldLevel { get; set; } = 30m;
}

public class ReportingConfig
{
    [JsonProperty("showMetricsEveryNCycles")] public int ShowMetricsEveryNCycles { get; set; } = 10;
}
