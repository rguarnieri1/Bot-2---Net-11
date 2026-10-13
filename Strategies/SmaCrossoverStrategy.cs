using BotCripto.Models;

namespace BotCripto.Strategies;

public class SmaCrossoverStrategy
{
    // Configurazione ottimizzata per win rate 55-60%
    private readonly int _fastPeriod = 10;
    private readonly int _mediumPeriod = 20;
    private readonly int _slowPeriod = 50;
    private readonly decimal _minVolumeRatio = 0.8m;              // Volume minimo richiesto rispetto alla media
    private readonly decimal _bodyStrengthThreshold = 0.5m;      // Corpo deve essere 50% della candela
    private readonly int _momentumLookback = 10;                 // Candele per il filtro momentum
    private readonly int _crossoverLookback = 5;                 // Il cross deve essere avvenuto entro N candele, non solo sull'ultima
    private readonly decimal _rsiExtremeOverbought;
    private readonly decimal _rsiExtremeOversold;

    public SmaCrossoverStrategy(decimal rsiOverbought = 70m, decimal rsiOversold = 30m)
    {
        _rsiExtremeOverbought = rsiOverbought;
        _rsiExtremeOversold = rsiOversold;
    }

    public AnalysisResult Analyze(string symbol, List<Candle> candles)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = "SMA Strategy (Simple Moving Average) + Candle Confirmation",
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < 60)
            return result;

        var closes = candles.Select(c => c.Close).ToList();
        var volumes = candles.Select(c => c.Volume).ToList();

        // 1️⃣ CALCULATE SMA
        var smaFast = TechnicalIndicators.CalculateSMA(closes, _fastPeriod);
        var smaMedium = TechnicalIndicators.CalculateSMA(closes, _mediumPeriod);
        var smaSlow = TechnicalIndicators.CalculateSMA(closes, _slowPeriod);

        if (smaFast.Count < 2 || smaMedium.Count < 2 || smaSlow.Count == 0)
            return result;

        var currentPrice = closes.Last();
        var lastSmaFast = smaFast.Last();
        var lastSmaMedium = smaMedium.Last();
        var lastSmaSlow = smaSlow.Last();

        result.Indicators["SMA10"] = lastSmaFast;
        result.Indicators["SMA20"] = lastSmaMedium;
        result.Indicators["SMA50"] = lastSmaSlow;
        result.CurrentPrice = currentPrice;

        // 2️⃣ TREND FILTER - SMA Alignment Check
        // Uptrend: SMA10 > SMA20 > SMA50
        // Downtrend: SMA10 < SMA20 < SMA50
        bool isUptrendAligned = lastSmaFast > lastSmaMedium && lastSmaMedium > lastSmaSlow;
        bool isDowntrendAligned = lastSmaFast < lastSmaMedium && lastSmaMedium < lastSmaSlow;

        if (!isUptrendAligned && !isDowntrendAligned)
        {
            result.Signal = "Rejected - SMA not aligned (no clear trend)";
            return result;
        }

        result.Indicators["Trend"] = isUptrendAligned ? 1 : -1;

        // 3️⃣ CROSSOVER FILTER - SMA10 deve aver incrociato SMA20 entro le ultime _crossoverLookback candele
        // (non solo sull'ultima: un trend appena confermato resta valido per qualche candela)
        bool goldenCross = HasGoldenCrossWithin(smaFast, smaMedium, _crossoverLookback);
        bool deathCross = HasDeathCrossWithin(smaFast, smaMedium, _crossoverLookback);

        if (isUptrendAligned && !goldenCross)
        {
            result.Signal = $"Rejected - No Golden Cross in last {_crossoverLookback} candles (SMA10/SMA20)";
            return result;
        }

        if (isDowntrendAligned && !deathCross)
        {
            result.Signal = $"Rejected - No Death Cross in last {_crossoverLookback} candles (SMA10/SMA20)";
            return result;
        }

        // 4️⃣ VOLUME FILTER - Ensure strong volume
        var avgVolume = volumes.TakeLast(20).Average();
        var recentVolume = volumes.Last();

        if (recentVolume < avgVolume * _minVolumeRatio)
        {
            result.Signal = "Rejected - Volume insufficient";
            return result;
        }

        result.Indicators["Volume"] = recentVolume;
        result.Indicators["AvgVolume"] = avgVolume;
        result.Indicators["VolumeRatio"] = recentVolume / avgVolume;

        // 5️⃣ MOMENTUM FILTER - Rate of change nella direzione del trend
        var momentumBaseIndex = closes.Count - 1 - _momentumLookback;
        if (momentumBaseIndex < 0)
            return result;

        var momentum = (currentPrice - closes[momentumBaseIndex]) / closes[momentumBaseIndex];
        result.Indicators["Momentum"] = momentum;

        if (isUptrendAligned && momentum <= 0)
        {
            result.Signal = "Rejected - Momentum negativo in uptrend";
            return result;
        }

        if (isDowntrendAligned && momentum >= 0)
        {
            result.Signal = "Rejected - Momentum positivo in downtrend";
            return result;
        }

        // 6️⃣ CANDLE CONFIRMATION - Solid body and alignment
        var lastCandle = candles.Last();

        // Corpo orientato nella direzione del trend (positivo = candela a favore del trade)
        var lastCandleRange = lastCandle.High - lastCandle.Low;
        var directedBody = isUptrendAligned
            ? lastCandle.Close - lastCandle.Open
            : lastCandle.Open - lastCandle.Close;
        var bodyStrength = lastCandleRange > 0 ? directedBody / lastCandleRange : 0;

        if (directedBody <= 0 || bodyStrength < _bodyStrengthThreshold)
        {
            result.Signal = isUptrendAligned
                ? "Rejected - Last candle not bullish enough"
                : "Rejected - Last candle not bearish enough";
            return result;
        }

        result.Indicators["CandleBodyStrength"] = bodyStrength;

        // 7️⃣ RSI FILTER - Avoid overbought/oversold extremes
        var rsi = TechnicalIndicators.CalculateRSI(closes);
        if (rsi.Count < 1)
            return result;

        var currentRSI = rsi.Last();
        result.Indicators["RSI"] = currentRSI;

        if (isUptrendAligned && currentRSI > _rsiExtremeOverbought)
        {
            result.Signal = "Rejected - RSI overbought (extended move)";
            return result;
        }

        if (isDowntrendAligned && currentRSI < _rsiExtremeOversold)
        {
            result.Signal = "Rejected - RSI oversold (extended move)";
            return result;
        }

        // ✅ SIGNAL GENERATED
        if (isUptrendAligned)
        {
            result.IsSignal = true;
            result.Signal = "🟢 BUY - SMA Golden Cross (Trend + Momentum + Volume)";
            result.Indicators["StopLoss"] = Math.Min(lastSmaMedium, lastCandle.Low) * 0.98m;
            result.Indicators["Target"] = currentPrice + (currentPrice - result.Indicators["StopLoss"]) * 2;
        }
        else
        {
            result.IsSignal = true;
            result.Signal = "🔴 SELL - SMA Death Cross (Trend + Momentum + Volume)";
            result.Indicators["StopLoss"] = Math.Max(lastSmaMedium, lastCandle.High) * 1.02m;
            result.Indicators["Target"] = currentPrice - (result.Indicators["StopLoss"] - currentPrice) * 2;
        }

        result.Indicators["SignalStrength"] = Math.Min(1m, bodyStrength + (recentVolume / avgVolume - 1) * 0.5m);

        return result;
    }

    // SMA con periodi diversi hanno lunghezze diverse (periodo più corto = lista più lunga):
    // i loro elementi vanno allineati contando dalla fine (l'ultimo elemento di ciascuna
    // corrisponde sempre alla stessa candela), mai per indice assoluto.
    private static bool HasGoldenCrossWithin(List<decimal> smaFast, List<decimal> smaMedium, int lookback)
    {
        int maxOffset = Math.Min(lookback, Math.Min(smaFast.Count, smaMedium.Count) - 1);
        for (int offset = 0; offset < maxOffset; offset++)
        {
            var fastCurr = smaFast[smaFast.Count - 1 - offset];
            var fastPrev = smaFast[smaFast.Count - 2 - offset];
            var medCurr = smaMedium[smaMedium.Count - 1 - offset];
            var medPrev = smaMedium[smaMedium.Count - 2 - offset];

            if (fastPrev <= medPrev && fastCurr > medCurr)
                return true;
        }
        return false;
    }

    private static bool HasDeathCrossWithin(List<decimal> smaFast, List<decimal> smaMedium, int lookback)
    {
        int maxOffset = Math.Min(lookback, Math.Min(smaFast.Count, smaMedium.Count) - 1);
        for (int offset = 0; offset < maxOffset; offset++)
        {
            var fastCurr = smaFast[smaFast.Count - 1 - offset];
            var fastPrev = smaFast[smaFast.Count - 2 - offset];
            var medCurr = smaMedium[smaMedium.Count - 1 - offset];
            var medPrev = smaMedium[smaMedium.Count - 2 - offset];

            if (fastPrev >= medPrev && fastCurr < medCurr)
                return true;
        }
        return false;
    }
}
