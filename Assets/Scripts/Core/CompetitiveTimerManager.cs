
using UnityEngine;
using Unity.Netcode;
using System;

/// <summary>
/// Управляет общим 20-минутным таймером для режима ShadowMultiplayer.
///
/// АРХИТЕКТУРА СИНХРОНИЗАЦИИ (финальная версия):
///
///   ПРОБЛЕМА v1 (оригинал):
///     NetworkVariable<float> timeElapsed обновлялась каждый кадр.
///     30 тиков/сек × 60 сек × 20 мин = 36 000 пакетов за матч.
///
///   ПРОБЛЕМА v2 (прошлое исправление):
///     serverTimeElapsed — обычный private float на сервере.
///     Хост (= сервер) и клиент накапливали время независимо.
///     При лаге хоста Time.unscaledDeltaTime мог прыгнуть → дрейф.
///     Два разных счётчика = два разных "конца матча".
///
///   РЕШЕНИЕ v3 (этот файл) — NetworkManager.ServerTime:
///     NGO синхронизирует ServerTime между всеми участниками сессии.
///     Это единые часы для хоста и клиента — не локальный Time.time.
///     Сервер записывает startTime ОДИН РАЗ в NetworkVariable.
///     Каждый игрок вычисляет elapsed = ServerTime.Time - startTime — ЛОКАЛЬНО.
///     Нет постоянного сетевого трафика. Нет дрейфа. Оба видят одно время.
///
///   ИТОГ v3:
///     Трафик: 1 пакет за матч (startTime при старте).
///     Точность: абсолютная — оба используют одни ServerTime часы.
///     Конец матча: networkTimeExpired NetworkVariable — гарантированная доставка.
/// </summary>
public class CompetitiveTimerManager : NetworkBehaviour
{
    [SerializeField] private float competitiveDurationMinutes = 20f;

    // ─── СТАРТОВОЕ ВРЕМЯ ─────────────────────────────────────────────────────
    // Сервер записывает один раз при старте матча.
    // Клиент получает при подключении (NetworkVariable гарантирует это).
    // Оба вычисляют elapsed = ServerTime.Time - serverStartTime.
    //
    // Тип double — ServerTime использует double для точности (float теряет
    // точность при больших значениях времени сессии).
    private NetworkVariable<double> serverStartTime = new NetworkVariable<double>(
        -1.0,                                   // -1 = матч ещё не начат
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // ─── ФЛАГ ОКОНЧАНИЯ ──────────────────────────────────────────────────────
    // NetworkVariable — единственный правильный способ для этого флага.
    // Гарантирует доставку даже опоздавшим клиентам (в отличие от RPC).
    private NetworkVariable<bool> networkTimeExpired = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public event Action OnTimeExpired;

    public static CompetitiveTimerManager Instance { get; private set; }

    // ─────────────────────────────────────────────────────────────────────────
    // ИНИЦИАЛИЗАЦИЯ
    // ─────────────────────────────────────────────────────────────────────────

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Записываем точку отсчёта — ServerTime одинаков для всех в сессии.
            serverStartTime.Value = NetworkManager.ServerTime.Time;
            networkTimeExpired.Value = false;

            Debug.Log($"⏱️ CompetitiveTimer: Match started. ServerTime = {serverStartTime.Value:F3}");
        }

        // Подписка на флаг окончания — работает и для опоздавших клиентов.
        networkTimeExpired.OnValueChanged += OnNetworkTimeExpiredChanged;

        Debug.Log($"⏱️ CompetitiveTimer spawned. IsServer={IsServer}, StartTime={serverStartTime.Value:F3}");
    }

    public override void OnNetworkDespawn()
    {
        networkTimeExpired.OnValueChanged -= OnNetworkTimeExpiredChanged;
    }

    private void OnNetworkTimeExpiredChanged(bool oldValue, bool newValue)
    {
        if (newValue && !oldValue)
        {
            OnTimeExpired?.Invoke();
            Debug.Log("⏰ COMPETITIVE TIME EXPIRED! Game Over.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ЛОГИКА ТАЙМЕРА
    // ─────────────────────────────────────────────────────────────────────────

    void Update()
    {
        if (!GameModeManager.IsCompetitiveMode()) return;

        // Только сервер решает когда матч заканчивается.
        // Клиент только читает — никакой логики окончания на клиенте.
        if (!IsServer) return;

        // serverStartTime.Value == -1 означает что таймер ещё не инициализирован.
        // Это происходит в первый кадр до OnNetworkSpawn — безопасный фолбэк.
        if (serverStartTime.Value < 0) return;

        if (!networkTimeExpired.Value &&
            GetTimeElapsed() >= competitiveDurationMinutes * 60f)
        {
            networkTimeExpired.Value = true;
            Debug.Log($"⏰ Server: Match ended at ServerTime={NetworkManager.ServerTime.Time:F3}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ПУБЛИЧНЫЕ МЕТОДЫ
    // API полностью совместим со старой версией — ничего менять в UI не нужно.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Прошедшее время с начала матча.
    /// Вычисляется локально из ServerTime — одинаково для обоих игроков.
    /// </summary>
    public float GetTimeElapsed()
    {
        if (!IsSpawned || serverStartTime.Value < 0) return 0f;

        double elapsed = NetworkManager.ServerTime.Time - serverStartTime.Value;
        return (float)elapsed;
    }

    public float GetTimeRemaining()
    {
        return Mathf.Max(0f, competitiveDurationMinutes * 60f - GetTimeElapsed());
    }

    public bool IsTimeExpired() => networkTimeExpired.Value;

    public float GetProgress()
    {
        return Mathf.Clamp01(GetTimeElapsed() / (competitiveDurationMinutes * 60f));
    }

    public string GetTimeFormattedRemaining()
    {
        float r = GetTimeRemaining();
        return $"{(int)(r / 60f):D2}:{(int)(r % 60f):D2}";
    }

    public string GetTimeFormattedElapsed()
    {
        float e = GetTimeElapsed();
        return $"{(int)(e / 60f):D2}:{(int)(e % 60f):D2}";
    }
}