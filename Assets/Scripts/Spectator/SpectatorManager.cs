using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using TMPro;
using VContainer;

/// <summary>
/// SpectatorManager — синглтон на сцене, управляет режимом наблюдения.
///
/// ═══════════════════════════════════════════════════════════════════════
/// ЧТО ДЕЛАЕТ
/// ═══════════════════════════════════════════════════════════════════════
///   • F2 — переключение между своим видом и видом оппонента
///   • Spectator Camera следит за Ghost оппонента (уже есть в GhostSync)
///   • Ghost-враги — визуальные копии (нет физики, нет AI) позиций из снапшота
///   • Spectator UI overlay — "НАБЛЮДЕНИЕ / F2 вернуться"
///   • Не мешает игровому трафику основных игроков
///
/// ═══════════════════════════════════════════════════════════════════════
/// АРХИТЕКТУРА РЕНДЕРИНГА ВРАГОВ
/// ═══════════════════════════════════════════════════════════════════════
///   SpectatorBroadcaster (у оппонента) → RPC → OnSnapshotReceived()
///   → _pendingSnapshot буфер → Update() → RenderEnemySnapshot()
///   → pool ghost GameObjects (только Renderer, без Rigidbody/AI)
///   → позиционируем их по данным снапшота
///
/// ═══════════════════════════════════════════════════════════════════════
/// РЕФАКТОРИНГ — УБРАНЫ static Instance и EnemyBatchSystem.Instance:
/// ═══════════════════════════════════════════════════════════════════════
///
///   БЫЛО:
///     public static SpectatorManager Instance { get; private set; }
///     SpectatorBroadcaster.DeliverSnapshotRpc → SpectatorManager.Instance?.OnSnapshotReceived(...)
///     HideNewlySpawnedEnemies() / HideLocalEnemies() → EnemyBatchSystem.Instance
///
///   СТАЛО:
///     Instance удалён. SpectatorManager зарегистрирован в GameLifetimeScope
///     через RegisterComponent — VContainer хранит единственный экземпляр.
///     SpectatorBroadcaster получает SpectatorManager через [Inject].
///     EnemyBatchSystem получает через [Inject] Construct().
///
/// ═══════════════════════════════════════════════════════════════════════
/// ИНТЕГРАЦИЯ
/// ═══════════════════════════════════════════════════════════════════════
///   1. SpectatorManager GameObject на сцене
///   2. Назначен в GameLifetimeScope → поле spectatorManager (уже так)
///   3. Опционально: назначить enemyGhostPrefab / miniBossGhostPrefab
///      (если не назначены — создаются автоматически из примитивов)
/// </summary>
public class SpectatorManager : MonoBehaviour
{
    // ── static Instance УДАЛЁН ───────────────────────────────────────────────
    // SpectatorManager теперь доступен только через VContainer DI.
    // SpectatorBroadcaster получает его через [Inject] Construct().

    // ─── НАСТРОЙКИ GHOST ВРАГОВ ───────────────────────────────────────────────

    [Header("Ghost Enemy Prefabs (опционально — создаются авто если пусто)")]
    [Tooltip("Префаб для ghost обычного врага. Только Renderer. Если пусто — создаётся Capsule.")]
    [SerializeField] private GameObject enemyGhostPrefab;

    [Tooltip("Префаб для ghost мини-босса. Если пусто — создаётся увеличенный Capsule.")]
    [SerializeField] private GameObject miniBossGhostPrefab;

    [Tooltip("Начальный размер пула ghost-врагов. Совпадает с EnemyPool.maxActiveEnemies.")]
    [SerializeField] private int ghostPoolSize = 800;

    // ─── НАСТРОЙКИ КАМЕРЫ ─────────────────────────────────────────────────────

    [Header("Spectator Camera")]
    [Tooltip("Смещение камеры от цели. Подбери под свою камеру в игре.")]
    [SerializeField] private Vector3 cameraOffset = new Vector3(0f, 18f, -12f);

    [Tooltip("Плавность следования камеры за Ghost оппонента.")]
    [SerializeField] private float cameraFollowSpeed = 8f;

