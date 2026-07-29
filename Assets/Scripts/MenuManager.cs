using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// MenuManager v4.4 — rooms_v2 fix (загрузка BaseScene сразу после StartHost)
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО v4.4:
/// ══════════════════════════════════════════════════════════════════════
///   [CRITICAL] CreateRoomFlow: убран OnRoomGuestConnected callback.
///              После StartHost() теперь сразу вызывается
///              NetworkManager.SceneManager.LoadScene("BaseScene") —
///              точно как в OnSinglePlayerClicked.
///              Причина бага: хост ждал гостя в MenuScene без DI
///              (InjectionProvider.Container == null), GameMode=Selecting
///              в MenuScene показывал CharacterSelectUI без загрузки мира.
///
///   [FIX]     Убраны OnRoomGuestConnected и RoomAutoClose.
///              Теперь хост грузит BaseScene немедленно, гость получает
///              BaseScene автоматически через NGO-синхронизацию при подключении.
///              Нормальный поток: оба игрока в BaseScene → Selecting →
///              CharacterSelect → SetPlayerReadyServerRpc → ShadowMultiplayer.
///
///   [FIX]     Убрана двойная блокировка кнопок: createRoomBtn блокируется
///              при старте и разблокируется только при ошибке.
///
/// ══════════════════════════════════════════════════════════════════════
/// НОВОЕ v4.3 (rooms_v2):
/// ══════════════════════════════════════════════════════════════════════
///   [NEW]  OnCreateRoomClicked — хост сам создаёт Relay.
///   [NEW]  JoinRoomFlow — гость поллит /room/find → JoinRelayAsClient.
///   [NEW]  _currentRoomCode, _isRoomMatch — состояние комнаты.
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class MenuManager : MonoBehaviour
{
    // AUTH
    [Header("=== AUTH ПАНЕЛЬ ===")]
    [SerializeField] private GameObject authPanel;
    [SerializeField] private TMP_InputField usernameInput;
    [SerializeField] private TMP_InputField passwordInput;
    [SerializeField] private Toggle rememberToggle;
    [SerializeField] private Button loginButton;
    [SerializeField] private Button registerButton;
    [SerializeField] private Button offlineButton;
    [SerializeField] private TextMeshProUGUI authStatusText;

    // ГЛАВНОЕ МЕНЮ
    [Header("=== ГЛАВНОЕ МЕНЮ ===")]
    [SerializeField] private GameObject mainMenuPanel;
    [SerializeField] private TextMeshProUGUI playerInfoText;
    [SerializeField] private Button singlePlayerBtn;
    [SerializeField] private Button competitiveBtn;
    [SerializeField] private Button leaderboardBtn;
    [SerializeField] private Button exitBtn;

    // МАТЧМЕЙКИНГ
    [Header("=== МАТЧМЕЙКИНГ ===")]
    [SerializeField] private GameObject matchmakingPanel;
    [SerializeField] private Button findMatchBtn;
    [SerializeField] private Button cancelMatchBtn;
    [SerializeField] private TextMeshProUGUI matchStatusText;
    [SerializeField] private Button createRoomBtn;
    [SerializeField] private TextMeshProUGUI roomCodeDisplay;
    [SerializeField] private TMP_InputField roomCodeInput;
    [SerializeField] private Button joinRoomBtn;
    [SerializeField] private Button backFromMatchBtn;

    // ЛИДЕРБОРД
    [Header("=== ЛИДЕРБОРД ===")]
    [SerializeField] private GameObject leaderboardPanel;
    [SerializeField] private Transform leaderboardContent;
    [SerializeField] private Button closeLeaderboardBtn;

    // СЦЕНЫ
    [Header("=== СЦЕНЫ ===")]
    [SerializeField] private string baseSceneName = "BaseScene";
    [SerializeField] private string menuSceneName = "MenuScene";

    // СОСТОЯНИЕ
    private enum GameModeChoice { SinglePlayer, Competitive }
    private GameModeChoice _chosenMode = GameModeChoice.SinglePlayer;
    private bool _isOffline = false;

    // [rooms_v2] Состояние комнаты
    private string _currentRoomCode = "";
    private bool   _isRoomMatch     = false;

    private const string PREF_USERNAME = "rv_username";
    private const string PREF_PASSWORD = "rv_password";
    private const string PREF_REMEMBER = "rv_remember";

    // ─────────────────────────────────────────────────────────────────────────
    // INIT
    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        Debug.Log("[MenuManager] Start.");
        BindButtons();
        ShowPanel(authPanel);
        LoadSavedCredentials();

        if (RelayManager.Instance != null)
        {
            RelayManager.Instance.InitializeAsync(ok =>
            {
                Debug.Log($"[MenuManager] RelayManager init: {(ok ? "ok" : "failed")}");
            });
        }
        else
        {
            Debug.LogWarning("[MenuManager] RelayManager.Instance == null.");
        }
    }

    private void BindButtons()
    {
        loginButton?.onClick.AddListener(OnLoginClicked);
        registerButton?.onClick.AddListener(OnRegisterClicked);
        offlineButton?.onClick.AddListener(OnOfflineClicked);

        singlePlayerBtn?.onClick.AddListener(OnSinglePlayerClicked);
        competitiveBtn?.onClick.AddListener(OnCompetitiveClicked);
        leaderboardBtn?.onClick.AddListener(OnLeaderboardClicked);
        exitBtn?.onClick.AddListener(Application.Quit);

        findMatchBtn?.onClick.AddListener(OnFindMatchClicked);
        cancelMatchBtn?.onClick.AddListener(OnCancelMatchClicked);
        createRoomBtn?.onClick.AddListener(OnCreateRoomClicked);
        joinRoomBtn?.onClick.AddListener(OnJoinRoomClicked);
        backFromMatchBtn?.onClick.AddListener(OnBackFromMatch);

        closeLeaderboardBtn?.onClick.AddListener(() => ShowPanel(mainMenuPanel));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПАНЕЛИ
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowPanel(GameObject panel)
    {
        authPanel?.SetActive(false);
        mainMenuPanel?.SetActive(false);
        matchmakingPanel?.SetActive(false);
        leaderboardPanel?.SetActive(false);
        panel?.SetActive(true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // АВТОРИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnLoginClicked()
    {
        string username = usernameInput?.text.Trim() ?? "";
        string password = passwordInput?.text ?? "";

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        { SetAuthStatus("Введи имя и пароль", Color.red); return; }

        SetAuthStatus("Вход...", Color.yellow);
        SetAuthInteractable(false);

        if (BackendService.Instance == null)
        {
            SetAuthStatus("Сервер недоступен. Попробуй оффлайн режим.", Color.red);
            SetAuthInteractable(true);
            return;
        }

        BackendService.Instance.Login(username, password,
            onSuccess: () =>
            {
                SaveCredentials(username, password);
                _isOffline = false;
                SetAuthStatus("", Color.white);
                SetAuthInteractable(true);
                EnterMainMenu();
            },
            onError: (err) =>
            {
                SetAuthStatus(err, Color.red);
                SetAuthInteractable(true);
            }
        );
    }

    private void OnRegisterClicked()
    {
        string username = usernameInput?.text.Trim() ?? "";
        string password = passwordInput?.text ?? "";

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        { SetAuthStatus("Введи имя и пароль", Color.red); return; }

        if (password.Length < 6)
        { SetAuthStatus("Пароль минимум 6 символов", Color.red); return; }

        SetAuthStatus("Регистрация...", Color.yellow);
        SetAuthInteractable(false);

        if (BackendService.Instance == null)
        {
            SetAuthStatus("Сервер недоступен.", Color.red);
            SetAuthInteractable(true);
            return;
        }

        BackendService.Instance.Register(username, password,
            onSuccess: () =>
            {
                SetAuthStatus("Успешно! Выполняю вход...", Color.green);
                BackendService.Instance.Login(username, password,
                    onSuccess: () =>
                    {
                        SaveCredentials(username, password);
                        _isOffline = false;
                        SetAuthStatus("", Color.white);
                        SetAuthInteractable(true);
                        EnterMainMenu();
                    },
                    onError: (err) =>
                    {
                        SetAuthStatus(err, Color.red);
                        SetAuthInteractable(true);
                    }
                );
            },
            onError: (err) => { SetAuthStatus(err, Color.red); SetAuthInteractable(true); }
        );
    }

    private void OnOfflineClicked()
    {
        Debug.Log("[MenuManager] Offline mode.");
        _isOffline = true;
        EnterMainMenu();
    }

    private void SetAuthStatus(string msg, Color color)
    {
        if (authStatusText == null) return;
        authStatusText.text  = msg;
        authStatusText.color = color;
    }

    private void SetAuthInteractable(bool state)
    {
        if (loginButton    != null) loginButton.interactable    = state;
        if (registerButton != null) registerButton.interactable = state;
        if (offlineButton  != null) offlineButton.interactable  = state;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СОХРАНЕНИЕ ПАРОЛЯ
    // ─────────────────────────────────────────────────────────────────────────

    private void SaveCredentials(string username, string password)
    {
        if (rememberToggle != null && rememberToggle.isOn)
        {
            PlayerPrefs.SetString(PREF_USERNAME, username);
            PlayerPrefs.SetString(PREF_PASSWORD, password);
            PlayerPrefs.SetInt(PREF_REMEMBER, 1);
        }
        else
        {
            PlayerPrefs.DeleteKey(PREF_USERNAME);
            PlayerPrefs.DeleteKey(PREF_PASSWORD);
            PlayerPrefs.SetInt(PREF_REMEMBER, 0);
        }
        PlayerPrefs.Save();
    }

    private void LoadSavedCredentials()
    {
        if (PlayerPrefs.GetInt(PREF_REMEMBER, 0) != 1) return;
        string u = PlayerPrefs.GetString(PREF_USERNAME, "");
        string p = PlayerPrefs.GetString(PREF_PASSWORD, "");
        if (!string.IsNullOrEmpty(u) && usernameInput != null) usernameInput.text = u;
        if (!string.IsNullOrEmpty(p) && passwordInput != null) passwordInput.text = p;
        if (rememberToggle != null) rememberToggle.isOn = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ГЛАВНОЕ МЕНЮ
    // ─────────────────────────────────────────────────────────────────────────

    private void EnterMainMenu()
    {
        ShowPanel(mainMenuPanel);

        if (playerInfoText != null)
        {
            if (_isOffline)
                playerInfoText.text = "<color=#AAAAAA>Оффлайн режим</color>";
            else if (BackendService.Instance?.IsLoggedIn == true)
                playerInfoText.text =
                    $"Игрок: <color=#FFD700>{BackendService.Instance.PlayerName}</color>" +
                    $"  |  Рейтинг: <color=#4FC3F7>{BackendService.Instance.PlayerRating}</color>";
        }

        if (competitiveBtn != null) competitiveBtn.interactable = !_isOffline;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КНОПКА НАЗАД из матчмейкинга
    // ─────────────────────────────────────────────────────────────────────────

    private void OnBackFromMatch()
    {
        if (_isRoomMatch)
        {
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
        }
        ShowPanel(mainMenuPanel);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РЕЖИМ ИГРЫ
    // ─────────────────────────────────────────────────────────────────────────

    private void OnSinglePlayerClicked()
    {
        Debug.Log("[MenuManager] OnSinglePlayerClicked...");

        GameStartConfig.IsSinglePlayer = true;

        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[MenuManager] ❌ NetworkManager not found!");
            return;
        }

        if (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
        {
            Debug.Log("[MenuManager] Stopping previous connection...");
            NetworkManager.Singleton.Shutdown();
        }

        var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
        if (transport == null)
        {
            Debug.LogError("[MenuManager] ❌ UnityTransport not found!");
            return;
        }

        bool started   = false;
        ushort usedPort = 7777;
        for (ushort port = 7777; port <= 7800; port++)
        {
            transport.SetConnectionData("127.0.0.1", port);
            try
            {
                started = NetworkManager.Singleton.StartHost();
                if (started) { usedPort = port; break; }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[MenuManager] Port {port} busy: {ex.Message}");
            }
        }

        if (!started)
        {
            Debug.LogError("[MenuManager] ❌ Failed to start host on ports 7777–7800!");
            SetAuthStatus("Ошибка: все порты заняты. Перезапустите приложение.", Color.red);
            return;
        }

        Debug.Log($"[MenuManager] ✅ Host started on port {usedPort}");

        if (NetworkManager.Singleton.SceneManager != null)
            NetworkManager.Singleton.SceneManager.LoadScene(baseSceneName, LoadSceneMode.Single);
    }

    private void OnCompetitiveClicked()
    {
        Debug.Log("[MenuManager] OnCompetitiveClicked.");
        GameStartConfig.IsSinglePlayer = false;
        ShowPanel(matchmakingPanel);
    }

    public void OnMatchFound(string joinCode)
    {
        Debug.Log($"[MenuManager] Match found, joinCode={joinCode}");
        StartCoroutine(ConnectAndShowCharSelect(joinCode));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // МАТЧМЕЙКИНГ (Ranked)
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFindMatchClicked()
    {
        if (BackendService.Instance == null)
        { SetMatchStatus("Нет подключения к серверу", Color.red); return; }

        SetMatchStatus("Ищем оппонента...", Color.yellow);
        if (findMatchBtn   != null) findMatchBtn.interactable   = false;
        if (cancelMatchBtn != null) cancelMatchBtn.interactable = true;

        BackendService.Instance.StartMatchmaking(
            onMatchFound: (result) =>
            {
                SetMatchStatus("Оппонент найден! Подключение...", Color.green);
                if (findMatchBtn != null) findMatchBtn.interactable = true;
                if (BackendService.Instance != null)
                    BackendService.Instance.OpponentName = result.opponent_name;
                StartCoroutine(ConnectAndShowCharSelect(result.join_code));
            },
            onWaiting: (queuePos) =>
            {
                string msg = queuePos == 0
                    ? "Матч найден, сервер готовит соединение..."
                    : $"Ищем противника... позиция #{queuePos}";
                SetMatchStatus(msg, Color.yellow);
            },
            onError: (err) =>
            {
                SetMatchStatus($"Ошибка: {err}", Color.red);
                if (findMatchBtn   != null) findMatchBtn.interactable   = true;
                if (cancelMatchBtn != null) cancelMatchBtn.interactable = false;
            }
        );
    }

    private IEnumerator ConnectAndShowCharSelect(string joinCode)
    {
        bool done = false, connected = false;

        if (RelayManager.Instance != null && !string.IsNullOrEmpty(joinCode))
        {
            SetMatchStatus("Подключение к Relay...", Color.yellow);
            RelayManager.Instance.JoinRelayAsClient(joinCode, ok => {
                connected = ok;
                done = true;
            });
            yield return new WaitUntil(() => done);
        }
        else
        {
            connected = true;
        }

        if (connected)
        {
            SetMatchStatus("Готово! Входим в игру...", Color.green);
            yield return new WaitForSeconds(0.5f);
            _chosenMode = GameModeChoice.Competitive;
            ShowCharSelect();
        }
        else
        {
            SetMatchStatus("Ошибка подключения к Relay", Color.red);
            if (findMatchBtn != null) findMatchBtn.interactable = true;
        }
    }

    private void OnCancelMatchClicked()
    {
        BackendService.Instance?.StopMatchmaking();
        SetMatchStatus("Поиск отменён", Color.gray);
        if (findMatchBtn   != null) findMatchBtn.interactable   = true;
        if (cancelMatchBtn != null) cancelMatchBtn.interactable = false;
    }

    private void ShowCharSelect()
    {
        Debug.Log($"[MenuManager] ShowCharSelect: _isOffline={_isOffline}, _chosenMode={_chosenMode}");

        if (_isOffline || _chosenMode == GameModeChoice.SinglePlayer)
        {
            SceneManager.LoadScene(baseSceneName);
        }
        else
        {
            SetMatchStatus("Синхронизация с сервером...", Color.green);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КОМНАТЫ (Play with Friend) — rooms_v2
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Хост создаёт комнату и Relay.
    ///
    /// ИСПРАВЛЕНО v4.4: после StartHost() сразу грузим BaseScene через
    /// NetworkManager.SceneManager.LoadScene (идентично SinglePlayer).
    /// Хост переходит в BaseScene → GameMode=Selecting → CharacterSelectUI.
    /// Гость подключается → NGO синхронизирует BaseScene автоматически.
    /// Оба игрока выбирают персонажа → SetPlayerReadyServerRpc → ShadowMultiplayer.
    /// </summary>
    private void OnCreateRoomClicked()
    {
        if (BackendService.Instance == null)
        { SetMatchStatus("Нет подключения к серверу", Color.red); return; }
        if (RelayManager.Instance == null)
        { SetMatchStatus("RelayManager не готов", Color.red); return; }

        if (createRoomBtn != null) createRoomBtn.interactable = false;
        SetMatchStatus("Создаём комнату...", Color.yellow);
        if (roomCodeDisplay != null) roomCodeDisplay.text = "";

        _isRoomMatch = true;
        RatingService.SetRoomMatch(true);

        StartCoroutine(CreateRoomFlow());
    }

    private IEnumerator CreateRoomFlow()
    {
        // ── Шаг 1: создаём комнату в БД ──────────────────────────────────────
        bool   roomDone  = false;
        string roomCode  = "";
        string roomError = "";

        StartCoroutine(BackendService.Instance.CreateRoom(
            onSuccess: (resp) => { roomCode = resp.code; roomDone = true; },
            onError:   (err)  => { roomError = err; roomDone = true; }
        ));

        yield return new WaitUntil(() => roomDone);

        if (!string.IsNullOrEmpty(roomError) || string.IsNullOrEmpty(roomCode))
        {
            SetMatchStatus($"Ошибка создания комнаты: {roomError}", Color.red);
            if (createRoomBtn != null) createRoomBtn.interactable = true;
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
            yield break;
        }

        _currentRoomCode = roomCode;

        if (roomCodeDisplay != null)
            roomCodeDisplay.text = $"Код комнаты: <b>{roomCode}</b>";
        SetMatchStatus("Создаём соединение...", Color.yellow);

        // ── Шаг 2: создаём Relay-аллокацию (StartHost внутри CreateRelayHost) ─
        string relayJoinCode = null;
        bool   relayDone     = false;

        RelayManager.Instance.CreateRelayHost(
            onSuccess: (code) => { relayJoinCode = code; relayDone = true; },
            onError:   (err)  => { relayDone = true; Debug.LogError($"[MenuManager] Relay error: {err}"); }
        );

        float waitRelay = 0f;
        yield return new WaitUntil(() =>
        {
            waitRelay += Time.deltaTime;
            return relayDone || waitRelay > 30f;
        });

        if (string.IsNullOrEmpty(relayJoinCode))
        {
            SetMatchStatus("Ошибка создания Relay", Color.red);
            if (createRoomBtn != null) createRoomBtn.interactable = true;
            StartCoroutine(BackendService.Instance.CloseRoom(roomCode));
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
            yield break;
        }

        // ── Шаг 2.5: StartHost ───────────────────────────────────────────────
        // Relay настроен внутри CreateRelayHost. Теперь стартуем хост.
        if (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
        {
            Debug.Log("[MenuManager] Stopping previous NetworkManager before StartHost...");
            NetworkManager.Singleton.Shutdown();
            yield return null;
        }

        bool hostStarted = NetworkManager.Singleton.StartHost();
        if (!hostStarted)
        {
            Debug.LogError("[MenuManager] ❌ StartHost() failed for room!");
            SetMatchStatus("Ошибка запуска хоста", Color.red);
            if (createRoomBtn != null) createRoomBtn.interactable = true;
            StartCoroutine(BackendService.Instance.CloseRoom(roomCode));
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
            yield break;
        }

        Debug.Log("[MenuManager] ✅ StartHost() succeeded for room");
        GameStartConfig.IsSinglePlayer = false;

        // ── Шаг 3: сохраняем Relay join_code в БД ────────────────────────────
        // Гость поллит /room/find пока has_relay не станет true.
        bool   relaySetDone  = false;
        string relaySetError = "";

        StartCoroutine(BackendService.Instance.SetRoomRelay(roomCode, relayJoinCode,
            onSuccess: ()    => { relaySetDone = true; },
            onError:   (err) => { relaySetError = err; relaySetDone = true; }
        ));

        yield return new WaitUntil(() => relaySetDone);

        if (!string.IsNullOrEmpty(relaySetError))
        {
            SetMatchStatus("Ошибка сохранения Relay кода", Color.red);
            Debug.LogError($"[MenuManager] SetRoomRelay failed: {relaySetError}");
            if (createRoomBtn != null) createRoomBtn.interactable = true;
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
            yield break;
        }

        Debug.Log($"[MenuManager] ✅ Room relay saved. Code={roomCode}. Loading BaseScene...");

        // ── Шаг 4: СРАЗУ грузим BaseScene ────────────────────────────────────
        //
        // ИСПРАВЛЕНИЕ v4.4:
        //   Раньше здесь был OnRoomGuestConnected callback, и хост ждал гостя
        //   в MenuScene. Это вызывало: InjectionProvider.Container==null,
        //   PlayerPrefab без DI, CharacterSelectUI без BaseScene.
        //
        //   Теперь: хост грузит BaseScene СРАЗУ (как SinglePlayer).
        //   - Хост видит CharacterSelect в BaseScene (нормальный DI)
        //   - Гость подключается позже → NGO автоматически синхронизирует
        //     BaseScene на гостя при подключении
        //   - Оба в BaseScene → Selecting → выбор персонажа → SetPlayerReadyServerRpc
        //
        if (NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.LoadScene(baseSceneName, LoadSceneMode.Single);
        }
        else
        {
            Debug.LogError("[MenuManager] ❌ SceneManager == null после StartHost!");
            SetMatchStatus("Ошибка загрузки сцены", Color.red);
        }
    }

    /// <summary>
    /// Гость поллит /room/find пока has_relay не станет true,
    /// затем JoinRelayAsClient. NGO синхронизирует BaseScene автоматически.
    /// </summary>
    private void OnJoinRoomClicked()
    {
        if (BackendService.Instance == null || roomCodeInput == null) return;

        string code = roomCodeInput.text.Trim().ToUpper();
        if (string.IsNullOrEmpty(code))
        { SetMatchStatus("Введи код комнаты", Color.red); return; }

        if (joinRoomBtn != null) joinRoomBtn.interactable = false;
        SetMatchStatus("Ищем комнату...", Color.yellow);

        _isRoomMatch = true;
        RatingService.SetRoomMatch(true);

        StartCoroutine(JoinRoomFlow(code));
    }

    private IEnumerator JoinRoomFlow(string code)
    {
        const float POLL_INTERVAL = 2f;
        const float POLL_TIMEOUT  = 5f * 60f; // 5 минут (срок жизни пустой комнаты)
        float elapsed  = 0f;
        string joinCode  = "";
        string hostName  = "";

        SetMatchStatus($"Ищем комнату {code}...", Color.yellow);

        // Поллим пока хост не сохранит Relay join_code (has_relay = true)
        while (string.IsNullOrEmpty(joinCode) && elapsed < POLL_TIMEOUT)
        {
            bool   done = false;
            string err  = "";

            StartCoroutine(BackendService.Instance.FindRoom(code,
                onSuccess: (resp) =>
                {
                    if (resp.has_relay && !string.IsNullOrEmpty(resp.join_code))
                    {
                        joinCode = resp.join_code;
                        hostName = resp.host_name;
                    }
                    done = true;
                },
                onError: (e) => { err = e; done = true; }
            ));

            yield return new WaitUntil(() => done);

            if (!string.IsNullOrEmpty(err))
            {
                SetMatchStatus($"Ошибка: {err}", Color.red);
                if (joinRoomBtn != null) joinRoomBtn.interactable = true;
                _isRoomMatch = false;
                RatingService.SetRoomMatch(false);
                yield break;
            }

            if (string.IsNullOrEmpty(joinCode))
            {
                SetMatchStatus($"Ждём хоста... ({code})", Color.yellow);
                yield return new WaitForSeconds(POLL_INTERVAL);
                elapsed += POLL_INTERVAL;
            }
        }

        if (string.IsNullOrEmpty(joinCode))
        {
            SetMatchStatus("Комната не найдена или истекла (5 мин)", Color.red);
            if (joinRoomBtn != null) joinRoomBtn.interactable = true;
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
            yield break;
        }

        SetMatchStatus($"Подключение к {hostName}...", Color.yellow);

        // Подключаемся к Relay хоста (StartClient внутри JoinRelayAsClient)
        bool relayDone = false;
        bool relayOk   = false;

        RelayManager.Instance.JoinRelayAsClient(joinCode, ok =>
        {
            relayOk   = ok;
            relayDone = true;
        });

        yield return new WaitUntil(() => relayDone);

        if (!relayOk)
        {
            SetMatchStatus("Ошибка подключения к Relay", Color.red);
            if (joinRoomBtn != null) joinRoomBtn.interactable = true;
            _isRoomMatch = false;
            RatingService.SetRoomMatch(false);
            yield break;
        }

        SetMatchStatus("Подключено! Ждём загрузки сцены...", Color.green);
        Debug.Log("[MenuManager] ✅ Guest connected to Relay. NGO will sync BaseScene automatically.");
        // NGO синхронизирует сцену от хоста — ничего делать не нужно
    }

    private void SetMatchStatus(string msg, Color color)
    {
        if (matchStatusText == null) return;
        matchStatusText.text  = msg;
        matchStatusText.color = color;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЛИДЕРБОРД
    // ─────────────────────────────────────────────────────────────────────────

    private void OnLeaderboardClicked()
    {
        ShowPanel(leaderboardPanel);

        if (leaderboardContent != null)
            foreach (Transform child in leaderboardContent)
                Destroy(child.gameObject);

        AddLeaderboardRow("#", "Игрок", "Рейтинг", "Убийств", isHeader: true);

        if (BackendService.Instance == null)
        {
            AddLeaderboardRow("—", "Нет подключения к серверу", "—", "—");
            return;
        }

        StartCoroutine(BackendService.Instance.GetLeaderboard(
            onSuccess: (response) =>
            {
                if (response.entries == null || response.entries.Length == 0)
                {
                    AddLeaderboardRow("—", "Список пуст", "—", "—");
                    return;
                }
                for (int i = 0; i < response.entries.Length; i++)
                {
                    var e = response.entries[i];
                    string medal = i == 0 ? "1." : i == 1 ? "2." : i == 2 ? "3." : (i + 1).ToString();
                    AddLeaderboardRow(medal, e.username, e.rating.ToString(), e.total_kills.ToString());
                }
            },
            onError: (err) => AddLeaderboardRow("—", $"Ошибка: {err}", "—", "—")
        ));
    }

    private void AddLeaderboardRow(string rank, string name, string rating,
                                    string kills, bool isHeader = false)
    {
        if (leaderboardContent == null) return;

        var rowGO  = new GameObject("Row", typeof(RectTransform));
        rowGO.transform.SetParent(leaderboardContent, false);

        var layout = rowGO.AddComponent<HorizontalLayoutGroup>();
        layout.childForceExpandWidth  = false;
        layout.childForceExpandHeight = false;
        layout.spacing = 10;
        layout.padding = new RectOffset(10, 10, 4, 4);

        rowGO.GetComponent<RectTransform>().sizeDelta = new Vector2(0, 40);
        rowGO.AddComponent<Image>().color = isHeader
            ? new Color(0.15f, 0.15f, 0.25f)
            : new Color(0.10f, 0.10f, 0.18f, 0.8f);

        AddRowCell(rowGO.transform, rank,   80,  isHeader);
        AddRowCell(rowGO.transform, name,   260, isHeader);
        AddRowCell(rowGO.transform, rating, 120, isHeader);
        AddRowCell(rowGO.transform, kills,  120, isHeader);
    }

    private void AddRowCell(Transform parent, string text, float width, bool isBold)
    {
        var go   = new GameObject("Cell", typeof(RectTransform));
        go.transform.SetParent(parent, false);
        go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 36);

        var tmp       = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.fontSize  = isBold ? 16 : 15;
        tmp.fontStyle = isBold ? FontStyles.Bold : FontStyles.Normal;
        tmp.color     = isBold ? new Color(1f, 0.8f, 0f) : Color.white;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
    }
}
