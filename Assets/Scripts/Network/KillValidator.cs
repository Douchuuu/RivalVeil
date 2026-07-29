using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

/// <summary>
/// KillValidator v2.0 — сервер-side античит проверка убийств.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v2.0:
/// ══════════════════════════════════════════════════════════════════════
///   [HIGH] estimatedRTT теперь берётся из NetworkTransport.GetCurrentRtt().
///          Убран хардкод 100ms. При 200ms RTT divergence автоматически
///          увеличивается с 105 до 210, уменьшая ложные срабатывания.
///   [MEDIUM] RecalculateMaxDivergence вызывается каждые 5 секунд для
///            отслеживания изменения RTT во время матча.
///
/// ══════════════════════════════════════════════════════════════════════
/// АЛГОРИТМ Token Bucket:
/// ══════════════════════════════════════════════════════════════════════
///   Две проверки:
///   1. Rate limit: delta > maxKillsPerSecond * elapsed * 1.5
///   2. Divergence: delta > calculatedMaxDivergence (из RTT)
///
///   При RTT = 100ms, maxKillsPerSecond = 700:
///     inFlight = 0.1 * 700 = 70
///     divergence = Clamp(70 * 1.5, 100, 500) = 105
///
///   При RTT = 250ms (через Relay из другого региона):
///     inFlight = 0.25 * 700 = 175
///     divergence = Clamp(175 * 1.5, 100, 500) = 263
/// </summary>
public class KillValidator : NetworkBehaviour
{
    private struct PlayerKillData
    {
        public int ConfirmedKills;
        public float KillBucket;
        public float LastBatchTime;
        public float LastDivergenceWarning;
    }

    [Header("Rate Limiting")]
    [SerializeField] private float maxKillsPerSecond = 700f;
    [SerializeField] private float batchSafetyMultiplier = 1.5f;

    [Header("Divergence Settings")]
    [Tooltip("ХАРДКОД RTT УБРАН. Теперь читается из NetworkTransport.GetCurrentRtt(). " +
             "Это поле используется как fallback, если RTT от транспорта недоступен.")]
    [SerializeField] private float estimatedRTTMilliseconds = 100f;
    [SerializeField] private float divergenceBufferMultiplier = 1.5f;
    [SerializeField] private int minDivergence = 100;
    [SerializeField] private int maxDivergence = 500;

    private Dictionary<ulong, PlayerKillData> _playerData = new Dictionary<ulong, PlayerKillData>();
    private int _calculatedMaxDivergence;
    private const float BATCH_INTERVAL = 0.1f;