    [Tooltip("FOV spectator камеры. Совпади с игровой камерой.")]
    [SerializeField] private float spectatorFOV = 60f;

    // ─── UI ───────────────────────────────────────────────────────────────────

    [Header("UI (опционально — создаётся авто если пусто)")]
    [Tooltip("Canvas для overlay. Если пусто — создаётся программно.")]
    [SerializeField] private Canvas spectatorOverlayCanvas;

    // ─── ПУБЛИЧНОЕ СОСТОЯНИЕ ──────────────────────────────────────────────────

    /// <summary>true пока активен режим наблюдения.</summary>
    public bool IsSpectating { get; private set; }

    // ─── ПРИВАТНЫЕ ПОЛЯ ───────────────────────────────────────────────────────

    // Камера
    private Camera _spectatorCamera;
    private Camera _playerCameraRef;
    private AudioListener _playerAudioListener;
    private Transform _spectatorCamTransform;

    // Цели наблюдения
    private SpectatorBroadcaster _targetBroadcaster;
    private GhostSync _targetGhost;

    // Пулы ghost-объектов
    private readonly List<GameObject> _enemyGhostPool = new List<GameObject>(800);
    private readonly List<GameObject> _activeEnemyGhosts = new List<GameObject>(700);
    private readonly List<GameObject> _bossGhostPool = new List<GameObject>(20);
    private readonly List<GameObject> _activeBossGhosts = new List<GameObject>(10);

    // UI
    private TextMeshProUGUI _labelText;
    private TextMeshProUGUI _hintText;

    // Снапшот
    private EnemyGhostData[] _pendingSnapshot;
    private bool _hasNewSnapshot;

    // Локальные враги, скрытые при входе в spectator
    private readonly List<Renderer> _hiddenLocalEnemyRenderers = new List<Renderer>(700);

    // Throttle для HideNewlySpawnedEnemies
    private float _nextHideCheckTime;
    private const float HIDE_CHECK_INTERVAL = 0.1f;

    // ─── ЗАВИСИМОСТИ ─────────────────────────────────────────────────────────

    private PlayerRegistry _playerRegistry;
    private EnemyBatchSystem _enemyBatchSystem;

    [Inject]
    public void Construct(PlayerRegistry registry)
    {
        _playerRegistry = registry;
        // EnemyBatchSystem находится лениво — живёт в PlayerWorldScope (PlayerWorldScene),
        // а SpectatorManager в BaseLifetimeScope. Прямая инжекция невозможна.
    }

    private EnemyBatchSystem FindEnemyBatchSystem()
    {
        if (_enemyBatchSystem != null) return _enemyBatchSystem;
        var localPlayer = _playerRegistry?.GetLocalPlayer();
        if (localPlayer != null)
        {
            foreach (var root in localPlayer.gameObject.scene.GetRootGameObjects())
            {
                var b = root.GetComponentInChildren<EnemyBatchSystem>(true);
                if (b != null) { _enemyBatchSystem = b; return b; }
            }
        }
        _enemyBatchSystem = Object.FindFirstObjectByType<EnemyBatchSystem>();
        return _enemyBatchSystem;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        BuildSpectatorCamera();
        BuildGhostPools();
        BuildSpectatorUI();

        Debug.Log("✅ [SpectatorManager] Инициализирован. F2 — режим наблюдения.");
    }

    void OnDestroy()
    {
        if (IsSpectating) ExitSpectatorMode();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE — ввод и логика наблюдения
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!GameModeManager.IsCompetitiveMode()) return;

        if (Input.GetKeyDown(KeyCode.F2))
        {
            if (IsSpectating) ExitSpectatorMode();
            else EnterSpectatorMode();
        }

        if (!IsSpectating) return;

        FollowOpponentWithCamera();
        ApplyPendingSnapshot();
        HideNewlySpawnedEnemies();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ВХОД / ВЫХОД ИЗ РЕЖИМА НАБЛЮДЕНИЯ
    // ─────────────────────────────────────────────────────────────────────────

