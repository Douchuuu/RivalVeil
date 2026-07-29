#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// ItemBalanceInspector — удобный редакторский инструмент для баланса предметов.
///
/// ОТКРЫТИЕ: Window → Item Balance Inspector
///
/// ВОЗМОЖНОСТИ:
///   • Список всех ItemDefinition из папки Assets/
///   • Редактирование dropWeight, шансов редкостей прямо в окне
///   • Предпросмотр вероятностей выпадения каждого предмета
///   • Быстрый доступ к Chest настройкам (шансы редкостей)
///   • Визуализация шанса ExtraExpOrbChance (Шляпа)
/// </summary>
public class ItemBalanceInspector : EditorWindow
{
    [MenuItem("Window/Item Balance Inspector")]
    public static void ShowWindow()
    {
        var window = GetWindow<ItemBalanceInspector>("Item Balance");
        window.minSize = new Vector2(500, 400);
    }

    // ── Состояние окна ────────────────────────────────────────────────────────
    private List<ItemDefinition> _items = new List<ItemDefinition>();
    private Vector2 _scrollPos;
    private bool    _showChestSettings = true;
    private bool    _showItemList      = true;
    private bool    _showProbabilities = true;

    // Шансы редкостей (зеркало Chest.cs настроек для предпросмотра)
    private float _chanceCommon    = 60f;
    private float _chanceRare      = 25f;
    private float _chanceMythic    = 12f;
    private float _chanceLegendary =  3f;

    // ── GUI стили ─────────────────────────────────────────────────────────────
    private GUIStyle _headerStyle;
    private GUIStyle _rarityStyle;
    private bool     _stylesInitialized;

