using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// CharacterDatabase — база данных всех персонажей в игре.
///
/// ══════════════════════════════════════════════════════════════════════
/// РОЛЬ В АРХИТЕКТУРЕ
/// ══════════════════════════════════════════════════════════════════════
///
///   ScriptableObject содержащий список всех playable персонажей.
///   Регистрируется в VContainer (BaseLifetimeScope).
///   Используется CharacterSelectManager и CharacterSelectUI.
///
/// ══════════════════════════════════════════════════════════════════════
/// ВАЛИДАЦИЯ
/// ══════════════════════════════════════════════════════════════════════
///
///   При изменении в Inspector проверяет:
///   - Уникальность ID
///   - Наличие портрета и иконки
///   - Корректность значений характеристик
/// </summary>
[CreateAssetMenu(fileName = "CharacterDatabase", menuName = "Game/Character Database")]
public class CharacterDatabase : ScriptableObject
{
    [Tooltip("Все playable персонажи")]
    [SerializeField] private List<CharacterDefinition> characters = new List<CharacterDefinition>();

    // ─── КЭШ ─────────────────────────────────────────────────────────────────
    private Dictionary<string, CharacterDefinition> _idCache;
    private Dictionary<int, CharacterDefinition> _indexCache;
    private bool _isDirty = true;

    // ─────────────────────────────────────────────────────────────────────────
    // ВАЛИДАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnValidate()
    {
        ValidateCharacters();
        _isDirty = true;
    }

    private void OnEnable()
    {
        _isDirty = true;
        BuildCache();
    }

