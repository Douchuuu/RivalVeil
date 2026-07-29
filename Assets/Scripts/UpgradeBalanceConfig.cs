using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// UpgradeBalanceConfig — ScriptableObject для балансировки апгрейдов оружий И тотемов.
///
/// НОВЫЕ ПОЛЯ (тотемы):
///   totemRanges     — диапазоны min/max для каждого типа тотема и редкости
///   totemCardChance — вероятность [0..1] что одна карточка LevelUp будет тотемом
///
/// Создать: Assets → ПКМ → Create → Game → Upgrade Balance Config
/// Подключить: LevelUpManager → поле "Upgrade Balance Config"
/// </summary>
[CreateAssetMenu(menuName = "Game/Upgrade Balance Config", fileName = "UpgradeBalanceConfig")]
public class UpgradeBalanceConfig : ScriptableObject
{
    [Header("── Веса выпадения редкостей (сумма = 100) ───────────────────────")]
    [Range(0, 100)] public int commonWeight    = 60;
    [Range(0, 100)] public int rareWeight      = 25;
    [Range(0, 100)] public int mythicWeight    = 12;
    [Range(0, 100)] public int legendaryWeight = 3;

    [Header("── Диапазоны значений по статам и редкостям (ОРУЖИЯ) ────────────")]
    public List<StatRangeEntry> statRanges = new List<StatRangeEntry>
    {
        new StatRangeEntry { statType = StatBonusType.Damage,
            unit = "%",
            common    = new RarityRange { min = 3f,    max = 6f    },
            rare      = new RarityRange { min = 7f,    max = 12f   },
            mythic    = new RarityRange { min = 13f,   max = 18f   },
            legendary = new RarityRange { min = 19f,   max = 23f   } },

        new StatRangeEntry { statType = StatBonusType.AttackSpeed,
            unit = "%",
            common    = new RarityRange { min = 5f,    max = 10f   },
            rare      = new RarityRange { min = 11f,   max = 18f   },
            mythic    = new RarityRange { min = 19f,   max = 28f   },
            legendary = new RarityRange { min = 30f,   max = 40f   } },

        new StatRangeEntry { statType = StatBonusType.Radius,
            unit = "%",
            common    = new RarityRange { min = 6f,    max = 12f   },
            rare      = new RarityRange { min = 13f,   max = 22f   },
            mythic    = new RarityRange { min = 23f,   max = 35f   },
            legendary = new RarityRange { min = 36f,   max = 50f   } },

        new StatRangeEntry { statType = StatBonusType.ProjectileCount,
            unit = "шт",
            common    = new RarityRange { min = 0.5f,  max = 1f    },
            rare      = new RarityRange { min = 1f,    max = 1.5f  },
            mythic    = new RarityRange { min = 1.5f,  max = 2f    },
            legendary = new RarityRange { min = 2f,    max = 3f    } },

        new StatRangeEntry { statType = StatBonusType.TickRate,
            unit = "%",
            common    = new RarityRange { min = 8f,    max = 14f   },
            rare      = new RarityRange { min = 15f,   max = 24f   },
            mythic    = new RarityRange { min = 25f,   max = 36f   },
            legendary = new RarityRange { min = 38f,   max = 50f   } },

        new StatRangeEntry { statType = StatBonusType.CritChance,
            unit = "%",
            common    = new RarityRange { min = 3f,    max = 6f    },
            rare      = new RarityRange { min = 7f,    max = 12f   },
            mythic    = new RarityRange { min = 13f,   max = 20f   },
            legendary = new RarityRange { min = 21f,   max = 28f   } },

        new StatRangeEntry { statType = StatBonusType.CritMultiplier,
            unit = "x",
            common    = new RarityRange { min = 0.15f, max = 0.30f },
            rare      = new RarityRange { min = 0.30f, max = 0.55f },
            mythic    = new RarityRange { min = 0.55f, max = 0.85f },
            legendary = new RarityRange { min = 0.85f, max = 1.20f } },

        new StatRangeEntry { statType = StatBonusType.ProjectileSpeed,
            unit = "%",
            common    = new RarityRange { min = 6f,    max = 12f   },
            rare      = new RarityRange { min = 13f,   max = 22f   },
            mythic    = new RarityRange { min = 23f,   max = 35f   },
            legendary = new RarityRange { min = 36f,   max = 50f   } },

        new StatRangeEntry { statType = StatBonusType.Duration,
            unit = "%",
            common    = new RarityRange { min = 6f,    max = 12f   },
            rare      = new RarityRange { min = 13f,   max = 22f   },
            mythic    = new RarityRange { min = 23f,   max = 35f   },
            legendary = new RarityRange { min = 36f,   max = 50f   } },

        new StatRangeEntry { statType = StatBonusType.ShieldCharge,
            unit = "шт",
            common    = new RarityRange { min = 1f,    max = 1f    },
            rare      = new RarityRange { min = 1f,    max = 2f    },
            mythic    = new RarityRange { min = 2f,    max = 3f    },
            legendary = new RarityRange { min = 3f,    max = 5f    } },

        new StatRangeEntry { statType = StatBonusType.ShieldRegen,
            unit = "%",
            common    = new RarityRange { min = 8f,    max = 15f   },
            rare      = new RarityRange { min = 16f,   max = 28f   },
            mythic    = new RarityRange { min = 29f,   max = 44f   },
            legendary = new RarityRange { min = 45f,   max = 60f   } },

        new StatRangeEntry { statType = StatBonusType.Piercing,
            unit = "шт",
            common    = new RarityRange { min = 1f,    max = 1f    },
            rare      = new RarityRange { min = 1f,    max = 2f    },
            mythic    = new RarityRange { min = 2f,    max = 3f    },
            legendary = new RarityRange { min = 3f,    max = 4f    } },
    };

