using UnityEngine;

/// <summary>
/// ItemDefinition — определение предмета для сундуков.
/// 
/// ОТЛИЧИЯ ОТ ОРУЖИЯ:
///   - Нет префаба (предметы хранятся в инвентаре, не на сцене)
///   - Может быть несколько одного типа (неограниченные слоты)
///   - Редкость не влияет на характеристики, только на редкость выпадения
///   - Может иметь неограниченное количество эффектов
/// 
/// ХАРАКТЕРИСТИКИ И СТАКИНГ:
///   Предметы стакаются с характеристиками:
///   - Характеристиками персонажа (PlayerStats)
///   - Характеристиками оружия (WeaponManager)
///   Все применяются через StatModifier
/// </summary>
[CreateAssetMenu(menuName = "Game/Item Definition", fileName = "NewItem")]
public class ItemDefinition : ScriptableObject
{
    [Header("🔷 ОСНОВНАЯ ИНФОРМАЦИЯ")]
    [SerializeField] public string itemId;
    [SerializeField] public string displayName;
    [SerializeField] public Sprite icon;
    [TextArea(3, 5)]
    [SerializeField] public string description;

    [Header("💎 РЕДКОСТЬ И ВЫПАДЕНИЕ")]
    [SerializeField] public ItemRarity rarity;
    [Tooltip("Относительный вес выпадения (чем больше, тем выше шанс выпадения этой редкости).")]
    [SerializeField] public float dropWeight = 1f;

    [Header("⚡ ЭФФЕКТЫ ПРЕДМЕТА")]
    [SerializeField] public ItemEffect[] effects;

    [Header("🎨 ВИЗУАЛИЗАЦИЯ")]
    [Tooltip("Цвет предмета в инвентаре и при выпадении из сундука.")]
    [SerializeField] public Color rarityColor = Color.white;

    [Header("📊 ФЛАГИ")]
    [SerializeField] public int FlagMask = 1;

    public bool IsActiveInFlags(int flags) => (flags & FlagMask) != 0;

    public override string ToString() => $"{displayName} ({itemId})";

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(itemId))
            Debug.LogWarning($"[ItemDefinition] {name}: itemId пусто!", this);
        if (string.IsNullOrEmpty(displayName))
            Debug.LogWarning($"[ItemDefinition] {name}: displayName пусто!", this);
        if (icon == null)
            Debug.LogWarning($"[ItemDefinition] {name}: icon не назначена!", this);
        
        // Установка цвета по редкости
        switch (rarity)
        {
            case ItemRarity.Common:
                rarityColor = new Color(0.7f, 0.7f, 0.7f, 1f); // Серый
                break;
            case ItemRarity.Rare:
                rarityColor = new Color(0.27f, 0.6f, 1f, 1f);  // Синий
                break;
            case ItemRarity.Mythic:
                rarityColor = new Color(0.67f, 0.27f, 1f, 1f); // Фиолетовый
                break;
            case ItemRarity.Legendary:
                rarityColor = new Color(1f, 0.72f, 0f, 1f);    // Золотой
                break;
        }
    }

    /// <summary>Получить локализованный текст редкости.</summary>
    public string GetRarityLabel()
    {
        switch (rarity)
        {
            case ItemRarity.Common:    return "<color=#AAAAAA>ОБЫЧНЫЙ</color>";
            case ItemRarity.Rare:      return "<color=#4499FF>РЕДКИЙ</color>";
            case ItemRarity.Mythic:    return "<color=#AA44FF>МИФИЧЕСКИЙ</color>";
            case ItemRarity.Legendary: return "<color=#FFB700>ЛЕГЕНДАРНЫЙ</color>";
            default:                   return "НЕИЗВЕСТНЫЙ";
        }
    }

    /// <summary>Полное описание для UI.</summary>
    public string GetFullDescription()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{GetRarityLabel()} — {displayName}");
        sb.AppendLine(description);
        sb.AppendLine();
        foreach (var effect in effects)
            sb.AppendLine(effect.GetDescription());
        return sb.ToString().TrimEnd();
    }
}
