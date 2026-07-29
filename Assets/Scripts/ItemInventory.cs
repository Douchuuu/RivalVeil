using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// ItemInventory — управление предметами игрока.
///
/// ══════════════════════════════════════════════════════════════
/// ДОБАВЛЕНО: алиасы для совместимости с новой EnemyHealth.cs
///   NotifyHitEnemy()    → вызывает NotifyPlayerHitEnemy()
///   NotifyEnemyKilled() → вызывает NotifyPlayerKillEnemy()
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class ItemInventory : MonoBehaviour
{
    [SerializeField] private ItemDefinition[] startingItems;

    private readonly List<ItemInstance> _items = new List<ItemInstance>();
    private PlayerStats _playerStats;
    private WeaponManager _weaponManager;

    private readonly List<ItemInstance> _effectFilterCache = new List<ItemInstance>(8);

    public Action<ItemInstance> OnItemAdded;
    public Action<ItemInstance> OnItemRemoved;
    public Action OnInventoryChanged;

    public event Action OnEnemyKilled;

    internal int BlackHoleActiveCount = 0;

    private void Awake()
    {
        _playerStats   = GetComponent<PlayerStats>();
        _weaponManager = GetComponent<WeaponManager>();

        if (_playerStats == null)
        {
            Debug.LogWarning($"[ItemInventory] ⚠️ PlayerStats не найден на '{gameObject.name}'. " +
                             "Компонент должен быть только на prefab игрока. Отключаюсь.");
            enabled = false;
        }
    }

    private void Start()
    {
        if (!enabled) return;

        if (startingItems != null)
            foreach (var def in startingItems)
                if (def != null) AddItem(def);
    }

    private void Update()
    {
        int count = _items.Count;
        for (int i = 0; i < count; i++)
            _items[i].UpdateEffects();
    }

    private void OnDestroy()
    {
        foreach (var item in _items)
            item.Deactivate();
        _items.Clear();
        BlackHoleActiveCount = 0;
    }

    // ─── УПРАВЛЕНИЕ ──────────────────────────────────────────────────────────

    public ItemInstance AddItem(ItemDefinition definition)
    {
        if (definition == null) return null;

        var item = new ItemInstance(definition);
        item.Activate(_playerStats, _weaponManager, this);
        _items.Add(item);

        OnItemAdded?.Invoke(item);
        OnInventoryChanged?.Invoke();
        _playerStats?.SyncItemFlags(ComputeItemFlags());   // ← синхронизация на сервер

        Debug.Log($"[ItemInventory] + {definition.displayName} (всего: {_items.Count})");
        return item;
    }

    public bool RemoveItem(ItemInstance item)
    {
        if (item == null || !_items.Remove(item)) return false;

        item.Deactivate();
        OnItemRemoved?.Invoke(item);
        OnInventoryChanged?.Invoke();
        _playerStats?.SyncItemFlags(ComputeItemFlags());   // ← синхронизация на сервер
        return true;
    }

    public IReadOnlyList<ItemInstance> GetItemsWithEffect(ItemEffectType effectType)
    {
        _effectFilterCache.Clear();
        foreach (var item in _items)
            foreach (var effect in item.Definition.effects)
                if (effect.type == effectType) { _effectFilterCache.Add(item); break; }
        return _effectFilterCache;
    }

    public List<ItemInstance> GetItemsByDefinition(ItemDefinition definition)
    {
        var result = new List<ItemInstance>();
        foreach (var item in _items)
            if (item.Definition == definition) result.Add(item);
        return result;
    }

    // ─── СОБЫТИЯ БОЕВОЙ СИСТЕМЫ ───────────────────────────────────────────────

    /// <summary>Челюсть: вампиризм при ударе. Вызывается из EnemyHealth.TakeDamage().</summary>
    public void NotifyPlayerHitEnemy(EnemyHealth enemyHealth)
    {
        int count = _items.Count;
        for (int i = 0; i < count; i++)
            _items[i].OnPlayerHitEnemy(enemyHealth);
    }

    /// <summary>Голова Демона: стак за убийство. Вызывается из EnemyHealth.Die().</summary>
    public void NotifyPlayerKillEnemy()
    {
        int count = _items.Count;
        for (int i = 0; i < count; i++)
            _items[i].OnPlayerKillEnemy();

        OnEnemyKilled?.Invoke();
    }

    // ─── АЛИАСЫ ДЛЯ НОВОЙ EnemyHealth.cs ────────────────────────────────────

    /// <summary>Алиас для совместимости с EnemyHealth v2. Вызывает NotifyPlayerHitEnemy.</summary>
    public void NotifyHitEnemy(EnemyHealth enemyHealth) => NotifyPlayerHitEnemy(enemyHealth);

    /// <summary>Алиас для совместимости с EnemyHealth v2. Вызывает NotifyPlayerKillEnemy.</summary>
    public void NotifyEnemyKilled() => NotifyPlayerKillEnemy();

    // ─── УВЕДОМЛЕНИЕ: ОТКРЫТ СУНДУК ──────────────────────────────────────────

    public void NotifyChestOpened()
    {
        int count = _items.Count;
        for (int i = 0; i < count; i++)
            _items[i].OnChestOpened();
    }

    // ─── API ─────────────────────────────────────────────────────────────────

    public int GetItemCount() => _items.Count;

    public List<ItemInstance> GetAllItems() => new List<ItemInstance>(_items);

    public void Clear()
    {
        foreach (var item in _items) item.Deactivate();
        _items.Clear();
        BlackHoleActiveCount = 0;
        _playerStats?.SyncItemFlags(0);                    // ← сбрасываем флаги на сервере
        OnInventoryChanged?.Invoke();
    }

    // ─── СИНХРОНИЗАЦИЯ ФЛАГОВ ПРЕДМЕТОВ ──────────────────────────────────────

    /// <summary>
    /// Вычисляет битмаску всех предметов в инвентаре через ItemDefinition.FlagMask.
    /// Результат передаётся в PlayerStats.SyncItemFlags() → netItemFlags NetworkVariable
    /// → читается на dedicated server через LiveMatchTracker для записи в БД.
    ///
    /// ВАЖНО: В Inspector каждый ItemDefinition должен иметь уникальный FlagMask (степень двойки).
    /// Например: Bandage=1, Torch=2, Talisman=4, Hat=8, Jaw=16, DemonHead=32, BlackHole=64.
    /// </summary>
    private int ComputeItemFlags()
    {
        int flags = 0;
        foreach (var item in _items)
            flags |= item.Definition.FlagMask;
        return flags;
    }
}
