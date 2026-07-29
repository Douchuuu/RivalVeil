using UnityEngine;
using System.Collections.Generic;
using VContainer;

/// <summary>
/// EnemySpawner — ОБНОВЛЁН: сложность персонажа читается из CharacterMultipliers.
///
/// ═══════════════════════════════════════════════════════════════════
/// СИСТЕМА СЛОЖНОСТИ ПЕРСОНАЖА:
/// ═══════════════════════════════════════════════════════════════════
///
///   CharacterMultipliers.GetEnemyDifficultyBonus() → значение в %
///   ratio = enemyDifficultyBonus / 100f
///
///   0%   → без изменений (мобы масштабируются только от времени)
///   100% → +100% — удвоенный спавн, мобы вдвое сильнее поверх времени
///
///   ФОРМУЛЫ:
///
///   Мобов за тик:
///     count = Clamp(1 + Floor(ratio × SpawnBonusPerHundredPct), 1, MaxSpawnPerTick)
///
///   Интервал спавна:
///     interval = timeScaled / (1 + ratio × IntervalSpeedBonus)
///
///   HP / Урон / Скорость:
///     итог = timeMult × (1 + ratio × соответствующий_бонус)
///
/// ═══════════════════════════════════════════════════════════════════
/// КАК НАЗНАЧИТЬ СЛОЖНОСТЬ ПЕРСОНАЖУ:
/// ═══════════════════════════════════════════════════════════════════
///
///   В Inspector на объекте персонажа, компонент CharacterMultipliers:
///   Поле "Enemy Difficulty Bonus" → выставить значение (например 100).
///
///   Или через предмет в ItemInstance:
///   charMult.AddEnemyDifficultyBonus(25f);
///
/// ═══════════════════════════════════════════════════════════════════
/// ИСТОРИЯ ИЗМЕНЕНИЙ (предыдущие):
/// ═══════════════════════════════════════════════════════════════════
///
/// FindGroundPosition() — многоуровневый поиск поверхности.
/// Рой с rate-limiter (_swarmSpawnTimer).
/// GameStateService вместо LevelUpManager.IsPaused.
/// FindPlayer() fallback для SinglePlayer.
/// SpawnMiniBoss() — синхронизация Rigidbody после телепорта.
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — ОСНОВНОЕ
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Префабы ━━━")]
    public GameObject enemyPrefab;
    public GameObject miniBossPrefab;

    [Header("━━━ Радиусы спавна ━━━")]
    [Tooltip("Радиус вокруг игрока, на котором появляются враги (как в Vampire Survivors).")]
    public float spawnRadius = 20f;

    [Tooltip("Радиус 'вне экрана' — туда спавним, потом телепортируем на spawnRadius.")]
    [SerializeField] private float offScreenRadius = 50f;

    [Tooltip("Разброс радиуса спавна — каждый моб смещается на случайную величину от −jitter до +jitter.\n" +
             "0 = все мобы на одинаковом расстоянии (кольцо).\n" +
             "5 = мобы появляются в диапазоне [spawnRadius−5 .. spawnRadius+5].\n" +
             "Создаёт визуальную глубину — кто ближе, кто дальше.")]
    [SerializeField] private float spawnRadiusJitter = 5f;

    [Tooltip("Включить мгновенный телепорт мобов на spawnRadius (Vampire Survivors механика)?")]
    [SerializeField] private bool useVampireSurvivalsMechanics = true;

    [Header("━━━ Интервал спавна ━━━")]
    [Tooltip("Базовый интервал между обычными спавнами (сек). Сокращается со временем и от сложности персонажа.")]
    public float timeBetweenSpawns = 2f;

    [Header("━━━ Этап / длительность ━━━")]
    [Tooltip("Общая длительность этапа (мин). После истечения — финальный рой.")]
    public float stageTimeMinutes = 10f;

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — ВРЕМЕННОЕ МАСШТАБИРОВАНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Временное масштабирование (от таймера) ━━━")]
    [Tooltip("На сколько увеличивается HP мобов за каждую минуту игры. 0.15 = +15%/мин.")]
    [SerializeField] private float hpMultiplierPerMinute = 0.15f;

    [Tooltip("На сколько увеличивается урон мобов за каждую минуту игры. 0.10 = +10%/мин.")]
    [SerializeField] private float damageMultiplierPerMinute = 0.1f;

    [Tooltip("На сколько увеличивается скорость мобов за каждую минуту. 0.05 = +5%/мин.")]
    [SerializeField] private float speedMultiplierPerMinute = 0.05f;

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — МАСШТАБИРОВАНИЕ ОТ СЛОЖНОСТИ ПЕРСОНАЖА
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Масштабирование от сложности персонажа ━━━")]
    [Tooltip("Сколько дополнительных врагов добавляется за тик при каждых +100% сложности.\n" +
             "Итого за тик = 1 + Floor(ratio × SpawnBonusPerHundredPct).\n" +
             "При 500% и значении 140: 1 + floor(5×140) = 701 → капается до maxSpawnPerTick.")]
    [SerializeField] private int spawnBonusPerHundredPct = 140;

    [Tooltip("Порог сложности (в %), выше которого количество мобов за тик больше не растёт.\n" +
             "Сложность сверх этого порога продолжает усиливать характеристики врагов (HP, DMG, SPD).\n" +
             "500 = при 500% пул 700 заполнен, дальше растут только статы врагов.")]
    [SerializeField] private float spawnCapDifficultyPct = 500f;

    [Tooltip("Максимум врагов за один тик спавна. Жёсткий потолок независимо от сложности.\n" +
             "Итоговый лимит — также ограничен пулом (700 мобов одновременно).")]
    [SerializeField] private int maxSpawnPerTick = 10;

    [Tooltip("Базовое количество врагов за тик при 0% сложности персонажа.\n" +
             "Итого = Clamp(baseSpawnPerTick + Floor(ratio × spawnBonusPerHundredPct), baseSpawnPerTick, maxSpawnPerTick).\n" +
             "Примеры (base=1, bonus=4):  0%=1, 50%=3, 100%=5\n" +
             "Примеры (base=3, bonus=4):  0%=3, 50%=5, 100%=7")]
    [SerializeField] private int baseSpawnPerTick = 1;

    [Tooltip("На сколько увеличивается количество мобов за тик каждую минуту.\n" +
             "0 = без временного масштабирования.\n" +
             "0.5 = каждые 2 минуты +1 моб за тик (floor).\n" +
             "Итого за тик = Clamp(base + Floor(мин × spawnPerMinute) + Floor(ratio × bonus), base, max)")]
    [SerializeField] private float spawnCountPerMinute = 0f;

    [Tooltip("Насколько ускоряется спавн при каждых +100% сложности.\n" +
             "interval = base / (1 + ratio × IntervalSpeedBonus).\n" +
             "0.5 = при 100% интервал в 1.5× короче (на 33% быстрее).")]
    [SerializeField] private float intervalSpeedBonus = 0.5f;

    [Tooltip("Бонус к HP мобов при каждых +100% сложности.\n" +
             "hpMult = timeMult × (1 + ratio × DifficultyHpBonus).\n" +
             "1.0 = при 100% HP удваивается поверх временного масштаба.")]
    [SerializeField] private float difficultyHpBonus = 1.0f;

    [Tooltip("Бонус к урону мобов при каждых +100% сложности.\n" +
             "0.5 = при 100% урон × 1.5 поверх временного масштаба.")]
    [SerializeField] private float difficultyDamageBonus = 0.5f;

    [Tooltip("Бонус к скорости мобов при каждых +100% сложности (итог зажат в × 3.0).\n" +
             "0.3 = при 100% скорость × 1.3 поверх временного масштаба.")]
    [SerializeField] private float difficultySpeedBonus = 0.3f;

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — РОЙ
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Настройки роя ━━━")]
    [Tooltip("Интервал между спавнами в режиме роя (сек). Каждый тик = 2+ врага.\n" +
             "0.02 = 100 врагов/сек (мощное железо)\n" +
             "0.05 = 40 врагов/сек  (рекомендуется)\n" +
             "0.10 = 20 врагов/сек  (слабые машины)")]
    [SerializeField] private float swarmSpawnInterval = 0.05f;

    [Header("━━━ Ограничения спавна по времени ━━━")]
    [Tooltip("Жёсткие ограничения спавна на определённых минутах игры.\n\n" +
             "Каждая запись задаёт:\n" +
             "  fromMinute      — с какой минуты действует ограничение\n" +
             "  maxSpawnPerTick — максимум мобов за один тик (не больше globalMaxSpawnPerTick)\n" +
             "  minInterval     — минимальный интервал между тиками (сек)\n\n" +
             "Записи сортируются по fromMinute автоматически.\n" +
             "Берётся последняя запись у которой fromMinute ≤ текущей минуте.\n\n" +
             "Пример:\n" +
             "  { fromMinute=0, maxSpawnPerTick=25, minInterval=5 }\n" +
             "  { fromMinute=2, maxSpawnPerTick=50, minInterval=4 }\n" +
             "  { fromMinute=5, maxSpawnPerTick=100, minInterval=2 }")]
    [SerializeField]
    private List<SpawnTimeCap> spawnTimeCaps = new List<SpawnTimeCap>
    {
        new SpawnTimeCap { fromMinute = 0, maxSpawnPerTick = 25, minInterval = 5f },
        new SpawnTimeCap { fromMinute = 2, maxSpawnPerTick = 50, minInterval = 4f },
        new SpawnTimeCap { fromMinute = 5, maxSpawnPerTick = 100, minInterval = 2f },
    };

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — ВОЛНЫ
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Конфиг волн ━━━")]
    [Tooltip("ScriptableObject с настройкой всех волновых событий.\n" +
             "Создай через Assets → Create → Game → Wave Config.")]
    [SerializeField] private WaveConfig waveConfig;

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — ПОИСК ПОВЕРХНОСТИ
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Поиск поверхности ━━━")]
    [Tooltip("Высота откуда бросаем луч вниз (юниты). Должна быть выше любой точки уровня.")]
    [SerializeField] private float spawnRaycastHeight = 120f;

    [Tooltip("Радиус поиска коллайдеров, если луч не нашёл поверхность.")]
    [SerializeField] private float fallbackSearchRadius = 15f;

    [Tooltip("Слой игрока — исключаем из поиска поверхности.")]
    [SerializeField] private LayerMask playerLayerMask = 0;

    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR — ДЕБАГ
    // ─────────────────────────────────────────────────────────────────────────

    [Header("━━━ Отладка (только в Editor) ━━━")]
    [Tooltip("Лог текущих множителей в консоль каждые N секунд. 0 = выключено.")]
    [SerializeField] private float debugLogInterval = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    // RUNTIME STATE
    // ─────────────────────────────────────────────────────────────────────────

    private float _currentDifficultyMultiplier = 1.0f;
    private float _currentDamageMultiplier = 1.0f;
    private float _currentSpeedMultiplier = 1.0f;

    private bool _isFinalSwarm = false;
    private bool _isInitialized = false;
    private float _spawnTimer;
    private Transform _playerTransform;

    private bool _isSwarmActive = false;
    private float _swarmEndTime = 0f;
    private float _swarmSpawnTimer = 0f;

    private float _nextFindPlayerTime = 0f;
    private const float FIND_PLAYER_RETRY = 0.5f;

    private int _groundLayerMask = ~0;

    // Кэш — получаем один раз при FindPlayer()
    private CharacterMultipliers _charMultipliers;
    private float _debugLogTimer = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    // DI ЗАВИСИМОСТИ
    // ─────────────────────────────────────────────────────────────────────────

    private LocalGameTimer _localTimer;
    private EnemyPool _enemyPool;
    private PlayerRegistry _playerRegistry;
    private GameStateService _gameState;

    [Inject]
    public void Construct(
        LocalGameTimer localTimer,
        EnemyPool enemyPool,
        PlayerRegistry registry,
        GameStateService gameState)
    {
        _localTimer = localTimer;
        _enemyPool = enemyPool;
        _playerRegistry = registry;
        _gameState = gameState;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LIFECYCLE
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (!GameModeManager.IsSelecting())
            Initialize();
    }

    void Update()
    {
        if (!_isInitialized && !GameModeManager.IsSelecting())
            Initialize();

        if (GameModeManager.IsSelecting() || !_isInitialized) return;

        if (_gameState != null && _gameState.IsPaused) return;

        if (_playerTransform == null)
        {
            if (Time.time >= _nextFindPlayerTime)
            {
                _nextFindPlayerTime = Time.time + FIND_PLAYER_RETRY;
                FindPlayer();
            }
            return;
        }

        HandleTimer();
        HandleSpawning();
        HandleDebugLog();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void Initialize()
    {
        _spawnTimer = timeBetweenSpawns;
        _isFinalSwarm = false;
        _currentDifficultyMultiplier = 1.0f;
        _currentDamageMultiplier = 1.0f;
        _currentSpeedMultiplier = 1.0f;

        _isSwarmActive = false;
        _swarmSpawnTimer = 0f;

        _groundLayerMask = ~playerLayerMask;

        if (waveConfig != null)
        {
            foreach (var waveEvent in waveConfig.WaveEvents)
                waveEvent.HasFired = false;
        }
        else
        {
            Debug.LogWarning("[EnemySpawner] waveConfig не назначен! Волн не будет.");
        }

        FindPlayer();
        _isInitialized = true;
        Debug.Log($"✅ EnemySpawner initialized. Mode: {GameModeManager.GetCurrentMode()}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ИГРОКА + КЭШИРОВАНИЕ CharacterMultipliers
    // ─────────────────────────────────────────────────────────────────────────

    void FindPlayer()
    {
        // Быстрый путь — через реестр
        PlayerStats local = _playerRegistry?.GetLocalPlayer();
        if (local != null)
        {
            _playerTransform = local.transform;
            _charMultipliers = local.GetComponent<CharacterMultipliers>();
            return;
        }

        // Fallback для SinglePlayer / без сети
        var nm = Unity.Netcode.NetworkManager.Singleton;
        bool networkListening = nm != null && nm.IsListening;
        bool isSinglePlayer = GameModeManager.IsMode(GameMode.SinglePlayer);

        foreach (var p in FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None))
        {
            if (p.IsOwner || !networkListening || isSinglePlayer)
            {
                _playerTransform = p.transform;
                _charMultipliers = p.GetComponent<CharacterMultipliers>();
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЧТЕНИЕ СЛОЖНОСТИ ПЕРСОНАЖА
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ratio = CharacterMultipliers.GetEnemyDifficultyBonus() / 100f
    /// 0.0 = 0% стандарт, 1.0 = 100% бонус, 2.0 = 200% и т.д.
    /// </summary>
    private float GetPlayerDifficultyRatio()
    {
        if (_charMultipliers == null) return 0f;
        return Mathf.Max(0f, _charMultipliers.GetEnemyDifficultyBonus() / 100f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ТАЙМЕР И МАСШТАБИРОВАНИЕ
    // ─────────────────────────────────────────────────────────────────────────

    void HandleTimer()
    {
        if (_localTimer == null) return;

        float elapsed = _localTimer.GetTimeElapsed();
        float minutes = elapsed / 60f;

        if (_localTimer.IsTimeExpired() && !_isFinalSwarm)
        {
            _isFinalSwarm = true;
            Debug.Log("⏰ LOCAL TIMER EXPIRED! FINAL SWARM!");
        }

        // Временные множители (только от таймера)
        float timeDiffMult = _isFinalSwarm
            ? Mathf.Clamp(2.5f + Mathf.Max(0, elapsed - stageTimeMinutes * 60f) * 0.01f, 2.5f, 10f)
            : 1.0f + minutes * hpMultiplierPerMinute;

        float timeDmgMult = _isFinalSwarm ? timeDiffMult : 1.0f + minutes * damageMultiplierPerMinute;
        float timeSpeedMult = Mathf.Clamp(1.0f + minutes * speedMultiplierPerMinute, 1.0f, 2.5f);

        // Бонус от CharacterMultipliers.EnemyDifficultyBonus — мультипликативно поверх времени
        float ratio = GetPlayerDifficultyRatio();

        _currentDifficultyMultiplier = timeDiffMult * (1f + ratio * difficultyHpBonus);
        _currentDamageMultiplier = timeDmgMult * (1f + ratio * difficultyDamageBonus);
        _currentSpeedMultiplier = Mathf.Clamp(
            timeSpeedMult * (1f + ratio * difficultySpeedBonus), 1f, 3f);

        CheckEvents(minutes);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВОЛНОВЫЕ СОБЫТИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void CheckEvents(float minutes)
    {
        if (waveConfig == null) return;

        foreach (var waveEvent in waveConfig.WaveEvents)
        {
            if (waveEvent.HasFired) continue;
            if (minutes < waveEvent.TriggerTimeMinutes) continue;
            waveEvent.HasFired = true;
            FireEvent(waveEvent);
        }
    }

    void FireEvent(WaveEvent waveEvent)
    {
        switch (waveEvent.EventType)
        {
            case WaveEventType.EnemyWave:
                Debug.Log($"🔔 ВОЛНА в {waveEvent.TriggerTimeMinutes} мин — {waveEvent.EnemyCount} врагов");
                for (int i = 0; i < waveEvent.EnemyCount; i++) SpawnEnemy();
                break;

            case WaveEventType.MiniBoss:
                Debug.Log($"🔴 БОСС в {waveEvent.TriggerTimeMinutes} мин + {waveEvent.EnemyCount} врагов");
                SpawnMiniBoss();
                for (int i = 0; i < waveEvent.EnemyCount; i++) SpawnEnemy();
                break;

            case WaveEventType.Swarm:
                Debug.Log($"⚔️ РОЙ в {waveEvent.TriggerTimeMinutes} мин на {waveEvent.SwarmDurationSeconds} сек");
                StartSwarm(waveEvent.SwarmDurationSeconds);
                break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ОГРАНИЧЕНИЕ СПАВНА ПО ВРЕМЕНИ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает активную запись SpawnTimeCap для текущей минуты, или null если список пуст.
    /// Берётся последняя запись у которой fromMinute ≤ minutes.
    /// </summary>
    private SpawnTimeCap GetTimeCap(float minutes)
    {
        if (spawnTimeCaps == null || spawnTimeCaps.Count == 0) return null;

        SpawnTimeCap active = null;
        foreach (var cap in spawnTimeCaps)
        {
            if (minutes >= cap.fromMinute)
                active = cap;
        }
        return active;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СПАВН
    // ─────────────────────────────────────────────────────────────────────────

    void HandleSpawning()
    {
        float ratio = GetPlayerDifficultyRatio();

        if (_isSwarmActive)
        {
            float elapsed = _localTimer?.GetTimeElapsed() ?? Time.time;

            if (elapsed >= _swarmEndTime)
            {
                _isSwarmActive = false;
                _swarmSpawnTimer = 0f;
                Debug.Log("⚔️ Рой завершён.");
            }
            else
            {
                _swarmSpawnTimer -= Time.unscaledDeltaTime;
                if (_swarmSpawnTimer <= 0f)
                {
                    // В рое базово 2 врага, сложность добавляет ещё
                    float swarmMins = (_localTimer?.GetTimeElapsed() ?? 0f) / 60f;
                    float swarmSpawnRatio = Mathf.Min(ratio, spawnCapDifficultyPct / 100f);
                    int count = Mathf.Clamp(
                        baseSpawnPerTick + 1
                        + Mathf.FloorToInt(swarmMins * spawnCountPerMinute)
                        + Mathf.FloorToInt(swarmSpawnRatio * spawnBonusPerHundredPct),
                        baseSpawnPerTick + 1, maxSpawnPerTick);

                    // Ограничение по времени — кап на тик роя тоже применяется
                    var timeCap = GetTimeCap(swarmMins);
                    if (timeCap != null)
                        count = Mathf.Min(count, timeCap.maxSpawnPerTick);

                    for (int i = 0; i < count; i++) SpawnEnemy();
                    _swarmSpawnTimer = swarmSpawnInterval;
                }
            }
            return;
        }

        _spawnTimer -= Time.unscaledDeltaTime;
        if (_spawnTimer <= 0)
        {
            float elapsed2 = _localTimer?.GetTimeElapsed() ?? 0f;
            float minutes2 = elapsed2 / 60f;
            float spawnRatio = Mathf.Min(ratio, spawnCapDifficultyPct / 100f);
            int spawnCount = Mathf.Clamp(
                baseSpawnPerTick
                + Mathf.FloorToInt(minutes2 * spawnCountPerMinute)
                + Mathf.FloorToInt(spawnRatio * spawnBonusPerHundredPct),
                baseSpawnPerTick, maxSpawnPerTick);

            // Ограничение по времени — жёсткий потолок тика на данной минуте
            var timeCap = GetTimeCap(minutes2);
            if (timeCap != null)
                spawnCount = Mathf.Min(spawnCount, timeCap.maxSpawnPerTick);

            for (int i = 0; i < spawnCount; i++) SpawnEnemy();

            // Интервал: временной масштаб ÷ ускорение от сложности
            float elapsed = _localTimer?.GetTimeElapsed() ?? 0f;
            float progress = _isFinalSwarm ? 1f : Mathf.Clamp01(elapsed / (stageTimeMinutes * 60f));
            float timeScale = _isFinalSwarm ? 0.3f : Mathf.Lerp(1.0f, 0.3f, progress);
            float difficultyDiv = 1f + ratio * intervalSpeedBonus;

            float rawInterval = timeBetweenSpawns * Mathf.Clamp(timeScale, 0.3f, 1f) / difficultyDiv;

            // Ограничение по времени — минимальный интервал не может быть меньше timeCap.minInterval
            float minInterval = timeCap != null ? timeCap.minInterval : 0.05f;
            _spawnTimer = Mathf.Max(rawInterval, minInterval);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПРИМЕНЕНИЕ СЛОЖНОСТИ К МОБУ
    // ─────────────────────────────────────────────────────────────────────────

    void ApplyDifficultyToEnemy(GameObject enemy)
    {
        EnemyHealth health = _enemyPool?.GetCachedHealth(enemy) ?? enemy.GetComponent<EnemyHealth>();
        BaseEnemyAI ai = _enemyPool?.GetCachedEnemyAI(enemy) ?? enemy.GetComponent<BaseEnemyAI>();

        if (health != null)
            health.SetMaxHealth(health.GetBaseMaxHealth() * _currentDifficultyMultiplier);

        if (ai != null)
        {
            ai.SetDamage(ai.GetDamage() * _currentDamageMultiplier);
            ai.SetSpeed(ai.GetSpeed() * _currentSpeedMultiplier);
        }
    }

    void ApplyDifficultyToMiniBoss(GameObject boss)
    {
        EnemyHealth health = _enemyPool?.GetCachedHealth(boss) ?? boss.GetComponent<EnemyHealth>();
        BaseEnemyAI ai = _enemyPool?.GetCachedMiniBossAI(boss) ?? boss.GetComponent<BaseEnemyAI>();

        if (health != null)
            health.SetMaxHealth(health.GetBaseMaxHealth() * _currentDifficultyMultiplier * 1.5f);

        if (ai != null)
        {
            ai.SetDamage(ai.GetDamage() * _currentDamageMultiplier * 1.3f);
            ai.SetSpeed(ai.GetSpeed() * _currentSpeedMultiplier * 1.2f);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СПАВН ВРАГОВ
    // ─────────────────────────────────────────────────────────────────────────

    void SpawnEnemy()
    {
        if (_playerTransform == null) { FindPlayer(); return; }

        if (useVampireSurvivalsMechanics)
        {
            Vector2 circle = Random.insideUnitCircle.normalized * offScreenRadius;
            Vector3 desiredOffScreen = _playerTransform.position + new Vector3(circle.x, 0f, circle.y);
            Vector3 offScreenPos = FindGroundPosition(desiredOffScreen);

            GameObject enemy = _enemyPool?.GetEnemy(offScreenPos);
            if (enemy == null) return;

            float jitteredRadius = spawnRadius + Random.Range(-spawnRadiusJitter, spawnRadiusJitter);
            jitteredRadius = Mathf.Max(jitteredRadius, 1f); // не ближе 1 юнита к игроку
            Vector2 finalCircle = Random.insideUnitCircle.normalized * jitteredRadius;
            Vector3 finalPos = _playerTransform.position + new Vector3(finalCircle.x, 0f, finalCircle.y);
            Vector3 finalSpawnPos = FindGroundPosition(finalPos);

            enemy.transform.position = finalSpawnPos;

            Rigidbody rb = enemy.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                rb.position = finalSpawnPos;
                rb.linearVelocity = Vector3.zero;
            }

            ApplyDifficultyToEnemy(enemy);
            return;
        }

        float oldJittered = Mathf.Max(spawnRadius + Random.Range(-spawnRadiusJitter, spawnRadiusJitter), 1f);
        Vector2 oldCircle = Random.insideUnitCircle.normalized * oldJittered;
        Vector3 desired = _playerTransform.position + new Vector3(oldCircle.x, 0f, oldCircle.y);
        Vector3 spawnPos = FindGroundPosition(desired);

        GameObject oldEnemy = _enemyPool?.GetEnemy(spawnPos);
        if (oldEnemy == null) return;

        ApplyDifficultyToEnemy(oldEnemy);
    }

    void SpawnMiniBoss()
    {
        if (_playerTransform == null) { FindPlayer(); return; }

        if (useVampireSurvivalsMechanics)
        {
            Vector2 circleOff = Random.insideUnitCircle.normalized * (offScreenRadius + 10f);
            Vector3 desiredOff = _playerTransform.position + new Vector3(circleOff.x, 0f, circleOff.y);
            Vector3 offPos = FindGroundPosition(desiredOff);

            GameObject boss = _enemyPool?.GetMiniBoss(offPos);
            if (boss == null) return;

            float bossJittered = Mathf.Max((spawnRadius + 5f) + Random.Range(-spawnRadiusJitter, spawnRadiusJitter), 1f);
            Vector2 finalCircle = Random.insideUnitCircle.normalized * bossJittered;
            Vector3 finalPos = _playerTransform.position + new Vector3(finalCircle.x, 0f, finalCircle.y);
            Vector3 finalSpawnPos = FindGroundPosition(finalPos);

            boss.transform.position = finalSpawnPos;

            Rigidbody bossRb = boss.GetComponent<Rigidbody>();
            if (bossRb != null && !bossRb.isKinematic)
            {
                bossRb.position = finalSpawnPos;
                bossRb.linearVelocity = Vector3.zero;
            }

            ApplyDifficultyToMiniBoss(boss);
            return;
        }

        float bossOldJittered = Mathf.Max((spawnRadius + 5f) + Random.Range(-spawnRadiusJitter, spawnRadiusJitter), 1f);
        Vector2 circle = Random.insideUnitCircle.normalized * bossOldJittered;
        Vector3 desired = _playerTransform.position + new Vector3(circle.x, 0f, circle.y);
        Vector3 spawnPos = FindGroundPosition(desired);

        GameObject oldBoss = _enemyPool?.GetMiniBoss(spawnPos);
        if (oldBoss == null) return;

        ApplyDifficultyToMiniBoss(oldBoss);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // РОЙ
    // ─────────────────────────────────────────────────────────────────────────

    void StartSwarm(float duration)
    {
        _isSwarmActive = true;
        _swarmSpawnTimer = 0f;
        _swarmEndTime = (_localTimer?.GetTimeElapsed() ?? Time.time) + duration;
        Debug.Log($"⚔️ Рой начат! Длительность: {duration}s, интервал: {swarmSpawnInterval}s");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ПОВЕРХНОСТИ — МНОГОУРОВНЕВЫЙ
    // ─────────────────────────────────────────────────────────────────────────

    Vector3 FindGroundPosition(Vector3 desiredXZ)
    {
        Vector3 playerPos = _playerTransform.position;
        float groundOffset = 0.5f;

        // Этап 1: луч вниз с большой высоты
        {
            Vector3 rayOrigin = new Vector3(desiredXZ.x, desiredXZ.y + spawnRaycastHeight, desiredXZ.z);
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit,
                                spawnRaycastHeight * 2f, _groundLayerMask))
                return hit.point + Vector3.up * groundOffset;
        }

        // Этап 2: луч с высоты игрока
        {
            Vector3 rayOrigin = new Vector3(desiredXZ.x, playerPos.y + 20f, desiredXZ.z);
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 40f, _groundLayerMask))
                return hit.point + Vector3.up * groundOffset;
        }

        // Этап 3: OverlapSphere вокруг игрока
        {
            Collider[] cols = Physics.OverlapSphere(playerPos, fallbackSearchRadius, _groundLayerMask);
            if (cols.Length > 0)
            {
                float minDist = float.MaxValue;
                Vector3 bestPos = playerPos;

                foreach (var col in cols)
                {
                    Vector3 closest;
                    if (col is BoxCollider || col is SphereCollider || col is CapsuleCollider)
                        closest = col.ClosestPoint(desiredXZ);
                    else if (col is MeshCollider mc && mc.convex)
                        closest = col.ClosestPoint(desiredXZ);
                    else
                        closest = new Vector3(desiredXZ.x, col.bounds.max.y, desiredXZ.z);

                    float dist = Vector3.Distance(closest, desiredXZ);
                    if (dist < minDist) { minDist = dist; bestPos = closest + Vector3.up * groundOffset; }
                }
                return bestPos;
            }
        }

        // Этап 4: несколько попыток вокруг игрока
        {
            float nearbyRadius = Mathf.Min(spawnRadius * 0.4f, 5f);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                float angle = (attempt / 8f) * 360f * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(angle) * nearbyRadius, 0f, Mathf.Sin(angle) * nearbyRadius);
                Vector3 rayOrigin = playerPos + offset + Vector3.up * 20f;

                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 40f, _groundLayerMask))
                    return hit.point + Vector3.up * groundOffset;
            }
        }

        // Этап 5: прямо под игроком — абсолютный гарант
        {
            Vector3 underPlayer = playerPos + Vector3.up * 20f;
            if (Physics.Raycast(underPlayer, Vector3.down, out RaycastHit groundHit, 40f, _groundLayerMask))
            {
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector3 jitter = new Vector3(Mathf.Cos(angle) * 2f, 0f, Mathf.Sin(angle) * 2f);
                Debug.LogWarning("[EnemySpawner] Этап 5: поверхность не найдена — спавним у ног игрока.");
                return groundHit.point + Vector3.up * groundOffset + jitter;
            }

            Debug.LogError("[EnemySpawner] Поверхность не найдена ни одним методом!");
            return playerPos + Vector3.up * groundOffset;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ДЕБАГ ЛОГ
    // ─────────────────────────────────────────────────────────────────────────

    void HandleDebugLog()
    {
#if UNITY_EDITOR
        if (debugLogInterval <= 0f) return;

        _debugLogTimer -= Time.unscaledDeltaTime;
        if (_debugLogTimer > 0f) return;
        _debugLogTimer = debugLogInterval;

        float ratio = GetPlayerDifficultyRatio();
        float dbgMins = (_localTimer?.GetTimeElapsed() ?? 0f) / 60f;
        float dbgSpawnRatio = Mathf.Min(ratio, spawnCapDifficultyPct / 100f);
        int spawnCount = Mathf.Clamp(baseSpawnPerTick + Mathf.FloorToInt(dbgMins * spawnCountPerMinute) + Mathf.FloorToInt(dbgSpawnRatio * spawnBonusPerHundredPct), baseSpawnPerTick, maxSpawnPerTick);

        Debug.Log($"[EnemySpawner DEBUG] " +
                  $"EnemyDifficulty={ratio * 100f:F0}% | " +
                  $"SpawnPerTick={spawnCount} | " +
                  $"HP×{_currentDifficultyMultiplier:F2} | " +
                  $"DMG×{_currentDamageMultiplier:F2} | " +
                  $"SPD×{_currentSpeedMultiplier:F2} | " +
                  $"ActiveMobs={_enemyPool?.ActiveEnemies ?? -1}/700 | " +
                  $"FinalSwarm={_isFinalSwarm}");
#endif
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PUBLIC API
    // ─────────────────────────────────────────────────────────────────────────

    public float GetCurrentDifficultyMultiplier() => _currentDifficultyMultiplier;
    public float GetPlayerDifficultyPercent() => GetPlayerDifficultyRatio() * 100f;

    /// <summary>Возвращает активный SpawnTimeCap для текущей минуты (для Editor превью).</summary>
    public SpawnTimeCap GetActiveTimeCap()
    {
        float mins = (_localTimer?.GetTimeElapsed() ?? 0f) / 60f;
        return GetTimeCap(mins);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Жёсткое ограничение спавна на конкретном отрезке времени игры.
/// Активируется когда текущая минута ≥ fromMinute.
/// </summary>
[System.Serializable]
public class SpawnTimeCap
{
    [Tooltip("С какой минуты игры вступает в силу это ограничение.\n" +
             "0 = с самого старта.")]
    public float fromMinute = 0f;

    [Tooltip("Максимальное количество мобов за один тик спавна на этом отрезке.\n" +
             "Применяется и к обычному спавну, и к рою.")]
    public int maxSpawnPerTick = 25;

    [Tooltip("Минимальный интервал между тиками спавна (сек) на этом отрезке.\n" +
             "Интервал не может стать короче этого значения даже при высокой сложности.\n" +
             "Применяется только к обычному спавну (рой использует swarmSpawnInterval).")]
    public float minInterval = 5f;
}