    [Header("── Переопределения для конкретных оружий ────────────────────────")]
    public List<WeaponUpgradeOverride> weaponOverrides = new List<WeaponUpgradeOverride>();

    // =========================================================================
    // ТОТЕМЫ
    // =========================================================================

    [Header("── Тотемы: шанс появления карточки ───────────────────────────────")]
    [Tooltip("Вероятность [0..1] что КАЖДЫЙ слот LevelUp станет тотемом.\n" +
             "0.5 = тотемы выпадают с той же вероятностью, что и оружия (рекомендуется).\n" +
             "0.0 = тотемы не выпадают никогда.\n" +
             "1.0 = все слоты всегда тотемы.")]
    [Range(0f, 1f)]
    public float totemCardChance = 0.5f;

    [Header("── Диапазоны значений тотемов по редкостям ────────────────────────")]
    [Tooltip("Значения в % (плоское значение для Armor).\n\n" +
             "XpGain: Common +2-5%, Rare +6-10%, Mythic +11-15%, Legendary +16-25%")]
    public List<TotemRangeEntry> totemRanges = new List<TotemRangeEntry>
    {
        new TotemRangeEntry { bonusType = TotemBonusType.XpGain,
            common    = new RarityRange { min = 2f,  max = 5f  },
            rare      = new RarityRange { min = 6f,  max = 10f },
            mythic    = new RarityRange { min = 11f, max = 15f },
            legendary = new RarityRange { min = 16f, max = 25f } },

        new TotemRangeEntry { bonusType = TotemBonusType.MoveSpeed,
            common    = new RarityRange { min = 2f,  max = 5f  },
            rare      = new RarityRange { min = 6f,  max = 10f },
            mythic    = new RarityRange { min = 11f, max = 15f },
            legendary = new RarityRange { min = 16f, max = 25f } },

        new TotemRangeEntry { bonusType = TotemBonusType.MaxHealth,
            common    = new RarityRange { min = 3f,  max = 6f  },
            rare      = new RarityRange { min = 7f,  max = 12f },
            mythic    = new RarityRange { min = 13f, max = 18f },
            legendary = new RarityRange { min = 19f, max = 28f } },

        new TotemRangeEntry { bonusType = TotemBonusType.Damage,
            common    = new RarityRange { min = 2f,  max = 4f  },
            rare      = new RarityRange { min = 5f,  max = 8f  },
            mythic    = new RarityRange { min = 9f,  max = 12f },
            legendary = new RarityRange { min = 13f, max = 18f } },

        new TotemRangeEntry { bonusType = TotemBonusType.CritChance,
            common    = new RarityRange { min = 2f,  max = 4f  },
            rare      = new RarityRange { min = 5f,  max = 8f  },
            mythic    = new RarityRange { min = 9f,  max = 12f },
            legendary = new RarityRange { min = 13f, max = 18f } },

        new TotemRangeEntry { bonusType = TotemBonusType.PickupRange,
            common    = new RarityRange { min = 5f,  max = 10f },
            rare      = new RarityRange { min = 11f, max = 18f },
            mythic    = new RarityRange { min = 19f, max = 28f },
            legendary = new RarityRange { min = 29f, max = 40f } },

        new TotemRangeEntry { bonusType = TotemBonusType.Armor,
            common    = new RarityRange { min = 2f,  max = 4f  },
            rare      = new RarityRange { min = 5f,  max = 8f  },
            mythic    = new RarityRange { min = 9f,  max = 14f },
            legendary = new RarityRange { min = 15f, max = 22f } },

        new TotemRangeEntry { bonusType = TotemBonusType.DamageReduction,
            common    = new RarityRange { min = 1f,  max = 3f  },
            rare      = new RarityRange { min = 4f,  max = 6f  },
            mythic    = new RarityRange { min = 7f,  max = 9f  },
            legendary = new RarityRange { min = 10f, max = 14f } },
    };