    public void EnterSpectatorMode()
    {
        if (IsSpectating) return;
        if (!GameModeManager.IsCompetitiveMode()) return;

        _targetBroadcaster = FindOpponentBroadcaster();
        if (_targetBroadcaster == null)
        {
            Debug.LogWarning("[SpectatorManager] SpectatorBroadcaster оппонента не найден.");
            return;
        }

        _targetGhost = FindOpponentGhost();

        ulong myClientId = NetworkManager.Singleton.LocalClientId;
        _targetBroadcaster.RegisterSpectatorRpc(myClientId);

        DisablePlayerCamera();

        if (_spectatorCamera != null)
        {
            _spectatorCamera.enabled = true;
            var al = _spectatorCamera.GetComponent<AudioListener>();
            if (al != null) al.enabled = true;
        }

        if (_spectatorCamTransform != null && _targetGhost != null)
        {
            _spectatorCamTransform.position = _targetGhost.transform.position + cameraOffset;
            _spectatorCamTransform.LookAt(_targetGhost.transform.position + Vector3.up);
        }

        spectatorOverlayCanvas?.gameObject.SetActive(true);
        HideLocalEnemies();

        IsSpectating = true;
        Debug.Log("👁 [SpectatorManager] Режим наблюдения ВКЛЮЧЁН");
    }

