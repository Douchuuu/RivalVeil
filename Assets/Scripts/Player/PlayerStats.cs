using UnityEngine;
using Unity.Netcode;
using VContainer;
using VContainer.Unity;
using System;
using System.Collections.Generic;

/// <summary>
/// PlayerStats v4 — все характеристики игрока + сетевые переменные.
///
/// ИСПРАВЛЕНО:
///   - Shield interceptors: List вместо одного поля.
///     AspisWeapon + LuckyBlock passive больше не конфликтуют —
///     урон проходит через обоих перехватчиков последовательно.
///
///   - netCharacterIndex: NetworkVariable для синхронизации индекса персонажа
///     оппонента (нужен CompetitiveUIManager для отображения портрета/имени).
///
/// ВСЕ NetworkVariable оппонента читаются через GetNet*() методы —
/// используй их в CompetitiveUIManager вместо прямого доступа к полям.
/// </summary>
public partial class PlayerStats : NetworkBehaviour
{
    [Header("Базовые характеристики")]
    [Tooltip("НЕ РЕДАКТИРУЙ — берётся из CharacterMultipliers/CharacterDefinition.")]
    [SerializeField] private float maxHealth = 100f;
    public float MaxHealth     => maxHealth;
    public float CurrentHealth { get; private set; }

    [Header("Щит")]
    [SerializeField] private float shieldRegenDelay = 4f;
    [SerializeField] private float shieldRegenRate  = 5f;

    public float MaxShieldHP     => StatSheet?.GetStat(StatType.Armor) ?? 0f;
    public float CurrentShieldHP { get; private set; }
    private float _shieldRegenTimer = 0f;

    [Header("Опыт")]
    [SerializeField] private int   startLevel    = 1;
    [SerializeField] private float startXpToNext = 100f;
    public int   CurrentLevel  { get; private set; }
    public float CurrentXP     { get; private set; }
    public float XpToNextLevel { get; private set; }

    // ─── СЕТЕВЫЕ ПЕРЕМЕННЫЕ ───────────────────────────────────────────────────
    // Все с WritePermission.Owner — только владелец пишет, все читают.

