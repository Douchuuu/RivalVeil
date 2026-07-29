using UnityEngine;

/// <summary>
/// TotemDefinition — данные одного тотема (ScriptableObject).
///
/// ─── АРХИТЕКТУРА ────────────────────────────────────────────────────────────
///   TotemDefinition     → ЧТО это за тотем (id, название, тип бонуса, иконка)
///   UpgradeBalanceConfig → КАК балансировать (диапазоны по редкостям)
///   TotemManager        → КАК применяется (StatModifier на PlayerStatSheet)
///
/// ─── КАК ДОБАВИТЬ НОВЫЙ ТОТЕМ ───────────────────────────────────────────────
///   1. Assets → ПКМ → Create → Game → Totem Definition
///   2. Задай totemId, displayName, bonusType
///   3. Иконка — опциональна (тотем не имеет 3D-модели, только UI)
///   4. Добавь в TotemDatabase
///   5. Диапазоны — в UpgradeBalanceConfig → totemRanges
/// </summary>
[CreateAssetMenu(menuName = "Game/Totem Definition", fileName = "NewTotem")]
public class TotemDefinition : ScriptableObject
{
    [Header("Основная информация")]
    [Tooltip("Уникальный ID тотема. Должен совпадать с ключом в TotemDatabase.")]
    public string totemId;

    [Tooltip("Название для отображения в UI.")]
    public string displayName;

    [Tooltip("Описание тотема (опционально).")]
    [TextArea(2, 4)]
    public string description;

    [Header("Бонус тотема")]
    [Tooltip("Тип пассивного бонуса. Диапазоны значений — в UpgradeBalanceConfig.totemRanges.")]
    public TotemBonusType bonusType;

    [Header("Иконка (опционально)")]
    [Tooltip("Иконка для UI слотов тотемов. Тотем не имеет 3D-модели.")]
    public Sprite icon;

    public override string ToString() => $"{displayName} ({totemId}, {bonusType})";

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(totemId))
            Debug.LogWarning($"[TotemDefinition] {name}: totemId пусто!", this);
        if (string.IsNullOrEmpty(displayName))
            Debug.LogWarning($"[TotemDefinition] {name}: displayName пусто!", this);
    }
}
