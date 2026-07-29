using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// UI для Single Player режима.
///
/// Наследует BasePlayerUIManager — HP/Shield/XP/Level/Kills/Weapons/Totems
/// обновляются через UpdateBaseUI(). Здесь только то, что уникально для SinglePlayer:
///   • Таймер (LocalGameTimer)
///   • Сложность волны (EnemySpawner)
///   • Текст режима
/// </summary>
public class SinglePlayerUIManager : BasePlayerUIManager
{
    [Header("── Canvas ──────────────────────────────────────")]
    [SerializeField] private Canvas uiCanvas;

    [Header("── Только для SinglePlayer ─────────────────────")]
    [SerializeField] private TextMeshProUGUI timerText;
    [SerializeField] private TextMeshProUGUI waveText;
    [SerializeField] private TextMeshProUGUI modeText;

    [Header("── Цвета таймера ───────────────────────────────")]
    [SerializeField] private Color timerNormalColor  = Color.white;
    [SerializeField] private Color timerWarningColor = Color.yellow;
    [SerializeField] private Color timerDangerColor  = Color.red;

    // ─── ССЫЛКИ ───────────────────────────────────────────────────────────────
    private PlayerStats   _playerStats;
    private WeaponManager _weaponManager;
    private TotemManager  _totemManager;   // ← ДОБАВЛЕНО
    private EnemySpawner  _enemySpawner;
    private LocalGameTimer _localTimer;
    private GameMode      _lastGameMode = GameMode.Selecting;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (uiCanvas == null)
            uiCanvas = GetComponentInParent<Canvas>();

        if (uiCanvas != null)
            uiCanvas.enabled = false;

        Debug.Log("[SinglePlayerUIManager] Initialized");
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

        if (!GameModeManager.IsMode(GameMode.SinglePlayer)) return;

        // Ленивый поиск зависимостей (один раз после спавна игрока)
        if (_playerStats == null)
            _playerStats = FindLocalPlayerStats();

        if (_playerStats != null)
        {
            if (_weaponManager == null)
                _weaponManager = _playerStats.GetComponent<WeaponManager>();

            // ← ДОБАВЛЕНО: находим TotemManager на том же объекте что и PlayerStats
            if (_totemManager == null)
                _totemManager = _playerStats.GetComponent<TotemManager>();
        }

        if (_enemySpawner == null)
            _enemySpawner = FindFirstObjectByType<EnemySpawner>();

        if (_localTimer == null)
            _localTimer = LocalGameTimer.Instance ?? FindFirstObjectByType<LocalGameTimer>();

        if (_playerStats == null) return;

        // Базовые элементы (из BasePlayerUIManager) — теперь передаём и TotemManager
        UpdateBaseUI(_playerStats, _weaponManager, _totemManager);

        // Элементы уникальные для SinglePlayer
        UpdateTimerText();
        UpdateWaveText();
        UpdateModeText();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СМЕНА РЕЖИМА
    // ─────────────────────────────────────────────────────────────────────────

    void OnGameModeChanged(GameMode newMode)
    {
        Debug.Log($"[SinglePlayerUIManager] Mode → {newMode}");

        bool shouldShow = (newMode == GameMode.SinglePlayer);

        if (uiCanvas != null)
            uiCanvas.enabled = shouldShow;

        if (!shouldShow)
        {
            _playerStats   = null;
            _weaponManager = null;
            _totemManager  = null; // ← ДОБАВЛЕНО
            ResetDirtyState();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // УНИКАЛЬНЫЕ ЭЛЕМЕНТЫ SINGLE PLAYER
    // ─────────────────────────────────────────────────────────────────────────

    void UpdateTimerText()
    {
        if (timerText == null || _localTimer == null) return;

        float remaining = _localTimer.GetTimeRemaining();
        int minutes = (int)(remaining / 60f);
        int seconds = (int)(remaining % 60f);
        timerText.text = LocalGameTimer.Instance.GetTimeFormattedElapsed();

        timerText.color = remaining > 300f ? timerNormalColor
                        : remaining > 60f  ? timerWarningColor
                        : timerDangerColor;
    }

    void UpdateWaveText()
    {
        if (waveText == null || _enemySpawner == null) return;
        waveText.text = $"Difficulty: {_enemySpawner.GetCurrentDifficultyMultiplier():F1}x";
    }

    void UpdateModeText()
    {
        if (modeText != null)
            modeText.text = $"Mode: {GameModeManager.GetCurrentModeString()}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПУБЛИЧНОЕ API
    // ─────────────────────────────────────────────────────────────────────────

    public bool IsUIActive() => uiCanvas != null && uiCanvas.enabled;
}
