using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Globalization;   // ИСПРАВЛЕНО: нужен для InvariantCulture

/// <summary>
/// LiveMatchTracker v1.4
///
/// ИСПРАВЛЕНО v1.4:
///   - 422 Unprocessable Entity: float форматировался через системную локаль.
///     Русская локаль использует запятую: "100,0" вместо "100.0".
///     FastAPI не может распарсить JSON с запятой → 422.
///     Исправление: добавлен вспомогательный метод F() который принудительно
///     использует CultureInfo.InvariantCulture для всех float значений в JSON.
/// </summary>
public class LiveMatchTracker : MonoBehaviour
{
    [Header("Настройки отправки")]
    [SerializeField] private float updateIntervalSeconds = 5f;
    [SerializeField] private string fastApiUrl = "http://localhost:8000";
    [SerializeField] private int requestTimeoutSeconds = 10;

    // ─── СОСТОЯНИЕ ──────────────────────────────────────────────────────────────

    private int _sessionId;
    private string _serverId = "";
    private bool _isTracking;
    private bool _isSending;
    private float _timer;

    private PlayerStats[] _cachedPlayers;
    private CompetitiveTimerManager _cachedCompTimer;
    private LocalGameTimer _cachedLocalTimer;

    private Dictionary<ulong, string> _playerNames = new Dictionary<ulong, string>();

    private WeaponDatabase _weaponDb;
    private TotemDatabase _totemDb;
    private CharacterDatabase _charDb;
    private ItemDatabase _itemDb;
    private bool _dependenciesResolved;

    // ─── PUBLIC API ─────────────────────────────────────────────────────────────

    public void SetSessionId(int sessionId) => _sessionId = sessionId;
    public void SetServerId(string serverId) => _serverId = serverId ?? "";
    public void SetFastApiUrl(string url) { if (!string.IsNullOrEmpty(url)) fastApiUrl = url; }

    public void SetPlayerNames(Dictionary<ulong, string> names)
    {
        if (names != null) _playerNames = names;
    }

    public void StartTracking()
    {
        if (_sessionId <= 0)
        {
            Debug.LogError("[LiveMatchTracker] ❌ session_id не установлен!");
            return;
        }

        _cachedPlayers = FindObjectsByType<PlayerStats>(FindObjectsSortMode.None);
        _cachedCompTimer = FindFirstObjectByType<CompetitiveTimerManager>();
        _cachedLocalTimer = FindFirstObjectByType<LocalGameTimer>();

        _isTracking = true;
        _isSending = false;
        _timer = updateIntervalSeconds;

        Debug.Log($"[LiveMatchTracker] ▶️ Трекинг #{_sessionId} | " +
                  $"игроков: {_cachedPlayers?.Length ?? 0} | интервал: {updateIntervalSeconds}с");
    }

    public void StopTracking()
    {
        _isTracking = false;
        Debug.Log($"[LiveMatchTracker] ⏹️ Трекинг #{_sessionId} остановлен");
    }

    // ─── UNITY LIFECYCLE ────────────────────────────────────────────────────────

    void Update()
    {
        if (!_isTracking) return;

        if (NetworkManager.Singleton == null ||
            !NetworkManager.Singleton.IsListening ||
            !NetworkManager.Singleton.IsServer)
        {
            StopTracking();
            return;
        }

        _timer += Time.deltaTime;
        if (_timer < updateIntervalSeconds) return;
        _timer = 0f;

        if (_isSending)
        {
            Debug.LogWarning("[LiveMatchTracker] ⏳ Предыдущий POST не завершился — пропускаем тик");
            return;
        }

        SendMatchSnapshot();
    }

    // ─── СБОР ДАННЫХ ────────────────────────────────────────────────────────────

    private void SendMatchSnapshot()
    {
        ResolveDependencies();

        if (_cachedPlayers == null || _cachedPlayers.Length == 0)
        {
            _cachedPlayers = FindObjectsByType<PlayerStats>(FindObjectsSortMode.None);
            if (_cachedPlayers.Length == 0)
            {
                Debug.LogWarning("[LiveMatchTracker] ⚠️ Игроки не найдены — снепшот пропущен");
                return;
            }
        }

        var request = new LiveMatchUpdateRequest
        {
            session_id = _sessionId,
            server_id = _serverId,
            match_time_seconds = GetMatchTimeSeconds(),
            players = new List<LivePlayerSnapshot>()
        };

        foreach (var player in _cachedPlayers)
        {
            if (player == null) continue;
            var snap = BuildPlayerSnapshot(player);
            if (snap != null) request.players.Add(snap);
        }

        if (request.players.Count == 0) return;

        string json = SerializeToJson(request);
        StartCoroutine(PostCoroutine(json, request.players.Count));
    }

