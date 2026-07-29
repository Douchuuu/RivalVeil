using UnityEngine;

/// <summary>
/// CharacterMultipliers — компонент на PlayerPrefab, "шаблон игрока".
///
/// ════════════════════════════════════════════════════════════════════
/// ЧТО ЭТО И ЗАЧЕМ (читай внимательно!)
/// ════════════════════════════════════════════════════════════════════
///
/// У тебя есть ОДИН PlayerPrefab с тремя компонентами:
///   ├── CharacterMultipliers   ← ЭТОТ файл
///   ├── PlayerStats            ← HP, урон, XP во время игры
///   └── PlayerMovement         ← движение, прыжки, подбор
///
/// CharacterMultipliers — это ШАБЛОН со значениями по умолчанию.
/// Ты настраиваешь их в Inspector ОДИН раз.
///
/// Когда игрок ВЫБИРАЕТ ПЕРСОНАЖА (CharacterDefinition):
///   1. CharacterSelectManager вызывает ApplyDefinition(def)
///   2. ApplyDefinition() перезаписывает поля этого компонента
///   3. InitializeStatSheet() заполняет StatSheet данными персонажа
///   4. PlayerStats.ApplyCharacterDefinition() обновляет maxHealth
///
/// НЕ НУЖНО дублировать настройки!
///   Inspector на CharacterMultipliers = запасной вариант (без выбора персонажа)
///   CharacterDefinition.maxHealth     = значение конкретного персонажа
///   PlayerStats.maxHealth (Inspector) = ИГНОРИРУЕТСЯ (берётся из StatSheet)
///
/// ════════════════════════════════════════════════════════════════════
/// ФОРМУЛА РЕГЕНЕРАЦИИ:
///   CurrentHealth += healthRegeneration * 0.1 * deltaTime
/// ════════════════════════════════════════════════════════════════════
/// </summary>
[RequireComponent(typeof(PlayerStats))]
public class CharacterMultipliers : MonoBehaviour
{
    [Header("❤️ ЗДОРОВЬЕ И РЕГЕНЕРАЦИЯ")]
    [Tooltip("HP по умолчанию. CharacterDefinition может изменить это значение.")]
    [SerializeField] private int maxHealth = 100;
    [Tooltip("Регенерация: +healthRegeneration * 0.1 HP/сек")]
    [SerializeField] private int healthRegeneration = 0;

    [Header("🚀 ДВИЖЕНИЕ И ПРЫЖКИ")]
    [Tooltip("0 = только с земли (1 прыжок). 1 = земля + 1 прыжок в воздухе.")]
    [SerializeField] private int extraJumps = 0;
    [Tooltip("Радиус подбора XP-орбов. Тотем добавляет Multiplicative-модификатор.")]
    [SerializeField] private int pickupRadius = 5;
    [Tooltip("Высота прыжка. База=5. Значение 7 -> в 1.4x выше стандарта.")]
    [SerializeField] private int jumpHeight = 5;

    [Header("🎯 СНАРЯДЫ")]
    [SerializeField] private int projectileCount = 1;
    [SerializeField] private int projectileRicochet = 0;

    [Header("💥 ПРОЦЕНТЫ")]
    [Tooltip("Шанс крита [0..1]. 0.05 = 5%.")]
    [SerializeField][Range(0f, 1f)] private float critChance = 0f;
    [Tooltip("Скорость атаки. 1.0 = стандарт. 1.5 = атакует в 1.5x чаще.")]
    [SerializeField][Range(0.1f, 5f)] private float attackSpeed = 1.0f;

