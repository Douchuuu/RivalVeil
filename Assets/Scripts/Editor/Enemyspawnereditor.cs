#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EnemySpawner))]
public class EnemySpawnerEditor : Editor
{
    // ── Фолдауты ─────────────────────────────────────────────────────
    private bool _showPreview = true;
    private bool _showLiveStats = true;
    private bool _showFormulas = false;

    // ── Превью ────────────────────────────────────────────────────────
    private float _previewDifficultyPct = 0f;

    // ── Auto-repaint в Play Mode ──────────────────────────────────────
    private double _lastRepaintTime = 0;
    private const double REPAINT_INTERVAL = 0.25;

    // ── Цвета ─────────────────────────────────────────────────────────
    private static readonly Color COL_HEADER = new Color(0.25f, 0.25f, 0.25f);
    private static readonly Color COL_ROW_A = new Color(0.20f, 0.20f, 0.20f);
    private static readonly Color COL_ROW_B = new Color(0.23f, 0.23f, 0.23f);
    private static readonly Color COL_WARN = new Color(0.8f, 0.4f, 0.1f);
    private static readonly Color COL_DANGER = new Color(0.8f, 0.15f, 0.15f);
    private static readonly Color COL_SAFE = new Color(0.2f, 0.7f, 0.3f);
    private static readonly Color COL_LIVE_BG = new Color(0.12f, 0.20f, 0.12f);

    // ── Стили ─────────────────────────────────────────────────────────
    private GUIStyle _headerStyle;
    private GUIStyle _cellStyle;
    private GUIStyle _cellBoldStyle;
    private GUIStyle _liveValueStyle;
    private bool _stylesInit = false;

    // ─────────────────────────────────────────────────────────────────
    // NULL-SAFE PROPERTY HELPERS
    // ─────────────────────────────────────────────────────────────────

    private static float SafeFloat(SerializedObject so, string name, float fallback = 0f)
    {
        var prop = so.FindProperty(name);
        return prop != null ? prop.floatValue : fallback;
    }

    private static int SafeInt(SerializedObject so, string name, int fallback = 0)
    {
        var prop = so.FindProperty(name);
        return prop != null ? prop.intValue : fallback;
    }

    // ─────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────

    void OnEnable() => EditorApplication.update += OnEditorUpdate;
    void OnDisable() => EditorApplication.update -= OnEditorUpdate;

    void OnEditorUpdate()
    {
        if (!Application.isPlaying) return;
        if (EditorApplication.timeSinceStartup - _lastRepaintTime > REPAINT_INTERVAL)
        {
            _lastRepaintTime = EditorApplication.timeSinceStartup;
            Repaint();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // СТИЛИ
    // ─────────────────────────────────────────────────────────────────

    void InitStyles()
    {
        if (_stylesInit) return;
        _stylesInit = true;

        _headerStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _cellStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.9f, 0.9f, 0.9f) }
        };

