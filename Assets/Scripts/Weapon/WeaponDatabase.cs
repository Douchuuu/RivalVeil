using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// БАЗА ДАННЫХ ОРУЖИЙ — ScriptableObject
///
/// Один ассет содержит все WeaponDefinition.
/// Зарегистрирован в VContainer как singleton:
///   builder.RegisterInstance(weaponDatabase)
///
/// ─── КАК ИСПОЛЬЗОВАТЬ ────────────────────────────────────────────────────────
///   [Inject] WeaponDatabase _weaponDb;
///   var sword = _weaponDb.GetById("sword");
///   var allWeapons = _weaponDb.GetAll();
///
/// ─── КАК РАСШИРЯТЬ ───────────────────────────────────────────────────────────
///   1. Создай новый WeaponDefinition ассет
///   2. Добавь в список weapons здесь
///   3. Всё остальное подхватывает автоматически (LevelUp, UI, Network)
///   4. Если нужна кастомная балансировка — добавь WeaponUpgradeOverride в UpgradeBalanceConfig
///
/// ─── СТАРТОВОЕ ОРУЖИЕ ────────────────────────────────────────────────────────
///   defaultStartWeaponId — weaponId оружия выдаваемого при старте.
///   В будущем: CharacterDefinition сможет переопределить это для каждого персонажа.
/// </summary>
[CreateAssetMenu(menuName = "Game/Weapon Database", fileName = "WeaponDatabase")]
public class WeaponDatabase : ScriptableObject
{
    [SerializeField] private List<WeaponDefinition> weapons = new List<WeaponDefinition>();

    [Header("Стартовое оружие")]
    [Tooltip("weaponId оружия выдаваемого игроку при старте.\n" +
             "В будущем: CharacterDefinition переопределит для каждого персонажа.")]
    [SerializeField] private string defaultStartWeaponId = "sword";

    public string DefaultStartWeaponId => defaultStartWeaponId;

    // Кэш для O(1) поиска по ID
    private Dictionary<string, WeaponDefinition> _cache;

    private void BuildCache()
    {
        _cache = new Dictionary<string, WeaponDefinition>(weapons.Count);
        foreach (var w in weapons)
        {
            if (w == null) continue;
            if (_cache.ContainsKey(w.weaponId))
                Debug.LogError($"[WeaponDatabase] Дублирующийся weaponId: '{w.weaponId}'!");
            else
                _cache[w.weaponId] = w;
        }
        
        Debug.Log($"[WeaponDatabase] Кэш построен: {_cache.Count} оружий");
    }

    // ─────────────────────────────────────────────────────────────────────────

    public WeaponDefinition GetById(string id)
    {
        // ГАРАНТИЯ: Кэш должен быть построен
        if (_cache == null || _cache.Count == 0)
        {
            Debug.Log($"[WeaponDatabase] Кэш null/пуст, строим... (поиск '{id}')");
            BuildCache();
            
            if (_cache == null || _cache.Count == 0)
            {
                Debug.LogError($"[WeaponDatabase] ❌ Не удалось построить кэш! weapons.Count={weapons.Count}");
                return null;
            }
        }
        
        // Поиск оружия
        if (!_cache.TryGetValue(id, out var def))
        {
            Debug.LogWarning($"[WeaponDatabase] ⚠️ Оружие '{id}' не найдено! Доступные: {string.Join(", ", _cache.Keys)}");
            return null;
        }
        
        return def;
    }

    public IReadOnlyList<WeaponDefinition> GetAll()
    {
        // Гарантия что кэш построен перед возвратом списка
        if (_cache == null) BuildCache();
        return weapons;
    }

    public int GetCount() 
    { 
        if (_cache == null) BuildCache();
        return _cache?.Count ?? 0; 
    }

    // Возвращает список WeaponDefinition из битмаски (для UI оружий оппонента).
    // Использует индексные биты (1 << i) — гарантированно согласован с GetFlagBit.
    public List<WeaponDefinition> GetFromFlags(int flags)
    {
        var result = new List<WeaponDefinition>();
        for (int i = 0; i < weapons.Count; i++)
        {
            if (weapons[i] != null && (flags & (1 << i)) != 0)
                result.Add(weapons[i]);
        }
        return result;
    }

    // Возвращает индексный бит оружия по weaponId.
    // Используется в WeaponManager.GetWeaponFlags().
    public int GetFlagBit(string weaponId)
    {
        for (int i = 0; i < weapons.Count; i++)
            if (weapons[i] != null && weapons[i].weaponId == weaponId)
                return 1 << i;
        return 0;
    }

    // Validation в Editor
    private void OnValidate()
    {
        _cache = null; // сбрасываем кэш при изменении в Editor
        var seen = new HashSet<string>();
        foreach (var w in weapons)
        {
            if (w == null) { Debug.LogWarning("[WeaponDatabase] Null entry in weapons list!"); continue; }
            if (string.IsNullOrEmpty(w.weaponId)) { Debug.LogError($"[WeaponDatabase] Empty weaponId on {w.name}!"); continue; }
            if (!seen.Add(w.weaponId)) Debug.LogError($"[WeaponDatabase] Duplicate weaponId: '{w.weaponId}'");
        }

        if (!string.IsNullOrEmpty(defaultStartWeaponId) && weapons.Count > 0)
        {
            bool found = false;
            foreach (var w in weapons)
                if (w != null && w.weaponId == defaultStartWeaponId) { found = true; break; }
            if (!found)
                Debug.LogWarning($"[WeaponDatabase] defaultStartWeaponId '{defaultStartWeaponId}' не найден в списке оружий!", this);
        }
    }
}
