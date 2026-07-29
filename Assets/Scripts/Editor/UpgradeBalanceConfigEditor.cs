#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

[CustomEditor(typeof(UpgradeBalanceConfig))]
public class UpgradeBalanceConfigEditor : Editor
{
    private bool _showChances = true;
    private bool _showGlobal  = true;
    private bool _showTotems  = true;  // ← ДОБАВЛЕНО
    private bool _showWeapons = true;

    private Dictionary<int, bool> _weaponFoldouts = new Dictionary<int, bool>();

    // ── Цвета ────────────────────────────────────────────────────────────────
    private static readonly Color COL_HEADER    = new Color(0.18f, 0.18f, 0.18f);
    private static readonly Color COL_TOTEM_HDR = new Color(0.16f, 0.12f, 0.22f); // фиолетовый заголовок тотемов
    private static readonly Color COL_ROW_A     = new Color(0.16f, 0.16f, 0.16f);
    private static readonly Color COL_ROW_B     = new Color(0.20f, 0.20f, 0.20f);
    private static readonly Color COL_TOTEM_A   = new Color(0.16f, 0.14f, 0.20f); // тёмно-фиолетовый
    private static readonly Color COL_TOTEM_B   = new Color(0.20f, 0.17f, 0.25f);
    private static readonly Color COL_OVERRIDE  = new Color(0.14f, 0.22f, 0.14f);
    private static readonly Color COL_DISABLED  = new Color(0.24f, 0.14f, 0.14f);
    private static readonly Color COL_COMMON    = new Color(0.65f, 0.65f, 0.65f);
    private static readonly Color COL_RARE      = new Color(0.26f, 0.60f, 1.00f);
    private static readonly Color COL_MYTHIC    = new Color(0.67f, 0.27f, 1.00f);
    private static readonly Color COL_LEGENDARY = new Color(1.00f, 0.72f, 0.00f);

    // ── Стили ────────────────────────────────────────────────────────────────
    private GUIStyle _headerStyle;
    private GUIStyle _cellStyle;
    private GUIStyle _cellBoldStyle;
    private GUIStyle _overrideStyle;
    private GUIStyle _disabledStyle;
    private GUIStyle _rangeStyle;
    private bool     _stylesInit;