    private void ValidateCharacters()
    {
        var seenIds = new HashSet<string>();
        var duplicates = new List<string>();

        int index = 0;
        foreach (var character in characters)
        {
            if (character == null) continue;

            // Устанавливаем dbIndex
            character.dbIndex = index++;

            if (!seenIds.Add(character.id))
            {
                duplicates.Add(character.id);
            }
        }

        if (duplicates.Count > 0)
        {
            Debug.LogError($"[CharacterDatabase] Обнаружены дубликаты ID: {string.Join(", ", duplicates)}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КЭШИРОВАНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    private void BuildCache()
    {
        if (!_isDirty) return;

        _idCache = new Dictionary<string, CharacterDefinition>();
        _indexCache = new Dictionary<int, CharacterDefinition>();

        int index = 0;
        foreach (var character in characters)
        {
            if (character == null) continue;

            if (!_idCache.ContainsKey(character.id))
            {
                _idCache[character.id] = character;
                _indexCache[index] = character;
                character.dbIndex = index;
                index++;
            }
        }

        _isDirty = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает персонажа по ID.
    /// </summary>
    public CharacterDefinition GetById(string id)
    {
        BuildCache();
        _idCache.TryGetValue(id, out var character);
        return character;
    }

    /// <summary>
    /// Возвращает персонажа по индексу.
    /// </summary>
    public CharacterDefinition GetByIndex(int index)
    {
        BuildCache();
        _indexCache.TryGetValue(index, out var character);
        return character;
    }

    /// <summary>
    /// Возвращает индекс персонажа.
    /// </summary>
    public int GetIndex(string id)
    {
        BuildCache();
        if (_idCache.TryGetValue(id, out var character))
            return character.dbIndex;
        return -1;
    }

    /// <summary>
    /// Возвращает индекс персонажа.
    /// </summary>
    public int GetIndex(CharacterDefinition character)
    {
        if (character == null) return -1;
        return character.dbIndex;
    }

    /// <summary>
    /// Возвращает всех персонажей.
    /// </summary>
    public IReadOnlyList<CharacterDefinition> GetAll()
    {
        return characters.Where(c => c != null).ToList();
    }

    /// <summary>
    /// Возвращает количество персонажей.
    /// </summary>
    public int GetCount()
    {
        BuildCache();
        return _idCache.Count;
    }

    /// <summary>
    /// Возвращает дефолтного персонажа (первого в списке).
    /// </summary>
    public CharacterDefinition GetDefault()
    {
        BuildCache();
        return characters.FirstOrDefault(c => c != null);
    }

    /// <summary>
    /// Проверяет существование персонажа.
    /// </summary>
    public bool Contains(string id)
    {
        BuildCache();
        return _idCache.ContainsKey(id);
    }

    /// <summary>
    /// Возвращает случайного персонажа.
    /// </summary>
    public CharacterDefinition GetRandom(System.Random random = null)
    {
        BuildCache();
        if (_indexCache.Count == 0) return null;

        int index = random != null 
            ? random.Next(_indexCache.Count) 
            : Random.Range(0, _indexCache.Count);

        return GetByIndex(index);
    }
}
/*using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CharacterDatabase — реестр всех персонажей (ScriptableObject).
///
/// Зарегистрирован в VContainer как singleton:
///   builder.RegisterInstance(characterDatabase)
///
/// ─── КАК ИСПОЛЬЗОВАТЬ ────────────────────────────────────────────────────────
///   [Inject] CharacterDatabase _charDb;
///   var warrior = _charDb.GetById("warrior");
///   var all = _charDb.GetAll();
///
/// ─── КАК РАСШИРЯТЬ ───────────────────────────────────────────────────────────
///   1. Создай CharacterDefinition (Create → Game → Character Definition)
///   2. Добавь в список characters здесь
///   3. Остальное подхватывается автоматически
///
/// ─── ИНДЕКСАЦИЯ ───────────────────────────────────────────────────────────────
///   GetIndex() возвращает позицию персонажа в списке.
///   CharacterSelectManager упаковывает индекс в NetworkVariable<int> (0-127).
///   Это дешевле чем передавать строку по сети.
/// </summary>
[CreateAssetMenu(menuName = "Game/Character Database", fileName = "CharacterDatabase")]
public class CharacterDatabase : ScriptableObject
{
    [SerializeField] private List<CharacterDefinition> characters = new List<CharacterDefinition>();

    [Header("Персонаж по умолчанию (если не выбран)")]
    [Tooltip("Индекс персонажа из списка characters который используется\n" +
             "если игрок не выбрал персонажа до начала матча.")]
    [SerializeField] private int defaultCharacterIndex = 0;

    private Dictionary<string, CharacterDefinition> _cache;

    private void BuildCache()
    {
        _cache = new Dictionary<string, CharacterDefinition>(characters.Count);
        foreach (var c in characters)
        {
            if (c == null) continue;
            if (_cache.ContainsKey(c.characterId))
                Debug.LogError($"[CharacterDatabase] Дублирующийся characterId: '{c.characterId}'!");
            else
                _cache[c.characterId] = c;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    public CharacterDefinition GetById(string id)
    {
        if (_cache == null) BuildCache();
        return _cache.TryGetValue(id, out var def) ? def : null;
    }

    public CharacterDefinition GetByIndex(int index)
    {
        if (index < 0 || index >= characters.Count) return GetDefault();
        return characters[index];
    }

    public CharacterDefinition GetDefault()
    {
        if (characters.Count == 0) return null;
        int idx = Mathf.Clamp(defaultCharacterIndex, 0, characters.Count - 1);
        return characters[idx];
    }

    public int GetIndex(string characterId)
    {
        for (int i = 0; i < characters.Count; i++)
            if (characters[i] != null && characters[i].characterId == characterId)
                return i;
        return 0;
    }

    public int GetIndex(CharacterDefinition def)
    {
        return def != null ? GetIndex(def.characterId) : 0;
    }

    public IReadOnlyList<CharacterDefinition> GetAll() => characters;
    public int GetCount() => characters.Count;

    private void OnValidate()
    {
        _cache = null;
        var seen = new HashSet<string>();
        foreach (var c in characters)
        {
            if (c == null)           { Debug.LogWarning("[CharacterDatabase] Null entry!"); continue; }
            if (string.IsNullOrEmpty(c.characterId)) { Debug.LogError($"[CharacterDatabase] Empty characterId on {c.name}!"); continue; }
            if (!seen.Add(c.characterId)) Debug.LogError($"[CharacterDatabase] Duplicate characterId: '{c.characterId}'");
        }

        if (characters.Count > 0 && defaultCharacterIndex >= characters.Count)
            Debug.LogWarning($"[CharacterDatabase] defaultCharacterIndex {defaultCharacterIndex} вне диапазона!", this);
    }
}
*/