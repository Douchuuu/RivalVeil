using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using System.Collections;
using VContainer;

/// <summary>
/// WorldSceneLoader v5 — загружает PlayerWorldScene.
///
/// ИСПРАВЛЕНО (BUG Race Condition SharedSeed):
///   БЫЛО: _seedProvider.Initialize() вызывался сразу при старте LoadCompetitiveScene().
///         Если NetworkVariable sharedWorldSeed ещё не синхронизировалась с сервера
///         (SharedSeed == 0), WorldSeedProvider падал в Random.Range → оба игрока
///         получали РАЗНЫЕ миры (разные позиции сундуков, terrain, пропсов).
///
///   СТАЛО: В Competitive режиме ждём пока GameModeManager.SharedSeed != 0
///          (таймаут 10 сек), только потом вызываем Initialize().
///          Это гарантирует одинаковый мир у обоих игроков.
///
/// ИСПРАВЛЕНО (BUG Player Not Found в Competitive):
///   БЫЛО: FindLocalPlayer() использовал только IsOwner и ждал 3 секунды (20×0.15s).
///         С Relay latency игровой объект игрока ещё не успевал заспавниться/
///         зарегистрироваться → WorldSceneLoader выходил без загрузки PlayerWorldScene.
///
///   СТАЛО: Инжектирован PlayerRegistry. FindLocalPlayer() сначала проверяет реестр
///          (куда PlayerStats записывается в OnNetworkSpawn), затем IsOwner.
///          Таймаут увеличен до 15 секунд (100×0.15s) для Relay+дедик-окружений.
/// </summary>
public class WorldSceneLoader : NetworkBehaviour
{
    [Header("Название сцены мира")]
    [Tooltip("Должно точно совпадать с именем файла сцены БЕЗ расширения .unity")]
    [SerializeField] private string playerWorldSceneName = "PlayerWorldScene";

    [Tooltip("Задержка перед загрузкой (сек)")]
    [SerializeField] private float loadDelay = 0.3f;

    [Tooltip("Таймаут ожидания SharedSeed от сервера (сек)")]
    [SerializeField] private float seedWaitTimeout = 10f;

    private Scene _myScene;
    public Scene MyScene => _myScene;

    private bool _sceneLoadStarted = false;

    private WorldSeedProvider _seedProvider;
    private PlayerRegistry    _playerRegistry;

    [Inject]
    public void Construct(WorldSeedProvider seedProvider, PlayerRegistry playerRegistry)
    {
        _seedProvider   = seedProvider;
        _playerRegistry = playerRegistry;
    }

    public static WorldSceneLoader Instance { get; private set; }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        // Подписываемся на событие смены режима — работает независимо от IsSpawned
        if (GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;
        else
            GameModeManager.OnInstanceInitialized += SubscribeToGameMode;

        // Проверяем текущий режим — вдруг уже выставлен (hot-reload, поздний спавн)
        CheckCurrentMode();
    }