    void InitStyles()
    {
        if (_stylesInit) return;
        _stylesInit = true;

        _headerStyle = new GUIStyle(EditorStyles.label)
        {
            alignment  = TextAnchor.MiddleCenter,
            fontStyle  = FontStyle.Bold,
            normal     = { textColor = Color.white }
        };
        _cellStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            normal    = { textColor = new Color(0.9f, 0.9f, 0.9f) }
        };
        _cellBoldStyle = new GUIStyle(_cellStyle) { fontStyle = FontStyle.Bold };
        _overrideStyle = new GUIStyle(_cellStyle)
        {
            normal    = { textColor = new Color(0.4f, 1f, 0.5f) },
            fontStyle = FontStyle.Bold
        };
        _disabledStyle = new GUIStyle(_cellStyle)
        {
            normal    = { textColor = new Color(0.6f, 0.3f, 0.3f) },
            fontStyle = FontStyle.Italic
        };
        _rangeStyle = new GUIStyle(_cellStyle)
        {
            fontSize = 10,
            normal   = { textColor = new Color(0.75f, 0.75f, 0.75f) }
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MAIN
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnInspectorGUI()
    {
        InitStyles();
        UpgradeBalanceConfig cfg = (UpgradeBalanceConfig)target;

        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        DrawDivider();

        DrawRarityChances(cfg);
        EditorGUILayout.Space(6);
        DrawGlobalTable(cfg);
        EditorGUILayout.Space(6);
        DrawTotemGlobalTable(cfg);   // ← ДОБАВЛЕНО
        EditorGUILayout.Space(6);
        DrawWeaponOverrideTables(cfg);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ШАНСЫ ВЫПАДЕНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void DrawRarityChances(UpgradeBalanceConfig cfg)
    {
        _showChances = DrawFoldout(_showChances, "🎲  Шансы выпадения редкостей", COL_HEADER);
        if (!_showChances) return;
        EditorGUILayout.Space(4);

        int total = cfg.commonWeight + cfg.rareWeight + cfg.mythicWeight + cfg.legendaryWeight;
        if (total != 100)
            EditorGUILayout.HelpBox($"⚠️ Сумма весов = {total}, должна быть 100!", MessageType.Warning);

        Rect barRect = EditorGUILayout.GetControlRect(false, 24);
        float bx = 0;
        void DrawBar(int w, Color c, string label)
        {
            if (w <= 0 || total <= 0) return;
            float bw = barRect.width * w / total;
            var r = new Rect(barRect.x + bx, barRect.y, bw, barRect.height);
            EditorGUI.DrawRect(r, c);
            if (bw > 40f)
            {
                var st = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    normal    = { textColor = Color.black }
                };
                GUI.Label(r, label, st);
            }
            bx += bw;
        }
        DrawBar(cfg.commonWeight,    COL_COMMON,    $"Common {cfg.commonWeight}%");
        DrawBar(cfg.rareWeight,      COL_RARE,      $"Rare {cfg.rareWeight}%");
        DrawBar(cfg.mythicWeight,    COL_MYTHIC,    $"Mythic {cfg.mythicWeight}%");
        DrawBar(cfg.legendaryWeight, COL_LEGENDARY, $"Legendary {cfg.legendaryWeight}%");

        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        DrawPill("Common",    COL_COMMON);
        DrawPill("Rare",      COL_RARE);
        DrawPill("Mythic",    COL_MYTHIC);
        DrawPill("Legendary", COL_LEGENDARY);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ГЛОБАЛЬНАЯ ТАБЛИЦА ОРУЖИЙ
    // ─────────────────────────────────────────────────────────────────────────

    void DrawGlobalTable(UpgradeBalanceConfig cfg)
    {
        _showGlobal = DrawFoldout(_showGlobal, "🌐  Глобальные диапазоны (для всех оружий)", COL_HEADER);
        if (!_showGlobal) return;
        EditorGUILayout.Space(4);

        if (cfg.statRanges == null || cfg.statRanges.Count == 0)
        {
            EditorGUILayout.HelpBox("Список statRanges пуст.", MessageType.Info);
            return;
        }

        DrawRangeTableHeader("Стат");
        for (int i = 0; i < cfg.statRanges.Count; i++)
        {
            var entry = cfg.statRanges[i];
            DrawRangeRow(entry.statType, entry.unit,
                entry.common, entry.rare, entry.mythic, entry.legendary,
                i % 2 == 0 ? COL_ROW_A : COL_ROW_B,
                isDisabled: false, isOverride: false);
        }
        EditorGUILayout.Space(4);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ГЛОБАЛЬНАЯ ТАБЛИЦА ТОТЕМОВ ← НОВАЯ СЕКЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void DrawTotemGlobalTable(UpgradeBalanceConfig cfg)
    {
        _showTotems = DrawFoldout(_showTotems, "🗿  Глобальные диапазоны (тотемы)", COL_TOTEM_HDR);
        if (!_showTotems) return;
        EditorGUILayout.Space(4);

        if (cfg.totemRanges == null || cfg.totemRanges.Count == 0)
        {
            EditorGUILayout.HelpBox("Список totemRanges пуст.", MessageType.Info);
            return;
        }

        // Шанс карточки тотема
        EditorGUILayout.BeginHorizontal();
        var pillSt = new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Bold,
            normal    = { textColor = new Color(0.7f, 0.5f, 1.0f) }
        };
        GUILayout.Label($"🗿  Шанс карточки тотема: {cfg.totemCardChance * 100f:F0}%  " +
                        $"(≈ {(cfg.totemCardChance > 0 ? 1f / cfg.totemCardChance : 0):F1} " +
                        $"тотемов из 3 карточек)", pillSt);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);

        DrawTotemTableHeader();
        for (int i = 0; i < cfg.totemRanges.Count; i++)
        {
            var entry = cfg.totemRanges[i];
            DrawTotemRangeRow(
                entry.bonusType,
                entry.common, entry.rare, entry.mythic, entry.legendary,
                i % 2 == 0 ? COL_TOTEM_A : COL_TOTEM_B);
        }
        EditorGUILayout.Space(4);
    }

    void DrawTotemTableHeader()
    {
        Rect r = EditorGUILayout.GetControlRect(false, 26);
        EditorGUI.DrawRect(r, COL_TOTEM_HDR);
        float[] w = RangeColWidths(r.width);
        float x = r.x;

        GUI.Label(new Rect(x, r.y, w[0], r.height), "Тотем (бонус)", _headerStyle);
        x += w[0];

        string[] rarityNames  = { "Common",    "Rare",    "Mythic",    "Legendary" };
        Color[]  rarityColors = { COL_COMMON, COL_RARE, COL_MYTHIC, COL_LEGENDARY };

        for (int i = 0; i < 4; i++)
        {
            var cr = new Rect(x, r.y, w[i + 1], r.height);
            EditorGUI.DrawRect(cr, Darken(rarityColors[i], 0.35f));
            var st = new GUIStyle(_headerStyle) { normal = { textColor = rarityColors[i] } };
            GUI.Label(cr, $"{rarityNames[i]}\nmin  –  max", st);
            x += w[i + 1];
        }
    }

    void DrawTotemRangeRow(TotemBonusType bonus,
        RarityRange common, RarityRange rare, RarityRange mythic, RarityRange legendary,
        Color rowBg)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, rowBg);
        float[] w = RangeColWidths(r.width);
        float x = r.x;

