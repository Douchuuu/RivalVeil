using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Валидатор инициализации игрока.
/// Добавьте на префаб игрока для автоматической проверки.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО v1.3:
/// ══════════════════════════════════════════════════════════════════════
///   - Увеличена задержка валидации с 0.5с до 5с (время на выбор персонажа)
///   - Убрано авто-применение дефолтного персонажа в режиме Selecting
///     (игрок должен выбрать персонажа сам через UI)
///   - Добавлена проверка GameMode - в режиме Selecting не применяем дефолтного
///   - Добавлено логирование для диагностики проблем с выбором персонажа
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class PlayerInitializationValidator : NetworkBehaviour
{
    [Header("Validation Settings")]
    [SerializeField] private bool  _validateOnSpawn  = true;
    [SerializeField] private bool  _autoFixIssues    = true;
    [SerializeField] private float _validationDelay  = 5.0f; // ИСПРАВЛЕНО: было 0.5f

    private PlayerStats          _playerStats;
    private CharacterMultipliers _charMultipliers;
    private CharacterSelectManager _charSelectManager;
    private WeaponManager        _weaponManager;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (_validateOnSpawn)
            Invoke(nameof(ValidateAndFix), _validationDelay);
    }

    private void ValidateAndFix()
    {
        Debug.Log("[PlayerInitValidator] Начинаем валидацию...");

        _playerStats       = GetComponent<PlayerStats>();
        _charMultipliers   = GetComponent<CharacterMultipliers>();
        _charSelectManager = GetComponent<CharacterSelectManager>();
        _weaponManager     = GetComponent<WeaponManager>();

        bool hasErrors = false;

        // 1. PlayerStats + StatSheet
        if (_playerStats == null)
        {
            Debug.LogError("[PlayerInitValidator] ❌ PlayerStats не найден!");
            hasErrors = true;
        }
        else if (_playerStats.StatSheet == null)
        {
            Debug.LogError("[PlayerInitValidator] ❌ StatSheet is NULL!");
            if (_autoFixIssues)
            {
                Debug.Log("[PlayerInitValidator] Переинициализируем StatSheet через ReinitializeStatSheet()...");
                _playerStats.ReinitializeStatSheet();
            }
            hasErrors = true;
        }
        else
        {
            Debug.Log("[PlayerInitValidator] ✅ PlayerStats и StatSheet в порядке");
        }

        // 2. CharacterMultipliers
        if (_charMultipliers == null)
        {
            Debug.LogError("[PlayerInitValidator] ❌ CharacterMultipliers не найден!");
            hasErrors = true;
        }
        else
        {
            Debug.Log("[PlayerInitValidator] ✅ CharacterMultipliers найден");
        }

        // 3. CharacterSelectManager + выбранный персонаж
        if (_charSelectManager == null)
        {
            Debug.LogError("[PlayerInitValidator] ❌ CharacterSelectManager не найден!");
            hasErrors = true;
        }
        else if (!_charSelectManager.HasSelectedCharacter())
        {
            // ИСПРАВЛЕНО: Не применяем дефолтного персонажа если мы в режиме Selecting
            // Игрок должен выбрать персонажа сам через UI
            if (GameModeManager.IsSelecting())
            {
                Debug.Log("[PlayerInitValidator] ℹ️ Режим Selecting - ждём выбор игрока через UI");
                // Не применяем дефолтного - игрок выберет сам
            }
            else
            {
                // Если не Selecting и персонаж не выбран - применяем дефолтного
                Debug.LogWarning("[PlayerInitValidator] ⚠️ Персонаж не выбран и режим не Selecting! Применяем дефолтного...");
                if (_autoFixIssues)
                {
                    _charSelectManager.ApplySelectedCharacter();
                }
            }
        }
        else
        {
            Debug.Log($"[PlayerInitValidator] ✅ Персонаж: {_charSelectManager.SelectedCharacter.displayName}");
        }

        // 4. WeaponManager
        if (_weaponManager == null)
        {
            Debug.LogError("[PlayerInitValidator] ❌ WeaponManager не найден!");
            hasErrors = true;
        }
        else
        {
            Debug.Log($"[PlayerInitValidator] ✅ WeaponManager. PendingWeaponId: '{_weaponManager.PendingStartWeaponId}'");

            // ИСПРАВЛЕНИЕ: Убран ложный варнинг. Пустой PendingStartWeaponId нормален —
            // WeaponManager использует DefaultStartWeaponId из WeaponDatabase как fallback.
            if (string.IsNullOrEmpty(_weaponManager.PendingStartWeaponId))
            {
                Debug.Log("[PlayerInitValidator] ℹ️ PendingStartWeaponId пуст — будет использован DefaultStartWeaponId из WeaponDatabase");

                // Автофикс: если персонаж выбран, устанавливаем его оружие
                if (_autoFixIssues && _charSelectManager?.SelectedCharacter != null)
                {
                    string weaponId = _charSelectManager.SelectedCharacter.startingWeaponId;
                    Debug.Log($"[PlayerInitValidator] Устанавливаем PendingStartWeaponId: '{weaponId}'");
                    _weaponManager.PendingStartWeaponId = weaponId;
                }
            }
        }

        // 5. Соответствие оружия персонажу
        if (_charSelectManager?.SelectedCharacter != null && _weaponManager != null)
        {
            string expectedWeapon = _charSelectManager.SelectedCharacter.startingWeaponId;
            string actualWeapon   = _weaponManager.PendingStartWeaponId;

            if (expectedWeapon != actualWeapon)
            {
                Debug.LogWarning($"[PlayerInitValidator] ⚠️ Несоответствие оружия! Ожидалось: '{expectedWeapon}', Фактически: '{actualWeapon}'");
                if (_autoFixIssues)
                {
                    Debug.Log($"[PlayerInitValidator] Исправляем оружие на '{expectedWeapon}'");
                    _weaponManager.PendingStartWeaponId = expectedWeapon;
                }
            }
            else
            {
                Debug.Log($"[PlayerInitValidator] ✅ Оружие соответствует: '{expectedWeapon}'");
            }
        }

        // 6. Характеристики персонажа
        if (_playerStats?.StatSheet != null && _charSelectManager?.SelectedCharacter != null)
        {
            var charDef = _charSelectManager.SelectedCharacter;

            float expectedHealth = charDef.maxHealth;
            float actualHealth   = _playerStats.StatSheet.GetStat(StatType.MaxHealth);

            if (expectedHealth > 0f && Mathf.Abs(expectedHealth - actualHealth) > 5f)
            {
                Debug.LogWarning($"[PlayerInitValidator] ⚠️ HP не соответствует! Ожидалось: {expectedHealth}, Фактически: {actualHealth}");
                if (_autoFixIssues)
                {
                    Debug.Log("[PlayerInitValidator] Переинициализируем StatSheet...");
                    _playerStats.ReinitializeStatSheet();
                }
            }
            else
            {
                Debug.Log($"[PlayerInitValidator] ✅ HP корректен: {actualHealth:F0}");
            }
        }

        // 7. Инициализация оружия
        if (_weaponManager != null && !_weaponManager.HasInitializedWeapons)
        {
            Debug.LogWarning("[PlayerInitValidator] ⚠️ Оружие не инициализировано!");
            if (_autoFixIssues)
            {
                Debug.Log("[PlayerInitValidator] Форсируем инициализацию оружия...");
                _weaponManager.ForcedInitialization();
            }
        }
        else if (_weaponManager != null)
        {
            Debug.Log("[PlayerInitValidator] ✅ Оружие инициализировано");
        }

        if (hasErrors) Debug.LogError("[PlayerInitValidator] ❌ Валидация завершена с ошибками!");
        else           Debug.Log("[PlayerInitValidator] ✅ Валидация успешно завершена!");
    }

    [ContextMenu("Validate Now")]
    public void ManualValidate() => ValidateAndFix();

    [ContextMenu("Force Fix All Issues")]
    public void ForceFixAll()
    {
        _autoFixIssues = true;
        ValidateAndFix();
    }
}
