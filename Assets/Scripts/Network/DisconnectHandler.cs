using UnityEngine;
using Unity.Netcode;
using System;
using System.Collections;
using VContainer;
using VContainer.Unity;

/// <summary>
/// ══════════════════════════════════════════════════════════════════════
/// ОБРАБОТЧИК ДИСКОННЕКТОВ — рефакторинг VContainer + Netcode
/// ══════════════════════════════════════════════════════════════════════
///
/// БЫЛО (проблема):
///   DisconnectHandler — NetworkBehaviour, зарегистрирован в VContainer через
///   builder.RegisterComponent(), но [Inject] Construct() не вызывался —
///   Netcode спавнил объект в обход VContainer lifecycle.
///   _ratingService = null → RecordTechnicalVictory() не работал.
///
/// СТАЛО (решение):
///   В OnNetworkSpawn() добавлен вызов:
///     InjectionProvider.Container.InjectGameObject(gameObject)
///   VContainer находит [Inject] Construct(RatingService) и заполняет _ratingService.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v5.0:
/// ══════════════════════════════════════════════════════════════════════
///   [CRITICAL] GetRemainingClientId(): исключает LocalClientId (сервер, ClientId=0).
///              На dedicated server LocalClientId = 0, клиенты = 1, 2.
///              Раньше при отключении клиента 1 возвращался ClientId 0 → сервер
///              побеждал → RatingService не находил логин → 404.
///   [HIGH]     GracePeriodCoroutine: проверка winnerId != LocalClientId.
///              Если остался только сервер — матч не сохраняется как тех. победа.
///
/// ЧТО ДЕЛАЕТ ЭТОТ СКРИПТ:
///   1. Обнаруживает дисконнект (через NGO callback OnClientDisconnectedCallback)
///   2. Запускает Grace Period (30 сек) — время на реконнект
///   3. Если реконнект не произошёл:
///        - Засчитывает техническую победу оставшемуся игроку
///        - Вызывает OnMatchEndedByDisconnect → GameOverScreenManager показывает результат
///        - Отправляет результат в RatingService
///   4. Если реконнект произошёл в течение grace period — матч продолжается
///
/// СОБЫТИЯ:
///   OnOpponentDisconnected          — оппонент вышел, начался отсчёт
///   OnOpponentReconnected           — оппонент вернулся
///   OnMatchEndedByDisconnect(id,msg) — матч завершён технической победой
///
/// РАЗМЕЩЕНИЕ:
///   Один GameObject [DisconnectHandler] в Competitive сцене.
///   Зарегистрирован в VContainer через builder.RegisterComponent().
/// </summary>
public class DisconnectHandler : NetworkBehaviour
{
    [Header("Настройки")]
    [SerializeField] private float gracePeriodSeconds = 30f;

    // ─── СОБЫТИЯ ─────────────────────────────────────────────────────────────

    public event Action<ulong> OnOpponentDisconnected;
    public event Action<ulong> OnOpponentReconnected;
    public event Action<ulong, string> OnMatchEndedByDisconnect;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────
    // Заполняется через InjectGameObject → VContainer вызывает Construct()
    private RatingService _ratingService;

    [Inject]
    public void Construct(RatingService ratingService)
    {
        _ratingService = ratingService;
        Debug.Log("✅ [DisconnectHandler] RatingService инжектирован через VContainer");
    }

    // ─── СОСТОЯНИЕ ────────────────────────────────────────────────────────────

    private bool _isGracePeriodActive = false;
    private ulong _disconnectedClientId;
    private Coroutine _gracePeriodCoroutine;

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        // Инжектируем RatingService через VContainer.
        // Вызывает Construct(RatingService) — теперь _ratingService гарантированно non-null.
        InjectionProvider.Container?.InjectGameObject(gameObject);

        if (!IsServer) return;

