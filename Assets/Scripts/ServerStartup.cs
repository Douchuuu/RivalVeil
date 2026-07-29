using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

/// <summary>
/// ServerStartup v4.3
///
/// ИСПРАВЛЕНО v4.3:
///   - Неверный маппинг ClientId в StartLiveTracking.
///     БЫЛО: { 0UL → player1_name, 1UL → player2_name }
///     В Unity Netcode dedicated server всегда имеет ClientId = 0.
///     Первый подключившийся клиент получает ClientId = 1, второй = 2.
///     LiveMatchTracker искал имя по OwnerClientId (1, 2) — не находил (ключи 0, 1).
///     СТАЛО: { 1UL → player1_name, 2UL → player2_name }
///
///   - Добавлен вызов RatingService.SetPlayerNames(1UL, name1, 2UL, name2).
///     Без этого RatingService.SendViaDirectHttp брал имя игрока через
///     GameObject.name ("Player_Pribaff(Clone)") → FastAPI 404.
///     Теперь реальные логины из /server/poll передаются в RatingService
///     и используются при сохранении результата матча.
/// </summary>
public class ServerStartup : MonoBehaviour
{
    [Header("Настройки сервера")]
    [SerializeField] private bool autoStartServer = true;
    [SerializeField] private float startDelay = 0.5f;
    [SerializeField] private int port = 0;

    [Header("FastAPI интеграция")]
    [Tooltip("Динамический URL — перезаписывается из аргумента -fastApiUrl при запуске")]
    [SerializeField] private string fastApiUrl = "http://localhost:8000";
    [SerializeField] private float pollInterval = 2f;

    [Header("Настройки сцены")]
    [SerializeField] private string gameSceneName = "BaseScene";

    // ─── СОСТОЯНИЕ ────────────────────────────────────────────────────────────
    private string _serverId;
    private bool _isRegistered = false;
    private bool _matchAssigned = false;
    private bool _matchHandled = false;
    private Coroutine _pollCoroutine;

    private LiveMatchTracker _tracker;
    private PollResponse _pendingMatchForTracking;
    private int _currentSessionId;

    // ─── DTO ──────────────────────────────────────────────────────────────────
    [System.Serializable]
    private class PollResponse
    {
        public bool has_match;
        public string type;
        public int session_id;
        public int room_id;
        public int player1_id;
        public int player2_id;
        public string player1_name;
        public string player2_name;
    }

    [System.Serializable]
    private class RegisterResponse
    {
        public bool success;
        public string server_id;
    }

    // ─── ИНИЦИАЛИЗАЦИЯ ────────────────────────────────────────────────────────

    private void Start()
    {
        ParseCommandLineArgs();

        if (!autoStartServer)
        {
            Debug.Log("[ServerStartup] Автозапуск сервера отключен.");
            return;
        }

        StartCoroutine(StartServerDelayed());
    }

