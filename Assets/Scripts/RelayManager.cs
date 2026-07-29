using UnityEngine;
using Unity.Netcode;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;
using System;
using System.Threading.Tasks;

/// <summary>
/// RelayManager v3.0 — Unity Relay SDK интеграция.
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v3.0:
/// ══════════════════════════════════════════════════════════════════════
///   [CRITICAL] Убран Task.Run() вокруг UnityServices.InitializeAsync().
///              Unity Services API НЕЛЬЗЯ вызывать вне main thread —
///              это приводит к крашу/неопределённому поведению.
///              Теперь используем await напрямую (async/await в Unity
///              выполняется на main thread).
///   [CRITICAL] CreateRelayHost и JoinRelayAsClient теперь инициализируют
///              сервисы синхронно через InitializeAsync(callback).
///
/// ══════════════════════════════════════════════════════════════════════
/// ИСПРАВЛЕНИЯ v2.1:
/// ══════════════════════════════════════════════════════════════════════
///   - Добавлен флаг _isInitializing — предотвращает двойную инициализацию
///     (MenuManager и RelayManager.Start() вызывали InitializeAsync одновременно)
///   - Проверяется IsSigningIn перед SignInAnonymouslyAsync
///   - Если вход уже идёт — ждём его завершения вместо исключения
/// ══════════════════════════════════════════════════════════════════════
/// </summary>
public class RelayManager : MonoBehaviour
{
    public static RelayManager Instance { get; private set; }

    [Header("Настройки Relay")]
    [Tooltip("Максимальное количество игроков (включая хост)")]
    [SerializeField] private int maxPlayers = 2;

    [Tooltip("Тип соединения: dtls или udp")]
    [SerializeField] private string connectionType = "dtls";

    [Tooltip("Регион Relay (пусто = авто)")]
    [SerializeField] private string region = "";

    // ─── СОСТОЯНИЕ ────────────────────────────────────────────────────────────
    private bool _isInitialized = false;
    private bool _isInitializing = false;   // FIX v2.1: защита от параллельных вызовов
    private string _currentJoinCode;
    private Allocation _hostAllocation;
    private JoinAllocation _clientAllocation;

    // ─── СОБЫТИЯ ──────────────────────────────────────────────────────────────
    public event Action OnInitialized;
    public event Action<string> OnHostCreated;
    public event Action OnClientConnected;
    public event Action<string> OnError;