        _cellBoldStyle = new GUIStyle(_cellStyle)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };

        _liveValueStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = new Color(0.4f, 1f, 0.4f) }
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // MAIN DRAW
    // ─────────────────────────────────────────────────────────────────

    public override void OnInspectorGUI()
    {
        InitStyles();
        DrawDefaultInspector();

        EnemySpawner spawner = (EnemySpawner)target;

        EditorGUILayout.Space(8);
        DrawDivider();

        if (Application.isPlaying)
        {
            DrawLiveStats(spawner);
            EditorGUILayout.Space(4);
        }

        DrawPreviewSection(spawner);
        DrawFormulasSection();
    }

    // ─────────────────────────────────────────────────────────────────
    // LIVE STATS
    // ─────────────────────────────────────────────────────────────────

    void DrawLiveStats(EnemySpawner spawner)
    {
        _showLiveStats = DrawFoldout(_showLiveStats, "▶  Live Stats  (Play Mode)", COL_LIVE_BG);
        if (!_showLiveStats) return;

        EditorGUILayout.Space(4);

        float hp = spawner.GetCurrentDifficultyMultiplier();
        float diffP = spawner.GetPlayerDifficultyPercent();
        float dmg = GetPrivateFloat(spawner, "_currentDamageMultiplier");
        float spd = GetPrivateFloat(spawner, "_currentSpeedMultiplier");
        bool final = GetPrivateBool(spawner, "_isFinalSwarm");
        bool swarm = GetPrivateBool(spawner, "_isSwarmActive");

        EnemyPool pool = FindAnyObjectByType<EnemyPool>();
        int activeMobs = pool != null ? pool.ActiveEnemies : 0;
        int totalSlots = pool != null ? pool.ActiveEnemies + pool.PooledEnemies : 0;

        string modeStr = final ? "⚠️  ФИНАЛЬНЫЙ РОЙ" : (swarm ? "⚔️  РОЙ" : "✅  Обычный спавн");
        Color modeBg = final ? COL_DANGER : (swarm ? COL_WARN : COL_SAFE);
        DrawColoredBox(modeStr, modeBg, FontStyle.Bold);

        EditorGUILayout.Space(4);

        DrawLiveRow("HP×", $"{hp:F2}", MultiplierColor(hp, 2f, 4f));
        DrawLiveRow("DMG×", $"{dmg:F2}", MultiplierColor(dmg, 1.5f, 3f));
        DrawLiveRow("SPD×", $"{spd:F2}", MultiplierColor(spd, 1.5f, 2.5f));
        DrawLiveRow("Сложность %", $"{diffP:F0}%", Color.white);
        DrawLiveRow("Активных мобов", pool != null ? $"{activeMobs} / {totalSlots}" : "—", Color.white);

        // Активный SpawnTimeCap
        var activeCap = spawner.GetActiveTimeCap();
        if (activeCap != null)
            DrawLiveRow("Тайм-кап", $"макс {activeCap.maxSpawnPerTick} моб/тик  |  мин.интервал {activeCap.minInterval:F1}с", new Color(1f, 0.85f, 0.3f));
        else
            DrawLiveRow("Тайм-кап", "не задан", new Color(0.5f, 0.5f, 0.5f));

        EditorGUILayout.Space(4);
    }

    void DrawLiveRow(string label, string value, Color valueColor)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel, GUILayout.Width(150));
            var st = new GUIStyle(_liveValueStyle) { normal = { textColor = valueColor } };
            EditorGUILayout.LabelField(value, st);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // ПРЕВЬЮ СЛОЖНОСТИ
    // ─────────────────────────────────────────────────────────────────

    void DrawPreviewSection(EnemySpawner spawner)
    {
        _showPreview = DrawFoldout(_showPreview, "📊  Превью сложности по времени", COL_HEADER);
        if (!_showPreview) return;

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Симуляция Enemy Difficulty Bonus %", GUILayout.Width(250));
        _previewDifficultyPct = EditorGUILayout.Slider(_previewDifficultyPct, 0f, 500f);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        SerializedObject so = new SerializedObject(spawner);

        bool missingNew = so.FindProperty("spawnCountPerMinute") == null
                       || so.FindProperty("baseSpawnPerTick") == null
                       || so.FindProperty("spawnCapDifficultyPct") == null;
        if (missingNew)
        {
            EditorGUILayout.HelpBox(
                "⚠️ Поля spawnCountPerMinute / baseSpawnPerTick не найдены.\n" +
                "Примени обновлённый EnemySpawner.cs в проект и перекомпилируй.",
                MessageType.Warning);
        }

        float stageMin = SafeFloat(so, "stageTimeMinutes", 10f);
        float hpPerMin = SafeFloat(so, "hpMultiplierPerMinute", 0.15f);
        float dmgPerMin = SafeFloat(so, "damageMultiplierPerMinute", 0.1f);
        float spdPerMin = SafeFloat(so, "speedMultiplierPerMinute", 0.05f);
        float diffHp = SafeFloat(so, "difficultyHpBonus", 1f);
        float diffDmg = SafeFloat(so, "difficultyDamageBonus", 0.5f);
        float diffSpd = SafeFloat(so, "difficultySpeedBonus", 0.3f);
        int spawnBonus = SafeInt(so, "spawnBonusPerHundredPct", 4);
        int baseSpawn = SafeInt(so, "baseSpawnPerTick", 1);
        float spawnPerMin = SafeFloat(so, "spawnCountPerMinute", 0f);
        float spawnCapPct = SafeFloat(so, "spawnCapDifficultyPct", 500f);
        int maxPerTick = SafeInt(so, "maxSpawnPerTick", 10);
        float intervalBonus = SafeFloat(so, "intervalSpeedBonus", 0.5f);
        float baseInterval = SafeFloat(so, "timeBetweenSpawns", 2f);

        // Читаем список тайм-капов из SpawnTimeCaps
        var capsListProp = so.FindProperty("spawnTimeCaps");
        var timeCaps = new List<(float fromMin, int maxTick, float minInt)>();
        if (capsListProp != null)
        {
            for (int ci = 0; ci < capsListProp.arraySize; ci++)
            {
                var elem = capsListProp.GetArrayElementAtIndex(ci);
                float fm = elem.FindPropertyRelative("fromMinute")?.floatValue ?? 0f;
                int mt = elem.FindPropertyRelative("maxSpawnPerTick")?.intValue ?? 999;
                float mi = elem.FindPropertyRelative("minInterval")?.floatValue ?? 0.05f;
                timeCaps.Add((fm, mt, mi));
            }
            timeCaps.Sort((a, b) => a.fromMin.CompareTo(b.fromMin));
        }

        float ratio = _previewDifficultyPct / 100f;

        DrawTableHeader();

        int stageMinInt = Mathf.CeilToInt(stageMin);
        for (int m = 0; m <= stageMinInt; m++)
        {
            // Находим активный кап для этой минуты
            (float fromMin, int maxTick, float minInt) activeCap = (-1, int.MaxValue, 0.05f);
            foreach (var c in timeCaps)
                if (m >= c.fromMin) activeCap = c;
            bool hasCap = activeCap.fromMin >= 0;

            Color rowBg = m % 2 == 0 ? COL_ROW_A : COL_ROW_B;
            DrawPreviewRow(m, false, stageMin,
                hpPerMin, dmgPerMin, spdPerMin,
                diffHp, diffDmg, diffSpd,
                baseSpawn, spawnPerMin, spawnBonus, maxPerTick,
                intervalBonus, baseInterval, ratio, spawnCapPct,
                hasCap ? activeCap.maxTick : int.MaxValue,
                hasCap ? activeCap.minInt : 0.05f,
                rowBg);
        }

        // Строка финального роя
        DrawPreviewRow(stageMinInt + 2, true, stageMin,
            hpPerMin, dmgPerMin, spdPerMin,
            diffHp, diffDmg, diffSpd,
            baseSpawn, spawnPerMin, spawnBonus, maxPerTick,
            intervalBonus, baseInterval, ratio, spawnCapPct,
            int.MaxValue, 0.05f,
            COL_DANGER);

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        DrawColorPill("низко", COL_SAFE);
        DrawColorPill("средне", COL_WARN);
        DrawColorPill("высоко", COL_DANGER);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);
    }

    void DrawTableHeader()
    {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, COL_HEADER);
        float[] w = ColWidths(r.width);
        float x = r.x;
        string[] headers = { "Мин", "HP×", "DMG×", "SPD×", "Моб/тик", "Инт (с)" };
        for (int i = 0; i < headers.Length; i++)
        {
            GUI.Label(new Rect(x, r.y, w[i], r.height), headers[i], _headerStyle);
            x += w[i];
        }
    }

    void DrawPreviewRow(float min, bool isFinal, float stageMin,
                        float hpPerMin, float dmgPerMin, float spdPerMin,
                        float diffHp, float diffDmg, float diffSpd,
                        int baseSpawn, float spawnPerMin, int spawnBonus, int maxPerTick,
                        float intervalBonus, float baseInterval,
                        float ratio, float spawnCapPct,
                        int capMaxTick, float capMinInterval,
                        Color rowBg)
    {
        float timeDiffMult, timeDmgMult, timeSpeedMult;

        if (isFinal)
        {
            float over = Mathf.Max(0, (min - stageMin) * 60f);
            timeDiffMult = Mathf.Clamp(2.5f + over * 0.01f, 2.5f, 10f);
            timeDmgMult = timeDiffMult;
            timeSpeedMult = Mathf.Clamp(1f + stageMin * spdPerMin, 1f, 2.5f);
        }
        else
        {
            timeDiffMult = 1f + min * hpPerMin;
            timeDmgMult = 1f + min * dmgPerMin;
            timeSpeedMult = Mathf.Clamp(1f + min * spdPerMin, 1f, 2.5f);
        }

        float hp = timeDiffMult * (1f + ratio * diffHp);
        float dmg = timeDmgMult * (1f + ratio * diffDmg);
        float spd = Mathf.Clamp(timeSpeedMult * (1f + ratio * diffSpd), 1f, 3f);

        float effectiveMin = isFinal ? stageMin : min;
        float cappedRatio = Mathf.Min(ratio, spawnCapPct / 100f);
        int mobs = Mathf.Clamp(
            baseSpawn
            + Mathf.FloorToInt(effectiveMin * spawnPerMin)
            + Mathf.FloorToInt(cappedRatio * spawnBonus),
            baseSpawn, maxPerTick);

        // Применяем тайм-кап к мобам
        bool mobCapped = mobs > capMaxTick;
        mobs = Mathf.Min(mobs, capMaxTick);

        float progress = isFinal ? 1f : Mathf.Clamp01(min / Mathf.Max(stageMin, 1f));
        float timeScale = isFinal ? 0.3f : Mathf.Lerp(1f, 0.3f, progress);
        float interval = Mathf.Max(
            baseInterval * Mathf.Clamp(timeScale, 0.3f, 1f)
            / Mathf.Max(1f + ratio * intervalBonus, 0.01f),
            0.05f);

        // Применяем тайм-кап к интервалу (минимальный интервал)
        bool intCapped = !isFinal && interval < capMinInterval;
        if (!isFinal) interval = Mathf.Max(interval, capMinInterval);

        Rect r = EditorGUILayout.GetControlRect(false, 20);
        EditorGUI.DrawRect(r, rowBg);
        float[] w = ColWidths(r.width);
        float x = r.x;

        string minLabel = isFinal ? "РОЙ" : $"{min:F0}";
        GUI.Label(new Rect(x, r.y, w[0], r.height), minLabel, _cellBoldStyle); x += w[0];

        DrawColoredCell(new Rect(x, r.y, w[1], r.height), $"{hp:F2}×", MultiplierColor(hp, 2f, 4f)); x += w[1];
        DrawColoredCell(new Rect(x, r.y, w[2], r.height), $"{dmg:F2}×", MultiplierColor(dmg, 1.5f, 3f)); x += w[2];
        DrawColoredCell(new Rect(x, r.y, w[3], r.height), $"{spd:F2}×", MultiplierColor(spd, 1.5f, 2.5f)); x += w[3];

        // Моб/тик — жёлтый если зажат капом
        Color mobColor = mobCapped ? new Color(1f, 0.85f, 0.2f) : MultiplierColor(mobs, 3f, 7f);
        string mobStr = mobCapped ? $"{mobs} ⌛" : $"{mobs}";
        DrawColoredCell(new Rect(x, r.y, w[4], r.height), mobStr, mobColor); x += w[4];

        // Интервал — жёлтый если зажат капом
        Color intColor = intCapped ? new Color(1f, 0.85f, 0.2f) : Color.white;
        string intStr = intCapped ? $"{interval:F2} ⌛" : $"{interval:F2}";
        DrawColoredCell(new Rect(x, r.y, w[5], r.height), intStr, intColor);
    }

    // ─────────────────────────────────────────────────────────────────
    // ФОРМУЛЫ
    // ─────────────────────────────────────────────────────────────────

    void DrawFormulasSection()
    {
        _showFormulas = DrawFoldout(_showFormulas, "📐  Формулы расчёта", COL_HEADER);
        if (!_showFormulas) return;

        var st = new GUIStyle(EditorStyles.helpBox)
        {
            fontSize = 11,
            richText = true,
            wordWrap = true,
            padding = new RectOffset(10, 10, 8, 8)
        };

        EditorGUILayout.LabelField(
            "<b>HP×</b>   = (1 + мин × hpPerMin) × (1 + ratio × diffHp)\n" +
            "<b>DMG×</b>  = (1 + мин × dmgPerMin) × (1 + ratio × diffDmg)\n" +
            "<b>SPD×</b>  = Clamp((1 + мин × spdPerMin) × (1 + ratio × diffSpd),  1, 3)\n\n" +
            "<b>Моб/тик</b>  = Clamp(base + Floor(мин × spawnPerMin) + Floor(min(ratio, cap) × spawnBonus),  base, max)\n" +
            "<b>Кеп спавна</b>: ratio капается на spawnCapPct — выше растут только статы врагов\n" +
            "<b>Интервал</b> = base × timeScale / (1 + ratio × intervalBonus)\n" +
            "   timeScale: 1.0 → 0.3 линейно за этап, 0.3 в финальном рое\n\n" +
            "<b>Финал HP×</b> = Clamp(2.5 + (t – конец_этапа) × 0.01,  2.5, 10)",
            st);

        EditorGUILayout.Space(4);
    }

    // ─────────────────────────────────────────────────────────────────
    // УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────

    float[] ColWidths(float total)
    {
        float[] pct = { 0.12f, 0.18f, 0.18f, 0.18f, 0.18f, 0.16f };
        float[] res = new float[pct.Length];
        for (int i = 0; i < pct.Length; i++) res[i] = total * pct[i];
        return res;
    }

    bool DrawFoldout(bool state, string label, Color bg)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, bg);
        r.xMin += 4;
        return EditorGUI.Foldout(r, state, label, true, EditorStyles.foldoutHeader);
    }

    void DrawColoredBox(string text, Color bg, FontStyle fs = FontStyle.Normal)
    {
        Rect r = EditorGUILayout.GetControlRect(false, 22);
        EditorGUI.DrawRect(r, bg);
        var st = new GUIStyle(EditorStyles.boldLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = fs,
            normal = { textColor = Color.white }
        };
        GUI.Label(r, text, st);
    }

    void DrawColoredCell(Rect r, string text, Color textColor)
    {
        var st = new GUIStyle(_cellStyle) { normal = { textColor = textColor } };
        GUI.Label(r, text, st);
    }

    void DrawColorPill(string label, Color color)
    {
        var st = new GUIStyle(EditorStyles.miniLabel)
        {
            normal = { textColor = color },
            fontStyle = FontStyle.Bold,
            padding = new RectOffset(4, 4, 1, 1)
        };
        GUILayout.Label($"■ {label}", st, GUILayout.ExpandWidth(false));
        GUILayout.Space(8);
    }

    void DrawDivider()
    {
        Rect r = EditorGUILayout.GetControlRect(false, 2);
        EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.4f));
        EditorGUILayout.Space(2);
    }

    Color MultiplierColor(float v, float warn, float danger)
    {
        if (v >= danger) return COL_DANGER;
        if (v >= warn) return COL_WARN;
        return COL_SAFE;
    }

    // ─────────────────────────────────────────────────────────────────
    // РЕФЛЕКСИЯ — приватные поля для Live Stats
    // ─────────────────────────────────────────────────────────────────

    float GetPrivateFloat(object obj, string fieldName)
    {
        var f = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return f != null ? (float)f.GetValue(obj) : 0f;
    }

    bool GetPrivateBool(object obj, string fieldName)
    {
        var f = obj.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return f != null && (bool)f.GetValue(obj);
    }
}
#endif