using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using VContainer;
using VContainer.Unity;

/// <summary>
/// SpectatorBroadcaster — сетевой компонент на PlayerPrefab.
///
/// ═══════════════════════════════════════════════════════════════════════
/// ЗАЧЕМ НУЖЕН ЭТОТ КЛАСС
/// ═══════════════════════════════════════════════════════════════════════
/// Враги в ShadowMultiplayer — ЛОКАЛЬНЫЕ объекты (не сетевые).
/// Сервер ничего не знает об их позициях.
/// Чтобы спектатор видел врагов оппонента, нужно:
///   1. Владелец мира собирает позиции из EnemyBatchSystem
///   2. Отправляет их на сервер через UploadSnapshotRpc
///   3. Сервер ретранслирует данные спектатору через DeliverSnapshotRpc
///
/// ПОТОК ДАННЫХ:
///   [Владелец] → UploadSnapshotRpc → [Сервер] → DeliverSnapshotRpc → [Спектатор]
///   Частота: 10 снапшотов/сек (sendInterval). Трафик: ~90 KB/с.
///
/// ═══════════════════════════════════════════════════════════════════════
/// РЕФАКТОРИНГ — УБРАНЫ EnemyBatchSystem.Instance и SpectatorManager.Instance:
/// ═══════════════════════════════════════════════════════════════════════
///
///   БЫЛО (проблемы):
///     CollectAndSendSnapshot() → EnemyBatchSystem.Instance.GetSpectatorSnapshot(...)
///     DeliverSnapshotRpc()     → SpectatorManager.Instance?.OnSnapshotReceived(...)
///
///     Скрытые зависимости через статику. Порядок инициализации хрупкий.
///     При добавлении новой сцены Instance мог вернуть устаревший объект.
///
///   СТАЛО:
///     EnemyBatchSystem и SpectatorManager инжектируются через [Inject] Construct().
///     SpectatorBroadcaster — NetworkBehaviour, Netcode спавнит его в обход VContainer.
///     Тот же паттерн что PlayerStats/PlayerMovement:
///       InjectionProvider.Container?.InjectGameObject(gameObject) в OnNetworkSpawn().
///
/// ШАГ 4 РЕФАКТОРИНГА — GameStateService:
///   БЫЛО:  if (LevelUpManager.IsPaused) return;
///   СТАЛО: if (_gameState != null && _gameState.IsPaused) return;
/// </summary>
public class SpectatorBroadcaster : NetworkBehaviour
{
    [Header("Настройки трансляции")]
    [Tooltip("Интервал отправки снапшота в секундах. 0.1 = 10 раз/сек")]
    [SerializeField] private float sendInterval = 0.1f;

    // ─── СЕРВЕРНОЕ СОСТОЯНИЕ ─────────────────────────────────────────────────
    private ulong _spectatorClientId = ulong.MaxValue;

    private readonly NetworkVariable<bool> _isBeingWatched = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // ─── СОСТОЯНИЕ ВЛАДЕЛЬЦА ──────────────────────────────────────────────────
    private float _nextSendTime;
    private readonly List<EnemyGhostData> _snapshotBuffer = new List<EnemyGhostData>(700);

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────
    // Все три инжектируются через InjectGameObject в OnNetworkSpawn.
    // Null-safe: если инъекция не прошла — трансляция не ломается, просто не работает.

    private GameStateService _gameState;
    private EnemyBatchSystem _enemyBatchSystem;
    private SpectatorManager _spectatorManager;

    [Inject]
    public void Construct(
        GameStateService gameState,
        SpectatorManager spectatorManager)
    {
        _gameState = gameState;
        _spectatorManager = spectatorManager;
        // EnemyBatchSystem находится лениво — живёт в PlayerWorldScope (PlayerWorldScene),
        // а SpectatorBroadcaster инжектируется из BaseLifetimeScope. Прямая инжекция невозможна.
    }