    private LivePlayerSnapshot BuildPlayerSnapshot(PlayerStats player)
    {
        if (player == null) return null;

        var snap = new LivePlayerSnapshot
        {
            client_id = (long)player.OwnerClientId,
            username = ResolveUsername(player),
            character_index = player.GetNetCharacterIndex(),
            level = player.GetNetLevel(),
            kills = player.GetKillCount(),
            health = player.GetNetHealth(),
            max_health = player.GetNetMaxHealth(),
            shield = player.GetNetShieldHP(),
            weapons = new List<LiveWeaponData>(),
            totems = new List<LiveTotemData>()
        };

        // ── ОРУЖИЯ ──────────────────────────────────────────────────────────
        if (_weaponDb != null)
        {
            int weaponFlags = player.GetNetWeaponFlags();
            int packedLevels = player.GetNetWeaponLevels();

            if (weaponFlags != 0)
            {
                var defs = _weaponDb.GetFromFlags(weaponFlags);
                for (int slot = 0; slot < defs.Count && slot < 4; slot++)
                {
                    var def = defs[slot];
                    if (def == null) continue;
                    snap.weapons.Add(new LiveWeaponData
                    {
                        slot_index = slot,
                        weapon_id = def.weaponId,
                        weapon_name = def.displayName,
                        weapon_level = PlayerStats.UnpackWeaponLevel(packedLevels, slot)
                    });
                }
            }
        }

        // ── ТОТЕМЫ ──────────────────────────────────────────────────────────
        if (_totemDb != null)
        {
            long totemPacked = player.GetNetTotemData();
            if (totemPacked != 0L)
            {
                var allTotems = _totemDb.GetAll();
                for (int slot = 0; slot < 4; slot++)
                {
                    var (dbIndex, level) = PlayerStats.UnpackTotemSlot(totemPacked, slot);
                    if (dbIndex == 31) continue;
                    if (dbIndex < 0 || dbIndex >= allTotems.Count) continue;

                    var def = allTotems[dbIndex];
                    if (def == null) continue;

                    snap.totems.Add(new LiveTotemData
                    {
                        slot_index = slot,
                        totem_id = def.totemId,
                        totem_name = def.displayName,
                        bonus_type = def.bonusType.ToString(),
                        totem_level = level,
                        totem_value = player.StatSheet?.GetStat(MapBonusToStat(def.bonusType)) ?? 0f
                    });
                }
            }
        }

        // ── ПРЕДМЕТЫ (через netItemFlags битмаску) ───────────────────────────
        // Каждый бит соответствует ItemDefinition.FlagMask.
        // ItemInventory обновляет netItemFlags через PlayerStats.SyncItemFlags() при каждом изменении.
        if (_itemDb != null)
        {
            int itemFlags = player.GetNetItemFlags();
            if (itemFlags != 0)
            {
                foreach (var itemDef in _itemDb.GetAllItems())
                {
                    if (itemDef == null) continue;
                    if ((itemFlags & itemDef.FlagMask) == 0) continue;

                    snap.items.Add(new LiveItemData
                    {
                        item_id     = itemDef.itemId,
                        item_name   = itemDef.displayName,
                        item_rarity = itemDef.rarity.ToString(),
                        quantity    = 1   // битмаска не хранит количество — только факт наличия
                    });
                }
            }
        }

        return snap;
    }

    // ─── ВСПОМОГАТЕЛЬНЫЕ ────────────────────────────────────────────────────────

    private string ResolveUsername(PlayerStats player)
    {
        if (player == null) return "Unknown";
        if (_playerNames.TryGetValue(player.OwnerClientId, out string name) &&
            !string.IsNullOrEmpty(name))
            return name;
        return $"Player_{player.OwnerClientId}";
    }

    private int GetMatchTimeSeconds()
    {
        if (_cachedCompTimer != null && GameModeManager.IsCompetitiveMode())
            return (int)_cachedCompTimer.GetTimeElapsed();
        if (_cachedLocalTimer != null)
            return (int)_cachedLocalTimer.GetTimeElapsed();
        return 0;
    }

    private void ResolveDependencies()
    {
        if (_dependenciesResolved) return;

        if (_weaponDb == null)
        {
            try { _weaponDb = InjectionProvider.Resolve<WeaponDatabase>(); }
            catch (System.Exception e)
            { Debug.LogWarning($"[LiveMatchTracker] WeaponDatabase: {e.Message}"); }
        }

        if (_totemDb == null)
        {
            try { _totemDb = InjectionProvider.Resolve<TotemDatabase>(); }
            catch (System.Exception e)
            { Debug.LogWarning($"[LiveMatchTracker] TotemDatabase: {e.Message}"); }
        }

        if (_charDb == null)
        {
            try { _charDb = InjectionProvider.Resolve<CharacterDatabase>(); }
            catch { /* опционально */ }
        }

        if (_itemDb == null)
        {
            try { _itemDb = InjectionProvider.Resolve<ItemDatabase>(); }
            catch (System.Exception e)
            { Debug.LogWarning($"[LiveMatchTracker] ItemDatabase: {e.Message}"); }
        }

        _dependenciesResolved = (_weaponDb != null && _totemDb != null);

        if (_dependenciesResolved)
            Debug.Log("[LiveMatchTracker] ✅ DI разрешены (WeaponDB + TotemDB)");
        else
            Debug.LogWarning("[LiveMatchTracker] ⚠️ Не все DI разрешены — повтор при следующем тике");
    }

