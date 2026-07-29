using UnityEngine;

/// <summary>
/// WeaponDefinition — идентификатор оружия.
///
/// Содержит ТОЛЬКО данные необходимые для создания и отображения оружия.
/// Балансировка апгрейдов — в UpgradeBalanceConfig (ScriptableObject).
/// Базовые характеристики оружия — в самом MonoBehaviour оружия (SwordWeapon, etc.)
///
/// ─── АРХИТЕКТУРА ─────────────────────────────────────────────────────────────
///
/// WeaponDefinition     → ЧТО это за оружие (id, название, иконка, префаб)
/// UpgradeBalanceConfig → КАК апгрейдить (базовые значения, множители редкости)
/// SwordWeapon / etc.   → КАК оружие работает (урон, скорость, логика)
///
/// ─── КАК ДОБАВИТЬ НОВОЕ ОРУЖИЕ ───────────────────────────────────────────────
///
/// 1. Assets → ПКМ → Create → Game → Weapon Definition — задай weaponId, displayName, icon, prefab
/// 2. Добавь запись в WeaponDatabase
/// 3. Создай MonoBehaviour оружия, реализуй IDynamicUpgradeable
/// 4. Если нужна особая балансировка — добавь weaponOverride в UpgradeBalanceConfig
///
/// Больше ничего не нужно — LevelUp, UI, сеть подхватывают автоматически.
/// </summary>
[CreateAssetMenu(menuName = "Game/Weapon Definition", fileName = "NewWeapon")]
public class WeaponDefinition : ScriptableObject
{
    [Header("Основная информация")]
    [Tooltip("Уникальный ID оружия. Используется в WeaponDatabase и WeaponManager.\n" +
             "Должен совпадать с weaponId в UpgradeBalanceConfig.WeaponUpgradeOverride если есть.")]
    [SerializeField] public string weaponId;

    [Tooltip("Название для отображения в UI (LevelUp карточки, подсказки).")]
    [SerializeField] public string displayName;

    [Tooltip("Иконка в слотах оружий.")]
    [SerializeField] public Sprite icon;

    [Tooltip("Префаб оружия. Должен содержать MonoBehaviour реализующий IDynamicUpgradeable.")]
    [SerializeField] public GameObject prefab;

    public override string ToString() => $"{displayName} ({weaponId})";

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(weaponId))
            Debug.LogWarning($"[WeaponDefinition] {name}: weaponId пусто!", this);
        if (string.IsNullOrEmpty(displayName))
            Debug.LogWarning($"[WeaponDefinition] {name}: displayName пусто!", this);
        if (prefab == null)
            Debug.LogWarning($"[WeaponDefinition] {name}: prefab не назначен!", this);
        if (icon == null)
            Debug.LogWarning($"[WeaponDefinition] {name}: icon не назначен!", this);
    }
}
