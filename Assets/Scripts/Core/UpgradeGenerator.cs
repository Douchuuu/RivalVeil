using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// UpgradeGenerator — генератор предложений LevelUp (оружия + тотемы).
///
/// ══════════════════════════════════════════════════════════════
/// ИЗМЕНЕНИЯ:
/// ══════════════════════════════════════════════════════════════
///
///   1. totemCardChance теперь реально используется (предыдущий фикс).
///
///   2. Максовые оружия/тотемы исключаются из пула:
///        • Оружие >= MAX_WEAPON_LEVEL (50) → не попадает в weaponBag
///          (нельзя ни получить как новое, ни апгрейднуть)
///        • Тотем >= MAX_TOTEM_LEVEL (100)  → не попадает в totemBag
///          (нельзя ни получить как новый, ни апгрейднуть)
///        • Если все оружия и тотемы максовые → выдаётся пустое предложение
///          (или оружие-заглушка "ЗОЛОТО")
///
///   3. Логика проверки: weaponManager.IsWeaponMaxLevel(id) и
///      totemManager.IsTotemMaxLevel(id).
/// ══════════════════════════════════════════════════════════════
/// </summary>
public static class UpgradeGenerator
{
    private static readonly int[]   _defaultWeights = { 60, 25, 12, 3 };
    private static readonly float[,] _defaultRanges =
    {
        {  3f,  6f }, // Common
        {  7f, 12f }, // Rare
        { 13f, 18f }, // Mythic
        { 19f, 23f }, // Legendary
    };

    private const float DEFAULT_TOTEM_CHANCE = 0.5f;

    // ─────────────────────────────────────────────────────────────────────────

