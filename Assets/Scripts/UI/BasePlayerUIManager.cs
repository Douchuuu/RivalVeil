using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// BasePlayerUIManager — общая логика UI для всех игровых режимов.
///
/// ══════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЕ: уровни оружий/тотемов для scene-объектов
/// ══════════════════════════════════════════════════════════════
///
///   БЫЛО (проблема):
///     FindOrCreateLevelText() — создавал TMP программно как дочерний
///     объект к Image слоту. Для статических scene-объектов это
///     ненадёжно: RectTransform может не вписаться в LayoutGroup,
///     Canvas sorting может скрыть текст за иконкой.
///
///   СТАЛО:
///     Два явных [SerializeField] массива: weaponLevelTexts[] и
///     totemLevelTexts[]. Пользователь добавляет TextMeshProUGUI
///     объекты прямо на сцену и назначает их в Inspector.
///     Это стандартный Unity-подход для scene-based UI.
///
///   КАК НАСТРОИТЬ В INSPECTOR:
///     1. Для каждого слота оружия создай дочерний GameObject
///        с TextMeshProUGUI (например "WeaponLevel_0")
///     2. Назначь их в массив weaponLevelTexts[] по порядку
///     3. Аналогично для тотемов — массив totemLevelTexts[]
///     Подробный гайд в guide_levels.docx
///
///   FALLBACK:
///     Если массивы не назначены — код ищет дочерние объекты
///     с именем "LevelText" или создаёт TextMeshProUGUI программно.
///     Но для scene-объектов лучше использовать Inspector-массивы.
///
/// ══════════════════════════════════════════════════════════════
/// </summary>
public abstract class BasePlayerUIManager : MonoBehaviour
{
    // ─── БАЗОВЫЕ UI-ЭЛЕМЕНТЫ ─────────────────────────────────────────────────

    [Header("── HP ─────────────────────────────────────────")]
    [SerializeField] protected Slider          hpSlider;
    [SerializeField] protected TextMeshProUGUI hpText;
    [SerializeField] protected Image           hpFill;

    [Header("── Щит (Armor) ─────────────────────────────────")]
    [SerializeField] protected Slider          shieldSlider;
    [SerializeField] protected TextMeshProUGUI shieldText;
    [SerializeField] protected Image           shieldFill;
    [SerializeField] protected GameObject      shieldBarRoot;

    [Header("── XP ─────────────────────────────────────────")]
    [SerializeField] protected Slider          xpSlider;
    [SerializeField] protected TextMeshProUGUI xpText;
    [SerializeField] protected Image           xpFill;

    [Header("── Уровень и убийства ──────────────────────────")]
    [SerializeField] protected TextMeshProUGUI levelText;
    [SerializeField] protected TextMeshProUGUI killCountText;

    [Header("── Слоты оружий ────────────────────────────────")]
    [SerializeField] protected Transform       weaponSlotContainer;
    [SerializeField] protected TextMeshProUGUI weaponCountText;

    [Header("── Уровни оружий (по одному TMP на каждый слот) ─")]
    [Tooltip("Создай TextMeshProUGUI объекты на сцене рядом с иконками оружий.\n" +
             "Назначь их сюда по порядку: [0] = первый слот, [1] = второй и т.д.\n" +
             "Скрипт будет писать 'Lv1', 'Lv2'... или 'MAX' в эти объекты.")]
    [SerializeField] protected TextMeshProUGUI[] weaponLevelTexts;

    [Header("── Слоты тотемов ────────────────────────────────")]
    [SerializeField] protected Transform       totemSlotContainer;
    [SerializeField] protected TextMeshProUGUI totemCountText;

    [Header("── Уровни тотемов (по одному TMP на каждый слот) ─")]
    [Tooltip("Создай TextMeshProUGUI объекты на сцене рядом с иконками тотемов.\n" +
             "Назначь их сюда по порядку: [0] = первый слот, [1] = второй и т.д.\n" +
             "Скрипт будет писать 'Lv1', 'Lv12'... или 'MAX' в эти объекты.")]
    [SerializeField] protected TextMeshProUGUI[] totemLevelTexts;