    new void OnDestroy()
    {
        if (GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged -= OnGameModeChanged;
        GameModeManager.OnInstanceInitialized -= SubscribeToGameMode;
    }

    private void SubscribeToGameMode()
    {
        GameModeManager.OnInstanceInitialized -= SubscribeToGameMode;
        if (GameModeManager.Instance != null)
            GameModeManager.Instance.OnGameModeChanged += OnGameModeChanged;
        CheckCurrentMode();
    }

    private void OnGameModeChanged(GameMode newMode)
    {
        if (_sceneLoadStarted) return;
        TryStartLoad(newMode);
    }

    private void CheckCurrentMode()
    {
        if (!_sceneLoadStarted)
            TryStartLoad(GameModeManager.CurrentMode);
    }

    private void TryStartLoad(GameMode mode)
    {
        if (_sceneLoadStarted) return;

        if (mode == GameMode.SinglePlayer)
        {
            _sceneLoadStarted = true;
            StartCoroutine(LoadSinglePlayerScene());
        }
        else if (mode == GameMode.ShadowMultiplayer)
        {
            _sceneLoadStarted = true;
            StartCoroutine(LoadCompetitiveScene());
        }
    }

    // Update() оставляем как резервный опрос (на случай если событие не сработало)
    void Update()
    {
        if (_sceneLoadStarted) return;
        CheckCurrentMode();
    }

    public override void OnNetworkDespawn()
    {
        if (_myScene.isLoaded)
            SceneManager.UnloadSceneAsync(_myScene);
    }

    // ─── SINGLEPLAYER ─────────────────────────────────────────────────────────

    private IEnumerator LoadSinglePlayerScene()
    {
        _seedProvider?.Initialize();

        PlayerStats localPlayer = FindLocalPlayer();
        Rigidbody   rb          = localPlayer?.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.constraints = RigidbodyConstraints.FreezeAll;
            Debug.Log("[WorldSceneLoader] Rigidbody заморожен");
        }

        yield return new WaitForSeconds(loadDelay);

        var op = SceneManager.LoadSceneAsync(playerWorldSceneName,
            new LoadSceneParameters(LoadSceneMode.Additive));
        yield return op;

        _myScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        Debug.Log($"[WorldSceneLoader] ✅ SP сцена загружена: {_myScene.name}");

        if (rb != null) rb.constraints = RigidbodyConstraints.FreezeRotation;
        yield return null;

        FindLocalPlayer()?.RespawnAtSpawnPoint();
    }

    // ─── COMPETITIVE ──────────────────────────────────────────────────────────