    private void ParseCommandLineArgs()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        Debug.Log($"[ServerStartup] Args: {string.Join(", ", args)}");

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-fastApiUrl" && i + 1 < args.Length)
            {
                fastApiUrl = args[i + 1];
                Debug.Log($"[ServerStartup] FastAPI URL (из args): {fastApiUrl}");
            }
        }
    }

    private IEnumerator StartServerDelayed()
    {
        Debug.Log($"[ServerStartup] Ожидание {startDelay}с... FastAPI: {fastApiUrl}");
        yield return new WaitForSeconds(startDelay);

        if (NetworkManager.Singleton == null)
        {
            Debug.LogError("[ServerStartup] NetworkManager.Singleton == null!");
            yield break;
        }

        _serverId = System.Guid.NewGuid().ToString();
        Debug.Log($"[ServerStartup] Server ID: {_serverId}");

        yield return RegisterWithFastApi();

        if (!_isRegistered)
        {
            Debug.LogError("[ServerStartup] Не удалось зарегистрироваться в FastAPI!");
            yield break;
        }

        RatingService.SetFastApiUrl(fastApiUrl);
        RatingService.SetServerId(_serverId);
        Debug.Log("[ServerStartup] RatingService настроен");

        _pollCoroutine = StartCoroutine(PollForMatch());
    }

    // ─── РЕГИСТРАЦИЯ В FASTAPI ────────────────────────────────────────────────

    private IEnumerator RegisterWithFastApi()
    {
        string url = $"{fastApiUrl}/server/register?server_id={_serverId}&version=1.0";
        Debug.Log($"[ServerStartup] Регистрация: {url}");

        using var req = UnityWebRequest.PostWwwForm(url, "");
        req.timeout = 10;
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<RegisterResponse>(req.downloadHandler.text);
            _isRegistered = resp.success;
            Debug.Log("[ServerStartup] Зарегистрирован");
        }
        else
        {
            Debug.LogError($"[ServerStartup] Ошибка регистрации: {req.error} ({req.responseCode})");
            _isRegistered = false;
        }
    }

    // ─── ПОЛЛИНГ МАТЧЕЙ ───────────────────────────────────────────────────────

    private IEnumerator PollForMatch()
    {
        Debug.Log("[ServerStartup] Поллинг матчей запущен...");
        int pollCount = 0;

        while (!_matchAssigned && _isRegistered)
        {
            yield return new WaitForSeconds(pollInterval);
            pollCount++;

            if (pollCount % 5 == 1)
                Debug.Log($"[ServerStartup] Poll #{pollCount}");

            using var req = UnityWebRequest.Get($"{fastApiUrl}/server/poll?server_id={_serverId}");
            req.timeout = 10;
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<PollResponse>(req.downloadHandler.text);
                if (resp.has_match)
                {
                    Debug.Log($"[ServerStartup] МАТЧ: {resp.player1_name} vs {resp.player2_name} | session_id={resp.session_id}");
                    _matchAssigned = true;
                    StartCoroutine(HandleMatchAssignment(resp));
                }
            }
            else if (req.responseCode == 404)
            {
                Debug.LogWarning("[ServerStartup] 404 — перерегистрация...");
                yield return RegisterWithFastApi();
            }
        }
    }

    // ─── ОБРАБОТКА МАТЧА ──────────────────────────────────────────────────────

    private IEnumerator HandleMatchAssignment(PollResponse match)
    {
        if (_matchHandled)
        {
            Debug.LogWarning("[ServerStartup] HandleMatchAssignment: уже обрабатывался, пропускаю.");
            yield break;
        }
        _matchHandled = true;

        if (match.session_id <= 0)
        {
            Debug.LogError($"[ServerStartup] session_id НЕВАЛИДЕН: {match.session_id}.");
            yield return SendMatchFailed(match, "Invalid session_id from FastAPI");
            yield break;
        }

        _currentSessionId = match.session_id;
        Debug.Log($"[ServerStartup] Матч: {match.player1_name} vs {match.player2_name} | session_id={match.session_id}");

        // Ждём RelayManager
        float waitTime = 0f;
        while (RelayManager.Instance == null && waitTime < 10f)
        {
            waitTime += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        if (RelayManager.Instance == null)
        {
            Debug.LogError("[ServerStartup] ❌ RelayManager не найден!");
            yield return SendMatchFailed(match, "RelayManager not found");
            yield break;
        }

        // Создаём Relay
        string joinCode = null;
        bool relayCreated = false;

        RelayManager.Instance.CreateRelayHost(
            onSuccess: code => { joinCode = code; relayCreated = true; Debug.Log($"[ServerStartup] ✅ Relay: {code}"); },
            onError: err => { relayCreated = true; Debug.LogError($"[ServerStartup] ❌ Relay: {err}"); }
        );

        waitTime = 0f;
        while (!relayCreated && waitTime < 30f)
        {
            waitTime += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        if (string.IsNullOrEmpty(joinCode))
        {
            Debug.LogError("[ServerStartup] ❌ Relay не создан!");
            yield return SendMatchFailed(match, "Relay creation failed");
            yield break;
        }

        // Запускаем Netcode сервер
        if (port > 0)
        {
            var transport = NetworkManager.Singleton
                .GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport != null)
                transport.ConnectionData.Port = (ushort)port;
        }

        bool started = NetworkManager.Singleton.StartServer();
        if (!started)
        {
            Debug.LogError("[ServerStartup] ❌ StartServer() вернул false!");
            yield return SendMatchFailed(match, "Server start failed");
            yield break;
        }

        Debug.Log("[ServerStartup] ✅ Netcode сервер запущен");

        yield return SendMatchReady(match, joinCode);

        // Ждём загрузки сцены перед стартом трекинга
        _pendingMatchForTracking = match;
        if (NetworkManager.Singleton.SceneManager != null)
            NetworkManager.Singleton.SceneManager.OnLoadComplete += OnGameSceneLoaded;

        Debug.Log($"[ServerStartup] Загрузка сцены '{gameSceneName}'...");
        if (NetworkManager.Singleton.SceneManager != null)
        {
            var status = NetworkManager.Singleton.SceneManager.LoadScene(
                gameSceneName, LoadSceneMode.Single);
            Debug.Log(status == SceneEventProgressStatus.Started
                ? "[ServerStartup] Сцена загружается..."
                : $"[ServerStartup] Сцена не загружена: {status}");
        }
    }

    // ─── КОЛБЭК ЗАГРУЗКИ СЦЕНЫ ───────────────────────────────────────────────

    private void OnGameSceneLoaded(ulong clientId, string sceneName, LoadSceneMode mode)
    {
        if (!NetworkManager.Singleton.IsServer) return;
        if (sceneName != gameSceneName) return;

        NetworkManager.Singleton.SceneManager.OnLoadComplete -= OnGameSceneLoaded;

        if (_pendingMatchForTracking != null)
        {
            StartLiveTracking(_pendingMatchForTracking);
            _pendingMatchForTracking = null;
        }
    }

    // ─── LIVE ТРЕКИНГ ────────────────────────────────────────────────────────

    private void StartLiveTracking(PollResponse match)
    {
        // ─── ИСПРАВЛЕНО: передаём реальные логины в RatingService ────────────
        // RatingService.SendViaDirectHttp раньше брал GameObject.name
        // ("Player_Pribaff(Clone)") вместо логина в БД → FastAPI 404.
        // Теперь RatingService знает реальные имена и ищет по ним.
        //
        // ClientId 1 = первый клиент (player1), ClientId 2 = второй (player2).
        // Dedicated server в Netcode всегда = ClientId 0.
        RatingService.SetPlayerNames(1UL, match.player1_name, 2UL, match.player2_name);

        _tracker = FindFirstObjectByType<LiveMatchTracker>();

        if (_tracker == null)
        {
            Debug.LogWarning("[ServerStartup] LiveMatchTracker не найден на сцене. " +
                             "Создай GameObject 'LiveMatchTracker' и добавь скрипт.");
            return;
        }

        _tracker.SetSessionId(match.session_id);
        _tracker.SetServerId(_serverId);
        _tracker.SetFastApiUrl(fastApiUrl);

        // ИСПРАВЛЕНО: было { 0UL, 1UL } — не совпадало с реальными ClientId клиентов (1, 2).
        // Dedicated server = ClientId 0, клиенты начинаются с 1.
        _tracker.SetPlayerNames(new Dictionary<ulong, string>
        {
            { 1UL, match.player1_name ?? "Player1" },
            { 2UL, match.player2_name ?? "Player2" }
        });

        _tracker.StartTracking();

        Debug.Log($"[ServerStartup] ✅ LiveMatchTracker запущен | " +
                  $"session={match.session_id} | " +
                  $"{match.player1_name}(ClientId=1) vs {match.player2_name}(ClientId=2)");
    }

    public void StopLiveTracking()
    {
        if (_tracker != null)
        {
            _tracker.StopTracking();
            Debug.Log("[ServerStartup] LiveMatchTracker остановлен");
        }

        if (_currentSessionId > 0)
            StartCoroutine(FullMatchCleanup(_currentSessionId));
    }

    private IEnumerator FullMatchCleanup(int sessionId)
    {
        yield return SendMatchEnded(sessionId);
        yield return CleanupLiveData(sessionId);
    }

    // ─── СЛУЖЕБНЫЕ HTTP ───────────────────────────────────────────────────────

    private IEnumerator SendMatchReady(PollResponse match, string joinCode)
    {
        _currentSessionId = match.session_id;

        if (match.session_id <= 0)
        {
            Debug.LogError($"[ServerStartup] SendMatchReady: session_id={match.session_id} — не отправляю!");
            yield break;
        }

        string url = match.room_id > 0
            ? $"{fastApiUrl}/server/match_ready?server_id={_serverId}&join_code={UnityWebRequest.EscapeURL(joinCode)}&room_id={match.room_id}"
            : $"{fastApiUrl}/server/match_ready?server_id={_serverId}&join_code={UnityWebRequest.EscapeURL(joinCode)}&session_id={match.session_id}";

        Debug.Log($"[ServerStartup] match_ready: {url}");

        using var req = UnityWebRequest.PostWwwForm(url, "");
        req.timeout = 10;
        yield return req.SendWebRequest();

        Debug.Log(req.result == UnityWebRequest.Result.Success
            ? "[ServerStartup] match_ready отправлен"
            : $"[ServerStartup] match_ready ошибка: {req.error}");
    }

    private IEnumerator SendMatchFailed(PollResponse match, string error)
    {
        string url = $"{fastApiUrl}/server/match_failed?" +
                     $"server_id={_serverId}" +
                     $"&session_id={match.session_id}" +
                     $"&error={UnityWebRequest.EscapeURL(error)}";

        using var req = UnityWebRequest.PostWwwForm(url, "");
        req.timeout = 10;
        yield return req.SendWebRequest();
        Debug.Log($"[ServerStartup] match_failed отправлен: {error}");
    }

    private IEnumerator SendMatchEnded(int sessionId)
    {
        if (sessionId <= 0)
        {
            Debug.LogWarning("[ServerStartup] SendMatchEnded: session_id не задан, пропускаю.");
            yield break;
        }

        string url = $"{fastApiUrl}/server/match_ended?server_id={_serverId}&session_id={sessionId}";
        Debug.Log($"[ServerStartup] match_ended: {url}");

        using var req = UnityWebRequest.PostWwwForm(url, "");
        req.timeout = 10;
        yield return req.SendWebRequest();

        Debug.Log(req.result == UnityWebRequest.Result.Success
            ? $"[ServerStartup] match_ended OK — сессия #{sessionId} закрыта"
            : $"[ServerStartup] match_ended ошибка: {req.error}");
    }

    private IEnumerator CleanupLiveData(int sessionId)
    {
        string url = $"{fastApiUrl}/live/match/{sessionId}/cleanup";
        using var req = UnityWebRequest.Delete(url);
        req.timeout = 10;
        yield return req.SendWebRequest();

        Debug.Log(req.result == UnityWebRequest.Result.Success
            ? $"[ServerStartup] Live-данные сессии #{sessionId} очищены"
            : $"[ServerStartup] Ошибка cleanup: {req.error}");
    }

    // ─── ОТКЛЮЧЕНИЕ ───────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        if (_pollCoroutine != null)
            StopCoroutine(_pollCoroutine);

        if (NetworkManager.Singleton?.SceneManager != null)
            NetworkManager.Singleton.SceneManager.OnLoadComplete -= OnGameSceneLoaded;

        if (_tracker != null)
            _tracker.StopTracking();

        try
        {
            using var http = new System.Net.Http.HttpClient
            {
                Timeout = System.TimeSpan.FromSeconds(4)
            };

            if (_matchAssigned && _isRegistered && _currentSessionId > 0)
            {
                _ = http.PostAsync(
                    $"{fastApiUrl}/server/match_ended?server_id={_serverId}&session_id={_currentSessionId}",
                    null);
                Debug.Log($"[ServerStartup] OnDestroy: match_ended отправлен для сессии #{_currentSessionId}");
            }

            if (_currentSessionId > 0)
                _ = http.DeleteAsync($"{fastApiUrl}/live/match/{_currentSessionId}/cleanup");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[ServerStartup] OnDestroy cleanup error: {e.Message}");
        }
    }

    // ─── ПУБЛИЧНЫЙ API ────────────────────────────────────────────────────────

    public string GetServerId() => _serverId;
    public bool IsRegistered() => _isRegistered;
    public bool IsMatchAssigned() => _matchAssigned;
    public string GetFastApiUrl() => fastApiUrl;
    public int GetCurrentSessionId() => _currentSessionId;
}