        NetworkManager.OnClientDisconnectCallback += HandleClientDisconnect;
        NetworkManager.OnClientConnectedCallback += HandleClientReconnect;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsServer) return;

        NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnect;
        NetworkManager.OnClientConnectedCallback -= HandleClientReconnect;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОБРАБОТКА СОБЫТИЙ
    // ─────────────────────────────────────────────────────────────────────────

    private void HandleClientDisconnect(ulong clientId)
    {
        if (!IsServer) return;
        if (!GameModeManager.IsCompetitiveMode()) return;

        // Хост сам себя не считаем дисконнектом
        if (clientId == NetworkManager.LocalClientId) return;

        Debug.Log($"[DisconnectHandler] Client {clientId} disconnected. Grace period: {gracePeriodSeconds}s");

        _disconnectedClientId = clientId;
        _isGracePeriodActive = true;

        NotifyDisconnectClientRpc(clientId, gracePeriodSeconds);

        if (_gracePeriodCoroutine != null) StopCoroutine(_gracePeriodCoroutine);
        _gracePeriodCoroutine = StartCoroutine(GracePeriodCoroutine(clientId));
    }

    private void HandleClientReconnect(ulong clientId)
    {
        if (!IsServer) return;
        if (!_isGracePeriodActive) return;
        if (clientId != _disconnectedClientId) return;

        Debug.Log($"[DisconnectHandler] Client {clientId} reconnected! Match continues.");

        _isGracePeriodActive = false;
        if (_gracePeriodCoroutine != null) StopCoroutine(_gracePeriodCoroutine);

        NotifyReconnectClientRpc(clientId);
        OnOpponentReconnected?.Invoke(clientId);
    }

    private IEnumerator GracePeriodCoroutine(ulong disconnectedClientId)
    {
        yield return new WaitForSecondsRealtime(gracePeriodSeconds);

        if (!_isGracePeriodActive) yield break;

        _isGracePeriodActive = false;

        ulong winnerId = GetRemainingClientId(disconnectedClientId);
        string reason = $"Opponent (client {disconnectedClientId}) disconnected";

        // ИСПРАВЛЕНО v5.0: если winnerId = LocalClientId (сервер), значит оба клиента
        // отключились — матч без победителя. Не сохраняем результат.
        if (winnerId == NetworkManager.LocalClientId)
        {
            Debug.LogWarning($"[DisconnectHandler] Нет оставшихся клиентов. " +
                             $"Client {disconnectedClientId} отключился, других клиентов нет. " +
                             $"Техническая победа не назначается.");
            // Опционально: пометить сессию как abandoned через ServerStartup
            yield break;
        }

        Debug.Log($"[DisconnectHandler] Grace period expired. Winner: client {winnerId}");

        DeclareWinnerClientRpc(winnerId, disconnectedClientId, reason);
        OnMatchEndedByDisconnect?.Invoke(winnerId, reason);

        // _ratingService теперь гарантированно инжектирован — null-check для безопасности
        _ratingService?.RecordTechnicalVictory(winnerId, disconnectedClientId, reason);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CLIENT RPCs
    // ─────────────────────────────────────────────────────────────────────────

    [Rpc(SendTo.Everyone)]
    private void NotifyDisconnectClientRpc(ulong clientId, float graceSeconds)
    {
        OnOpponentDisconnected?.Invoke(clientId);
        Debug.Log($"[DisconnectHandler] Opponent {clientId} disconnected. Match ends in {graceSeconds}s.");
    }

    [Rpc(SendTo.Everyone)]
    private void NotifyReconnectClientRpc(ulong clientId)
    {
        OnOpponentReconnected?.Invoke(clientId);
        Debug.Log($"[DisconnectHandler] Opponent {clientId} reconnected!");
    }

    [Rpc(SendTo.Everyone)]
    private void DeclareWinnerClientRpc(ulong winnerId, ulong loserId, string reason)
    {
        OnMatchEndedByDisconnect?.Invoke(winnerId, reason);
        Debug.Log($"[DisconnectHandler] Technical victory! Winner: {winnerId}, Loser: {loserId} ({reason})");
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ИСПРАВЛЕНО v5.0:
    /// Исключает LocalClientId (сервер, ClientId=0 на dedicated server) из перебора.
    /// Сначала ищет другого клиента, исключая и disconnectedId, и LocalClientId.
    /// Если не находит — возвращает LocalClientId как fallback (вызывающий проверит это).
    /// </summary>
    private ulong GetRemainingClientId(ulong disconnectedId)
    {
        // Приоритет 1: найти другого РЕАЛЬНОГО клиента (не сервер)
        foreach (var clientId in NetworkManager.ConnectedClientsIds)
        {
            if (clientId != disconnectedId && clientId != NetworkManager.LocalClientId)
                return clientId;
        }

        // Приоритет 2: если нет других клиентов — любой оставшийся, кроме отключившегося
        foreach (var clientId in NetworkManager.ConnectedClientsIds)
        {
            if (clientId != disconnectedId)
                return clientId;
        }

        // Fallback: сервер
        return NetworkManager.LocalClientId;
    }

    public bool IsGracePeriodActive() => _isGracePeriodActive;
    public float GracePeriodDuration => gracePeriodSeconds;
}