    // ─── ИНИЦИАЛИЗАЦИЯ ────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("[RelayManager] ✅ Instance создан");
        }
        else if (Instance != this)
        {
            Debug.LogWarning("[RelayManager] Дубликат уничтожен");
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        InitializeAsync((success) => {
            if (success)
                Debug.Log("[RelayManager] ✅ Авто-инициализация успешна");
            else
                Debug.LogError("[RelayManager] ❌ Авто-инициализация не удалась");
        });
    }

    // ─── ИНИЦИАЛИЗАЦИЯ UNITY SERVICES ─────────────────────────────────────────

    /// <summary>
    /// Инициализирует Unity Services (необходимо перед использованием Relay).
    /// Безопасно вызывать несколько раз одновременно — второй вызов ждёт первый.
    /// </summary>
    public async void InitializeAsync(Action<bool> callback)
    {
        if (_isInitialized)
        {
            Debug.Log("[RelayManager] Уже инициализирован");
            callback?.Invoke(true);
            return;
        }

        // FIX v2.1: если инициализация уже идёт — ждём её вместо повторного запуска
        // Именно здесь падало: "The player is already signing in"
        if (_isInitializing)
        {
            Debug.Log("[RelayManager] Инициализация уже выполняется, ожидаю завершения...");
            while (_isInitializing)
                await Task.Delay(50);
            callback?.Invoke(_isInitialized);
            return;
        }

        _isInitializing = true;
        Debug.Log("[RelayManager] Инициализация Unity Services...");

        try
        {
            await UnityServices.InitializeAsync();

            if (UnityServices.State == ServicesInitializationState.Initialized)
            {
                // FIX v2.1: _isInitializing гарантирует что SignInAnonymouslyAsync
                // вызывается только один раз. Если всё же прилетит повторный вызов —
                // просто пропускаем вход (IsSignedIn уже true).
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    try
                    {
                        Debug.Log("[RelayManager] Вход в систему (anonymous)...");
                        await AuthenticationService.Instance.SignInAnonymouslyAsync();
                        Debug.Log($"[RelayManager] ✅ Вошли как: {AuthenticationService.Instance.PlayerId}");
                    }
                    catch (Exception signInEx) when (signInEx.Message.Contains("already signing in") ||
                                                     signInEx.Message.Contains("already signed in"))
                    {
                        // Параллельный вызов уже выполняет вход — ждём пока IsSignedIn станет true
                        Debug.Log("[RelayManager] Вход выполняется параллельно, ожидаю...");
                        float waited = 0f;
                        while (!AuthenticationService.Instance.IsSignedIn && waited < 10f)
                        {
                            await Task.Delay(100);
                            waited += 0.1f;
                        }
                        Debug.Log($"[RelayManager] ✅ Вошли как: {AuthenticationService.Instance.PlayerId}");
                    }
                }
                else
                {
                    Debug.Log($"[RelayManager] ✅ Уже вошли как: {AuthenticationService.Instance.PlayerId}");
                }

                _isInitialized = true;
                Debug.Log("[RelayManager] ✅ Unity Services инициализированы");
                OnInitialized?.Invoke();
                callback?.Invoke(true);
            }
            else
            {
                Debug.LogError($"[RelayManager] ❌ Unity Services не инициализированы. State: {UnityServices.State}");
                callback?.Invoke(false);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] ❌ Ошибка инициализации: {e.Message}");
            OnError?.Invoke($"Ошибка инициализации: {e.Message}");
            callback?.Invoke(false);
        }
        finally
        {
            // Всегда снимаем флаг — даже при исключении
            _isInitializing = false;
        }
    }

    public bool IsInitialized() => _isInitialized;

    // ─── СОЗДАНИЕ ХОСТА (для Dedicated Server) ────────────────────────────────

    /// <summary>
    /// Создает Relay-аллокацию для хоста (сервера).
    /// Вызывается из ServerStartup при назначении матча.
    ///
    /// ИСПРАВЛЕНО v3.0: убран Task.Run() вокруг UnityServices.InitializeAsync().
    /// Unity Services API нельзя вызывать вне main thread.
    /// Используем InitializeAsync с callback + await Task.Yield.
    /// </summary>
    public async void CreateRelayHost(Action<string> onSuccess, Action<string> onError)
    {
        Debug.Log("[RelayManager] Создание Relay-хоста...");

        if (!_isInitialized)
        {
            Debug.LogWarning("[RelayManager] Relay не инициализирован, пробуем инициализировать...");
            bool initSuccess = false;
            string initError = null;

            // FIX v3.0: вызываем InitializeAsync синхронно через callback
            InitializeAsync((success) => {
                initSuccess = success;
                if (!success) initError = "Initialization failed";
            });

            // Ждём результат инициализации (максимум 15 секунд)
            float waitStart = Time.realtimeSinceStartup;
            while (!initSuccess && string.IsNullOrEmpty(initError) &&
                   Time.realtimeSinceStartup - waitStart < 15f)
            {
                await Task.Yield();
            }

            if (!initSuccess)
            {
                onError?.Invoke("Relay не инициализирован. Проверьте Unity Services.");
                return;
            }
            _isInitialized = true;
        }

        try
        {
            Debug.Log($"[RelayManager] Создание аллокации на {maxPlayers} игроков...");
            _hostAllocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers, region);
            _currentJoinCode = await RelayService.Instance.GetJoinCodeAsync(_hostAllocation.AllocationId);

            Debug.Log($"[RelayManager] ✅ Аллокация создана. AllocationId: {_hostAllocation.AllocationId}");
            Debug.Log($"[RelayManager] ✅ Join code: {_currentJoinCode}");

            SetupRelayServer(_hostAllocation);

            OnHostCreated?.Invoke(_currentJoinCode);
            onSuccess?.Invoke(_currentJoinCode);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"[RelayManager] ❌ Ошибка Relay: {e.Message}");
            OnError?.Invoke($"Ошибка Relay: {e.Message}");
            onError?.Invoke(e.Message);
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] ❌ Неожиданная ошибка: {e.Message}");
            OnError?.Invoke($"Ошибка: {e.Message}");
            onError?.Invoke(e.Message);
        }
    }

    private void SetupRelayServer(Allocation allocation)
    {
        Debug.Log($"[RelayManager] Настройка Relay сервера (тип: {connectionType})...");
        try
        {
            var relayServerData = AllocationUtils.ToRelayServerData(allocation, connectionType);
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("[RelayManager] ❌ UnityTransport не найден!");
                return;
            }
            transport.SetRelayServerData(relayServerData);
            Debug.Log("[RelayManager] ✅ Relay сервер настроен");
            Debug.Log($"[RelayManager]   Хост: {allocation.RelayServer.IpV4}:{allocation.RelayServer.Port}");
            Debug.Log($"[RelayManager]   Allocation ID: {allocation.AllocationId}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] ❌ Ошибка настройки сервера: {e.Message}");
        }
    }

    // ─── ПОДКЛЮЧЕНИЕ КЛИЕНТА ──────────────────────────────────────────────────

    /// <summary>
    /// Подключается к Relay-хосту по join code.
    /// Вызывается из клиента после получения кода от FastAPI.
    ///
    /// ИСПРАВЛЕНО v3.0: убран Task.Run() вокруг UnityServices.InitializeAsync().
    /// </summary>
    public async void JoinRelayAsClient(string joinCode, Action<bool> callback)
    {
        Debug.Log($"[RelayManager] Присоединение к Relay: {joinCode}");

        if (string.IsNullOrEmpty(joinCode))
        {
            Debug.LogWarning("[RelayManager] Join code пуст!");
            callback?.Invoke(false);
            return;
        }

        if (!_isInitialized)
        {
            Debug.LogWarning("[RelayManager] Relay не инициализирован, пробуем инициализировать...");
            bool initSuccess = false;
            string initError = null;

            // FIX v3.0: вызываем InitializeAsync синхронно через callback
            InitializeAsync((success) => {
                initSuccess = success;
                if (!success) initError = "Initialization failed";
            });

            // Ждём результат
            float waitStart = Time.realtimeSinceStartup;
            while (!initSuccess && string.IsNullOrEmpty(initError) &&
                   Time.realtimeSinceStartup - waitStart < 15f)
            {
                await Task.Yield();
            }

            if (!initSuccess)
            {
                Debug.LogWarning("[RelayManager] Не удалось инициализировать!");
                callback?.Invoke(false);
                return;
            }
            _isInitialized = true;
        }

        try
        {
            Debug.Log($"[RelayManager] JoinAllocationAsync: {joinCode}");
            _clientAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
            Debug.Log($"[RelayManager] ✅ Присоединились. AllocationId: {_clientAllocation.AllocationId}");

            SetupRelayClient(_clientAllocation);

            bool started = NetworkManager.Singleton.StartClient();
            if (started)
            {
                Debug.Log("[RelayManager] ✅ Клиент запущен");
                OnClientConnected?.Invoke();
            }
            else
            {
                Debug.LogError("[RelayManager] ❌ StartClient не удался!");
            }
            callback?.Invoke(started);
        }
        catch (RelayServiceException e)
        {
            Debug.LogError($"[RelayManager] ❌ Ошибка подключения: {e.Message}");
            OnError?.Invoke($"Ошибка подключения: {e.Message}");
            callback?.Invoke(false);
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] ❌ Неожиданная ошибка: {e.Message}");
            callback?.Invoke(false);
        }
    }

    private void SetupRelayClient(JoinAllocation allocation)
    {
        Debug.Log($"[RelayManager] Настройка Relay клиента (тип: {connectionType})...");
        try
        {
            var relayServerData = AllocationUtils.ToRelayServerData(allocation, connectionType);
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            if (transport == null)
            {
                Debug.LogError("[RelayManager] ❌ UnityTransport не найден!");
                return;
            }
            transport.SetRelayServerData(relayServerData);
            Debug.Log("[RelayManager] ✅ Relay клиент настроен");
            Debug.Log($"[RelayManager]   Хост: {allocation.RelayServer.IpV4}:{allocation.RelayServer.Port}");
            Debug.Log($"[RelayManager]   Allocation ID: {allocation.AllocationId}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] ❌ Ошибка настройки клиента: {e.Message}");
        }
    }

    // ─── ОТКЛЮЧЕНИЕ ───────────────────────────────────────────────────────────

    public void Disconnect()
    {
        Debug.Log("[RelayManager] Отключение...");
        if (NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient)
            {
                NetworkManager.Singleton.Shutdown();
                Debug.Log("[RelayManager] ✅ Соединение закрыто");
            }
        }
        _hostAllocation = null;
        _clientAllocation = null;
        _currentJoinCode = null;
    }

    // ─── ПУБЛИЧНЫЙ API ────────────────────────────────────────────────────────

    public string GetCurrentJoinCode() => _currentJoinCode;

    public string GetAllocationId()
    {
        if (_hostAllocation != null)  return _hostAllocation.AllocationId.ToString();
        if (_clientAllocation != null) return _clientAllocation.AllocationId.ToString();
        return null;
    }

    public bool IsRelayActive()
    {
        return NetworkManager.Singleton != null &&
               (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsClient) &&
               NetworkManager.Singleton.IsListening;
    }
}
