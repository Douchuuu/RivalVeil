using UnityEngine;
using VContainer;

/// <summary>
/// Вспомогательный скрипт для отладки игровых систем.
/// Добавьте на пустой GameObject в сцене WorldScene.
///
/// ИСПРАВЛЕНО: все неверные обращения к свойствам заменены корректными:
///   charSelect.SelectedCharacter        — теперь публичное свойство
///   charMultipliers.HealthMultiplier    → GetMaxHealth()
///   charMultipliers.DamageMultiplier    → GetDamageMultiplier()
///   charMultipliers.SpeedMultiplier     → GetMoveSpeedMultiplier()
///   player.StatSheet.MaxHealth.Value    → GetStat(StatType.MaxHealth)
///   weaponManager.HasInitializedWeapons — теперь публичное свойство
///   SelectedCharacter.StartWeaponId     → .startingWeaponId
///   SelectedCharacter.DisplayName       → .displayName
/// </summary>
public class DebugHelper : MonoBehaviour
{
    [Header("Debug Settings")]
    [SerializeField] private bool _enableDebugKeys = true;
    [SerializeField] private KeyCode _forceLevelUpKey = KeyCode.L;
    [SerializeField] private KeyCode _printStatsKey   = KeyCode.P;
    [SerializeField] private KeyCode _forceAddExpKey  = KeyCode.X;
    [SerializeField] private KeyCode _testWeaponInitKey = KeyCode.W;
    [SerializeField] private KeyCode _reinitStatsKey  = KeyCode.R;

    [Inject] private LevelUpManager _levelUpManager;

    private static DebugHelper _instance;
    public static DebugHelper Instance => _instance;

    private void Awake()
    {
        _instance = this;
    }

    private void Update()
    {
        if (!_enableDebugKeys) return;

        if (Input.GetKeyDown(_forceLevelUpKey))    ForceLevelUp();
        if (Input.GetKeyDown(_printStatsKey))       PrintPlayerStats();
        if (Input.GetKeyDown(_forceAddExpKey))      ForceAddExperience();
        if (Input.GetKeyDown(_testWeaponInitKey))   TestWeaponInitialization();
        if (Input.GetKeyDown(_reinitStatsKey))      ReinitializePlayerStats();
    }

    private void ForceLevelUp()
    {
        Debug.Log("[DebugHelper] Форсируем LevelUp...");

        var player = FindFirstObjectByType<PlayerStats>();
        if (player == null) { Debug.LogError("[DebugHelper] Игрок не найден!"); return; }

        var weaponManager = player.GetComponent<WeaponManager>();
        if (weaponManager == null) { Debug.LogError("[DebugHelper] WeaponManager не найден!"); return; }

        if (_levelUpManager == null)
            _levelUpManager = FindFirstObjectByType<LevelUpManager>();

        if (_levelUpManager != null)
        {
            _levelUpManager.ShowLevelUpMenu(weaponManager);
            Debug.Log("[DebugHelper] LevelUp вызван!");
        }
        else
        {
            Debug.LogError("[DebugHelper] LevelUpManager не найден!");
        }
    }

    private void PrintPlayerStats()
    {
        Debug.Log("[DebugHelper] === СТАТЫ ИГРОКА ===");

        var player = FindFirstObjectByType<PlayerStats>();
        if (player == null) { Debug.LogError("[DebugHelper] Игрок не найден!"); return; }

        // ИСПРАВЛЕНО: CharacterMultipliers использует методы-геттеры, не свойства
        var charMultipliers = player.GetComponent<CharacterMultipliers>();
        // ИСПРАВЛЕНО: CharacterSelectManager.SelectedCharacter — теперь публичное свойство
        var charSelect      = player.GetComponent<CharacterSelectManager>();
        var weaponManager   = player.GetComponent<WeaponManager>();

        Debug.Log($"[DebugHelper] Игрок:     {player.name}");
        Debug.Log($"[DebugHelper] IsOwner:   {player.IsOwner}");
        Debug.Log($"[DebugHelper] IsSpawned: {player.IsSpawned}");

        if (charSelect != null)
        {
            // ИСПРАВЛЕНО: SelectedCharacter.displayName (строчная d)
            string charName = charSelect.SelectedCharacter?.displayName ?? "NULL";
            Debug.Log($"[DebugHelper] Персонаж: {charName}");
        }

        if (charMultipliers != null)
        {
            // ИСПРАВЛЕНО: используем геттеры CharacterMultipliers
            Debug.Log($"[DebugHelper] Max Health:     {charMultipliers.GetMaxHealth()}");
            Debug.Log($"[DebugHelper] Damage Mult:    {charMultipliers.GetDamageMultiplier():F2}");
            Debug.Log($"[DebugHelper] Move Speed Mult:{charMultipliers.GetMoveSpeedMultiplier():F2}");
        }

        if (player.StatSheet != null)
        {
            // ИСПРАВЛЕНО: StatSheet использует GetStat(StatType.*), не свойства с .Value
            Debug.Log($"[DebugHelper] Max Health:   {player.StatSheet.GetStat(StatType.MaxHealth):F1}");
            Debug.Log($"[DebugHelper] Damage Mult:  {player.StatSheet.GetStat(StatType.DamageMultiplier):F2}");
            Debug.Log($"[DebugHelper] Move Speed:   {player.StatSheet.GetStat(StatType.MoveSpeedMultiplier):F2}");
            Debug.Log($"[DebugHelper] Attack Speed: {player.StatSheet.GetStat(StatType.AttackSpeed):F2}");
            Debug.Log($"[DebugHelper] Crit Chance:  {player.StatSheet.GetStat(StatType.CritChance) * 100f:F1}%");
            Debug.Log($"[DebugHelper] Crit Mult:    {player.StatSheet.GetStat(StatType.CritMultiplier):F2}");
        }
        else
        {
            Debug.LogError("[DebugHelper] StatSheet is NULL!");
        }

        if (weaponManager != null)
        {
            Debug.Log($"[DebugHelper] PendingWeaponId:        '{weaponManager.PendingStartWeaponId}'");
            // ИСПРАВЛЕНО: HasInitializedWeapons — теперь публичное свойство
            Debug.Log($"[DebugHelper] HasInitializedWeapons:  {weaponManager.HasInitializedWeapons}");
        }

        Debug.Log("[DebugHelper] ====================");
    }

