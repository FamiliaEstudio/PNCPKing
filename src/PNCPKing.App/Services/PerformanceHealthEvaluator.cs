using PNCPKing.Core.Models;
using PNCPKing.Infrastructure.Api;

namespace PNCPKing.App.Services;

public enum PerformanceIndicatorLevel
{
    Measuring,
    Good,
    Warning,
    Critical
}

public sealed record PerformanceHealthEvaluation(
    PerformanceIndicatorLevel Interface,
    string InterfaceLabel,
    string InterfaceReason,
    PerformanceIndicatorLevel Pncp,
    string PncpLabel,
    string PncpReason);

public static class PerformanceHealthEvaluator
{
    public static readonly TimeSpan RollingWindow = TimeSpan.FromSeconds(60);

    public static PerformanceHealthEvaluation Evaluate(
        LivePerformanceSnapshot snapshot,
        PncpRecentRequestSnapshot recentPncp)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(recentPncp);

        var interfaceLevel = PerformanceIndicatorLevel.Good;
        var interfaceLabel = "Responsiva";
        var interfaceReason = "A interface está respondendo dentro da faixa normal.";
        if (snapshot.Resources.Pressure == SystemResourcePressure.Critical ||
            snapshot.DispatcherDelayP95 >= TimeSpan.FromMilliseconds(500))
        {
            interfaceLevel = PerformanceIndicatorLevel.Critical;
            interfaceLabel = "Lenta";
            interfaceReason = snapshot.Resources.Pressure == SystemResourcePressure.Critical
                ? "A RAM física atingiu nível crítico."
                : "O atraso p95 da interface atingiu 500 ms ou mais.";
        }
        else if (snapshot.Resources.Pressure == SystemResourcePressure.Constrained ||
                 snapshot.DispatcherDelayP95 >= TimeSpan.FromMilliseconds(100))
        {
            interfaceLevel = PerformanceIndicatorLevel.Warning;
            interfaceLabel = "Regular";
            interfaceReason = snapshot.Resources.Pressure == SystemResourcePressure.Constrained
                ? "O computador está operando com recursos restritos."
                : "O atraso p95 da interface atingiu 100 ms ou mais.";
        }

        var scheduler = snapshot.Scheduler;
        var effectiveConcurrency = scheduler?.EffectiveConcurrency ?? 0;
        var queuedRequests = scheduler?.TotalQueued ?? 0;
        var criticalQueue = effectiveConcurrency > 0 && queuedRequests > effectiveConcurrency * 2;
        var warningQueue = effectiveConcurrency > 0 && queuedRequests > effectiveConcurrency;
        var cooldownActive = scheduler?.GrowthBlockedUntil is { } blockedUntil &&
                             blockedUntil > snapshot.CapturedAt;
        var completedCalls = recentPncp.Succeeded + recentPncp.Failed;

        var pncpLevel = PerformanceIndicatorLevel.Good;
        var pncpLabel = "Normal";
        var pncpReason = "As chamadas concluídas no último minuto estão dentro da faixa normal.";
        if (completedCalls == 0)
        {
            pncpLevel = PerformanceIndicatorLevel.Measuring;
            pncpLabel = "Aguardando";
            pncpReason = "Nenhuma chamada ao PNCP foi concluída no último minuto.";
        }
        else if ((recentPncp.Failed >= 3 && recentPncp.Succeeded == 0) ||
                 recentPncp.P95 is { } criticalP95 && criticalP95 > TimeSpan.FromSeconds(30) ||
                 criticalQueue)
        {
            pncpLevel = PerformanceIndicatorLevel.Critical;
            pncpLabel = "Indisponível";
            pncpReason = recentPncp.Failed >= 3 && recentPncp.Succeeded == 0
                ? "Três ou mais chamadas reais falharam sem sucesso no último minuto."
                : recentPncp.P95 is { } unavailableP95 && unavailableP95 > TimeSpan.FromSeconds(30)
                    ? "A latência p95 do PNCP ultrapassou 30 segundos."
                    : "A fila do PNCP ultrapassou o dobro da concorrência disponível.";
        }
        else if (cooldownActive)
        {
            pncpLevel = PerformanceIndicatorLevel.Warning;
            pncpLabel = "Recuperando";
            pncpReason = "A concorrência do PNCP está em recuo temporário e será recuperada gradualmente.";
        }
        else if (recentPncp.Failed > 0)
        {
            pncpLevel = PerformanceIndicatorLevel.Warning;
            pncpLabel = "Oscilando";
            pncpReason = recentPncp.Succeeded > 0
                ? "Houve falhas reais intercaladas com sucessos no último minuto."
                : "Houve falhas reais recentes, ainda abaixo do limite de indisponibilidade.";
        }
        else if (recentPncp.P95 is { } warningP95 && warningP95 > TimeSpan.FromSeconds(10) ||
                 warningQueue)
        {
            pncpLevel = PerformanceIndicatorLevel.Warning;
            pncpLabel = "Lento";
            pncpReason = recentPncp.P95 is { } slowP95 && slowP95 > TimeSpan.FromSeconds(10)
                ? "A latência p95 do PNCP ultrapassou 10 segundos."
                : "A fila do PNCP está acima da concorrência disponível.";
        }

        return new PerformanceHealthEvaluation(
            interfaceLevel,
            interfaceLabel,
            interfaceReason,
            pncpLevel,
            pncpLabel,
            pncpReason);
    }
}
