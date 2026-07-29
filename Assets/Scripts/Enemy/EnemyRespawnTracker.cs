using UnityEngine;
using VContainer;
using System.Collections.Generic;

/// <summary>
/// EnemyRespawnTracker v2 — ИСПРАВЛЕНО: враги респавнятся на уровне земли, а не над игроком.
///
/// ИСПРАВЛЕНИЯ v2:
///   ✦ RespawnEnemyNearPlayer теперь использует высоту terrain
///   ✦ Добавлен FindGroundPosition для поиска поверхности
///   ✦ Враги больше не респавнятся на Y-координате врага
/// </summary>
public class EnemyRespawnTracker : MonoBehaviour
{
    [Header("Настройки респавна")]
    [Tooltip("На каком расстоянии мобо считается 'потерянным' и нужно телепортировать")]
    [SerializeField] private float maxDistanceBeforeRespawn = 100f;

    [Tooltip("Радиус, на котором мобо переспавнится перед игроком")]
    [SerializeField] private float respawnRadius = 20f;

    [Tooltip("Включить систему отслеживания расстояния?")]
    [SerializeField] private bool enableRespawning = true;

    [Tooltip("Как часто проверять (сек). 0.5 = дважды в секунду.")]
    [SerializeField] private float checkInterval = 0.5f;

    [Tooltip("Минимальная пауза между телепортами одного моба (сек)")]
    [SerializeField] private float teleportCooldown = 5f;

    [Tooltip("Показывать отладку в консоли?")]
    [SerializeField] private bool debugLogging = false;

    [Header("Поиск поверхности")]
    [Tooltip("Высота откуда бросаем луч вниз для поиска земли")]
    [SerializeField] private float raycastHeight = 50f;

    [Tooltip("Слой земли для raycast")]
    [SerializeField] private LayerMask groundLayer = ~0;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────

    private EnemyBatchSystem _enemyBatchSystem;
    private PlayerRegistry _playerRegistry;
    private GameStateService _gameState;
    private EnemySpawner _enemySpawner;

