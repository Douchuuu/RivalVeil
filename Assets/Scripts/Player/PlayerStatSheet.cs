using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// PlayerStatSheet — "живые" характеристики игрока во время игры.
///
/// ════════════════════════════════════════════════════════════════════
/// ЧТО ЭТО И ЗАЧЕМ
/// ════════════════════════════════════════════════════════════════════
///
/// StatSheet хранит ИТОГОВЫЕ характеристики = база + модификаторы.
/// Это чистая математика без Unity, без MonoBehaviour.
///
/// Пример жизненного цикла:
///   1. Игра стартует → CharacterMultipliers.InitializeStatSheet(sheet)
///      → sheet.SetBase(DamageMultiplier, 1.8f)  ← Маг
///   2. Игрок берёт предмет "Талисман" (+3% урона за сундук):
///      → sheet.AddModifier(DamageMultiplier, +0.03, Additive, "talisman_1")
///   3. Игрок открывает сундук ещё раз:
///      → sheet.AddModifier(DamageMultiplier, +0.03, Additive, "talisman_1")
///   4. sheet.GetStat(DamageMultiplier) = 1.8 + 0.03 + 0.03 = 1.86
///   5. Оружие наносит: 20 × 1.86 = 37.2 урона ✓
///
/// ФОРМУЛА:
///   GetStat(X) = (baseValue[X] + сумма_Additive[X]) × произведение_Multiplicative[X]
///
/// КЭШ:
///   Пересчёт только когда _isDirty = true (при добавлении/удалении модификатора).
///   GetStat вызывается каждый кадр из оружий → кэш критически важен.
/// </summary>
public class PlayerStatSheet
{
    // ─────────────────────────────────────────────────────────────────────────
    // БАЗОВЫЕ ЗНАЧЕНИЯ
    // Устанавливаются CharacterMultipliers.InitializeStatSheet().
    // Сначала = дефолты Inspector на Player Prefab.
    // После выбора персонажа = значения из CharacterDefinition.
    // ─────────────────────────────────────────────────────────────────────────
    private readonly Dictionary<StatType, float> _baseValues = new Dictionary<StatType, float>
    {
        // ── Здоровье ────────────────────────────────────────────────────────
        { StatType.MaxHealth,           100f },   // HP по умолчанию

        // ── Движение ────────────────────────────────────────────────────────
        { StatType.MoveSpeed,           8f   },
        { StatType.PickupRange,         5f   },
        { StatType.MoveSpeedMultiplier, 1.0f },   // 1.0 = стандартная скорость
        { StatType.ExtraJumps,          0f   },   // 0 = только с земли (1 прыжок)
        { StatType.JumpForceMultiplier, 1.0f },   // 1.0 = стандартная высота прыжка

        // ── Урон ────────────────────────────────────────────────────────────
        { StatType.DamageMultiplier,    1.0f },   // 1.0 = без бонуса
        { StatType.AttackSpeed,         1.0f },   // 1.0 = стандартная скорость атаки

        // ── Критические удары ───────────────────────────────────────────────
        { StatType.CritChance,          0.0f },   // 0% по умолчанию
        { StatType.CritMultiplier,      2.0f },   // ×2 при крите
        { StatType.OvercritChance,      0.0f },
        { StatType.OvercritMultiplier,  3.5f },

        // ── Снаряды ─────────────────────────────────────────────────────────
        { StatType.ProjectileCount,     1f   },
        { StatType.ProjectileSpread,    0f   },
        { StatType.PiercingCount,       0f   },

        // ── Защита ──────────────────────────────────────────────────────────
        { StatType.Armor,               0f   },
        { StatType.DamageReduction,     0.0f },

        // ── Опыт ────────────────────────────────────────────────────────────
        { StatType.XpMultiplier,        1.0f },

        // ── Глобальные множители оружий ──────────────────────────────────────
        // Все = 1.0 (нейтральный множитель) — если персонаж не меняет их.
        // CharacterMultipliers.InitializeStatSheet() перезаписывает их
        // значениями из CharacterDefinition.
        { StatType.AttackRadiusMultiplier,    1.0f },
        { StatType.ProjectileSpeedMultiplier, 1.0f },
        { StatType.DurationMultiplier,        1.0f },
    };

    private readonly List<StatModifier> _modifiers = new List<StatModifier>(32);

