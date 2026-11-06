namespace Roshiq.Core.Models;

/// <summary>Позиции (роли) в Dota 2.</summary>
public enum Position
{
    Carry = 1,
    Mid = 2,
    Offlane = 3,
    SoftSupport = 4,
    HardSupport = 5
}

public enum PrimaryAttribute { Strength, Agility, Intelligence, Universal }

public enum Language { Russian, English }

public enum Team { Ally, Enemy }

/// <summary>Режим отображения оверлея.</summary>
public enum OverlayMode { Minimal, Expanded, Hidden }

/// <summary>Фазы сборки предметов.</summary>
public enum BuildPhase { Start, Early, Mid, Late, Situational, Alternative }

/// <summary>Уровень уверенности рекомендации. Никаких вымышленных процентов.</summary>
public enum Confidence { Low, Medium, High }

/// <summary>
/// Контекст использования, который пользователь указывает сам.
/// Reference — справочные функции, безопасные в любом матче.
/// Training — тренировочное лобби / матч с ботами / демо: включаются инструменты анализа.
/// </summary>
public enum UsageContext { Reference, Training }

/// <summary>Режим допуска функции.</summary>
public enum FeatureTier
{
    /// <summary>Обычные справочные возможности (не зависят от состояния матча противника).</summary>
    Reference,
    /// <summary>Тренировочные инструменты — только в контексте Training.</summary>
    Training,
    /// <summary>Разрешённость не подтверждена — отключено для публичных матчей всегда.</summary>
    RequiresVerification
}

public enum Feature
{
    HeroCatalog,
    ReferenceBuilds,
    RoleTips,
    ScheduledSupportReminders,
    GsiOwnHeroItems,
    ManualDraftAnalysis,
    AutoDraftRecognition,
    EnemyAwareItemAdvice,
    LiveDraftAdviceInPublicMatch
}

public enum ReasonKind { RoleFit, Counter, Synergy, TeamGap, Stats, Risk, Item, Info }

/// <summary>Как учитывать личный пул героев (избранные) при подборе контрпиков.</summary>
public enum PoolMode { Off, Prefer, Only }

/// <summary>Итог матча в локальной истории.</summary>
public enum MatchResult { Unknown, Win, Loss }