    private IEnumerator LoadCompetitiveScene()
    {
        // Dedicated server не загружает PlayerWorldScene — у него нет локального игрока.
        // Каждый клиент загружает свою собственную копию PlayerWorldScene аддитивно.
        if (NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsServer
            && !NetworkManager.Singleton.IsHost)
        {
            Debug.Log("[WorldSceneLoader] Dedicated server — PlayerWorldScene не нужна, пропускаем.");
            yield break;
        }

        // ── ИСПРАВЛЕНИЕ RACE CONDITION ────────────────────────────────────────
        // Ждём пока SharedSeed придёт от сервера через NetworkVariable.
        // Без этого ожидания оба игрока получали разные случайные миры.
        Debug.Log("[WorldSceneLoader] Ожидаю SharedSeed от сервера...");
        float waited = 0f;
        while (GameModeManager.SharedSeed == 0 && waited < seedWaitTimeout)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        if (GameModeManager.SharedSeed == 0)
            Debug.LogWarning($"[WorldSceneLoader] SharedSeed не получен за {seedWaitTimeout}с — " +
                             "используется случайный seed (миры могут различаться)!");
        else
            Debug.Log($"[WorldSceneLoader] ✅ SharedSeed получен: {GameModeManager.SharedSeed}");

        // Теперь инициализируем провайдер — SharedSeed уже установлен
        _seedProvider?.Initialize();
        // ──────────────────────────────────────────────────────────────────────

        // Ищем локального игрока.
        // PlayerStats.OnNetworkSpawn регистрирует себя в PlayerRegistry сразу при спавне,
        // поэтому проверяем реестр ПЕРВЫМ — это быстрее и надёжнее IsOwner при Relay latency.
        // Таймаут 15 сек (100×0.15s) — достаточно для дедик-сервера + Relay + медленных клиентов.
        PlayerStats localPlayer = null;
        for (int attempt = 0; attempt < 100 && localPlayer == null; attempt++)
        {
            localPlayer = FindLocalPlayer();
            if (localPlayer == null)
                yield return new WaitForSeconds(0.15f);
        }

        if (localPlayer == null)
        {
            Debug.LogError("[WorldSceneLoader] ❌ Локальный игрок не найден!");
            yield break;
        }

        yield return new WaitForSeconds(loadDelay);

        var op = SceneManager.LoadSceneAsync(playerWorldSceneName,
            new LoadSceneParameters(LoadSceneMode.Additive));
        yield return op;

        _myScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        Debug.Log($"[WorldSceneLoader] ✅ Competitive сцена загружена: {_myScene.name}");

        yield return null;

        localPlayer.RespawnAtSpawnPoint();
        Debug.Log("[WorldSceneLoader] ✅ RespawnAtSpawnPoint вызван");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private PlayerStats FindLocalPlayer()
    {
        // Приоритет 1: PlayerRegistry — регистрируется в PlayerStats.OnNetworkSpawn.
        var registered = _playerRegistry?.GetLocalPlayer();
        if (registered != null) return registered;

        var all = FindObjectsByType<PlayerStats>(FindObjectsSortMode.None);

        // Приоритет 2: SinglePlayer — сеть может не быть активна, IsOwner = false
        if (GameModeManager.IsMode(GameMode.SinglePlayer))
        {
            if (all.Length > 0) return all[0];
        }

        // Приоритет 3: Multiplayer — ищем по IsOwner
        var nm = Unity.Netcode.NetworkManager.Singleton;
        bool networkListening = nm != null && nm.IsListening;
        foreach (var p in all)
        {
            if (p.IsOwner || !networkListening) return p;
        }

        return null;
    }
}
/*using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using System.Collections;
using VContainer;

/// <summary>
/// WorldSceneLoader — загружает PlayerWorldScene для ЛОКАЛЬНОГО игрока.
///
/// ПОЧЕМУ БЫЛА ПРОБЛЕМА В БИЛДЕ:
///   1. WorldSceneLoader проверял GameModeManager.CurrentMode в OnNetworkSpawn,
///      но в тот момент режим ещё = Selecting → сцена не грузилась никогда.
///
///   2. PlayerStats.MoveToSpawnPoint() вызывается немедленно при спавне игрока,
///      когда PlayerWorldScene ещё не загружена → пола нет → игрок падает в пустоту.
///      Даже если сцена загружалась потом — игрок уже улетел вниз.
///
/// РЕШЕНИЕ:
///   - Update() опрашивает CurrentMode каждый кадр пока не станет SinglePlayer/Competitive.
///     Это устраняет любые race conditions с порядком инициализации NetworkBehaviour.
///   - После загрузки SinglePlayer сцены — вызываем RespawnAtSpawnPoint() на игроке,
///     возвращая его на правильную позицию.
///   - Пока сцена грузится — Rigidbody игрока заморожен (не падает).
/// </summary>
public class WorldSceneLoader : NetworkBehaviour
{
    [Header("Название сцены мира")]
    [Tooltip("Должно точно совпадать с именем файла сцены БЕЗ расширения .unity")]
    [SerializeField] private string playerWorldSceneName = "PlayerWorldScene";

    [Tooltip("Задержка перед загрузкой (сек)")]
    [SerializeField] private float loadDelay = 0.3f;

    private Scene _myScene;
    public Scene MyScene => _myScene;

    private bool _sceneLoadStarted = false;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────
    private WorldSeedProvider _seedProvider;

    [Inject]
    public void Construct(WorldSeedProvider seedProvider)
    {
        _seedProvider = seedProvider;
    }

    public static WorldSceneLoader Instance { get; private set; }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Update — опрашиваем режим каждый кадр, пока он не установлен.
    // Надёжнее событий: не зависит от порядка OnNetworkSpawn у разных объектов.
    // ─────────────────────────────────────────────────────────────────────────
    void Update()
    {
        if (_sceneLoadStarted) return;
        if (!IsSpawned) return;

        var mode = GameModeManager.CurrentMode;

        if (mode == GameMode.SinglePlayer)
        {
            _sceneLoadStarted = true;
            StartCoroutine(LoadSinglePlayerScene());
        }
        else if (mode == GameMode.ShadowMultiplayer)
        {
            _sceneLoadStarted = true;
            StartCoroutine(LoadCompetitiveScene());
        }
    }

    public override void OnNetworkDespawn()
    {
        if (_myScene.isLoaded)
            SceneManager.UnloadSceneAsync(_myScene);
    }

    // ─── SINGLEPLAYER ─────────────────────────────────────────────────────────

    private IEnumerator LoadSinglePlayerScene()
    {
        // Инициализируем seed ДО загрузки сцены — ChestSpawner и PropSpawner
        // получат уже готовый провайдер и не будут вызывать EnsureInitialized сами.
        _seedProvider?.Initialize();

        // Находим локального игрока и замораживаем его Rigidbody — не падает пока грузится сцена
        PlayerStats localPlayer = FindLocalPlayer();
        Rigidbody playerRb = localPlayer != null ? localPlayer.GetComponent<Rigidbody>() : null;

        if (playerRb != null)
        {
            playerRb.constraints = RigidbodyConstraints.FreezeAll;
            Debug.Log("[WorldSceneLoader] Rigidbody заморожен на время загрузки сцены");
        }

        yield return new WaitForSeconds(loadDelay);

        // Без LocalPhysicsMode — один игрок, физика общая
        var parameters = new LoadSceneParameters(LoadSceneMode.Additive);
        var operation = SceneManager.LoadSceneAsync(playerWorldSceneName, parameters);
        yield return operation;

        _myScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        Debug.Log($"[WorldSceneLoader] ✅ SinglePlayer: PlayerWorldScene загружена ({_myScene.name})");

        // Размораживаем Rigidbody и телепортируем игрока на RespawnPoint
        // (теперь пол есть — игрок встанет корректно)
        if (playerRb != null)
            playerRb.constraints = RigidbodyConstraints.FreezeRotation;

        // Даём один кадр чтобы физика сцены инициализировалась
        yield return null;

        localPlayer = FindLocalPlayer();
        localPlayer?.RespawnAtSpawnPoint();
    }

    // ─── COMPETITIVE ──────────────────────────────────────────────────────────

    private IEnumerator LoadCompetitiveScene()
    {
        // Инициализируем seed ДО загрузки сцены — SharedSeed уже пришёл от сервера.
        _seedProvider?.Initialize();

        // Ищем СВОЙ PlayerStats до задержки — чтобы успеть убедиться что игрок есть
        PlayerStats localPlayer = null;
        for (int attempt = 0; attempt < 20 && localPlayer == null; attempt++)
        {
            localPlayer = FindLocalPlayer();
            if (localPlayer == null)
                yield return new WaitForSeconds(0.15f);
        }

        if (localPlayer == null)
        {
            Debug.LogError("[WorldSceneLoader] ❌ Не найден локальный игрок!");
            yield break;
        }

        yield return new WaitForSeconds(loadDelay);

        // БЕЗ LocalPhysicsMode.Physics3D — игрок остаётся в глобальном физическом мире.
        //
        // ПОЧЕМУ LocalPhysicsMode убран:
        //   LocalPhysicsMode.Physics3D создаёт изолированный физический мир для сцены.
        //   Пол и объекты PlayerWorldScene попадают в этот изолированный мир.
        //   Игрок (NGO NetworkObject) остаётся в глобальном физическом мире BaseScene.
        //   MoveGameObjectToScene с NGO NetworkObject невозможно — NGO запрещает это.
        //   Итог: игрок и пол в разных физических мирах → коллизий нет → игрок висит.
        //
        // Разделение миров двух игроков обеспечивается:
        //   - Разными аддитивными сценами (у каждого своя PlayerWorldScene)
        //   - EnemyBatchSystem (у каждого свой экземпляр в своей сцене)
        //   - Враги управляются Job-системой, не физическими коллайдерами
        var parameters = new LoadSceneParameters(LoadSceneMode.Additive);

        var operation = SceneManager.LoadSceneAsync(playerWorldSceneName, parameters);
        yield return operation;

        _myScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        Debug.Log($"[WorldSceneLoader] ✅ Competitive: PlayerWorldScene загружена ({_myScene.name})");

        // Даём один кадр — физика сцены инициализируется
        yield return null;

        // Размораживаем и телепортируем — пол уже в том же физическом мире
        localPlayer.RespawnAtSpawnPoint();
        Debug.Log("[WorldSceneLoader] ✅ Competitive: RespawnAtSpawnPoint вызван.");
    }

    // ─── УТИЛИТЫ ─────────────────────────────────────────────────────────────

    private PlayerStats FindLocalPlayer()
    {
        var all = FindObjectsByType<PlayerStats>(FindObjectsSortMode.None);
        foreach (var p in all)
            if (p.IsOwner) return p;
        return null;
    }
}
*/