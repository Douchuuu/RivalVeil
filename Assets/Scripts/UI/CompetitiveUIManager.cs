using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;
using VContainer;

/// <summary>
/// CompetitiveUIManager — UI для Shadow Multiplayer режима.
///
/// ══════════════════════════════════════════════════════════════
/// НОВОЕ: Щит оппонента
/// ══════════════════════════════════════════════════════════════
///
///   Поля для щита оппонента:
///     opponentShieldSlider  — слайдер HP щита
///     opponentShieldText    — текст "🔷 50 / 100"
///     opponentShieldFill    — Image для цвета бара
///     opponentShieldBarRoot — корневой GameObject (скрывается если щита нет)
///
///   Данные читаются из:
///     opponentStats.GetNetShieldHP()
///     opponentStats.GetNetMaxShieldHP()
///
///   Оба значения синхронизируются по сети из PlayerStats
///   через netShieldHP и netMaxShieldHP NetworkVariables.
/// ══════════════════════════════════════════════════════════════
/// </summary>
public class CompetitiveUIManager_Canvas : BasePlayerUIManager
{
    [Header("── Canvas ──────────────────────────────────────")]
    [SerializeField] private Canvas competitiveCanvas;

    [Header("── Мой локальный таймер ───────────────────────")]
    [SerializeField] private TextMeshProUGUI localTimerText;
    [SerializeField] private Slider          localTimerSlider;
    [SerializeField] private Image           localTimerFill;

    [Header("── Глобальный таймер матча ─────────────────────")]
    [SerializeField] private TextMeshProUGUI globalTimerText;
    [SerializeField] private Slider          globalTimerSlider;
    [SerializeField] private Image           globalTimerBackground;

    [Header("── Оппонент — HP ───────────────────────────────")]
    [SerializeField] private Slider          opponentHpSlider;
    [SerializeField] private TextMeshProUGUI opponentHpText;
    [SerializeField] private Image           opponentHpFill;

    [Header("── Оппонент — Щит ──────────────────────────────")]
    [Tooltip("Root GameObject щита оппонента — скрывается если MaxShield = 0.")]
    [SerializeField] private GameObject      opponentShieldBarRoot;
    [SerializeField] private Slider          opponentShieldSlider;
    [SerializeField] private TextMeshProUGUI opponentShieldText;
    [SerializeField] private Image           opponentShieldFill;

    [Header("── Оппонент — XP ───────────────────────────────")]
    [SerializeField] private Slider          opponentXpSlider;
    [SerializeField] private TextMeshProUGUI opponentXpText;
    [SerializeField] private TextMeshProUGUI opponentLevelText;
    [SerializeField] private Image           opponentXpFill;

    [Header("── Оппонент — Убийства ────────────────────────")]
    [SerializeField] private TextMeshProUGUI opponentKillsText;
    [SerializeField] private Slider          opponentKillsSlider;

    [Header("── Оппонент — Оружия ──────────────────────────")]
    [SerializeField] private TextMeshProUGUI opponentWeaponText;
    [SerializeField] private Image[]         opponentWeaponSlots;

    [Header("── Оппонент — Уровни оружий ───────────────────")]
    [Tooltip("По одному TextMeshProUGUI на каждый слот оружия оппонента по порядку.")]
    [SerializeField] private TextMeshProUGUI[] opponentWeaponLevelTexts;

    [Header("── Оппонент — Тотемы ──────────────────────────")]
    [SerializeField] private Transform       opponentTotemSlotContainer;
    [SerializeField] private TextMeshProUGUI opponentTotemCountText;

    [Header("── Оппонент — Уровни тотемов ──────────────────")]
    [Tooltip("По одному TextMeshProUGUI на каждый слот тотема оппонента по порядку.")]
    [SerializeField] private TextMeshProUGUI[] opponentTotemLevelTexts;

    [Header("── Статус ──────────────────────────────────────")]
    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI differenceText;
    [SerializeField] private Image           statusBackground;

    [Header("── Цвета ──────────────────────────────────────")]
    [SerializeField] private Color normalTimerColor     = Color.white;
    [SerializeField] private Color warningTimerColor    = Color.yellow;
    [SerializeField] private Color finalStageTimerColor = Color.red;
    [SerializeField] private Color winningColor         = Color.green;
    [SerializeField] private Color losingColor          = Color.red;
    [SerializeField] private Color tiedColor            = Color.yellow;

