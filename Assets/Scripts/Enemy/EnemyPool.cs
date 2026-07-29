using UnityEngine;
using System.Collections.Generic;
using VContainer;
using VContainer.Unity;

/// <summary>
/// EnemyPool — пул врагов.
///
/// ═══════════════════════════════════════════════════════════════════
/// ФИКС: УБРАН DontDestroyOnLoad
/// ═══════════════════════════════════════════════════════════════════
///
///   БЫЛО (проблема):
///     Awake() → DontDestroyOnLoad(gameObject)
///     EnemyPool живёт в PlayerWorldScene, но уходит в DDOL.
///     При перезапуске:
///       1. Старый EnemyPool в DDOL — жив, Instance != null
///       2. Новая PlayerWorldScene создаёт новый EnemyPool
///       3. Awake(): Instance != null → Destroy(gameObject) — УНИЧТОЖАЕТ СЕБЯ!
///       4. EnemySpawner держит ссылку на уничтоженный объект → MissingReferenceException
///       5. Пул пустой (PrewarmPool не успел вызваться) → GetEnemy падает
///
///   СТАЛО:
///     DontDestroyOnLoad убран полностью.
///     EnemyPool живёт и умирает вместе с PlayerWorldScene — это правильно.
///     При выгрузке PlayerWorldScene все враги уничтожаются вместе с пулом.
///     Instance обновляется на актуальный пул при каждой загрузке сцены.
///
/// ═══════════════════════════════════════════════════════════════════
/// СПИСОК АКТИВНЫХ ВРАГОВ — GetActiveEnemyHealths()
/// ═══════════════════════════════════════════════════════════════════
///
///   Используется оружиями вместо FindObjectsByType<EnemyHealth>():
///     SpeedStaffWeapon.FindTargets()
///     AcidFlaskWeapon.FindNearestEnemy()
///
///   O(1) доступ к кэшированному списку вместо O(N) сканирования сцены.
///   Список обновляется в GetEnemy() / ReturnEnemy() / ReturnMiniBoss().
/// </summary>
public class EnemyPool : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    // INSTANCE — обновляется при каждой загрузке PlayerWorldScene
    // ─────────────────────────────────────────────────────────────────────────
    public static EnemyPool Instance { get; private set; }

    [Header("Pool Settings")]
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private GameObject miniBossPrefab;
    [SerializeField] private int initialEnemyPoolSize = 700;
    [SerializeField] private int initialBossPoolSize = 5;
    [SerializeField] private int maxActiveEnemies = 700;
    [SerializeField] private int maxActiveBosses = 10;

    private Queue<GameObject> _enemyPool = new Queue<GameObject>();
    private Queue<GameObject> _miniBossPool = new Queue<GameObject>();

    private int _activeEnemies = 0;
    private int _activeMiniBosses = 0;
    private int _totalEnemiesCreated = 0;
    private int _totalBossesCreated = 0;

    // ─── КЭШИ КОМПОНЕНТОВ ────────────────────────────────────────────────────
    private Dictionary<GameObject, EnemyHealth> _enemyHealthCache = new Dictionary<GameObject, EnemyHealth>(700);
    private Dictionary<GameObject, BaseEnemyAI> _enemyAICache = new Dictionary<GameObject, BaseEnemyAI>(700);

    // ─── СПИСОК АКТИВНЫХ ВРАГОВ ──────────────────────────────────────────────
    private readonly List<EnemyHealth> _activeEnemyHealths = new List<EnemyHealth>(700);

    // ─── INJECTION ────────────────────────────────────────────────────────────

    private IObjectResolver _resolver;

    [Inject]
    public void Construct(IObjectResolver resolver)
    {
        _resolver = resolver;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        // Просто обновляем Instance на актуальный пул этой сцены.
        // DontDestroyOnLoad убран — пул живёт вместе с PlayerWorldScene.
        Instance = this;
    }

    void Start()
    {
        if (enemyPrefab == null)
        {
            Debug.LogError("[EnemyPool] ❌ enemyPrefab не назначен в Inspector! Пул врагов пуст.");
            return;
        }

        PrewarmPool(enemyPrefab, _enemyPool, initialEnemyPoolSize);
        PrewarmPool(miniBossPrefab, _miniBossPool, initialBossPoolSize);

        Debug.Log($"[EnemyPool] ✅ Прогрев завершён: {_enemyPool.Count} врагов, {_miniBossPool.Count} боссов");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PREWARM
    // ─────────────────────────────────────────────────────────────────────────

    private void PrewarmPool(GameObject prefab, Queue<GameObject> pool, int count)
    {
        if (prefab == null) return;

        for (int i = 0; i < count; i++)
        {
            var go = Instantiate(prefab, transform);
            go.SetActive(false);
            CacheEnemyComponents(go);
            pool.Enqueue(go);
        }
    }

    private void CacheEnemyComponents(GameObject go)
    {
        if (!_enemyHealthCache.ContainsKey(go))
            _enemyHealthCache[go] = go.GetComponent<EnemyHealth>();
        if (!_enemyAICache.ContainsKey(go))
            _enemyAICache[go] = go.GetComponent<BaseEnemyAI>();

        if (_resolver != null)
            _resolver.InjectGameObject(go);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GET
    // ─────────────────────────────────────────────────────────────────────────

    public GameObject GetEnemy(Vector3 position, float healthMultiplier = 1f)
    {
        if (_activeEnemies >= maxActiveEnemies) return null;

        GameObject go;
        if (_enemyPool.Count > 0)
        {
            go = _enemyPool.Dequeue();
        }
        else
        {
            Debug.LogWarning($"[EnemyPool] ⚠️ Пул врагов исчерпан! " +
                             $"Создаём врага динамически (всего создано: {_totalEnemiesCreated + 1}). " +
                             $"Установите initialEnemyPoolSize = {maxActiveEnemies} чтобы избежать просадок FPS.");
            go = Instantiate(enemyPrefab, transform);
            go.SetActive(false);
            CacheEnemyComponents(go);
            _totalEnemiesCreated++;
        }

        ResetEnemy(go, position, healthMultiplier);
        go.SetActive(true);
        _activeEnemies++;

        if (_enemyHealthCache.TryGetValue(go, out var health) && health != null)
            _activeEnemyHealths.Add(health);

        return go;
    }

    public GameObject GetMiniBoss(Vector3 position, float healthMultiplier = 1f)
    {
        if (_activeMiniBosses >= maxActiveBosses || miniBossPrefab == null) return null;

        GameObject go;
        if (_miniBossPool.Count > 0)
        {
            go = _miniBossPool.Dequeue();
        }
        else
        {
            Debug.LogWarning($"[EnemyPool] ⚠️ Пул боссов исчерпан! Создаём динамически.");
            go = Instantiate(miniBossPrefab, transform);
            go.SetActive(false);
            CacheEnemyComponents(go);
            _totalBossesCreated++;
        }

        ResetEnemy(go, position, healthMultiplier);
        go.SetActive(true);
        _activeMiniBosses++;

        if (_enemyHealthCache.TryGetValue(go, out var health) && health != null)
            _activeEnemyHealths.Add(health);

        return go;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RETURN
    // ─────────────────────────────────────────────────────────────────────────

    public void ReturnEnemy(GameObject go)
    {
        if (go == null) return;

        if (_enemyHealthCache.TryGetValue(go, out var health) && health != null)
            _activeEnemyHealths.Remove(health);

        go.SetActive(false);
        _enemyPool.Enqueue(go);
        _activeEnemies = Mathf.Max(0, _activeEnemies - 1);
    }

    public void ReturnMiniBoss(GameObject go)
    {
        if (go == null) return;

        if (_enemyHealthCache.TryGetValue(go, out var health) && health != null)
            _activeEnemyHealths.Remove(health);

        go.SetActive(false);
        _miniBossPool.Enqueue(go);
        _activeMiniBosses = Mathf.Max(0, _activeMiniBosses - 1);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RESET
    // ─────────────────────────────────────────────────────────────────────────

    private void ResetEnemy(GameObject go, Vector3 position, float healthMult)
    {
        go.transform.position = position;
        go.transform.rotation = Quaternion.identity;

        if (_enemyHealthCache.TryGetValue(go, out var health))
        {
            health.SetMaxHealth(health.GetBaseMaxHealth() * healthMult);
            health.ResetState();
        }

        if (_enemyAICache.TryGetValue(go, out var ai) && ai != null)
            ai.ResetToBase();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КЭШИ
    // ─────────────────────────────────────────────────────────────────────────

    public EnemyHealth GetCachedEnemyHealth(GameObject go)
    {
        _enemyHealthCache.TryGetValue(go, out var h);
        return h;
    }

    public EnemyHealth GetCachedHealth(GameObject go) => GetCachedEnemyHealth(go);

    public BaseEnemyAI GetCachedEnemyAI(GameObject go)
    {
        _enemyAICache.TryGetValue(go, out var a);
        return a;
    }

    public BaseEnemyAI GetCachedMiniBossAI(GameObject go) => GetCachedEnemyAI(go);

    // ─────────────────────────────────────────────────────────────────────────
    // АКТИВНЫЕ ВРАГИ
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<EnemyHealth> GetActiveEnemyHealths() => _activeEnemyHealths;

    // ─────────────────────────────────────────────────────────────────────────
    // STATS / DEBUG
    // ─────────────────────────────────────────────────────────────────────────

    public int ActiveEnemies => _activeEnemies;
    public int ActiveBosses => _activeMiniBosses;
    public int PooledEnemies => _enemyPool.Count;
    public int PooledBosses => _miniBossPool.Count;
    public bool CanSpawnEnemy => _activeEnemies < maxActiveEnemies;
    public bool CanSpawnBoss => _activeMiniBosses < maxActiveBosses;

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public void DebugPrint()
    {
        Debug.Log($"[EnemyPool] Active: {_activeEnemies}/{maxActiveEnemies} enemies, " +
                  $"{_activeMiniBosses}/{maxActiveBosses} bosses | " +
                  $"Pool: {_enemyPool.Count} + {_miniBossPool.Count}");
    }
}