using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// ItemDatabase — централизованная база данных всех предметов в игре.
/// Использует кэширование для быстрого доступа по ID и индексу.
/// </summary>
[CreateAssetMenu(fileName = "ItemDatabase", menuName = "Game/Item Database")]
public class ItemDatabase : ScriptableObject
{
    [Tooltip("Все предметы в игре")]
    [SerializeField] private List<ItemDefinition> items = new List<ItemDefinition>();

    // ─── КЭШ ─────────────────────────────────────────────────────────────────
    private Dictionary<string, ItemDefinition> _idCache;
    private Dictionary<string, int> _idToIndex;
    private Dictionary<int, ItemDefinition> _indexCache;
    private bool _isDirty = true;

    // ─────────────────────────────────────────────────────────────────────────
    // ВАЛИДАЦИЯ И ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnValidate()
    {
        ValidateItems();
        _isDirty = true;
    }

    private void OnEnable()
    {
        _isDirty = true;
        // Кэш соберется при первом обращении через BuildCache()
    }

    private void ValidateItems()
    {
        var seenIds = new HashSet<string>();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] == null) continue;
            if (!seenIds.Add(items[i].itemId))
            {
                Debug.LogError($"[ItemDatabase] Дубликат ID: {items[i].itemId} у предмета {items[i].displayName}.");
            }
        }
    }

    private void BuildCache()
    {
        if (!_isDirty && _idCache != null) return;

        _idCache = new Dictionary<string, ItemDefinition>();
        _idToIndex = new Dictionary<string, int>();
        _indexCache = new Dictionary<int, ItemDefinition>();

        int currentIndex = 0;
        foreach (var item in items)
        {
            if (item == null) continue;

            if (!_idCache.ContainsKey(item.itemId))
            {
                _idCache[item.itemId] = item;
                _idToIndex[item.itemId] = currentIndex;
                _indexCache[currentIndex] = item;
                currentIndex++;
            }
        }

        _isDirty = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC API (Поиск и получение)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary> Основной метод поиска по ID (используется в большинстве скриптов) </summary>
    public ItemDefinition GetById(string id)
    {
        BuildCache();
        return _idCache.TryGetValue(id, out var item) ? item : null;
    }

    /// <summary> Алиас для GetById для совместимости со старым кодом </summary>
    public ItemDefinition GetItemById(string id) => GetById(id);

    public ItemDefinition GetByIndex(int index)
    {
        BuildCache();
        return _indexCache.TryGetValue(index, out var item) ? item : null;
    }

    public int GetIndex(string id)
    {
        BuildCache();
        return _idToIndex.TryGetValue(id, out int index) ? index : -1;
    }

    /// <summary> Возвращает список предметов определенной редкости (нужно для Chest и UpgradeGenerator) </summary>
    public List<ItemDefinition> GetItemsByRarity(ItemRarity rarity)
    {
        return items.FindAll(i => i != null && i.rarity == rarity);
    }

    public List<ItemDefinition> GetAllItems() => new List<ItemDefinition>(items.Where(i => i != null));

    public int GetCount()
    {
        BuildCache();
        return _idCache.Count;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РАНДОМ И УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────────────

    public ItemDefinition GetRandom(System.Random random = null)
    {
        BuildCache();
        if (_indexCache.Count == 0) return null;

        int idx = (random != null) ? random.Next(_indexCache.Count) : Random.Range(0, _indexCache.Count);
        return GetByIndex(idx);
    }

    public void PrintDatabase()
    {
        Debug.Log($"[ItemDatabase] === База данных ({items.Count} предметов) ===");
        foreach (var item in items)
            if (item != null) Debug.Log($"  - {item.displayName} ({item.rarity}) - ID: {item.itemId}");
    }
}
/*using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ItemDatabase — централизованное хранилище всех предметов игры.
/// Должна быть одна инстанция, сохранённая как ScriptableObject в Resources.
/// 
/// Использование:
///   ItemDatabase db = Resources.Load<ItemDatabase>("ItemDatabase");
///   ItemDefinition cloth = db.GetItemById("bandage");
/// </summary>
[CreateAssetMenu(menuName = "Game/Item Database", fileName = "ItemDatabase")]
public class ItemDatabase : ScriptableObject
{
    [SerializeField] private List<ItemDefinition> items = new List<ItemDefinition>();

    public ItemDefinition GetItemById(string itemId)
    {
        foreach (var item in items)
        {
            if (item.itemId == itemId)
                return item;
        }
        Debug.LogWarning($"[ItemDatabase] Предмет с ID '{itemId}' не найден!");
        return null;
    }

    public List<ItemDefinition> GetAllItems() => new List<ItemDefinition>(items);

    public List<ItemDefinition> GetItemsByRarity(ItemRarity rarity)
    {
        var result = new List<ItemDefinition>();
        foreach (var item in items)
        {
            if (item.rarity == rarity)
                result.Add(item);
        }
        return result;
    }

    public void AddItem(ItemDefinition item)
    {
        if (!items.Contains(item))
        {
            items.Add(item);
            Debug.Log($"[ItemDatabase] Добавлен предмет: {item.displayName}");
        }
    }

    public void RemoveItem(ItemDefinition item)
    {
        if (items.Remove(item))
            Debug.Log($"[ItemDatabase] Удалён предмет: {item.displayName}");
    }

    public int GetItemCount() => items.Count;

    public void PrintDatabase()
    {
        Debug.Log($"[ItemDatabase] === База данных ({items.Count} предметов) ===");
        foreach (var item in items)
            Debug.Log($"  - {item.displayName} ({item.rarity}) - ID: {item.itemId}");
    }
}
*/