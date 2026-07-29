using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────

public enum UpgradeRarity
{
    Common    = 0,
    Rare      = 1,
    Mythic    = 2,
    Legendary = 3
}

// ─────────────────────────────────────────────────────────────────────────────

public enum StatBonusType
{
    Damage          = 0,
    AttackSpeed     = 1,
    Radius          = 2,
    ProjectileCount = 3,
    TickRate        = 5,
    CritChance      = 6,
    CritMultiplier  = 7,
    ProjectileSpeed = 8,
    Duration        = 9,
    ShieldCharge    = 10,
    ShieldRegen     = 11,
    Piercing        = 12,
}

// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public struct StatBonus
{
    public StatBonusType type;
    public float value;

    public StatBonus(StatBonusType t, float v) { type = t; value = v; }

    public string GetLabel() => $"{GetStatName()}  {GetBonusLabel()}";

    public string GetBonusLabel()
    {
        switch (type)
        {
            case StatBonusType.Damage:          return $"+{value:F0}%";
            case StatBonusType.AttackSpeed:     return $"+{value:F0}%";
            case StatBonusType.Radius:          return $"+{value:F0}%";
            case StatBonusType.ProjectileCount:
                bool isWhole = (value == Mathf.Floor(value));
                return isWhole ? $"+{(int)value}шт" : $"+{value:F1}шт";
            case StatBonusType.TickRate:        return $"+{value:F0}%";
            case StatBonusType.CritChance:      return $"+{value:F0}%";
            case StatBonusType.CritMultiplier:  return $"+{value:F2}x";
            case StatBonusType.ProjectileSpeed: return $"+{value:F0}%";
            case StatBonusType.Duration:        return $"+{value:F0}%";
            case StatBonusType.ShieldCharge:    return $"+{(int)value}шт";
            case StatBonusType.ShieldRegen:     return $"+{value:F0}%";
            case StatBonusType.Piercing:        return $"+{(int)value}шт";
            default:                            return $"+{value:F1}";
        }
    }

    private string GetStatName()
    {
        switch (type)
        {
            case StatBonusType.Damage:          return "Урон";
            case StatBonusType.AttackSpeed:     return "Скор. атаки";
            case StatBonusType.Radius:          return "Радиус";
            case StatBonusType.ProjectileCount: return "Снаряды";
            case StatBonusType.TickRate:        return "Частота тиков";
            case StatBonusType.CritChance:      return "Шанс крита";
            case StatBonusType.CritMultiplier:  return "Крит ×";
            case StatBonusType.ProjectileSpeed: return "Скор. снаряда";
            case StatBonusType.Duration:        return "Длительность";
            case StatBonusType.ShieldCharge:    return "Заряды щита";
            case StatBonusType.ShieldRegen:     return "Реген щита";
            case StatBonusType.Piercing:        return "Пробитие";
            default:                            return type.ToString();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public class WeaponUpgradeOffer
{
    public readonly UpgradeRarity  Rarity;
    public readonly string         TargetWeaponId;
    public readonly string         TargetWeaponName;
    public readonly List<StatBonus> Bonuses;

    public WeaponUpgradeOffer(UpgradeRarity rarity, string targetId, string targetName, List<StatBonus> bonuses)
    {
        Rarity          = rarity;
        TargetWeaponId  = targetId;
        TargetWeaponName = targetName;
        Bonuses         = bonuses;
    }

    public string GetRarityLabel()
    {
        switch (Rarity)
        {
            case UpgradeRarity.Common:    return "<color=#AAAAAA>ОБЫЧНОЕ</color>";
            case UpgradeRarity.Rare:      return "<color=#4499FF>РЕДКОЕ</color>";
            case UpgradeRarity.Mythic:    return "<color=#AA44FF>МИФИЧЕСКОЕ</color>";
            case UpgradeRarity.Legendary: return "<color=#FFB700>ЛЕГЕНДАРНОЕ</color>";
            default:                      return "НЕИЗВЕСТНОЕ";
        }
    }

    public string GetButtonText(GameObject weaponObj = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{GetRarityLabel()} улучшение: {TargetWeaponName}");

        IDynamicUpgradeable upgr = weaponObj?.GetComponent<IDynamicUpgradeable>();
        foreach (var bonus in Bonuses)
        {
            float? cur = upgr?.GetCurrentStatValue(bonus.type);
            if (cur.HasValue)
                sb.AppendLine($"{FormatCurrentValue(bonus.type, cur.Value)}  →  {bonus.GetBonusLabel()}");
            else
                sb.AppendLine(bonus.GetLabel());
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatCurrentValue(StatBonusType stat, float value)
    {
        switch (stat)
        {
            case StatBonusType.Damage:          return $"{value:F1} урон";
            case StatBonusType.AttackSpeed:     return $"{value:F2}с кд";
            case StatBonusType.Radius:          return $"{value:F1} радиус";
            case StatBonusType.ProjectileCount: return $"{value:F0} снар.";
            case StatBonusType.TickRate:        return $"{value:F2}с тик";
            case StatBonusType.CritChance:      return $"{value:F1}% крит";
            case StatBonusType.CritMultiplier:  return $"{value:F2}x крит";
            case StatBonusType.ProjectileSpeed: return $"{value:F1} скор.";
            case StatBonusType.Duration:        return $"{value:F1}с длит.";
            case StatBonusType.ShieldCharge:    return $"{(int)value} заряд";
            case StatBonusType.ShieldRegen:     return $"{value:F2}с реген";
            case StatBonusType.Piercing:        return $"{(int)value} проб.";
            default:                            return $"{value:F1}";
        }
    }

    public void Apply(GameObject weaponObj)
    {
        if (weaponObj == null) return;
        var target = weaponObj.GetComponent<IDynamicUpgradeable>();
        if (target == null)
        {
            Debug.LogWarning($"[WeaponUpgradeOffer] {weaponObj.name} не реализует IDynamicUpgradeable!");
            return;
        }
        target.ApplyDynamicUpgrade(this);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Тип карточки LevelUp.
/// Enum вместо набора bool-флагов — чисто расширяемо.
/// </summary>
public enum LevelUpOfferType
{
    NewWeapon,      // выдать новое оружие
    WeaponUpgrade,  // апгрейд существующего оружия
    NewTotem,       // выдать новый тотем
    TotemUpgrade,   // апгрейд существующего тотема
}

// ─────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Предложение LevelUp. Тип определяется полем Type (enum).
/// Данные для оружий — в WeaponDef / WeaponUpgrade.
/// Данные для тотемов — в TotemDef / TotemUpgrade.
/// </summary>
public class LevelUpOffer
{
    public LevelUpOfferType Type;

    // ── оружие ────────────────────────────────────────────────────────────────
    public WeaponDefinition   WeaponDef;       // NewWeapon
    public WeaponUpgradeOffer WeaponUpgrade;   // WeaponUpgrade

    // ── тотем ─────────────────────────────────────────────────────────────────
    public TotemDefinition   TotemDef;         // NewTotem
    public TotemUpgradeOffer TotemUpgrade;     // NewTotem + TotemUpgrade

    // ── фабрики ───────────────────────────────────────────────────────────────

    public static LevelUpOffer ForNewWeapon(WeaponDefinition def) =>
        new LevelUpOffer { Type = LevelUpOfferType.NewWeapon, WeaponDef = def };

    public static LevelUpOffer ForWeaponUpgrade(WeaponUpgradeOffer offer) =>
        new LevelUpOffer { Type = LevelUpOfferType.WeaponUpgrade, WeaponUpgrade = offer };

    public static LevelUpOffer ForNewTotem(TotemDefinition def, TotemUpgradeOffer offer) =>
        new LevelUpOffer { Type = LevelUpOfferType.NewTotem, TotemDef = def, TotemUpgrade = offer };

    public static LevelUpOffer ForTotemUpgrade(TotemUpgradeOffer offer) =>
        new LevelUpOffer { Type = LevelUpOfferType.TotemUpgrade, TotemUpgrade = offer };

    // ── UI текст ──────────────────────────────────────────────────────────────

    public string GetButtonText(GameObject weaponObj = null, float totemAccumulated = 0f)
    {
        switch (Type)
        {
            case LevelUpOfferType.NewWeapon:
                return $"НОВОЕ ОРУЖИЕ\n{WeaponDef.displayName}";
            case LevelUpOfferType.WeaponUpgrade:
                return WeaponUpgrade.GetButtonText(weaponObj);
            case LevelUpOfferType.NewTotem:
                return TotemUpgrade.GetButtonText(0f);
            case LevelUpOfferType.TotemUpgrade:
                return TotemUpgrade.GetButtonText(totemAccumulated);
            default:
                return "???";
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public interface IDynamicUpgradeable
{
    StatBonusType[] GetAvailableStats();
    void ApplyDynamicUpgrade(WeaponUpgradeOffer offer);
    float? GetCurrentStatValue(StatBonusType stat);
}
