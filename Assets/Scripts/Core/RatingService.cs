using UnityEngine;
using Unity.Netcode;
using System;
using System.Collections;
using System.Text;
using UnityEngine.Networking;
using VContainer;

/// <summary>
/// RatingService v4.2 — rooms_v2 patch
///
/// ══════════════════════════════════════════════════════════════════════
/// НОВОЕ v4.2 (rooms_v2):
/// ══════════════════════════════════════════════════════════════════════
///   [NEW]  SetRoomMatch(bool) — статический флаг комнатного матча.
///          Если true — результат сохраняется без изменения ELO.
///          Устанавливается из MenuManager при входе/выходе из комнаты.
///   [NEW]  _isRoomMatch передаётся в PostMatchResult и PostMatchHttp.
///   [FIX]  SendViaDirectHttp: если isRoomMatch=true и нет залогиненного
///          BackendService — логируем предупреждение и пропускаем сохранение
///          (нет смысла сохранять без токена и без dedicated server).
///   [FIX]  После матча через комнату нужно сбрасывать флаг через
///          SetRoomMatch(false) — делается в GameOverScreenManager.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v4.1:
/// ══════════════════════════════════════════════════════════════════════
///   [CRITICAL] 404 при сохранении матча с dedicated server.
///              GetLocalPlayer().name → имя GameObject, а не логин.
///              РЕШЕНИЕ: SetPlayerNames(ulong, string, ulong, string).
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class RatingService
{
    private const int K_FACTOR = 32;
    private const int DEFAULT_RATING = 1000;

    public event Action<MatchResult> OnMatchResultCalculated;

    private static string _fastApiUrl  = "http://localhost:8000";
    private static string _serverId    = "";

    // --- [rooms_v2] флаг комнатного матча -----------------------------------
    // Если true — результат матча отправляется с is_room_match=true → ELO не меняется.
    // Устанавливается из MenuManager.OnCreateRoomClicked / OnJoinRoomClicked.
    // Сбрасывается в GameOverScreenManager.OnReturnButtonClicked.
    private static bool _isRoomMatch = false;

    // --- реальные логины игроков (ClientId → DB username) -------------------
    private static ulong  _player1ClientId = 1UL;
    private static string _player1Name     = "";
    private static ulong  _player2ClientId = 2UL;
    private static string _player2Name     = "";

    public static void SetFastApiUrl(string url)
    {
        if (!string.IsNullOrEmpty(url)) _fastApiUrl = url;
    }

    public static void SetServerId(string serverId)
    {
        if (!string.IsNullOrEmpty(serverId)) _serverId = serverId;
    }

    /// <summary>
    /// [rooms_v2] Включает/выключает режим комнатного матча.
    /// В этом режиме результат сохраняется без изменения ELO.
    ///
    /// Вызывать:
    ///   SetRoomMatch(true)  — в MenuManager при старте комнатного матча.
    ///   SetRoomMatch(false) — в GameOverScreenManager при возврате в меню.
    /// </summary>
    public static void SetRoomMatch(bool isRoom)
    {
        _isRoomMatch = isRoom;
        Debug.Log($"[RatingService] isRoomMatch = {isRoom}");
    }

    /// <summary>
    /// Сохраняет реальные логины игроков для корректной отправки результата.
    /// Вызывается из ServerStartup.StartLiveTracking() сразу после poll.
    /// ClientId 1 = первый клиент (player1), ClientId 2 = второй (player2).
    /// </summary>
    public static void SetPlayerNames(ulong p1Id, string p1Name, ulong p2Id, string p2Name)
    {
        _player1ClientId = p1Id;
        _player1Name     = p1Name ?? "";
        _player2ClientId = p2Id;
        _player2Name     = p2Name ?? "";
        Debug.Log($"[RatingService] Player names: {p1Id}={p1Name}, {p2Id}={p2Name}");
    }

    // ─── РЕЗУЛЬТАТ МАТЧА ──────────────────────────────────────────────────────

    public struct MatchResult
    {
        public ulong WinnerId;
        public ulong LoserId;
        public int   WinnerKills;
        public int   LoserKills;
        public float MatchDurationSeconds;
        public bool  IsTechnicalVictory;
        public string Reason;
        public int   WinnerRatingChange;
        public int   LoserRatingChange;
        public int   WinnerNewRating;
        public int   LoserNewRating;
    }

    // ─────────────────────────────────────────────────────────────────────────

    public void RecordMatchResult(
        ulong winnerId, ulong loserId,
        int winnerKills, int loserKills, float duration,
        int winnerCurrentRating = DEFAULT_RATING,
        int loserCurrentRating  = DEFAULT_RATING)
    {
        var result = new MatchResult
        {
            WinnerId             = winnerId,
            LoserId              = loserId,
            WinnerKills          = winnerKills,
            LoserKills           = loserKills,
            MatchDurationSeconds = duration,
            IsTechnicalVictory   = false,
        };
        CalculateElo(ref result, winnerCurrentRating, loserCurrentRating);
        FinalizeResult(result);
    }

    public void RecordTechnicalVictory(
        ulong winnerId, ulong loserId, string reason,
        int winnerCurrentRating = DEFAULT_RATING,
        int loserCurrentRating  = DEFAULT_RATING)
    {
        Debug.Log($"[RatingService] Technical victory: {winnerId} over {loserId}. {reason}");

        var result = new MatchResult
        {
            WinnerId           = winnerId,
            LoserId            = loserId,
            IsTechnicalVictory = true,
            Reason             = reason,
        };
        CalculateElo(ref result, winnerCurrentRating, loserCurrentRating);
        result.WinnerRatingChange = Mathf.RoundToInt(result.WinnerRatingChange * 0.5f);
        result.LoserRatingChange  = Mathf.RoundToInt(result.LoserRatingChange  * 0.5f);
        result.WinnerNewRating    = winnerCurrentRating + result.WinnerRatingChange;
        result.LoserNewRating     = loserCurrentRating  + result.LoserRatingChange;
        FinalizeResult(result);
    }

    // ─── ELO -----------------------------------------------------------------

    private void CalculateElo(ref MatchResult r, int ratingA, int ratingB)
    {
        float expected = 1f / (1f + Mathf.Pow(10f, (ratingB - ratingA) / 400f));
        int deltaA = Mathf.RoundToInt(K_FACTOR * (1f - expected));
        int deltaB = Mathf.RoundToInt(K_FACTOR * (0f - (1f - expected)));
        deltaA = Mathf.Max(1, deltaA);
        deltaB = Mathf.Min(-1, deltaB);
        r.WinnerRatingChange = deltaA;
        r.LoserRatingChange  = deltaB;
        r.WinnerNewRating    = ratingA + deltaA;
        r.LoserNewRating     = Mathf.Max(0, ratingB + deltaB);
    }

    private void FinalizeResult(MatchResult result)
    {
        string roomNote = _isRoomMatch ? " [ROOM — no ELO change]" : "";
        Debug.Log($"[RatingService]{roomNote} Winner {result.WinnerId}: +{result.WinnerRatingChange} → {result.WinnerNewRating}\n" +
                  $"  Loser {result.LoserId}: {result.LoserRatingChange} → {result.LoserNewRating}" +
                  (result.IsTechnicalVictory ? $"\n  Technical: {result.Reason}" : ""));

        OnMatchResultCalculated?.Invoke(result);
        SendResultToBackend(result);
    }

    // ─── ОТПРАВКА РЕЗУЛЬТАТА ──────────────────────────────────────────────────

    private void SendResultToBackend(MatchResult result)
    {
        var backend = BackendService.Instance;
        if (backend != null && backend.IsLoggedIn)
        {
            SendViaBackendService(result, backend);
            return;
        }

        // [rooms_v2] Если это комнатный матч, но нет залогиненного BackendService —
        // результат не сохраняем (нет токена, нет dedicated server → некуда отправить).
        if (_isRoomMatch && (backend == null || !backend.IsLoggedIn))
        {
            Debug.LogWarning("[RatingService] Room match result skipped: BackendService not logged in. " +
                             "Ensure player is logged in before a room match.");
            return;
        }

        SendViaDirectHttp(result);
    }

    private void SendViaBackendService(MatchResult result, BackendService backend)
    {
        ulong myClientId = NetworkManager.Singleton?.LocalClientId ?? 0;
        bool iAmWinner   = (result.WinnerId == myClientId);
        string winnerName = iAmWinner ? backend.PlayerName : backend.OpponentName;
        string loserName  = iAmWinner ? backend.OpponentName : backend.PlayerName;

        int myLevel = 1, oppLevel = 1;
        try
        {
            var reg = InjectionProvider.Container?.Resolve<PlayerRegistry>();
            myLevel  = reg?.GetLocalPlayer()?.CurrentLevel ?? 1;
            oppLevel = reg?.GetOpponent()?.CurrentLevel    ?? 1;
        }
        catch { }

        // [rooms_v2] передаём _isRoomMatch → сервер не изменит ELO
        backend.StartCoroutine(backend.PostMatchResult(
            winnerName, loserName,
            result.WinnerKills, result.LoserKills,
            iAmWinner ? myLevel : oppLevel,
            iAmWinner ? oppLevel : myLevel,
            (int)result.MatchDurationSeconds,
            result.IsTechnicalVictory,
            _isRoomMatch,       // [rooms_v2]
            data => Debug.Log($"[RatingService] ✅ Saved{(_isRoomMatch ? " (no ELO)" : "")}. " +
                              $"Rating → {(iAmWinner ? data.winner_new_rating : data.loser_new_rating)}"),
            err => Debug.LogError($"[RatingService] ❌ Error: {err}")
        ));
    }

    /// <summary>
    /// Прямой HTTP POST с dedicated server.
    /// ИСПРАВЛЕНО v4.1: берёт реальный логин из _player1Name/_player2Name,
    /// а не имя GameObject'а ("Player_Pribaff(Clone)").
    /// </summary>
    private void SendViaDirectHttp(MatchResult result)
    {
        string winnerName = GetRealPlayerName(result.WinnerId);
        string loserName  = GetRealPlayerName(result.LoserId);

        int wLevel = 1, lLevel = 1;
        try
        {
            var allStats = UnityEngine.Object.FindObjectsByType<PlayerStats>(
                UnityEngine.FindObjectsSortMode.None);
            foreach (var ps in allStats)
            {
                if (ps.OwnerClientId == result.WinnerId) wLevel = ps.CurrentLevel;
                if (ps.OwnerClientId == result.LoserId)  lLevel = ps.CurrentLevel;
            }
        }
        catch { }

        CoroutineRunner.Run(PostMatchHttp(
            winnerName, loserName,
            result.WinnerKills, result.LoserKills,
            wLevel, lLevel,
            (int)result.MatchDurationSeconds,
            result.IsTechnicalVictory
        ));
    }

    /// <summary>
    /// Возвращает реальный логин игрока по его ClientId.
    /// Fallback: "Player_{clientId}" если имя не установлено.
    /// </summary>
    private static string GetRealPlayerName(ulong clientId)
    {
        if (clientId == _player1ClientId && !string.IsNullOrEmpty(_player1Name))
            return _player1Name;
        if (clientId == _player2ClientId && !string.IsNullOrEmpty(_player2Name))
            return _player2Name;

        Debug.LogWarning($"[RatingService] No DB name for ClientId={clientId}. " +
                         "Ensure ServerStartup calls RatingService.SetPlayerNames().");
        return $"Player_{clientId}";
    }

    [Serializable]
    class MatchBody
    {
        public string winner_name;
        public string loser_name;
        public int    winner_kills;
        public int    loser_kills;
        public int    winner_level;
        public int    loser_level;
        public int    duration_sec;
        public bool   is_technical;
        public bool   is_room_match;   // [rooms_v2]
    }

    private IEnumerator PostMatchHttp(
        string winner, string loser,
        int wKills, int lKills, int wLevel, int lLevel,
        int duration, bool isTechnical)
    {
        var body = new MatchBody
        {
            winner_name   = winner,
            loser_name    = loser,
            winner_kills  = wKills,
            loser_kills   = lKills,
            winner_level  = wLevel,
            loser_level   = lLevel,
            duration_sec  = duration,
            is_technical  = isTechnical,
            is_room_match = _isRoomMatch   // [rooms_v2]
        };

        string json = JsonUtility.ToJson(body);

        string url;
        string token = "";

        if (!string.IsNullOrEmpty(_serverId))
        {
            // Dedicated server: /server/save_match_result проверяет server_id вместо токена
            url = $"{_fastApiUrl}/server/save_match_result?server_id={_serverId}";
        }
        else
        {
            url   = $"{_fastApiUrl}/matches/result";
            token = BackendService.Instance?.IsLoggedIn == true
                ? BackendService.Instance.GetToken()
                : "";
        }

        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = 10;
        req.SetRequestHeader("Content-Type", "application/json");
        if (!string.IsNullOrEmpty(token))
            req.SetRequestHeader("Authorization", $"Bearer {token}");

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log($"[RatingService] ✅ Result saved{(_isRoomMatch ? " (no ELO)" : "")}: {winner} beat {loser}");
        else
            Debug.LogError($"[RatingService] ❌ POST failed ({url}): {req.error}");
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// CoroutineRunner — запускает корутины без зависимости от конкретного MonoBehaviour.
/// </summary>
public static class CoroutineRunner
{
    private static CoroutineRunnerMono _runner;

    public static void Run(IEnumerator coroutine)
    {
        if (_runner == null)
        {
            var go = new GameObject("[CoroutineRunner]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<CoroutineRunnerMono>();
        }
        _runner.StartCoroutine(coroutine);
    }
}

public class CoroutineRunnerMono : MonoBehaviour { }
