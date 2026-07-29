using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// WeaponSlotsProvider — ЛУЧШЕЕ ИСПРАВЛЕНИЕ (часть 2 BUG #2)
///
/// ФИКС: Добавлен флаг isWeaponSlotsProvider.
///   В сцене есть несколько объектов с этим компонентом:
///   WeaponSlotContainer (оружия) + TotemSlots + TotemSlotsCOP (тотемы).
///   Все три инициализируются и перезаписывают статический CurrentSlots.
///   WeaponManager брал тотемные слоты вместо оружейных.
///
///   Решение: статический CurrentSlots обновляется ТОЛЬКО если
///   isWeaponSlotsProvider = true (только на контейнере оружий).
///   Тотемные контейнеры ставят isWeaponSlotsProvider = false.
/// </summary>
public class WeaponSlotsProvider : MonoBehaviour
{
    [Header("Настройки")]
    [Tooltip("Если пусто — ищет Image во всех прямых детях этого объекта")]
    [SerializeField] private Image[] manualSlots;

    [Tooltip("TRUE только для контейнера ОРУЖИЙ.\n" +
             "FALSE для тотемов и любых других слотов.\n" +
             "Только true-провайдер обновляет статический CurrentSlots,\n" +
             "который читает WeaponManager.")]
    [SerializeField] private bool isWeaponSlotsProvider = true;

    // ─── СТАТИЧЕСКИЙ STATE (ГЛОБАЛЬНОЕ СОСТОЯНИЕ) ─────────────────────────────
    // ✅ ЛУЧШЕЕ РЕШЕНИЕ: статический state вместо Event
    // WeaponManager может проверить это в любой момент без race condition

    /// <summary>true если слоты готовы к использованию</summary>
    public static bool IsReady { get; private set; }

    /// <summary>текущие слоты (null если не готовы)</summary>
    public static Image[] CurrentSlots { get; private set; }

    // Для отладки
    private static GameObject _registeredFrom;

    // ─── ЛОКАЛЬНЫЙ КЭШІ ────────────────────────────────────────────────────────
    private Image[] _cachedSlots;
    private bool _hasBeenInitialized;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void OnEnable()
    {
        if (!_hasBeenInitialized)
        {
            InitializeSlots();
            _hasBeenInitialized = true;
        }
        else
        {
            // Восстанавливаем глобальное состояние только для weapon slots
            if (isWeaponSlotsProvider)
                SetGlobalState(_cachedSlots);
        }
    }

    void OnDisable()
    {
        if (isWeaponSlotsProvider && IsReady && CurrentSlots == _cachedSlots)
        {
            IsReady = false;
            CurrentSlots = null;
            _registeredFrom = null;
            Debug.Log($"[WeaponSlotsProvider] STATE CLEARED (Canvas деактивирован)");
        }
    }