    private NetworkVariable<float> netHealth    = new NetworkVariable<float>(100f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<float> netMaxHealth = new NetworkVariable<float>(100f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<int>   netLevel     = new NetworkVariable<int>(1,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<float> netXP        = new NetworkVariable<float>(0f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<float> netXPToNext  = new NetworkVariable<float>(100f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<int>   netKills     = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<int>   netWeaponFlags = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<int>   netWeaponLevels = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<long>  netTotemData = new NetworkVariable<long>(0L,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<float> netShieldHP = new NetworkVariable<float>(0f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private NetworkVariable<float> netMaxShieldHP = new NetworkVariable<float>(0f,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    /// <summary>
    /// Индекс выбранного персонажа в CharacterDatabase.
    /// Нужен CompetitiveUIManager для отображения портрета/имени персонажа оппонента.
    /// -1 = не выбран.
    /// </summary>
    private NetworkVariable<int>   netCharacterIndex = new NetworkVariable<int>(-1,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>
    /// Битмаска предметов в инвентаре — OR всех ItemDefinition.FlagMask.
    /// Обновляется владельцем через SyncItemFlags() при каждом изменении ItemInventory.
    /// Сервер (LiveMatchTracker) читает через GetNetItemFlags() для записи в БД.
    /// </summary>
    private NetworkVariable<int>   netItemFlags = new NetworkVariable<int>(0,
        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────

    private KillValidator    _killValidator;
    private PlayerRegistry   _playerRegistry;
    private LevelUpManager   _levelUpManager;
    private CombatCalculator _combatCalc;
    private GameStateService _gameState;

    private WeaponManager _weaponManager;
    private ItemInventory _itemInventory;

    private bool  _hasPendingKills = false;
    private float _batchTimer      = 0f;
    private float _lastSyncedXP    = -1f;
    private const float XP_SYNC_THRESHOLD = 1f;
    private bool  _isDead          = false;

    // ─── ЩИТ — СПИСОК ПЕРЕХВАТЧИКОВ ──────────────────────────────────────────
    // ИСПРАВЛЕНО: было одно поле IShieldInterceptor — AspisWeapon и LuckyBlock
    // перезаписывали друг друга. Теперь список — урон проходит через всех.
    private readonly List<IShieldInterceptor> _shieldInterceptors = new List<IShieldInterceptor>(2);

    public Action OnLocalPlayerDied;

    [Inject]
    public void Construct(
        KillValidator    killValidator,
        PlayerRegistry   playerRegistry,
        LevelUpManager   levelUpManager,
        CombatCalculator combatCalc,
        GameStateService gameState)
    {
        _killValidator  = killValidator;
        _playerRegistry = playerRegistry;
        _levelUpManager = levelUpManager;
        _combatCalc     = combatCalc;
        _gameState      = gameState;
    }

    // ─── ИНИЦИАЛИЗАЦИЯ ────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        try { InjectionProvider.Container?.InjectGameObject(gameObject); }
        catch (Exception ex) { Debug.LogError($"[PlayerStats] Inject: {ex.Message}"); }

        if (_playerRegistry == null)
            _playerRegistry = InjectionProvider.Container?.Resolve<PlayerRegistry>();

        _weaponManager = GetComponent<WeaponManager>();
        _itemInventory = GetComponent<ItemInventory>();

        CurrentLevel  = startLevel;
        CurrentXP     = 0f;
        XpToNextLevel = startXpToNext;

        StatSheet = new PlayerStatSheet();
        GetComponent<CharacterMultipliers>()?.InitializeStatSheet(StatSheet);

        float statHP = StatSheet.GetStat(StatType.MaxHealth);
        if (statHP > 0f) maxHealth = statHP;

        if (IsOwner)
        {
            CurrentHealth    = maxHealth;
            CurrentShieldHP  = 0f;
            _shieldRegenTimer = 0f;

            netHealth.Value          = CurrentHealth;
            netMaxHealth.Value       = maxHealth;
            netLevel.Value           = CurrentLevel;
            netXP.Value              = CurrentXP;
            netXPToNext.Value        = XpToNextLevel;
            netKills.Value           = 0;
            netWeaponLevels.Value    = 0;
            netTotemData.Value       = 0L;
            netShieldHP.Value        = 0f;
            netMaxShieldHP.Value     = 0f;
            netCharacterIndex.Value  = -1;
            _lastSyncedXP            = 0f;

            _playerRegistry?.RegisterLocalPlayer(this);

            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.constraints = RigidbodyConstraints.FreezeAll;
                Debug.Log("[PlayerStats] Rigidbody заморожен — ждём PlayerWorldScene.");
            }
        }
        else
        {
            if (GameModeManager.IsMode(GameMode.SinglePlayer))
                _playerRegistry?.RegisterLocalPlayer(this);
            else
                _playerRegistry?.RegisterOpponent(this);
        }
    }

    // ─── ПРИМЕНЕНИЕ ПЕРСОНАЖА ─────────────────────────────────────────────────

    public void ApplyCharacterDefinition()
    {
        if (!IsOwner) return;
        float newHP = StatSheet?.GetStat(StatType.MaxHealth) ?? maxHealth;
        if (newHP > 0f && Mathf.Abs(newHP - maxHealth) > 0.5f)
        {
            maxHealth     = newHP;
            CurrentHealth = maxHealth;
            netHealth.Value    = CurrentHealth;
            netMaxHealth.Value = maxHealth;
            Debug.Log($"[PlayerStats] HP персонажа: {maxHealth}");
        }
    }

    /// <summary>
    /// Переинициализирует StatSheet — используется при смене персонажа или отладке.
    /// </summary>
    public void ReinitializeStatSheet()
    {
        StatSheet = new PlayerStatSheet();
        GetComponent<CharacterMultipliers>()?.InitializeStatSheet(StatSheet);
        ApplyCharacterDefinition();
        Debug.Log("[PlayerStats] StatSheet переинициализирован");
    }

    /// <summary>
    /// Синхронизирует индекс персонажа по сети.
    /// Вызывается из CharacterSelectManager после ApplySelectedCharacter().
    /// </summary>
    public void SyncCharacterIndex(int index)
    {
        if (!IsOwner) return;
        netCharacterIndex.Value = index;
    }

    // ─── СПАВН / РЕСПАВН ─────────────────────────────────────────────────────

    public void RespawnAtSpawnPoint()
    {
        var rb = GetComponent<Rigidbody>();
        if (rb != null) rb.constraints = RigidbodyConstraints.FreezeRotation;
        MoveToSpawnPoint();
    }

    private void MoveToSpawnPoint()
    {
        var respawn = FindRespawnPointInAllScenes();
        if (respawn != null)
        {
            transform.SetPositionAndRotation(respawn.transform.position, respawn.transform.rotation);
            var rb = GetComponent<Rigidbody>();
            if (rb != null) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            Debug.Log($"[PlayerStats] Спавн: {transform.position}");
        }
        else Debug.LogError("[PlayerStats] RespawnPoint не найден!");
    }

    private GameObject FindRespawnPointInAllScenes()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == "RespawnPoint") return root;
                try { if (root.CompareTag("RespawnPoint")) return root; } catch { }
                var child = FindRespawnInChildren(root.transform);
                if (child != null) return child.gameObject;
            }
        }
        return null;
    }

    private Transform FindRespawnInChildren(Transform parent)
    {
        if (parent.name == "RespawnPoint") return parent;
        try { if (parent.CompareTag("RespawnPoint")) return parent; } catch { }
        foreach (Transform c in parent)
        {
            var r = FindRespawnInChildren(c);
            if (r != null) return r;
        }
        return null;
    }

    // ─── UPDATE ───────────────────────────────────────────────────────────────

    private void Update()
    {
        if (!IsOwner) return;

        if (_hasPendingKills)
        {
            _batchTimer += Time.deltaTime;
            if (_batchTimer >= 0.1f)
            {
                _batchTimer      = 0f;
                _hasPendingKills = false;
                _killValidator?.ValidateKillBatchServerRpc(netKills.Value, 0.1f);
            }
        }

        float maxShield = MaxShieldHP;
        if (maxShield > 0f && CurrentShieldHP < maxShield && !_isDead)
        {
            if (_shieldRegenTimer > 0f)
                _shieldRegenTimer -= Time.deltaTime;
            else
            {
                float prev = CurrentShieldHP;
                CurrentShieldHP = Mathf.Min(CurrentShieldHP + shieldRegenRate * Time.deltaTime, maxShield);
                if (CurrentShieldHP - prev >= 1f || Mathf.Approximately(CurrentShieldHP, maxShield))
                    SyncShieldStats();
            }
        }
    }

    public override void OnNetworkDespawn() => _playerRegistry?.Unregister(this);

    // ─── ЛОГИКА БОЯ ───────────────────────────────────────────────────────────

    public bool IsAttackBlocked =>
        GameModeManager.IsCompetitiveMode() && (_gameState?.IsPaused ?? false);

    public void TakeDamage(float rawAmount, EnemyHealth attacker = null)
    {
        if (!IsOwner || IsAttackBlocked || _isDead) return;

        // ИСПРАВЛЕНО: все перехватчики применяются последовательно
        // Сначала LuckyBlock (может обнулить), потом AspisWeapon (щит)
        foreach (var interceptor in _shieldInterceptors)
        {
            rawAmount = interceptor.InterceptDamage(rawAmount, attacker);
            if (rawAmount <= 0f) return;
        }

        float damage = _combatCalc != null
            ? _combatCalc.CalculateIncomingDamage(rawAmount, this)
            : rawAmount;

        if (MaxShieldHP > 0f) _shieldRegenTimer = shieldRegenDelay;

        if (CurrentShieldHP > 0f)
        {
            CurrentShieldHP = Mathf.Max(0f, CurrentShieldHP - damage);
            SyncShieldStats();
            return;
        }

        CurrentHealth  -= damage;
        netHealth.Value = CurrentHealth;

        if (CurrentHealth <= 0f) Die();
    }

    public void Heal(float amount)
    {
        if (!IsOwner || amount <= 0f || _isDead) return;
        CurrentHealth   = Mathf.Min(CurrentHealth + amount, maxHealth);
        netHealth.Value = CurrentHealth;
    }

    public ItemInventory GetItemInventory() => _itemInventory;

    // ─── ЩИТ — РЕГИСТРАЦИЯ ПЕРЕХВАТЧИКОВ ────────────────────────────────────

    /// <summary>ИСПРАВЛЕНО: добавляет в список, не перезаписывает.</summary>
    public void RegisterShield(IShieldInterceptor shield)
    {
        if (!_shieldInterceptors.Contains(shield))
            _shieldInterceptors.Add(shield);
    }

    public void UnregisterShield(IShieldInterceptor shield)
        => _shieldInterceptors.Remove(shield);

    // ─── ЩИТ ARMOR-ТОТЕМ ─────────────────────────────────────────────────────

    public void RefreshShield(float delta)
    {
        if (!IsOwner || delta <= 0f) return;
        CurrentShieldHP = Mathf.Min(CurrentShieldHP + delta, MaxShieldHP);
        SyncShieldStats();
    }

    public void RestoreShieldToFull()
    {
        if (!IsOwner) return;
        CurrentShieldHP   = MaxShieldHP;
        _shieldRegenTimer = 0f;
        SyncShieldStats();
    }

    private void SyncShieldStats()
    {
        if (!IsOwner) return;
        netShieldHP.Value    = CurrentShieldHP;
        netMaxShieldHP.Value = MaxShieldHP;
    }

    // ─── БОНУС HP ─────────────────────────────────────────────────────────────

    public void AddBonusMaxHealth(float amount)
    {
        if (amount <= 0f) return;
        maxHealth      += amount;
        CurrentHealth   = Mathf.Min(CurrentHealth + amount, maxHealth);
        if (IsOwner) netMaxHealth.Value = maxHealth;
    }

    public void RemoveBonusMaxHealth(float amount)
    {
        if (amount <= 0f) return;
        maxHealth     = Mathf.Max(1f, maxHealth - amount);
        CurrentHealth = Mathf.Min(CurrentHealth, maxHealth);
        if (IsOwner) netMaxHealth.Value = maxHealth;
    }

    // ─── СМЕРТЬ ───────────────────────────────────────────────────────────────

    private void Die()
    {
        if (_isDead) return;
        _isDead = true;

        if (GameModeManager.IsMode(GameMode.SinglePlayer))
        {
            _gameState?.Pause(PauseReason.GameOver);
            OnLocalPlayerDied?.Invoke();
        }
        else
        {
            CurrentHealth     = maxHealth;
            CurrentShieldHP   = MaxShieldHP;
            _shieldRegenTimer = 0f;
            netHealth.Value   = CurrentHealth;
            SyncShieldStats();
            MoveToSpawnPoint();
            _isDead = false;
        }
    }

    // ─── ОПЫТ ─────────────────────────────────────────────────────────────────

    public void AddExperience(float amount)
    {
        float mult = StatSheet?.GetStat(StatType.XpMultiplier) ?? 1f;
        CurrentXP += amount * mult;

        if (IsOwner && Mathf.Abs(CurrentXP - _lastSyncedXP) >= XP_SYNC_THRESHOLD)
        {
            netXP.Value   = CurrentXP;
            _lastSyncedXP = CurrentXP;
        }

        if (CurrentXP >= XpToNextLevel) LevelUp();
    }

    private void LevelUp()
    {
        CurrentLevel++;
        CurrentXP    -= XpToNextLevel;
        XpToNextLevel = Mathf.Round(XpToNextLevel * 1.3f);

        if (IsOwner)
        {
            netLevel.Value    = CurrentLevel;
            netXP.Value       = CurrentXP;
            netXPToNext.Value = XpToNextLevel;
            _lastSyncedXP     = CurrentXP;
        }

        if (_weaponManager != null)
        {
            // ИСПРАВЛЕНИЕ: PlayerPrefab спавнится до загрузки BaseScene, когда
            // InjectionProvider.Container == null. Construct() никогда не получает
            // LevelUpManager если инъекция произошла до постройки DI-контейнера.
            // Решение: ленивое разрешение зависимости при первом использовании.
            if (_levelUpManager == null)
            {
                _levelUpManager = InjectionProvider.Container?.Resolve<LevelUpManager>();
                if (_levelUpManager == null)
                    Debug.LogWarning("[PlayerStats] LevelUpManager не найден — LevelUp меню не откроется!");
            }

            _levelUpManager?.ShowLevelUpMenu(_weaponManager);
        }
    }

    // ─── УБИЙСТВА ─────────────────────────────────────────────────────────────

    public void AddKill()
    {
        if (!IsOwner) return;
        netKills.Value++;
        _hasPendingKills = true;
    }

    public void ForceSetKills(int confirmed)
    {
        if (!IsOwner) return;
        netKills.Value   = confirmed;
        _hasPendingKills = false;
    }

    public void ValidateKillWithServer(int enemyId) => _hasPendingKills = true;

    [ClientRpc]
    public void NotifyKillClientRpc(int enemyId) { if (IsOwner) AddKill(); }

    // ─── АНТИ-ЧИТ СУНДУКОВ ───────────────────────────────────────────────────
    // Сервер ведёт свой счётчик открытых сундуков per-player.
    // Клиент вызывает ReportChestOpenServerRpc каждый раз когда открывает сундук.
    // Сервер проверяет что счётчик растёт строго +1 (нельзя открыть несколько сразу).

    private int _serverValidatedChestCount = 0;

    /// <summary>
    /// Вызывается клиентом из Chest.Open() с новым значением счётчика.
    /// Сервер валидирует что claimedCount == предыдущий + 1.
    /// Если счётчик прыгнул (например с 0 до 3) — логируем как подозрительное.
    /// </summary>
    [ServerRpc(RequireOwnership = true)]
    public void ReportChestOpenServerRpc(int claimedCount)
    {
        int expected = _serverValidatedChestCount + 1;
        if (claimedCount != expected)
        {
            Debug.LogWarning(
                $"[AntiCheat] ⚠️ ClientId={OwnerClientId} chest count suspicious: " +
                $"expected={expected}, got={claimedCount}. Ignoring.");
            return;
        }
        _serverValidatedChestCount = claimedCount;
        Debug.Log($"[AntiCheat] ✅ ClientId={OwnerClientId} chest #{claimedCount} validated");
    }

    // ─── STAT SHEET ───────────────────────────────────────────────────────────

    public PlayerStatSheet StatSheet { get; private set; } = new PlayerStatSheet();

    public void AddStatModifier(StatModifier m)       => StatSheet?.AddModifier(m);
    public void RemoveStatModifiersFromSource(string s) => StatSheet?.RemoveModifiersFromSource(s);

    // ─── СИНХРОНИЗАЦИЯ ────────────────────────────────────────────────────────

    public void SyncWeaponFlags(int flags)         { if (IsOwner) netWeaponFlags.Value  = flags; }
    public void SyncWeaponLevels(int packed)       { if (IsOwner) netWeaponLevels.Value = packed; }
    public void SyncTotemData(long packed)         { if (IsOwner) netTotemData.Value    = packed; }
    /// <summary>Вызывается из ItemInventory.OnInventoryChanged — синхронизирует битмаску предметов.</summary>
    public void SyncItemFlags(int flags)           { if (IsOwner) netItemFlags.Value    = flags; }

    // ─── ГЕТТЕРЫ СЕТЕВЫХ ДАННЫХ (для UI оппонента) ───────────────────────────

    public float GetNetHealth()          => netHealth.Value;
    public float GetNetMaxHealth()       => netMaxHealth.Value;
    public int   GetNetLevel()           => netLevel.Value;
    public float GetNetXP()              => netXP.Value;
    public float GetNetXPToNext()        => netXPToNext.Value;
    public int   GetNetWeaponFlags()     => netWeaponFlags.Value;
    public int   GetKillCount()          => netKills.Value;
    public int   GetNetWeaponLevels()    => netWeaponLevels.Value;
    public long  GetNetTotemData()       => netTotemData.Value;
    public float GetNetShieldHP()        => netShieldHP.Value;
    public float GetNetMaxShieldHP()     => netMaxShieldHP.Value;
    public int   GetNetCharacterIndex()  => netCharacterIndex.Value;
    /// <summary>Битмаска предметов — OR всех ItemDefinition.FlagMask активных предметов.</summary>
    public int   GetNetItemFlags()       => netItemFlags.Value;

    // ─── СТАТИЧЕСКИЕ РАСПАКОВЩИКИ ─────────────────────────────────────────────

    public static int UnpackWeaponLevel(int packed, int slot)
    {
        if (slot < 0 || slot > 3) return 0;
        return (packed >> (slot * 6)) & 0x3F;
    }

    public static (int dbIndex, int level) UnpackTotemSlot(long packed, int slot)
    {
        if (slot < 0 || slot > 3) return (31, 0);
        long data  = (packed >> (slot * 12)) & 0xFFF;
        int dbIdx  = (int)(data & 0x1F);
        int level  = (int)((data >> 5) & 0x7F);
        return (dbIdx, level);
    }
}
