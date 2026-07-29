using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using VContainer;

/// <summary>
/// GameOverScreenManager v4.1 — rooms_v2 patch
///
/// ══════════════════════════════════════════════════════════════════════
/// НОВОЕ v4.1 (rooms_v2):
/// ══════════════════════════════════════════════════════════════════════
///   [FIX]  OnReturnButtonClicked: добавлен вызов RatingService.SetRoomMatch(false).
///          Без этого следующий ranked-матч через matchmaking тоже уйдёт
///          без изменения ELO, т.к. флаг _isRoomMatch оставался true.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО v4 (BUG-9):
/// ══════════════════════════════════════════════════════════════════════
///   [FIX]  _gameState?.ForceResumeAll() вместо Resume() —
///          снимает ВСЕ причины паузы (LevelUp + GameOver одновременно).
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class GameOverScreenManager : MonoBehaviour
{
    // ─── SINGLE PLAYER ────────────────────────────────────────────────────────
    [Header("── Single Player GameOver ──────────────────────")]
    [SerializeField] private GameObject      singlePlayerGameOverPanel;
    [SerializeField] private TextMeshProUGUI spResultText;
    [SerializeField] private TextMeshProUGUI spStatsText;
    [SerializeField] private Button          spReturnButton;

    // ─── COMPETITIVE ──────────────────────────────────────────────────────────
    [Header("── Competitive GameOver ────────────────────────")]
    [SerializeField] private GameObject      competitiveGameOverPanel;
    [SerializeField] private TextMeshProUGUI compResultText;
    [SerializeField] private TextMeshProUGUI compStatsText;
    [SerializeField] private Button          compReturnButton;

    // ─── СОСТОЯНИЕ ────────────────────────────────────────────────────────────
    private CompetitiveTimerManager _competitiveTimer;
    private bool        _hasShownGameOver = false;
    private PlayerStats _localPlayerInstance;

    public static bool IsGameOver { get; private set; } = false;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────
    private GameStateService _gameState;
    private PlayerRegistry   _playerRegistry;
    private LocalGameTimer   _localTimer;

    [Inject]
    public void Construct(GameStateService gameState, PlayerRegistry playerRegistry,
                          LocalGameTimer localTimer)
    {
        _gameState      = gameState;
        _playerRegistry = playerRegistry;
        _localTimer     = localTimer;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // INIT
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        singlePlayerGameOverPanel?.SetActive(false);
        competitiveGameOverPanel?.SetActive(false);

        spReturnButton?.onClick.AddListener(OnReturnButtonClicked);
        compReturnButton?.onClick.AddListener(OnReturnButtonClicked);

        ValidateReferences();
        TrySubscribeToPlayerDeath();
    }

    private void ValidateReferences()
    {
        if (singlePlayerGameOverPanel == null)
            Debug.LogError("[GameOverScreenManager] singlePlayerGameOverPanel не назначена!");
        if (competitiveGameOverPanel == null)
            Debug.LogError("[GameOverScreenManager] competitiveGameOverPanel не назначена!");
        if (_gameState == null)
            Debug.LogError("[GameOverScreenManager] GameStateService не инжектирован!");
    }

    private void TrySubscribeToPlayerDeath()
    {
        if (_playerRegistry == null) return;
        _localPlayerInstance = _playerRegistry.GetLocalPlayer();
        if (_localPlayerInstance != null)
        {
            _localPlayerInstance.OnLocalPlayerDied += HandleLocalPlayerDied;
        }
        else
        {
            Invoke(nameof(TrySubscribeToPlayerDeath), 0.5f);
        }
    }

    void OnDestroy()
    {
        if (_localPlayerInstance != null)
            _localPlayerInstance.OnLocalPlayerDied -= HandleLocalPlayerDied;
        IsGameOver = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ТРИГГЕРЫ
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleLocalPlayerDied()
    {
        if (_hasShownGameOver) return;
        _hasShownGameOver = true;
        ShowGameOverScreen(!GameModeManager.IsCompetitiveMode());
    }

    void Update()
    {
        if (!GameModeManager.IsCompetitiveMode()) return;
        if (_competitiveTimer == null)
            _competitiveTimer = CompetitiveTimerManager.Instance;
        if (_competitiveTimer != null && _competitiveTimer.IsTimeExpired() && !_hasShownGameOver)
        {
            _hasShownGameOver = true;
            ShowGameOverScreen(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОКАЗ ПАНЕЛИ
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowGameOverScreen(bool isSinglePlayer)
    {
        IsGameOver = true;
        Cursor.visible   = true;
        Cursor.lockState = CursorLockMode.None;

        if (isSinglePlayer) ShowSinglePlayerGameOver();
        else                ShowCompetitiveGameOver();

        _localTimer?.PauseTimer();
        _gameState?.Pause(PauseReason.GameOver);
    }

    private void ShowSinglePlayerGameOver()
    {
        competitiveGameOverPanel?.SetActive(false);
        singlePlayerGameOverPanel?.SetActive(true);

        var stats = _playerRegistry?.GetLocalPlayer();
        if (stats == null) return;

        float elapsed = _localTimer?.GetTimeElapsed() ?? 0f;
        if (spResultText != null) spResultText.text = "GAME OVER";
        if (spStatsText  != null)
            spStatsText.text = $"Уровень: {stats.CurrentLevel}\n" +
                               $"Убийств: {stats.GetKillCount()}\n" +
                               $"Время: {(int)(elapsed / 60f):D2}:{(int)(elapsed % 60f):D2}";
    }

    private void ShowCompetitiveGameOver()
    {
        singlePlayerGameOverPanel?.SetActive(false);
        competitiveGameOverPanel?.SetActive(true);

        var myStats  = _playerRegistry?.GetLocalPlayer();
        var opp      = _playerRegistry?.GetAllOpponents();
        var oppStats = opp?.Count > 0 ? opp[0] : null;

        if (myStats == null) return;

        int myKills  = myStats.GetKillCount();
        int oppKills = oppStats?.GetKillCount() ?? 0;
        string result = myKills > oppKills ? "YOU WIN!" :
                        myKills < oppKills ? "YOU LOST!" : "TIE!";

        if (compResultText != null) compResultText.text = result;
        if (compStatsText  != null)
            compStatsText.text = $"Your Kills: {myKills}\n" +
                                 $"Opponent Kills: {oppKills}\n" +
                                 $"Your Level: {myStats.CurrentLevel}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВОЗВРАТ В МЕНЮ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnReturnButtonClicked()
    {
        Debug.Log("[GameOverScreenManager] Returning to menu");

        IsGameOver = false;

        // ИСПРАВЛЕНО v4 (BUG-9): снимаем ВСЕ причины паузы (LevelUp + GameOver)
        _gameState?.ForceResumeAll();

        Time.timeScale = 1f;

        // [rooms_v2] Сбрасываем флаг комнатного матча чтобы следующий
        // ranked-матч считал ELO нормально.
        RatingService.SetRoomMatch(false);

        GameModeManager.Instance?.ResetToSelecting();

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();

        if (EnemyPool.Instance != null)
            SceneManager.MoveGameObjectToScene(
                EnemyPool.Instance.gameObject, SceneManager.GetActiveScene());
        if (ExperienceOrbPool.Instance != null)
            SceneManager.MoveGameObjectToScene(
                ExperienceOrbPool.Instance.gameObject, SceneManager.GetActiveScene());

        StartCoroutine(ReloadNextFrame());
    }

    private System.Collections.IEnumerator ReloadNextFrame()
    {
        yield return null;
        yield return null;
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }
}
