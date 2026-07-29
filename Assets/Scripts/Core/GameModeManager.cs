using UnityEngine;
using Unity.Netcode;
using UnityEngine.SceneManagement;

/// <summary>
/// GameModeManager v2.1 — управление режимами игры.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНО (v2.1):
/// ══════════════════════════════════════════════════════════════════════
///   - Добавлено подробное логирование для диагностики.
///   - Улучшены проверки на null.
///   - Добавлены защитные механизмы для сетевых вызовов.
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public enum GameMode
{
    Selecting = -1,
    SinglePlayer = 0,
    ShadowMultiplayer = 2   
}

public class GameModeManager : NetworkBehaviour
{
    private NetworkVariable<int> networkGameMode = new NetworkVariable<int>(
        (int)GameMode.Selecting,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    
    // Общий сид для альтернативных миров
    private NetworkVariable<int> sharedWorldSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // Счётчик игроков нажавших "Готов"
    private NetworkVariable<int> readyCount = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    [Header("Название игровой сцены")]
    [Tooltip("Должно совпадать с именем сцены в Build Settings.")]
    [SerializeField] private string gameSceneName = "BaseScene";

    /// <summary>Вызывается на клиентах когда второй игрок нажал Готов.</summary>
    public event System.Action OnBothPlayersReady;

    public static int SharedSeed { get; private set; } = 0;

    public delegate void GameModeChangedDelegate(GameMode newMode);
    public event GameModeChangedDelegate OnGameModeChanged;

    public static GameMode CurrentMode { get; private set; } = GameMode.Selecting;

    private static GameModeManager instance;
    public static GameModeManager Instance
    {
        get
        {
            if (instance == null)
                instance = FindFirstObjectByType<GameModeManager>();
            return instance;
        }
    }
    
    public static event System.Action OnInstanceInitialized;
    
    void Awake()
    {
        Debug.Log($"[GameModeManager] Awake. Current instance: {(instance != null ? instance.GetInstanceID().ToString() : "NULL")}");
        
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
            OnInstanceInitialized?.Invoke();
            Debug.Log("[GameModeManager] ✅ Instance initialized and event invoked.");
        }
        else if (instance != this)
        {
            Debug.LogWarning("[GameModeManager] Дубликат уничтожен.");
            Destroy(gameObject);
        }
    }

    public override void OnNetworkSpawn()
    {
        Debug.Log($"[GameModeManager] OnNetworkSpawn. IsServer={IsServer}, IsClient={IsClient}");
        
        networkGameMode.OnValueChanged += OnNetworkGameModeChanged;
        sharedWorldSeed.OnValueChanged += OnSharedSeedChanged;
        readyCount.OnValueChanged      += OnReadyCountChanged;

        // Применяем текущее значение сразу
        CurrentMode = (GameMode)networkGameMode.Value;
        SharedSeed = sharedWorldSeed.Value;
        ApplyModeSettings(CurrentMode);
        
        Debug.Log($"[GameModeManager] Текущий режим: {CurrentMode}, SharedSeed: {SharedSeed}");
    }

    public override void OnNetworkDespawn()
    {
        Debug.Log("[GameModeManager] OnNetworkDespawn.");
        
        networkGameMode.OnValueChanged -= OnNetworkGameModeChanged;
        sharedWorldSeed.OnValueChanged -= OnSharedSeedChanged;
        readyCount.OnValueChanged      -= OnReadyCountChanged;
    }

    private void OnNetworkGameModeChanged(int oldValue, int newValue)
    {
        CurrentMode = (GameMode)newValue;
        ApplyModeSettings(CurrentMode);
        OnGameModeChanged?.Invoke(CurrentMode);
        Debug.Log($"[GameModeManager] {(GameMode)oldValue} → {CurrentMode}");
    }

    private void OnSharedSeedChanged(int oldSeed, int newSeed)
    {
        SharedSeed = newSeed;
        Debug.Log($"[GameModeManager] SharedSeed received = {newSeed}");
    }

    private void OnReadyCountChanged(int oldVal, int newVal)
    {
        Debug.Log($"[GameModeManager] Готовы: {newVal}/2");
        if (newVal >= 2)
            OnBothPlayersReady?.Invoke();
    }