    [Header("ГЛОБАЛЬНЫЕ МНОЖИТЕЛИ (1.0 = стандарт)")]
    [Tooltip("Множитель урона ВСЕХ оружий. 1.2 = +20% урона. Маг = 1.8.")]
    [SerializeField][Range(0.1f, 20f)] private float damageMultiplier = 1.0f;
    [Tooltip("Множитель крит-урона. 2.0 = x2 урона при крите.")]
    [SerializeField][Range(0.1f, 5f)] private float critDamageMultiplier = 2.0f;
    [Tooltip("Множитель радиуса ВСЕХ атак (меч, аура). 1.2 = +20% радиуса.")]
    [SerializeField][Range(0.1f, 5f)] private float attackRadiusMultiplier = 1.0f;
    [Tooltip("Множитель скорости снарядов. 1.3 = снаряды летят на 30% быстрее.")]
    [SerializeField][Range(0.1f, 5f)] private float projectileSpeedMultiplier = 1.0f;
    [Tooltip("Множитель длительности эффектов (яд, лужи). 1.5 = эффекты на 50% дольше.")]
    [SerializeField][Range(0.1f, 5f)] private float durationMultiplier = 1.0f;
    [Tooltip("Множитель скорости движения. 0.85 = медленнее. 1.15 = быстрее.")]
    [SerializeField][Range(0.1f, 3f)] private float moveSpeedMultiplier = 1.0f;
    [Tooltip("Множитель получаемого опыта. 1.5 = +50% XP.")]
    [SerializeField][Range(0f, 3f)] private float xpMultiplier = 1.0f;

    [Header("💀 СЛОЖНОСТЬ ВРАГОВ")]
    [SerializeField][Range(0f, 500f)] private float enemyDifficultyBonus = 0f;

    private PlayerStats _playerStats;

    // Базовое HP из Inspector — используется как основа для мультипликатора персонажа
    private int _baseMaxHealth;

    private void Awake()
    {
        // Кэшируем базовое HP ДО применения персонажа
        _baseMaxHealth = maxHealth;
    }

