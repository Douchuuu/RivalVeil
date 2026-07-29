using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// ItemInstance — экземпляр предмета.
///
/// ══════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЕ (ДЕНЬ 1-2):
///
///   БЫЛО: _blackHoleActiveCount — static поле.
///         В Competitive режиме оба игрока живут в одном процессе.
///         Player 1 подбирает Чёрную Дыру → счётчик = 1.
///         Player 2 подбирает Чёрную Дыру → счётчик уже 1 → не активируется.
///         Тихий баг: ошибок нет, предмет просто не работает у второго игрока.
///
///   СТАЛО: _blackHoleActiveCount хранится в ItemInventory (per-player).
///          ItemInstance получает ссылку на ItemInventory через Activate().
///          Каждый игрок имеет свой независимый счётчик.
///
/// ══════════════════════════════════════════════════════════════
/// МАРШРУТИЗАЦИЯ ЭФФЕКТОВ:
///
///   AttackSpeedBonus  → StatSheet.AttackSpeed  (Additive)
///   DamageBonus       → StatSheet.DamageMultiplier (Additive)
///   DamagePerKill     → StatSheet.DamageMultiplier (Additive, per kill)
///   HealthRegeneration → CharacterMultipliers.AddHealthRegeneration()
///   ExtraExpOrbChance → только данные, проверка в EnemyHealth
///   VampirismOnHit    → PlayerStats.Heal()
///   ExpOrbAttraction  → ExperienceOrbPool.AttractAll()
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class ItemInstance
{
    public ItemDefinition Definition { get; private set; }
    public string UniqueId { get; private set; }
    public bool IsActive { get; private set; }

    private PlayerStats           _playerStats;
    private WeaponManager         _weaponManager;
    private CharacterMultipliers  _characterMultipliers;

    // ── ИСПРАВЛЕНИЕ: ссылка на инвентарь вместо статического счётчика ─────────
    // Черная Дыра теперь отслеживается per-inventory, а не глобально.
    // ItemInventory.BlackHoleActiveCount — экземплярное поле, уникальное для каждого игрока.
    private ItemInventory _inventory;

    private Dictionary<ItemEffectType, float> _effectCooldowns = new Dictionary<ItemEffectType, float>();
    private Dictionary<ItemEffectType, float> _effectTimers    = new Dictionary<ItemEffectType, float>();

    private readonly List<ItemEffectType> _cooldownKeys = new List<ItemEffectType>();

    // ── Голова Демона: текущий стак этого экземпляра ──────────────────────────
    private float _demonHeadStack = 0f;
    private const float DemonHeadMaxPerInstance = 1.0f;

    // ── Чёрная Дыра ───────────────────────────────────────────────────────────
    // ИСПРАВЛЕНИЕ: убран static int _blackHoleActiveCount = 0.
    // Теперь используется _inventory.BlackHoleActiveCount.
    private bool _isBlackHoleActive = false;

    // ─────────────────────────────────────────────────────────────────────────

    public ItemInstance(ItemDefinition definition)
    {
        Definition = definition;
        UniqueId   = Guid.NewGuid().ToString();
        IsActive   = false;

        foreach (var effect in definition.effects)
        {
            _effectCooldowns[effect.type] = 0f;
            _effectTimers[effect.type]    = 0f;
            if (!_cooldownKeys.Contains(effect.type))
                _cooldownKeys.Add(effect.type);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АКТИВАЦИЯ / ДЕАКТИВАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ИСПРАВЛЕНИЕ: добавлен параметр ItemInventory inventory.
    /// Раньше: Activate(PlayerStats, WeaponManager)
    /// Теперь: Activate(PlayerStats, WeaponManager, ItemInventory)
    ///
    /// inventory нужен чтобы читать/писать BlackHoleActiveCount —
    /// per-player счётчик Чёрных Дыр вместо статического глобального.
    /// </summary>
    public void Activate(PlayerStats playerStats, WeaponManager weaponManager, ItemInventory inventory)
    {
        if (IsActive) return;

        _playerStats          = playerStats;
        _weaponManager        = weaponManager;
        _characterMultipliers = playerStats.GetComponent<CharacterMultipliers>();
        _inventory            = inventory; // ИСПРАВЛЕНИЕ: сохраняем ссылку на инвентарь
        IsActive              = true;

        Debug.Log($"[ItemInstance] 🎁 Активирован: {Definition.displayName}");
        ApplyPermanentEffects();
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;

        _playerStats?.RemoveStatModifiersFromSource(UniqueId);

        // ИСПРАВЛЕНИЕ: используем _inventory.BlackHoleActiveCount вместо static
        if (_isBlackHoleActive && _inventory != null)
        {
            _inventory.BlackHoleActiveCount--;
            _isBlackHoleActive = false;
        }

        Debug.Log($"[ItemInstance] 🎁 Деактивирован: {Definition.displayName} (модификаторы удалены)");

        _playerStats          = null;
        _weaponManager        = null;
        _characterMultipliers = null;
        _inventory            = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    public void UpdateEffects()
    {
        if (!IsActive || _playerStats == null) return;

        float dt = Time.deltaTime;
        for (int i = 0; i < _cooldownKeys.Count; i++)
        {
            var key = _cooldownKeys[i];
            if (_effectCooldowns[key] > 0f)
                _effectCooldowns[key] -= dt;
        }

        foreach (var effect in Definition.effects)
        {
            switch (effect.type)
            {
                case ItemEffectType.HealthRegeneration:
                    UpdateHealthRegen(effect);
                    break;

                case ItemEffectType.ExpOrbAttraction:
                    if (_isBlackHoleActive)
                        UpdateBlackHole(effect);
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОСТОЯННЫЕ ЭФФЕКТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyPermanentEffects()
    {
        foreach (var effect in Definition.effects)
        {
            switch (effect.type)
            {
                case ItemEffectType.AttackSpeedBonus:
                    _playerStats.AddStatModifier(new StatModifier(
                        StatType.AttackSpeed,
                        effect.value / 100f,
                        ModifierType.Additive,
                        source: UniqueId));
                    Debug.Log($"[ItemInstance] 🔥 Факел: +{effect.value:F0}% скорость атаки → StatSheet");
                    break;

                case ItemEffectType.HealthRegeneration:
                    _characterMultipliers?.AddHealthRegeneration((int)effect.value);
                    Debug.Log($"[ItemInstance] 🩹 Бинт: +{effect.value:F0} ед. регена");
                    break;

                case ItemEffectType.DamageBonus:
                    Debug.Log("[ItemInstance] 💎 Талисман готов. Ждёт открытия сундука...");
                    break;

                case ItemEffectType.ExtraExpOrbChance:
                    Debug.Log($"[ItemInstance] 🎓 Шляпа: +{effect.value:F0}% шанс доп. орба");
                    break;

                // ИСПРАВЛЕНИЕ: используем _inventory.BlackHoleActiveCount вместо static _blackHoleActiveCount
                case ItemEffectType.ExpOrbAttraction:
                    if (_inventory != null && _inventory.BlackHoleActiveCount == 0)
                    {
                        _inventory.BlackHoleActiveCount++;
                        _isBlackHoleActive = true;
                        Debug.Log($"[ItemInstance] 🌀 Чёрная Дыра: АКТИВИРОВАНА (inventory: {_inventory.name ?? "unknown"})");
                    }
                    else
                    {
                        Debug.Log("[ItemInstance] 🌀 Чёрная Дыра: уже активна у этого игрока (стакинг запрещён)");
                    }
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СОБЫТИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public void OnChestOpened()
    {
        if (!IsActive || _playerStats == null) return;

        foreach (var effect in Definition.effects)
        {
            if (effect.type != ItemEffectType.DamageBonus) continue;

            float bonus = effect.value / 100f;
            _playerStats.AddStatModifier(new StatModifier(
                StatType.DamageMultiplier,
                bonus,
                ModifierType.Additive,
                source: UniqueId));

            float total = _playerStats.StatSheet?.GetStat(StatType.DamageMultiplier) ?? 1f;
            Debug.Log($"[ItemInstance] 💎 Талисман: +{effect.value:F0}% урона → StatSheet (итого ×{total:F2})");
            break;
        }
    }

    public void OnPlayerHitEnemy(EnemyHealth enemyHealth)
    {
        if (!IsActive || _playerStats == null) return;

        foreach (var effect in Definition.effects)
        {
            if (effect.type != ItemEffectType.VampirismOnHit) continue;

            if (_effectCooldowns[ItemEffectType.VampirismOnHit] <= 0f)
            {
                float healAmount = _playerStats.MaxHealth * (effect.value / 100f);
                _playerStats.Heal(healAmount);
                _effectCooldowns[ItemEffectType.VampirismOnHit] = effect.cooldownOrDuration;
                Debug.Log($"[ItemInstance] 🧛 Челюсть: исцелено {healAmount:F1} HP");
            }
        }
    }

    public void OnPlayerKillEnemy()
    {
        if (!IsActive || _playerStats == null) return;

        foreach (var effect in Definition.effects)
        {
            if (effect.type != ItemEffectType.DamagePerKill) continue;

            if (_demonHeadStack >= DemonHeadMaxPerInstance) return;

            float bonusPerKill = effect.value / 100f;
            float actualBonus  = Mathf.Min(bonusPerKill, DemonHeadMaxPerInstance - _demonHeadStack);
            _demonHeadStack   += actualBonus;

            _playerStats.RemoveStatModifiersFromSource(UniqueId + "_demonhead");
            _playerStats.AddStatModifier(new StatModifier(
                StatType.DamageMultiplier,
                _demonHeadStack,
                ModifierType.Additive,
                source: UniqueId + "_demonhead"));

            Debug.Log($"[ItemInstance] 👿 Голова Демона: стак {_demonHeadStack * 100f:F1}% " +
                      $"/ {DemonHeadMaxPerInstance * 100f:F0}% → StatSheet");
            break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОБНОВЛЯЕМЫЕ ЭФФЕКТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateHealthRegen(ItemEffect effect)
    {
        if (_characterMultipliers == null) return;

        float totalRegen  = _characterMultipliers.GetHealthRegeneration();
        float hpPerSecond = totalRegen * 0.1f;
        float healthToAdd = hpPerSecond * Time.deltaTime;

        if (healthToAdd > 0f)
            _playerStats.Heal(healthToAdd);
    }

    private void UpdateBlackHole(ItemEffect effect)
    {
        if (!_effectTimers.ContainsKey(ItemEffectType.ExpOrbAttraction))
            _effectTimers[ItemEffectType.ExpOrbAttraction] = 0f;

        _effectTimers[ItemEffectType.ExpOrbAttraction] += Time.deltaTime;

        if (_effectTimers[ItemEffectType.ExpOrbAttraction] >= effect.cooldownOrDuration)
        {
            AttractNearbyExpOrbs();
            _effectTimers[ItemEffectType.ExpOrbAttraction] = 0f;
        }
    }

    private void AttractNearbyExpOrbs()
    {
        if (_playerStats == null) return;

        // ФИКС БАГ 2: ищем пул в сцене игрока, а не через статический Instance.
        // ExperienceOrbPool.Instance = последний созданный пул, в Competitive режиме
        // это может быть пул оппонента. Используем тот же паттерн что EnemyHealth.FindPoolInMyScene().
        var pool = FindOrbPoolInPlayerScene();
        if (pool != null)
        {
            // Передаём ClientId — пул будет притягивать только орбы этого игрока
            ulong myClientId = (_playerStats.IsSpawned) ? _playerStats.OwnerClientId : ulong.MaxValue;
            pool.AttractAll(_playerStats.transform, myClientId);
        }
        else
            Debug.LogWarning("[ItemInstance] 🌀 Чёрная Дыра: ExperienceOrbPool не найден в сцене игрока!");
    }

    /// <summary>
    /// Ищет ExperienceOrbPool в сцене игрока-владельца этого предмета.
    /// Аналог EnemyHealth.FindPoolInMyScene() — избегает захвата пула оппонента.
    /// </summary>
    private ExperienceOrbPool FindOrbPoolInPlayerScene()
    {
        if (_playerStats == null) return ExperienceOrbPool.Instance;

        // Быстрый путь: Instance уже в нужной сцене
        if (ExperienceOrbPool.Instance != null &&
            ExperienceOrbPool.Instance.gameObject.scene == _playerStats.gameObject.scene)
            return ExperienceOrbPool.Instance;

        // Медленный путь: ищем в сцене игрока (только при конфликте двух пулов)
        var roots = _playerStats.gameObject.scene.GetRootGameObjects();
        foreach (var root in roots)
        {
            var pool = root.GetComponentInChildren<ExperienceOrbPool>(true);
            if (pool != null) return pool;
        }

        // Финальный fallback
        return ExperienceOrbPool.Instance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОТЛАДКА
    // ─────────────────────────────────────────────────────────────────────────

    public float GetDemonHeadStack()  => _demonHeadStack;
    public bool  IsBlackHoleActive()  => _isBlackHoleActive;
}
/*public class ItemInstance
{
    public ItemDefinition Definition { get; private set; }
    public string UniqueId { get; private set; }
    public bool IsActive { get; private set; }

    private PlayerStats           _playerStats;
    private WeaponManager         _weaponManager;
    private CharacterMultipliers  _characterMultipliers;

    // ── ИСПРАВЛЕНИЕ: ссылка на инвентарь вместо статического счётчика ─────────
    // Черная Дыра теперь отслеживается per-inventory, а не глобально.
    // ItemInventory.BlackHoleActiveCount — экземплярное поле, уникальное для каждого игрока.
    private ItemInventory _inventory;

    private Dictionary<ItemEffectType, float> _effectCooldowns = new Dictionary<ItemEffectType, float>();
    private Dictionary<ItemEffectType, float> _effectTimers    = new Dictionary<ItemEffectType, float>();

    private readonly List<ItemEffectType> _cooldownKeys = new List<ItemEffectType>();

    // ── Голова Демона: текущий стак этого экземпляра ──────────────────────────
    private float _demonHeadStack = 0f;
    private const float DemonHeadMaxPerInstance = 1.0f;

    // ── Чёрная Дыра ───────────────────────────────────────────────────────────
    // ИСПРАВЛЕНИЕ: убран static int _blackHoleActiveCount = 0.
    // Теперь используется _inventory.BlackHoleActiveCount.
    private bool _isBlackHoleActive = false;

    // ─────────────────────────────────────────────────────────────────────────

    public ItemInstance(ItemDefinition definition)
    {
        Definition = definition;
        UniqueId   = Guid.NewGuid().ToString();
        IsActive   = false;

        foreach (var effect in definition.effects)
        {
            _effectCooldowns[effect.type] = 0f;
            _effectTimers[effect.type]    = 0f;
            if (!_cooldownKeys.Contains(effect.type))
                _cooldownKeys.Add(effect.type);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АКТИВАЦИЯ / ДЕАКТИВАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ИСПРАВЛЕНИЕ: добавлен параметр ItemInventory inventory.
    /// Раньше: Activate(PlayerStats, WeaponManager)
    /// Теперь: Activate(PlayerStats, WeaponManager, ItemInventory)
    ///
    /// inventory нужен чтобы читать/писать BlackHoleActiveCount —
    /// per-player счётчик Чёрных Дыр вместо статического глобального.
    /// </summary>
    public void Activate(PlayerStats playerStats, WeaponManager weaponManager, ItemInventory inventory)
    {
        if (IsActive) return;

        _playerStats          = playerStats;
        _weaponManager        = weaponManager;
        _characterMultipliers = playerStats.GetComponent<CharacterMultipliers>();
        _inventory            = inventory; // ИСПРАВЛЕНИЕ: сохраняем ссылку на инвентарь
        IsActive              = true;

        Debug.Log($"[ItemInstance] 🎁 Активирован: {Definition.displayName}");
        ApplyPermanentEffects();
    }

    public void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;

        _playerStats?.RemoveStatModifiersFromSource(UniqueId);

        // ИСПРАВЛЕНИЕ: используем _inventory.BlackHoleActiveCount вместо static
        if (_isBlackHoleActive && _inventory != null)
        {
            _inventory.BlackHoleActiveCount--;
            _isBlackHoleActive = false;
        }

        Debug.Log($"[ItemInstance] 🎁 Деактивирован: {Definition.displayName} (модификаторы удалены)");

        _playerStats          = null;
        _weaponManager        = null;
        _characterMultipliers = null;
        _inventory            = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    public void UpdateEffects()
    {
        if (!IsActive || _playerStats == null) return;

        float dt = Time.deltaTime;
        for (int i = 0; i < _cooldownKeys.Count; i++)
        {
            var key = _cooldownKeys[i];
            if (_effectCooldowns[key] > 0f)
                _effectCooldowns[key] -= dt;
        }

        foreach (var effect in Definition.effects)
        {
            switch (effect.type)
            {
                case ItemEffectType.HealthRegeneration:
                    UpdateHealthRegen(effect);
                    break;

                case ItemEffectType.ExpOrbAttraction:
                    if (_isBlackHoleActive)
                        UpdateBlackHole(effect);
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОСТОЯННЫЕ ЭФФЕКТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private void ApplyPermanentEffects()
    {
        foreach (var effect in Definition.effects)
        {
            switch (effect.type)
            {
                case ItemEffectType.AttackSpeedBonus:
                    _playerStats.AddStatModifier(new StatModifier(
                        StatType.AttackSpeed,
                        effect.value / 100f,
                        ModifierType.Additive,
                        source: UniqueId));
                    Debug.Log($"[ItemInstance] 🔥 Факел: +{effect.value:F0}% скорость атаки → StatSheet");
                    break;

                case ItemEffectType.HealthRegeneration:
                    _characterMultipliers?.AddHealthRegeneration((int)effect.value);
                    Debug.Log($"[ItemInstance] 🩹 Бинт: +{effect.value:F0} ед. регена");
                    break;

                case ItemEffectType.DamageBonus:
                    Debug.Log("[ItemInstance] 💎 Талисман готов. Ждёт открытия сундука...");
                    break;

                case ItemEffectType.ExtraExpOrbChance:
                    Debug.Log($"[ItemInstance] 🎓 Шляпа: +{effect.value:F0}% шанс доп. орба");
                    break;

                // ИСПРАВЛЕНИЕ: используем _inventory.BlackHoleActiveCount вместо static _blackHoleActiveCount
                case ItemEffectType.ExpOrbAttraction:
                    if (_inventory != null && _inventory.BlackHoleActiveCount == 0)
                    {
                        _inventory.BlackHoleActiveCount++;
                        _isBlackHoleActive = true;
                        Debug.Log($"[ItemInstance] 🌀 Чёрная Дыра: АКТИВИРОВАНА (inventory: {_inventory.name ?? "unknown"})");
                    }
                    else
                    {
                        Debug.Log("[ItemInstance] 🌀 Чёрная Дыра: уже активна у этого игрока (стакинг запрещён)");
                    }
                    break;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СОБЫТИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public void OnChestOpened()
    {
        if (!IsActive || _playerStats == null) return;

        foreach (var effect in Definition.effects)
        {
            if (effect.type != ItemEffectType.DamageBonus) continue;

            float bonus = effect.value / 100f;
            _playerStats.AddStatModifier(new StatModifier(
                StatType.DamageMultiplier,
                bonus,
                ModifierType.Additive,
                source: UniqueId));

            float total = _playerStats.StatSheet?.GetStat(StatType.DamageMultiplier) ?? 1f;
            Debug.Log($"[ItemInstance] 💎 Талисман: +{effect.value:F0}% урона → StatSheet (итого ×{total:F2})");
            break;
        }
    }

    public void OnPlayerHitEnemy(EnemyHealth enemyHealth)
    {
        if (!IsActive || _playerStats == null) return;

        foreach (var effect in Definition.effects)
        {
            if (effect.type != ItemEffectType.VampirismOnHit) continue;

            if (_effectCooldowns[ItemEffectType.VampirismOnHit] <= 0f)
            {
                float healAmount = _playerStats.MaxHealth * (effect.value / 100f);
                _playerStats.Heal(healAmount);
                _effectCooldowns[ItemEffectType.VampirismOnHit] = effect.cooldownOrDuration;
                Debug.Log($"[ItemInstance] 🧛 Челюсть: исцелено {healAmount:F1} HP");
            }
        }
    }

    public void OnPlayerKillEnemy()
    {
        if (!IsActive || _playerStats == null) return;

        foreach (var effect in Definition.effects)
        {
            if (effect.type != ItemEffectType.DamagePerKill) continue;

            if (_demonHeadStack >= DemonHeadMaxPerInstance) return;

            float bonusPerKill = effect.value / 100f;
            float actualBonus  = Mathf.Min(bonusPerKill, DemonHeadMaxPerInstance - _demonHeadStack);
            _demonHeadStack   += actualBonus;

            _playerStats.RemoveStatModifiersFromSource(UniqueId + "_demonhead");
            _playerStats.AddStatModifier(new StatModifier(
                StatType.DamageMultiplier,
                _demonHeadStack,
                ModifierType.Additive,
                source: UniqueId + "_demonhead"));

            Debug.Log($"[ItemInstance] 👿 Голова Демона: стак {_demonHeadStack * 100f:F1}% " +
                      $"/ {DemonHeadMaxPerInstance * 100f:F0}% → StatSheet");
            break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОБНОВЛЯЕМЫЕ ЭФФЕКТЫ
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateHealthRegen(ItemEffect effect)
    {
        if (_characterMultipliers == null) return;

        float totalRegen  = _characterMultipliers.GetHealthRegeneration();
        float hpPerSecond = totalRegen * 0.1f;
        float healthToAdd = hpPerSecond * Time.deltaTime;

        if (healthToAdd > 0f)
            _playerStats.Heal(healthToAdd);
    }

    private void UpdateBlackHole(ItemEffect effect)
    {
        if (!_effectTimers.ContainsKey(ItemEffectType.ExpOrbAttraction))
            _effectTimers[ItemEffectType.ExpOrbAttraction] = 0f;

        _effectTimers[ItemEffectType.ExpOrbAttraction] += Time.deltaTime;

        if (_effectTimers[ItemEffectType.ExpOrbAttraction] >= effect.cooldownOrDuration)
        {
            AttractNearbyExpOrbs();
            _effectTimers[ItemEffectType.ExpOrbAttraction] = 0f;
        }
    }

    private void AttractNearbyExpOrbs()
    {
        if (_playerStats == null) return;

        // ФИКС БАГ 2: ищем пул в сцене игрока, а не через статический Instance.
        // ExperienceOrbPool.Instance = последний созданный пул, в Competitive режиме
        // это может быть пул оппонента. Используем тот же паттерн что EnemyHealth.FindPoolInMyScene().
        var pool = FindOrbPoolInPlayerScene();
        if (pool != null)
        {
            // Передаём ClientId — пул будет притягивать только орбы этого игрока
            ulong myClientId = (_playerStats.IsSpawned) ? _playerStats.OwnerClientId : ulong.MaxValue;
            pool.AttractAll(_playerStats.transform, myClientId);
        }
        else
            Debug.LogWarning("[ItemInstance] 🌀 Чёрная Дыра: ExperienceOrbPool не найден в сцене игрока!");
    }

    /// <summary>
    /// Ищет ExperienceOrbPool в сцене игрока-владельца этого предмета.
    /// Аналог EnemyHealth.FindPoolInMyScene() — избегает захвата пула оппонента.
    /// </summary>
    private ExperienceOrbPool FindOrbPoolInPlayerScene()
    {
        if (_playerStats == null) return ExperienceOrbPool.Instance;

        // Быстрый путь: Instance уже в нужной сцене
        if (ExperienceOrbPool.Instance != null &&
            ExperienceOrbPool.Instance.gameObject.scene == _playerStats.gameObject.scene)
            return ExperienceOrbPool.Instance;

        // Медленный путь: ищем в сцене игрока (только при конфликте двух пулов)
        var roots = _playerStats.gameObject.scene.GetRootGameObjects();
        foreach (var root in roots)
        {
            var pool = root.GetComponentInChildren<ExperienceOrbPool>(true);
            if (pool != null) return pool;
        }

        // Финальный fallback
        return ExperienceOrbPool.Instance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОТЛАДКА
    // ─────────────────────────────────────────────────────────────────────────

    public float GetDemonHeadStack()  => _demonHeadStack;
    public bool  IsBlackHoleActive()  => _isBlackHoleActive;
}
*/