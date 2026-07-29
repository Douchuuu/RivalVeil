using UnityEngine;
using Unity.Netcode;
using VContainer;
using VContainer.Unity;
using System.Collections;

/// <summary>
/// CharacterSelectManager v5.4 — ИСПРАВЛЕНО: NetworkVariable права изменены на Server
/// для корректной работы Dedicated Server архитектуры.
/// 
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО (v5.4):
/// ══════════════════════════════════════════════════════════════════════
///   - selectedCharacterIndex: WritePermission изменен с Owner на Server
///   - SelectCharacter теперь корректно работает во всех 3 режимах:
///     * Competitive (Client → ServerRpc)
///     * SinglePlayer Host (прямая запись)
///     * Offline (прямая запись, NetworkObject не заспавнен)
///   - ИСПРАВЛЕНО: При смене персонажа сбрасываем _characterApplied
///     чтобы ApplySelectedCharacter вызвался заново при смене GameMode
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class CharacterSelectManager : NetworkBehaviour
{
    // ИСПРАВЛЕНО: WritePermission.Server вместо Owner
    // Теперь сервер имеет авторитет на запись (важно для Dedicated Server)
    private NetworkVariable<int> selectedCharacterIndex = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private CharacterDefinition _selectedCharacter;
    private bool _characterApplied = false;

    public System.Action<CharacterDefinition> OnCharacterSelected;
    public System.Action<CharacterDefinition> OnOpponentCharacterSelected;

    private CharacterDatabase    _characterDb;
    private PlayerStats          _playerStats;
    private CharacterMultipliers _characterMultipliers;
    private ItemInventory        _itemInventory;
    private WeaponManager        _weaponManager;

    [Inject]
    public void Construct(CharacterDatabase characterDatabase)
    {
        _characterDb = characterDatabase;
    }

    /// <summary>
    /// Гарантирует что _characterDb не null.
    /// Fallback на Resources если VContainer ещё не инжектировал.
    /// </summary>
    private void EnsureCharacterDatabase()
    {
        if (_characterDb != null) return;

        _characterDb = Resources.Load<CharacterDatabase>("CharacterDatabase");
        if (_characterDb != null)
        {
            Debug.LogWarning("[CSM] CharacterDatabase загружен из Resources (fallback). " +
                             "Убедись что CharacterDatabase назначен в BaseLifetimeScope Inspector.");
        }
    }

    public override void OnNetworkSpawn()
    {
        try { InjectionProvider.Container?.InjectGameObject(gameObject); }
        catch (System.Exception ex) { Debug.LogError($"[CharacterSelectManager] {ex.Message}"); }

        _playerStats          = GetComponent<PlayerStats>();
        _characterMultipliers = GetComponent<CharacterMultipliers>();
        _itemInventory        = GetComponent<ItemInventory>();
        _weaponManager        = GetComponent<WeaponManager>();

        selectedCharacterIndex.OnValueChanged += OnCharacterIndexChanged;

        // ИСПРАВЛЕНО: Всегда пытаемся обновить выбранного персонажа при спавне
        if (selectedCharacterIndex.Value >= 0)
            RefreshSelectedCharacter(selectedCharacterIndex.Value);

        SubscribeToGameModeManager();
    }

    public override void OnNetworkDespawn()
    {
        selectedCharacterIndex.OnValueChanged -= OnCharacterIndexChanged;

        if (GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged -= OnGameModeChanged;

        GameModeManager.OnInstanceInitialized -= OnGameModeManagerReady;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОДПИСКА НА GAMEMODEMANAGER
    // ─────────────────────────────────────────────────────────────────────────

    private void SubscribeToGameModeManager()
    {
        if (GameModeManager.Instance != null)
        {
            GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;
            Debug.Log("[CSM] Подписан на GameModeManager (Instance уже существует)");
        }
        else
        {
            GameModeManager.OnInstanceInitialized += OnGameModeManagerReady;
            Debug.Log("[CSM] GameModeManager.Instance == null, ожидаю OnInstanceInitialized...");
        }
    }

    private void OnGameModeManagerReady()
    {
        GameModeManager.OnInstanceInitialized -= OnGameModeManagerReady;

        if (GameModeManager.Instance == null)
        {
            Debug.LogError("[CSM] OnGameModeManagerReady вызван но Instance всё ещё null!");
            return;
        }

        GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;
        Debug.Log("[CSM] Подписан на GameModeManager (через OnInstanceInitialized fallback)");

        GameMode currentMode = GameModeManager.GetCurrentMode();
        if (currentMode != GameMode.Selecting)
            OnGameModeChanged(currentMode);
    }

    // ─── ВЫБОР ПЕРСОНАЖА ──────────────────────────────────────────────────────

    /// <summary>
    /// Выбирает персонажа по индексу.
    /// ИСПРАВЛЕНО: Корректно работает во всех режимах (Competitive, SinglePlayer, Offline)
    /// </summary>
    public void SelectCharacter(int index)
    {
        if (!IsOwner) return;

        EnsureCharacterDatabase();

        if (_characterDb == null) { Debug.LogError("[CSM] CharacterDatabase == null"); return; }

        var def = _characterDb.GetByIndex(index);
        if (def == null) { Debug.LogWarning($"[CSM] Индекс {index} не найден"); return; }

        // ИСПРАВЛЕНО: Проверяем IsServer вместо GameMode
        // IsServer = true: SinglePlayer Host или Offline (если NetworkManager есть)
        // IsServer = false: Competitive Client (Dedicated Server)
        if (IsServer)
        {
            // Мы являемся сервером (SinglePlayer Host) - пишем напрямую
            selectedCharacterIndex.Value = index;
            Debug.Log($"[CSM] Выбран (Host): {def.displayName} (idx {index})");
        }
        else
        {
            // Мы клиент (Competitive) - запрашиваем сервер
            RequestCharacterSelectionServerRpc(index);
            Debug.Log($"[CSM] Запрос на выбор: {def.displayName} (idx {index})");
        }
    }

    public void SelectCharacter(string characterId)
    {
        EnsureCharacterDatabase();
        if (_characterDb == null) return;
        SelectCharacter(_characterDb.GetIndex(characterId));
    }

    /// <summary>
    /// ServerRpc для запроса выбора персонажа от клиента к серверу.
    /// ИСПРАВЛЕНО: Сервер валидирует и применяет выбор (античит для Competitive)
    /// </summary>
    [Rpc(SendTo.Server)]
    private void RequestCharacterSelectionServerRpc(int index, RpcParams rpc = default)
    {
        EnsureCharacterDatabase();
        
        // Валидация индекса (античит)
        if (_characterDb == null || index < 0 || index >= _characterDb.GetCount())
        {
            Debug.LogWarning($"[CSM] Невалидный индекс {index} от клиента {rpc.Receive.SenderClientId}");
            return;
        }

        // Сервер применяет выбор (теперь у него есть права на запись!)
        selectedCharacterIndex.Value = index;
        
        var def = _characterDb.GetByIndex(index);
        Debug.Log($"[CSM] Сервер подтвердил выбор: {def?.displayName} (idx {index}) для клиента {rpc.Receive.SenderClientId}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void OnCharacterIndexChanged(int oldIndex, int newIndex)
    {
        Debug.Log($"[CSM] Индекс изменился: {oldIndex} → {newIndex}");
        
        // ИСПРАВЛЕНО: Сбрасываем флаг _characterApplied при смене персонажа
        // чтобы ApplySelectedCharacter вызвался заново при смене GameMode
        if (oldIndex != newIndex)
        {
            _characterApplied = false;
            Debug.Log("[CSM] Флаг _characterApplied сброшен - персонаж изменился");
        }
        
        RefreshSelectedCharacter(newIndex);
    }

    private void RefreshSelectedCharacter(int index)
    {
        EnsureCharacterDatabase();
        if (_characterDb == null) 
        {
            Debug.LogWarning($"[CSM] Не могу обновить персонажа (индекс {index}): CharacterDatabase недоступна");
            return;
        }

        var def = _characterDb.GetByIndex(index);
        if (def == null) 
        {
            Debug.LogWarning($"[CSM] Персонаж с индексом {index} не найден в БД");
            return;
        }

        _selectedCharacter = def;

        if (!string.IsNullOrEmpty(def.startingWeaponId) && IsOwner)
        {
            if (_weaponManager == null) _weaponManager = GetComponent<WeaponManager>();
            if (_weaponManager != null) _weaponManager.PendingStartWeaponId = def.startingWeaponId;
        }

        if (IsOwner) OnCharacterSelected?.Invoke(def);
        else         OnOpponentCharacterSelected?.Invoke(def);

        Debug.Log($"[CSM] {(IsOwner ? "Мой" : "Оппонент")} выбор: {def.displayName}");
    }

    // ─── ПРИМЕНЕНИЕ ───────────────────────────────────────────────────────────

    private void OnGameModeChanged(GameMode newMode)
    {
        if (newMode == GameMode.Selecting) { _characterApplied = false; return; }
        if (!IsOwner || _characterApplied) return;
        StopAllCoroutines();
        StartCoroutine(ApplyAfterSpawn());
    }

    private IEnumerator ApplyAfterSpawn()
    {
        yield return null;
        yield return null;
        ApplySelectedCharacter();
    }

    /// <summary>
    /// Применяет выбранного персонажа к игроку.
    /// ИСПРАВЛЕНО: Всегда использует selectedCharacterIndex.Value для получения 
    /// персонажа из БД, а не _selectedCharacter.
    /// </summary>
    public void ApplySelectedCharacter()
    {
        if (!IsOwner) return;

        EnsureCharacterDatabase();
        if (_characterDb == null)
        {
            Debug.LogError("[CSM] CharacterDatabase не доступна!");
            return;
        }

        CharacterDefinition def = null;
        
        // ИСПРАВЛЕНО: Всегда получаем персонажа по индексу из БД
        if (selectedCharacterIndex.Value >= 0)
        {
            def = _characterDb.GetByIndex(selectedCharacterIndex.Value);
            if (def != null)
            {
                _selectedCharacter = def;
                Debug.Log($"[CSM] Применяем персонажа по индексу {selectedCharacterIndex.Value}: {def.displayName}");
            }
            else
            {
                Debug.LogWarning($"[CSM] Персонаж с индексом {selectedCharacterIndex.Value} не найден в БД");
            }
        }
        else
        {
            Debug.Log($"[CSM] selectedCharacterIndex = {selectedCharacterIndex.Value}, получаем дефолтного");
        }
        
        // Если не получилось получить по индексу — берём дефолтного
        if (def == null)
        {
            def = _characterDb.GetDefault();
            _selectedCharacter = def;
            if (def != null)
                Debug.Log($"[CSM] Применяем дефолтного: {def.displayName}");
        }

        if (def == null)
        {
            Debug.LogError("[CSM] Нет персонажа для применения!");
            return;
        }

        // 1. Копируем данные CharacterDefinition в CharacterMultipliers
        if (_characterMultipliers != null) _characterMultipliers.ApplyDefinition(def);
        else Debug.LogWarning("[CSM] CharacterMultipliers не найден!");

        // 2. Пассивные способности
        ApplyPassiveAbility(def);

        // 3. Записываем базовые значения в StatSheet
        if (_playerStats?.StatSheet != null)
            _characterMultipliers?.InitializeStatSheet(_playerStats.StatSheet);

        // 4. Обновляем PlayerStats.maxHealth и CurrentHealth из StatSheet
        _playerStats?.ApplyCharacterDefinition();

        // 5. Стартовое оружие для WeaponManager
        ApplyStartingWeapon(def);

        // 6. Визуал тела
        ApplyBodyVisual(def);

        // 7. Синхронизируем индекс персонажа по сети
        int idx = _characterDb.GetIndex(def);
        _playerStats?.SyncCharacterIndex(idx);

        _characterApplied = true;
        Debug.Log($"[CSM] ✅ Применён: {def.displayName} | Passive: {def.passiveType} | HP: {_playerStats?.MaxHealth}");
    }

    // ─── ПАССИВНЫЕ СПОСОБНОСТИ ────────────────────────────────────────────────

    private void ApplyPassiveAbility(CharacterDefinition def)
    {
        if (def.passiveType == CharacterPassiveType.None) return;
        switch (def.passiveType)
        {
            case CharacterPassiveType.LuckyBlock:       ApplyLuckyBlock(def.passiveValue);    break;
            case CharacterPassiveType.LifeStealOnKill:  ApplyLifeStealOnKill(def.passiveValue); break;
            case CharacterPassiveType.DoubleXpChance:   ApplyDoubleXpChance(def.passiveValue); break;
            case CharacterPassiveType.AttackSpeedFloor: ApplyAttackSpeedFloor(def.passiveValue); break;
            case CharacterPassiveType.TotemDoubleFirst:
                Debug.Log("[CSM] 🗿 TotemDoubleFirst активна"); break;
        }
    }

    private void ApplyLuckyBlock(float pct)
    {
        if (_playerStats == null) return;
        var i = gameObject.GetComponent<LuckyBlockInterceptor>()
             ?? gameObject.AddComponent<LuckyBlockInterceptor>();
        i.SetChance(pct / 100f);
        _playerStats.RegisterShield(i);
        Debug.Log($"[CSM] 🍀 LuckyBlock: {pct:F0}%");
    }

    private void ApplyLifeStealOnKill(float pct)
    {
        if (_itemInventory == null || _playerStats == null) return;
        float healFraction = pct / 100f;
        _itemInventory.OnEnemyKilled += () =>
            _playerStats.Heal(_playerStats.MaxHealth * healFraction);
        Debug.Log($"[CSM] 🩸 LifeSteal: {pct:F1}%");
    }

    private void ApplyDoubleXpChance(float pct)
    {
        if (_playerStats == null) return;
        float bonus = (pct / 100f) * 1.0f;
        _playerStats.AddStatModifier(new StatModifier(
            StatType.XpMultiplier, bonus, ModifierType.Additive, "passive_double_xp"));
        Debug.Log($"[CSM] ⭐ DoubleXP: +{bonus * 100f:F0}%");
    }

    private void ApplyAttackSpeedFloor(float floor)
    {
        if (_playerStats?.StatSheet == null) return;
        float cur = _playerStats.StatSheet.GetStat(StatType.AttackSpeed);
        if (cur < floor)
        {
            _playerStats.AddStatModifier(new StatModifier(
                StatType.AttackSpeed, floor - cur, ModifierType.Additive, "passive_atk_floor"));
            Debug.Log($"[CSM] ⚡ AttackSpeedFloor: до {floor:F2}");
        }
    }

    private void ApplyStartingWeapon(CharacterDefinition def)
    {
        if (_weaponManager == null || string.IsNullOrEmpty(def.startingWeaponId)) return;

        // Если оружие уже инициализировано с другим ID — сбрасываем и переинициализируем
        if (_weaponManager.HasInitializedWeapons)
        {
            string currentWeaponId = _weaponManager.GetCurrentWeaponId();
            if (currentWeaponId != def.startingWeaponId)
            {
                Debug.Log($"[CSM] Смена оружия: '{currentWeaponId}' → '{def.startingWeaponId}'");
                _weaponManager.ResetAndReinitializeWeapons(def.startingWeaponId);
                return;
            }
        }

        _weaponManager.PendingStartWeaponId = def.startingWeaponId;
    }

    private void ApplyBodyVisual(CharacterDefinition def)
    {
        if (def == null) return;
        if (Mathf.Abs(def.bodyScale - 1f) > 0.001f)
            transform.localScale = Vector3.one * def.bodyScale;

        foreach (var r in GetComponentsInChildren<Renderer>(true))
        {
            foreach (var mat in r.materials)
            {
                if (mat == null) continue;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", def.bodyColor);
                if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     def.bodyColor);
            }
        }
        Debug.Log($"[CSM] 🎨 Visual: color={def.bodyColor} scale={def.bodyScale}");
    }

    // ─── PUBLIC API ───────────────────────────────────────────────────────────

    public CharacterDefinition SelectedCharacter => _selectedCharacter;

    public CharacterDefinition GetSelectedCharacter()      => _selectedCharacter;
    public int                 GetSelectedIndex()          => selectedCharacterIndex.Value;
    public bool                HasSelectedCharacter()      => selectedCharacterIndex.Value >= 0;
    public bool                IsTotemDoubleFirstActive()
        => _selectedCharacter?.passiveType == CharacterPassiveType.TotemDoubleFirst;
    public float               GetPassiveValue()           => _selectedCharacter?.passiveValue ?? 0f;
    public Color               GetBodyColor()              => _selectedCharacter?.bodyColor ?? Color.white;
}

// ─────────────────────────────────────────────────────────────────────────────

public class LuckyBlockInterceptor : UnityEngine.MonoBehaviour, IShieldInterceptor
{
    private float _chance;

    public void SetChance(float chance)
    {
        _chance = UnityEngine.Mathf.Clamp01(chance);
        UnityEngine.Debug.Log($"[LuckyBlock] Шанс поглощения: {_chance * 100f:F0}%");
    }

    public float InterceptDamage(float rawDamage, EnemyHealth attacker)
    {
        if (_chance > 0f && UnityEngine.Random.value < _chance)
        {
            UnityEngine.Debug.Log($"[LuckyBlock] ✨ Удар поглощён! ({_chance * 100f:F0}%)");
            return 0f;
        }
        return rawDamage;
    }
}