    [Inject]
    public void Construct(
        EnemyBatchSystem enemyBatchSystem,
        PlayerRegistry playerRegistry,
        GameStateService gameState,
        EnemySpawner enemySpawner)
    {
        _enemyBatchSystem = enemyBatchSystem;
        _playerRegistry = playerRegistry;
        _gameState = gameState;
        _enemySpawner = enemySpawner;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Transform _playerTransform;
    private float _checkTimer = 0f;

    // Кулдаун телепорта на каждого моба
    private readonly Dictionary<BaseEnemyAI, float> _lastTeleportTime = new Dictionary<BaseEnemyAI, float>();

    // Кэш: квадрат дистанции
    private float _maxDistSqr;

    void Start()
    {
        _maxDistSqr = maxDistanceBeforeRespawn * maxDistanceBeforeRespawn;
        FindPlayer();
    }

    void Update()
    {
        if (!enableRespawning) return;
        if (_gameState != null && _gameState.IsPaused) return;

        // Интервальная проверка вместо каждого кадра
        _checkTimer -= Time.deltaTime;
        if (_checkTimer > 0f) return;
        _checkTimer = checkInterval;

        if (_playerTransform == null)
        {
            FindPlayer();
            return;
        }

        // Защита: игрок ещё не заспавнился
        if (_playerTransform.position == Vector3.zero) return;

        CheckAndRespawnDistantEnemies();
    }

    void FindPlayer()
    {
        PlayerStats local = _playerRegistry?.GetLocalPlayer();
        if (local != null) { _playerTransform = local.transform; return; }

        bool isSinglePlayer = GameModeManager.IsMode(GameMode.SinglePlayer);
        foreach (var p in FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None))
        {
            if (p.IsOwner || isSinglePlayer) { _playerTransform = p.transform; return; }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ОСНОВНАЯ ЛОГИКА
    // ═════════════════════════════════════════════════════════════════════════

    void CheckAndRespawnDistantEnemies()
    {
        IReadOnlyList<BaseEnemyAI> enemies = GetEnemiesFromBatchSystem();
        if (enemies == null || enemies.Count == 0) return;

        Vector3 playerPos = _playerTransform.position;
        float now = Time.time;

        foreach (var enemy in enemies)
        {
            if (enemy == null || !enemy.gameObject.activeInHierarchy) continue;

            // Кулдаун: не телепортировать одного моба слишком часто
            if (_lastTeleportTime.TryGetValue(enemy, out float lastTime))
                if (now - lastTime < teleportCooldown) continue;

            // sqrMagnitude — без sqrt
            Vector3 diff = enemy.transform.position - playerPos;
            if (diff.sqrMagnitude > _maxDistSqr)
            {
                RespawnEnemyNearPlayer(enemy, playerPos);
                _lastTeleportTime[enemy] = now;
            }
        }

        // Очищаем словарь от уничтоженных врагов
        CleanupStaleEntries(now);
    }

    // ИСПРАВЛЕНИЕ v2: Респавн на уровне земли, а не на высоте врага
    void RespawnEnemyNearPlayer(BaseEnemyAI enemy, Vector3 playerPos)
    {
        if (enemy == null) return;

        // Вычисляем позицию респавна вокруг игрока
        Vector2 circle = Random.insideUnitCircle.normalized * respawnRadius;
        Vector3 desiredPos = playerPos + new Vector3(circle.x, 0f, circle.y);

        // ИСПРАВЛЕНИЕ: Находим высоту terrain в точке респавна
        Vector3 newPos = FindGroundPosition(desiredPos);

        enemy.transform.position = newPos;

        // Сбрасываем скорость Rigidbody если есть
        Rigidbody rb = enemy.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic)
        {
            rb.position = newPos;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        if (debugLogging)
            Debug.Log($"[EnemyRespawnTracker] Телепорт: {enemy.name} → {newPos}");
    }

    /// <summary>
    /// Находит высоту поверхности в указанной позиции
    /// </summary>
    private Vector3 FindGroundPosition(Vector3 desiredXZ)
    {
        // Метод 1: Raycast сверху вниз
        Vector3 rayOrigin = new Vector3(desiredXZ.x, desiredXZ.y + raycastHeight, desiredXZ.z);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, raycastHeight * 2f, groundLayer))
        {
            return hit.point + Vector3.up * 0.5f; // +0.5f чтобы враг не застрял в земле
        }

        // Метод 2: Используем Terrain.SampleHeight если есть terrain
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null)
        {
            float terrainHeight = terrain.SampleHeight(desiredXZ);
            float worldY = terrainHeight + terrain.transform.position.y;
            return new Vector3(desiredXZ.x, worldY + 0.5f, desiredXZ.z);
        }

        // Fallback: используем высоту игрока
        if (_playerTransform != null)
        {
            return new Vector3(desiredXZ.x, _playerTransform.position.y + 0.5f, desiredXZ.z);
        }

        // Последний fallback
        return desiredXZ + Vector3.up * 0.5f;
    }

    void CleanupStaleEntries(float now)
    {
        if (_lastTeleportTime.Count == 0) return;

        var toRemove = new List<BaseEnemyAI>();
        foreach (var pair in _lastTeleportTime)
        {
            if (pair.Key == null || now - pair.Value > teleportCooldown * 2f)
                toRemove.Add(pair.Key);
        }
        foreach (var key in toRemove)
            _lastTeleportTime.Remove(key);
    }

    IReadOnlyList<BaseEnemyAI> GetEnemiesFromBatchSystem()
    {
        if (_enemyBatchSystem == null) return null;
        return _enemyBatchSystem.GetActiveEnemyList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DEBUG ВИЗУАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        if (!enableRespawning || _playerTransform == null) return;
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(_playerTransform.position, maxDistanceBeforeRespawn);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(_playerTransform.position, respawnRadius);
    }
}
