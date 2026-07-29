using UnityEngine;
using System;
using System.Collections;
using System.Text;
using UnityEngine.Networking;

/// <summary>
/// BackendService v2.3 — rooms_v2 patch
///
/// ══════════════════════════════════════════════════════════════════════
/// НОВОЕ v2.3 (rooms_v2):
/// ══════════════════════════════════════════════════════════════════════
///   [NEW]  RoomResponse.has_relay — true когда хост выставил Relay join_code.
///   [NEW]  SetRoomRelay(code, joinCode) — хост сохраняет Relay код в БД.
///   [NEW]  CloseRoom(code) — хост закрывает комнату (при старте или выходе).
///   [NEW]  PostMatchResult теперь принимает isRoomMatch — если true,
///          сервер сохраняет матч без изменения ELO.
///   [NEW]  MatchResultRequest.is_room_match — поле для JSON-тела.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v2.2:
/// ══════════════════════════════════════════════════════════════════════
///   [CRITICAL] Защита от zombie join_code в MatchmakingCoroutine.
///   [MEDIUM]   StopMatchmaking вызывает /matchmaking/leave.
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class BackendService : MonoBehaviour
{
    public static BackendService Instance { get; private set; }

    [Header("=== Настройки сервера ===")]
    [Tooltip("URL FastAPI. Для Cloudflare туннеля: https://xxxx.trycloudflare.com")]
    [SerializeField] private string serverUrl = "http://localhost:8000";
    [SerializeField] private float requestTimeout = 15f;

    [Header("=== Gist URL Configuration ===")]
    [Tooltip("Raw URL Gist для получения динамического серверного URL")]
    [SerializeField] private string gistRawUrl = "https://gist.githubusercontent.com/Douchuuu/f33d3895db2ed6966728ad38f29bc83e/raw/rivalveil_url.txt";

    private const float GIST_FETCH_TIMEOUT = 10f;
    private bool _gistUrlFetched = false;

    private string _authToken;
    private string _playerId;
    private string _playerName;
    private int _playerRating;
    private int _playerWins;
    private int _playerLosses;
    private bool _isLoggedIn = false;
    private bool _matchmakingActive = false;
    private Coroutine _matchmakingCoroutine;

    private string _lastUsedJoinCode = "";

    public event Action OnLoggedIn;
    public event Action OnLoggedOut;

    public bool IsLoggedIn => _isLoggedIn;
    public string PlayerName => _playerName;
    public string PlayerRating_str => _playerRating.ToString();
    public int PlayerRating => _playerRating;
    public string PlayerId => _playerId;
    public string OpponentName { get; set; }
    public int OpponentRating { get; set; } = 1000;
    public int Rating => _playerRating;
    public bool IsGistUrlFetched => _gistUrlFetched;

    [Serializable] private class AuthBody { public string username; public string password; }
    [Serializable] private class LoginResp { public string token; public string username; public int rating; public int wins; public int losses; }
    [Serializable] private class StatusResp { public bool found; public string join_code; public string opponent_name; public int opponent_rating; public int queue_position; public string status; public string message; }
    [Serializable] private class RegisterResp { public bool success; public string message; }
    [Serializable] private class CreateRoomResp { public bool success; public string code; public string status; }

    // [rooms_v2] RoomFindResp — добавлено has_relay
    [Serializable] private class RoomFindResp
    {
        public bool   found;
        public string code;
        public string join_code;
        public string host_name;
        public string status;
        public bool   has_relay;   // true когда хост уже выставил Relay join_code
    }

    [Serializable] private class LeaderboardResp { public LeaderboardEntry[] entries; }

    // [rooms_v2] MatchResultRequest — добавлено is_room_match
    [System.Serializable]
    private class MatchResultRequest
    {
        public string winner_name;
        public string loser_name;
        public int    winner_kills;
        public int    loser_kills;
        public int    winner_level;
        public int    loser_level;
        public int    duration_sec;
        public bool   is_technical;
        public bool   is_room_match;   // [rooms_v2] если true — ELO не меняется
    }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("[BackendService] Instance created");
            StartCoroutine(FetchServerUrlFromGist());
        }
        else if (Instance != this)
        {
            Debug.LogWarning("[BackendService] Duplicate destroyed");
            Destroy(gameObject);
        }
    }

    private IEnumerator FetchServerUrlFromGist()
    {
        string url = gistRawUrl + "?t=" + System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Debug.Log("[BackendService] Fetching URL from Gist...");

        using var req = UnityWebRequest.Get(url);
        req.timeout = (int)GIST_FETCH_TIMEOUT;
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            string fetchedUrl = req.downloadHandler.text.Trim();
            if (!string.IsNullOrEmpty(fetchedUrl) && fetchedUrl.StartsWith("https://"))
            {
                SetServerUrl(fetchedUrl);
                _gistUrlFetched = true;
                Debug.Log("[BackendService] URL from Gist set: " + serverUrl);
            }
            else
            {
                Debug.LogWarning("[BackendService] Invalid URL from Gist: '" + fetchedUrl + "'. Using fallback: " + serverUrl);
            }
        }
        else
        {
            Debug.LogWarning("[BackendService] Failed to fetch URL from Gist: " + req.error + ". Using fallback: " + serverUrl);
        }
    }

    public void SetServerUrl(string url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            serverUrl = url;
            Debug.Log("[BackendService] URL set: " + serverUrl);
        }
    }

    // --- АВТОРИЗАЦИЯ ---------------------------------------------------------

    public void Login(string username, string password, Action onSuccess, Action<string> onError)
        => StartCoroutine(LoginCoroutine(username, password, onSuccess, onError));

    private IEnumerator LoginCoroutine(string username, string password, Action onSuccess, Action<string> onError)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            onError?.Invoke("Invalid username or password");
            yield break;
        }

        var body = new AuthBody { username = username.Trim(), password = password };
        string json = JsonUtility.ToJson(body);

        using var req = new UnityWebRequest(serverUrl + "/auth/login", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = (int)requestTimeout;

        Debug.Log("[BackendService] Sending request to " + serverUrl + "/auth/login");
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<LoginResp>(req.downloadHandler.text);
            _authToken   = resp.token;
            _playerName  = resp.username;
            _playerRating = resp.rating;
            _playerWins  = resp.wins;
            _playerLosses = resp.losses;
            _playerId    = resp.username;
            _isLoggedIn  = true;

            Debug.Log("[BackendService] Login: " + _playerName + " (" + _playerRating + " ELO, " + _playerWins + "W/" + _playerLosses + "L)");
            OnLoggedIn?.Invoke();
            onSuccess?.Invoke();
        }
        else
        {
            Debug.LogError("[BackendService] Login error: " + req.error + ", code: " + req.responseCode);
            string errorMsg = req.responseCode == 401
                ? "Invalid login or password"
                : "Server error: " + req.error;
            onError?.Invoke(errorMsg);
        }
    }

    public void Register(string username, string password, Action onSuccess, Action<string> onError)
        => StartCoroutine(RegisterCoroutine(username, password, onSuccess, onError));

    private IEnumerator RegisterCoroutine(string username, string password, Action onSuccess, Action<string> onError)
    {
        if (string.IsNullOrEmpty(username) || username.Length < 3)
        {
            onError?.Invoke("Username minimum 3 characters");
            yield break;
        }

        if (password.Length < 6)
        {
            onError?.Invoke("Password minimum 6 characters");
            yield break;
        }

        var body = new AuthBody { username = username.Trim(), password = password };
        string json = JsonUtility.ToJson(body);

        using var req = new UnityWebRequest(serverUrl + "/auth/register", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = (int)requestTimeout;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log("[BackendService] Registration: " + username);
            onSuccess?.Invoke();
        }
        else
        {
            Debug.LogError("[BackendService] Registration error: " + req.error + ", code: " + req.responseCode);
            string errorMsg = req.responseCode == 400
                ? "Player already exists"
                : "Server error: " + req.error;
            onError?.Invoke(errorMsg);
        }
    }

    public void Logout()
    {
        if (_isLoggedIn && !string.IsNullOrEmpty(_authToken))
            StartCoroutine(LogoutCoroutine());

        _authToken    = null;
        _playerId     = null;
        _playerName   = null;
        _playerRating = 0;
        _playerWins   = 0;
        _playerLosses = 0;
        _isLoggedIn   = false;
        _lastUsedJoinCode = "";
        OnLoggedOut?.Invoke();
    }

    private IEnumerator LogoutCoroutine()
    {
        using var req = UnityWebRequest.PostWwwForm(serverUrl + "/auth/logout", "");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;
        yield return req.SendWebRequest();
        Debug.Log("[BackendService] Logout completed");
    }

    public string GetToken() => _authToken ?? "";

    // --- МАТЧМЕЙКИНГ --------------------------------------------------------

    public void StartMatchmaking(
        Action<MatchmakingResult> onMatchFound,
        Action<int> onWaiting,
        Action<string> onError)
    {
        if (_matchmakingActive)
        {
            Debug.LogWarning("[BackendService] Matchmaking already active");
            return;
        }

        _matchmakingActive = true;
        _lastUsedJoinCode  = "";
        _matchmakingCoroutine = StartCoroutine(MatchmakingCoroutine(onMatchFound, onWaiting, onError));
    }

    private IEnumerator MatchmakingCoroutine(
        Action<MatchmakingResult> onMatchFound,
        Action<int> onWaiting,
        Action<string> onError)
    {
        using var joinReq = UnityWebRequest.PostWwwForm(serverUrl + "/matchmaking/join", "");
        joinReq.SetRequestHeader("Authorization", "Bearer " + _authToken);
        joinReq.timeout = (int)requestTimeout;

        yield return joinReq.SendWebRequest();

        if (joinReq.result != UnityWebRequest.Result.Success)
        {
            _matchmakingActive = false;
            onError?.Invoke("Queue join error: " + joinReq.error);
            yield break;
        }

        Debug.Log("[BackendService] Joined matchmaking queue");

        bool searching = true;
        int attempts   = 0;
        const int maxAttempts = 120;

        while (searching && _matchmakingActive && attempts < maxAttempts)
        {
            yield return new WaitForSeconds(3f);
            attempts++;

            using var statusReq = UnityWebRequest.Get(serverUrl + "/matchmaking/status");
            statusReq.SetRequestHeader("Authorization", "Bearer " + _authToken);
            statusReq.timeout = (int)requestTimeout;

            yield return statusReq.SendWebRequest();

            if (statusReq.result == UnityWebRequest.Result.Success)
            {
                var resp = JsonUtility.FromJson<StatusResp>(statusReq.downloadHandler.text);

                // FIX v2.2: защита от zombie join_code
                if (resp.found && !string.IsNullOrEmpty(resp.join_code))
                {
                    if (resp.join_code == _lastUsedJoinCode)
                    {
                        Debug.LogWarning("[BackendService] Duplicate join_code (zombie session): " + resp.join_code + ". Continuing poll.");
                        onWaiting?.Invoke(0);
                        continue;
                    }

                    searching = false;
                    _matchmakingActive  = false;
                    _lastUsedJoinCode = resp.join_code;

                    var result = new MatchmakingResult
                    {
                        found        = true,
                        join_code    = resp.join_code,
                        opponent_name   = resp.opponent_name ?? "Opponent",
                        opponent_rating = resp.opponent_rating
                    };

                    OpponentName   = result.opponent_name;
                    OpponentRating = result.opponent_rating;

                    Debug.Log("[BackendService] Match found! join_code=" + result.join_code + ", opponent=" + result.opponent_name);
                    onMatchFound?.Invoke(result);
                }
                else if (resp.queue_position > 0)
                {
                    onWaiting?.Invoke(resp.queue_position);
                }
                else if (resp.status == "waiting_for_relay")
                {
                    Debug.Log("[BackendService] Match found, server preparing Relay...");
                    onWaiting?.Invoke(0);
                }
                else
                {
                    onWaiting?.Invoke(0);
                }
            }
            else
            {
                Debug.LogWarning("[BackendService] Polling error: " + statusReq.error);
            }
        }

        if (attempts >= maxAttempts)
        {
            _matchmakingActive = false;
            onError?.Invoke("Matchmaking timeout");
        }
    }

    public void StopMatchmaking()
    {
        if (!_matchmakingActive) return;

        _matchmakingActive = false;

        if (_matchmakingCoroutine != null)
        {
            StopCoroutine(_matchmakingCoroutine);
            _matchmakingCoroutine = null;
        }

        StartCoroutine(LeaveMatchmakingCoroutine());
    }

    private IEnumerator LeaveMatchmakingCoroutine()
    {
        using var req = UnityWebRequest.Delete(serverUrl + "/matchmaking/leave");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;
        yield return req.SendWebRequest();
        Debug.Log("[BackendService] Left queue (stale sessions abandoned)");
    }

    // --- РЕЗУЛЬТАТЫ МАТЧА ---------------------------------------------------

    /// <summary>
    /// Отправляет результат матча на сервер.
    /// isRoomMatch=true → ELO не меняется (комнатный матч с другом).
    /// </summary>
    public IEnumerator PostMatchResult(
        string winnerName,
        string loserName,
        int winnerKills,
        int loserKills,
        int winnerLevel,
        int loserLevel,
        int durationSec,
        bool isTechnical  = false,
        bool isRoomMatch  = false,    // [rooms_v2]
        Action<MatchResultResponse> onSuccess = null,
        Action<string> onError = null)
    {
        var body = new MatchResultRequest
        {
            winner_name   = winnerName,
            loser_name    = loserName,
            winner_kills  = winnerKills,
            loser_kills   = loserKills,
            winner_level  = winnerLevel,
            loser_level   = loserLevel,
            duration_sec  = durationSec,
            is_technical  = isTechnical,
            is_room_match = isRoomMatch   // [rooms_v2]
        };

        string json = JsonUtility.ToJson(body);

        using var req = new UnityWebRequest(serverUrl + "/matches/result", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<MatchResultResponse>(req.downloadHandler.text);
            string note = isRoomMatch ? " (room match — ELO unchanged)" : "";
            Debug.Log("[BackendService] Result saved: " + winnerName + " beat " + loserName + note);
            onSuccess?.Invoke(resp);
        }
        else
        {
            Debug.LogError("[BackendService] Save error: " + req.error);
            onError?.Invoke(req.error);
        }
    }

    // --- КОМНАТЫ -------------------------------------------------------------

    public IEnumerator CreateRoom(Action<RoomResponse> onSuccess, Action<string> onError)
    {
        using var req = UnityWebRequest.PostWwwForm(serverUrl + "/rooms/create", "");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<CreateRoomResp>(req.downloadHandler.text);
            var result = new RoomResponse
            {
                code  = resp.code,
                found = true
            };
            Debug.Log("[BackendService] Room created: " + resp.code);
            onSuccess?.Invoke(result);
        }
        else
        {
            Debug.LogError("[BackendService] Room creation error: " + req.error);
            onError?.Invoke(req.error);
        }
    }

    /// <summary>
    /// [rooms_v2] Хост вызывает после создания Relay-аллокации.
    /// Сохраняет Relay join_code в комнате — гость получит его при поллинге.
    /// </summary>
    public IEnumerator SetRoomRelay(string roomCode, string relayJoinCode,
        Action onSuccess, Action<string> onError)
    {
        string url = $"{serverUrl}/rooms/{roomCode}/relay?join_code={UnityWebRequest.EscapeURL(relayJoinCode)}";

        using var req = UnityWebRequest.PostWwwForm(url, "");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"[BackendService] Room {roomCode}: relay join_code saved.");
            onSuccess?.Invoke();
        }
        else
        {
            Debug.LogError($"[BackendService] SetRoomRelay error: {req.error}");
            onError?.Invoke(req.error);
        }
    }

    /// <summary>
    /// [rooms_v2] Закрывает комнату. Вызывается хостом при старте игры или выходе.
    /// </summary>
    public IEnumerator CloseRoom(string roomCode)
    {
        if (string.IsNullOrEmpty(roomCode)) yield break;

        using var req = UnityWebRequest.Delete($"{serverUrl}/rooms/{roomCode}");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = 5;
        yield return req.SendWebRequest();

        Debug.Log(req.result == UnityWebRequest.Result.Success
            ? $"[BackendService] Room {roomCode} closed."
            : $"[BackendService] CloseRoom error: {req.error}");
    }

    /// <summary>
    /// [rooms_v2] Находит комнату по коду.
    /// Возвращает has_relay=true когда хост уже выставил Relay join_code.
    /// Гость должен поллить этот endpoint пока has_relay не станет true.
    /// </summary>
    public IEnumerator FindRoom(string code, Action<RoomResponse> onSuccess, Action<string> onError)
    {
        string url = serverUrl + "/room/find?code=" + code.ToUpper();
        using var req = UnityWebRequest.Get(url);
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<RoomFindResp>(req.downloadHandler.text);
            var result = new RoomResponse
            {
                found     = resp.found,
                code      = resp.code,
                join_code = resp.join_code,
                host_name = resp.host_name,
                status    = resp.status,
                has_relay = resp.has_relay   // [rooms_v2]
            };
            Debug.Log("[BackendService] Room found: " + resp.code + ", has_relay=" + resp.has_relay + ", join_code=" + resp.join_code);
            onSuccess?.Invoke(result);
        }
        else
        {
            Debug.LogError("[BackendService] Room search error: " + req.error);
            onError?.Invoke(req.error);
        }
    }

    // --- ЛИДЕРБОРД -----------------------------------------------------------

    public IEnumerator GetLeaderboard(Action<LeaderboardWrapper> onSuccess, Action<string> onError)
    {
        using var req = UnityWebRequest.Get(serverUrl + "/leaderboard");
        req.SetRequestHeader("Authorization", "Bearer " + _authToken);
        req.timeout = (int)requestTimeout;

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var resp = JsonUtility.FromJson<LeaderboardResp>(req.downloadHandler.text);
            var wrapper = new LeaderboardWrapper { entries = resp.entries };
            Debug.Log("[BackendService] Leaderboard received: " + (resp.entries?.Length ?? 0) + " entries");
            onSuccess?.Invoke(wrapper);
        }
        else
        {
            Debug.LogError("[BackendService] Leaderboard error: " + req.error);
            onError?.Invoke(req.error);
        }
    }

    // --- ОЖИДАНИЕ ------------------------------------------------------------

    public IEnumerator WaitUntilReady(float maxWait = 30f)
    {
        float elapsed = 0f;
        while (string.IsNullOrEmpty(_authToken) && elapsed < maxWait)
        {
            elapsed += 0.1f;
            yield return new WaitForSeconds(0.1f);
        }
    }

    // --- ВЛОЖЕННЫЕ ТИПЫ ------------------------------------------------------

    [System.Serializable]
    public class MatchmakingResult
    {
        public bool   found;
        public string join_code;
        public string opponent_name;
        public int    opponent_rating;
        public int    queue_position;
        public string message;
    }

    // [rooms_v2] RoomResponse — добавлено has_relay
    [System.Serializable]
    public class RoomResponse
    {
        public bool   found;
        public string code;
        public string join_code;
        public string host_name;
        public string status;
        public bool   has_relay;   // true когда хост выставил Relay join_code
    }

    [System.Serializable]
    public class LeaderboardEntry
    {
        public long   rank;
        public string username;
        public int    rating;
        public int    wins;
        public int    losses;
        public int    total_kills;
        public int    best_kills;
    }

    [System.Serializable]
    public class LeaderboardWrapper
    {
        public LeaderboardEntry[] entries;
    }
}

[System.Serializable]
public class MatchResultResponse
{
    public bool success;
    public int  winner_new_rating;
    public int  loser_new_rating;
    public int  winner_rating_change;
    public int  loser_rating_change;
    public bool is_room_match;   // [rooms_v2]
}