    private void ForceAddExperience()
    {
        Debug.Log("[DebugHelper] Добавляем опыт...");

        var player = FindFirstObjectByType<PlayerStats>();
        if (player == null) { Debug.LogError("[DebugHelper] Игрок не найден!"); return; }

        player.AddExperience(100);
        Debug.Log("[DebugHelper] +100 опыта добавлено!");
    }

    private void TestWeaponInitialization()
    {
        Debug.Log("[DebugHelper] Тестируем инициализацию оружия...");

        var player = FindFirstObjectByType<PlayerStats>();
        if (player == null) { Debug.LogError("[DebugHelper] Игрок не найден!"); return; }

        var weaponManager = player.GetComponent<WeaponManager>();
        if (weaponManager == null) { Debug.LogError("[DebugHelper] WeaponManager не найден!"); return; }

        var charSelect = player.GetComponent<CharacterSelectManager>();
        if (charSelect?.SelectedCharacter != null)
        {
            // ИСПРАВЛЕНО: startingWeaponId (не StartWeaponId)
            string weaponId = charSelect.SelectedCharacter.startingWeaponId;
            Debug.Log($"[DebugHelper] Устанавливаем PendingWeaponId: '{weaponId}'");
            weaponManager.PendingStartWeaponId = weaponId;
        }

        weaponManager.ForcedInitialization();
    }

    private void ReinitializePlayerStats()
    {
        Debug.Log("[DebugHelper] Переинициализируем статы...");

        var player = FindFirstObjectByType<PlayerStats>();
        if (player == null) { Debug.LogError("[DebugHelper] Игрок не найден!"); return; }

        player.ReinitializeStatSheet();
        PrintPlayerStats();
    }

    [ContextMenu("Validate Player Systems")]
    public void ValidatePlayerSystems()
    {
        Debug.Log("[DebugHelper] === ВАЛИДАЦИЯ СИСТЕМ ===");

        var player = FindFirstObjectByType<PlayerStats>();
        if (player == null)
        {
            Debug.LogError("[DebugHelper] ❌ PlayerStats не найден!");
            return;
        }

        bool allValid = true;

        // Используем Unity.Netcode.NetworkObject через GetComponent
        var networkObj = player.GetComponent<Unity.Netcode.NetworkObject>();

        var components = new (string name, UnityEngine.Component component)[]
        {
            ("PlayerStats",          player),
            ("CharacterMultipliers", player.GetComponent<CharacterMultipliers>()),
            ("CharacterSelectManager", player.GetComponent<CharacterSelectManager>()),
            ("WeaponManager",        player.GetComponent<WeaponManager>()),
            ("NetworkObject",        networkObj),
        };

        foreach (var (name, component) in components)
        {
            if (component == null)
            {
                Debug.LogError($"[DebugHelper] ❌ {name} отсутствует!");
                allValid = false;
            }
            else
            {
                Debug.Log($"[DebugHelper] ✅ {name} найден");
            }
        }

        if (player.StatSheet == null)
        {
            Debug.LogError("[DebugHelper] ❌ StatSheet is NULL!");
            allValid = false;
        }
        else
        {
            Debug.Log("[DebugHelper] ✅ StatSheet инициализирован");
        }

        var charSelect = player.GetComponent<CharacterSelectManager>();
        if (charSelect?.SelectedCharacter == null)
            Debug.LogWarning("[DebugHelper] ⚠️ Персонаж не выбран!");
        else
            // ИСПРАВЛЕНО: .displayName (строчная d)
            Debug.Log($"[DebugHelper] ✅ Персонаж: {charSelect.SelectedCharacter.displayName}");

        if (allValid) Debug.Log("[DebugHelper] ✅ Все системы в порядке!");
        else          Debug.LogError("[DebugHelper] ❌ Обнаружены проблемы!");

        Debug.Log("[DebugHelper] ====================");
    }
}
