using UnityEngine;

/// <summary>
/// CharacterDefinition — определение playable персонажа.
///
/// ══════════════════════════════════════════════════════════════════════
/// РОЛЬ В АРХИТЕКТУРЕ
/// ══════════════════════════════════════════════════════════════════════
///
///   ScriptableObject содержащий все данные персонажа:
///   - Визуал (портрет, иконка, цвет тела)
///   - Характеристики (множители к базовым значениям)
///   - Пассивная способность
///   - Стартовое оружие
///
/// ══════════════════════════════════════════════════════════════════════
/// ХАРАКТЕРИСТИКИ
/// ══════════════════════════════════════════════════════════════════════
///
///   Все значения — множители к базовым значениям из PlayerStatSheet.
///   Например: maxHealth = 0.8 означает -20% HP от базы.
///
/// ══════════════════════════════════════════════════════════════════════
/// ПАССИВНЫЕ СПОСОБНОСТИ
/// ══════════════════════════════════════════════════════════════════════
///
///   LuckyBlock     — шанс полностью поглотить урон
///   LifeStealOnKill — восстановление HP за убийство
///   DoubleXpChance — шанс удвоить получаемый опыт
///   AttackSpeedFloor — минимальный порог скорости атаки
///   TotemDoubleFirst — первый тотем ×2 эффект
/// </summary>
[CreateAssetMenu(fileName = "NewCharacter", menuName = "Game/Character Definition")]
public class CharacterDefinition : ScriptableObject
{
    [Header("=== Основное ===")]
    [Tooltip("Уникальный ID персонажа")]
    public string id;

    [Tooltip("Отображаемое имя")]
    public string displayName;

    [Tooltip("Описание персонажа")]
    [TextArea(3, 5)]
    public string description;

    [Header("=== Визуал ===")]
    [Tooltip("Портрет для UI выбора персонажа")]
    public Sprite portrait;

    [Tooltip("Иконка персонажа")]
    public Sprite icon;

    [Tooltip("Цвет тела персонажа")]
    public Color bodyColor = Color.white;

    [Tooltip("Масштаб тела (1 = нормальный)")]
    [Range(0.5f, 2f)]
    public float bodyScale = 1f;

    [Tooltip("Акцентный цвет (для UI)")]
    public Color accentColor = Color.white;

    [Header("=== Характеристики (множители) ===")]
    [Tooltip("Множитель макс HP (1 = база)")]
    [Range(0.5f, 2f)]
    public float maxHealth = 1f;

    [Tooltip("Множитель скорости передвижения")]
    [Range(0.5f, 1.5f)]
    public float moveSpeed = 1f;

    [Tooltip("Множитель скорости атаки")]
    [Range(0.5f, 2f)]
    public float attackSpeed = 1f;

    [Tooltip("Множитель урона")]
    [Range(0.5f, 2f)]
    public float damage = 1f;

    [Tooltip("Множитель брони (щита)")]
    [Range(0f, 2f)]
    public float armor = 0f;

    [Tooltip("Бонус к шансу крита (0.1 = +10%)")]
    [Range(0f, 0.5f)]
    public float critChanceBonus = 0f;

    [Tooltip("Бонус к крит урону (0.25 = +25%)")]
    [Range(0f, 1f)]
    public float critDamageBonus = 0f;

    [Tooltip("Множитель опыта")]
    [Range(0.5f, 2f)]
    public float xpMultiplier = 1f;

    [Header("=== Пассивная способность ===")]
    [Tooltip("Тип пассивки")]
    public CharacterPassiveType passiveType = CharacterPassiveType.None;

    [Tooltip("Значение пассивки (зависит от типа)")]
    public float passiveValue = 0f;

    [Header("=== Стартовое оружие ===")]
    [Tooltip("ID стартового оружия (из WeaponDatabase)")]
    public string startingWeaponId;

    // Внутренний индекс (устанавливается автоматически)
    [HideInInspector]
    public int dbIndex;

    // ─────────────────────────────────────────────────────────────────────────
    // ВАЛИДАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnValidate()
    {
        // Убеждаемся что ID не пустой
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogWarning($"[CharacterDefinition] {name}: ID не может быть пустым!");
        }

