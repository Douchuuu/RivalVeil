/// <summary>
/// StatType — все типы характеристик игрока в одном месте.
///
/// ════════════════════════════════════════════════════════════════════
/// КАК РАБОТАЕТ СИСТЕМА ХАРАКТЕРИСТИК (Vampire Survivors / Megabonk)
/// ════════════════════════════════════════════════════════════════════
///
/// У ИГРОКА ТРИ СЛОЯ ХАРАКТЕРИСТИК:
///
///   СЛОЙ 1 — CharacterMultipliers (Inspector на PlayerPrefab)
///     Это ДЕФОЛТНЫЕ значения для "безымянного" игрока.
///     Если нет выбора персонажа — игра работает с этими числами.
///     Inspector: maxHealth=100, critChance=0, damageMultiplier=1.0 и т.д.
///
///   СЛОЙ 2 — CharacterDefinition (ScriptableObject ассет)
///     Конкретный персонаж: Воин, Маг, Лучник...
///     Перезаписывает поля CharacterMultipliers через ApplyDefinition().
///     Пример: Воин maxHealth=150, Маг maxHealth=80, damageMultiplier=1.8
///
///   СЛОЙ 3 — PlayerStatSheet (рантайм, только в памяти)
///     Живые характеристики с модификаторами от предметов/апгрейдов/тотемов.
///     Начальные значения = Слой 1 или 2 (через InitializeStatSheet).
///     Предмет "Факел" → AddModifier(AttackSpeed, +0.1, Additive)
///     Тотем урона     → AddModifier(DamageMultiplier, +0.05, Additive)
///
/// ФОРМУЛА ФИНАЛЬНОГО УРОНА:
///   finalDamage = weapon.baseDamage               ← из Inspector оружия
///               × charDamageMultiplier            ← из StatSheet (Слой 2)
///               × (1 + upgradeBonuses)            ← из апгрейдов оружия
///               × critMultiplier (если крит)      ← из StatSheet
///               × 1.07 (если цель в кислоте)      ← из AcidDebuffComponent
///
/// ПРИМЕР: Маг бьёт мечом
///   sword.baseDamage = 20
///   Маг: damageMultiplier = 1.8
///   Апгрейд меча: +15% урона
///   Итог: 20 × 1.8 × 1.15 = 41.4 урона
///
/// ════════════════════════════════════════════════════════════════════
/// КАК ДОБАВИТЬ НОВУЮ ХАРАКТЕРИСТИКУ:
///   1. Добавь значение сюда
///   2. Добавь базовое значение в PlayerStatSheet._baseValues
///   3. Добавь sheet.SetBase(StatType.XXX, value) в CharacterMultipliers.InitializeStatSheet()
///   4. Читай через playerStats.StatSheet.GetStat(StatType.XXX) в нужном месте
/// ════════════════════════════════════════════════════════════════════
/// </summary>
public enum StatType
{
    // ── Здоровье ──────────────────────────────────────────────────────────────
    MaxHealth       = 0,   // только для синхронизации, реальный HP в PlayerStats.maxHealth

    // ── Движение ──────────────────────────────────────────────────────────────
    MoveSpeed       = 1,   // абсолютная скорость (не используется напрямую)
    PickupRange     = 2,   // радиус подбора орбов, читается в PlayerMovement.CollectItems()

    /// <summary>
    /// Множитель скорости движения. База = 1.0.
    /// Воин 0.85 = медленнее, Маг 1.15 = быстрее.
    /// PlayerMovement умножает walkSpeed и maxAirSpeed на это значение.
    /// Тотем скорости: AddModifier(MoveSpeedMultiplier, +0.10, Additive)
    /// </summary>
    MoveSpeedMultiplier = 51,

    /// <summary>
    /// Количество дополнительных прыжков. База = 0 (только с земли).
    /// extraJumps = 1 → maxJumpCount = 2 (как в Megabonk — земля + 1 воздух).
    /// Читается в PlayerMovement при нажатии Jump.
    /// </summary>
    ExtraJumps = 52,

    /// <summary>
    /// Множитель силы прыжка. База = 1.0.
    /// Персонаж с jumpHeight=7: JumpForceMultiplier = 7/5 = 1.4 → прыгает в 1.4× выше.
    /// Читается в PlayerMovement при нажатии Jump.
    /// </summary>
    JumpForceMultiplier = 53,

    // ── Урон ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// ГЛАВНЫЙ множитель урона. База = 1.0.
    /// Все оружия умножают свой baseDamage на это значение.
    /// Воин: 1.2 → меч 20 × 1.2 = 24 урона.
    /// Маг:  1.8 → меч 20 × 1.8 = 36 урона.
    /// </summary>
    DamageMultiplier = 10,

