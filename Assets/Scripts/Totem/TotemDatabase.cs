using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// TotemDatabase — реестр всех тотемов (ScriptableObject).
///
/// Зарегистрирован в VContainer как singleton:
///   builder.RegisterInstance(totemDatabase)
///
/// ─── КАК ИСПОЛЬЗОВАТЬ ────────────────────────────────────────────────────────
///   [Inject] TotemDatabase _totemDb;
///   var all = _totemDb.GetAll();
///
/// ─── КАК РАСШИРЯТЬ ───────────────────────────────────────────────────────────
///   1. Создай TotemDefinition (Create → Game → Totem Definition)
///   2. Добавь в список totems здесь
///   3. Добавь диапазоны в UpgradeBalanceConfig.totemRanges
/// </summary>
[CreateAssetMenu(menuName = "Game/Totem Database", fileName = "TotemDatabase")]
public class TotemDatabase : ScriptableObject
{
    [SerializeField] private List<TotemDefinition> totems = new List<TotemDefinition>();

    private Dictionary<string, TotemDefinition> _cache;

    private void BuildCache()
    {
        _cache = new Dictionary<string, TotemDefinition>(totems.Count);
        foreach (var t in totems)
        {
            if (t == null) continue;
            if (_cache.ContainsKey(t.totemId))
                Debug.LogError($"[TotemDatabase] Дублирующийся totemId: '{t.totemId}'!");
            else
                _cache[t.totemId] = t;
        }
    }

    public TotemDefinition GetById(string id)
    {
        if (_cache == null) BuildCache();
        return _cache.TryGetValue(id, out var def) ? def : null;
    }

    public IReadOnlyList<TotemDefinition> GetAll() => totems;
    public int GetCount() => totems.Count;

    private void OnValidate()
    {
        _cache = null;
        var seen = new HashSet<string>();
        foreach (var t in totems)
        {
            if (t == null) { Debug.LogWarning("[TotemDatabase] Null entry!"); continue; }
            if (string.IsNullOrEmpty(t.totemId)) { Debug.LogError($"[TotemDatabase] Empty totemId on {t.name}!"); continue; }
            if (!seen.Add(t.totemId)) Debug.LogError($"[TotemDatabase] Duplicate totemId: '{t.totemId}'");
        }
    }
}