        bool isFlat = (bonus == TotemBonusType.Armor); // единственный flat-бонус
        string unit = isFlat ? "" : "%";

        var labelSt = new GUIStyle(_cellBoldStyle)
        {
            alignment = TextAnchor.MiddleLeft,
            normal    = { textColor = new Color(0.85f, 0.75f, 1.0f) }
        };
        labelSt.padding.left = 4;

        string armorNote = isFlat ? "  <color=#888888><size=9>flat</size></color>" : "";
        var labelRich = new GUIStyle(labelSt) { richText = true };
        GUI.Label(new Rect(x, r.y, w[0], r.height), TotemLabel(bonus) + armorNote, labelRich);
        x += w[0];

        RarityRange[] ranges = { common, rare, mythic, legendary };
        Color[]       colors = { COL_COMMON, COL_RARE, COL_MYTHIC, COL_LEGENDARY };

        for (int i = 0; i < 4; i++)
        {
            var rr = ranges[i];
            string valStr = rr != null
                ? $"+{rr.min:F0}{unit} – +{rr.max:F0}{unit}"
                : "—";
            var st = new GUIStyle(_rangeStyle) { normal = { textColor = colors[i] } };
            GUI.Label(new Rect(x, r.y, w[i + 1], r.height), valStr, st);
            x += w[i + 1];
        }
    }

    string TotemLabel(TotemBonusType bonus)
    {
        switch (bonus)
        {
            case TotemBonusType.XpGain:          return "Опыт";
            case TotemBonusType.MoveSpeed:       return "Скорость";
            case TotemBonusType.MaxHealth:       return "Макс. HP";
            case TotemBonusType.Damage:          return "Урон";
            case TotemBonusType.CritChance:      return "Шанс крита";
            case TotemBonusType.PickupRange:     return "Радиус подбора";
            case TotemBonusType.Armor:           return "🔷 Щит (броня)";
            case TotemBonusType.DamageReduction: return "Снижение урона";
            default:                             return bonus.ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ТАБЛИЦЫ ПО ОРУЖИЯМ
    // ─────────────────────────────────────────────────────────────────────────

    void DrawWeaponOverrideTables(UpgradeBalanceConfig cfg)
    {
        _showWeapons = DrawFoldout(_showWeapons, "⚔️  Настройки по оружиям (переопределения)", COL_HEADER);
        if (!_showWeapons) return;
        EditorGUILayout.Space(4);

        if (cfg.weaponOverrides == null || cfg.weaponOverrides.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Нет переопределений. Добавь запись в 'Weapon Overrides' для каждого оружия " +
                "у которого нужны свои диапазоны или отключённые статы.\n" +
                "weaponId должен совпадать с полем weaponId в WeaponDefinition.",
                MessageType.Info);
            return;
        }

        for (int wi = 0; wi < cfg.weaponOverrides.Count; wi++)
        {
            var ov = cfg.weaponOverrides[wi];
            if (string.IsNullOrEmpty(ov.weaponId)) continue;

            string label = string.IsNullOrEmpty(ov.displayName)
                ? ov.weaponId
                : $"{ov.displayName}  [{ov.weaponId}]";

            if (!_weaponFoldouts.ContainsKey(wi)) _weaponFoldouts[wi] = true;
            _weaponFoldouts[wi] = DrawFoldout(_weaponFoldouts[wi],
                $"  🔹 {label}", new Color(0.18f, 0.26f, 0.18f));
            if (!_weaponFoldouts[wi]) continue;

            EditorGUILayout.Space(2);
            DrawRangeTableHeader(label);

            if (ov.statOverrides == null || ov.statOverrides.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Нет переопределений для этого оружия. " +
                    "Добавь статы в 'Stat Overrides' выше.", MessageType.Info);
                EditorGUILayout.Space(4);
                continue;
            }

            int row = 0;
            foreach (var se in ov.statOverrides)
            {
                string unit = cfg.GetUnit(se.statType);

                if (se.disabled)
                {
                    DrawRangeRow(se.statType, unit,
                        null, null, null, null,
                        COL_DISABLED,
                        isDisabled: true, isOverride: false);
                }
                else if (se.overrideRanges)
                {
                    DrawRangeRow(se.statType, unit,
                        se.common, se.rare, se.mythic, se.legendary,
                        COL_OVERRIDE,
                        isDisabled: false, isOverride: true);
                }
                else
                {
                    var ge = cfg.statRanges.Find(x => x.statType == se.statType);
                    if (ge != null)
                        DrawRangeRow(se.statType, unit,
                            ge.common, ge.rare, ge.mythic, ge.legendary,
                            row % 2 == 0 ? COL_ROW_A : COL_ROW_B,
                            isDisabled: false, isOverride: false, badge: "global");
                }
                row++;
            }

            EditorGUILayout.Space(4);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РЕНДЕР ТАБЛИЦЫ С ДИАПАЗОНАМИ (ОРУЖИЯ)
    // ─────────────────────────────────────────────────────────────────────────

    float[] RangeColWidths(float total)
    {
        float[] pct = { 0.24f, 0.19f, 0.19f, 0.19f, 0.19f };
        float[] res = new float[pct.Length];
        for (int i = 0; i < pct.Length; i++) res[i] = total * pct[i];
        return res;
    }

    void DrawRangeTableHeader(string statColLabel)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 26);
        EditorGUI.DrawRect(r, COL_HEADER);
        float[] w = RangeColWidths(r.width);
        float x = r.x;

        GUI.Label(new Rect(x, r.y, w[0], r.height), statColLabel, _headerStyle);
        x += w[0];

        string[] rarityNames  = { "Common",    "Rare",    "Mythic",    "Legendary" };
        Color[]  rarityColors = { COL_COMMON, COL_RARE, COL_MYTHIC, COL_LEGENDARY };

        for (int i = 0; i < 4; i++)
        {
            var cr = new Rect(x, r.y, w[i + 1], r.height);
            EditorGUI.DrawRect(cr, Darken(rarityColors[i], 0.38f));
            var st = new GUIStyle(_headerStyle) { normal = { textColor = rarityColors[i] } };
            GUI.Label(cr, $"{rarityNames[i]}\nmin  –  max", st);
            x += w[i + 1];
        }
    }

    void DrawRangeRow(StatBonusType stat, string unit,
        RarityRange common, RarityRange rare, RarityRange mythic, RarityRange legendary,
        Color rowBg, bool isDisabled, bool isOverride, string badge = null)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, rowBg);
        float[] w = RangeColWidths(r.width);
        float x = r.x;

        string badgeStr = isDisabled ? "  <color=#ff6666><size=9>✕ disabled</size></color>"
                        : isOverride ? "  <color=#44ff66><size=9>↑ override</size></color>"
                        : badge != null ? $"  <color=#888888><size=9>{badge}</size></color>"
                        : "";
        var labelSt = new GUIStyle(_cellBoldStyle) { richText = true, alignment = TextAnchor.MiddleLeft };
        labelSt.padding.left = 4;
        GUI.Label(new Rect(x, r.y, w[0], r.height), StatLabel(stat) + badgeStr, labelSt);
        x += w[0];

        if (isDisabled)
        {
            for (int i = 0; i < 4; i++)
            {
                GUI.Label(new Rect(x, r.y, w[i + 1], r.height), "—", _disabledStyle);
                x += w[i + 1];
            }
            return;
        }

        RarityRange[] ranges = { common, rare, mythic, legendary };
        Color[]       colors = { COL_COMMON, COL_RARE, COL_MYTHIC, COL_LEGENDARY };

        for (int i = 0; i < 4; i++)
        {
            var rr = ranges[i];
            string valStr = rr != null ? $"{FormatVal(stat, rr.min, unit)} – {FormatVal(stat, rr.max, unit)}" : "—";
            var st = new GUIStyle(isOverride ? _overrideStyle : _rangeStyle)
            { normal = { textColor = isOverride ? new Color(0.4f, 1f, 0.5f) : colors[i] } };
            GUI.Label(new Rect(x, r.y, w[i + 1], r.height), valStr, st);
            x += w[i + 1];
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────────────

    string StatLabel(StatBonusType stat)
    {
        switch (stat)
        {
            case StatBonusType.Damage:          return "Урон";
            case StatBonusType.AttackSpeed:     return "Скор. атаки";
            case StatBonusType.Radius:          return "Радиус";
            case StatBonusType.ProjectileCount: return "Снаряды";
            case StatBonusType.TickRate:        return "Частота тиков";
            case StatBonusType.CritChance:      return "Шанс крита";
            case StatBonusType.CritMultiplier:  return "Крит множитель";
            case StatBonusType.ProjectileSpeed: return "Скор. снаряда";
            case StatBonusType.Duration:        return "Длительность";
            case StatBonusType.ShieldCharge:    return "Заряды щита";
            case StatBonusType.ShieldRegen:     return "Реген щита";
            default:                            return stat.ToString();
        }
    }

    string FormatVal(StatBonusType stat, float val, string unit)
    {
        if (stat == StatBonusType.CritMultiplier)  return $"+{val:F2}{unit}";
        if (stat == StatBonusType.ProjectileCount) return $"+{val:F1}{unit}";
        if (stat == StatBonusType.ShieldCharge)    return $"+{(int)val}{unit}";
        return $"+{val:F0}{unit}";
    }

    bool DrawFoldout(bool state, string label, Color bg)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, bg);
        r.xMin += 4;
        return EditorGUI.Foldout(r, state, label, true, EditorStyles.foldoutHeader);
    }

    void DrawDivider()
    {
        Rect r = EditorGUILayout.GetControlRect(false, 2);
        EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.3f));
    }

    void DrawPill(string label, Color color)
    {
        var st = new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Bold,
            normal    = { textColor = color },
            padding   = new RectOffset(4, 4, 1, 1)
        };
        GUILayout.Label($"■ {label}", st, GUILayout.ExpandWidth(false));
        GUILayout.Space(6);
    }

    Color Darken(Color c, float t) => new Color(c.r * t, c.g * t, c.b * t, 1f);
}
#endif