    private EnemyBatchSystem FindEnemyBatchSystem()
    {
        if (_enemyBatchSystem != null) return _enemyBatchSystem;
        // SpectatorBroadcaster — NetworkBehaviour на Player. После WorldSceneLoader
        // Player перемещён в PlayerWorldScene, где живёт EnemyBatchSystem.
        foreach (var root in gameObject.scene.GetRootGameObjects())
        {
            var b = root.GetComponentInChildren<EnemyBatchSystem>(true);
            if (b != null) { _enemyBatchSystem = b; return b; }
        }
        _enemyBatchSystem = Object.FindFirstObjectByType<EnemyBatchSystem>();
        return _enemyBatchSystem;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    public override void OnNetworkSpawn()
    {
        // NetworkBehaviour спавнится Netcode в обход VContainer.
        // InjectGameObject проходит по компонентам gameObject и вызывает [Inject] Construct().
        // InjectionProvider.Container гарантированно установлен до спавна игроков.
        try { InjectionProvider.Container?.InjectGameObject(gameObject); }
        catch (System.Exception ex) { Debug.LogError($"[SpectatorBroadcaster] {ex.Message}"); }

        if (IsServer)
        {
            _spectatorClientId = ulong.MaxValue;
            _isBeingWatched.Value = false;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            _spectatorClientId = ulong.MaxValue;
            _isBeingWatched.Value = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE — сбор и отправка снапшота
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!IsOwner) return;
        if (!_isBeingWatched.Value) return;
        if (!GameModeManager.IsCompetitiveMode()) return;

        // ШАГ 4 РЕФАКТОРИНГА:
        // При паузе (LevelUp) враги стоят — нет смысла слать их позиции.
        // Null-safe: без GameStateService — трансляция не прерывается.
        if (_gameState != null && _gameState.IsPaused) return;

        if (Time.time < _nextSendTime) return;

        _nextSendTime = Time.time + sendInterval;
        CollectAndSendSnapshot();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РЕГИСТРАЦИЯ / ОТМЕНА НАБЛЮДЕНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    [Rpc(SendTo.Server)]
    public void RegisterSpectatorRpc(ulong spectatorClientId)
    {
        _spectatorClientId = spectatorClientId;
        _isBeingWatched.Value = true;
        Debug.Log($"[SpectatorBroadcaster] ClientId={OwnerClientId} наблюдает ClientId={spectatorClientId}. Трансляция запущена.");
    }

    [Rpc(SendTo.Server)]
    public void UnregisterSpectatorRpc()
    {
        _spectatorClientId = ulong.MaxValue;
        _isBeingWatched.Value = false;
        Debug.Log($"[SpectatorBroadcaster] ClientId={OwnerClientId} — наблюдение остановлено.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СБОРКА СНАПШОТА
    // ─────────────────────────────────────────────────────────────────────────

    void CollectAndSendSnapshot()
    {
        _snapshotBuffer.Clear();

        // Используем инжектированный _enemyBatchSystem вместо статического Instance.
        // Null-safe: если инъекция не прошла (например, DI ещё не инициализирован) —
        // пропускаем кадр трансляции без ошибки.
        var batch = FindEnemyBatchSystem();
        if (batch != null)
            batch.GetSpectatorSnapshot(_snapshotBuffer);

        if (_snapshotBuffer.Count == 0) return;

        UploadSnapshotRpc(_snapshotBuffer.ToArray());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СЕТЕВАЯ ПЕРЕДАЧА СНАПШОТА
    // ─────────────────────────────────────────────────────────────────────────

    [Rpc(SendTo.Server)]
    void UploadSnapshotRpc(EnemyGhostData[] enemies)
    {
        if (_spectatorClientId == ulong.MaxValue) return;
        var rpcParams = RpcTarget.Single(_spectatorClientId, RpcTargetUse.Temp);
        DeliverSnapshotRpc(enemies, rpcParams);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    void DeliverSnapshotRpc(EnemyGhostData[] enemies, RpcParams rpcParams = default)
    {
        // Используем инжектированный _spectatorManager вместо статического Instance.
        // Null-safe: если игрок не является спектатором — OnSnapshotReceived игнорируется.
        _spectatorManager?.OnSnapshotReceived(enemies);
    }

    // ─────────────────────────────────────────────────────────────────────────

    public new ulong OwnerClientId => base.OwnerClientId;
    public bool IsBeingWatched => _isBeingWatched.Value;
}