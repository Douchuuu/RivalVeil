using UnityEngine;
using VContainer;

/// <summary>
/// ЦЕНТРАЛЬНЫЙ КАЛЬКУЛЯТОР БОЯ
///
/// Вся математика урона в одном месте.
/// Оружия вызывают Calculate() — не считают урон сами.
///
/// ЦЕПОЧКА УРОНА (атака по врагу):
///   baseDamage (из WeaponDefinition)
///   × DamageMultiplier (из StatSheet атакующего)
///   → проверка Overcrit (если крит поверх крита)
///   → проверка Crit
///   × AcidDebuff (+7% если цель в кислотной луже)
///   = FinalDamage (мин. 1)
///
/// ЦЕПОЧКА УРОНА (враг бьёт игрока):
///   rawDamage (от EnemyAI)
///   × (1 − DamageReduction)  ← процентное снижение, максимум 90%
///   = reducedDamage (мин. 1)
///   → Щит игрока поглощает до CurrentShieldHP (в PlayerStats.TakeDamage)
///   → остаток идёт в CurrentHealth
///
/// ─── SHIELD vs DamageReduction ───────────────────────────────────────────────
///   Shield (StatType.Armor):
///     Отдельный бар HP. Поглощает урон после DamageReduction.
///     Заполняется при получении Armor-тотема. Не регенерирует.
///     Удары гасятся полностью пока щит > 0.
///
///   DamageReduction (StatType.DamageReduction):
///     Процентное снижение каждого удара (до 90%).
///     Работает всегда, независимо от щита.
///     Гарантирует минимум 1 урона.
///
/// ИСПРАВЛЕНИЕ: Debug.Log убран из горячего пути.
///   Calculate() вызывается при КАЖДОЙ атаке ЛЮБОГО оружия.
///   В продакшене: тысячи вызовов в секунду × Debug.Log (~несколько мс каждый) = лаги.
///   Теперь логи компилируются только в Editor и Development Build.
///
/// ЗАРЕГИСТРИРОВАН в VContainer как Singleton — получай через [Inject].
/// </summary>
public class CombatCalculator
{
    // ─────────────────────────────────────────────────────────────────────────
    // РЕЗУЛЬТАТ УДАРА
    // ─────────────────────────────────────────────────────────────────────────

    public struct HitResult
    {
        public float FinalDamage;
        public bool  IsCrit;
        public bool  IsOvercrit;
        public bool  IsBlocked;

        public override string ToString() =>
            $"DMG:{FinalDamage:F1} " +
            (IsOvercrit ? "[OVERCRIT]" : IsCrit ? "[CRIT]" : "") +
            (IsBlocked  ? "[BLOCKED]"  : "");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОСНОВНОЙ РАСЧЁТ УРОНА (атака по врагу)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Рассчитывает итоговый урон с учётом крита, оверкрита, кислотного дебаффа.
    ///
    /// attacker.StatSheet может быть null (для урона окружения — ловушки, зоны).
    /// target.StatSheet может быть null для врагов без StatSheet (обычные враги).
    /// </summary>
    public HitResult Calculate(
        float baseDamage,
        PlayerStats attacker,
        EnemyHealth target = null)
    {
        var result = new HitResult();

        if (baseDamage <= 0f)
        {
            result.IsBlocked = true;
            return result;
        }

        float damage = baseDamage;
        float dmgMult = attacker?.StatSheet?.GetStat(StatType.DamageMultiplier) ?? 1f;
        
        if (attacker?.StatSheet != null)
            damage *= dmgMult;

        // ЛОГИ УРОНА — только в редакторе и Development Build
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[DMG] Base:{baseDamage:F1} x Mult:{dmgMult:F2} = {damage:F1}");
#endif

        // Один детерминированный бросок для крит-системы
        float seed = Random.value;

        if (attacker?.StatSheet != null)
        {
            float overcritChance = attacker.StatSheet.GetStat(StatType.OvercritChance);
            float critChance     = attacker.StatSheet.GetStat(StatType.CritChance);

            if (overcritChance > 0f && seed < overcritChance)
            {
                float overcritMultiplier = attacker.StatSheet.GetStat(StatType.OvercritMultiplier);
                damage           *= overcritMultiplier;
                result.IsOvercrit = true;
                result.IsCrit     = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[DMG] >>> OVERCRIT! x{overcritMultiplier:F2} = {damage:F1}");
#endif
            }
            else if (seed < overcritChance + critChance)
            {
                float critMultiplier = attacker.StatSheet.GetStat(StatType.CritMultiplier);
                damage        *= critMultiplier;
                result.IsCrit  = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[DMG] >>> CRIT! x{critMultiplier:F2} = {damage:F1}");
#endif
            }
            else
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[DMG] Normal hit (no crit)");
#endif
            }
        }

        // ── КИСЛОТНЫЙ ДЕБАФФ: +7% урона от всех источников ───────────────────
        if (target != null)
        {
            var acidDebuff = target.GetComponent<AcidDebuffComponent>();
            if (acidDebuff != null && acidDebuff.IsActive)
            {
                damage *= AcidDebuffComponent.DAMAGE_BONUS_MULTIPLIER;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[DMG] Acid Debuff! x{AcidDebuffComponent.DAMAGE_BONUS_MULTIPLIER:F2} -> {damage:F1}");
#endif
            }
        }

        result.FinalDamage = Mathf.Max(1f, damage);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        Debug.Log($"[DMG] === FINAL: {result.FinalDamage:F1} ===");
#endif
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАСЧЁТ УРОНА ОТ ВРАГОВ ПО ИГРОКУ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Рассчитывает финальный урон после DamageReduction игрока.
    ///
    /// Возвращает урон который должен поглотить щит или здоровье.
    /// Поглощение щита происходит ПОСЛЕ этого, в PlayerStats.TakeDamage().
    ///
    /// Формула: max(1, rawDamage × (1 − reduction))
    ///   reduction зажата до 90% в PlayerStatSheet.RebuildCache().
    /// </summary>
    public float CalculateIncomingDamage(float rawDamage, PlayerStats defender)
    {
        if (defender?.StatSheet == null) return rawDamage;

        float reduction = defender.StatSheet.GetStat(StatType.DamageReduction);

        // DamageReduction снижает урон, минимум 1
        float reduced = Mathf.Max(1f, rawDamage * (1f - reduction));

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        if (reduction > 0f)
            Debug.Log($"[DMG] DamageReduction {reduction * 100f:F0}%: {rawDamage:F1} -> {reduced:F1}");
#endif

        return reduced;
    }
}
