using System.Collections.Generic;
using UnityEngine;
using VContainer;

/// <summary>
/// TotemManager — управление тотемами игрока.
///
/// ══════════════════════════════════════════════════════════════
/// НОВОЕ: SyncTotemDataToNetwork() + инжект TotemDatabase
/// ══════════════════════════════════════════════════════════════
///
///   TotemDatabase инжектируется через [Inject] Construct().
///   Нужен чтобы найти числовой индекс тотема в базе — этот
///   индекс используется при упаковке данных для сети.
///
///   После каждого изменения (UnlockTotem, UpgradeTotem)
///   вызывается SyncTotemDataToNetwork() которая упаковывает
///   данные 4 слотов (12 бит на слот: 5 бит = dbIndex, 7 бит = level)
///   в long и передаёт в PlayerStats.SyncTotemData().
///
///   CompetitiveUIManager читает GetNetTotemData() у оппонента
///   и распаковывает через PlayerStats.UnpackTotemSlot(data, slot).
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class TotemManager : MonoBehaviour
{
    public const int MAX_TOTEM_LEVEL = 100;

    [Header("Настройки")]
    public int maxSlots = 4;

    // ─── ДАННЫЕ СЛОТОВ ────────────────────────────────────────────────────────

    private class TotemSlot
    {
        public TotemDefinition Definition;
        public float           AccumulatedValue;
        public int             UpgradeLevel;
    }

    private readonly List<TotemSlot> _slots = new List<TotemSlot>(4);

    // ─── ЗАВИСИМОСТИ ──────────────────────────────────────────────────────────

    private PlayerStats          _playerStats;
    private CharacterMultipliers _charMult;
    private TotemDatabase        _totemDb;   // НОВОЕ: нужен для получения индекса тотема

    private void Awake()
    {
        _playerStats = GetComponent<PlayerStats>();
        _charMult    = GetComponent<CharacterMultipliers>();

        if (_playerStats == null)
            Debug.LogError("[TotemManager] ❌ PlayerStats не найден!");
    }

    // НОВОЕ: инжектируем TotemDatabase
    [Inject]
    public void Construct(TotemDatabase totemDatabase)
    {
        _totemDb = totemDatabase;
    }

    // ─── ПУБЛИЧНЫЙ API ────────────────────────────────────────────────────────

    public int  SlotCount   => _slots.Count;
    public bool HasFreeSlot => _slots.Count < maxSlots;

    public bool HasTotem(string totemId)
    {
        foreach (var s in _slots)
            if (s.Definition.totemId == totemId) return true;
        return false;
    }

    public IReadOnlyList<TotemDefinition> ActiveTotems
    {
        get
        {
            var list = new List<TotemDefinition>(_slots.Count);
            foreach (var s in _slots) list.Add(s.Definition);
            return list;
        }
    }

    public float GetAccumulatedValue(string totemId)
    {
        foreach (var s in _slots)
            if (s.Definition.totemId == totemId) return s.AccumulatedValue;
        return 0f;
    }

    public int GetTotemLevel(string totemId)
    {
        foreach (var s in _slots)
            if (s.Definition.totemId == totemId) return s.UpgradeLevel;
        return 0;
    }

    public bool IsTotemMaxLevel(string totemId)
        => GetTotemLevel(totemId) >= MAX_TOTEM_LEVEL;

    // ─── КОМАНДЫ ──────────────────────────────────────────────────────────────

    public void UnlockTotem(TotemDefinition def, float bonusValue)
    {
        if (def == null) { Debug.LogError("[TotemManager] def == null!"); return; }

        if (HasTotem(def.totemId))
        {
            UpgradeTotem(def.totemId, bonusValue);
            return;
        }

        if (!HasFreeSlot)
        {
            Debug.LogWarning($"[TotemManager] Нет слотов для '{def.displayName}'!");
            return;
        }

        var slot = new TotemSlot
        {
            Definition       = def,
            AccumulatedValue = bonusValue,
            UpgradeLevel     = 1
        };
        _slots.Add(slot);

        ApplyBonus(slot, bonusValue);

        Debug.Log($"[TotemManager] 🗿 Новый: {def.displayName} — {FormatValue(def.bonusType, bonusValue)} [Lv1]");

        // НОВОЕ: синхронизируем по сети
        SyncTotemDataToNetwork();
    }

    public void UpgradeTotem(string totemId, float bonusValue)
    {
        foreach (var slot in _slots)
        {
            if (slot.Definition.totemId != totemId) continue;

            slot.AccumulatedValue += bonusValue;

            if (slot.UpgradeLevel < MAX_TOTEM_LEVEL)
                slot.UpgradeLevel++;

            ApplyBonus(slot, bonusValue);

            bool isMax = slot.UpgradeLevel >= MAX_TOTEM_LEVEL;
            Debug.Log($"[TotemManager] 🗿 Апгрейд: {slot.Definition.displayName} " +
                      $"(+{FormatValue(slot.Definition.bonusType, bonusValue)}) " +
                      $"[Lv{slot.UpgradeLevel}{(isMax ? " MAX" : "")}]");

            // НОВОЕ: синхронизируем по сети
            SyncTotemDataToNetwork();
            return;
        }

        Debug.LogWarning($"[TotemManager] Тотем '{totemId}' не найден!");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СИНХРОНИЗАЦИЯ ПО СЕТИ — НОВОЕ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Упаковывает данные первых 4 тотемов в long и передаёт в PlayerStats
    /// для синхронизации по сети.
    ///
    /// Формат на слот (12 бит):
    ///   Биты 0-4:  dbIndex (позиция тотема в TotemDatabase, 0-30; 31 = пусто)
    ///   Биты 5-11: level   (0-127)
    ///
    ///   Слот 0: биты 0-11
    ///   Слот 1: биты 12-23
    ///   Слот 2: биты 24-35
    ///   Слот 3: биты 36-47
    ///
    /// CompetitiveUIManager распаковывает через
    /// PlayerStats.UnpackTotemSlot(data, slotIndex).
    /// </summary>
    private void SyncTotemDataToNetwork()
    {
        if (_playerStats == null || !_playerStats.IsOwner) return;

        long packed = 0L;

        for (int i = 0; i < Mathf.Min(_slots.Count, 4); i++)
        {
            var slot = _slots[i];

            // Находим индекс тотема в базе данных
            int dbIndex = GetTotemDatabaseIndex(slot.Definition.totemId);

            int level     = Mathf.Clamp(slot.UpgradeLevel, 0, 127);
            long slotData = ((long)(dbIndex & 0x1F) ) |        // биты 0-4: dbIndex
                            ((long)(level   & 0x7F) << 5);     // биты 5-11: level

            packed |= slotData << (i * 12);
        }

        _playerStats.SyncTotemData(packed);
    }

    /// <summary>
    /// Возвращает индекс тотема в TotemDatabase.GetAll().
    /// Возвращает 31 (sentinel = пусто) если тотем не найден или база не инжектирована.
    /// </summary>
    private int GetTotemDatabaseIndex(string totemId)
    {
        if (_totemDb == null)
        {
            Debug.LogWarning("[TotemManager] TotemDatabase не инжектирован — тотемы оппонента не отобразятся.");
            return 31;
        }

        var allTotems = _totemDb.GetAll();
        for (int j = 0; j < allTotems.Count && j < 31; j++)
        {
            if (allTotems[j] != null && allTotems[j].totemId == totemId)
                return j;
        }

        Debug.LogWarning($"[TotemManager] Тотем '{totemId}' не найден в базе (индекс > 30 или не зарегистрирован).");
        return 31;
    }

    // ─── ПРИМЕНЕНИЕ БОНУСА ────────────────────────────────────────────────────

    private void ApplyBonus(TotemSlot slot, float delta)
    {
        if (_playerStats == null) { Debug.LogError("[TotemManager] PlayerStats == null!"); return; }

        if (slot.Definition.bonusType == TotemBonusType.MaxHealth)
        {
            float baseHp    = _charMult != null ? _charMult.GetMaxHealth() : 100f;
            float flatBonus = baseHp * delta / 100f;
            _playerStats.AddBonusMaxHealth(flatBonus);
            return;
        }

        string source = $"totem_{slot.Definition.totemId}";
        _playerStats.RemoveStatModifiersFromSource(source);
        _playerStats.AddStatModifier(BuildModifier(slot.Definition.bonusType, slot.AccumulatedValue, source));

        if (slot.Definition.bonusType == TotemBonusType.Armor)
            _playerStats.RefreshShield(delta);
    }

    private static StatModifier BuildModifier(TotemBonusType bonusType, float configValue, string source)
    {
        switch (bonusType)
        {
            case TotemBonusType.XpGain:
                return new StatModifier(StatType.XpMultiplier, configValue / 100f, ModifierType.Additive, source);
            case TotemBonusType.Damage:
                return new StatModifier(StatType.DamageMultiplier, configValue / 100f, ModifierType.Additive, source);
            case TotemBonusType.CritChance:
                return new StatModifier(StatType.CritChance, configValue / 100f, ModifierType.Additive, source);
            case TotemBonusType.DamageReduction:
                return new StatModifier(StatType.DamageReduction, configValue / 100f, ModifierType.Additive, source);
            case TotemBonusType.MoveSpeed:
                return new StatModifier(StatType.MoveSpeedMultiplier, configValue / 100f, ModifierType.Additive, source);
            case TotemBonusType.PickupRange:
                return new StatModifier(StatType.PickupRange, 1f + configValue / 100f, ModifierType.Multiplicative, source);
            case TotemBonusType.Armor:
                return new StatModifier(StatType.Armor, configValue, ModifierType.Additive, source);
            default:
                Debug.LogWarning($"[TotemManager] Неизвестный TotemBonusType: {bonusType}");
                return new StatModifier(StatType.XpMultiplier, 0f, ModifierType.Additive, source);
        }
    }

    // ─── ВСПОМОГАТЕЛЬНЫЕ ─────────────────────────────────────────────────────

    private static string FormatValue(TotemBonusType bonusType, float val)
        => bonusType == TotemBonusType.Armor ? $"+{val:F0}" : $"+{val:F0}%";

    [ContextMenu("Вывести тотемы в консоль")]
    public void PrintTotems()
    {
        if (_slots.Count == 0) { Debug.Log("[TotemManager] Нет активных тотемов."); return; }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[TotemManager] ══ Тотемы ({_slots.Count}/{maxSlots}) ══");
        foreach (var s in _slots)
            sb.AppendLine($"  🗿 {s.Definition.displayName} ({s.Definition.bonusType}): " +
                          $"{FormatValue(s.Definition.bonusType, s.AccumulatedValue)} " +
                          $"[Lv{s.UpgradeLevel}{(s.UpgradeLevel >= MAX_TOTEM_LEVEL ? " MAX" : "")}]");
        Debug.Log(sb.ToString());
    }
}
