using UnityEngine;
using System;
using VContainer;

/// <summary>
/// ФИКС БАГ 1: Таймер не работал при запуске игры.
/// 
/// ПРОБЛЕМА:
///   При старте игры GameModeManager.CurrentMode = GameMode.Selecting
///   В Update() была проверка:
///     if (!GameModeManager.IsMode(GameMode.SinglePlayer) && ...) return;
///   Таймер выходил раньше времени и не считал.
/// 
/// РЕШЕНИЕ:
///   Подписываемся на OnGameModeChanged event ИСПОЛЬЗУЯ +=
///   Когда режим меняется → устанавливаем флаг _isActive = true.
///   Update() проверяет этот флаг, а не режим напрямую.
///
/// ШАГ 4 РЕФАКТОРИНГА — GameStateService:
///   LocalGameTimer теперь подписывается на OnPaused/OnResumed из GameStateService.
///   Когда LevelUpPanel открывается → _gameState.Pause(PauseReason.LevelUp)
///   → LocalGameTimer.OnGamePaused() → PauseTimer() — таймер останавливается.
///   Когда панель закрывается → _gameState.Resume()
///   → LocalGameTimer.OnGameResumed() → ResumeTimer() — таймер продолжается.
///   LocalGameTimer зарегистрирован в GameLifetimeScope через RegisterComponent,
///   поэтому VContainer вызывает [Inject] Construct() автоматически.
/// </summary>
public class LocalGameTimer : MonoBehaviour
{
    [SerializeField] private float stageDurationMinutes = 10f;
    private float timeElapsed = 0f;
    private bool isPaused = false;
    public event Action OnTimeExpired;
    private bool hasTimeExpired = false;
    private bool _isActive = false;

    public static LocalGameTimer Instance { get; private set; }

    // ─── ЗАВИСИМОСТЬ — GameStateService ──────────────────────────────────────
    private GameStateService _gameState;

    [Inject]
    public void Construct(GameStateService gameState)
    {
        _gameState = gameState;

        // Подписываемся на события паузы — таймер останавливается/продолжается
        // автоматически при любой причине паузы (LevelUp, GameOver и т.д.)
        _gameState.OnPaused += OnGamePaused;
        _gameState.OnResumed += OnGameResumed;
    }

    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }

    void Start()
    {
        if (GameModeManager.Instance != null)
        {
            GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;
            Debug.Log("[LocalGameTimer] Подписан на OnGameModeChanged событие");
        }
        else
        {
            Debug.LogWarning("[LocalGameTimer] GameModeManager.Instance не найден при Start()!");
        }
    }

    void OnDestroy()
    {
        if (GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged -= OnGameModeChanged;

        // Отписываемся от GameStateService чтобы избежать утечки памяти
        if (_gameState != null)
        {
            _gameState.OnPaused -= OnGamePaused;
            _gameState.OnResumed -= OnGameResumed;
        }
    }

    // ─── ОБРАБОТЧИКИ ПАУЗЫ ───────────────────────────────────────────────────

    private void OnGamePaused(PauseReason reason)
    {
        PauseTimer();
    }

    private void OnGameResumed()
    {
        ResumeTimer();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void OnGameModeChanged(GameMode newMode)
    {
        _isActive = (newMode == GameMode.SinglePlayer || newMode == GameMode.ShadowMultiplayer);

        if (_isActive)
        {
            timeElapsed = 0f;
            hasTimeExpired = false;
            isPaused = false;
            Debug.Log($"✅ [LocalGameTimer] АКТИВИРОВАН для режима: {newMode}. Длительность: {stageDurationMinutes} мин");
        }
        else
        {
            Debug.Log($"⏸️ [LocalGameTimer] ДЕАКТИВИРОВАН (режим: {newMode})");
        }
    }

    void Update()
    {
        if (!_isActive || isPaused)
            return;

        timeElapsed += Time.unscaledDeltaTime;
        float totalSeconds = stageDurationMinutes * 60f;

        if (timeElapsed >= totalSeconds && !hasTimeExpired)
        {
            hasTimeExpired = true;
            OnTimeExpired?.Invoke();
            Debug.Log("🔴 [LocalGameTimer] ВРЕМЯ ИСТЕКЛО!");
        }
    }

    // ─── ПУБЛИЧНЫЙ API ───────────────────────────────────────────────────────

    public void PauseTimer()
    {
        isPaused = true;
        Debug.Log("⏸️ [LocalGameTimer] ПАУЗА");
    }

    public void ResumeTimer()
    {
        isPaused = false;
        Debug.Log("▶️ [LocalGameTimer] ПРОДОЛЖЕНИЕ");
    }

    public float GetTimeRemaining()
    {
        float totalSeconds = stageDurationMinutes * 60f;
        return Mathf.Max(0f, totalSeconds - timeElapsed);
    }

    public float GetTimeElapsed() => timeElapsed;

    /// <summary>
    /// Время с начала матча в формате MM:SS — счётчик ВВЕРХ (00:00 → 10:00).
    /// Идентично CompetitiveTimerManager.GetTimeFormattedElapsed().
    /// Используй именно этот метод в HUD для SinglePlayer.
    /// </summary>
    public string GetTimeFormattedElapsed()
    {
        float e = timeElapsed;
        return $"{(int)(e / 60f):D2}:{(int)(e % 60f):D2}";
    }

    /// <summary>
    /// Оставшееся время в формате MM:SS — счётчик ВНИЗ (10:00 → 00:00).
    /// </summary>
    public string GetTimeFormattedRemaining()
    {
        float r = GetTimeRemaining();
        return $"{(int)(r / 60f):D2}:{(int)(r % 60f):D2}";
    }

    public float GetProgress()
    {
        float totalSeconds = stageDurationMinutes * 60f;
        return Mathf.Clamp01(timeElapsed / totalSeconds);
    }

    public bool IsTimeExpired() => hasTimeExpired;
    public bool IsPaused() => isPaused;
    public bool IsActive() => _isActive;
}