    /// <summary>
    /// Вызывается из CharacterSelectUI.OnConfirmClicked() для Competitive режима.
    /// Клиент сигнализирует серверу что он готов начать игру.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void SetPlayerReadyServerRpc(RpcParams rpc = default)
    {
        Debug.Log($"[GameModeManager] SetPlayerReadyServerRpc вызван. IsServer={IsServer}, IsClient={IsClient}, IsSpawned={IsSpawned}");
        
        if (!IsServer) 
        {
            Debug.LogWarning("[GameModeManager] SetPlayerReadyServerRpc: не сервер, игнорируем.");
            return;
        }

        readyCount.Value++;
        Debug.Log($"[GameModeManager] Игрок готов. Счёт: {readyCount.Value}/2");

        if (readyCount.Value < 2) 
        {
            Debug.Log("[GameModeManager] Ждём второго игрока...");
            return;
        }

        // Оба готовы — устанавливаем режим
        readyCount.Value = 0;

        networkGameMode.Value = (int)GameMode.ShadowMultiplayer;
        CurrentMode = GameMode.ShadowMultiplayer;
        ApplyModeSettings(GameMode.ShadowMultiplayer);
        OnGameModeChanged?.Invoke(GameMode.ShadowMultiplayer);

        sharedWorldSeed.Value = Random.Range(1, int.MaxValue);
        SharedSeed = sharedWorldSeed.Value;
        Debug.Log($"[GameModeManager] ✅ Оба готовы — режим ShadowMultiplayer. SharedSeed: {SharedSeed}");
    }

    void ApplyModeSettings(GameMode mode)
    {
        // runInBackground важен для Competitive
        Application.runInBackground = (mode == GameMode.ShadowMultiplayer);
        Debug.Log($"[GameModeManager] Mode={mode}, runInBackground={Application.runInBackground}");
    }

    [Rpc(SendTo.Server)]
    public void SetGameModeServerRpc(int mode)
    {
        Debug.Log($"[GameModeManager] SetGameModeServerRpc: mode={mode}");
        
        if (!IsServer) 
        {
            Debug.LogWarning("[GameModeManager] SetGameModeServerRpc: не сервер, игнорируем.");
            return;
        }

        GameMode newMode = (GameMode)mode;
        if (newMode == GameMode.Selecting) 
        {
            Debug.LogWarning("[GameModeManager] SetGameModeServerRpc: попытка установить Selecting, игнорируем.");
            return;
        }

        networkGameMode.Value = mode;
        CurrentMode = newMode;
        ApplyModeSettings(newMode);
        OnGameModeChanged?.Invoke(newMode);

        // Генерируем общий сид для альтернативных миров
        if (newMode == GameMode.ShadowMultiplayer)
        {
            sharedWorldSeed.Value = Random.Range(1, int.MaxValue);
            SharedSeed = sharedWorldSeed.Value;
            Debug.Log($"[GameModeManager] SharedSeed generated: {SharedSeed}");
        }

        Debug.Log($"[GameModeManager] Mode set on server: {newMode}");
    }

    public static GameMode GetCurrentMode() => CurrentMode;
    public static bool IsMode(GameMode mode) => CurrentMode == mode;
    public static bool IsSelecting() => CurrentMode == GameMode.Selecting;
    public static bool IsCompetitiveMode() => CurrentMode == GameMode.ShadowMultiplayer;

    public static bool IsMultiplayer() => CurrentMode == GameMode.ShadowMultiplayer;

    /// <summary>
    /// Сбрасывает режим в Selecting — вызывается перед перезагрузкой сцены.
    /// </summary>
    public void ResetToSelecting()
    {
        Debug.Log("[GameModeManager] ResetToSelecting.");
        CurrentMode = GameMode.Selecting;
        SharedSeed = 0;
        if (IsServer) readyCount.Value = 0;
    }

