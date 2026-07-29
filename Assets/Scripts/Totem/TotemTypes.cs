// ═══════════════════════════════════════════════════════════════════════════
// TotemTypes.cs — типы данных системы тотемов
// LevelUpOfferType объявлен в WeaponUpgradeTypes.cs (не здесь)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Тип пассивного бонуса тотема.
///
/// ─── КАК ПРИМЕНЯЕТСЯ В TotemManager ─────────────────────────────────────
///
///   XpGain          → StatType.XpMultiplier         (Additive,       +value/100)
///   Damage          → StatType.DamageMultiplier      (Additive,       +value/100)
///   CritChance      → StatType.CritChance            (Additive,       +value/100)
///   DamageReduction → StatType.DamageReduction       (Additive,       +value/100)
///   Armor           → StatType.Armor                 (Additive,       flat value)
///   MoveSpeed       → StatType.MoveSpeedMultiplier   (Additive,       +value/100)
///                     Читается в PlayerMovement.MovePlayer()
///   PickupRange     → StatType.PickupRange           (Multiplicative, 1+value/100)
///                     Читается в PlayerMovement.CollectItems()
///   MaxHealth       → PlayerStats.AddBonusMaxHealth  (flat = base_hp * value/100)
///                     Особый случай — не через StatSheet
///
/// </summary>
public enum TotemBonusType
{
    XpGain = 0,   // % к получаемому опыту
    MoveSpeed = 1,   // % к скорости движения
    MaxHealth = 2,   // % к максимальному HP
    Damage = 3,   // % к урону
    CritChance = 4,   // % к шансу крита (абсолютный)
    PickupRange = 5,   // % к радиусу подбора
    Armor = 6,   // flat к броне
    DamageReduction = 7,   // % снижения входящего урона
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Предложение тотема на LevelUp-панели.
/// Создаётся в UpgradeGenerator, применяется через TotemManager.
/// </summary>
public class TotemUpgradeOffer
{
    public readonly UpgradeRarity Rarity;
    public readonly string TotemId;
    public readonly string TotemName;
    public readonly TotemBonusType BonusType;
    public readonly float BonusValue; // значение в % (или flat для Armor)

    public TotemUpgradeOffer(
        UpgradeRarity rarity,
        string totemId,
        string totemName,
        TotemBonusType bonusType,
        float bonusValue)
    {
        Rarity = rarity;
        TotemId = totemId;
        TotemName = totemName;
        BonusType = bonusType;
        BonusValue = bonusValue;
    }

    // ── UI ────────────────────────────────────────────────────────────────────

    public string GetRarityLabel()
    {
        switch (Rarity)
        {
            case UpgradeRarity.Common: return "<color=#AAAAAA>ОБЫЧНЫЙ</color>";
            case UpgradeRarity.Rare: return "<color=#4499FF>РЕДКИЙ</color>";
            case UpgradeRarity.Mythic: return "<color=#AA44FF>МИФИЧЕСКИЙ</color>";
            case UpgradeRarity.Legendary: return "<color=#FFB700>ЛЕГЕНДАРНЫЙ</color>";
            default: return "ТОТЕМ";
        }
    }

    public string GetBonusTypeName()
    {
        switch (BonusType)
        {
            case TotemBonusType.XpGain: return "Получаемый опыт";
            case TotemBonusType.MoveSpeed: return "Скорость";
            case TotemBonusType.MaxHealth: return "Макс. здоровье";
            case TotemBonusType.Damage: return "Урон";
            case TotemBonusType.CritChance: return "Шанс крита";
            case TotemBonusType.PickupRange: return "Радиус подбора";
            case TotemBonusType.Armor: return "Броня";
            case TotemBonusType.DamageReduction: return "Снижение урона";
            default: return BonusType.ToString();
        }
    }

    public string GetBonusLabel()
    {
        if (BonusType == TotemBonusType.Armor)
            return $"+{BonusValue:F0}";
        return $"+{BonusValue:F0}%";
    }

    /// <summary>
    /// Текст кнопки LevelUp.
    /// currentAccumulated — сколько уже накоплено у игрока (для апгрейда).
    /// </summary>
    public string GetButtonText(float currentAccumulated = 0f)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🗿 {GetRarityLabel()} тотем: {TotemName}");
        sb.AppendLine(GetBonusTypeName());

        if (currentAccumulated > 0f)
        {
            string cur = BonusType == TotemBonusType.Armor
                ? $"+{currentAccumulated:F0}"
                : $"+{currentAccumulated:F0}%";
            sb.AppendLine($"{cur}  →  {GetBonusLabel()}");
        }
        else
        {
            sb.AppendLine(GetBonusLabel());
        }

        return sb.ToString().TrimEnd();
    }
}