    private static readonly Color MAX_LEVEL_COLOR    = new Color(1f, 0.72f, 0f);
    private static readonly Color NORMAL_LEVEL_COLOR = Color.black;

    // ─── ССЫЛКИ ───────────────────────────────────────────────────────────────

    private PlayerStats             _myStats;
    private PlayerStats             _opponentStats;
    private WeaponManager           _weaponManager;
    private TotemManager            _totemManager;
    private LocalGameTimer          _localTimer;
    private CompetitiveTimerManager _globalTimer;
    private GameMode                _lastGameMode = GameMode.Selecting;
    private TotemDatabase           _totemDb;

    // ─── DIRTY CHECKS ─────────────────────────────────────────────────────────

    private int   _lastMyKills                = -1;
    private float _lastOpponentHP             = -1f;
    private float _lastOpponentShieldHP       = -1f;  // НОВОЕ
    private float _lastOpponentMaxShieldHP    = -1f;  // НОВОЕ
    private int   _lastOpponentLevel          = -1;
    private float _lastOpponentXP             = -1f;
    private int   _lastOpponentKills          = -1;
    private int   _lastOpponentWeapons        = -1;
    private int   _lastLocalTimerSec          = -1;
    private int   _lastGlobalTimerSec         = -1;
    private int   _lastOpponentWeaponLevels   = -1;
    private long  _lastOpponentTotemData      = -1L;

    // ─────────────────────────────────────────────────────────────────────────
    // INJECTION
    // ─────────────────────────────────────────────────────────────────────────