    // FIX v2.0: периодическое обновление RTT
    private float _lastRttUpdate = 0f;
    private const float RTT_UPDATE_INTERVAL = 5f;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            UpdateRTTFromTransport();
            RecalculateMaxDivergence();
        }
    }

    private void Update()
    {
        if (!IsServer) return;

        // FIX v2.0: обновляем RTT каждые 5 секунд для отслеживания изменений
        if (Time.time - _lastRttUpdate >= RTT_UPDATE_INTERVAL)
        {
            UpdateRTTFromTransport();
            RecalculateMaxDivergence();
            _lastRttUpdate = Time.time;
        }
    }

    /// <summary>
    /// FIX v2.0: Читает актуальный RTT из NetworkTransport вместо хардкода.
    /// Fallback на SerializeField estimatedRTTMilliseconds если транспорт недоступен.
    /// </summary>
    private void UpdateRTTFromTransport()
    {
        try
        {
            var transport = NetworkManager.Singleton?.NetworkConfig?.NetworkTransport;
            if (transport != null)
            {
                // Получаем RTT для первого подключённого клиента
                float rttMs = 100f; // default fallback
                bool gotRtt = false;

                foreach (var clientId in NetworkManager.Singleton.ConnectedClientsIds)
                {
                    if (clientId == NetworkManager.Singleton.LocalClientId) continue;
                    try
                    {
                        // Unity Transport (UTP) — GetCurrentRtt принимает clientId
                        rttMs = transport.GetCurrentRtt(clientId);
                        gotRtt = true;
                        break; // берём RTT первого клиента
                    }
                    catch
                    {
                        // Если GetCurrentRtt с clientId не работает — пробуем без параметров
                        continue;
                    }
                }

                // Если не удалось получить per-client RTT — пробуем общий метод
                if (!gotRtt)
                {
                    try
                    {
                        // Некоторые транспорты имеют безпараметрический GetCurrentRtt()
                        var method = transport.GetType().GetMethod("GetCurrentRtt",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance,
                            null, System.Type.EmptyTypes, null);
                        if (method != null)
                        {
                            var result = method.Invoke(transport, null);
                            if (result is float f)
                            {
                                rttMs = f;
                                gotRtt = true;
                            }
                        }
                    }
                    catch { /* игнорируем */ }
                }

                // Обновляем estimatedRTT если получили реальное значение
                if (gotRtt && rttMs > 0f)
                {
                    // Сглаживание: EWMA с alpha=0.3
                    estimatedRTTMilliseconds = estimatedRTTMilliseconds * 0.7f + rttMs * 0.3f;
                    Debug.Log($"[KillValidator] RTT обновлён: {rttMs:F1}ms " +
                              $"(smoothed: {estimatedRTTMilliseconds:F1}ms)");
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[KillValidator] Не удалось получить RTT из транспорта: {e.Message}. " +
                             $"Используется fallback: {estimatedRTTMilliseconds}ms");
        }
    }

    private void RecalculateMaxDivergence()
    {
        float inFlight = (estimatedRTTMilliseconds / 1000f) * maxKillsPerSecond;
        _calculatedMaxDivergence = Mathf.Clamp(Mathf.CeilToInt(inFlight * divergenceBufferMultiplier), minDivergence, maxDivergence);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void ValidateKillBatchServerRpc(int clientTotalKills, float batchElapsedTime, RpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        if (!_playerData.ContainsKey(clientId))
        {
            _playerData[clientId] = new PlayerKillData { ConfirmedKills = 0, KillBucket = maxKillsPerSecond, LastBatchTime = Time.time };
        }

        var data = _playerData[clientId];
        int delta = clientTotalKills - data.ConfirmedKills;
        if (delta <= 0) return;

        float safeElapsed = Mathf.Max(batchElapsedTime, BATCH_INTERVAL);
        data.KillBucket = Mathf.Min(maxKillsPerSecond, data.KillBucket + safeElapsed * maxKillsPerSecond);

        // Проверка 1: Лимит по времени
        if (delta > (maxKillsPerSecond * safeElapsed * batchSafetyMultiplier) || data.KillBucket < delta)
        {
            RollbackKillsClientRpc(data.ConfirmedKills, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            return;
        }

        // Проверка 2: Divergence
        if (delta > _calculatedMaxDivergence)
        {
            // Логируем предупреждение не чаще раза в 5 секунд на клиента
            if (Time.time - data.LastDivergenceWarning >= 5f)
            {
                Debug.LogWarning($"[KillValidator] Client {clientId}: divergence {delta} > max {_calculatedMaxDivergence}. Rollback. RTT={estimatedRTTMilliseconds:F0}ms");
                data.LastDivergenceWarning = Time.time;
                _playerData[clientId] = data;
            }
            RollbackKillsClientRpc(data.ConfirmedKills, RpcTarget.Single(clientId, RpcTargetUse.Temp));
            return;
        }

        data.KillBucket -= delta;
        data.ConfirmedKills = clientTotalKills;
        _playerData[clientId] = data;
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void RollbackKillsClientRpc(int confirmedKills, RpcParams rpcParams = default)
    {
        var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
        if (localPlayer != null && localPlayer.TryGetComponent<PlayerStats>(out var stats))
            stats.ForceSetKills(confirmedKills);
    }

    public int GetConfirmedKills(ulong clientId) => _playerData.TryGetValue(clientId, out var d) ? d.ConfirmedKills : 0;
    public int GetMaxDivergence() => _calculatedMaxDivergence;
    public float GetCurrentRTT() => estimatedRTTMilliseconds;

    public void LogAllClientsState()
    {
        foreach (var kvp in _playerData)
            Debug.Log($"Client {kvp.Key}: Confirmed={kvp.Value.ConfirmedKills}, Bucket={kvp.Value.KillBucket:F1}");
    }
}