    void OnDestroy()
    {
        // При уничтожении полностью очищаем state
        if (IsReady && CurrentSlots == _cachedSlots)
        {
            IsReady = false;
            CurrentSlots = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОСНОВНАЯ ЛОГИКА
    // ─────────────────────────────────────────────────────────────────────────

    private void InitializeSlots()
    {
        _cachedSlots = FindSlots();

        if (_cachedSlots == null || _cachedSlots.Length == 0)
        {
            Debug.LogError($"[WeaponSlotsProvider] {gameObject.name}: " +
                         "Нет слотов! Убедись что есть дочерние Image компоненты.");
            return;
        }

        // Статический CurrentSlots обновляем только для контейнера ОРУЖИЙ
        if (isWeaponSlotsProvider)
            SetGlobalState(_cachedSlots);

        // Событие шлём всегда — WeaponManager фильтрует сам по IsOwner
        WeaponManager.OnSlotsRegistered?.Invoke(_cachedSlots);

        Debug.Log($"✅ [WeaponSlotsProvider] {gameObject.name}: " +
                 $"Инициализировано {_cachedSlots.Length} слотов" +
                 (isWeaponSlotsProvider ? " [WEAPON SLOTS]" : " [other, static не обновлён]"));
    }

    /// <summary>
    /// ✅ Устанавливает глобальное состояние (thread-safe операция)
    /// </summary>
    private void SetGlobalState(Image[] slots)
    {
        IsReady = (slots != null && slots.Length > 0);
        CurrentSlots = slots;
        _registeredFrom = gameObject;

        if (IsReady)
            Debug.Log($"[WeaponSlotsProvider] STATE UPDATED: IsReady=true, " +
                     $"SlotCount={slots.Length}, from {gameObject.name}");
    }

    /// <summary>
    /// Ищет Image компоненты в дочерних объектах
    /// </summary>
    private Image[] FindSlots()
    {
        // Опция 1: если вручную назначены в Inspector
        if (manualSlots != null && manualSlots.Length > 0)
        {
            Debug.Log($"[WeaponSlotsProvider] Используются вручную назначенные слоты " +
                     $"(кол-во: {manualSlots.Length})");
            return manualSlots;
        }

        // Опция 2: автоматический поиск в дочерних объектах
        var slotsList = new System.Collections.Generic.List<Image>();

        foreach (Transform child in transform)
        {
            Image img = child.GetComponent<Image>();
            if (img != null)
            {
                slotsList.Add(img);
                Debug.Log($"[WeaponSlotsProvider] Найден слот: {child.name}");
            }
        }

        return slotsList.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СТАТИСТИКА (для отладки)
    // ─────────────────────────────────────────────────────────────────────────

    public static void LogState()
    {
        Debug.Log($"[WeaponSlotsProvider] STATE DEBUG: " +
                 $"IsReady={IsReady}, " +
                 $"SlotCount={(CurrentSlots?.Length ?? 0)}, " +
                 $"RegisteredFrom={_registeredFrom?.name ?? "NONE"}");
    }
}
/*using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// WeaponSlotsProvider — ЛУЧШЕЕ ИСПРАВЛЕНИЕ (часть 2 BUG #2)
///
/// ФИКС: Добавлен флаг isWeaponSlotsProvider.
///   В сцене есть несколько объектов с этим компонентом:
///   WeaponSlotContainer (оружия) + TotemSlots + TotemSlotsCOP (тотемы).
///   Все три инициализируются и перезаписывают статический CurrentSlots.
///   WeaponManager брал тотемные слоты вместо оружейных.
///
///   Решение: статический CurrentSlots обновляется ТОЛЬКО если
///   isWeaponSlotsProvider = true (только на контейнере оружий).
///   Тотемные контейнеры ставят isWeaponSlotsProvider = false.
/// </summary>
public class WeaponSlotsProvider : MonoBehaviour
{
    [Header("Настройки")]
    [Tooltip("Если пусто — ищет Image во всех прямых детях этого объекта")]
    [SerializeField] private Image[] manualSlots;

    [Tooltip("TRUE только для контейнера ОРУЖИЙ.\n" +
             "FALSE для тотемов и любых других слотов.\n" +
             "Только true-провайдер обновляет статический CurrentSlots,\n" +
             "который читает WeaponManager.")]
    [SerializeField] private bool isWeaponSlotsProvider = true;

    // ─── СТАТИЧЕСКИЙ STATE (ГЛОБАЛЬНОЕ СОСТОЯНИЕ) ─────────────────────────────
    // ✅ ЛУЧШЕЕ РЕШЕНИЕ: статический state вместо Event
    // WeaponManager может проверить это в любой момент без race condition

    /// <summary>true если слоты готовы к использованию</summary>
    public static bool IsReady { get; private set; }

    /// <summary>текущие слоты (null если не готовы)</summary>
    public static Image[] CurrentSlots { get; private set; }

    // Для отладки
    private static GameObject _registeredFrom;

    // ─── ЛОКАЛЬНЫЙ КЭШІ ────────────────────────────────────────────────────────
    private Image[] _cachedSlots;
    private bool _hasBeenInitialized;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void OnEnable()
    {
        if (!_hasBeenInitialized)
        {
            InitializeSlots();
            _hasBeenInitialized = true;
        }
        else
        {
            // Восстанавливаем глобальное состояние только для weapon slots
            if (isWeaponSlotsProvider)
                SetGlobalState(_cachedSlots);
        }
    }

    void OnDisable()
    {
        if (isWeaponSlotsProvider && IsReady && CurrentSlots == _cachedSlots)
        {
            IsReady = false;
            CurrentSlots = null;
            _registeredFrom = null;
            Debug.Log($"[WeaponSlotsProvider] STATE CLEARED (Canvas деактивирован)");
        }
    }

    void OnDestroy()
    {
        // При уничтожении полностью очищаем state
        if (IsReady && CurrentSlots == _cachedSlots)
        {
            IsReady = false;
            CurrentSlots = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОСНОВНАЯ ЛОГИКА
    // ─────────────────────────────────────────────────────────────────────────

    private void InitializeSlots()
    {
        _cachedSlots = FindSlots();

        if (_cachedSlots == null || _cachedSlots.Length == 0)
        {
            Debug.LogError($"[WeaponSlotsProvider] {gameObject.name}: " +
                         "Нет слотов! Убедись что есть дочерние Image компоненты.");
            return;
        }

        // Статический CurrentSlots обновляем только для контейнера ОРУЖИЙ
        if (isWeaponSlotsProvider)
            SetGlobalState(_cachedSlots);

        // Событие шлём всегда — WeaponManager фильтрует сам по IsOwner
        WeaponManager.OnSlotsRegistered?.Invoke(_cachedSlots);

        Debug.Log($"✅ [WeaponSlotsProvider] {gameObject.name}: " +
                 $"Инициализировано {_cachedSlots.Length} слотов" +
                 (isWeaponSlotsProvider ? " [WEAPON SLOTS]" : " [other, static не обновлён]"));
    }

    /// <summary>
    /// ✅ Устанавливает глобальное состояние (thread-safe операция)
    /// </summary>
    private void SetGlobalState(Image[] slots)
    {
        IsReady = (slots != null && slots.Length > 0);
        CurrentSlots = slots;
        _registeredFrom = gameObject;

        if (IsReady)
            Debug.Log($"[WeaponSlotsProvider] STATE UPDATED: IsReady=true, " +
                     $"SlotCount={slots.Length}, from {gameObject.name}");
    }

    /// <summary>
    /// Ищет Image компоненты в дочерних объектах
    /// </summary>
    private Image[] FindSlots()
    {
        // Опция 1: если вручную назначены в Inspector
        if (manualSlots != null && manualSlots.Length > 0)
        {
            Debug.Log($"[WeaponSlotsProvider] Используются вручную назначенные слоты " +
                     $"(кол-во: {manualSlots.Length})");
            return manualSlots;
        }

        // Опция 2: автоматический поиск в дочерних объектах
        var slotsList = new System.Collections.Generic.List<Image>();

        foreach (Transform child in transform)
        {
            Image img = child.GetComponent<Image>();
            if (img != null)
            {
                slotsList.Add(img);
                Debug.Log($"[WeaponSlotsProvider] Найден слот: {child.name}");
            }
        }

        return slotsList.ToArray();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СТАТИСТИКА (для отладки)
    // ─────────────────────────────────────────────────────────────────────────

    public static void LogState()
    {
        Debug.Log($"[WeaponSlotsProvider] STATE DEBUG: " +
                 $"IsReady={IsReady}, " +
                 $"SlotCount={(CurrentSlots?.Length ?? 0)}, " +
                 $"RegisteredFrom={_registeredFrom?.name ?? "NONE"}");
    }
}
*/