    private void Start()
    {
        _playerStats = GetComponent<PlayerStats>();
        if (_playerStats == null)
            Debug.LogError("[CharacterMultipliers] PlayerStats не найден!");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПРИМЕНИТЬ ПЕРСОНАЖА
    // Вызывается из CharacterSelectManager ДО InitializeStatSheet
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Перезаписывает все поля данными из CharacterDefinition.
    /// Вызывается из CharacterSelectManager.ApplySelectedCharacter().
    /// Должен вызываться ДО InitializeStatSheet().
    /// </summary>
    public void ApplyDefinition(CharacterDefinition def)
    {
        if (def == null) return;

        Debug.Log($"[CharacterMultipliers] ApplyDefinition: {def.displayName}");
        Debug.Log($"[CharacterMultipliers]   Input: maxHealth={def.maxHealth}, damage={def.damage}, moveSpeed={def.moveSpeed}");
        Debug.Log($"[CharacterMultipliers]   Base HP (from Inspector): {_baseMaxHealth}");

        // maxHealth в CharacterDefinition — МНОЖИТЕЛЬ (Range 0.5f..2f), а не абсолютное значение!
        // Применяем к базовому HP из Inspector: 100 * 1.0 = 100, 100 * 0.8 = 80 (маг), 100 * 1.5 = 150 (воин)
        // Если def.maxHealth > 10 — скорее всего это абсолютное значение из старого префаба, используем напрямую
        if (def.maxHealth <= 10f)
        {
            maxHealth = Mathf.Max(1, Mathf.RoundToInt(_baseMaxHealth * def.maxHealth));
            Debug.Log($"[CharacterMultipliers]   maxHealth as MULTIPLIER: {_baseMaxHealth} x {def.maxHealth} = {maxHealth}");
        }
        else
        {
            maxHealth = Mathf.Max(1, Mathf.RoundToInt(def.maxHealth));
            Debug.Log($"[CharacterMultipliers]   maxHealth as ABSOLUTE: {maxHealth}");
        }

        // Поля удалённые из новой CharacterDefinition:
        // healthRegeneration, extraJumps, pickupRadius, jumpHeight,
        // projectileCount, projectileRicochet — сохраняем Inspector-значения.

        // critChance удалён из CharacterDefinition — сохраняем Inspector-значение.

        // attackSpeed и xpMultiplier остались в CharacterDefinition.
        attackSpeed  = def.attackSpeed;
        xpMultiplier = def.xpMultiplier;

        // damageMultiplier переименован в damage в новой CharacterDefinition.
        damageMultiplier = def.damage;

        // Удалены: critDamageMultiplier, attackRadiusMultiplier,
        // projectileSpeedMultiplier, durationMultiplier — сохраняем Inspector-значения.

        // moveSpeedMultiplier переименован в moveSpeed в новой CharacterDefinition.
        moveSpeedMultiplier = def.moveSpeed;

        // enemyDifficultyBonus удалён из CharacterDefinition — сохраняем Inspector-значение.

        Debug.Log($"[CharacterMultipliers] Персонаж применён: {def.displayName} | " +
                  $"HP={maxHealth} | DMG x{damageMultiplier:F2} | SPD x{moveSpeedMultiplier:F2}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ STATSHEET
    // Вызывается из PlayerStats.OnNetworkSpawn()
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Записывает ВСЕ характеристики персонажа в PlayerStatSheet.
    /// Вызывается из PlayerStats.OnNetworkSpawn() при спавне.
    ///
    /// Если ApplyDefinition() был вызван раньше — StatSheet получит
    /// характеристики персонажа. Иначе — дефолты из Inspector.
    /// </summary>
    public void InitializeStatSheet(PlayerStatSheet sheet)
    {
        if (sheet == null) 
        {
            Debug.LogError("[CharacterMultipliers] InitializeStatSheet: sheet is NULL!");
            return;
        }

        Debug.Log($"[CharacterMultipliers] InitializeStatSheet: HP={maxHealth}, DMGx{damageMultiplier:F2}, ATKx{attackSpeed:F2}");

        // Урон и скорость атаки
        sheet.SetBase(StatType.DamageMultiplier,    damageMultiplier);
        sheet.SetBase(StatType.AttackSpeed,         attackSpeed);

        // Критические удары
        sheet.SetBase(StatType.CritChance,          critChance);
        sheet.SetBase(StatType.CritMultiplier,      critDamageMultiplier);

        // Опыт и подбор
        sheet.SetBase(StatType.XpMultiplier,        xpMultiplier);
        sheet.SetBase(StatType.PickupRange,         (float)pickupRadius);

        // Движение
        sheet.SetBase(StatType.MoveSpeedMultiplier, moveSpeedMultiplier);
        sheet.SetBase(StatType.ExtraJumps,          (float)extraJumps);
        // jumpHeight=5 -> x1.0, jumpHeight=7 -> x1.4, jumpHeight=10 -> x2.0
        sheet.SetBase(StatType.JumpForceMultiplier, jumpHeight / 5f);

        // Глобальные множители оружий — применяются КО ВСЕМ оружиям автоматически
        sheet.SetBase(StatType.AttackRadiusMultiplier,    attackRadiusMultiplier);
        sheet.SetBase(StatType.ProjectileSpeedMultiplier, projectileSpeedMultiplier);
        sheet.SetBase(StatType.DurationMultiplier,        durationMultiplier);

        // HP — читается PlayerStats.ApplyCharacterDefinition()
        sheet.SetBase(StatType.MaxHealth, (float)maxHealth);

        Debug.Log($"[CharacterMultipliers] StatSheet готов: " +
                  $"HP={maxHealth} | DMG x{damageMultiplier:F2} | ATK x{attackSpeed:F2} | " +
                  $"Crit={critChance*100f:F1}% | Spd x{moveSpeedMultiplier:F2} | " +
                  $"Jumps={1+extraJumps} | Radius x{attackRadiusMultiplier:F2}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GETTERS
    // ─────────────────────────────────────────────────────────────────────────

    public int   GetMaxHealth()             => maxHealth;
    public int   GetHealthRegeneration()    => healthRegeneration;
    public int   GetExtraJumps()            => extraJumps;
    public int   GetPickupRadius()          => pickupRadius;
    public int   GetJumpHeight()            => jumpHeight;
    public int   GetProjectileCount()       => projectileCount;
    public int   GetProjectileRicochet()    => projectileRicochet;
    public float GetCritChance()            => critChance;
    public float GetAttackSpeed()           => attackSpeed;
    public float GetEnemyDifficultyBonus()  => enemyDifficultyBonus;
    public float GetDamageMultiplier()      => damageMultiplier;
    public float GetCritDamageMultiplier()  => critDamageMultiplier;
    public float GetAttackRadiusMultiplier()    => attackRadiusMultiplier;
    public float GetProjectileSpeedMultiplier() => projectileSpeedMultiplier;
    public float GetDurationMultiplier()    => durationMultiplier;
    public float GetMoveSpeedMultiplier()   => moveSpeedMultiplier;
    public float GetXpMultiplier()          => xpMultiplier;

    // ─────────────────────────────────────────────────────────────────────────
    // SETTERS (изменяют поля напрямую — для предметов и событий)
    // Новый код: используй PlayerStats.AddStatModifier() вместо этих методов
    // ─────────────────────────────────────────────────────────────────────────

    public void AddMaxHealth(int amount)
    {
        maxHealth += amount;
        Debug.Log($"[CharacterMultipliers] HP: +{amount} = {maxHealth}");
    }

    public void AddHealthRegeneration(int amount)
    {
        healthRegeneration += amount;
        Debug.Log($"[CharacterMultipliers] Реген: +{amount} = {healthRegeneration} ({healthRegeneration*0.1f:F1} HP/с)");
    }

    public void AddExtraJumps(int amount)         { extraJumps          += amount; }
    public void AddPickupRadius(int amount)       { pickupRadius         += amount; }
    public void AddJumpHeight(int amount)         { jumpHeight           += amount; }
    public void AddProjectileCount(int amount)    { projectileCount      += amount; }
    public void AddProjectileRicochet(int amount) { projectileRicochet   += amount; }

    public void AddEnemyDifficultyBonus(float amount)
    {
        enemyDifficultyBonus = Mathf.Max(0f, enemyDifficultyBonus + amount);
    }

    // Legacy setters — обратная совместимость
    public void AddCritChance(float v)               { critChance = Mathf.Clamp01(critChance + v); }
    public void AddAttackSpeed(float v)              { attackSpeed += v; }
    public void AddDamageMultiplier(float v)         { damageMultiplier += v; }
    public void AddCritDamageMultiplier(float v)     { critDamageMultiplier += v; }
    public void AddAttackRadiusMultiplier(float v)   { attackRadiusMultiplier += v; }
    public void AddProjectileSpeedMultiplier(float v){ projectileSpeedMultiplier += v; }
    public void AddDurationMultiplier(float v)       { durationMultiplier += v; }
    public void AddMoveSpeedMultiplier(float v)      { moveSpeedMultiplier += v; }
    public void AddXpMultiplier(float v)             { xpMultiplier += v; }

    [ContextMenu("Вывести характеристики")]
    public void PrintCharacteristics()
    {
        Debug.Log($"[CharacterMultipliers]\n" +
                  $"  HP={maxHealth} Реген={healthRegeneration*0.1f:F1}/с\n" +
                  $"  Прыжки={1+extraJumps} Высота x{jumpHeight/5f:F2} Скорость x{moveSpeedMultiplier:F2}\n" +
                  $"  Урон x{damageMultiplier:F2} АтакСкор x{attackSpeed:F2}\n" +
                  $"  Крит={critChance*100f:F1}% x{critDamageMultiplier:F1}\n" +
                  $"  Снаряды={projectileCount} Радиус x{attackRadiusMultiplier:F2} ПrojSpd x{projectileSpeedMultiplier:F2}\n" +
                  $"  Длительность x{durationMultiplier:F2} XP x{xpMultiplier:F2} Подбор={pickupRadius}");
    }
}
