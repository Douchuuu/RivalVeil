using UnityEngine;

/// <summary>
/// Редкость предмета.
///   Common    → базовые характеристики, шанс выпадения 60%
///   Rare      → улучшенные характеристики, шанс 25%
///   Mythic    → редкие характеристики, шанс 12%
///   Legendary → легендарные характеристики, шанс 3%
/// </summary>
public enum ItemRarity
{
    Common    = 0,
    Rare      = 1,
    Mythic    = 2,
    Legendary = 3
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тип эффекта предмета (как он влияет на характеристики).
/// </summary>
public enum ItemEffectType
{
    // Бинт
    HealthRegeneration,     // Восстановление HP каждые N секунд
    
    // Факел
    AttackSpeedBonus,       // Бонус к скорости атаки
    
    // Талисман
    DamageBonus,            // Процентный бонус к урону
    
    // Академичная шляпа
    ExtraExpOrbChance,      // Шанс получить дополнительный ExpOrb
    
    // Челюсть Вампира
    VampirismOnHit,         // Исцеление при ударе врагам
    
    // Голова Демона
    DamagePerKill,          // Бонус урона за каждое убийство
    
    // Черная дыра
    ExpOrbAttraction,       // Притяжение ExpOrb с карты
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Эффект одного предмета.
/// </summary>
[System.Serializable]
public struct ItemEffect
{
    public ItemEffectType type;
    public float value;
    public float cooldownOrDuration; // Для эффектов с кулдауном или длительностью
    
    public ItemEffect(ItemEffectType t, float v, float cd = 0f)
    {
        type = t;
        value = v;
        cooldownOrDuration = cd;
    }

    /// <summary>Локализованное описание эффекта для UI.</summary>
    public string GetDescription()
    {
        switch (type)
        {
            case ItemEffectType.HealthRegeneration:
                return $"Восстанавливает {value:F0} HP каждые {cooldownOrDuration:F0}сек";
            case ItemEffectType.AttackSpeedBonus:
                return $"+{value:F0}% скорость атаки";
            case ItemEffectType.DamageBonus:
                return $"+{value:F0}% урона ЗА КАЖДЫЙ ОТКРЫТЫЙ СУНДУК!";
            case ItemEffectType.ExtraExpOrbChance:
                return $"Выпадает ОДИН дополнительный ExpOrb с {value:F0}% вероятностью (золотой цвет)";
            case ItemEffectType.VampirismOnHit:
                return $"Исцеляетесь на {value:F1}% при ударе (кулдаун {cooldownOrDuration:F0}сек)";
            case ItemEffectType.DamagePerKill:
                return $"+{value:F2}% урона за каждое убийство (макс 100%)";
            case ItemEffectType.ExpOrbAttraction:
                return $"Притягивает ExpOrb с карты";
            default:
                return $"+{value:F1}";
        }
    }
}