    /// <summary>
    /// Множитель скорости атаки. База = 1.0.
    /// Уменьшает кулдаун между атаками: cooldown / AttackSpeed.
    /// attackSpeed=1.5 → кулдаун в 1.5× короче.
    /// </summary>
    AttackSpeed = 11,

    // ── Критические удары ─────────────────────────────────────────────────────
    CritChance         = 20,  // [0..1], 0.05 = 5% шанс крита
    CritMultiplier     = 21,  // ×2.0 = двойной урон при крите (дефолт)
    OvercritChance     = 22,  // шанс "крита поверх крита" (особая механика)
    OvercritMultiplier = 23,  // ×3.5 = тройной+ урон при оверкрите

    // ── Снаряды ───────────────────────────────────────────────────────────────
    ProjectileCount  = 30,  // количество снарядов за атаку
    ProjectileSpread = 31,  // разброс снарядов в градусах
    PiercingCount    = 32,  // количество врагов которых пробивает снаряд

    // ── Защита ────────────────────────────────────────────────────────────────
    Armor           = 40,  // HP щита (плоское значение, не множитель)
    DamageReduction = 41,  // снижение входящего урона [0..0.9], max 90%

    // ── Опыт ──────────────────────────────────────────────────────────────────
    XpMultiplier = 50,  // множитель получаемого опыта. 1.5 = +50% XP

    // ── Глобальные множители оружий (применяются КО ВСЕМ оружиям) ─────────────
    /// <summary>
    /// Глобальный множитель радиуса/дальности атак. База = 1.0.
    /// SwordWeapon: attackRange × AttackRadiusMultiplier
    /// AuraWeapon:  auraRadius  × AttackRadiusMultiplier
    /// Воин: 1.2 → радиус удара на 20% больше.
    /// </summary>
    AttackRadiusMultiplier = 60,

    /// <summary>
    /// Глобальный множитель скорости снарядов. База = 1.0.
    /// SpeedStaffWeapon, AcidFlaskWeapon применяют его.
    /// projectileSpeed × ProjectileSpeedMultiplier.
    /// </summary>
    ProjectileSpeedMultiplier = 61,

