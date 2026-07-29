using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using VContainer;
using VContainer.Unity;

/// <summary>
/// WeaponManager — управление оружиями игрока.
///
/// ══════════════════════════════════════════════════════════════
/// НОВОЕ: SyncWeaponLevelsToNetwork()
/// ══════════════════════════════════════════════════════════════
///
///   После каждого изменения уровня (UnlockWeapon, NotifyWeaponUpgraded)
///   вызывается SyncWeaponLevelsToNetwork() которая упаковывает
///   уровни 4 оружий (6 бит на оружие) в int и передаёт в
///   PlayerStats.SyncWeaponLevels() → netWeaponLevels NetworkVariable.
///
///   CompetitiveUIManager читает GetNetWeaponLevels() у оппонента
///   и распаковывает через PlayerStats.UnpackWeaponLevel(packed, slot).
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class WeaponManager : NetworkBehaviour
{
    public const int MAX_WEAPON_LEVEL = 50;

    [Header("Инвентарь")]
    public List<GameObject> activeWeapons = new List<GameObject>();
    public int maxSlots = 4;

    private Image[] _slotImages;
    private bool _hasUISlots = false;
    private bool _hasInitializedWeapons = false;
    private Coroutine _initializationCoroutine;

    // weaponId → уровень (1 при получении, +1 за апгрейд)
    private readonly Dictionary<string, int> _weaponLevels = new Dictionary<string, int>();

    private WeaponDatabase _weaponDb;
    public static System.Action<Image[]> OnSlotsRegistered;

    /// <summary>Публичное свойство для IntegrationTest, DebugHelper, PlayerInitializationValidator.</summary>
    public bool HasInitializedWeapons => _hasInitializedWeapons;

    // ─────────────────────────────────────────────────────────────────────────
    // INJECTION
    // ─────────────────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(WeaponDatabase weaponDb)
    {
        _weaponDb = weaponDb;
        Debug.Log("✅ [WeaponManager] WeaponDatabase инжектирован");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЕТЕВОЙ СПАВН
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        Debug.Log($"[WeaponManager] OnNetworkSpawn. ClientId={OwnerClientId}");
        try { InjectionProvider.Container?.InjectGameObject(gameObject); }
        catch (System.Exception ex) { Debug.LogError($"[WeaponManager] {ex.Message}"); }

        // Fallback: если VContainer не смог инжектировать WeaponDatabase — ищем в Resources
        if (_weaponDb == null)
        {
            _weaponDb = Resources.Load<WeaponDatabase>("WeaponDatabase");
            if (_weaponDb != null)
                Debug.LogWarning("[WeaponManager] WeaponDatabase загружена из Resources как fallback. " +
                                 "Убедись что она назначена в BaseLifetimeScope Inspector.");
        }

        ValidateWeaponDatabase();

        if (_initializationCoroutine != null)
            StopCoroutine(_initializationCoroutine);

        _initializationCoroutine = StartCoroutine(WaitForUIAndInitialize());
    }

    public override void OnNetworkDespawn()
    {
        if (_initializationCoroutine != null)
        {
            StopCoroutine(_initializationCoroutine);
            _initializationCoroutine = null;
        }
        OnSlotsRegistered = null;
    }

    private void ValidateWeaponDatabase()
    {
        if (_weaponDb == null) { Debug.LogError("[WeaponManager] ❌ WeaponDatabase == null!"); return; }
        if (_weaponDb.GetCount() == 0) { Debug.LogError("[WeaponManager] ❌ WeaponDatabase пуста!"); return; }

        var ids = new System.Text.StringBuilder();
        foreach (var w in _weaponDb.GetAll()) ids.Append($"'{w?.weaponId ?? "NULL"}' ");
        Debug.Log($"[WeaponManager] ℹ️ {_weaponDb.GetCount()} оружий: {ids}");

        var startDef = _weaponDb.GetById(_weaponDb.DefaultStartWeaponId);
        if (startDef == null)
            Debug.LogError($"[WeaponManager] ❌ DefaultStartWeaponId '{_weaponDb.DefaultStartWeaponId}' не найден! Доступные: {ids}");
    }

    private IEnumerator WaitForUIAndInitialize()
    {
        const float TIMEOUT = 10f;
        float elapsed = 0f;

        // 1. Ждём UI слоты
        while (!_hasUISlots && elapsed < TIMEOUT)
        {
            if (WeaponSlotsProvider.IsReady) { HandleSlotsReceived(WeaponSlotsProvider.CurrentSlots); break; }
            elapsed += 0.1f;
            yield return new WaitForSeconds(0.1f);
        }

        // 2. Ждём пока игрок нажмёт Confirm и режим сменится с Selecting.
        //    Это гарантирует что CharacterSelectManager уже записал PendingStartWeaponId.
        //    Таймаут 60 секунд — игрок может долго выбирать персонажа.
        float modeWait = 0f;
        while (GameModeManager.IsSelecting() && modeWait < 60f)
        {
            modeWait += 0.05f;
            yield return new WaitForSeconds(0.05f);
        }

        // 3. Несколько кадров паузы — CharacterSelectManager.ApplyAfterSpawn() успеет
        //    отработать и записать PendingStartWeaponId (он ждёт 2 кадра внутри).
        for (int i = 0; i < 8; i++)
            yield return null;

        // 4. Ждём пока PlayerWorldScope апгрейднет Container и зарегистрирует EnemyPool.
        //    Без этого InjectGameObject(SpeedStaffWeapon) выбросит VContainerException.
        float scopeWait = 0f;
        bool hasEnemyPool = false;
        while (!hasEnemyPool && scopeWait < 10f)
        {
            try
            {
                InjectionProvider.Container?.Resolve<EnemyPool>();
                hasEnemyPool = true;
            }
            catch { }

            if (!hasEnemyPool)
            {
                scopeWait += 0.05f;
                yield return new WaitForSeconds(0.05f);
            }
        }

        if (hasEnemyPool)
            Debug.Log($"[WeaponManager] PlayerWorldScope готов. PendingStartWeaponId='{PendingStartWeaponId}'");
        else
            Debug.LogWarning("[WeaponManager] PlayerWorldScope не поднялся за 10с — оружие может не инжектироваться корректно.");

        TryInitializeWeapons();
        if (!_hasInitializedWeapons) ForcedInitialization();
        _initializationCoroutine = null;
    }

    private void HandleSlotsReceived(Image[] slots)
    {
        if (slots == null || slots.Length == 0) return;
        _slotImages = slots;
        _hasUISlots = true;
        OnSlotsRegistered?.Invoke(slots);
    }

    /// <summary>
    /// Переопределяет стартовое оружие из WeaponDatabase.defaultStartWeaponId.
    /// Устанавливается из CharacterSelectManager до того как
    /// WaitForUIAndInitialize() вызовет TryInitializeWeapons().
    /// </summary>
    public string PendingStartWeaponId { get; set; } = "";

    private void TryInitializeWeapons()
    {
        if (_hasInitializedWeapons || _weaponDb == null || _weaponDb.GetCount() == 0) 
        {
            Debug.LogWarning($"[WeaponManager] TryInitializeWeapons aborted: initialized={_hasInitializedWeapons}, dbNull={_weaponDb == null}");
            return;
        }

        // Персонаж задал своё стартовое оружие — используем его вместо дефолтного
        string startId = !string.IsNullOrEmpty(PendingStartWeaponId)
            ? PendingStartWeaponId
            : _weaponDb.DefaultStartWeaponId;

        Debug.Log($"[WeaponManager] Попытка получить оружие: '{startId}' (Pending='{PendingStartWeaponId}', Default='{_weaponDb.DefaultStartWeaponId}')");

        WeaponDefinition startDef = _weaponDb.GetById(startId);
        
        // ДОБАВЛЕНО: Диагностика почему оружие не найдено
        if (startDef == null)
        {
            Debug.LogError($"[WeaponManager] Оружие '{startId}' НЕ НАЙДЕНО в БД!");
            Debug.Log($"[WeaponManager] Доступные оружия в БД:");
            foreach (var w in _weaponDb.GetAll())
            {
                if (w != null)
                    Debug.Log($"[WeaponManager]   - '{w.weaponId}': {w.displayName}");
            }
        }

        // ДИАГНОСТИКА: Логируем результат поиска
        if (startDef == null)
        {
            Debug.LogError($"[WeaponManager] ❌ Оружие '{startId}' не найдено в WeaponDatabase!");
        }
        else
        {
            Debug.Log($"[WeaponManager] ✅ Оружие найдено: '{startDef.weaponId}' ({startDef.displayName})");
        }

        // Фоллбэк если оружие персонажа не найдено в базе
        if (startDef == null && !string.IsNullOrEmpty(PendingStartWeaponId))
        {
            Debug.LogWarning($"[WeaponManager] Оружие персонажа '{PendingStartWeaponId}' не найдено. " +
                             $"Фоллбэк: '{_weaponDb.DefaultStartWeaponId}'.");
            startDef = _weaponDb.GetById(_weaponDb.DefaultStartWeaponId);
            
            if (startDef != null)
                Debug.Log($"[WeaponManager] ✅ Фолбэк оружие найдено: '{startDef.weaponId}'");
            else
                Debug.LogError($"[WeaponManager] ❌ Фолбэк оружие '{_weaponDb.DefaultStartWeaponId}' тоже не найдено!");
        }

        if (startDef != null) 
        { 
            UnlockWeapon(startDef); 
            _hasInitializedWeapons = true; 
            return; 
        }

        // Крайний фоллбэк: берём первое доступное оружие
        Debug.LogError("[WeaponManager] ❌ Не удалось найти стартовое оружие! Берём первое доступное...");
        foreach (var w in _weaponDb.GetAll())
        {
            if (w != null && w.prefab != null)
            {
                Debug.LogError($"[WeaponManager] ⚠️ Крайний фоллбэк: '{w.weaponId}'");
                UnlockWeapon(w);
                _hasInitializedWeapons = true;
                return;
            }
        }
        
        Debug.LogError("[WeaponManager] ❌❌❌ В WeaponDatabase нет ни одного валидного оружия!");
    }

    public void ForcedInitialization()
    {
        if (_weaponDb != null) TryInitializeWeapons();
        _hasInitializedWeapons = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УРОВНИ
    // ─────────────────────────────────────────────────────────────────────────

    public int GetWeaponLevel(string weaponId) => _weaponLevels.TryGetValue(weaponId, out int l) ? l : 0;
    public bool IsWeaponMaxLevel(string weaponId) => GetWeaponLevel(weaponId) >= MAX_WEAPON_LEVEL;
    // ─────────────────────────────────────────────────────────────────────────
    // СБРОС И ПЕРЕИНИЦИАЛИЗАЦИЯ ОРУЖИЯ (для смены персонажа)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает ID первого активного оружия или пустую строку.
    /// </summary>
    public string GetCurrentWeaponId()
    {
        if (activeWeapons.Count == 0) return "";
        var firstWeapon = activeWeapons[0];
        return firstWeapon != null ? firstWeapon.name : "";
    }

    /// <summary>
    /// Сбрасывает оружие и переинициализирует с новым ID.
    /// Вызывается из CharacterSelectManager при смене персонажа.
    /// </summary>
    public void ResetAndReinitializeWeapons(string newWeaponId)
    {
        Debug.Log($"[WeaponManager] ResetAndReinitializeWeapons: '{newWeaponId}'");

        // 1. Удаляем текущее оружие
        foreach (var weapon in activeWeapons)
        {
            if (weapon != null)
            {
                Debug.Log($"[WeaponManager] Удаляем оружие: '{weapon.name}'");
                Destroy(weapon);
            }
        }
        activeWeapons.Clear();
        _weaponLevels.Clear();

        // 2. Сбрасываем флаг инициализации
        _hasInitializedWeapons = false;

        // 3. Устанавливаем новое оружие
        PendingStartWeaponId = newWeaponId;

        // 4. Переинициализируем
        TryInitializeWeapons();

        if (!_hasInitializedWeapons)
        {
            Debug.LogError($"[WeaponManager] ❌ Не удалось переинициализировать оружие '{newWeaponId}'");
            // Фоллбэк на дефолтное
            PendingStartWeaponId = _weaponDb.DefaultStartWeaponId;
            TryInitializeWeapons();
        }

        Debug.Log($"[WeaponManager] ✅ Оружие переинициализировано. Активно: {GetCurrentWeaponId()}");
    }


    public void NotifyWeaponUpgraded(string weaponId)
    {
        if (!_weaponLevels.ContainsKey(weaponId))
        {
            Debug.LogWarning($"[WeaponManager] NotifyWeaponUpgraded: '{weaponId}' не в словаре.");
            return;
        }

        int current = _weaponLevels[weaponId];
        if (current >= MAX_WEAPON_LEVEL) { Debug.Log($"[WeaponManager] '{weaponId}' уже MAX."); return; }

        _weaponLevels[weaponId] = current + 1;
        bool isMax = _weaponLevels[weaponId] >= MAX_WEAPON_LEVEL;
        Debug.Log($"[WeaponManager] ⬆️ '{weaponId}': Lv{current} → Lv{_weaponLevels[weaponId]}{(isMax ? " ⭐ [MAX]" : "")}");

        // НОВОЕ: синхронизируем уровни по сети после изменения
        SyncWeaponLevelsToNetwork();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СИНХРОНИЗАЦИЯ УРОВНЕЙ ПО СЕТИ — НОВОЕ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Упаковывает уровни первых 4 оружий (6 бит на оружие) в int
    /// и передаёт в PlayerStats для синхронизации по сети.
    ///
    /// CompetitiveUIManager читает это у оппонента через
    /// opponentStats.GetNetWeaponLevels() и распаковывает через
    /// PlayerStats.UnpackWeaponLevel(packed, slotIndex).
    /// </summary>
    private void SyncWeaponLevelsToNetwork()
    {
        var stats = GetComponent<PlayerStats>();
        if (stats == null || !stats.IsOwner) return;

        int packed = 0;
        for (int i = 0; i < Mathf.Min(activeWeapons.Count, 4); i++)
        {
            var w = activeWeapons[i];
            if (w == null) continue;
            int level = GetWeaponLevel(w.name);
            // Зажимаем до 63 (6 бит), укладываем в позицию слота
            packed |= (Mathf.Clamp(level, 0, 63)) << (i * 6);
        }

        stats.SyncWeaponLevels(packed);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОСНОВНОЙ API
    // ─────────────────────────────────────────────────────────────────────────

    public void UnlockWeapon(WeaponDefinition weaponDef)
    {
        if (weaponDef == null) 
        {
            Debug.LogError("[WeaponManager] UnlockWeapon: weaponDef is NULL!");
            return;
        }

        Debug.Log($"[WeaponManager] UnlockWeapon: '{weaponDef.weaponId}' | Slots:{activeWeapons.Count}/{maxSlots}");

        if (activeWeapons.Count >= maxSlots)
        {
            Debug.LogWarning($"[WeaponManager] ⚠️ Инвентарь полон! {weaponDef.displayName}");
            return;
        }

        if (HasWeapon(weaponDef.weaponId))
        {
            Debug.LogWarning($"[WeaponManager] ⚠️ Уже есть {weaponDef.displayName}!");
            return;
        }

        Debug.Log($"[WeaponManager] Спавним оружие: '{weaponDef.weaponId}' (prefab: {(weaponDef.prefab != null ? "OK" : "NULL")})");

        GameObject obj = Instantiate(
            weaponDef.prefab, transform.position, Quaternion.identity, transform);
        obj.transform.localPosition = Vector3.zero;
        obj.transform.localRotation = Quaternion.identity;
        obj.name = weaponDef.weaponId;

        // ═══ ИНЪЕКЦИЯ ЗАВИСИМОСТЕЙ ═══
        InjectWeaponDependencies(obj);

        // Уровень устанавливается ДО добавления в список
        _weaponLevels[weaponDef.weaponId] = 1;
        activeWeapons.Add(obj);

        int slotIndex = activeWeapons.Count - 1;
        UpdateUISlot(slotIndex, weaponDef.icon);
        SyncWeaponsToNetwork();

        // НОВОЕ: синхронизируем уровни после добавления нового оружия
        SyncWeaponLevelsToNetwork();

        Debug.Log($"🎯 {weaponDef.displayName} → слот {slotIndex} [Lv1]");
    }

    public Sprite GetWeaponIcon(GameObject weaponObj)
    {
        if (weaponObj == null || _weaponDb == null) return null;
        return _weaponDb.GetById(weaponObj.name)?.icon;
    }

    public WeaponDefinition GetWeaponDefinition(GameObject weaponObj)
    {
        if (weaponObj == null || _weaponDb == null) return null;
        return _weaponDb.GetById(weaponObj.name);
    }

    public List<WeaponDefinition> GetWeaponDefsFromFlags(int flags)
        => _weaponDb != null ? _weaponDb.GetFromFlags(flags) : new List<WeaponDefinition>();

    public bool HasWeapon(string weaponId)
    {
        foreach (var w in activeWeapons)
            if (w.name == weaponId) return true;
        return false;
    }

    public int GetWeaponFlags()
    {
        int flags = 0;
        if (_weaponDb == null) return flags;
        foreach (var w in activeWeapons) flags |= _weaponDb.GetFlagBit(w.name);
        return flags;
    }

    void SyncWeaponsToNetwork()
    {
        var stats = GetComponent<PlayerStats>();
        if (stats != null && stats.IsOwner) stats.SyncWeaponFlags(GetWeaponFlags());
    }

    void UpdateUISlot(int index, Sprite icon)
    {
        if (_slotImages == null || index >= _slotImages.Length) return;
        if (_slotImages[index] == null) return;
        _slotImages[index].sprite = icon;
        _slotImages[index].color = Color.white;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ИНЪЕКЦИЯ ЗАВИСИМОСТЕЙ В ОРУЖИЕ
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Инжектирует зависимости в оружие с диагностикой и fallback.
    /// </summary>
    private void InjectWeaponDependencies(GameObject weaponObj)
    {
        // 1. Проверяем контейнер
        if (InjectionProvider.Container == null)
        {
            Debug.LogError("[WeaponManager] ❌ InjectionProvider.Container == null!");
            return;
        }

        // 2. Проверяем, есть ли SpeedStaffWeapon на объекте
        var speedStaff = weaponObj.GetComponent<SpeedStaffWeapon>();
        if (speedStaff == null)
        {
            speedStaff = weaponObj.GetComponentInChildren<SpeedStaffWeapon>(true);
        }

        // 3. Пробуем стандартную инъекцию
        try
        {
            InjectionProvider.Container.InjectGameObject(weaponObj);
            Debug.Log("[WeaponManager] ✅ InjectGameObject выполнен");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[WeaponManager] ❌ InjectGameObject ошибка: {ex.Message}");
        }

        // 4. Проверяем, прошла ли инъекция для SpeedStaffWeapon
        if (speedStaff != null)
        {
            // Проверяем через reflection - есть ли значения в полях
            var combatCalcField = typeof(SpeedStaffWeapon).GetField("_combatCalc", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var enemyPoolField = typeof(SpeedStaffWeapon).GetField("_enemyPool", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var currentCombatCalc = combatCalcField?.GetValue(speedStaff);
            var currentEnemyPool = enemyPoolField?.GetValue(speedStaff);

            if (currentCombatCalc == null || currentEnemyPool == null)
            {
                Debug.LogWarning("[WeaponManager] ⚠️ InjectGameObject не сработал, пробуем явный Inject...");

                // Fallback: явный Inject
                try
                {
                    InjectionProvider.Container.Inject(speedStaff);
                    Debug.Log("[WeaponManager] ✅ Явный Inject выполнен");
                }
                catch (System.Exception ex2)
                {
                    Debug.LogError($"[WeaponManager] ❌ Явный Inject ошибка: {ex2.Message}");

                    // Крайний fallback: ручная установка через Resolve
                    TryManualInjection(speedStaff);
                }
            }
            else
            {
                Debug.Log("[WeaponManager] ✅ SpeedStaffWeapon инжектирован успешно");
            }
        }
    }

    /// <summary>
    /// Крайний fallback: ручное получение зависимостей из контейнера.
    /// </summary>
    private void TryManualInjection(SpeedStaffWeapon speedStaff)
    {
        try
        {
            var combatCalc = InjectionProvider.Resolve<CombatCalculator>();
            var enemyPool = InjectionProvider.Resolve<EnemyPool>();

            if (combatCalc != null && enemyPool != null)
            {
                // Используем reflection для установки private полей
                var combatCalcField = typeof(SpeedStaffWeapon).GetField("_combatCalc", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var enemyPoolField = typeof(SpeedStaffWeapon).GetField("_enemyPool", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                combatCalcField?.SetValue(speedStaff, combatCalc);
                enemyPoolField?.SetValue(speedStaff, enemyPool);

                Debug.Log("[WeaponManager] ✅ Ручная инъекция выполнена");
            }
            else
            {
                Debug.LogError($"[WeaponManager] ❌ Не удалось resolve: CombatCalculator={combatCalc != null}, EnemyPool={enemyPool != null}");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[WeaponManager] ❌ Ручная инъекция ошибка: {ex.Message}");
        }
    }
}