    [Inject]
    public void Construct(WeaponDatabase weaponDb, TotemDatabase totemDb)
    {
        _totemDb = totemDb;
        Debug.Log("✅ [CompetitiveUIManager] Инжектированы WeaponDatabase + TotemDatabase");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (competitiveCanvas == null)
            competitiveCanvas = GetComponentInParent<Canvas>();

        if (competitiveCanvas != null)
            competitiveCanvas.enabled = false;

        // Щит оппонента скрыт по умолчанию
        if (opponentShieldBarRoot != null)
            opponentShieldBarRoot.SetActive(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        GameMode currentMode = GameModeManager.GetCurrentMode();
        if (currentMode != _lastGameMode)
        {
            OnGameModeChanged(currentMode);
            _lastGameMode = currentMode;
        }

        if (!GameModeManager.IsCompetitiveMode()) return;

        if (_myStats == null || _opponentStats == null) FindPlayers();

        if (_localTimer == null)
            _localTimer = LocalGameTimer.Instance ?? FindFirstObjectByType<LocalGameTimer>();
        if (_globalTimer == null)
            _globalTimer = CompetitiveTimerManager.Instance ?? FindFirstObjectByType<CompetitiveTimerManager>();
        if (_weaponManager == null && _myStats != null)
            _weaponManager = _myStats.GetComponent<WeaponManager>();
        if (_totemManager == null && _myStats != null)
            _totemManager = _myStats.GetComponent<TotemManager>();

        if (_myStats == null) return;

        UpdateBaseUI(_myStats, _weaponManager, _totemManager);
        UpdateLocalTimerUI();
        UpdateGlobalTimerUI();
        UpdateKillsStatusUI();

        if (_opponentStats != null)
            UpdateOpponentUI();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СМЕНА РЕЖИМА
    // ─────────────────────────────────────────────────────────────────────────

    void OnGameModeChanged(GameMode newMode)
    {
        _myStats       = null;
        _opponentStats = null;
        _weaponManager = null;
        _totemManager  = null;

        ResetDirtyState();
        ResetCompetitiveDirtyChecks();

        if (competitiveCanvas != null)
            competitiveCanvas.enabled = (newMode == GameMode.ShadowMultiplayer);
    }

    void ResetCompetitiveDirtyChecks()
    {
        _lastMyKills             = -1;
        _lastOpponentHP          = -1f;
        _lastOpponentShieldHP    = -1f;   // НОВОЕ
        _lastOpponentMaxShieldHP = -1f;   // НОВОЕ
        _lastOpponentLevel       = -1;
        _lastOpponentXP          = -1f;
        _lastOpponentKills       = -1;
        _lastOpponentWeapons     = -1;
        _lastLocalTimerSec       = -1;
        _lastGlobalTimerSec      = -1;
        _lastOpponentWeaponLevels = -1;
        _lastOpponentTotemData   = -1L;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ИГРОКОВ
    // ─────────────────────────────────────────────────────────────────────────

    void FindPlayers()
    {
        foreach (var p in FindObjectsByType<PlayerStats>(FindObjectsSortMode.None))
        {
            if (p.IsOwner) _myStats       = p;
            else           _opponentStats = p;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ТАЙМЕРЫ
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateLocalTimerUI()
    {
        if (_localTimer == null) return;

        float elapsed    = _localTimer.GetTimeElapsed();
        int   currentSec = (int)elapsed;
        if (currentSec == _lastLocalTimerSec) return;
        _lastLocalTimerSec = currentSec;

        if (localTimerText != null)
        {
            localTimerText.text  = $"{currentSec / 60:D2}:{currentSec % 60:D2}";
            localTimerText.color = _localTimer.IsTimeExpired() ? finalStageTimerColor
                                 : elapsed >= 540f ? warningTimerColor : normalTimerColor;
        }

        float progress = _localTimer.GetProgress();
        if (localTimerSlider != null) { localTimerSlider.maxValue = 1f; localTimerSlider.value = progress; }
        if (localTimerFill   != null)
            localTimerFill.color = progress >= 1.0f ? finalStageTimerColor
                                 : progress >= 0.9f ? warningTimerColor : Color.blue;
    }

    void UpdateGlobalTimerUI()
    {
        if (_globalTimer == null) return;

        float remaining  = _globalTimer.GetTimeRemaining();
        int   currentSec = (int)remaining;
        if (currentSec == _lastGlobalTimerSec) return;
        _lastGlobalTimerSec = currentSec;

        if (globalTimerText != null)
        {
            globalTimerText.text  = $"{currentSec / 60:D2}:{currentSec % 60:D2}";
            globalTimerText.color = remaining <= 30f  ? finalStageTimerColor
                                  : remaining <= 120f ? warningTimerColor : normalTimerColor;
        }
        if (globalTimerSlider != null)
        {
            globalTimerSlider.maxValue = 1f;
            globalTimerSlider.value    = _globalTimer.GetProgress();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // МОИ УБИЙСТВА И СТАТУС
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateKillsStatusUI()
    {
        int myKills = _myStats.GetKillCount();
        if (myKills == _lastMyKills) return;
        _lastMyKills = myKills;
        UpdateStatus(myKills);
    }

    void UpdateStatus(int myKills)
    {
        if (statusText == null || statusBackground == null) return;

        int oppKills = _opponentStats != null ? _opponentStats.GetKillCount() : 0;
        int diff     = myKills - oppKills;

        statusText.text        = diff > 0 ? "WINNING!" : diff < 0 ? "LOSING..." : "TIED";
        statusBackground.color = diff > 0 ? winningColor : diff < 0 ? losingColor : tiedColor;

        if (differenceText != null)
            differenceText.text = (diff >= 0 ? "+" : "") + diff;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UI ОППОНЕНТА
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateOpponentUI()
    {
        // HP
        float oppHP = _opponentStats.GetNetHealth();
        if (Mathf.Abs(oppHP - _lastOpponentHP) > 0.01f)
        {
            _lastOpponentHP = oppHP;
            UpdateOpponentHealthUI(oppHP, _opponentStats.GetNetMaxHealth());
        }

        // НОВОЕ: Щит оппонента
        float oppShieldHP    = _opponentStats.GetNetShieldHP();
        float oppMaxShieldHP = _opponentStats.GetNetMaxShieldHP();

        bool shieldChanged = Mathf.Abs(oppShieldHP    - _lastOpponentShieldHP)    > 0.01f
                          || Mathf.Abs(oppMaxShieldHP - _lastOpponentMaxShieldHP) > 0.01f;

        if (shieldChanged)
        {
            _lastOpponentShieldHP    = oppShieldHP;
            _lastOpponentMaxShieldHP = oppMaxShieldHP;
            UpdateOpponentShieldUI(oppShieldHP, oppMaxShieldHP);
        }

        // Уровень
        int oppLevel = _opponentStats.GetNetLevel();
        if (oppLevel != _lastOpponentLevel)
        {
            _lastOpponentLevel = oppLevel;
            if (opponentLevelText != null) opponentLevelText.text = $"Lvl {oppLevel}";
        }

        // XP
        float oppXP = _opponentStats.GetNetXP();
        if (Mathf.Abs(oppXP - _lastOpponentXP) > 0.5f)
        {
            _lastOpponentXP = oppXP;
            UpdateOpponentXPUI(oppXP, _opponentStats.GetNetXPToNext());
        }

        // Убийства
        int oppKills = _opponentStats.GetKillCount();
        if (oppKills != _lastOpponentKills)
        {
            _lastOpponentKills = oppKills;
            if (opponentKillsText   != null) opponentKillsText.text    = oppKills.ToString();
            if (opponentKillsSlider != null) opponentKillsSlider.value = oppKills;
            UpdateStatus(_myStats.GetKillCount());
        }

        // Оружия (иконки)
        int oppWeaponFlags = _opponentStats.GetNetWeaponFlags();
        if (oppWeaponFlags != _lastOpponentWeapons)
        {
            _lastOpponentWeapons = oppWeaponFlags;
            UpdateOpponentWeaponIconsUI(oppWeaponFlags);
        }

        // Уровни оружий
        int oppWeaponLevels = _opponentStats.GetNetWeaponLevels();
        if (oppWeaponLevels != _lastOpponentWeaponLevels)
        {
            _lastOpponentWeaponLevels = oppWeaponLevels;
            UpdateOpponentWeaponLevelsUI(oppWeaponLevels);
        }

        // Тотемы
        long oppTotemData = _opponentStats.GetNetTotemData();
        if (oppTotemData != _lastOpponentTotemData)
        {
            _lastOpponentTotemData = oppTotemData;
            UpdateOpponentTotemsUI(oppTotemData);
        }
    }

    // ─── HP оппонента ─────────────────────────────────────────────────────────

    void UpdateOpponentHealthUI(float hp, float maxHp)
    {
        if (opponentHpSlider != null)
        {
            opponentHpSlider.maxValue = maxHp > 0 ? maxHp : 100f;
            opponentHpSlider.value    = hp;
        }
        if (opponentHpText != null) opponentHpText.text = $"HP: {(int)hp} / {(int)maxHp}";
        if (opponentHpFill != null)
        {
            float pct = maxHp > 0 ? hp / maxHp : 0f;
            opponentHpFill.color = pct > 0.5f ? Color.green : pct > 0.25f ? Color.yellow : Color.red;
        }
    }

    // ─── НОВОЕ: Щит оппонента ─────────────────────────────────────────────────

    /// <summary>
    /// Обновляет бар щита оппонента.
    /// Скрывает opponentShieldBarRoot если maxShield = 0 (нет тотема брони).
    /// Цвета совпадают с BasePlayerUIManager.UpdateShieldUI().
    /// </summary>
    void UpdateOpponentShieldUI(float shieldHP, float maxShieldHP)
    {
        bool hasShield = maxShieldHP > 0f;

        // Показываем/скрываем контейнер щита
        if (opponentShieldBarRoot != null)
            opponentShieldBarRoot.SetActive(hasShield);

        if (!hasShield) return;

        if (opponentShieldSlider != null)
        {
            opponentShieldSlider.maxValue = maxShieldHP;
            opponentShieldSlider.value    = shieldHP;
        }

        if (opponentShieldText != null)
            opponentShieldText.text = $"🔷 {(int)shieldHP} / {(int)maxShieldHP}";

        if (opponentShieldFill != null)
        {
            float pct = maxShieldHP > 0f ? shieldHP / maxShieldHP : 0f;
            opponentShieldFill.color = pct > 0.5f  ? new Color(0.2f, 0.6f, 1.0f)   // синий
                                     : pct > 0.25f ? new Color(0.4f, 0.7f, 1.0f)   // голубой
                                                   : new Color(0.5f, 0.5f, 0.6f);  // серый
        }
    }

    // ─── XP оппонента ─────────────────────────────────────────────────────────

    void UpdateOpponentXPUI(float xp, float xpToNext)
    {
        if (opponentXpSlider != null)
        {
            opponentXpSlider.maxValue = xpToNext > 0 ? xpToNext : 100f;
            opponentXpSlider.value    = xp;
        }
        if (opponentXpText != null) opponentXpText.text = $"XP: {(int)xp}/{(int)xpToNext}";
        if (opponentXpFill != null)
        {
            float pct = xpToNext > 0 ? xp / xpToNext : 0f;
            opponentXpFill.color = pct > 0.8f ? Color.green : Color.cyan;
        }
    }

    // ─── Иконки оружий оппонента ──────────────────────────────────────────────

    void UpdateOpponentWeaponIconsUI(int flags)
    {
        if (opponentWeaponSlots == null) return;

        var weaponDb = InjectionProvider.Container?.Resolve<WeaponDatabase>();
        var opponentWeapons = weaponDb != null
            ? weaponDb.GetFromFlags(flags)
            : new List<WeaponDefinition>();

        if (opponentWeaponText != null)
        {
            if (opponentWeapons.Count > 0)
            {
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < opponentWeapons.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(opponentWeapons[i].displayName);
                }
                opponentWeaponText.text = names.ToString();
            }
            else opponentWeaponText.text = "—";
        }

        for (int i = 0; i < opponentWeaponSlots.Length; i++)
        {
            if (opponentWeaponSlots[i] == null) continue;
            if (i < opponentWeapons.Count)
            {
                opponentWeaponSlots[i].sprite = opponentWeapons[i].icon;
                opponentWeaponSlots[i].color  = Color.white;
            }
            else
            {
                opponentWeaponSlots[i].sprite = null;
                opponentWeaponSlots[i].color  = new Color(1f, 1f, 1f, 0.2f);
            }
        }
    }

    // ─── Уровни оружий оппонента ──────────────────────────────────────────────

    void UpdateOpponentWeaponLevelsUI(int packedLevels)
    {
        if (opponentWeaponLevelTexts == null) return;

        for (int i = 0; i < opponentWeaponLevelTexts.Length; i++)
        {
            var tmp = opponentWeaponLevelTexts[i];
            if (tmp == null) continue;

            bool slotHasWeapon = opponentWeaponSlots != null
                && i < opponentWeaponSlots.Length
                && opponentWeaponSlots[i] != null
                && opponentWeaponSlots[i].sprite != null;

            if (!slotHasWeapon) { tmp.text = ""; continue; }

            int  level = PlayerStats.UnpackWeaponLevel(packedLevels, i);
            bool isMax = level >= WeaponManager.MAX_WEAPON_LEVEL;

            tmp.text  = isMax ? "MAX" : (level > 0 ? $"Lv{level}" : "");
            tmp.color = isMax ? MAX_LEVEL_COLOR : NORMAL_LEVEL_COLOR;
        }
    }

    // ─── Тотемы оппонента ─────────────────────────────────────────────────────

    void UpdateOpponentTotemsUI(long packedData)
    {
        const int TOTAL_SLOTS = 4;

        int filledCount = 0;
        for (int i = 0; i < TOTAL_SLOTS; i++)
        {
            var (dbIndex, _) = PlayerStats.UnpackTotemSlot(packedData, i);
            if (dbIndex != 31) filledCount++;
        }

        if (opponentTotemCountText != null)
            opponentTotemCountText.text = $"🗿 {filledCount}/{TOTAL_SLOTS}";

        if (opponentTotemSlotContainer == null) return;

        int slotIdx = 0;
        foreach (Transform child in opponentTotemSlotContainer)
        {
            if (slotIdx >= TOTAL_SLOTS) break;

            var img = child.GetComponent<Image>();
            var (dbIndex, level) = PlayerStats.UnpackTotemSlot(packedData, slotIdx);
            bool isEmpty = (dbIndex == 31);

            if (img != null)
            {
                if (isEmpty)
                {
                    img.sprite = null;
                    img.color  = new Color(1f, 1f, 1f, 0.12f);
                }
                else
                {
                    var def = GetTotemByIndex(dbIndex);
                    if (def != null && def.icon != null) { img.sprite = def.icon; img.color = Color.white; }
                    else                                 { img.sprite = null; img.color = new Color(0.4f, 0.3f, 0.6f, 0.8f); }
                }
            }

            if (opponentTotemLevelTexts != null && slotIdx < opponentTotemLevelTexts.Length)
            {
                var lvlTmp = opponentTotemLevelTexts[slotIdx];
                if (lvlTmp != null)
                {
                    if (isEmpty) { lvlTmp.text = ""; }
                    else
                    {
                        bool isMax = level >= TotemManager.MAX_TOTEM_LEVEL;
                        lvlTmp.text  = isMax ? "MAX" : $"Lv{level}";
                        lvlTmp.color = isMax ? MAX_LEVEL_COLOR : NORMAL_LEVEL_COLOR;
                    }
                }
            }

            slotIdx++;
        }
    }

    private TotemDefinition GetTotemByIndex(int dbIndex)
    {
        if (_totemDb == null) return null;
        var all = _totemDb.GetAll();
        if (dbIndex < 0 || dbIndex >= all.Count) return null;
        return all[dbIndex];
    }
}