    // Кэш вычисленных значений — сбрасывается при изменении модификаторов
    private readonly Dictionary<StatType, float> _cache = new Dictionary<StatType, float>();
    private bool _isDirty = true;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Устанавливает базовое значение стата.
    /// Вызывается из CharacterMultipliers.InitializeStatSheet().
    /// Должно вызываться ДО старта игры (не во время боя).
    /// </summary>
    public void SetBase(StatType stat, float value)
    {
        _baseValues[stat] = value;
        _isDirty = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОЛУЧЕНИЕ ЗНАЧЕНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public float GetStat(StatType stat)
    {
        if (_isDirty)
        {
            RebuildCache();
            _isDirty = false;
        }
        return _cache.TryGetValue(stat, out float val) ? val : GetBase(stat);
    }

    // Целочисленная версия для ProjectileCount, ExtraJumps и т.д.
    public int GetStatInt(StatType stat) => Mathf.RoundToInt(GetStat(stat));

    private float GetBase(StatType stat) =>
        _baseValues.TryGetValue(stat, out float v) ? v : 0f;

    // ─────────────────────────────────────────────────────────────────────────
    // МОДИФИКАТОРЫ
    // ─────────────────────────────────────────────────────────────────────────

    public void AddModifier(StatModifier modifier)
    {
        _modifiers.Add(modifier);
        _isDirty = true;
        Debug.Log($"[StatSheet] +{modifier.ModifierType} {modifier.StatType} {modifier.Value:+0.##;-0.##} from '{modifier.Source}'");
    }

    public void RemoveModifiersFromSource(string source)
    {
        int removed = _modifiers.RemoveAll(m => m.Source == source);
        if (removed > 0) _isDirty = true;
    }

    public void RemoveModifier(StatModifier modifier)
    {
        if (_modifiers.Remove(modifier)) _isDirty = true;
    }

    public void ClearAllModifiers()
    {
        _modifiers.Clear();
        _isDirty = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПЕРЕСЧЁТ КЭША
    // ─────────────────────────────────────────────────────────────────────────

    // Overflow крит.шанса конвертируется в бонус крит.урона.
    // 10% overflow → sqrt(0.10) × 0.5 = +0.158x к CritMultiplier
    private const float CRIT_OVERFLOW_RATE = 0.5f;

    private void RebuildCache()
    {
        _cache.Clear();

        float rawCritChance = 0f;

        foreach (StatType stat in System.Enum.GetValues(typeof(StatType)))
        {
            float baseVal = GetBase(stat);
            float additive = 0f;
            float multiplicative = 1f;

            foreach (var mod in _modifiers)
            {
                if (mod.StatType != stat) continue;
                if (mod.ModifierType == ModifierType.Additive)       additive       += mod.Value;
                if (mod.ModifierType == ModifierType.Multiplicative) multiplicative *= mod.Value;
            }

            float final = (baseVal + additive) * multiplicative;

            if (stat == StatType.CritChance)
            {
                rawCritChance = final;
                _cache[stat]  = Mathf.Clamp01(final);
                continue;
            }

            // Зажимы для конкретных статов
            final = stat switch
            {
                StatType.OvercritChance  => Mathf.Clamp01(final),
                StatType.DamageReduction => Mathf.Clamp(final, 0f, 0.9f), // макс 90%
                StatType.ProjectileCount => Mathf.Max(1f, final),
                StatType.AttackSpeed     => Mathf.Max(0.1f, final),
                StatType.Armor           => Mathf.Max(0f, final),
                StatType.ExtraJumps      => Mathf.Max(0f, final),
                StatType.JumpForceMultiplier   => Mathf.Max(0.1f, final),
                StatType.MoveSpeedMultiplier   => Mathf.Max(0.1f, final),
                StatType.AttackRadiusMultiplier    => Mathf.Max(0.1f, final),
                StatType.ProjectileSpeedMultiplier => Mathf.Max(0.1f, final),
                StatType.DurationMultiplier        => Mathf.Max(0.1f, final),
                _ => final
            };

            _cache[stat] = final;
        }

        // Overflow крит.шанса → бонус крит.урона
        float overflow = Mathf.Max(0f, rawCritChance - 1f);
        if (overflow > 0f)
        {
            float overflowBonus = Mathf.Sqrt(overflow) * CRIT_OVERFLOW_RATE;
            _cache[StatType.CritMultiplier] = _cache[StatType.CritMultiplier] + overflowBonus;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[StatSheet] CritOverflow: raw={rawCritChance * 100f:F1}% " +
                      $"overflow={overflow * 100f:F1}% → +{overflowBonus:F3}x CritMult");
#endif
        }
    }

    /// <summary>
    /// Возвращает RAW крит.шанс до зажима (может быть > 1.0).
    /// Для отображения overflow в UI.
    /// </summary>
    public float GetRawCritChance()
    {
        if (_isDirty) { RebuildCache(); _isDirty = false; }
        float baseVal = GetBase(StatType.CritChance);
        float additive = 0f;
        float mult = 1f;
        foreach (var mod in _modifiers)
        {
            if (mod.StatType != StatType.CritChance) continue;
            if (mod.ModifierType == ModifierType.Additive)       additive += mod.Value;
            if (mod.ModifierType == ModifierType.Multiplicative) mult     *= mod.Value;
        }
        return (baseVal + additive) * mult;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public enum ModifierType
{
    /// Добавляется к базовому значению: base + modifier.value
    /// Пример: critChance 5% + Additive(0.05) = 10%
    Additive,

    /// Умножает итоговое значение: (base + additives) × modifier.value
    /// Пример: damage (base 1.0 + additive 0.3) × Multiplicative(1.5) = 1.95
    Multiplicative,
}

public class StatModifier
{
    public StatType StatType { get; }
    public float Value { get; }
    public ModifierType ModifierType { get; }
    public string Source { get; } // ID предмета/тотема/оружия для удаления

    public StatModifier(StatType stat, float value, ModifierType type, string source = "unknown")
    {
        StatType     = stat;
        Value        = value;
        ModifierType = type;
        Source       = source;
    }
}