    /// <summary>
    /// Глобальный множитель длительности эффектов. База = 1.0.
    /// AcidPuddle: duration × DurationMultiplier.
    /// </summary>
    DurationMultiplier = 62,
}
/*/// <summary>
/// StatType — все типы характеристик игрока в одном месте.
///
/// ════════════════════════════════════════════════════════════════════
/// КАК РАБОТАЕТ СИСТЕМА ХАРАКТЕРИСТИК (Vampire Survivors / Megabonk)
/// ════════════════════════════════════════════════════════════════════
///
/// У ИГРОКА ТРИ СЛОЯ ХАРАКТЕРИСТИК:
///
///   СЛОЙ 1 — CharacterMultipliers (Inspector на PlayerPrefab)
///     Это ДЕФОЛТНЫЕ значения для "безымянного" игрока.
///     Если нет выбора персонажа — игра работает с этими числами.
///     Inspector: maxHealth=100, critChance=0, damageMultiplier=1.0 и т.д.
///
///   СЛОЙ 2 — CharacterDefinition (ScriptableObject ассет)
///     Конкретный персонаж: Воин, Маг, Лучник...
///     Перезаписывает поля CharacterMultipliers через ApplyDefinition().
///     Пример: Воин maxHealth=150, Маг maxHealth=80, damageMultiplier=1.8
///
///   СЛОЙ 3 — PlayerStatSheet (рантайм, только в памяти)
///     Живые характеристики с модификаторами от предметов/апгрейдов/тотемов.
///     Начальные значения = Слой 1 или 2 (через InitializeStatSheet).
///     Предмет "Факел" → AddModifier(AttackSpeed, +0.1, Additive)
///     Тотем урона     → AddModifier(DamageMultiplier, +0.05, Additive)
///
/// ФОРМУЛА ФИНАЛЬНОГО УРОНА:
///   finalDamage = weapon.baseDamage               ← из Inspector оружия
///               × charDamageMultiplier            ← из StatSheet (Слой 2)
///               × (1 + upgradeBonuses)            ← из апгрейдов оружия
///               × critMultiplier (если крит)      ← из StatSheet
///               × 1.07 (если цель в кислоте)      ← из AcidDebuffComponent
///
/// ПРИМЕР: Маг бьёт мечом
///   sword.baseDamage = 20
///   Маг: damageMultiplier = 1.8
///   Апгрейд меча: +15% урона
///   Итог: 20 × 1.8 × 1.15 = 41.4 урона
///
/// ════════════════════════════════════════════════════════════════════
/// КАК ДОБАВИТЬ НОВУЮ ХАРАКТЕРИСТИКУ:
///   1. Добавь значение сюда
///   2. Добавь базовое значение в PlayerStatSheet._baseValues
///   3. Добавь sheet.SetBase(StatType.XXX, value) в CharacterMultipliers.InitializeStatSheet()
///   4. Читай через playerStats.StatSheet.GetStat(StatType.XXX) в нужном месте
/// ════════════════════════════════════════════════════════════════════
/// </summary>
public enum StatType
{
    // ── Здоровье ──────────────────────────────────────────────────────────────
    MaxHealth       = 0,   // только для синхронизации, реальный HP в PlayerStats.maxHealth

    // ── Движение ──────────────────────────────────────────────────────────────
    MoveSpeed       = 1,   // абсолютная скорость (не используется напрямую)
    PickupRange     = 2,   // радиус подбора орбов, читается в PlayerMovement.CollectItems()

    /// <summary>
    /// Множитель скорости движения. База = 1.0.
    /// Воин 0.85 = медленнее, Маг 1.15 = быстрее.
    /// PlayerMovement умножает walkSpeed и maxAirSpeed на это значение.
    /// Тотем скорости: AddModifier(MoveSpeedMultiplier, +0.10, Additive)
    /// </summary>
    MoveSpeedMultiplier = 51,

    /// <summary>
    /// Количество дополнительных прыжков. База = 0 (только с земли).
    /// extraJumps = 1 → maxJumpCount = 2 (как в Megabonk — земля + 1 воздух).
    /// Читается в PlayerMovement при нажатии Jump.
    /// </summary>
    ExtraJumps = 52,

    /// <summary>
    /// Множитель силы прыжка. База = 1.0.
    /// Персонаж с jumpHeight=7: JumpForceMultiplier = 7/5 = 1.4 → прыгает в 1.4× выше.
    /// Читается в PlayerMovement при нажатии Jump.
    /// </summary>
    JumpForceMultiplier = 53,

    // ── Урон ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// ГЛАВНЫЙ множитель урона. База = 1.0.
    /// Все оружия умножают свой baseDamage на это значение.
    /// Воин: 1.2 → меч 20 × 1.2 = 24 урона.
    /// Маг:  1.8 → меч 20 × 1.8 = 36 урона.
    /// </summary>
    DamageMultiplier = 10,

    /// <summary>
    /// Множитель скорости атаки. База = 1.0.
    /// Уменьшает кулдаун между атаками: cooldown / AttackSpeed.
    /// attackSpeed=1.5 → кулдаун в 1.5× короче.
    /// </summary>
    AttackSpeed = 11,

    // ── Критические удары ─────────────────────────────────────────────────────
    CritChance         = 20,  // [0..1], 0.05 = 5% шанс крита
    CritMultiplier     = 21,  // ×2.0 = двойной урон при крите (дефолт)
    OvercritChance     = 22,  // шанс "крита поверх крита" (особая механика)
    OvercritMultiplier = 23,  // ×3.5 = тройной+ урон при оверкрите

    // ── Снаряды ───────────────────────────────────────────────────────────────
    ProjectileCount  = 30,  // количество снарядов за атаку
    ProjectileSpread = 31,  // разброс снарядов в градусах
    PiercingCount    = 32,  // количество врагов которых пробивает снаряд

    // ── Защита ────────────────────────────────────────────────────────────────
    Armor           = 40,  // HP щита (плоское значение, не множитель)
    DamageReduction = 41,  // снижение входящего урона [0..0.9], max 90%

    // ── Опыт ──────────────────────────────────────────────────────────────────
    XpMultiplier = 50,  // множитель получаемого опыта. 1.5 = +50% XP

    // ── Глобальные множители оружий (применяются КО ВСЕМ оружиям) ─────────────
    /// <summary>
    /// Глобальный множитель радиуса/дальности атак. База = 1.0.
    /// SwordWeapon: attackRange × AttackRadiusMultiplier
    /// AuraWeapon:  auraRadius  × AttackRadiusMultiplier
    /// Воин: 1.2 → радиус удара на 20% больше.
    /// </summary>
    AttackRadiusMultiplier = 60,

    /// <summary>
    /// Глобальный множитель скорости снарядов. База = 1.0.
    /// SpeedStaffWeapon, AcidFlaskWeapon применяют его.
    /// projectileSpeed × ProjectileSpeedMultiplier.
    /// </summary>
    ProjectileSpeedMultiplier = 61,

    /// <summary>
    /// Глобальный множитель длительности эффектов. База = 1.0.
    /// AcidPuddle: duration × DurationMultiplier.
    /// </summary>
    DurationMultiplier = 62,
}
*/