    // ─────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        RefreshItems();
    }

    private void OnGUI()
    {
        InitStyles();

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("⚖️  Item Balance Inspector", _headerStyle);
        EditorGUILayout.Space(5);

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        DrawChestSettings();
        EditorGUILayout.Space(10);
        DrawItemList();
        EditorGUILayout.Space(10);
        DrawProbabilities();

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(5);
        if (GUILayout.Button("🔄  Обновить список предметов", GUILayout.Height(30)))
            RefreshItems();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЕКЦИЯ: ШАНСЫ РЕДКОСТЕЙ СУНДУКА
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawChestSettings()
    {
        _showChestSettings = EditorGUILayout.BeginFoldoutHeaderGroup(_showChestSettings, "📦 Шансы редкостей сундука (предпросмотр)");
        if (!_showChestSettings) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

        EditorGUI.indentLevel++;

        float total = _chanceCommon + _chanceRare + _chanceMythic + _chanceLegendary;

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Common (серый)", GUILayout.Width(140));
            _chanceCommon = EditorGUILayout.Slider(_chanceCommon, 0f, 100f);
            EditorGUILayout.LabelField($"{(_chanceCommon / total * 100f):F1}%", GUILayout.Width(50));
        }
        DrawRarityBar(_chanceCommon / total, new Color(0.7f, 0.7f, 0.7f));

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Rare (синий)", GUILayout.Width(140));
            _chanceRare = EditorGUILayout.Slider(_chanceRare, 0f, 100f);
            EditorGUILayout.LabelField($"{(_chanceRare / total * 100f):F1}%", GUILayout.Width(50));
        }
        DrawRarityBar(_chanceRare / total, new Color(0.27f, 0.6f, 1f));

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Mythic (фиолет.)", GUILayout.Width(140));
            _chanceMythic = EditorGUILayout.Slider(_chanceMythic, 0f, 100f);
            EditorGUILayout.LabelField($"{(_chanceMythic / total * 100f):F1}%", GUILayout.Width(50));
        }
        DrawRarityBar(_chanceMythic / total, new Color(0.67f, 0.27f, 1f));

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Legendary (золот.)", GUILayout.Width(140));
            _chanceLegendary = EditorGUILayout.Slider(_chanceLegendary, 0f, 100f);
            EditorGUILayout.LabelField($"{(_chanceLegendary / total * 100f):F1}%", GUILayout.Width(50));
        }
        DrawRarityBar(_chanceLegendary / total, new Color(1f, 0.72f, 0f));

        if (Mathf.Abs(total - 100f) > 0.1f)
            EditorGUILayout.HelpBox($"Сумма шансов = {total:F1}% (нормализуется автоматически при запуске)", MessageType.Info);

        EditorGUI.indentLevel--;
        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    private void DrawRarityBar(float fraction, Color color)
    {
        var rect = EditorGUILayout.GetControlRect(false, 6);
        rect.x     += EditorGUI.indentLevel * 15;
        rect.width -= EditorGUI.indentLevel * 15;

        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, rect.height), new Color(0.2f, 0.2f, 0.2f));
        EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fraction), rect.height), color);
        EditorGUILayout.Space(2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЕКЦИЯ: СПИСОК ПРЕДМЕТОВ
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawItemList()
    {
        _showItemList = EditorGUILayout.BeginFoldoutHeaderGroup(_showItemList, $"🎁 Предметы ({_items.Count} шт.)");
        if (!_showItemList) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

        if (_items.Count == 0)
        {
            EditorGUILayout.HelpBox("Предметы не найдены. Нажми 'Обновить'.", MessageType.Warning);
            EditorGUILayout.EndFoldoutHeaderGroup();
            return;
        }

        // Заголовок таблицы
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            EditorGUILayout.LabelField("Название",   GUILayout.Width(150));
            EditorGUILayout.LabelField("Редкость",   GUILayout.Width(90));
            EditorGUILayout.LabelField("Вес (weight)", GUILayout.Width(120));
            EditorGUILayout.LabelField("Эффекты",    GUILayout.ExpandWidth(true));
        }

        foreach (var item in _items)
        {
            if (item == null) continue;

            using (new EditorGUILayout.HorizontalScope())
            {
                // Название (кликабельное)
                if (GUILayout.Button(item.displayName, EditorStyles.label, GUILayout.Width(150)))
                    Selection.activeObject = item;

                // Редкость (цветная метка)
                var rarityColor = GetEditorRarityColor(item.rarity);
                var old = GUI.color;
                GUI.color = rarityColor;
                EditorGUILayout.LabelField(item.rarity.ToString(), GUILayout.Width(90));
                GUI.color = old;

                // Вес выпадения (редактируемый)
                float newWeight = EditorGUILayout.FloatField(item.dropWeight, GUILayout.Width(120));
                if (Mathf.Abs(newWeight - item.dropWeight) > 0.001f)
                {
                    Undo.RecordObject(item, "Change Drop Weight");
                    item.dropWeight = Mathf.Max(0f, newWeight);
                    EditorUtility.SetDirty(item);
                }

                // Эффекты (краткое описание)
                string effectsSummary = BuildEffectsSummary(item);
                EditorGUILayout.LabelField(effectsSummary, EditorStyles.miniLabel);
            }
        }

        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЕКЦИЯ: ВЕРОЯТНОСТИ ВЫПАДЕНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawProbabilities()
    {
        _showProbabilities = EditorGUILayout.BeginFoldoutHeaderGroup(_showProbabilities, "📊 Итоговые вероятности выпадения");
        if (!_showProbabilities) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

        float total = _chanceCommon + _chanceRare + _chanceMythic + _chanceLegendary;
        if (total <= 0f) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

        // По каждой редкости считаем вес предметов и вероятности
        foreach (ItemRarity rarity in System.Enum.GetValues(typeof(ItemRarity)))
        {
            float rarityChance = GetRarityChance(rarity) / total;
            if (rarityChance <= 0f) continue;

            var itemsOfRarity = _items.FindAll(i => i != null && i.rarity == rarity);
            if (itemsOfRarity.Count == 0) continue;

            float totalWeight = 0f;
            foreach (var i in itemsOfRarity) totalWeight += i.dropWeight;

            EditorGUILayout.LabelField($"── {rarity} ({rarityChance * 100f:F1}%) ──", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            foreach (var item in itemsOfRarity)
            {
                float itemChance = totalWeight > 0f
                    ? (item.dropWeight / totalWeight) * rarityChance * 100f
                    : 0f;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.displayName, GUILayout.Width(180));
                    EditorGUILayout.LabelField($"{itemChance:F2}%", GUILayout.Width(60));
                    var barRect = EditorGUILayout.GetControlRect(false, 12, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawRect(barRect, new Color(0.15f, 0.15f, 0.15f));
                    EditorGUI.DrawRect(new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(itemChance / 100f), barRect.height),
                        GetEditorRarityColor(rarity) * 0.8f);
                }
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        EditorGUILayout.EndFoldoutHeaderGroup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВСПОМОГАТЕЛЬНЫЕ
    // ─────────────────────────────────────────────────────────────────────────

    private void RefreshItems()
    {
        _items.Clear();
        string[] guids = AssetDatabase.FindAssets("t:ItemDefinition");
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(path);
            if (item != null) _items.Add(item);
        }
        _items.Sort((a, b) => {
            int rc = a.rarity.CompareTo(b.rarity);
            return rc != 0 ? rc : string.Compare(a.displayName, b.displayName, System.StringComparison.Ordinal);
        });
        Repaint();
    }

    private float GetRarityChance(ItemRarity rarity)
    {
        switch (rarity)
        {
            case ItemRarity.Common:    return _chanceCommon;
            case ItemRarity.Rare:      return _chanceRare;
            case ItemRarity.Mythic:    return _chanceMythic;
            case ItemRarity.Legendary: return _chanceLegendary;
            default:                   return 0f;
        }
    }

    private Color GetEditorRarityColor(ItemRarity rarity)
    {
        switch (rarity)
        {
            case ItemRarity.Common:    return new Color(0.75f, 0.75f, 0.75f);
            case ItemRarity.Rare:      return new Color(0.4f,  0.7f,  1.0f);
            case ItemRarity.Mythic:    return new Color(0.8f,  0.4f,  1.0f);
            case ItemRarity.Legendary: return new Color(1.0f,  0.8f,  0.2f);
            default:                   return Color.white;
        }
    }

    private string BuildEffectsSummary(ItemDefinition def)
    {
        if (def.effects == null || def.effects.Length == 0) return "—";
        var parts = new List<string>();
        foreach (var e in def.effects)
        {
            switch (e.type)
            {
                case ItemEffectType.DamageBonus:       parts.Add($"+{e.value}% урон/сундук"); break;
                case ItemEffectType.AttackSpeedBonus:  parts.Add($"+{e.value}% скорость атаки"); break;
                case ItemEffectType.HealthRegeneration:parts.Add($"+{e.value} ед. регена"); break;
                case ItemEffectType.ExtraExpOrbChance: parts.Add($"+{e.value}% доп.орб"); break;
                case ItemEffectType.VampirismOnHit:    parts.Add($"{e.value}% вампир."); break;
                case ItemEffectType.DamagePerKill:     parts.Add($"+{e.value}% за убийство"); break;
                case ItemEffectType.ExpOrbAttraction:  parts.Add("притяж. опыта"); break;
                default: parts.Add(e.type.ToString()); break;
            }
        }
        return string.Join(", ", parts);
    }

    private void InitStyles()
    {
        if (_stylesInitialized) return;
        _headerStyle = new GUIStyle(EditorStyles.largeLabel)
        {
            fontSize  = 16,
            fontStyle = FontStyle.Bold
        };
        _rarityStyle = new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Bold
        };
        _stylesInitialized = true;
    }
}
#endif