    private StatType MapBonusToStat(TotemBonusType bonus)
    {
        switch (bonus)
        {
            case TotemBonusType.Damage: return StatType.DamageMultiplier;
            case TotemBonusType.MoveSpeed: return StatType.MoveSpeedMultiplier;
            case TotemBonusType.MaxHealth: return StatType.MaxHealth;
            case TotemBonusType.CritChance: return StatType.CritChance;
            case TotemBonusType.Armor: return StatType.Armor;
            case TotemBonusType.DamageReduction: return StatType.DamageReduction;
            case TotemBonusType.XpGain: return StatType.XpMultiplier;
            case TotemBonusType.PickupRange: return StatType.PickupRange;
            default: return StatType.DamageMultiplier;
        }
    }

    // ─── HTTP ───────────────────────────────────────────────────────────────────

    private IEnumerator PostCoroutine(string json, int playerCount)
    {
        _isSending = true;

        byte[] body = Encoding.UTF8.GetBytes(json);
        using var req = new UnityEngine.Networking.UnityWebRequest(
            $"{fastApiUrl}/live/match_update", "POST");
        req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body);
        req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = requestTimeoutSeconds;

        yield return req.SendWebRequest();

        if (req.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            Debug.Log($"[LiveMatchTracker] ✅ Снепшот #{_sessionId} | игроков: {playerCount}");
        else
            Debug.LogError($"[LiveMatchTracker] ❌ HTTP {req.responseCode}: {req.error}");

        _isSending = false;
    }

    // ─── JSON ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// ИСПРАВЛЕНО: принудительно использует InvariantCulture для float.
    /// Без этого русская локаль форматирует "100,0" → JSON невалиден → FastAPI 422.
    /// </summary>
    private static string F(float val, string fmt = "F1")
        => val.ToString(fmt, CultureInfo.InvariantCulture);

    private string SerializeToJson(LiveMatchUpdateRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.AppendFormat("\"session_id\":{0},", req.session_id);
        sb.AppendFormat("\"server_id\":\"{0}\",", J(req.server_id));
        sb.AppendFormat("\"match_time_seconds\":{0},", req.match_time_seconds);
        sb.Append("\"players\":[");

        for (int i = 0; i < req.players.Count; i++)
        {
            if (i > 0) sb.Append(",");
            AppendPlayer(sb, req.players[i]);
        }

        sb.Append("]}");
        return sb.ToString();
    }

    private void AppendPlayer(StringBuilder sb, LivePlayerSnapshot p)
    {
        sb.Append("{");
        sb.AppendFormat("\"client_id\":{0},", p.client_id);
        sb.AppendFormat("\"username\":\"{0}\",", J(p.username));
        sb.AppendFormat("\"character_index\":{0},", p.character_index);
        sb.AppendFormat("\"level\":{0},", p.level);
        sb.AppendFormat("\"kills\":{0},", p.kills);
        // ИСПРАВЛЕНО: F() вместо {0:F1} — точка вместо запятой в любой локали
        sb.AppendFormat("\"health\":{0},", F(p.health));
        sb.AppendFormat("\"max_health\":{0},", F(p.max_health));
        sb.AppendFormat("\"shield\":{0},", F(p.shield));

        sb.Append("\"weapons\":[");
        for (int w = 0; w < p.weapons.Count; w++)
        {
            if (w > 0) sb.Append(",");
            var wep = p.weapons[w];
            sb.Append("{");
            sb.AppendFormat("\"slot_index\":{0},", wep.slot_index);
            sb.AppendFormat("\"weapon_id\":\"{0}\",", J(wep.weapon_id));
            sb.AppendFormat("\"weapon_name\":\"{0}\",", J(wep.weapon_name));
            sb.AppendFormat("\"weapon_level\":{0}", wep.weapon_level);
            sb.Append("}");
        }
        sb.Append("],");

        sb.Append("\"totems\":[");
        for (int t = 0; t < p.totems.Count; t++)
        {
            if (t > 0) sb.Append(",");
            var tot = p.totems[t];
            sb.Append("{");
            sb.AppendFormat("\"slot_index\":{0},", tot.slot_index);
            sb.AppendFormat("\"totem_id\":\"{0}\",", J(tot.totem_id));
            sb.AppendFormat("\"totem_name\":\"{0}\",", J(tot.totem_name));
            sb.AppendFormat("\"bonus_type\":\"{0}\",", J(tot.bonus_type));
            sb.AppendFormat("\"totem_level\":{0},", tot.totem_level);
            // ИСПРАВЛЕНО: F() с форматом "F2"
            sb.AppendFormat("\"totem_value\":{0}", F(tot.totem_value, "F2"));
            sb.Append("}");
        }
        sb.Append("],\"items\":[]}");
    }

    private string J(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}