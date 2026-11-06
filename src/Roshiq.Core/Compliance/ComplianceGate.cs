using Roshiq.Core.Models;

namespace Roshiq.Core.Compliance;

/// <summary>
/// Product feature routing. User-enabled on-screen draft recognition works in either context;
/// enemy-aware item recommendations retain the separate Training setting.
/// This enum is not a statement of approval by a game publisher.
/// </summary>
public static class ComplianceGate
{
    public static FeatureTier TierOf(Feature feature) => feature switch
    {
        Feature.HeroCatalog => FeatureTier.Reference,
        Feature.ReferenceBuilds => FeatureTier.Reference,
        Feature.RoleTips => FeatureTier.Reference,
        Feature.ScheduledSupportReminders => FeatureTier.Reference,
        Feature.GsiOwnHeroItems => FeatureTier.Reference,
        // Ручной разбор драфта в главном окне — учебный справочник (как сайт с матчапами).
        Feature.ManualDraftAnalysis => FeatureTier.Reference,
        Feature.AutoDraftRecognition => FeatureTier.Reference,
        Feature.EnemyAwareItemAdvice => FeatureTier.Training,
        Feature.LiveDraftAdviceInPublicMatch => FeatureTier.Reference,
        _ => FeatureTier.RequiresVerification
    };

    /// <summary>Разрешена ли функция в текущем контексте использования.</summary>
    public static bool IsAllowed(Feature feature, UsageContext context) => TierOf(feature) switch
    {
        FeatureTier.Reference => true,
        FeatureTier.Training => context == UsageContext.Training,
        _ => false // RequiresVerification: всегда выключено, пока правила не подтверждены
    };
}