    public static string GetCurrentModeString() => CurrentMode switch
    {
        GameMode.Selecting => "Selecting",
        GameMode.SinglePlayer => "Single Player",
        GameMode.ShadowMultiplayer => "Shadow Multiplayer",
        _ => "Unknown"
    };
}
/*using UnityEngine;
using Unity.Netcode;

/// <summary>
/// Режимы игры:
///   SinglePlayer     — локальная игра, без сети, таймер 10 мин
///   ShadowMultiplayer — альтернативные миры, таймер 20 мин (CompetitiveTimerManager)
///
/// Multiplayer (кооп) — удалён, не будет реализован.
///
/// АЛЬТЕРНАТИВНЫЕ МИРЫ — ОБЩИЙ СИД:
///   Оба игрока живут в "одном мире но параллельно".
///   Чтобы LevelUp предлагал одинаковые карточки — используем SharedSeed.
///   Сервер генерирует его один раз при старте матча.
///   LevelUpManager использует new System.Random(SharedSeed + playerLevel)
///   → оба получают одинаковый набор карточек на одном уровне.
/// </summary>
public enum GameMode
{
    Selecting = -1,
    SinglePlayer = 0,
    ShadowMultiplayer = 2   // 1 (Multiplayer) пропущен намеренно — удалён
}

public class GameModeManager : NetworkBehaviour
{
    private NetworkVariable<int> networkGameMode = new NetworkVariable<int>(-1);

    // ─── ОБЩИЙ СИД ДЛЯ АЛЬТЕРНАТИВНЫХ МИРОВ ────────────────────────────────
    // Сервер генерирует один раз при выборе ShadowMultiplayer.
    // Оба игрока используют его для одинакового Random в LevelUp/дропах.
    private NetworkVariable<int> sharedWorldSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public static int SharedSeed { get; private set; } = 0;

    public delegate void GameModeChangedDelegate(GameMode newMode);
    public event GameModeChangedDelegate OnGameModeChanged;

    public static GameMode CurrentMode { get; private set; } = GameMode.Selecting;

    private static GameModeManager instance;
    public static GameModeManager Instance
    {
        get
        {
            if (instance == null)
                instance = FindFirstObjectByType<GameModeManager>();
            return instance;
        }
    }

    void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (instance != this)
        {
            Destroy(gameObject);
        }
    }

    public override void OnNetworkSpawn()
    {
        networkGameMode.OnValueChanged += OnNetworkGameModeChanged;
        sharedWorldSeed.OnValueChanged += OnSharedSeedChanged;

        // Применяем текущее значение сразу (важно для опоздавших клиентов)
        CurrentMode = (GameMode)networkGameMode.Value;
        SharedSeed = sharedWorldSeed.Value;
        ApplyModeSettings(CurrentMode);
    }

    public override void OnNetworkDespawn()
    {
        networkGameMode.OnValueChanged -= OnNetworkGameModeChanged;
        sharedWorldSeed.OnValueChanged -= OnSharedSeedChanged;
    }

    private void OnNetworkGameModeChanged(int oldValue, int newValue)
    {
        CurrentMode = (GameMode)newValue;
        ApplyModeSettings(CurrentMode);
        OnGameModeChanged?.Invoke(CurrentMode);
        Debug.Log($"GameModeManager: {(GameMode)oldValue} → {CurrentMode}");
    }

    private void OnSharedSeedChanged(int oldSeed, int newSeed)
    {
        SharedSeed = newSeed;
        Debug.Log($"GameModeManager: SharedSeed received = {newSeed}");
    }

    void ApplyModeSettings(GameMode mode)
    {
        // runInBackground важен для Competitive — игра должна работать
        // даже если игрок свернул окно (иначе таймер и спавн встают)
        Application.runInBackground = (mode == GameMode.ShadowMultiplayer);
        Debug.Log($"[GameModeManager] Mode={mode}, runInBackground={Application.runInBackground}");
    }

    [Rpc(SendTo.Server)]
    public void SetGameModeServerRpc(int mode)
    {
        if (!IsServer) return;

        GameMode newMode = (GameMode)mode;
        if (newMode == GameMode.Selecting) return;

        networkGameMode.Value = mode;
        CurrentMode = newMode;
        ApplyModeSettings(newMode);
        OnGameModeChanged?.Invoke(newMode);

        // Генерируем общий сид для альтернативных миров один раз при старте матча.
        // Оба игрока будут использовать его для LevelUp карточек и будущих дропов.
        if (newMode == GameMode.ShadowMultiplayer)
        {
            sharedWorldSeed.Value = Random.Range(1, int.MaxValue);
            SharedSeed = sharedWorldSeed.Value;
            Debug.Log($"[GameModeManager] SharedSeed generated: {SharedSeed}");
        }

        Debug.Log($"[GameModeManager] Mode set on server: {newMode}");
    }

    public static GameMode GetCurrentMode() => CurrentMode;
    public static bool IsMode(GameMode mode) => CurrentMode == mode;
    public static bool IsSelecting() => CurrentMode == GameMode.Selecting;
    public static bool IsCompetitiveMode() => CurrentMode == GameMode.ShadowMultiplayer;

    // IsMultiplayer теперь = только ShadowMultiplayer (Multiplayer кооп удалён)
    public static bool IsMultiplayer() => CurrentMode == GameMode.ShadowMultiplayer;

    /// <summary>
    /// Сбрасывает режим в Selecting — вызывается перед перезагрузкой сцены.
    /// GameModeManager живёт через DontDestroyOnLoad, поэтому его нужно сбросить вручную.
    /// SetGameModeServerRpc не подходит — там стоит guard на Selecting.
    /// </summary>
    public void ResetToSelecting()
    {
        CurrentMode = GameMode.Selecting;
        SharedSeed = 0;
        Debug.Log("[GameModeManager] Сброс в Selecting режим");
    }

    public static string GetCurrentModeString() => CurrentMode switch
    {
        GameMode.Selecting => "Selecting",
        GameMode.SinglePlayer => "Single Player",
        GameMode.ShadowMultiplayer => "Shadow Multiplayer",
        _ => "Unknown"
    };
}
*/