    // ─────────────────────────────────────────────────────────────────────────
    // API — ОРУЖИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public bool IsStatDisabled(StatBonusType stat, string weaponId)
    {
        if (string.IsNullOrEmpty(weaponId)) return false;
        foreach (var ov in weaponOverrides)
        {
            if (ov.weaponId != weaponId) continue;
            foreach (var se in ov.statOverrides)
                if (se.statType == stat) return se.disabled;
            break;
        }
        return false;
    }

    public RarityRange GetRange(StatBonusType stat, UpgradeRarity rarity, string weaponId = null)
    {
        if (!string.IsNullOrEmpty(weaponId))
        {
            foreach (var ov in weaponOverrides)
            {
                if (ov.weaponId != weaponId) continue;
                foreach (var se in ov.statOverrides)
                {
                    if (se.statType != stat) continue;
                    if (se.overrideRanges) return se.GetRange(rarity);
                    break;
                }
                break;
            }
        }
        foreach (var e in statRanges)
            if (e.statType == stat) return e.GetRange(rarity);
        return new RarityRange { min = 5f, max = 10f };
    }

    public float GetFinalValue(StatBonusType stat, UpgradeRarity rarity, System.Random rng, string weaponId = null)
    {
        var range = GetRange(stat, rarity, weaponId);
        float raw = range.min + (float)rng.NextDouble() * (range.max - range.min);
        return RoundValue(stat, raw);
    }

    public int[] GetRarityWeights() =>
        new[] { commonWeight, rareWeight, mythicWeight, legendaryWeight };

    public string GetUnit(StatBonusType stat)
    {
        foreach (var e in statRanges)
            if (e.statType == stat) return e.unit;
        return "%";
    }

    public float RoundValuePublic(StatBonusType stat, float raw) => RoundValue(stat, raw);