    public void ExitSpectatorMode()
    {
        if (!IsSpectating) return;

        _targetBroadcaster?.UnregisterSpectatorRpc();
        _targetBroadcaster = null;
        _targetGhost = null;

        EnablePlayerCamera();

        if (_spectatorCamera != null)
        {
            _spectatorCamera.enabled = false;
            var al = _spectatorCamera.GetComponent<AudioListener>();
            if (al != null) al.enabled = false;
        }

        ReturnAllGhostsToPool();
        ShowLocalEnemies();

        spectatorOverlayCanvas?.gameObject.SetActive(false);

        IsSpectating = false;
        _hasNewSnapshot = false;
        _pendingSnapshot = null;

        Debug.Log("👁 [SpectatorManager] Режим наблюдения ВЫКЛЮЧЁН");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КАМЕРА — следим за Ghost оппонента
    // ─────────────────────────────────────────────────────────────────────────

    void FollowOpponentWithCamera()
    {
        if (_spectatorCamTransform == null) return;

        Vector3 targetPos;
        bool hasTarget = false;

        if (_targetGhost != null && _targetGhost.gameObject.activeInHierarchy)
        {
            targetPos = _targetGhost.transform.position;
            hasTarget = true;
        }
        else if (_playerRegistry?.GetOpponent() is PlayerStats opp && opp != null)
        {
            targetPos = opp.transform.position;
            hasTarget = true;
        }
        else
        {
            return;
        }

        if (!hasTarget) return;

        Vector3 desiredPos = targetPos + cameraOffset;

        _spectatorCamTransform.position = Vector3.Lerp(
            _spectatorCamTransform.position,
            desiredPos,
            Time.deltaTime * cameraFollowSpeed
        );

        _spectatorCamTransform.LookAt(targetPos + Vector3.up * 1.5f);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SNAPSHOT — приём и рендеринг ghost-врагов
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Вызывается из SpectatorBroadcaster.DeliverSnapshotRpc() через инжектированную ссылку.
    /// NGO вызывает RPC на main thread — буферизация безопасна.
    /// </summary>
    public void OnSnapshotReceived(EnemyGhostData[] snapshot)
    {
        if (!IsSpectating) return;
        _pendingSnapshot = snapshot;
        _hasNewSnapshot = true;
    }

    void ApplyPendingSnapshot()
    {
        if (!_hasNewSnapshot || _pendingSnapshot == null) return;
        _hasNewSnapshot = false;
        RenderEnemySnapshot(_pendingSnapshot);
    }

    void RenderEnemySnapshot(EnemyGhostData[] snapshot)
    {
        ReturnAllGhostsToPool();

        foreach (var data in snapshot)
        {
            bool isBoss = data.IsMiniBoss == 1;

            GameObject ghost = isBoss
                ? GetBossGhostFromPool()
                : GetEnemyGhostFromPool();

            if (ghost == null) continue;

            ghost.transform.position = data.Position;
            ghost.SetActive(true);

            if (isBoss) _activeBossGhosts.Add(ghost);
            else _activeEnemyGhosts.Add(ghost);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СКРЫТИЕ / ПОКАЗ ЛОКАЛЬНЫХ ВРАГОВ
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Прячет врагов, заспавненных пока игрок смотрит spectator.
    /// Вызывается каждые HIDE_CHECK_INTERVAL секунд в Update().
    ///
    /// Использует инжектированный _enemyBatchSystem вместо статического Instance.
    /// </summary>
    void HideNewlySpawnedEnemies()
    {
        if (Time.time < _nextHideCheckTime) return;
        _nextHideCheckTime = Time.time + HIDE_CHECK_INTERVAL;

        // Null-safe: если EnemyBatchSystem не инжектирован — пропускаем
        var batch = FindEnemyBatchSystem();
        if (batch == null) return;

        var enemies = batch.GetActiveEnemyList();
        if (enemies == null) return;

        foreach (var enemy in enemies)
        {
            if (enemy == null) continue;

            var renderers = enemy.GetComponentsInChildren<Renderer>(includeInactive: false);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!r.enabled) continue;
                if (_hiddenLocalEnemyRenderers.Contains(r)) continue;

                r.enabled = false;
                _hiddenLocalEnemyRenderers.Add(r);
            }
        }
    }

    /// <summary>
    /// Скрывает всех активных врагов локального игрока при входе в spectator.
    /// Использует инжектированный _enemyBatchSystem вместо статического Instance.
    ///
    /// ПОЧЕМУ renderer, а не SetActive:
    ///   SetActive(false) вызывает OnDisable() у EnemyAI → Unregister из EnemyBatchSystem.
    ///   Renderer.enabled = false — только визуальная невидимость, AI/физика работает.
    /// </summary>
    void HideLocalEnemies()
    {
        _hiddenLocalEnemyRenderers.Clear();

        var batch = FindEnemyBatchSystem();
        if (batch == null) return;

        var enemies = batch.GetActiveEnemyList();
        if (enemies == null) return;

        foreach (var enemy in enemies)
        {
            if (enemy == null) continue;

            var renderers = enemy.GetComponentsInChildren<Renderer>(includeInactive: false);
            foreach (var r in renderers)
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                _hiddenLocalEnemyRenderers.Add(r);
            }
        }

        Debug.Log($"👁 [SpectatorManager] Скрыто локальных врагов: {_hiddenLocalEnemyRenderers.Count} рендеров");
    }

    /// <summary>
    /// Возвращает видимость локальных врагов при выходе из spectator.
    /// </summary>
    void ShowLocalEnemies()
    {
        int restored = 0;
        foreach (var r in _hiddenLocalEnemyRenderers)
        {
            if (r == null) continue;
            if (!r.gameObject.activeInHierarchy) continue;
            r.enabled = true;
            restored++;
        }

        _hiddenLocalEnemyRenderers.Clear();
        Debug.Log($"👁 [SpectatorManager] Восстановлено рендеров: {restored}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GHOST POOL
    // ─────────────────────────────────────────────────────────────────────────

    GameObject GetEnemyGhostFromPool()
    {
        if (_enemyGhostPool.Count > 0)
        {
            int last = _enemyGhostPool.Count - 1;
            var g = _enemyGhostPool[last];
            _enemyGhostPool.RemoveAt(last);
            return g;
        }
        return Instantiate(enemyGhostPrefab, transform);
    }

    GameObject GetBossGhostFromPool()
    {
        if (_bossGhostPool.Count > 0)
        {
            int last = _bossGhostPool.Count - 1;
            var g = _bossGhostPool[last];
            _bossGhostPool.RemoveAt(last);
            return g;
        }
        return Instantiate(miniBossGhostPrefab, transform);
    }

    void ReturnAllGhostsToPool()
    {
        foreach (var g in _activeEnemyGhosts)
        {
            if (g == null) continue;
            g.SetActive(false);
            _enemyGhostPool.Add(g);
        }
        _activeEnemyGhosts.Clear();

        foreach (var g in _activeBossGhosts)
        {
            if (g == null) continue;
            g.SetActive(false);
            _bossGhostPool.Add(g);
        }
        _activeBossGhosts.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // КАМЕРА ИГРОКА — enable / disable
    // ─────────────────────────────────────────────────────────────────────────

    void DisablePlayerCamera()
    {
        var localPlayer = _playerRegistry?.GetLocalPlayer();
        if (localPlayer == null) return;

        var movement = localPlayer.GetComponent<PlayerMovement>();
        if (movement == null) return;

        Transform camTransform = movement.cam;
        if (camTransform == null) return;

        _playerCameraRef = camTransform.GetComponent<Camera>()
                        ?? camTransform.GetComponentInChildren<Camera>();

        if (_playerCameraRef != null)
        {
            _playerCameraRef.enabled = false;
            Debug.Log("[SpectatorManager] Камера игрока отключена");
        }

        _playerAudioListener = localPlayer.GetComponentInChildren<AudioListener>();
        if (_playerAudioListener != null)
            _playerAudioListener.enabled = false;
    }

    void EnablePlayerCamera()
    {
        if (_playerCameraRef != null)
        {
            _playerCameraRef.enabled = true;
            _playerCameraRef = null;
            Debug.Log("[SpectatorManager] Камера игрока восстановлена");
        }

        if (_playerAudioListener != null)
        {
            _playerAudioListener.enabled = true;
            _playerAudioListener = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПОИСК ОБЪЕКТОВ ОППОНЕНТА
    // ─────────────────────────────────────────────────────────────────────────

    SpectatorBroadcaster FindOpponentBroadcaster()
    {
        if (NetworkManager.Singleton == null) return null;

        ulong myClientId = NetworkManager.Singleton.LocalClientId;
        var broadcasters = FindObjectsByType<SpectatorBroadcaster>(FindObjectsSortMode.None);

        foreach (var b in broadcasters)
        {
            if (b.OwnerClientId != myClientId)
            {
                Debug.Log($"[SpectatorManager] Broadcaster оппонента найден: ClientId={b.OwnerClientId}");
                return b;
            }
        }

        Debug.LogWarning("[SpectatorManager] SpectatorBroadcaster оппонента не найден!");
        return null;
    }

    GhostSync FindOpponentGhost()
    {
        if (NetworkManager.Singleton == null) return null;

        ulong myClientId = NetworkManager.Singleton.LocalClientId;
        var ghosts = FindObjectsByType<GhostSync>(FindObjectsSortMode.None);

        foreach (var g in ghosts)
        {
            if (g.OwnerClientId != myClientId)
            {
                Debug.Log($"[SpectatorManager] Ghost оппонента найден: ClientId={g.OwnerClientId}");
                return g;
            }
        }

        Debug.LogWarning("[SpectatorManager] Ghost оппонента не найден.");
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // СОЗДАНИЕ КАМЕРЫ И UI
    // ─────────────────────────────────────────────────────────────────────────

    void BuildSpectatorCamera()
    {
        var camObj = new GameObject("SpectatorCamera");
        camObj.transform.SetParent(transform);

        _spectatorCamera = camObj.AddComponent<Camera>();
        _spectatorCamera.fieldOfView = spectatorFOV;
        _spectatorCamera.nearClipPlane = 0.1f;
        _spectatorCamera.farClipPlane = 500f;
        _spectatorCamera.enabled = false;

        _spectatorCamTransform = camObj.transform;

        var al = camObj.AddComponent<AudioListener>();
        al.enabled = false;

        Debug.Log("[SpectatorManager] SpectatorCamera создана");
    }

    void BuildGhostPools()
    {
        if (enemyGhostPrefab == null)
            enemyGhostPrefab = CreateGhostPrimitive(
                new Color(0.5f, 0.5f, 0.9f, 0.55f),
                new Vector3(0.6f, 0.9f, 0.6f),
                "EnemyGhost_Template"
            );

        if (miniBossGhostPrefab == null)
            miniBossGhostPrefab = CreateGhostPrimitive(
                new Color(1f, 0.3f, 0.3f, 0.65f),
                new Vector3(1.2f, 1.8f, 1.2f),
                "MiniBossGhost_Template"
            );

        for (int i = 0; i < ghostPoolSize; i++)
        {
            var g = Instantiate(enemyGhostPrefab, transform);
            g.SetActive(false);
            _enemyGhostPool.Add(g);
        }

        for (int i = 0; i < 20; i++)
        {
            var g = Instantiate(miniBossGhostPrefab, transform);
            g.SetActive(false);
            _bossGhostPool.Add(g);
        }

        Debug.Log($"[SpectatorManager] Ghost пулы готовы: {ghostPoolSize} врагов, 20 боссов");
    }

    static GameObject CreateGhostPrimitive(Color color, Vector3 scale, string objName)
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        obj.name = objName;

        Object.Destroy(obj.GetComponent<Collider>());
        obj.transform.localScale = scale;

        var rend = obj.GetComponent<Renderer>();
        Material mat = null;

        var urpShader = Shader.Find("Universal Render Pipeline/Lit");
        if (urpShader != null && urpShader.name != "Hidden/InternalErrorShader")
        {
            mat = new Material(urpShader);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else
        {
            mat = new Material(Shader.Find("Standard"));
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
        }

        mat.color = color;
        rend.material = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;

        obj.SetActive(false);
        return obj;
    }

    void BuildSpectatorUI()
    {
        if (spectatorOverlayCanvas != null)
        {
            var texts = spectatorOverlayCanvas.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (texts.Length >= 1) _labelText = texts[0];
            if (texts.Length >= 2) _hintText = texts[1];
            spectatorOverlayCanvas.gameObject.SetActive(false);
            return;
        }

        var canvasObj = new GameObject("SpectatorOverlayCanvas");
        canvasObj.transform.SetParent(transform);

        spectatorOverlayCanvas = canvasObj.AddComponent<Canvas>();
        spectatorOverlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        spectatorOverlayCanvas.sortingOrder = 99;

        var scaler = canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = UnityEngine.UI.CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        _labelText = CreateUIText(
            canvasObj.transform,
            "SpectatorLabel",
            "👁  НАБЛЮДЕНИЕ ЗА ОППОНЕНТОМ",
            fontSize: 26,
            color: new Color(1f, 0.85f, 0f),
            anchorMin: new Vector2(0f, 1f),
            anchorMax: new Vector2(0f, 1f),
            pivot: new Vector2(0f, 1f),
            anchoredPos: new Vector2(24f, -24f),
            sizeDelta: new Vector2(580f, 50f),
            alignment: TextAlignmentOptions.TopLeft
        );

        _hintText = CreateUIText(
            canvasObj.transform,
            "SpectatorHint",
            "[ F2 ] — вернуться к своему виду",
            fontSize: 20,
            color: new Color(1f, 1f, 1f, 0.65f),
            anchorMin: new Vector2(0.5f, 0f),
            anchorMax: new Vector2(0.5f, 0f),
            pivot: new Vector2(0.5f, 0f),
            anchoredPos: new Vector2(0f, 24f),
            sizeDelta: new Vector2(500f, 40f),
            alignment: TextAlignmentOptions.Bottom
        );

        spectatorOverlayCanvas.gameObject.SetActive(false);
        Debug.Log("[SpectatorManager] Spectator UI создан программно");
    }

    static TextMeshProUGUI CreateUIText(
        Transform parent,
        string name,
        string text,
        float fontSize,
        Color color,
        Vector2 anchorMin,
        Vector2 anchorMax,
        Vector2 pivot,
        Vector2 anchoredPos,
        Vector2 sizeDelta,
        TextAlignmentOptions alignment)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);

        var tmp = obj.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;

        var rect = obj.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPos;
        rect.sizeDelta = sizeDelta;

        return tmp;
    }
}