    public static List<LevelUpOffer> GenerateOffers(
        System.Random rarityRng,
        System.Random personalRng,
        IReadOnlyList<WeaponDefinition> allWeapons,
        WeaponManager weaponManager,
        int count = 3,
        UpgradeBalanceConfig balanceConfig = null,
        IReadOnlyList<TotemDefinition> allTotems = null,
        TotemManager totemManager = null)
    {
        var offers = new List<LevelUpOffer>(count);

        if (allWeapons == null || allWeapons.Count == 0)
        {
            Debug.LogError("[UpgradeGenerator] allWeapons пуст!");
            return offers;
        }

        // ── Оружейные данные ─────────────────────────────────────────────────

        // Новые оружия — те которых нет у игрока И слот свободен
        var newWeaponCandidates = new List<WeaponDefinition>();
        foreach (var def in allWeapons)
            if (!weaponManager.HasWeapon(def.weaponId))
                newWeaponCandidates.Add(def);

        bool canAddWeapon = newWeaponCandidates.Count > 0
                         && weaponManager.activeWeapons.Count < weaponManager.maxSlots;
        bool hasWeapons  = weaponManager.activeWeapons.Count > 0;

        // ИЗМЕНЕНИЕ: в weaponBag добавляем только НЕ максовые оружия
        // Максовые оружия (>= MAX_WEAPON_LEVEL) исключаются из апгрейдов
        var upgradableWeaponIndices = new List<int>();
        for (int i = 0; i < weaponManager.activeWeapons.Count; i++)
        {
            var w = weaponManager.activeWeapons[i];
            if (w != null && !weaponManager.IsWeaponMaxLevel(w.name))
                upgradableWeaponIndices.Add(i);
        }
        int upgradableWeaponCount = upgradableWeaponIndices.Count;

        // Если все оружия максовые — нельзя ни добавить новое ни апгрейднуть
        bool hasUpgradableWeapons = upgradableWeaponCount > 0;

        var weaponBag = new Queue<int>(ShuffleList(personalRng, upgradableWeaponIndices));

        // ── Тотемные данные ───────────────────────────────────────────────────

        bool totemsAvailable = allTotems != null && allTotems.Count > 0 && totemManager != null;

        var newTotemCandidates = new List<TotemDefinition>();
        IReadOnlyList<TotemDefinition> ownedTotems = null;

        if (totemsAvailable)
        {
            foreach (var def in allTotems)
                if (!totemManager.HasTotem(def.totemId))
                    newTotemCandidates.Add(def);
            ownedTotems = totemManager.ActiveTotems;
        }

        bool canAddTotem = totemsAvailable && newTotemCandidates.Count > 0 && totemManager.HasFreeSlot;

        // ИЗМЕНЕНИЕ: в totemBag добавляем только НЕ максовые тотемы
        var upgradableTotemIndices = new List<int>();
        if (ownedTotems != null)
        {
            for (int i = 0; i < ownedTotems.Count; i++)
            {
                var t = ownedTotems[i];
                if (t != null && !totemManager.IsTotemMaxLevel(t.totemId))
                    upgradableTotemIndices.Add(i);
            }
        }
        bool canUpgradeTotem = upgradableTotemIndices.Count > 0;
        bool totemPossible   = canAddTotem || canUpgradeTotem;

        var totemBag = new Queue<int>(ShuffleList(personalRng, upgradableTotemIndices));

        // ── Генерация карточек ────────────────────────────────────────────────
        //
        // ИЗМЕНЕНИЕ: убран pre-roll одного слота (totemSlot = 0 с шансом 33%).
        // Теперь каждый слот независимо решает: тотем или оружие.
        // perSlotTotemChance = 0.5f → тотемы выпадают с той же вероятностью,
        // что и оружия. Значение настраивается через totemCardChance в конфиге.
        float perSlotTotemChance = balanceConfig != null
            ? balanceConfig.totemCardChance
            : DEFAULT_TOTEM_CHANCE;

        for (int i = 0; i < count; i++)
        {
            UpgradeRarity rarity = RollRarity(rarityRng, balanceConfig);

            // Решаем per-slot: тотем или оружие?
            // Если доступны оба — бросаем монету с perSlotTotemChance.
            // Если доступны только тотемы — всегда тотем.
            // Если доступны только оружия — всегда оружие.
            bool tryTotem;
            if (totemPossible && (canAddWeapon || hasUpgradableWeapons))
                tryTotem = rarityRng.NextDouble() < perSlotTotemChance;
            else
                tryTotem = totemPossible;

            if (tryTotem)
            {
                var totemOffer = GenerateTotemOffer(
                    personalRng, rarity,
                    newTotemCandidates, ownedTotems, totemBag,
                    totemManager, balanceConfig,
                    canAddTotem, canUpgradeTotem);

                if (totemOffer != null)
                {
                    Debug.Log($"[UpgradeGenerator] 🗿 Слот {i}: тотем (шанс {perSlotTotemChance * 100f:F0}% per-slot)");
                    offers.Add(totemOffer);
                    continue;
                }
                // Тотем не сгенерировался (bag пуст, новых нет) — откат на оружие
            }

            // Если нет ни новых ни апгрейдных оружий — заглушка
            if (!canAddWeapon && !hasUpgradableWeapons)
            {
                var stub = new WeaponUpgradeOffer(
                    UpgradeRarity.Common, "gold", "ЗОЛОТО",
                    new List<StatBonus> { new StatBonus(StatBonusType.Damage, 0) });
                offers.Add(LevelUpOffer.ForWeaponUpgrade(stub));
                continue;
            }

            offers.Add(GenerateWeaponOffer(
                personalRng, rarity,
                newWeaponCandidates, weaponManager, ref weaponBag,
                canAddWeapon, hasWeapons, upgradableWeaponCount, balanceConfig,
                upgradableWeaponIndices));
        }

        return offers;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ТОТЕМ
    // ─────────────────────────────────────────────────────────────────────────

    private static LevelUpOffer GenerateTotemOffer(
        System.Random personalRng,
        UpgradeRarity rarity,
        List<TotemDefinition> newCandidates,
        IReadOnlyList<TotemDefinition> ownedTotems,
        Queue<int> totemBag,
        TotemManager totemManager,
        UpgradeBalanceConfig config,
        bool canAddNew,
        bool canUpgrade)
    {
        // Решаем: новый тотем или апгрейд
        bool offerNew = canAddNew && (!canUpgrade || personalRng.NextDouble() < 0.40);

        if (offerNew)
        {
            int idx = personalRng.Next(0, newCandidates.Count);
            var def = newCandidates[idx];
            float val = GetTotemValue(def.bonusType, rarity, personalRng, config);
            var offer = new TotemUpgradeOffer(rarity, def.totemId, def.displayName, def.bonusType, val);
            return LevelUpOffer.ForNewTotem(def, offer);
        }

        if (canUpgrade)
        {
            // Перезаполняем bag если пуст (как weaponBag) —
            // иначе при 3+ тотем-слотах bag иссякнет и тотем не выпадет.
            if (totemBag.Count == 0)
            {
                var refillList = new List<int>();
                if (ownedTotems != null)
                    for (int k = 0; k < ownedTotems.Count; k++)
                    {
                        var t = ownedTotems[k];
                        if (t != null && !totemManager.IsTotemMaxLevel(t.totemId))
                            refillList.Add(k);
                    }
                if (refillList.Count == 0) return null;
                // Перемешиваем (personalRng недоступен здесь, используем простой сдвиг)
                for (int k = refillList.Count - 1; k > 0; k--)
                {
                    int j = (int)(System.DateTime.Now.Ticks % (k + 1));
                    (refillList[k], refillList[j]) = (refillList[j], refillList[k]);
                }
                foreach (var idx in refillList) totemBag.Enqueue(idx);
            }

            int slotIdx = totemBag.Dequeue();
            var def = ownedTotems[slotIdx];
            float val = GetTotemValue(def.bonusType, rarity, personalRng, config);
            var offer = new TotemUpgradeOffer(rarity, def.totemId, def.displayName, def.bonusType, val);
            return LevelUpOffer.ForTotemUpgrade(offer);
        }

        return null;
    }

    private static float GetTotemValue(
        TotemBonusType bonusType, UpgradeRarity rarity,
        System.Random rng, UpgradeBalanceConfig config)
    {
        if (config != null) return config.GetTotemFinalValue(bonusType, rarity, rng);

        float[] defaults = { 3f, 7f, 12f, 18f };
        float b = defaults[(int)rarity];
        float raw = b + (float)(rng.NextDouble() * 2 - 1) * b * 0.3f;
        return Mathf.Round(Mathf.Max(1f, raw) * 10f) / 10f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОРУЖИЕ
    // ─────────────────────────────────────────────────────────────────────────

    private static LevelUpOffer GenerateWeaponOffer(
        System.Random personalRng,
        UpgradeRarity rarity,
        List<WeaponDefinition> newWeaponCandidates,
        WeaponManager weaponManager,
        ref Queue<int> weaponBag,
        bool canAddNew,
        bool hasWeapons,
        int upgradableCount,
        UpgradeBalanceConfig balanceConfig,
        List<int> upgradableIndices)
    {
        bool offerNew = canAddNew && (upgradableCount == 0 || personalRng.NextDouble() < 0.40);

        if (offerNew && newWeaponCandidates.Count > 0)
        {
            int idx = personalRng.Next(0, newWeaponCandidates.Count);
            return LevelUpOffer.ForNewWeapon(newWeaponCandidates[idx]);
        }

        if (upgradableCount > 0)
        {
            // Перезаполняем bag если пуст
            if (weaponBag.Count == 0)
                weaponBag = new Queue<int>(ShuffleList(personalRng, upgradableIndices));

            int realSlotIdx = weaponBag.Dequeue();
            var weaponObj   = weaponManager.activeWeapons[realSlotIdx];
            var dynUpgr     = weaponObj?.GetComponent<IDynamicUpgradeable>();

            StatBonusType[] availStats = dynUpgr != null
                ? dynUpgr.GetAvailableStats()
                : new[] { StatBonusType.Damage };

            var def   = weaponManager.GetWeaponDefinition(weaponObj);
            string name = def != null ? def.displayName : weaponObj.name;

            StatBonusType[] filteredStats = availStats;
            if (balanceConfig != null)
            {
                var filtered = new List<StatBonusType>(availStats.Length);
                foreach (var s in availStats)
                    if (!balanceConfig.IsStatDisabled(s, weaponObj.name))
                        filtered.Add(s);
                filteredStats = filtered.ToArray();
            }

            var upgradeOffer = GenerateWeaponUpgrade(
                personalRng, rarity, weaponObj.name, name, filteredStats, balanceConfig);
            return LevelUpOffer.ForWeaponUpgrade(upgradeOffer);
        }

        // Заглушка
        var fallback = new WeaponUpgradeOffer(
            UpgradeRarity.Common, "gold", "ЗОЛОТО",
            new List<StatBonus> { new StatBonus(StatBonusType.Damage, 0) });
        return LevelUpOffer.ForWeaponUpgrade(fallback);
    }

    private static WeaponUpgradeOffer GenerateWeaponUpgrade(
        System.Random personalRng, UpgradeRarity rarity,
        string weaponId, string weaponName,
        StatBonusType[] availStats, UpgradeBalanceConfig config)
    {
        int statCount = Mathf.Min(2, availStats.Length);
        var chosen    = new List<StatBonusType>(statCount);
        var available = new List<StatBonusType>(availStats);

        for (int s = 0; s < statCount && available.Count > 0; s++)
        {
            int idx = personalRng.Next(0, available.Count);
            chosen.Add(available[idx]);
            available.RemoveAt(idx);
        }

        var bonuses = new List<StatBonus>();
        foreach (var stat in chosen)
        {
            float finalVal;
            if (config != null)
                finalVal = config.GetFinalValue(stat, rarity, personalRng, weaponId);
            else
            {
                int ri = (int)rarity;
                float raw = _defaultRanges[ri, 0]
                    + (float)personalRng.NextDouble() * (_defaultRanges[ri, 1] - _defaultRanges[ri, 0]);
                finalVal = RoundWeaponValue(stat, raw);
            }
            bonuses.Add(new StatBonus(stat, finalVal));
        }

        return new WeaponUpgradeOffer(rarity, weaponId, weaponName, bonuses);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ШАНС ТОТЕМА
    // ─────────────────────────────────────────────────────────────────────────

    private static bool RollTotemChance(System.Random rng, UpgradeBalanceConfig config)
    {
        float chance = config != null ? config.totemCardChance : DEFAULT_TOTEM_CHANCE;
        return rng.NextDouble() < chance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private static UpgradeRarity RollRarity(System.Random rng, UpgradeBalanceConfig config)
    {
        int[] weights = config != null ? config.GetRarityWeights() : _defaultWeights;
        int total = 0;
        foreach (int w in weights) total += w;
        int roll = rng.Next(0, total);
        int acc  = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            acc += weights[i];
            if (roll < acc) return (UpgradeRarity)i;
        }
        return UpgradeRarity.Common;
    }

    private static float RoundWeaponValue(StatBonusType stat, float raw)
    {
        if (stat == StatBonusType.ProjectileCount) return Mathf.Max(1f, Mathf.Round(raw));
        if (stat == StatBonusType.CritMultiplier)  return Mathf.Round(raw * 100f) / 100f;
        return Mathf.Round(raw * 10f) / 10f;
    }

    /// <summary>
    /// Перемешивает список индексов и возвращает новый перемешанный список.
    /// Принимает List вместо int count чтобы работать с подмножеством индексов.
    /// </summary>
    private static List<int> ShuffleList(System.Random rng, List<int> source)
    {
        var result = new List<int>(source);
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }

    // Оставляем старый метод для обратной совместимости
    private static int[] ShuffleIndices(System.Random rng, int count)
    {
        var indices = new int[count];
        for (int i = 0; i < count; i++) indices[i] = i;
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        return indices;
    }
}