    // ─── КЭШ СЛОТОВ ──────────────────────────────────────────────────────────

    private Image[] _cachedSlotImages;

    // Fallback текст уровней — используется ТОЛЬКО если weaponLevelTexts не назначен
    // Создаётся программно один раз при первом обращении
    private TextMeshProUGUI[] _fallbackWeaponLevelTexts;
    private TextMeshProUGUI[] _fallbackTotemLevelTexts;

    private int   _lastWeaponCount  = -1;
    private int   _lastKillCount    = -1;
    private int   _lastTotemCount   = -1;
    private float _lastMaxShield    = -1f;

    // Цвета уровней
    private static readonly Color MAX_LEVEL_COLOR    = new Color(1f, 0.72f, 0f);   // золотой #FFB700
    private static readonly Color NORMAL_LEVEL_COLOR = Color.black;

    // ─────────────────────────────────────────────────────────────────────────
    // ГЛАВНЫЙ МЕТОД
    // ─────────────────────────────────────────────────────────────────────────

    protected void UpdateBaseUI(PlayerStats stats, WeaponManager weaponManager,
                                 TotemManager totemManager = null)
    {
        if (stats == null) return;

        UpdateHealthUI(stats);
        UpdateShieldUI(stats);
        UpdateExperienceUI(stats);
        UpdateLevelUI(stats);
        UpdateKillsUI(stats);
        UpdateWeaponSlotsUI(weaponManager);
        UpdateTotemSlotsUI(totemManager);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HP
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateHealthUI(PlayerStats stats)
    {
        float hp    = stats.CurrentHealth;
        float maxHp = stats.MaxHealth;

        if (hpSlider != null) { hpSlider.maxValue = maxHp; hpSlider.value = hp; }
        if (hpText != null) hpText.text = $"HP: {(int)hp} / {(int)maxHp}";
        if (hpFill != null)
        {
            float pct = maxHp > 0f ? hp / maxHp : 0f;
            hpFill.color = pct > 0.5f ? Color.green : pct > 0.25f ? Color.yellow : Color.red;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЩИТ
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateShieldUI(PlayerStats stats)
    {
        float shieldHp    = stats.CurrentShieldHP;
        float maxShieldHp = stats.MaxShieldHP;

        bool hasShield = maxShieldHp > 0f;
        if (shieldBarRoot != null && maxShieldHp != _lastMaxShield)
        {
            shieldBarRoot.SetActive(hasShield);
            _lastMaxShield = maxShieldHp;
        }

        if (!hasShield) return;

        if (shieldSlider != null) { shieldSlider.maxValue = maxShieldHp; shieldSlider.value = shieldHp; }
        if (shieldText != null) shieldText.text = $"🔷 {(int)shieldHp} / {(int)maxShieldHp}";
        if (shieldFill != null)
        {
            float pct = maxShieldHp > 0f ? shieldHp / maxShieldHp : 0f;
            shieldFill.color = pct > 0.5f ? new Color(0.2f, 0.6f, 1.0f)
                             : pct > 0.25f ? new Color(0.4f, 0.7f, 1.0f)
                             : new Color(0.5f, 0.5f, 0.6f);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // XP
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateExperienceUI(PlayerStats stats)
    {
        float xp       = stats.CurrentXP;
        float xpToNext = stats.XpToNextLevel;

        if (xpSlider != null) { xpSlider.maxValue = xpToNext; xpSlider.value = xp; }
        if (xpText != null)
        {
            float pct = xpToNext > 0f ? (xp / xpToNext) * 100f : 0f;
            xpText.text = $"XP: {pct:F1}%";
        }
        if (xpFill != null)
        {
            float pct = xpToNext > 0f ? xp / xpToNext : 0f;
            xpFill.color = pct > 0.8f ? Color.green : Color.cyan;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УРОВЕНЬ ИГРОКА
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateLevelUI(PlayerStats stats)
    {
        if (levelText != null) levelText.text = $"LEVEL {stats.CurrentLevel}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УБИЙСТВА
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateKillsUI(PlayerStats stats)
    {
        if (killCountText == null) return;
        int kills = stats.GetKillCount();
        if (kills == _lastKillCount) return;
        _lastKillCount     = kills;
        killCountText.text = kills.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЛОТЫ ОРУЖИЙ
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateWeaponSlotsUI(WeaponManager weaponManager)
    {
        if (weaponManager == null || weaponSlotContainer == null) return;

        int count = weaponManager.activeWeapons?.Count ?? 0;

        // Перестраиваем кэш иконок при изменении количества оружий
        if (count != _lastWeaponCount)
        {
            _lastWeaponCount  = count;
            _cachedSlotImages = null;
        }

        if (_cachedSlotImages == null)
        {
            var imgs = new System.Collections.Generic.List<Image>();
            foreach (Transform child in weaponSlotContainer)
            {
                var img = child.GetComponent<Image>();
                if (img != null) imgs.Add(img);
            }
            _cachedSlotImages = imgs.ToArray();
        }

        // Обновляем иконки и уровни каждый кадр
        for (int i = 0; i < _cachedSlotImages.Length; i++)
        {
            if (_cachedSlotImages[i] == null) continue;

            if (i >= count)
            {
                // Пустой слот
                _cachedSlotImages[i].sprite = null;
                _cachedSlotImages[i].color  = new Color(1f, 1f, 1f, 0.2f);
                SetWeaponLevelText(i, "");
            }
            else
            {
                // Заполненный слот — иконка
                var weaponObj = weaponManager.activeWeapons[i];
                Sprite icon   = weaponManager.GetWeaponIcon(weaponObj);
                if (icon != null)
                {
                    _cachedSlotImages[i].sprite = icon;
                    _cachedSlotImages[i].color  = Color.white;
                }

                // Уровень оружия
                if (weaponObj != null)
                {
                    int  level = weaponManager.GetWeaponLevel(weaponObj.name);
                    bool isMax = weaponManager.IsWeaponMaxLevel(weaponObj.name);
                    SetWeaponLevelText(i, isMax ? "MAX" : $"Lv{level}", isMax);
                }
            }
        }

        if (weaponCountText != null)
            weaponCountText.text = $"Weapons: {count}/{weaponManager.maxSlots}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЛОТЫ ТОТЕМОВ
    // ─────────────────────────────────────────────────────────────────────────

    protected virtual void UpdateTotemSlotsUI(TotemManager totemManager)
    {
        if (totemManager == null || totemSlotContainer == null) return;

        var totems = totemManager.ActiveTotems;
        int count  = totems.Count;

        bool layoutChanged = count != _lastTotemCount;
        if (layoutChanged) _lastTotemCount = count;

        int slotIdx = 0;
        foreach (Transform child in totemSlotContainer)
        {
            var img   = child.GetComponent<Image>();
            // label = основной TextMeshProUGUI слота (накопленное значение)
            // Берём первый TMP который НЕ является частью нашего массива totemLevelTexts
            var label = GetTotemValueLabel(child, slotIdx);

            if (slotIdx < count)
            {
                var def = totems[slotIdx];
                float val = totemManager.GetAccumulatedValue(def.totemId);

                // Иконка (только при смене layout)
                if (img != null && layoutChanged)
                {
                    if (def.icon != null) { img.sprite = def.icon; img.color = Color.white; }
                    else                  { img.sprite = null; img.color = new Color(0.4f, 0.3f, 0.6f, 0.8f); }
                }

                // Накопленное значение
                if (label != null)
                {
                    string valStr = FormatTotemValue(def.bonusType, val);
                    label.text = def.icon == null ? $"🗿\n{valStr}" : valStr;
                }

                // Уровень тотема
                int  level = totemManager.GetTotemLevel(def.totemId);
                bool isMax = totemManager.IsTotemMaxLevel(def.totemId);
                SetTotemLevelText(slotIdx, isMax ? "MAX" : $"Lv{level}", isMax);
            }
            else
            {
                // Пустой слот
                if (img != null && layoutChanged) { img.sprite = null; img.color = new Color(1f, 1f, 1f, 0.12f); }
                if (label != null) label.text = "";
                SetTotemLevelText(slotIdx, "");
            }

            slotIdx++;
            if (slotIdx >= totemManager.maxSlots) break;
        }

        if (totemCountText != null)
            totemCountText.text = $"🗿 {count}/{totemManager.maxSlots}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УСТАНОВКА ТЕКСТА УРОВНЯ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Устанавливает текст уровня для слота оружия.
    ///
    /// ПРИОРИТЕТ:
    ///   1. weaponLevelTexts[index] — если назначен в Inspector (рекомендуется для scene UI)
    ///   2. Fallback: ищет "LevelText" дочерний объект или создаёт TMP программно
    ///
    /// Для scene-объектов: назначай weaponLevelTexts[] в Inspector.
    /// Для prefab-объектов: добавь дочерний "LevelText" в prefab.
    /// </summary>
    private void SetWeaponLevelText(int slotIndex, string text, bool isMax = false)
    {
        TextMeshProUGUI tmp = GetWeaponLevelTMP(slotIndex);
        if (tmp == null) return;

        tmp.text  = text;
        tmp.color = isMax ? MAX_LEVEL_COLOR : NORMAL_LEVEL_COLOR;
    }

    private void SetTotemLevelText(int slotIndex, string text, bool isMax = false)
    {
        TextMeshProUGUI tmp = GetTotemLevelTMP(slotIndex);
        if (tmp == null) return;

        tmp.text  = text;
        tmp.color = isMax ? MAX_LEVEL_COLOR : NORMAL_LEVEL_COLOR;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОЛУЧЕНИЕ TMP ДЛЯ УРОВНЯ
    // ─────────────────────────────────────────────────────────────────────────

    private TextMeshProUGUI GetWeaponLevelTMP(int slotIndex)
    {
        // Приоритет 1: явно назначенный в Inspector
        if (weaponLevelTexts != null && slotIndex < weaponLevelTexts.Length &&
            weaponLevelTexts[slotIndex] != null)
            return weaponLevelTexts[slotIndex];

        // Приоритет 2: fallback — находим slot Image и создаём дочерний TMP
        if (_cachedSlotImages == null || slotIndex >= _cachedSlotImages.Length) return null;

        var slotImage = _cachedSlotImages[slotIndex];
        if (slotImage == null) return null;

        // Инициализируем fallback-массив
        if (_fallbackWeaponLevelTexts == null)
            _fallbackWeaponLevelTexts = new TextMeshProUGUI[_cachedSlotImages.Length];

        if (slotIndex >= _fallbackWeaponLevelTexts.Length)
        {
            var resized = new TextMeshProUGUI[_cachedSlotImages.Length];
            System.Array.Copy(_fallbackWeaponLevelTexts, resized,
                Mathf.Min(_fallbackWeaponLevelTexts.Length, resized.Length));
            _fallbackWeaponLevelTexts = resized;
        }

        if (_fallbackWeaponLevelTexts[slotIndex] != null)
            return _fallbackWeaponLevelTexts[slotIndex];

        // Создаём новый TMP
        _fallbackWeaponLevelTexts[slotIndex] = CreateLevelTextObject(slotImage.gameObject);
        return _fallbackWeaponLevelTexts[slotIndex];
    }

    private TextMeshProUGUI GetTotemLevelTMP(int slotIndex)
    {
        // Приоритет 1: явно назначенный в Inspector
        if (totemLevelTexts != null && slotIndex < totemLevelTexts.Length &&
            totemLevelTexts[slotIndex] != null)
            return totemLevelTexts[slotIndex];

        // Приоритет 2: fallback — ищем slot в totemSlotContainer
        if (totemSlotContainer == null) return null;

        int idx = 0;
        foreach (Transform child in totemSlotContainer)
        {
            if (idx == slotIndex)
            {
                if (_fallbackTotemLevelTexts == null)
                    _fallbackTotemLevelTexts = new TextMeshProUGUI[totemSlotContainer.childCount];

                if (slotIndex < _fallbackTotemLevelTexts.Length &&
                    _fallbackTotemLevelTexts[slotIndex] != null)
                    return _fallbackTotemLevelTexts[slotIndex];

                var created = CreateLevelTextObject(child.gameObject);
                if (slotIndex < _fallbackTotemLevelTexts.Length)
                    _fallbackTotemLevelTexts[slotIndex] = created;
                return created;
            }
            idx++;
        }

        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СОЗДАНИЕ TMP ПРОГРАММНО (FALLBACK)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Создаёт TextMeshProUGUI как дочерний объект к слоту.
    /// Используется только как fallback если weaponLevelTexts/totemLevelTexts не назначены.
    ///
    /// Для надёжного отображения на scene-объектах лучше назначить
    /// TMP объекты вручную в Inspector через weaponLevelTexts[].
    /// </summary>
    private TextMeshProUGUI CreateLevelTextObject(GameObject parentSlot)
    {
        if (parentSlot == null) return null;

        // Проверяем нет ли уже созданного
        var existing = parentSlot.transform.Find("LevelText");
        if (existing != null)
        {
            var existingTmp = existing.GetComponent<TextMeshProUGUI>();
            if (existingTmp != null) return existingTmp;
        }

        var obj = new GameObject("LevelText");
        obj.transform.SetParent(parentSlot.transform, false);

        var rt = obj.AddComponent<RectTransform>();
        rt.anchorMin         = new Vector2(0f, 0f);
        rt.anchorMax         = new Vector2(1f, 0.38f);
        rt.offsetMin         = Vector2.zero;
        rt.offsetMax         = Vector2.zero;

        var tmp              = obj.AddComponent<TextMeshProUGUI>();
        tmp.fontSize         = 10f;
        tmp.fontStyle        = FontStyles.Bold;
        tmp.color            = NORMAL_LEVEL_COLOR;
        tmp.alignment        = TextAlignmentOptions.BottomRight;
        tmp.text             = "";
        tmp.outlineWidth     = 0.2f;
        tmp.outlineColor     = new Color32(0, 0, 0, 200);

        return tmp;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УТИЛИТЫ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает label-TMP для накопленного значения тотема (не уровень).
    /// Берём первый TMP в дочерних объектах слота который не входит в totemLevelTexts[].
    /// </summary>
    private TextMeshProUGUI GetTotemValueLabel(Transform slotChild, int slotIndex)
    {
        // Проверяем назначен ли этот слот в totemLevelTexts
        TextMeshProUGUI levelTmp = null;
        if (totemLevelTexts != null && slotIndex < totemLevelTexts.Length)
            levelTmp = totemLevelTexts[slotIndex];

        // Находим любой TMP в дочерних кроме levelTmp
        foreach (var tmp in slotChild.GetComponentsInChildren<TextMeshProUGUI>(false))
        {
            if (tmp != levelTmp) return tmp;
        }

        return null;
    }

    private static string FormatTotemValue(TotemBonusType type, float val)
        => type == TotemBonusType.Armor ? $"+{val:F0}" : $"+{val:F0}%";

    // ─────────────────────────────────────────────────────────────────────────
    // ИНВАЛИДАЦИЯ КЭША
    // ─────────────────────────────────────────────────────────────────────────

    protected void InvalidateWeaponSlotsCache()
    {
        _cachedSlotImages         = null;
        _fallbackWeaponLevelTexts = null;
        _lastWeaponCount          = -1;
    }

    protected void ResetDirtyState()
    {
        InvalidateWeaponSlotsCache();
        _fallbackTotemLevelTexts = null;
        _lastKillCount  = -1;
        _lastTotemCount = -1;
        _lastMaxShield  = -1f;
    }

    protected PlayerStats FindLocalPlayerStats()
    {
        var allStats = FindObjectsByType<PlayerStats>(FindObjectsSortMode.None);
        foreach (var s in allStats)
        {
            if (s.IsOwner || GameModeManager.IsMode(GameMode.SinglePlayer))
                return s;
        }
        return null;
    }
}