    public float RoundValue(StatBonusType stat, float raw)
    {
        if (stat == StatBonusType.ProjectileCount) return Mathf.Max(0.5f, Mathf.Round(raw * 2f) / 2f);
        if (stat == StatBonusType.ShieldCharge)    return Mathf.Max(1f, Mathf.Round(raw));
        if (stat == StatBonusType.CritMultiplier)  return Mathf.Round(raw * 100f) / 100f;
        return Mathf.Round(raw * 10f) / 10f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // API — ТОТЕМЫ
    // ─────────────────────────────────────────────────────────────────────────

    public RarityRange GetTotemRange(TotemBonusType bonusType, UpgradeRarity rarity)
    {
        foreach (var e in totemRanges)
            if (e.bonusType == bonusType) return e.GetRange(rarity);
        return new RarityRange { min = 2f, max = 5f };
    }

    public float GetTotemFinalValue(TotemBonusType bonusType, UpgradeRarity rarity, System.Random rng)
    {
        var range = GetTotemRange(bonusType, rarity);
        float raw = range.min + (float)rng.NextDouble() * (range.max - range.min);
        return Mathf.Round(raw * 10f) / 10f;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void OnValidate()
    {
        int total = commonWeight + rareWeight + mythicWeight + legendaryWeight;
        if (total != 100)
            Debug.LogWarning($"[UpgradeBalanceConfig] Сумма весов = {total}, должна быть 100!", this);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class RarityRange
{
    public float min;
    public float max;
}

[System.Serializable]
public class StatRangeEntry
{
    public StatBonusType statType;
    public string unit = "%";
    public RarityRange common    = new RarityRange { min = 3f,  max = 6f  };
    public RarityRange rare      = new RarityRange { min = 7f,  max = 12f };
    public RarityRange mythic    = new RarityRange { min = 13f, max = 18f };
    public RarityRange legendary = new RarityRange { min = 19f, max = 23f };

    public RarityRange GetRange(UpgradeRarity rarity)
    {
        switch (rarity)
        {
            case UpgradeRarity.Common:    return common;
            case UpgradeRarity.Rare:      return rare;
            case UpgradeRarity.Mythic:    return mythic;
            case UpgradeRarity.Legendary: return legendary;
            default:                      return common;
        }
    }
}

[System.Serializable]
public class TotemRangeEntry
{
    public TotemBonusType bonusType;
    public RarityRange common    = new RarityRange { min = 2f,  max = 5f  };
    public RarityRange rare      = new RarityRange { min = 6f,  max = 10f };
    public RarityRange mythic    = new RarityRange { min = 11f, max = 15f };
    public RarityRange legendary = new RarityRange { min = 16f, max = 25f };

    public RarityRange GetRange(UpgradeRarity rarity)
    {
        switch (rarity)
        {
            case UpgradeRarity.Common:    return common;
            case UpgradeRarity.Rare:      return rare;
            case UpgradeRarity.Mythic:    return mythic;
            case UpgradeRarity.Legendary: return legendary;
            default:                      return common;
        }
    }
}

[System.Serializable]
public class WeaponUpgradeOverride
{
    public string weaponId;
    public string displayName;
    public List<StatOverrideEntry> statOverrides = new List<StatOverrideEntry>();
}

[System.Serializable]
public class StatOverrideEntry
{
    public StatBonusType statType;
    public bool disabled     = false;
    public bool overrideRanges = false;
    public RarityRange common    = new RarityRange { min = 3f,  max = 6f  };
    public RarityRange rare      = new RarityRange { min = 7f,  max = 12f };
    public RarityRange mythic    = new RarityRange { min = 13f, max = 18f };
    public RarityRange legendary = new RarityRange { min = 19f, max = 23f };

    public RarityRange GetRange(UpgradeRarity rarity)
    {
        switch (rarity)
        {
            case UpgradeRarity.Common:    return common;
            case UpgradeRarity.Rare:      return rare;
            case UpgradeRarity.Mythic:    return mythic;
            case UpgradeRarity.Legendary: return legendary;
            default:                      return common;
        }
    }
}