        // Убеждаемся что имя не пустое
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = name;
        }
    }
}

/// <summary>
/// Типы пассивных способностей персонажей.
/// </summary>
public enum CharacterPassiveType
{
    None,
    LuckyBlock,         // Шанс поглотить урон
    LifeStealOnKill,    // Восстановление HP за убийство
    DoubleXpChance,     // Шанс удвоить опыт
    AttackSpeedFloor,   // Минимальный порог скорости атаки
    TotemDoubleFirst    // Первый тотем ×2 эффект
}
/*using UnityEngine;

/// <summary>
/// CharacterDefinition — данные одного персонажа (ScriptableObject).
///
/// ─── АРХИТЕКТУРА ────────────────────────────────────────────────────────────
///   CharacterDefinition  → ЧТО это за персонаж (stats, passive, artwork)
///   CharacterDatabase    → ГДЕ хранятся все персонажи
///   CharacterSelectManager → КТО выбрал какого персонажа (NetworkVariable)
///   CharacterMultipliers → КАК применяются данные (ApplyDefinition())
///
/// ─── КАК ДОБАВИТЬ ПЕРСОНАЖА ─────────────────────────────────────────────────
///   1. Assets → ПКМ → Create → Game → Character Definition
///   2. Заполни поля в Inspector
///   3. Добавь в CharacterDatabase
///   4. Готово — UI и сеть подхватят автоматически
///
/// ─── ПАССИВНЫЕ СПОСОБНОСТИ ──────────────────────────────────────────────────
///   passiveType определяет логику которая включается в CharacterSelectManager
///   при старте игры. Добавить новую пассивку = добавить case в
///   CharacterSelectManager.ApplyPassiveAbility().
///
/// ─── ВИЗУАЛ ПЕРСОНАЖА ───────────────────────────────────────────────────────
///   bodyColor  → цвет тела (Warrior=оранжевый, Wizard=синий итд.)
///   bodyScale  → масштаб (1.0 = стандарт)
///   Ghost оппонента автоматически получает тот же цвет но прозрачный (alpha 0.35)
/// </summary>
[CreateAssetMenu(menuName = "Game/Character Definition", fileName = "NewCharacter")]
public class CharacterDefinition : ScriptableObject
{
    [Header("── Основная информация ──────────────────────────────────────────")]
    [Tooltip("Уникальный ID персонажа. Используется в CharacterDatabase.")]
    public string characterId;

    [Tooltip("Отображаемое имя в UI выбора персонажа.")]
    public string displayName;

    [TextArea(2, 4)]
    [Tooltip("Описание персонажа для экрана выбора.")]
    public string description;

    [Tooltip("Портрет или иконка персонажа для UI выбора.")]
    public Sprite portrait;

    [Tooltip("Цвет акцента персонажа в UI (рамка, подсветка).")]
    public Color accentColor = Color.white;

    // ─── ВИЗУАЛ ПЕРСОНАЖА ─────────────────────────────────────────────────────
    // bodyColor применяется ко всем Renderer на PlayerPrefab при старте матча.
    // Ghost оппонента получает тот же цвет но с alpha 0.35 (полупрозрачный).

    [Header("── Визуал персонажа ─────────────────────────────────────────────")]
    [Tooltip("Цвет тела персонажа.\n" +
             "Warrior = оранжевый (1.0, 0.45, 0.0)\n" +
             "Wizard  = синий    (0.2, 0.5, 1.0)\n" +
             "Ghost оппонента получает этот же цвет но прозрачный (alpha 0.35).")]
    public Color bodyColor = Color.white;

    [Tooltip("Масштаб персонажа.\n" +
             "1.0 = стандарт. 0.9 = чуть ниже (Wizard). 1.1 = крупнее (Tank).")]
    [Range(0.5f, 2.0f)]
    public float bodyScale = 1.0f;

    // ─── БАЗОВЫЕ ХАРАКТЕРИСТИКИ ────────────────────────────────────────────────
    // Эти поля ПЕРЕЗАПИСЫВАЮТ Inspector-поля CharacterMultipliers при старте.
    // Если не нужно менять значение — оставь такое же как в CharacterMultipliers.

    [Header("── Характеристики (переопределяют CharacterMultipliers) ──────────")]
    [SerializeField] public int maxHealth = 100;
    [SerializeField] public int healthRegeneration = 0;
    [SerializeField] public int extraJumps = 0;
    [SerializeField] public int pickupRadius = 5;
    [SerializeField] public int jumpHeight = 5;
    [SerializeField] public int projectileCount = 1;
    [SerializeField] public int projectileRicochet = 0;

    [Header("── Процентные характеристики ───────────────────────────────────────")]
    [SerializeField][Range(0f, 1f)]   public float critChance = 0f;
    [SerializeField][Range(0.1f, 5f)] public float attackSpeed = 1.0f;
    [SerializeField][Range(0.1f, 20f)] public float damageMultiplier = 1.0f;
    [SerializeField][Range(0.1f, 5f)] public float critDamageMultiplier = 2.0f;
    [SerializeField][Range(0.1f, 5f)] public float attackRadiusMultiplier = 1.0f;
    [SerializeField][Range(0.1f, 5f)] public float projectileSpeedMultiplier = 1.0f;
    [SerializeField][Range(0.1f, 5f)] public float durationMultiplier = 1.0f;
    [SerializeField][Range(0.1f, 3f)] public float moveSpeedMultiplier = 1.0f;
    [SerializeField][Range(0f, 3f)]   public float xpMultiplier = 1.0f;
    [SerializeField][Range(0f, 500f)] public float enemyDifficultyBonus = 0f;

    [Header("── Стартовое оружие ──────────────────────────────────────────────")]
    [Tooltip("ID оружия с которым начинает персонаж.\n" +
             "Если пустое — используется WeaponDatabase.defaultStartWeaponId.\n" +
             "Должен совпадать с weaponId в WeaponDatabase.")]
    public string startingWeaponId = "";

    [Header("── Пассивная способность ────────────────────────────────────────")]
    [Tooltip("Тип уникальной пассивной способности персонажа.\n" +
             "None = нет пассивки, только базовые статы.")]
    public CharacterPassiveType passiveType = CharacterPassiveType.None;

    [Tooltip("Значение пассивной способности (шанс, бонус, множитель — зависит от типа).")]
    public float passiveValue = 0f;

    [TextArea(1, 3)]
    [Tooltip("Описание пассивной способности для UI.")]
    public string passiveDescription;

    // ─────────────────────────────────────────────────────────────────────────

    public override string ToString() => $"{displayName} ({characterId})";

    /// <summary>Возвращает стартовое оружие или дефолтный ID если не задан.</summary>
    public string GetEffectiveStartWeaponId(string databaseDefault)
    {
        return string.IsNullOrEmpty(startingWeaponId) ? databaseDefault : startingWeaponId;
    }

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(characterId))
            Debug.LogWarning($"[CharacterDefinition] {name}: characterId пусто!", this);
        if (string.IsNullOrEmpty(displayName))
            Debug.LogWarning($"[CharacterDefinition] {name}: displayName пусто!", this);
        if (portrait == null)
            Debug.LogWarning($"[CharacterDefinition] {name}: portrait не назначен!", this);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тип пассивной способности персонажа.
///
/// Логика каждого типа живёт в CharacterSelectManager.ApplyPassiveAbility().
/// Добавить новую пассивку = добавить значение сюда + case в ApplyPassiveAbility().
/// </summary>
public enum CharacterPassiveType
{
    /// Нет пассивки
    None = 0,

    /// При получении урона: шанс [passiveValue %] полностью поглотить удар.
    /// Реализуется как IShieldInterceptor.
    LuckyBlock = 1,

    /// Первый выпавший тотем имеет удвоенное значение бонуса.
    TotemDoubleFirst = 2,

    /// Убийство врага: шанс [passiveValue %] мгновенно восстановить X HP.
    /// X = passiveValue * MaxHealth / 100
    LifeStealOnKill = 3,

    /// При подборе XP-орба: шанс [passiveValue %] получить двойной опыт.
    DoubleXpChance = 4,

    /// Скорость атаки не уменьшается при откате (глобальный AttackSpeed
    /// всегда >= passiveValue).
    AttackSpeedFloor = 5,
}
*/